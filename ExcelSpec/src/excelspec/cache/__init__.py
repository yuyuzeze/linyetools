"""Optional content-addressed local cache for the zero-config pipeline."""

from .keys import (
    DETECTOR_VERSION,
    PARSER_VERSION,
    SPARSE_SCHEMA_VERSION,
    content_sha,
    document_cache_key,
    sha256_file,
)
from .store import FileCache

__all__ = [
    "DETECTOR_VERSION",
    "FileCache",
    "PARSER_VERSION",
    "SPARSE_SCHEMA_VERSION",
    "content_sha",
    "document_cache_key",
    "sha256_file",
]
