"""KnowledgeChunkIR: one retrieval unit, serialized as one JSONL line."""

from __future__ import annotations

from dataclasses import dataclass, field

from ..serialization import JsonModel

CHUNK_SCHEMA_VERSION = "1"

JsonValue = None | bool | int | float | str | list["JsonValue"] | dict[str, "JsonValue"]


@dataclass(slots=True)
class ChunkSource(JsonModel):
    workbook: str | None = None
    sheet: str | None = None
    range: str | None = None


@dataclass(slots=True)
class KnowledgeChunkIR(JsonModel):
    chunk_id: str
    document_id: str
    chunk_index: int
    chunk_type: str
    text: str
    source: ChunkSource
    content_hash: str
    schema_version: str = CHUNK_SCHEMA_VERSION
    document_type: str | None = None
    sheet: str | None = None
    sheet_role: str | None = None
    section_path: list[str] = field(default_factory=list)
    region_id: str | None = None
    title: str | None = None
    structured_data: dict[str, JsonValue] = field(default_factory=dict)
    asset_refs: list[str] = field(default_factory=list)
    formula_refs: list[str] = field(default_factory=list)
    confidence: float = 0.0
    metadata: dict[str, JsonValue] = field(default_factory=dict)


__all__ = ["CHUNK_SCHEMA_VERSION", "ChunkSource", "KnowledgeChunkIR"]
