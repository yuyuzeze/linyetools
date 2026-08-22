"""Semantic coverage checks: does everything detected reach a chunk?"""

from __future__ import annotations

from dataclasses import dataclass, field

from ..models.chunk import KnowledgeChunkIR
from ..models.document_ir import DiagnosticIR, DiagnosticSeverity
from ..models.semantic import SemanticDocumentIR, SemanticRegionType

_LOW_CONFIDENCE = 0.55


@dataclass(slots=True)
class CoverageReport:
    stats: dict = field(default_factory=dict)
    diagnostics: list[DiagnosticIR] = field(default_factory=list)


def analyze_coverage(
    document: SemanticDocumentIR, chunks: list[KnowledgeChunkIR]
) -> CoverageReport:
    diagnostics: list[DiagnosticIR] = []

    region_ids = {region.region_id for region in document.regions}
    chunk_region_ids = {chunk.region_id for chunk in chunks if chunk.region_id}

    # every semantic region should reach at least one chunk
    for region in document.regions:
        if region.region_id not in chunk_region_ids:
            diagnostics.append(
                DiagnosticIR(
                    code="coverage.region_without_chunk",
                    severity=DiagnosticSeverity.WARNING,
                    message=f"语义区域未进入任何 chunk: {region.region_id}",
                    region_id=region.region_id,
                )
            )
        if not region.source_range:
            diagnostics.append(
                DiagnosticIR(
                    code="coverage.region_without_source",
                    severity=DiagnosticSeverity.WARNING,
                    message=f"语义区域缺少 source_range: {region.region_id}",
                    region_id=region.region_id,
                )
            )

    # table rows: every row should be chunked
    table_row_count = 0
    for region in document.regions:
        if region.region_type == SemanticRegionType.TABLE and region.table:
            table_row_count += len(region.table.rows)
    chunked_table_rows = sum(
        len(chunk.structured_data.get("rows", []))
        for chunk in chunks
        if chunk.chunk_type == "table"
    )
    if chunked_table_rows < table_row_count:
        diagnostics.append(
            DiagnosticIR(
                code="coverage.table_rows_missing",
                severity=DiagnosticSeverity.WARNING,
                message=f"表格行未全部进入 chunk: {chunked_table_rows}/{table_row_count}",
            )
        )

    # duplicate chunk ids
    ids = [chunk.chunk_id for chunk in chunks]
    if len(ids) != len(set(ids)):
        diagnostics.append(
            DiagnosticIR(
                code="coverage.duplicate_chunk",
                severity=DiagnosticSeverity.ERROR,
                message="发现重复的 chunk_id",
            )
        )

    # asset referencing
    referenced = sum(1 for asset in document.assets if asset.referenced)
    unreferenced = [asset for asset in document.assets if not asset.referenced]
    for asset in unreferenced:
        diagnostics.append(
            DiagnosticIR(
                code="coverage.unreferenced_asset",
                severity=DiagnosticSeverity.INFO,
                message=f"资产未被任何区域引用: {asset.asset_id}",
            )
        )

    confidences = [region.confidence for region in document.regions]
    low_confidence = [region for region in document.regions if region.confidence < _LOW_CONFIDENCE]
    regions_with_source = sum(1 for region in document.regions if region.source_range)
    source_coverage = (
        regions_with_source / len(document.regions) if document.regions else 1.0
    )

    stats = {
        "semantic_region_count": len(document.regions),
        "chunk_count": len(chunks),
        "table_row_count": table_row_count,
        "chunked_table_row_count": chunked_table_rows,
        "referenced_asset_count": referenced,
        "unreferenced_asset_count": len(unreferenced),
        "source_coverage": round(source_coverage, 4),
        "average_confidence": round(sum(confidences) / len(confidences), 4) if confidences else 0.0,
        "low_confidence_count": len(low_confidence),
        "reference_count": len(document.references),
    }
    return CoverageReport(stats=stats, diagnostics=diagnostics)


__all__ = ["CoverageReport", "analyze_coverage"]
