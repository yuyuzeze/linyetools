"""Semantic assembly: DocumentIR (routed regions) -> SemanticDocumentIR."""

from .assembler import assemble_semantic
from .references import extract_references

__all__ = ["assemble_semantic", "extract_references"]
