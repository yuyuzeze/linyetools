"""Shared orchestration for ingestion, template extraction, validation, and export."""

from __future__ import annotations

import importlib
import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable

from .ingest import ingest_xlsx
from .models.document_ir import DiagnosticIR, DocumentIR
from .models.template import TemplateSpec
from .templates import (
    ExtractionResult,
    MatchResult,
    apply_best_template,
    extract_with_template,
    load_template,
    load_templates,
    score_template,
)
from .validate import ValidationResult, validate_document, validate_ir_data


XLSX_SUFFIXES = {".xlsx", ".xlsm", ".xltx", ".xltm"}


class PipelineValidationError(ValueError):
    def __init__(self, diagnostics: list[DiagnosticIR]) -> None:
        self.diagnostics = diagnostics
        super().__init__("; ".join(item.message for item in diagnostics))


@dataclass(slots=True)
class PipelineResult:
    source: Path
    document: DocumentIR
    match: MatchResult | None
    validation: ValidationResult
    unrecognized_ranges: dict[str, list[str]]
    processing: dict = field(default_factory=dict)

    def all_diagnostics(self) -> list[DiagnosticIR]:
        diagnostics = [*self.document.diagnostics]
        diagnostics.extend(
            item for sheet in self.document.sheets for item in sheet.diagnostics
        )
        diagnostics.extend(self.validation.diagnostics)
        return diagnostics


def bundled_template_directory() -> Path:
    return Path(__file__).resolve().parents[2] / "templates"


def load_template_candidates(
    template: str | Path | None = None,
    *,
    template_directory: str | Path | None = None,
) -> tuple[list[TemplateSpec], bool]:
    """Load explicit or automatic template candidates; bool indicates explicit mode."""

    from .template_pack import is_template_pack

    if template is not None:
        path = Path(template)
        if path.is_file() or is_template_pack(path):
            return [load_template(path)], True
        if path.is_dir():
            return load_templates(path), True
        raise FileNotFoundError(f"模板不存在: {path}")
    directory = (
        Path(template_directory)
        if template_directory is not None
        else bundled_template_directory()
    )
    return (load_templates(directory) if directory.is_dir() else []), False


def discover_inputs(
    inputs: Iterable[str | Path], *, include_json: bool = False
) -> list[Path]:
    suffixes = set(XLSX_SUFFIXES)
    if include_json:
        suffixes.add(".json")
    discovered: set[Path] = set()
    for item in inputs:
        path = Path(item)
        if path.is_file():
            if path.suffix.lower() not in suffixes:
                raise ValueError(f"不支持的输入文件类型: {path}")
            discovered.add(path.resolve())
        elif path.is_dir():
            discovered.update(
                child.resolve()
                for child in path.rglob("*")
                if child.is_file() and child.suffix.lower() in suffixes
            )
        else:
            raise FileNotFoundError(f"输入不存在: {path}")
    return sorted(discovered, key=lambda path: str(path).casefold())


