"""Command-line entry point for inspection, conversion, and validation."""

from __future__ import annotations

import argparse
import json
import sys
from collections.abc import Sequence
from dataclasses import asdict
from pathlib import Path
from typing import Any

from . import __version__
from .ingest import ingest_xlsx
from .models.document_ir import DiagnosticIR, DiagnosticSeverity
from .pipeline import (
    PipelineResult,
    discover_inputs,
    export_document,
    load_template_candidates,
    run_pipeline,
)
from .schemas import load_schema
from .templates import TemplateValidationError, match_template


def _add_input_options(parser: argparse.ArgumentParser, *, include_output: bool = False) -> None:
    parser.add_argument("inputs", nargs="+", help="XLSX 文件、IR JSON 或目录")
    parser.add_argument("--template", help="显式模板文件或模板目录")
    parser.add_argument("--template-dir", help="自动匹配使用的模板目录")
    parser.add_argument("--minimum-score", type=float, help="覆盖自动匹配最低分")
    parser.add_argument("--asset-dir", help="摄取出的资源目录")
    parser.add_argument("--screenshot-manifest", help="截图清单 JSON")
    parser.add_argument("--strict", action="store_true", help="将 warning 视为失败")
    parser.add_argument("--json", action="store_true", dest="json_output", help="输出机器可读 JSON")
    parser.add_argument(
        "--diagnostics",
        metavar="PATH",
        help="将机器可读 diagnostics 写入文件；使用 - 写到标准输出",
    )
    if include_output:
        parser.add_argument("-o", "--output", required=True, help="输出文件或目录")
        parser.add_argument(
            "-f",
            "--format",
            default="json",
            help="导出格式，逗号分隔：json,md,html,jsonl",
        )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="excelspec",
        description="Excel specification document conversion toolkit",
    )
    parser.add_argument("--version", action="version", version=f"%(prog)s {__version__}")
    subparsers = parser.add_subparsers(dest="command")

    schema_parser = subparsers.add_parser("schema", help="输出内置 JSON Schema")
    schema_parser.add_argument("name", choices=("document-ir", "template"))

    inspect_parser = subparsers.add_parser("inspect", help="检查工作簿结构与模板识别结果")
    _add_input_options(inspect_parser)

    convert_parser = subparsers.add_parser("convert", help="摄取、匹配、校验并导出")
    _add_input_options(convert_parser, include_output=True)

    validate_parser = subparsers.add_parser("validate", help="执行 Schema 与业务规则校验")
    _add_input_options(validate_parser)

    template_parser = subparsers.add_parser("template", help="模板工具")
    template_subparsers = template_parser.add_subparsers(dest="template_command")
    match_parser = template_subparsers.add_parser("match", help="对输入文件进行模板评分")
    _add_input_options(match_parser)
    return parser


def _diagnostic_dict(item: DiagnosticIR) -> dict[str, Any]:
    return item.to_dict()


def _result_dict(result: PipelineResult) -> dict[str, Any]:
    match = result.match
    diagnostics = result.all_diagnostics()
    counts = {
        severity.value: sum(item.severity == severity for item in diagnostics)
        for severity in DiagnosticSeverity
    }
    return {
        "source": str(result.source),
        "document_id": result.document.document_id,
        "title": result.document.title,
        "template": (
            {
                "mode": match.mode,
                "selected": match.template.template_id if match.template else None,
                "version": match.template.version if match.template else None,
                "candidates": [asdict(candidate) for candidate in match.candidates],
            }
            if match is not None
            else {
                "mode": "existing-ir",
                "selected": result.document.template_id,
                "version": result.document.template_version,
                "candidates": [],
            }
        ),
        "sheets": [
            {
                "sheet_id": sheet.sheet_id,
                "name": sheet.name,
                "regions": [
                    {
                        "region_id": region.region_id,
                        "region_type": region.region_type.value,
                        "range": region.source.range if region.source else None,
                    }
                    for region in sheet.regions
                ],
                "assets": len(sheet.assets),
            }
            for sheet in result.document.sheets
        ],
        "unrecognized_ranges": result.unrecognized_ranges,
        "validation": {
            "valid": not counts["error"],
            "counts": counts,
        },
        "diagnostics": [_diagnostic_dict(item) for item in diagnostics],
    }


