"""Pluggable OCR / VLM provider interfaces (Null implementations only).

No online SDKs, models, or network calls are imported here. The defaults are
:class:`NullOcrProvider` / :class:`NullVlmProvider` (``available = False``), so
the ``fast`` pipeline never touches them, and ``auto`` only invokes a provider
when ``provider.available`` is true. A provider failure is recorded as a
diagnostic and never discards the underlying visual asset.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Protocol, runtime_checkable


@dataclass(slots=True)
class ProviderContext:
    sheet: str | None = None
    region_id: str | None = None
    title: str | None = None
    source_range: str | None = None


@dataclass(slots=True)
class ProviderResult:
    text: str
    provider: str
    source: str  # "ocr" | "vlm"
    confidence: float
    metadata: dict[str, Any] = field(default_factory=dict)


@runtime_checkable
class OcrProvider(Protocol):
    available: bool

    def extract(self, asset: Any, context: ProviderContext) -> ProviderResult: ...


@runtime_checkable
class VlmProvider(Protocol):
    available: bool

    def describe(self, asset: Any, context: ProviderContext) -> ProviderResult: ...


class NullOcrProvider:
    """Default OCR provider — never available, never called by fast mode."""

    available = False

    def extract(self, asset: Any, context: ProviderContext) -> ProviderResult:
        raise RuntimeError("NullOcrProvider is not available")


class NullVlmProvider:
    """Default VLM provider — never available, never called by fast mode."""

    available = False

    def describe(self, asset: Any, context: ProviderContext) -> ProviderResult:
        raise RuntimeError("NullVlmProvider is not available")


__all__ = [
    "NullOcrProvider",
    "NullVlmProvider",
    "OcrProvider",
    "ProviderContext",
    "ProviderResult",
    "VlmProvider",
]
