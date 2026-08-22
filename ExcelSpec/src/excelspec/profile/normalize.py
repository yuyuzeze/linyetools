"""Header / label normalization for tolerant, deterministic alias matching."""

from __future__ import annotations

import unicodedata

# Japanese punctuation folded to a canonical form so aliases match regardless
# of full/half width or stylistic variants.
_PUNCT_MAP = {
    "：": ":",
    "・": "",
    "／": "/",
    "（": "(",
    "）": ")",
    "　": " ",  # ideographic space
    "―": "-",
    "－": "-",
    "ー": "ー",
}


def normalize_header(value: object) -> str:
    """Normalize a header/label for alias comparison.

    Applies NFKC, trims, collapses whitespace, folds Japanese punctuation, and
    casefolds. Deterministic and lossless-of-meaning for exact-alias matching.
    """

    if value is None:
        return ""
    text = unicodedata.normalize("NFKC", str(value))
    for source, target in _PUNCT_MAP.items():
        text = text.replace(source, target)
    text = " ".join(text.split())
    return text.casefold()


__all__ = ["normalize_header"]
