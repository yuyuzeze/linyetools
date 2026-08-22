"""Semantic profile data model and field/sheet resolution."""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from .normalize import normalize_header


@dataclass(slots=True)
class FieldConcept:
    concept: str
    aliases: list[str] = field(default_factory=list)
    regex_aliases: list[str] = field(default_factory=list)

    def normalized_aliases(self) -> set[str]:
        return {normalize_header(alias) for alias in self.aliases}


@dataclass(slots=True)
class ValidationConcept:
    concept: str
    required: bool = False


@dataclass(slots=True)
class ProfileOverride:
    sheet_alias: str | None = None
    sheet: str | None = None
    ignore: list[str] = field(default_factory=list)
    force_region_type: str | None = None
    exclude_sheet: bool = False
    title: str | None = None


@dataclass(slots=True)
class SemanticProfile:
    schema_version: str
    profile_id: str
    document_type: str
    filename_patterns: list[str] = field(default_factory=list)
    sheet_aliases: dict[str, list[str]] = field(default_factory=dict)
    fields: dict[str, FieldConcept] = field(default_factory=dict)
    required_concepts: dict[str, list[str]] = field(default_factory=dict)
    validation: list[ValidationConcept] = field(default_factory=list)
    overrides: list[ProfileOverride] = field(default_factory=list)

    # -- resolution helpers ----------------------------------------------------

    def sheet_role(self, sheet_name: str) -> str | None:
        """Return the profile role for a sheet name via normalized aliases."""

        target = normalize_header(sheet_name)
        for role, aliases in self.sheet_aliases.items():
            if any(normalize_header(alias) == target for alias in aliases):
                return role
        return None

    def match_field(self, header: str) -> tuple[list[str], str]:
        """Return (concepts, method) matching a header.

        Exact normalized alias wins; regex aliases are an opt-in fallback. A
        header matching several concepts returns them all so the caller can
        emit an ambiguity diagnostic. An empty list means "keep source header".
        """

        normalized = normalize_header(header)
        if not normalized:
            return [], "empty"
        exact = [
            concept.concept
            for concept in self.fields.values()
            if normalized in concept.normalized_aliases()
        ]
        if exact:
            return exact, "exact_alias"
        regex_hits = [
            concept.concept
            for concept in self.fields.values()
            for pattern in concept.regex_aliases
            if _safe_search(pattern, header)
        ]
        if regex_hits:
            return regex_hits, "regex_alias"
        return [], "unmatched"

    def filename_matches(self, filename: str) -> bool:
        return any(_safe_search(pattern, filename) for pattern in self.filename_patterns)


def _safe_search(pattern: str, value: str) -> bool:
    try:
        return re.search(pattern, value) is not None
    except re.error:
        return False


__all__ = [
    "FieldConcept",
    "ProfileOverride",
    "SemanticProfile",
    "ValidationConcept",
]
