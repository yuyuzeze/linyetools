"""faster-whisper model wrapper.

The model is created once (in the worker's `initialize` handler) and reused for the whole
session — never reloaded per audio chunk (PROJECT.md 5.4). Optional decoding context
(`initial_prompt`) and a technical-term glossary (`hotwords`) bias recognition toward
domain vocabulary; both are real faster-whisper 1.2.x `transcribe()` parameters.
"""
from __future__ import annotations

from typing import Any, Iterable, List, Optional, Sequence

# Defensive caps mirroring the C# Hotwords validator (count / term length / total chars).
_MAX_HOTWORDS = 64
_MAX_TERM_LEN = 40
_MAX_TOTAL_CHARS = 1000


def _clean_hotwords(hotwords: Optional[Sequence[str]]) -> Optional[str]:
    """Normalize a hotword list into the single space-separated string faster-whisper expects."""
    if not hotwords:
        return None

    seen: set[str] = set()
    kept: List[str] = []
    total = 0
    for raw in hotwords:
        if not isinstance(raw, str):
            continue
        term = raw.strip()
        if not term or term in seen or len(term) > _MAX_TERM_LEN:
            continue
        seen.add(term)
        kept.append(term)
        total += len(term)
        if len(kept) >= _MAX_HOTWORDS or total >= _MAX_TOTAL_CHARS:
            break

    return " ".join(kept) if kept else None


class Recognizer:
    def __init__(
        self,
        model: str,
        device: str,
        compute_type: str,
        beam_size: int,
        download_root: Optional[str] = None,
        initial_prompt: Optional[str] = None,
        hotwords: Optional[Sequence[str]] = None,
    ) -> None:
        # Imported lazily so protocol-only usage doesn't require the ML stack.
        from faster_whisper import WhisperModel

        self.beam_size = int(beam_size)
        self.initial_prompt = initial_prompt.strip() if isinstance(initial_prompt, str) and initial_prompt.strip() else None
        self.hotwords = _clean_hotwords(hotwords)
        self.model = WhisperModel(
            model,
            device=device,
            compute_type=compute_type,
            download_root=download_root,
        )

    def transcribe(self, audio_f32: Any, language: str) -> Iterable[Any]:
        """Return the segment generator for a float32 (16 kHz mono) audio buffer."""
        segments, _info = self.model.transcribe(
            audio_f32,
            language=language,
            beam_size=self.beam_size,
            initial_prompt=self.initial_prompt,
            hotwords=self.hotwords,
            vad_filter=False,
        )
        return segments
