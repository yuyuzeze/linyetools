"""Zero-config assembly: SparseWorkbookIR -> DocumentIR via detect + route."""

from __future__ import annotations

from pathlib import Path

from openpyxl.utils import range_boundaries

from ..ingest.sparse_model import SparseWorkbookIR
from ..models.document_ir import (
    AssetIR,
    AssetType,
    DiagnosticIR,
    DiagnosticSeverity,
    DocumentIR,
    SheetIR,
)
from ..profile.enrich import enrich_regions, profile_required_diagnostics
from ..profile.model import SemanticProfile
from ..providers import (
    NullOcrProvider,
    NullVlmProvider,
    OcrProvider,
    ProviderContext,
    VlmProvider,
)
from .detector import RegionDetector
from .models import CandidateRegion, CandidateRegionType
from .router import RegionRouter


def _override_for(profile: SemanticProfile | None, sheet_name: str, role: str | None):
    if profile is None:
        return None
    for override in profile.overrides:
        if override.sheet and override.sheet == sheet_name:
            return override
        if override.sheet_alias and override.sheet_alias == role:
            return override
    return None


def _ignored(bounds, ignore_ranges: list[str]) -> bool:
    for ignore in ignore_ranges:
        try:
            min_col, min_row, max_col, max_row = range_boundaries(ignore)
        except ValueError:
            continue
        if (
            bounds.min_row >= min_row
            and bounds.max_row <= max_row
            and bounds.min_col >= min_col
            and bounds.max_col <= max_col
        ):
            return True
    return False


def assemble_document(
    sparse: SparseWorkbookIR,
    *,
    mode: str = "fast",
    profile: SemanticProfile | None = None,
    ocr: OcrProvider | None = None,
    vlm: VlmProvider | None = None,
) -> tuple[DocumentIR, dict[str, list[str]]]:
    """Build a DocumentIR from the sparse IR using detection + routing."""

    path = Path(sparse.path)
    detector = RegionDetector()
    router = RegionRouter(sparse)
    sheets: list[SheetIR] = []
    unrecognized: dict[str, list[str]] = {}

    for sparse_sheet in sparse.sheets:
        role = profile.sheet_role(sparse_sheet.name) if profile else None
        override = _override_for(profile, sparse_sheet.name, role)
        if override and override.exclude_sheet:
            continue

        candidates = detector.detect_sheet_regions(sparse_sheet, sparse.styles)
        if override and override.ignore:
            candidates = [
                c for c in candidates if not _ignored(c.bounds, override.ignore)
            ]

        regions = [router.route(sparse_sheet, candidate) for candidate in candidates]

        sheet_diagnostics: list[DiagnosticIR] = list(sparse_sheet.diagnostics)
        for candidate in candidates:
            sheet_diagnostics.extend(candidate.diagnostics)
        if profile:
            sheet_diagnostics.extend(enrich_regions(profile, sparse_sheet.name, regions))

        metadata = {"extraction_mode": mode, "detected_region_count": len(regions)}
        if role:
            metadata["sheet_role"] = role
        sheets.append(
            SheetIR(
                sheet_id=sparse_sheet.sheet_id,
                name=sparse_sheet.name,
                index=sparse_sheet.index,
                regions=regions,
                assets=list(sparse_sheet.assets),
                diagnostics=sheet_diagnostics,
                metadata=metadata,
            )
        )
        unrecognized[sparse_sheet.name] = [
            candidate.bounds.a1()
            for candidate in candidates
            if candidate.region_type == CandidateRegionType.FREEFORM
        ]

    if mode in ("auto", "visual"):
        _capture_layout_regions(sparse, sheets)
        _apply_visual_providers(
            sheets, ocr=ocr or NullOcrProvider(), vlm=vlm or NullVlmProvider()
        )

    document = DocumentIR(
        document_id=path.stem,
        title=sparse.properties.get("title") or path.stem,
        source_path=str(path),
        sheets=sheets,
        diagnostics=list(sparse.document_diagnostics),
        metadata={
            "ingestor": "sparse-ooxml",
            "extraction_mode": mode,
            "profile_id": profile.profile_id if profile else None,
            "document_type": profile.document_type if profile else None,
            "asset_directory": sparse.metadata.get("asset_directory"),
            "sparse_stats": sparse.metadata.get("sparse_stats", {}),
        },
    )
    if profile:
        document.diagnostics.extend(profile_required_diagnostics(profile, document))
    return document, unrecognized


