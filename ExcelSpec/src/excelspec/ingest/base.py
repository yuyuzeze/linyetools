"""Ingestor abstraction and engine dispatch (sparse / legacy / auto)."""

from __future__ import annotations

import zipfile
from pathlib import Path
from typing import Protocol

from ..models.document_ir import DiagnosticIR, DiagnosticSeverity, DocumentIR
from .workbook import LegacyOpenpyxlIngestor, XlsxIngestOptions


class UnsupportedWorkbookError(Exception):
    """Raised by the sparse ingestor for a workbook it cannot handle.

    This is the **only** condition under which ``engine="auto"`` falls back to
    the legacy ingestor. Ordinary programming errors propagate untouched so
    bugs are never masked by a silent fallback.
    """


class WorkbookIngestor(Protocol):
    def ingest(self, workbook: Path) -> DocumentIR: ...


# Exceptions that make a workbook genuinely unreadable by the sparse path and
# therefore justify a legacy fallback under ``engine="auto"``.
try:  # pragma: no cover - import shape depends on openpyxl version
    from openpyxl.utils.exceptions import InvalidFileException

    _FALLBACK_EXCEPTIONS: tuple[type[Exception], ...] = (
        UnsupportedWorkbookError,
        InvalidFileException,
        zipfile.BadZipFile,
    )
except Exception:  # pragma: no cover
    _FALLBACK_EXCEPTIONS = (UnsupportedWorkbookError, zipfile.BadZipFile)


def ingest_with_engine(
    workbook: Path,
    options: XlsxIngestOptions,
    *,
    engine: str = "auto",
) -> DocumentIR:
    """Dispatch ingestion by engine.

    * ``sparse`` — always the sparse OOXML ingestor (errors propagate).
    * ``legacy`` — always the legacy openpyxl double-load ingestor.
    * ``auto``   — sparse first; on a genuine unsupported-workbook error, fall
      back to legacy and record an observable diagnostic + metadata. Any other
      exception (i.e. a real bug) is **not** caught.
    """

    from .sparse import SparseOoxmlIngestor

    engine = (engine or "auto").lower()
    if engine not in {"auto", "sparse", "legacy"}:
        raise ValueError(f"未知 ingest engine: {engine!r}（可选 auto|sparse|legacy）")

    if engine == "legacy":
        document = LegacyOpenpyxlIngestor(options).ingest(workbook)
        document.metadata.setdefault("legacy_fallback", False)
        document.metadata.setdefault("fallback_reason", None)
        return document

    if engine == "sparse":
        return SparseOoxmlIngestor(options).ingest(workbook)

    # auto
    try:
        return SparseOoxmlIngestor(options).ingest(workbook)
    except _FALLBACK_EXCEPTIONS as reason:
        document = LegacyOpenpyxlIngestor(options).ingest(workbook)
        document.metadata["legacy_fallback"] = True
        document.metadata["fallback_reason"] = f"{type(reason).__name__}: {reason}"
        document.diagnostics.append(
            DiagnosticIR(
                code="INGEST_LEGACY_FALLBACK",
                severity=DiagnosticSeverity.WARNING,
                message=f"sparse ingest 不支持该工作簿，已回退 legacy: {reason}",
                details={"reason_type": type(reason).__name__, "reason": str(reason)},
            )
        )
        return document


__all__ = [
    "UnsupportedWorkbookError",
    "WorkbookIngestor",
    "ingest_with_engine",
]
