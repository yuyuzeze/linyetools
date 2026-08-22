"""Lightweight formula reference extraction (no formula evaluation).

Recognises same-sheet, cross-sheet (quoted / unquoted), external-workbook, and
named-range references. It never computes results — the cached display value is
preserved, and a formula it cannot decompose is kept verbatim with a metadata
flag.
"""

from __future__ import annotations

import re

from ..models.document_ir import DocumentIR
from ..models.semantic import (
    ReferenceIR,
    ReferenceTarget,
    ReferenceType,
)

_RANGE = r"\$?[A-Z]{1,3}\$?[0-9]+(?::\$?[A-Z]{1,3}\$?[0-9]+)?"

# [Book.xlsx]'Sheet Name'!A1  /  'Sheet Name'!A1:B2
_QUOTED = re.compile(rf"(?:\[(?P<wb>[^\]]+)\])?'(?P<sheet>[^']+)'!(?P<range>{_RANGE})")
# [Book.xlsx]Sheet2!A1  /  基本情報!B3
_UNQUOTED = re.compile(
    rf"(?:\[(?P<wb>[^\]]+)\])?(?P<sheet>[^\s'!\[\]()+\-*/,=&<>]+)!(?P<range>{_RANGE})"
)
# a bare cell/range not preceded by a sheet separator or identifier char
_SAME_SHEET = re.compile(rf"(?<![A-Za-z0-9_!'$]){_RANGE}")
# a bare identifier that could be a named range (letters/underscore, not a cell)
_NAME = re.compile(r"(?<![A-Za-z0-9_!'])(?P<name>[A-Za-z_　-￿][A-Za-z0-9_　-￿]*)(?!\s*\()")


def _norm(text: str) -> str:
    return text.replace("$", "")


def extract_targets(formula: str) -> tuple[list[ReferenceTarget], ReferenceType, bool]:
    """Return (targets, dominant_type, resolved) for one formula string."""

    body = formula[1:] if formula.startswith("=") else formula
    targets: list[ReferenceTarget] = []
    has_external = False
    has_cross = False

    consumed_spans: list[tuple[int, int]] = []
    for match in _QUOTED.finditer(body):
        workbook = match.group("wb")
        targets.append(
            ReferenceTarget(
                workbook=workbook,
                sheet=match.group("sheet"),
                range=_norm(match.group("range")),
            )
        )
        has_external = has_external or bool(workbook)
        has_cross = True
        consumed_spans.append(match.span())

    for match in _UNQUOTED.finditer(body):
        if any(start <= match.start() < end for start, end in consumed_spans):
            continue
        workbook = match.group("wb")
        targets.append(
            ReferenceTarget(
                workbook=workbook,
                sheet=match.group("sheet"),
                range=_norm(match.group("range")),
            )
        )
        has_external = has_external or bool(workbook)
        has_cross = True
        consumed_spans.append(match.span())

    if not targets:
        # same-sheet cell references
        for match in _SAME_SHEET.finditer(body):
            targets.append(ReferenceTarget(range=_norm(match.group(0))))
        if targets:
            return targets, ReferenceType.SAME_SHEET, True
        # named range fallback (conservative)
        names = [m.group("name") for m in _NAME.finditer(body)]
        # drop obvious function names (uppercase-only tokens immediately reused)
        names = [n for n in names if not n.isupper() or "_" in n]
        if names:
            return (
                [ReferenceTarget(name=name) for name in dict.fromkeys(names)],
                ReferenceType.NAMED_RANGE,
                False,
            )
        return [], ReferenceType.SAME_SHEET, False

    if has_external:
        return targets, ReferenceType.EXTERNAL, True
    if has_cross:
        return targets, ReferenceType.CROSS_SHEET, True
    return targets, ReferenceType.SAME_SHEET, True


def extract_references(document: DocumentIR) -> list[ReferenceIR]:
    """Extract a ReferenceIR for every formula cell in the document."""

    references: list[ReferenceIR] = []
    counter = 0
    for sheet in document.sheets:
        for region in sheet.regions:
            for table in region.tables:
                for cell in table.cells:
                    if not cell.formula:
                        continue
                    counter += 1
                    targets, ref_type, resolved = extract_targets(cell.formula)
                    metadata: dict = {}
                    if not resolved:
                        metadata["unparsed"] = True
                    references.append(
                        ReferenceIR(
                            reference_id=f"{sheet.sheet_id}:{cell.coordinate}:ref-{counter}",
                            source_sheet=sheet.name,
                            source_cell=cell.coordinate,
                            formula=cell.formula,
                            targets=targets,
                            reference_type=ref_type,
                            resolved=resolved,
                            display_value=cell.display_value,
                            metadata=metadata,
                        )
                    )
    return references


__all__ = ["extract_references", "extract_targets"]
