"""Public template loading, matching, and extraction API."""

from pathlib import Path
from typing import Protocol

from ..models.document_ir import DocumentIR
from ..models.template import TemplateSpec
from .engine import (
    ExtractionResult,
    MatchResult,
    TemplateCandidate,
    apply_best_template,
    extract_with_template,
    locate_region,
    locate_regions,
    match_template,
    score_template,
)
from .loader import (
    TemplateValidationError,
    load_template,
    load_templates,
    validate_template_data,
)


class TemplateLoader(Protocol):
    def load(self, path: Path) -> TemplateSpec: ...


class TemplateMatcher(Protocol):
    def score(self, document: DocumentIR, template: TemplateSpec) -> float: ...


__all__ = [
    "ExtractionResult",
    "MatchResult",
    "TemplateCandidate",
    "TemplateLoader",
    "TemplateMatcher",
    "TemplateValidationError",
    "apply_best_template",
    "extract_with_template",
    "load_template",
    "load_templates",
    "locate_region",
    "locate_regions",
    "match_template",
    "score_template",
    "validate_template_data",
]
