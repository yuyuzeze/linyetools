"""Annotation and result models for the evaluation harness."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable


@dataclass(slots=True)
class ExpectedRegion:
    sheet: str
    range: str          # A1 range, e.g. "A2:H6"
    type: str           # table / key_value / text / image / shape / layout / freeform
    header_rows: int | None = None
    title: str | None = None
    row_count: int | None = None   # expected data-row count for tables


@dataclass(slots=True)
class EvalCase:
    case_id: str
    description: str
    build: Callable[[object], None]   # build(path) -> writes an .xlsx
    expected_regions: list[ExpectedRegion] = field(default_factory=list)
    expected_fields: dict = field(default_factory=dict)     # {sheet: {column_letter: concept}}
    expected_assets: list[str] = field(default_factory=list)
    expected_references: int | None = None                  # count of formula refs
    profile: str | None = None                              # inline profile yaml, optional
    tags: list[str] = field(default_factory=list)


__all__ = ["EvalCase", "ExpectedRegion"]
