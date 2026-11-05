"""faster-whisper model wrapper.

The model is created once (in the worker's `initialize` handler) and reused for the whole
session — never reloaded per audio chunk (PROJECT.md 5.4). Optional decoding context
(`initial_prompt`) and a technical-term glossary (`hotwords`) bias recognition toward
domain vocabulary; both are real faster-whisper 1.2.x `transcribe()` parameters.
"""
from __future__ import annotations

import sys
from typing import Any, Iterable, List, Optional, Sequence

# Defensive caps mirroring the C# Hotwords validator (count / term length / total chars).
_MAX_HOTWORDS = 64
_MAX_TERM_LEN = 40
_MAX_TOTAL_CHARS = 1000

# Whisper decodes within a fixed 448-token window that INCLUDES the prompt context
# (sot_prev + hotwords tokens + previous/initial-prompt tokens + the start-of-transcript
# sequence). faster-whisper caps the hotwords and the previous-prompt independently at
# max_length // 2 - 1 (= 223) each, so a long initial_prompt AND a long hotwords list can
# together push the prompt to ~451 tokens. ctranslate2 then gets a decode budget of
# 448 - prompt_len <= 0 and raises "The maximum decoding length must be > 0", which tears
# down the whole real-time caption stream.
#
# We clamp each context source well below 223 so the prompt can never eat the budget,
# leaving a generous decode headroom regardless of what the active dictionary contains.
_WHISPER_MAX_LENGTH = 448
_MAX_INITIAL_PROMPT_TOKENS = 140
_MAX_HOTWORDS_TOKENS = 140


def _clean_hotword_terms(hotwords: Optional[Sequence[str]]) -> List[str]:
    """Normalize a hotword list: trim, dedupe, and apply the count/length/total-char caps."""
    if not hotwords:
        return []

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

    return kept


def _clean_hotwords(hotwords: Optional[Sequence[str]]) -> Optional[str]:
    """Normalize a hotword list into the single space-separated string faster-whisper expects."""
    terms = _clean_hotword_terms(hotwords)
    return " ".join(terms) if terms else None


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
        self.initial_prompt = (
            initial_prompt.strip()
            if isinstance(initial_prompt, str) and initial_prompt.strip()
            else None
        )
        hotword_terms = _clean_hotword_terms(hotwords)
        self.model = WhisperModel(
            model,
            device=device,
            compute_type=compute_type,
            download_root=download_root,
        )

        # Clamp the prompt context to a safe token budget now that the tokenizer is loaded,
        # so a decode can never be asked for with a non-positive length (see module docstring).
        self.initial_prompt, self.hotwords = self._fit_prompt_budget(
            self.initial_prompt, hotword_terms
        )

    # --- prompt budgeting -------------------------------------------------------------

    def _token_count(self, text: Optional[str]) -> int:
        """Token count of a prompt fragment as faster-whisper's get_prompt() would see it."""
        if not text:
            return 0
        tok = getattr(self.model, "hf_tokenizer", None)
        if tok is None:
            return 0
        try:
            return len(tok.encode(" " + text.strip()).ids)
        except Exception:
            return 0

    def _fit_prompt_budget(
        self, initial_prompt: Optional[str], hotword_terms: List[str]
    ) -> tuple[Optional[str], Optional[str]]:
        """Truncate initial_prompt and drop trailing hotwords so neither context source
        exceeds its safe token cap. Returns (initial_prompt, joined_hotwords_or_None)."""
        tok = getattr(self.model, "hf_tokenizer", None)

        # Hotwords: keep whole terms, dropping from the end until under the cap.
        hotwords_str: Optional[str] = " ".join(hotword_terms) if hotword_terms else None
        if hotwords_str and tok is not None:
            terms = list(hotword_terms)
            while terms and self._token_count(" ".join(terms)) > _MAX_HOTWORDS_TOKENS:
                dropped = terms.pop()
                print(
                    f"hotword dropped to fit token budget (term_len={len(dropped)}, "
                    f"remaining={len(terms)})",
                    file=sys.stderr,
                    flush=True,
                )
            hotwords_str = " ".join(terms) if terms else None

        # Initial prompt: truncate by characters until under the cap (it is only a hint).
        prompt = initial_prompt
        if prompt and tok is not None and self._token_count(prompt) > _MAX_INITIAL_PROMPT_TOKENS:
            original_chars = len(prompt)
            while prompt and self._token_count(prompt) > _MAX_INITIAL_PROMPT_TOKENS:
                # Drop ~10% of the remaining characters each step; converges quickly.
                prompt = prompt[: max(1, int(len(prompt) * 0.9) - 1)]
            prompt = prompt.strip() or None
            print(
                f"initial_prompt truncated to fit token budget "
                f"(chars {original_chars} -> {len(prompt or '')})",
                file=sys.stderr,
                flush=True,
            )

        return prompt, hotwords_str

    # --- transcription ----------------------------------------------------------------

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
