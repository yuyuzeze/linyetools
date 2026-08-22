"""SemanticDocumentIR: a knowledge-oriented view built from routed regions.

Distinct from :class:`DocumentIR` (the layout/compat model). The semantic model
never copies full ``CellIR`` objects — a table keeps structured columns/rows with
source provenance, and every semantic region traces back to workbook/sheet/range.
It is produced only by :mod:`excelspec.semantic.assembler` (a forward transform),
never by parsing Markdown back into structure.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import StrEnum
from typing import Any

from ..serialization import JsonModel

SEMANTIC_SCHEMA_VERSION = "1"

JsonValue = None | bool | int | float | str | list["JsonValue"] | dict[str, "JsonValue"]


class SemanticRegionType(StrEnum):
    TABLE = "table"
    KEY_VALUE = "key_value"
    TEXT = "text"
    IMAGE = "image"
    SHAPE = "shape"
    LAYOUT = "layout"
    FREEFORM = "freeform"


class ReferenceType(StrEnum):
    SAME_SHEET = "same_sheet"
    CROSS_SHEET = "cross_sheet"
    EXTERNAL = "external"
    NAMED_RANGE = "named_range"


@dataclass(slots=True)
class SemanticSource(JsonModel):
    workbook: str | None = None
    sheet: str | None = None
    range: str | None = None


@dataclass(slots=True)
class ReferenceTarget(JsonModel):
    workbook: str | None = None
    sheet: str | None = None
    range: str | None = None
    name: str | None = None


@dataclass(slots=True)
class ReferenceIR(JsonModel):
    reference_id: str
    source_sheet: str
    source_cell: str
    formula: str
    targets: list[ReferenceTarget] = field(default_factory=list)
    reference_type: ReferenceType = ReferenceType.SAME_SHEET
    resolved: bool = True
    display_value: str | None = None
    metadata: dict[str, JsonValue] = field(default_factory=dict)


@dataclass(slots=True)
class SemanticColumn(JsonModel):
    column_id: str
    source_header: str | None = None
    semantic_name: str | None = None
    display_name: str | None = None
    confidence: float = 0.0


@dataclass(slots=True)
class SemanticRow(JsonModel):
    row_id: str
    source_range: str | None = None
    values: dict[str, JsonValue] = field(default_factory=dict)          # by semantic/column_id
    source_values: dict[str, JsonValue] = field(default_factory=dict)   # by source header
    formulas: dict[str, str] = field(default_factory=dict)
    confidence: float = 1.0


@dataclass(slots=True)
class SemanticTable(JsonModel):
    columns: list[SemanticColumn] = field(default_factory=list)
    rows: list[SemanticRow] = field(default_factory=list)
    header_rows: int = 1


@dataclass(slots=True)
class KeyValueEntry(JsonModel):
    key: str
    value: JsonValue = None
    semantic_name: str | None = None
    source_cell: str | None = None
    formula: str | None = None
    confidence: float = 1.0


@dataclass(slots=True)
class SemanticRegion(JsonModel):
    region_id: str
    region_type: SemanticRegionType
    sheet: str
    sheet_role: str | None = None
    title: str | None = None
    section_path: list[str] = field(default_factory=list)
    source_range: str | None = None
    confidence: float = 0.0
    detection_method: str | None = None
    text: str | None = None
    table: SemanticTable | None = None
    key_values: list[KeyValueEntry] = field(default_factory=list)
    asset_refs: list[str] = field(default_factory=list)
    formula_refs: list[str] = field(default_factory=list)
    metadata: dict[str, JsonValue] = field(default_factory=dict)
    diagnostics: list[dict[str, JsonValue]] = field(default_factory=list)


@dataclass(slots=True)
class SemanticAsset(JsonModel):
    asset_id: str
    asset_type: str
    uri: str
    sheet: str | None = None
    description: str | None = None
    anchor: str | None = None
    referenced: bool = False
    metadata: dict[str, JsonValue] = field(default_factory=dict)


@dataclass(slots=True)
class SemanticSheet(JsonModel):
    sheet_id: str
    name: str
    index: int
    sheet_role: str | None = None
    region_ids: list[str] = field(default_factory=list)


@dataclass(slots=True)
class SemanticDocumentIR(JsonModel):
    document_id: str
    title: str
    schema_version: str = SEMANTIC_SCHEMA_VERSION
    document_type: str | None = None
    source_path: str | None = None
    source_hash: str | None = None
    profile_id: str | None = None
    processing_mode: str | None = None
    language: str | None = None
    sheets: list[SemanticSheet] = field(default_factory=list)
    sections: list[dict[str, JsonValue]] = field(default_factory=list)
    regions: list[SemanticRegion] = field(default_factory=list)
    assets: list[SemanticAsset] = field(default_factory=list)
    references: list[ReferenceIR] = field(default_factory=list)
    diagnostics: list[dict[str, JsonValue]] = field(default_factory=list)
    metadata: dict[str, JsonValue] = field(default_factory=dict)


__all__ = [
    "KeyValueEntry",
    "ReferenceIR",
    "ReferenceTarget",
    "ReferenceType",
    "SEMANTIC_SCHEMA_VERSION",
    "SemanticAsset",
    "SemanticColumn",
    "SemanticDocumentIR",
    "SemanticRegion",
    "SemanticRegionType",
    "SemanticRow",
    "SemanticSheet",
    "SemanticSource",
    "SemanticTable",
]