def run_pipeline(
    source: str | Path,
    *,
    template: str | Path | None = None,
    template_directory: str | Path | None = None,
    asset_dir: str | Path | None = None,
    screenshot_manifest: str | Path | None = None,
    minimum_score: float | None = None,
    strict_schema: bool | None = None,
    ingest_engine: str = "auto",
    mode: str | None = None,
    profile: str | Path | None = None,
    auto_legacy_template: bool = False,
    cache: bool = False,
    cache_dir: str | Path | None = None,
) -> PipelineResult:
    """Create or load an IR, detect/route or apply a template, then validate.

    **Default (zero-config)**: an XLSX with no ``template`` / ``auto_legacy_template``
    / ``template_directory`` runs the zero-config detection pipeline
    (SparseWorkbookIR -> RegionDetector -> RegionRouter -> DocumentIR) in the
    given ``mode`` (defaulting to ``fast``). It never auto-loads bundled legacy
    templates, never starts Excel, and never runs the full JSON Schema.

    **Legacy template** is used only when explicitly requested: ``template`` (an
    explicit file/dir), ``auto_legacy_template=True`` (bundled auto-match), or a
    ``template_directory`` (directory auto-match). ``template`` takes precedence
    over ``mode``.

    ``strict_schema`` controls DocumentIR validation depth (external JSON inputs
    are always full-schema validated regardless).
    """

    source_path = Path(source)
    is_xlsx = source_path.suffix.lower() != ".json"
    legacy_requested = (
        template is not None
        or auto_legacy_template
        or template_directory is not None
    )
    if is_xlsx and not legacy_requested:
        return _run_zeroconfig(
            source_path,
            mode=mode or "fast",
            profile=profile,
            asset_dir=asset_dir,
            screenshot_manifest=screenshot_manifest,
            strict_schema=strict_schema,
            ingest_engine=ingest_engine,
            cache=cache,
            cache_dir=cache_dir,
        )

    candidates, explicit = load_template_candidates(
        template, template_directory=template_directory
    )
    match: MatchResult | None = None
    unrecognized: dict[str, list[str]] = {}
    if source_path.suffix.lower() == ".json":
        data = json.loads(source_path.read_text(encoding="utf-8-sig"))
        if not isinstance(data, dict):
            raise ValueError("DocumentIR JSON 根节点必须是对象")
        schema_diagnostics = validate_ir_data(data)
        if schema_diagnostics:
            raise PipelineValidationError(schema_diagnostics)
        document = DocumentIR.from_dict(data)
        selected = next(
            (
                item
                for item in candidates
                if item.template_id == document.template_id
                and (document.template_version is None or item.version == document.template_version)
            ),
            candidates[0] if explicit and len(candidates) == 1 else None,
        )
    else:
        raw = ingest_xlsx(
            source_path,
            asset_dir=asset_dir,
            screenshot_manifest=screenshot_manifest,
            engine=ingest_engine,
        )
        if explicit and len(candidates) == 1:
            selected = candidates[0]
            candidate = score_template(raw, selected)
            candidate.accepted = True
            match = MatchResult(mode="template", template=selected, candidates=[candidate])
            extraction = extract_with_template(raw, match)
        else:
            extraction = apply_best_template(
                raw, candidates, minimum_score=minimum_score
            )
            match = extraction.match
            selected = match.template
        document = extraction.document
        unrecognized = extraction.unrecognized_ranges
    # JSON DocumentIR inputs were already full-schema validated above; the
    # typed document reproduces that data, so the fast structural check is
    # sufficient here unless the caller explicitly asks for strict schema.
    effective_strict = bool(strict_schema)
    validation = validate_document(document, selected, strict_schema=effective_strict)
    processing = {
        "processing_mode": "existing-ir" if source_path.suffix.lower() == ".json" else "legacy-template",
        "detection_mode": None,
        "profile_id": None,
        "legacy_template_id": selected.template_id if selected else None,
        "ingest_engine": ingest_engine,
    }
    return PipelineResult(
        source_path, document, match, validation, unrecognized, processing
    )


