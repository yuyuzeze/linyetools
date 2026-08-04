"""Public schema and business validation API."""

from typing import Protocol

from ..models.document_ir import DiagnosticIR, DocumentIR
from .core import (
    ValidationResult,
    validate_business_rules,
    validate_document,
    validate_ir_data,
    validate_ir_schema,
    validate_template_structure,
)


class DocumentValidator(Protocol):
    def validate(self, document: DocumentIR) -> list[DiagnosticIR]: ...


__all__ = [
    "DocumentValidator",
    "ValidationResult",
    "validate_business_rules",
    "validate_document",
    "validate_ir_data",
    "validate_ir_schema",
    "validate_template_structure",
]
