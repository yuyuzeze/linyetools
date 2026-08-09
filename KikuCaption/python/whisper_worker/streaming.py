"""Audio buffering and transcription orchestration for Milestone 2.

This is intentionally minimal: it accumulates PCM and, on flush, transcribes the buffer and
yields ("partial", seg) as segments stream out, then ("final", seg) for each. The sliding
window / Stable Prefix / Finalizer algorithm is Milestone 3 and is NOT implemented here.
"""
from __future__ import annotations

import math
from typing import Any, Iterator, Tuple

import numpy as np


def _confidence(segment: Any) -> float | None:
    avg_logprob = getattr(segment, "avg_logprob", None)
    if avg_logprob is None:
        return None
    try:
        return round(min(1.0, max(0.0, math.exp(float(avg_logprob)))), 4)
    except (OverflowError, ValueError):
        return None


class AudioBuffer:
    def __init__(self, recognizer: Any, language: str) -> None:
        self._recognizer = recognizer
        self._language = language
        self._buffer = bytearray()

    def append(self, pcm: bytes) -> None:
        self._buffer.extend(pcm)

    @property
    def buffered_bytes(self) -> int:
        return len(self._buffer)

    def _take_float32(self) -> np.ndarray:
        samples = np.frombuffer(bytes(self._buffer), dtype="<i2").astype(np.float32) / 32768.0
        self._buffer.clear()
        return samples

    def flush(self) -> Iterator[Tuple[str, dict[str, Any]]]:
        audio = self._take_float32()
        if audio.size == 0:
            return

        finals: list[dict[str, Any]] = []
        for segment in self._recognizer.transcribe(audio, self._language):
            payload = {
                "start": round(float(segment.start), 3),
                "end": round(float(segment.end), 3),
                "text": segment.text.strip(),
                "confidence": _confidence(segment),
            }
            yield ("partial", payload)
            finals.append(payload)

        for payload in finals:
            yield ("final", payload)
