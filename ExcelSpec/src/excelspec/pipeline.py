"""Shared orchestration for ingestion, template extraction, validation, and export."""

from __future__ import annotations

import importlib
import json
from dataclasses import dataclass
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
) -> PipelineResult:
    """Create or load an IR, apply a template, then run all applicable validation."""

    source_path = Path(source)
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
    validation = validate_document(document, selected)
    return PipelineResult(source_path, document, match, validation, unrecognized)


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
