"""Semantic extraction extension point (implemented in stage 3)."""

from typing import Protocol

from ..models.document_ir import DocumentIR
from ..models.template import TemplateSpec


class DocumentExtractor(Protocol):
    def extract(self, document: DocumentIR, template: TemplateSpec) -> DocumentIR: ...


__all__ = ["DocumentExtractor"]
