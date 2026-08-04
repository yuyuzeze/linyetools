"""Schema, template-contract, and business-rule validation."""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable

from jsonschema import Draft202012Validator

from ..models.document_ir import (
    DiagnosticIR,
    DiagnosticSeverity,
    DocumentIR,
    RegionIR,
    SheetIR,
    SourceRef,
)
from ..models.template import RegionTemplate, TemplateSpec, ValidationRule
from ..schemas import load_schema


@dataclass(slots=True)
class ValidationResult:
    diagnostics: list[DiagnosticIR] = field(default_factory=list)

    @property
    def errors(self) -> int:
        return sum(item.severity == DiagnosticSeverity.ERROR for item in self.diagnostics)

    @property
    def warnings(self) -> int:
        return sum(item.severity == DiagnosticSeverity.WARNING for item in self.diagnostics)

    @property
    def infos(self) -> int:
        return sum(item.severity == DiagnosticSeverity.INFO for item in self.diagnostics)

    def failed(self, *, strict: bool = False) -> bool:
        return bool(self.errors or (strict and self.warnings))

    def to_dict(self) -> dict[str, Any]:
        return {
            "valid": not self.errors,
            "counts": {
                "error": self.errors,
                "warning": self.warnings,
                "info": self.infos,
            },
            "diagnostics": [item.to_dict() for item in self.diagnostics],
        }


def _severity(value: str) -> DiagnosticSeverity:
    try:
        return DiagnosticSeverity(value)
    except ValueError:
        return DiagnosticSeverity.ERROR


def _schema_source(document: DocumentIR, path: Iterable[Any]) -> tuple[SourceRef | None, str | None]:
    parts = list(path)
    try:
        sheet_index = parts[parts.index("sheets") + 1]
        sheet = document.sheets[int(sheet_index)]
    except (ValueError, IndexError, TypeError):
        return None, None
    region: RegionIR | None = None
    try:
        region_index = parts[parts.index("regions") + 1]
        region = sheet.regions[int(region_index)]
    except (ValueError, IndexError, TypeError):
        pass
    return (region.source if region and region.source else SourceRef(sheet=sheet.name)), (
        region.region_id if region else None
    )


def validate_ir_data(
    data: dict[str, Any], *, document: DocumentIR | None = None
) -> list[DiagnosticIR]:
    """Validate an untyped mapping against the bundled DocumentIR schema."""

    diagnostics: list[DiagnosticIR] = []
    validator = Draft202012Validator(load_schema("document-ir"))
    for error in sorted(
        validator.iter_errors(data),
        key=lambda item: tuple(str(part) for part in item.absolute_path),
    ):
        location = ".".join(str(part) for part in error.absolute_path) or "$"
        source, region_id = (
            _schema_source(document, error.absolute_path)
            if document is not None
            else (None, None)
        )
        diagnostics.append(
            DiagnosticIR(
                code="schema.document_ir",
                severity=DiagnosticSeverity.ERROR,
                message=f"{location}: {error.message}",
                source=source,
                region_id=region_id,
                details={"schema_path": list(error.absolute_schema_path)},
            )
        )
    return diagnostics


def validate_ir_schema(document: DocumentIR) -> list[DiagnosticIR]:
    """Validate a typed document against the bundled DocumentIR schema."""

    return validate_ir_data(document.to_dict(), document=document)


def validate_template_structure(
    data: dict[str, Any], *, path: str | Path | None = None
) -> list[DiagnosticIR]:
    """Return template-schema failures as normal machine-readable diagnostics."""

    diagnostics: list[DiagnosticIR] = []
    validator = Draft202012Validator(load_schema("template"))
    for error in sorted(
        validator.iter_errors(data),
        key=lambda item: tuple(str(part) for part in item.absolute_path),
    ):
        location = ".".join(str(part) for part in error.absolute_path) or "$"
        diagnostics.append(
            DiagnosticIR(
                code="schema.template",
                severity=DiagnosticSeverity.ERROR,
                message=f"{location}: {error.message}",
                details={
                    "template_path": str(path) if path is not None else "<memory>",
                    "schema_path": list(error.absolute_schema_path),
                },
            )
        )
    return diagnostics