def _apply_visual_providers(
    sheets: list[SheetIR], *, ocr: OcrProvider, vlm: VlmProvider
) -> None:
    """Run available OCR/VLM providers over visual regions (auto/visual only).

    Only called when a provider reports ``available``. A provider failure records
    a diagnostic and never discards the region's assets. Results are tagged with
    provider / source / confidence.
    """

    if not getattr(ocr, "available", False) and not getattr(vlm, "available", False):
        return
    for sheet in sheets:
        for region in sheet.regions:
            if not region.metadata.get("visual"):
                continue
            asset_id = region.asset_ids[0] if region.asset_ids else None
            context = ProviderContext(
                sheet=sheet.name,
                region_id=region.region_id,
                title=region.title,
                source_range=region.source.range if region.source else None,
            )
            for provider, kind, method in (
                (vlm, "vlm", "describe"),
                (ocr, "ocr", "extract"),
            ):
                if not getattr(provider, "available", False):
                    continue
                try:
                    result = getattr(provider, method)(asset_id, context)
                except Exception as error:  # noqa: BLE001 - provider is best-effort
                    region.metadata.setdefault("diagnostics", []).append(
                        DiagnosticIR(
                            code=f"provider.{kind}_failed",
                            severity=DiagnosticSeverity.WARNING,
                            message=f"{kind} provider 失败，保留视觉资源: {region.region_id} ({error})",
                            source=region.source,
                            region_id=region.region_id,
                        ).to_dict()
                    )
                    continue
                region.metadata[f"{kind}_result"] = {
                    "text": result.text,
                    "provider": result.provider,
                    "source": result.source,
                    "confidence": result.confidence,
                }


def _capture_layout_regions(sparse: SparseWorkbookIR, sheets: list[SheetIR]) -> None:
    """auto/visual: screenshot layout regions, reusing one Excel session.

    A capture failure never drops structured content — it only records a
    warning on the region.
    """

    targets = [
        (sheet, region)
        for sheet in sheets
        for region in sheet.regions
        if region.metadata.get("visual") and region.source and region.source.range
    ]
    if not targets:
        return

    from ..render import ExcelCaptureSession

    asset_root = Path(sparse.metadata.get("asset_directory") or ".")
    with ExcelCaptureSession(sparse.path) as session:
        for sheet, region in targets:
            destination = asset_root / "screenshots" / f"{sheet.sheet_id}-{region.region_id}.png"
            try:
                image = session.capture(sheet.name, region.source.range, destination)
            except Exception as error:  # noqa: BLE001 - screenshot is best-effort
                region.metadata.setdefault("diagnostics", []).append(
                    DiagnosticIR(
                        code="route.screenshot_failed",
                        severity=DiagnosticSeverity.WARNING,
                        message=f"截图失败，保留结构化内容: {region.region_id} ({error})",
                        source=region.source,
                        region_id=region.region_id,
                    ).to_dict()
                )
                continue
            asset_id = f"{sheet.sheet_id}-{region.region_id}-screenshot"
            sheet.assets.append(
                AssetIR(
                    asset_id=asset_id,
                    asset_type=AssetType.SCREENSHOT,
                    uri=str(image),
                    media_type="image/png",
                    description=region.title or region.region_id,
                    source=region.source,
                    anchor=region.source.range,
                    extraction_status="rendered",
                    metadata={"capture_method": "excel_com", "region_id": region.region_id},
                )
            )
            region.asset_ids.append(asset_id)


__all__ = ["assemble_document"]
