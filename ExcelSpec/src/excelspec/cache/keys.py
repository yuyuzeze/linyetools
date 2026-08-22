"""Cache-key construction and component versions.

Bumping any of these versions invalidates every cached entry that depends on it.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

from ..models.semantic import SEMANTIC_SCHEMA_VERSION

PARSER_VERSION = "1"
SPARSE_SCHEMA_VERSION = "1"
DETECTOR_VERSION = "1"


def sha256_file(path: str | Path) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as handle:
        for block in iter(lambda: handle.read(65536), b""):
            digest.update(block)
    return digest.hexdigest()


def content_sha(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def document_cache_key(
    *,
    workbook_hash: str,
    mode: str,
    profile_hash: str | None,
    asset_dir: str | None,
    screenshot_manifest_hash: str | None = None,
) -> str:
    """Key for a cached zero-config DocumentIR.

    Includes the workbook content hash, every schema/component version, the
    profile content hash, processing mode, and the asset directory (which the
    document's URIs/metadata embed). Chunk parameters are deliberately excluded
    — they never affect the DocumentIR, so changing them keeps this hit and only
    re-derives chunks.
    """

    payload = json.dumps(
        {
            "workbook": workbook_hash,
            "parser": PARSER_VERSION,
            "sparse": SPARSE_SCHEMA_VERSION,
            "detector": DETECTOR_VERSION,
            "semantic": SEMANTIC_SCHEMA_VERSION,
            "profile": profile_hash,
            "mode": mode,
            "asset_dir": asset_dir,
            "manifest": screenshot_manifest_hash,
        },
        sort_keys=True,
    )
    return content_sha(payload)[:40]


__all__ = [
    "DETECTOR_VERSION",
    "PARSER_VERSION",
    "SPARSE_SCHEMA_VERSION",
    "content_sha",
    "document_cache_key",
    "sha256_file",
]
