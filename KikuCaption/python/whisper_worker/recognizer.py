"""faster-whisper model wrapper.

The model is created once (in the worker's `initialize` handler) and reused for the whole
session — never reloaded per audio chunk (PROJECT.md 5.4).
"""
from __future__ import annotations

from typing import Any, Iterable, Optional


class Recognizer:
    def __init__(
        self,
        model: str,
        device: str,
        compute_type: str,
        beam_size: int,
        download_root: Optional[str] = None,
    ) -> None:
        # Imported lazily so protocol-only usage doesn't require the ML stack.
        from faster_whisper import WhisperModel

        self.beam_size = int(beam_size)
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
            vad_filter=False,
        )
        return segments
