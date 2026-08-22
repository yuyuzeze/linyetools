"""Deterministic chunking of a SemanticDocumentIR into KnowledgeChunkIR."""

from .chunker import ChunkingOptions, KnowledgeChunker, chunk_document

__all__ = ["ChunkingOptions", "KnowledgeChunker", "chunk_document"]