def _matches(pattern: str, value: str) -> bool:
    try:
        return re.search(pattern, value, flags=re.IGNORECASE) is not None
    except re.error:
        return False


def _find_sheet(
    document: DocumentIR, name: str, aliases: dict[str, SheetIR] | None = None
) -> SheetIR | None:
    if aliases and name in aliases:
        return aliases[name]
    return next(
        (
            sheet
            for sheet in document.sheets
            if sheet.sheet_id.casefold() == name.casefold()
            or sheet.name.casefold() == name.casefold()
        ),
        None,
    )


def _find_region(sheet: SheetIR, region_id: str) -> RegionIR | None:
    return next((region for region in sheet.regions if region.region_id == region_id), None)


def _cell_value(cell: Any) -> Any:
    return cell.raw_value if cell.raw_value is not None else cell.display_value


def _region_field_values(region: RegionIR, field_name: str) -> tuple[list[Any], bool]:
    if field_name in region.values:
        return [region.values[field_name]], True
    values: list[Any] = []
    column_found = False
    for table in region.tables:
        columns = [
            column for column, semantic in table.column_semantics.items() if semantic == field_name
        ]
        if not columns:
            continue
        column_found = True
        header_end = min((cell.row for cell in table.cells), default=1) + table.header_rows
        for cell in table.cells:
            if cell.row >= header_end and cell.coordinate.rstrip("0123456789") in columns:
                values.append(_cell_value(cell))
    return values, column_found


def _resolve_field(
    document: DocumentIR,
    field_name: str | None,
    *,
    sheet: SheetIR | None = None,
    region: RegionIR | None = None,
    aliases: dict[str, SheetIR] | None = None,
) -> tuple[list[Any], SourceRef | None, str | None, bool]:
    if not field_name:
        return [], region.source if region else None, region.region_id if region else None, False
    if sheet is not None and region is not None and "." not in field_name:
        values, found = _region_field_values(region, field_name)
        return values, region.source, region.region_id, found
    parts = field_name.split(".")
    if len(parts) < 3:
        return [], None, None, False
    target_sheet = _find_sheet(document, parts[0], aliases)
    if target_sheet is None:
        return [], None, parts[1], False
    target_region = _find_region(target_sheet, parts[1])
    if target_region is None:
        return [], SourceRef(sheet=target_sheet.name), parts[1], False
    values, found = _region_field_values(target_region, ".".join(parts[2:]))
    return values, target_region.source, target_region.region_id, found


def _present(value: Any) -> bool:
    return value is not None and (not isinstance(value, str) or bool(value.strip()))


def _rule_diagnostic(
    rule: ValidationRule,
    message: str,
    source: SourceRef | None,
    region_id: str | None,
    **details: Any,
) -> DiagnosticIR:
    return DiagnosticIR(
        code=f"business.{rule.kind}",
        severity=_severity(rule.severity),
        message=rule.message or message,
        source=source,
        region_id=region_id,
        details={"rule_id": rule.rule_id, "field": rule.field, **details},
    )