def _run_zeroconfig(
    source_path: Path,
    *,
    mode: str,
    profile: str | Path | None,
    asset_dir: str | Path | None,
    screenshot_manifest: str | Path | None,
    strict_schema: bool | None,
    ingest_engine: str = "sparse",
    cache: bool = False,
    cache_dir: str | Path | None = None,
) -> PipelineResult:
    """Zero-config detection pipeline over the sparse workbook IR.

    With ``cache`` enabled, the zero-config DocumentIR is cached by content hash
    (workbook + versions + profile + mode + asset dir). A cache hit skips ingest,
    detection, and routing entirely and deserialises to the identical DocumentIR,
    so every downstream export is byte-for-byte the same as a cold run.
    """

    from .detect.assemble import assemble_document
    from .ingest import ingest_sparse_workbook
    from .profile import load_profile

    normalized_mode = mode.lower()
    if normalized_mode not in {"fast", "auto", "visual"}:
        raise ValueError(f"未知 mode: {mode}（可选 fast|auto|visual）")

    profile_obj = load_profile(profile) if profile is not None else None
    cache_status = "disabled"
    file_cache = None
    cache_key = None
    if cache and cache_dir is not None:
        from .cache import FileCache, content_sha, document_cache_key, sha256_file

        file_cache = FileCache(Path(cache_dir) / ".excelspec-cache")
        profile_hash = (
            content_sha(Path(profile).read_text(encoding="utf-8-sig"))
            if profile is not None
            else None
        )
        cache_key = document_cache_key(
            workbook_hash=sha256_file(source_path),
            mode=normalized_mode,
            profile_hash=profile_hash,
            asset_dir=str(asset_dir) if asset_dir is not None else None,
        )

    document: DocumentIR | None = None
    unrecognized: dict[str, list[str]] = {}
    if file_cache is not None:
        cached = file_cache.get("document", cache_key)
        if cached is not None:
            document = DocumentIR.from_dict(cached["document"])
            unrecognized = cached.get("unrecognized", {})
            cache_status = "hit"

    if document is None:
        sparse = ingest_sparse_workbook(
            source_path,
            asset_dir=asset_dir,
            screenshot_manifest=screenshot_manifest,
        )
        document, unrecognized = assemble_document(
            sparse, mode=normalized_mode, profile=profile_obj
        )
        if file_cache is not None:
            file_cache.put(
                "document",
                cache_key,
                {"document": document.to_dict(), "unrecognized": unrecognized},
            )
            cache_status = "miss"

    match = MatchResult(
        mode=f"profile:{profile_obj.profile_id}" if profile_obj else f"zero-config:{normalized_mode}",
        template=None,
        candidates=[],
    )
    validation = validate_document(document, None, strict_schema=bool(strict_schema))
    processing = {
        "processing_mode": "zero-config",
        "detection_mode": normalized_mode,
        "profile_id": profile_obj.profile_id if profile_obj else None,
        "legacy_template_id": None,
        "ingest_engine": "sparse",
        "cache": cache_status,
    }
    if file_cache is not None and file_cache.warnings:
        processing["cache_warnings"] = list(file_cache.warnings)
    return PipelineResult(
        source_path, document, match, validation, unrecognized, processing
    )


def export_document(document: DocumentIR, destination: str | Path, format_name: str) -> None:
    """Dispatch to stage-5 exporters without coupling their package internals to CLI."""

    normalized = format_name.lower().lstrip(".")
    exporters = {
        "json": ("excelspec.exporters.json_exporter", "JsonExporter"),
        "md": ("excelspec.exporters.markdown", "MarkdownExporter"),
        "markdown": ("excelspec.exporters.markdown", "MarkdownExporter"),
        "html": ("excelspec.exporters.html_exporter", "HtmlExporter"),
        "jsonl": ("excelspec.exporters.jsonl", "JsonlExporter"),
        "kb-jsonl": ("excelspec.exporters.jsonl", "JsonlExporter"),
        "semantic-json": ("excelspec.exporters.semantic_json", "SemanticJsonExporter"),
        "chunks": ("excelspec.exporters.chunks_jsonl", "ChunksJsonlExporter"),
    }
    try:
        module_name, class_name = exporters[normalized]
    except KeyError as error:
        raise ValueError(f"不支持的输出格式: {format_name}") from error
    try:
        exporter_class = getattr(importlib.import_module(module_name), class_name)
    except (ImportError, AttributeError) as error:
        raise RuntimeError(f"输出器尚不可用: {normalized}") from error
    exporter_class().export(document, Path(destination))


__all__ = [
    "PipelineResult",
    "PipelineValidationError",
    "bundled_template_directory",
    "discover_inputs",
    "export_document",
    "load_template_candidates",
    "run_pipeline",
]
