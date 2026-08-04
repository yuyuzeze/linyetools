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
from .manifest import ManifestAsset, ScreenshotManifest, load_screenshot_manifest
from .workbook import XlsxIngestOptions, XlsxIngestor, ingest_xlsx


class WorkbookIngestor(Protocol):
    def ingest(self, workbook: Path) -> DocumentIR: ...


__all__ = [
    "EnrichmentRequest",
    "EnrichmentResult",
    "LibreOfficeRenderer",
    "ManifestAsset",
    "OcrAdapter",
    "RenderRequest",
    "RenderResult",
    "ScreenshotManifest",
    "VlmAdapter",
    "WorkbookIngestor",
    "XlsxIngestOptions",
    "XlsxIngestor",
    "ingest_xlsx",
    "load_screenshot_manifest",
]