def _validate_rule(
    document: DocumentIR,
    rule: ValidationRule,
    *,
    sheet: SheetIR | None = None,
    region: RegionIR | None = None,
    aliases: dict[str, SheetIR] | None = None,
) -> list[DiagnosticIR]:
    values, source, region_id, field_found = _resolve_field(
        document, rule.field, sheet=sheet, region=region, aliases=aliases
    )
    label = rule.field or rule.rule_id
    diagnostics: list[DiagnosticIR] = []
    if rule.kind == "required":
        if not field_found:
            diagnostics.append(
                _rule_diagnostic(rule, f"必需字段或列不存在: {label}", source, region_id)
            )
        elif not values or any(not _present(value) for value in values):
            diagnostics.append(
                _rule_diagnostic(rule, f"必需值为空: {label}", source, region_id)
            )
    elif rule.kind == "regex":
        pattern = rule.options.get("pattern")
        if not isinstance(pattern, str):
            diagnostics.append(
                _rule_diagnostic(rule, f"正则规则缺少 pattern: {label}", source, region_id)
            )
        else:
            try:
                invalid = [value for value in values if _present(value) and not re.search(pattern, str(value))]
            except re.error as error:
                diagnostics.append(
                    _rule_diagnostic(
                        rule, f"无效正则表达式: {error}", source, region_id, pattern=pattern
                    )
                )
            else:
                if invalid:
                    diagnostics.append(
                        _rule_diagnostic(
                            rule,
                            f"值不符合正则表达式: {label}",
                            source,
                            region_id,
                            pattern=pattern,
                            values=invalid,
                        )
                    )
    elif rule.kind == "enum":
        allowed = rule.options.get("values", rule.options.get("enum", []))
        invalid = [value for value in values if _present(value) and value not in allowed]
        if invalid:
            diagnostics.append(
                _rule_diagnostic(
                    rule,
                    f"值不在允许集合中: {label}",
                    source,
                    region_id,
                    allowed=allowed,
                    values=invalid,
                )
            )
    elif rule.kind == "unique":
        normalized = [str(value) for value in values if _present(value)]
        duplicates = sorted({value for value in normalized if normalized.count(value) > 1})
        if duplicates:
            diagnostics.append(
                _rule_diagnostic(
                    rule, f"值不唯一: {label}", source, region_id, duplicates=duplicates
                )
            )
    elif rule.kind == "reference":
        target = rule.options.get("target") or rule.options.get("target_field")
        target_values, _, _, target_found = _resolve_field(
            document, target, aliases=aliases
        )
        missing = sorted(
            {
                str(value)
                for value in values
                if _present(value) and value not in target_values
            }
        )
        if not target_found:
            diagnostics.append(
                _rule_diagnostic(
                    rule, f"引用目标不存在: {target}", source, region_id, target=target
                )
            )
        elif missing:
            diagnostics.append(
                _rule_diagnostic(
                    rule,
                    f"引用值在目标中不存在: {label}",
                    source,
                    region_id,
                    target=target,
                    values=missing,
                )
            )
    return diagnostics


def validate_business_rules(
    document: DocumentIR, template: TemplateSpec
) -> list[DiagnosticIR]:
    """Validate required template structure and declarative business rules."""

    diagnostics: list[DiagnosticIR] = []
    matched: dict[str, SheetIR] = {}
    for sheet_template in template.sheets:
        sheet = next(
            (
                candidate
                for candidate in document.sheets
                if _matches(sheet_template.name_pattern, candidate.name)
            ),
            None,
        )
        if sheet is None:
            if sheet_template.required:
                diagnostics.append(
                    DiagnosticIR(
                        code="business.required_sheet",
                        severity=DiagnosticSeverity.ERROR,
                        message=f"必需工作表不存在: {sheet_template.sheet_id}",
                        details={
                            "sheet_id": sheet_template.sheet_id,
                            "name_pattern": sheet_template.name_pattern,
                        },
                    )
                )
            continue
        matched[sheet_template.sheet_id] = sheet
        for region_template in sheet_template.regions:
            region = _find_region(sheet, region_template.region_id)
            if region is None:
                if region_template.required:
                    diagnostics.append(
                        DiagnosticIR(
                            code="business.required_region",
                            severity=DiagnosticSeverity.ERROR,
                            message=f"必需区域不存在: {region_template.region_id}",
                            source=SourceRef(sheet=sheet.name),
                            region_id=region_template.region_id,
                        )
                    )
                continue
            diagnostics.extend(
                diagnostic
                for rule in region_template.validation_rules
                for diagnostic in _validate_rule(
                    document,
                    rule,
                    sheet=sheet,
                    region=region,
                    aliases=matched,
                )
            )
    known_sheet_ids = {item.sheet_id for item in template.sheets}
    for rule in template.validation_rules:
        root = rule.field.split(".", 1)[0] if rule.field else None
        if root in known_sheet_ids and root not in matched:
            continue
        diagnostics.extend(_validate_rule(document, rule, aliases=matched))
    return diagnostics


def validate_document(
    document: DocumentIR, template: TemplateSpec | None = None
) -> ValidationResult:
    diagnostics = validate_ir_schema(document)
    if template is not None:
        diagnostics.extend(validate_business_rules(document, template))
    return ValidationResult(diagnostics)


__all__ = [
    "ValidationResult",
    "validate_business_rules",
    "validate_document",
    "validate_ir_data",
    "validate_ir_schema",
    "validate_template_structure",
]
