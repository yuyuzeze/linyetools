"""Canonical, replayable representation of an Excel specification document."""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import StrEnum
from typing import Any

from ..serialization import JsonModel

JsonValue = None | bool | int | float | str | list["JsonValue"] | dict[str, "JsonValue"]

DOCUMENT_IR_SCHEMA_VERSION = "1.0"


class RegionType(StrEnum):
    METADATA = "metadata"
    KEY_VALUE = "key_value"
    TABLE = "table"
    FREEFORM = "freeform"
    IMAGE = "image"
    CHART = "chart"
    LAYOUT = "layout"


class AssetType(StrEnum):
    IMAGE = "image"
    SCREENSHOT = "screenshot"
    CHART = "chart"
    SHAPE = "shape"
    LAYOUT = "layout"


class DiagnosticSeverity(StrEnum):
    INFO = "info"
    WARNING = "warning"
    ERROR = "error"


@dataclass(slots=True)
class SourceRef(JsonModel):
    """Location in the source workbook; coordinates use Excel A1 notation."""

    sheet: str
    range: str | None = None
    cell: str | None = None
    workbook_path: str | None = None


@dataclass(slots=True)
class StyleIR(JsonModel):
    number_format: str | None = None
    font: dict[str, JsonValue] = field(default_factory=dict)
    fill: dict[str, JsonValue] = field(default_factory=dict)
    border: dict[str, JsonValue] = field(default_factory=dict)
    alignment: dict[str, JsonValue] = field(default_factory=dict)


@dataclass(slots=True)
class CellIR(JsonModel):
    coordinate: str
    row: int
    column: int
    raw_value: JsonValue = None
    display_value: str | None = None
    data_type: str | None = None
    formula: str | None = None
    style: StyleIR | None = None
    merged_master: str | None = None
    row_span: int = 1
    col_span: int = 1
    source: SourceRef | None = None


@dataclass(slots=True)
class TableIR(JsonModel):
    table_id: str
    cells: list[CellIR] = field(default_factory=list)
    source: SourceRef | None = None
    header_rows: int = 0
    column_semantics: dict[str, str] = field(default_factory=dict)
    metadata: dict[str, JsonValue] = field(default_factory=dict)


@dataclass(slots=True)
class RegionIR(JsonModel):
    region_id: str
    region_type: RegionType
    title: str | None = None
    source: SourceRef | None = None
    tables: list[TableIR] = field(default_factory=list)
    values: dict[str, JsonValue] = field(default_factory=dict)
    asset_ids: list[str] = field(default_factory=list)
    confidence: float | None = None
    metadata: dict[str, JsonValue] = field(default_factory=dict)


@dataclass(slots=True)
class AssetIR(JsonModel):
    asset_id: str
    asset_type: AssetType
    uri: str
    media_type: str | None = None
    description: str | None = None
    source: SourceRef | None = None
    anchor: str | None = None
    extraction_status: str = "pending"
    metadata: dict[str, JsonValue] = field(default_factory=dict)


@dataclass(slots=True)
class DiagnosticIR(JsonModel):
    code: str
    severity: DiagnosticSeverity
    message: str
    source: SourceRef | None = None
    region_id: str | None = None
    details: dict[str, JsonValue] = field(default_factory=dict)


@dataclass(slots=True)
class SheetIR(JsonModel):
    sheet_id: str
    name: str
    index: int
    regions: list[RegionIR] = field(default_factory=list)
    assets: list[AssetIR] = field(default_factory=list)
    diagnostics: list[DiagnosticIR] = field(default_factory=list)
    metadata: dict[str, JsonValue] = field(default_factory=dict)


@dataclass(slots=True)
class DocumentIR(JsonModel):
    document_id: str
    title: str
    sheets: list[SheetIR]
    schema_version: str = DOCUMENT_IR_SCHEMA_VERSION
    source_path: str | None = None
    template_id: str | None = None
    template_version: str | None = None
    assets: list[AssetIR] = field(default_factory=list)
    diagnostics: list[DiagnosticIR] = field(default_factory=list)
    metadata: dict[str, JsonValue] = field(default_factory=dict)
