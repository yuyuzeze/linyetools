"""Per-stage pipeline benchmark.

Measures each phase of a conversion separately (ingest, template extraction,
validation, export) rather than only end-to-end CLI wall time, so a change can
be attributed to the stage it actually affects.

Run::

    python -m excelspec.benchmark WORKBOOK.xlsx [--template T] [--json]

Reported metrics per workbook:

* per-stage seconds (ingest / extract / validate_fast / validate_strict / export)
* total seconds
* sheet_count, cell_count (cells actually materialised), region_count
* chunk_count (KnowledgeBase JSONL lines)
* legacy_fallback (did ingest fall back to the legacy openpyxl path?)
* excel_com_launched (did any stage start Excel?)
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

from .ingest import ingest_xlsx
from .models.document_ir import DocumentIR
from .pipeline import export_document, load_template_candidates
from .templates import apply_best_template, extract_with_template, score_template
from .templates.engine import MatchResult
from .validate import validate_document


@dataclass(slots=True)
class StageTimings:
    stages: dict[str, float] = field(default_factory=dict)

    def time(self, name: str, func: Callable[[], Any]) -> Any:
        start = time.perf_counter()
        result = func()
        self.stages[name] = time.perf_counter() - start
        return result

    @property
    def total(self) -> float:
        return sum(self.stages.values())


def _count_cells(document: DocumentIR) -> int:
    return sum(
        len(table.cells)
        for sheet in document.sheets
        for region in sheet.regions
        for table in region.tables
    )


def _count_regions(document: DocumentIR) -> int:
    return sum(len(sheet.regions) for sheet in document.sheets)


def _count_chunks(document: DocumentIR) -> int:
    try:
        from .exporters import KnowledgeBaseJsonlExporter
    except Exception:  # pragma: no cover - exporter import guard
        return 0
    text = KnowledgeBaseJsonlExporter().render(document)
    return sum(1 for line in text.splitlines() if line.strip())


def benchmark_workbook(
    workbook: str | Path,
    *,
    template: str | Path | None = None,
    formats: tuple[str, ...] = ("json", "md", "jsonl"),
    export_dir: str | Path | None = None,
    engine: str = "auto",
) -> dict[str, Any]:
    """Benchmark a single workbook, returning a metrics mapping."""

    from .render import excel_capture

    workbook_path = Path(workbook)
    timings = StageTimings()
    launches_before = excel_capture.capture_launch_count()

    # Never write extracted assets next to the source workbook; keep the
    # benchmark side-effect free by staging assets in a temp directory.
    import tempfile

    asset_stage = Path(tempfile.mkdtemp(prefix="excelspec-bench-"))
    raw = timings.time(
        "ingest", lambda: ingest_xlsx(workbook_path, asset_dir=asset_stage, engine=engine)
    )
    sparse_stats = raw.metadata.get("sparse_stats", {})

    candidates, explicit = load_template_candidates(template)
    if explicit and len(candidates) == 1:
        selected = candidates[0]
        candidate = score_template(raw, selected)
        candidate.accepted = True
        match = MatchResult(mode="template", template=selected, candidates=[candidate])
        extraction = timings.time("extract", lambda: extract_with_template(raw, match))
        template_spec = selected
    else:
        extraction = timings.time(
            "extract", lambda: apply_best_template(raw, candidates)
        )
        template_spec = extraction.match.template
    document = extraction.document

    timings.time(
        "validate_fast",
        lambda: validate_document(document, template_spec, strict_schema=False),
    )
    timings.time(
        "validate_strict",
        lambda: validate_document(document, template_spec, strict_schema=True),
    )

    export_root = Path(export_dir) if export_dir else None
    if export_root is not None:
        export_root.mkdir(parents=True, exist_ok=True)

    def _export() -> None:
        for fmt in formats:
            suffix = {"markdown": ".md", "md": ".md"}.get(fmt, f".{fmt}")
            if export_root is not None:
                export_document(document, export_root / f"{workbook_path.stem}{suffix}", fmt)
            else:
                # Render-only (no disk write) to isolate serialization cost.
                _render_only(document, fmt)

    timings.time("export", _export)

    excel_launched = excel_capture.capture_launch_count() > launches_before
    return {
        "workbook": str(workbook_path),
        "template": template_spec.template_id if template_spec else None,
        "ingest_engine": engine,
        "ingestor": raw.metadata.get("ingestor"),
        "stages": {name: round(value, 6) for name, value in timings.stages.items()},
        "total_seconds": round(timings.total, 6),
        "sheet_count": len(document.sheets),
        "cell_count": _count_cells(document),
        "materialized_cell_count": _count_cells(document),
        "xml_cell_count": sparse_stats.get("xml_cell_count"),
        "value_cell_count": sparse_stats.get("value_cell_count"),
        "style_only_cell_count": sparse_stats.get("style_only_cell_count"),
        "region_count": _count_regions(document),
        "chunk_count": _count_chunks(document),
        "legacy_fallback": bool(raw.metadata.get("legacy_fallback")),
        "excel_com_launched": excel_launched,
    }


def compare_engines(
    workbook: str | Path, *, template: str | Path | None = None
) -> dict[str, Any]:
    """Benchmark sparse vs legacy ingestion of one workbook and compare output."""

    import tempfile

    from .ingest.sparse import SparseOoxmlIngestor
    from .ingest.workbook import XlsxIngestOptions, XlsxIngestor

    workbook_path = Path(workbook)

    def _ingest(ingestor_cls) -> tuple[float, Any]:
        stage = Path(tempfile.mkdtemp(prefix="excelspec-cmp-"))
        start = time.perf_counter()
        document = ingestor_cls(XlsxIngestOptions(asset_dir=stage)).ingest(workbook_path)
        return time.perf_counter() - start, document

    legacy_time, legacy_doc = _ingest(XlsxIngestor)
    sparse_time, sparse_doc = _ingest(SparseOoxmlIngestor)

    def _content(document) -> str:
        data = document.to_dict()
        meta = data.get("metadata", {})
        for key in (
            "ingestor",
            "legacy_fallback",
            "fallback_reason",
            "sparse_stats",
            "asset_directory",
        ):
            meta.pop(key, None)
        for sheet in data["sheets"]:
            for asset in sheet.get("assets", []):
                asset["uri"] = "<uri>"
        return json.dumps(data, ensure_ascii=False, sort_keys=True)

    return {
        "workbook": str(workbook_path),
        "legacy_ingest_seconds": round(legacy_time, 6),
        "sparse_ingest_seconds": round(sparse_time, 6),
        "ingest_speedup": (
            round(legacy_time / sparse_time, 2) if sparse_time else None
        ),
        "legacy_cell_count": _count_cells(legacy_doc),
        "sparse_cell_count": _count_cells(sparse_doc),
        "sparse_stats": sparse_doc.metadata.get("sparse_stats", {}),
        "output_identical": _content(legacy_doc) == _content(sparse_doc),
    }


def benchmark_zeroconfig(
    workbook: str | Path,
    *,
    mode: str = "fast",
    profile: str | Path | None = None,
    warm: bool = True,
) -> dict[str, Any]:
    """Benchmark the zero-config semantic pipeline, cold and (optionally) warm.

    Reports per-stage timings for: hashing, sparse ingest, detect+route, semantic
    assembly, reference extraction, chunking, semantic-json export, chunks (JSONL)
    export — plus cold vs warm-cache totals and cache hit/miss.
    """

    import tempfile

    from .cache import sha256_file
    from .chunking import chunk_document
    from .detect.assemble import assemble_document
    from .exporters import ChunksJsonlExporter, SemanticJsonExporter
    from .ingest import ingest_sparse_workbook
    from .pipeline import run_pipeline
    from .profile import load_profile
    from .semantic import assemble_semantic
    from .semantic.coverage import analyze_coverage
    from .semantic.references import extract_references

    workbook_path = Path(workbook)
    timings = StageTimings()
    stage = Path(tempfile.mkdtemp(prefix="excelspec-zc-"))
    cache_dir = Path(tempfile.mkdtemp(prefix="excelspec-zc-cache-"))

    timings.time("hash", lambda: sha256_file(workbook_path))
    sparse = timings.time(
        "ingest_sparse", lambda: ingest_sparse_workbook(workbook_path, asset_dir=stage)
    )
    profile_obj = load_profile(profile) if profile else None
    document, _ = timings.time(
        "detect_route", lambda: assemble_document(sparse, mode=mode, profile=profile_obj)
    )
    timings.time("references", lambda: extract_references(document))
    semantic = timings.time("semantic_assembly", lambda: assemble_semantic(document))
    chunks = timings.time("chunking", lambda: chunk_document(semantic))
    timings.time("semantic_json_export", lambda: SemanticJsonExporter().render(document))
    timings.time("chunks_jsonl_export", lambda: ChunksJsonlExporter().render(document))

    report = analyze_coverage(semantic, chunks)

    cold_total = None
    warm_total = None
    warm_status = None
    if warm:
        cold_start = time.perf_counter()
        run_pipeline(workbook_path, mode=mode, profile=profile, asset_dir=stage, cache=True, cache_dir=cache_dir)
        cold_total = time.perf_counter() - cold_start
        warm_start = time.perf_counter()
        warm_result = run_pipeline(
            workbook_path, mode=mode, profile=profile, asset_dir=stage, cache=True, cache_dir=cache_dir
        )
        warm_total = time.perf_counter() - warm_start
        warm_status = warm_result.processing.get("cache")

    return {
        "workbook": str(workbook_path),
        "mode": mode,
        "profile": profile_obj.profile_id if profile_obj else None,
        "stages": {name: round(value, 6) for name, value in timings.stages.items()},
        "cold_pipeline_seconds": round(cold_total, 6) if cold_total is not None else None,
        "warm_pipeline_seconds": round(warm_total, 6) if warm_total is not None else None,
        "warm_cache_status": warm_status,
        "cache_speedup": (
            round(cold_total / warm_total, 2)
            if cold_total and warm_total
            else None
        ),
        "semantic_region_count": report.stats["semantic_region_count"],
        "chunk_count": report.stats["chunk_count"],
        "table_row_count": report.stats["table_row_count"],
        "chunked_table_row_count": report.stats["chunked_table_row_count"],
        "referenced_asset_count": report.stats["referenced_asset_count"],
        "unreferenced_asset_count": report.stats["unreferenced_asset_count"],
        "source_coverage": report.stats["source_coverage"],
        "average_confidence": report.stats["average_confidence"],
        "reference_count": report.stats["reference_count"],
    }


def benchmark_directory(
    directory: str | Path, *, mode: str = "fast", profile: str | Path | None = None
) -> dict[str, Any]:
    """Benchmark every XLSX under a directory, isolating per-file failures.

    A single file failing (bad OOXML, etc.) never aborts the batch; it is
    recorded with its error. Results are sorted slowest-first.
    """

    root = Path(directory)
    workbooks = sorted(
        p for p in root.rglob("*.xlsx") if not p.name.startswith("~$")
    )
    results: list[dict[str, Any]] = []
    failures: list[dict[str, Any]] = []
    for workbook in workbooks:
        try:
            metrics = benchmark_zeroconfig(workbook, mode=mode, profile=profile, warm=True)
        except Exception as error:  # noqa: BLE001 - batch isolation
            failures.append({"workbook": str(workbook), "error": f"{type(error).__name__}: {error}"})
            continue
        results.append(metrics)
    results.sort(key=lambda item: item.get("cold_pipeline_seconds") or 0, reverse=True)
    cold = [r["cold_pipeline_seconds"] for r in results if r.get("cold_pipeline_seconds")]
    warm = [r["warm_pipeline_seconds"] for r in results if r.get("warm_pipeline_seconds")]

    def _pct(values: list[float], q: float) -> float | None:
        if not values:
            return None
        ordered = sorted(values)
        idx = min(len(ordered) - 1, int(q * (len(ordered) - 1)))
        return round(ordered[idx], 6)

    return {
        "directory": str(root),
        "file_count": len(workbooks),
        "success_count": len(results),
        "failure_count": len(failures),
        "cold_p50": _pct(cold, 0.5),
        "cold_p95": _pct(cold, 0.95),
        "warm_p50": _pct(warm, 0.5),
        "warm_p95": _pct(warm, 0.95),
        "results": results,
        "failures": failures,
    }


def directory_summary_csv(summary: dict[str, Any]) -> str:
    lines = ["workbook,cold_seconds,warm_seconds,cache,regions,chunks,table_rows,source_coverage"]
    for r in summary["results"]:
        lines.append(
            f"{r['workbook']},{r['cold_pipeline_seconds']},{r['warm_pipeline_seconds']},"
            f"{r['warm_cache_status']},{r['semantic_region_count']},{r['chunk_count']},"
            f"{r['chunked_table_row_count']},{r['source_coverage']}"
        )
    for f in summary["failures"]:
        lines.append(f"{f['workbook']},FAILED,,{f['error']},,,,")
    return "\n".join(lines) + "\n"


def _render_only(document: DocumentIR, fmt: str) -> str:
    from .exporters import (
        HtmlExporter,
        JsonExporter,
        KnowledgeBaseJsonlExporter,
        MarkdownExporter,
    )

    renderers = {
        "json": JsonExporter,
        "md": MarkdownExporter,
        "markdown": MarkdownExporter,
        "html": HtmlExporter,
        "jsonl": KnowledgeBaseJsonlExporter,
        "kb-jsonl": KnowledgeBaseJsonlExporter,
    }
    return renderers[fmt]().render(document)


def _format_text(metrics: dict[str, Any]) -> str:
    lines = [
        f"workbook: {metrics['workbook']}",
        f"template: {metrics['template']}",
        f"sheets={metrics['sheet_count']} cells={metrics['cell_count']} "
        f"regions={metrics['region_count']} chunks={metrics['chunk_count']}",
        f"legacy_fallback={metrics['legacy_fallback']} "
        f"excel_com_launched={metrics['excel_com_launched']}",
        "stages (s):",
    ]
    for name, value in metrics["stages"].items():
        lines.append(f"  {name:16} {value:.6f}")
    lines.append(f"  {'TOTAL':16} {metrics['total_seconds']:.6f}")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="excelspec-bench")
    parser.add_argument("workbooks", nargs="*", help="XLSX 文件（使用 --directory 时可省略）")
    parser.add_argument("--template", help="显式模板文件/目录")
    parser.add_argument("--format", default="json,md,jsonl", help="导出格式，逗号分隔")
    parser.add_argument("--export-dir", help="导出目录（省略则仅渲染不落盘）")
    parser.add_argument(
        "--engine", choices=("auto", "sparse", "legacy"), default="auto"
    )
    parser.add_argument(
        "--compare",
        action="store_true",
        help="对每个工作簿比较 sparse 与 legacy 摄取（时间/单元格/输出一致性）",
    )
    parser.add_argument(
        "--zeroconfig",
        action="store_true",
        help="零配置语义流水线基准（含 cold/warm 缓存与 chunk 统计）",
    )
    parser.add_argument("--mode", default="fast", help="零配置模式 fast|auto|visual")
    parser.add_argument("--profile", dest="profile_path", help="语义 Profile")
    parser.add_argument("--directory", help="批量基准：对目录下所有 XLSX 运行零配置（隔离单文件失败）")
    parser.add_argument("--csv", help="批量结果 CSV 输出路径")
    parser.add_argument("--json", action="store_true", dest="json_output")
    args = parser.parse_args(argv)

    if args.directory:
        summary = benchmark_directory(args.directory, mode=args.mode, profile=args.profile_path)
        if args.csv:
            Path(args.csv).write_text(directory_summary_csv(summary), encoding="utf-8")
        if args.json_output:
            print(json.dumps(summary, ensure_ascii=False, indent=2))
        else:
            print(
                f"directory: {summary['directory']}  files={summary['file_count']} "
                f"ok={summary['success_count']} failed={summary['failure_count']}"
            )
            print(
                f"  cold p50={summary['cold_p50']}s p95={summary['cold_p95']}s | "
                f"warm p50={summary['warm_p50']}s p95={summary['warm_p95']}s"
            )
            for r in summary["results"]:
                print(f"    {r['cold_pipeline_seconds']:.4f}s  {r['workbook']}")
            for f in summary["failures"]:
                print(f"    FAILED  {f['workbook']}: {f['error']}")
        return 0

    formats = tuple(part.strip() for part in args.format.split(",") if part.strip())
    results = []
    for workbook in args.workbooks:
        if args.zeroconfig:
            results.append(
                benchmark_zeroconfig(workbook, mode=args.mode, profile=args.profile_path)
            )
        elif args.compare:
            results.append(compare_engines(workbook, template=args.template))
        else:
            results.append(
                benchmark_workbook(
                    workbook,
                    template=args.template,
                    formats=formats,
                    export_dir=args.export_dir,
                    engine=args.engine,
                )
            )

    if args.json_output:
        print(json.dumps(results, ensure_ascii=False, indent=2))
    elif args.zeroconfig:
        for metrics in results:
            print(f"workbook: {metrics['workbook']}  mode={metrics['mode']} profile={metrics['profile']}")
            print(
                f"  regions={metrics['semantic_region_count']} chunks={metrics['chunk_count']} "
                f"table_rows={metrics['chunked_table_row_count']}/{metrics['table_row_count']} "
                f"refs={metrics['reference_count']} coverage={metrics['source_coverage']} "
                f"avg_conf={metrics['average_confidence']}"
            )
            for name, value in metrics["stages"].items():
                print(f"    {name:22} {value:.6f}")
            print(
                f"  cold={metrics['cold_pipeline_seconds']}s warm={metrics['warm_pipeline_seconds']}s "
                f"({metrics['warm_cache_status']}) speedup={metrics['cache_speedup']}x"
            )
            print()
    elif args.compare:
        for metrics in results:
            print(f"workbook: {metrics['workbook']}")
            print(
                f"  ingest  legacy={metrics['legacy_ingest_seconds']:.6f}s "
                f"sparse={metrics['sparse_ingest_seconds']:.6f}s "
                f"speedup={metrics['ingest_speedup']}x"
            )
            print(
                f"  cells   legacy={metrics['legacy_cell_count']} "
                f"sparse={metrics['sparse_cell_count']} "
                f"output_identical={metrics['output_identical']}"
            )
            print(f"  sparse_stats={metrics['sparse_stats']}")
            print()
    else:
        for metrics in results:
            print(_format_text(metrics))
            print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
