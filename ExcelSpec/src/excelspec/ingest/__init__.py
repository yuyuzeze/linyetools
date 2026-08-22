"""Public XLSX ingestion API and optional adapter contracts."""

from pathlib import Path
from typing import Protocol

from ..models.document_ir import DocumentIR
from .adapters import (
    EnrichmentRequest,
    EnrichmentResult,
    LibreOfficeRenderer,
    OcrAdapter,
    RenderRequest,
    RenderResult,
    VlmAdapter,
)
from .base import UnsupportedWorkbookError, ingest_with_engine
from .manifest import ManifestAsset, ScreenshotManifest, load_screenshot_manifest
from .sparse import SparseOoxmlIngestor
from .sparse_model import SparseCell, SparseSheet, SparseWorkbookIR
from .workbook import (
    LegacyOpenpyxlIngestor,
    XlsxIngestOptions,
    XlsxIngestor,
)


class WorkbookIngestor(Protocol):
    def ingest(self, workbook: Path) -> DocumentIR: ...


def ingest_xlsx(
    workbook: str | Path,
    *,
    asset_dir: str | Path | None = None,
    screenshot_manifest: str | Path | None = None,
    include_images: bool = True,
    include_shapes: bool = True,
    engine: str = "auto",
) -> DocumentIR:
    """Ingest an XLSX into a DocumentIR.

    ``engine`` selects the ingestor: ``"auto"`` (default) uses the sparse OOXML
    ingestor and falls back to legacy only on a genuine unsupported workbook;
    ``"sparse"`` and ``"legacy"`` force one path.
    """

    options = XlsxIngestOptions(
        asset_dir=Path(asset_dir) if asset_dir else None,
        screenshot_manifest=Path(screenshot_manifest) if screenshot_manifest else None,
        include_images=include_images,
        include_shapes=include_shapes,
    )
    return ingest_with_engine(Path(workbook), options, engine=engine)


def ingest_sparse_workbook(
    workbook: str | Path,
    *,
    asset_dir: str | Path | None = None,
    screenshot_manifest: str | Path | None = None,
    include_images: bool = True,
    include_shapes: bool = True,
) -> SparseWorkbookIR:
    """Ingest an XLSX into the sparse IR (with drawings) for region detection."""

    options = XlsxIngestOptions(
        asset_dir=Path(asset_dir) if asset_dir else None,
        screenshot_manifest=Path(screenshot_manifest) if screenshot_manifest else None,
        include_images=include_images,
        include_shapes=include_shapes,
    )
    return SparseOoxmlIngestor(options).build_sparse_workbook(Path(workbook))


__all__ = [
    "EnrichmentRequest",
    "EnrichmentResult",
    "LegacyOpenpyxlIngestor",
    "LibreOfficeRenderer",
    "ManifestAsset",
    "OcrAdapter",
    "RenderRequest",
    "RenderResult",
    "ScreenshotManifest",
    "SparseCell",
    "SparseOoxmlIngestor",
    "SparseSheet",
    "SparseWorkbookIR",
    "UnsupportedWorkbookError",
    "VlmAdapter",
    "WorkbookIngestor",
    "XlsxIngestOptions",
    "XlsxIngestor",
    "ingest_sparse_workbook",
    "ingest_xlsx",
]
