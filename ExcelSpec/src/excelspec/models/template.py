"""Declarative template contract used by later matching and extraction stages."""

from __future__ import annotations

from dataclasses import dataclass, field as dataclass_field
from enum import StrEnum
from typing import Any

from ..serialization import JsonModel

TEMPLATE_SCHEMA_VERSION = "1.0"


class LocatorMode(StrEnum):
    FIXED = "fixed"
    ANCHOR = "anchor"


@dataclass(slots=True)
class FingerprintRule(JsonModel):
    sheet_name_pattern: str | None = None
    cells: dict[str, str] = dataclass_field(default_factory=dict)
    required_text: list[str] = dataclass_field(default_factory=list)
    weight: float = 1.0


@dataclass(slots=True)
class TemplateMatch(JsonModel):
    sheet_name_patterns: list[str] = dataclass_field(default_factory=list)
    fingerprints: list[FingerprintRule] = dataclass_field(default_factory=list)
    minimum_score: float = 0.7
    # File-name regexes matched against Path(source).name only (NFKC-normalized,
    # case-insensitive). When empty, scoring is unchanged from before.
    file_name_patterns: list[str] = dataclass_field(default_factory=list)
    # When true, a template whose file_name_patterns do not match is rejected in
    # automatic matching (--template-dir / --auto-legacy-template). An explicit
    # single --legacy-template can still force-run (with an info diagnostic).
    require_file_name_match: bool = False
    # When true, and the file name matches and at least one sheet matches, this
    # template outranks generic templates (those without file_name_patterns) even
    # if the generic template's primary score is slightly higher.
    file_name_priority: bool = False


@dataclass(slots=True)
class RegionLocator(JsonModel):
    mode: LocatorMode
    range: str | None = None
    anchor_text: str | None = None
    anchor_pattern: str | None = None
    end_anchor_text: str | None = None
    end_anchor_pattern: str | None = None
    row_offset: int = 0
    column_offset: int = 0
    height: int | None = None
    width: int | None = None
    # When true, every matching anchor becomes its own table/region.
    repeat_anchor: bool = False


@dataclass(slots=True)
class ExtractionSpec(JsonModel):
    kind: str
    header_rows: int = 0
    key_column: int | None = None
    value_column: int | None = None
    key_semantics: dict[str, str] = dataclass_field(default_factory=dict)
    column_semantics: dict[str, str] = dataclass_field(default_factory=dict)
    options: dict[str, Any] = dataclass_field(default_factory=dict)


@dataclass(slots=True)
class ScreenshotBinding(JsonModel):
    asset_id: str
    path: str
    asset_type: str = "screenshot"
    description: str | None = None


@dataclass(slots=True)
class ValidationRule(JsonModel):
    rule_id: str
    kind: str
    severity: str = "error"
    field: str | None = None
    message: str | None = None
    options: dict[str, Any] = dataclass_field(default_factory=dict)


@dataclass(slots=True)
class RegionTemplate(JsonModel):
    region_id: str
    region_type: str
    locator: RegionLocator
    title: str | None = None
    extractor: ExtractionSpec | None = None
    screenshot_bindings: list[ScreenshotBinding] = dataclass_field(default_factory=list)
    validation_rules: list[ValidationRule] = dataclass_field(default_factory=list)
    order: int = 0
    required: bool = False


@dataclass(slots=True)
class SheetTemplate(JsonModel):
    sheet_id: str
    name_pattern: str
    regions: list[RegionTemplate] = dataclass_field(default_factory=list)
    required: bool = True
    order: int = 0


@dataclass(slots=True)
class TemplateSpec(JsonModel):
    template_id: str
    version: str
    name: str
    sheets: list[SheetTemplate]
    schema_version: str = TEMPLATE_SCHEMA_VERSION
    description: str | None = None
    match: TemplateMatch = dataclass_field(default_factory=TemplateMatch)
    validation_rules: list[ValidationRule] = dataclass_field(default_factory=list)
    metadata: dict[str, Any] = dataclass_field(default_factory=dict)
