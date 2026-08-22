"""Apply a semantic profile to already-detected & routed regions.

Enrichment is purely semantic: it names sheet roles and maps table headers to
field concepts. It never moves, splits, or drops content — unmatched headers are
kept verbatim, and both the source header and the semantic name are preserved.
"""

from __future__ import annotations

from openpyxl.utils import column_index_from_string

from ..models.document_ir import (
    DiagnosticIR,
    DiagnosticSeverity,
    RegionIR,
    SourceRef,
)
from .model import SemanticProfile


def enrich_regions(
    profile: SemanticProfile, sheet_name: str, regions: list[RegionIR]
) -> list[DiagnosticIR]:
    """Enrich regions in place; return semantic diagnostics."""

    diagnostics: list[DiagnosticIR] = []
    role = profile.sheet_role(sheet_name)
    for region in regions:
        if role:
            region.metadata["sheet_role"] = role
        for table in region.tables:
            header_labels = table.metadata.get("header_labels", {})
            if not header_labels:
                continue
            field_mapping: list[dict] = []
            semantics: dict[str, str] = {}
            for column_letter, label in header_labels.items():
                if not label:
                    continue
                concepts, method = profile.match_field(label)
                if len(concepts) > 1:
                    diagnostics.append(
                        DiagnosticIR(
                            code="profile.ambiguous_header",
                            severity=DiagnosticSeverity.WARNING,
                            message=f"表头 '{label}' 同时匹配多个 concept: {concepts}",
                            source=SourceRef(sheet=sheet_name),
                            region_id=region.region_id,
                            details={"header": label, "concepts": concepts},
                        )
                    )
                if concepts:
                    concept = concepts[0]
                    semantics[column_letter] = concept
                    field_mapping.append(
                        {
                            "source_header": label,
                            "semantic_name": concept,
                            "source_column": column_letter,
                            "confidence": 1.0 if method == "exact_alias" else 0.7,
                        }
                    )
                else:
                    # unmatched header kept verbatim, semantic_name None
                    field_mapping.append(
                        {
                            "source_header": label,
                            "semantic_name": None,
                            "source_column": column_letter,
                            "confidence": 0.0,
                        }
                    )
            if semantics:
                # keep only the first physical column per concept (merged headers)
                seen: set[str] = set()
                for column_letter in sorted(semantics, key=column_index_from_string):
                    concept = semantics[column_letter]
                    if concept not in seen:
                        table.column_semantics[column_letter] = concept
                        seen.add(concept)
            table.metadata["field_mapping"] = field_mapping
    return diagnostics


def profile_required_diagnostics(
    profile: SemanticProfile, document
) -> list[DiagnosticIR]:
    """Emit diagnostics for required concepts never mapped in the document."""

    mapped: set[str] = set()
    for sheet in document.sheets:
        for region in sheet.regions:
            for table in region.tables:
                mapped.update(table.column_semantics.values())
            mapped.update(region.values.keys())
    diagnostics: list[DiagnosticIR] = []
    for rule in profile.validation:
        if rule.required and rule.concept not in mapped:
            diagnostics.append(
                DiagnosticIR(
                    code="profile.required_concept_missing",
                    severity=DiagnosticSeverity.WARNING,
                    message=f"必填 concept 未在文档中出现: {rule.concept}",
                    details={"concept": rule.concept},
                )
            )
    return diagnostics


__all__ = ["enrich_regions", "profile_required_diagnostics"]
