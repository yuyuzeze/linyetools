"""Semantic profiles: business-only metadata, no coordinates.

A profile declares document type, filename patterns, sheet roles/aliases, field
concepts/aliases, required concepts, validation, and a small set of overrides.
It never contains locators/ranges/offsets — the :mod:`excelspec.detect` layer
owns region discovery; the profile only enriches what was detected.
"""

from .enrich import enrich_regions, profile_required_diagnostics
from .loader import ProfileValidationError, load_profile, match_profile, parse_profile
from .model import (
    FieldConcept,
    ProfileOverride,
    SemanticProfile,
    ValidationConcept,
)
from .normalize import normalize_header

__all__ = [
    "FieldConcept",
    "ProfileOverride",
    "ProfileValidationError",
    "SemanticProfile",
    "ValidationConcept",
    "enrich_regions",
    "load_profile",
    "match_profile",
    "normalize_header",
    "parse_profile",
    "profile_required_diagnostics",
]