def _failure(source: Path | str, error: Exception) -> dict[str, Any]:
    if hasattr(error, "diagnostics"):
        diagnostics = list(error.diagnostics)
    elif isinstance(error, TemplateValidationError):
        diagnostics = [
            DiagnosticIR(
                code="schema.template",
                severity=DiagnosticSeverity.ERROR,
                message=message,
                details={"source_path": str(error.path)},
            )
            for message in error.errors
        ]
    else:
        diagnostics = [
            DiagnosticIR(
                code="cli.processing_error",
                severity=DiagnosticSeverity.ERROR,
                message=str(error),
                details={"source_path": str(source), "exception": type(error).__name__},
            )
        ]
    counts = {
        severity.value: sum(item.severity == severity for item in diagnostics)
        for severity in DiagnosticSeverity
    }
    return {
        "source": str(source),
        "validation": {
            "valid": False,
            "counts": counts,
        },
        "diagnostics": [diagnostic.to_dict() for diagnostic in diagnostics],
    }


def _write_json(path: str, payload: Any) -> None:
    text = json.dumps(payload, ensure_ascii=False, indent=2) + "\n"
    if path == "-":
        sys.stdout.write(text)
    else:
        destination = Path(path)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(text, encoding="utf-8")


def _print_terminal(items: list[dict[str, Any]]) -> None:
    for item in items:
        counts = item["validation"]["counts"]
        template = item.get("template", {}).get("selected")
        suffix = f", template={template}" if template else ""
        print(
            f"{item['source']}: errors={counts['error']}, "
            f"warnings={counts['warning']}, info={counts['info']}{suffix}"
        )
        for diagnostic in item.get("diagnostics", []):
            source = diagnostic.get("source") or {}
            location = source.get("cell") or source.get("range") or source.get("sheet")
            region = diagnostic.get("region_id")
            context = "/".join(value for value in (location, region) if value)
            print(
                f"  [{diagnostic['severity']}] {diagnostic['code']}"
                f"{f' ({context})' if context else ''}: {diagnostic['message']}",
                file=sys.stderr,
            )


def _process(args: argparse.Namespace, *, include_json: bool) -> tuple[list[dict[str, Any]], list[PipelineResult]]:
    items: list[dict[str, Any]] = []
    results: list[PipelineResult] = []
    try:
        sources = discover_inputs(args.inputs, include_json=include_json)
    except Exception as error:
        return [_failure(",".join(args.inputs), error)], []
    if not sources:
        return [_failure(",".join(args.inputs), ValueError("目录中没有支持的输入文件"))], []
    output = Path(args.output) if getattr(args, "output", None) else None
    for source in sources:
        try:
            asset_dir = args.asset_dir
            if asset_dir is None and output is not None and args.command == "convert":
                if output.suffix.lower() in {".json", ".md", ".html", ".jsonl"}:
                    asset_root = output.parent
                else:
                    asset_root = output
                    asset_root.mkdir(parents=True, exist_ok=True)
                asset_dir = str(asset_root / f"asset.{source.stem}")
            result = run_pipeline(
                source,
                template=args.template,
                template_directory=args.template_dir,
                asset_dir=asset_dir,
                screenshot_manifest=args.screenshot_manifest,
                minimum_score=args.minimum_score,
            )
        except Exception as error:
            items.append(_failure(source, error))
        else:
            results.append(result)
            items.append(_result_dict(result))
    return items, results


def _parse_formats(raw: str) -> list[str]:
    formats = []
    for part in raw.split(","):
        name = part.strip().lower()
        if not name:
            continue
        if name not in {"json", "md", "markdown", "html", "jsonl", "kb-jsonl"}:
            raise ValueError(f"不支持的输出格式: {name}")
        formats.append(name)
    return formats or ["json"]


def _output_destination(
    output: Path, source: Path, format_name: str, *, multiple_sources: bool, multiple_formats: bool
) -> Path:
    extensions = {
        "json": ".json",
        "md": ".md",
        "markdown": ".md",
        "html": ".html",
        "jsonl": ".jsonl",
        "kb-jsonl": ".jsonl",
    }
    # Explicit single file: convert one.xlsx -o out.md -f md
    if (
        not multiple_sources
        and not multiple_formats
        and output.suffix.lower() in {".json", ".md", ".html", ".jsonl"}
    ):
        return output
    output.mkdir(parents=True, exist_ok=True)
    return output / f"{source.stem}{extensions[format_name]}"


def _emit(args: argparse.Namespace, items: list[dict[str, Any]]) -> None:
    payload = {"command": args.command, "results": items}
    if args.diagnostics == "-":
        _write_json("-", payload)
        return
    if args.diagnostics:
        _write_json(args.diagnostics, payload)
    if args.json_output:
        _write_json("-", payload)
    else:
        _print_terminal(items)


def _failed(items: list[dict[str, Any]], *, strict: bool) -> bool:
    return any(
        item["validation"]["counts"]["error"]
        or (strict and item["validation"]["counts"]["warning"])
        for item in items
    )


def _run_template_match(args: argparse.Namespace) -> int:
    items: list[dict[str, Any]] = []
    try:
        sources = discover_inputs(args.inputs)
        templates, _ = load_template_candidates(
            args.template, template_directory=args.template_dir
        )
        if not sources:
            raise ValueError("目录中没有支持的输入文件")
        for source in sources:
            raw = ingest_xlsx(
                source,
                asset_dir=args.asset_dir,
                screenshot_manifest=args.screenshot_manifest,
            )
            result = match_template(raw, templates, minimum_score=args.minimum_score)
            items.append(
                {
                    "source": str(source),
                    "template": {
                        "mode": result.mode,
                        "selected": result.template.template_id if result.template else None,
                        "version": result.template.version if result.template else None,
                        "candidates": [asdict(candidate) for candidate in result.candidates],
                    },
                    "validation": {
                        "valid": result.template is not None,
                        "counts": {
                            "error": 0,
                            "warning": int(result.template is None),
                            "info": 0,
                        },
                    },
                    "diagnostics": (
                        []
                        if result.template is not None
                        else [
                            DiagnosticIR(
                                code="template.no_match",
                                severity=DiagnosticSeverity.WARNING,
                                message="没有模板达到匹配阈值",
                            ).to_dict()
                        ]
                    ),
                }
            )
    except Exception as error:
        items.append(_failure(",".join(args.inputs), error))
    _emit(args, items)
    return 1 if _failed(items, strict=args.strict) else 0


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if args.command == "schema":
        print(json.dumps(load_schema(args.name), ensure_ascii=False, indent=2))
        return 0
    if args.command == "template" and args.template_command == "match":
        return _run_template_match(args)
    if args.command in {"inspect", "validate", "convert"}:
        items, results = _process(args, include_json=args.command in {"validate", "convert"})
        if args.command == "convert":
            output = Path(args.output)
            multiple_sources = len(items) > 1
            try:
                formats = _parse_formats(args.format)
            except ValueError as error:
                print(str(error), file=sys.stderr)
                return 1
            multiple_formats = len(formats) > 1
            by_source = {str(result.source): result for result in results}
            for item in items:
                result = by_source.get(item["source"])
                counts = item["validation"]["counts"]
                if result is None or counts["error"] or (args.strict and counts["warning"]):
                    continue
                outputs: list[str] = []
                try:
                    for format_name in formats:
                        destination = _output_destination(
                            output,
                            result.source,
                            format_name,
                            multiple_sources=multiple_sources,
                            multiple_formats=multiple_formats,
                        )
                        export_document(result.document, destination, format_name)
                        outputs.append(str(destination))
                    item["output"] = outputs[0] if len(outputs) == 1 else outputs
                except Exception as error:
                    failed = _failure(result.source, error)
                    item["validation"] = failed["validation"]
                    item["diagnostics"].extend(failed["diagnostics"])
        _emit(args, items)
        return 1 if _failed(items, strict=args.strict) else 0
    parser.print_help()
    return 0
