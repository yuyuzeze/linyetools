"""Tests for the recognizer's initial_prompt / hotwords handling (Japanese quality optimization).

`_clean_hotwords` is pure and imports without the ML stack. The transcribe-passthrough test
monkeypatches faster_whisper.WhisperModel so it never loads a real model.
"""
from __future__ import annotations

import recognizer as r


def test_clean_hotwords_none_and_empty():
    assert r._clean_hotwords(None) is None
    assert r._clean_hotwords([]) is None
    assert r._clean_hotwords(["  ", ""]) is None


def test_clean_hotwords_trims_dedups_and_joins():
    assert r._clean_hotwords([" API ", "API", "Azure"]) == "API Azure"


def test_clean_hotwords_caps_term_length():
    long_term = "x" * (r._MAX_TERM_LEN + 5)
    assert r._clean_hotwords([long_term, "API"]) == "API"  # over-long term dropped


def test_recognizer_passes_prompt_and_hotwords_to_transcribe(monkeypatch):
    import faster_whisper

    captured: dict = {}

    class FakeModel:
        def __init__(self, *args, **kwargs):
            pass

        def transcribe(self, audio, **kwargs):
            captured.update(kwargs)
            return ([], None)

    monkeypatch.setattr(faster_whisper, "WhisperModel", FakeModel)

    rec = r.Recognizer(
        "small", "cpu", "int8", beam_size=2,
        initial_prompt="技術会議", hotwords=["Azure", "OpenAI", "Azure"],
    )
    list(rec.transcribe([0.0, 0.0], "ja"))

    assert captured["beam_size"] == 2
    assert captured["initial_prompt"] == "技術会議"
    assert captured["hotwords"] == "Azure OpenAI"   # deduped + joined
    assert captured["language"] == "ja"


def test_recognizer_no_hotwords_is_none(monkeypatch):
    import faster_whisper

    captured: dict = {}

    class FakeModel:
        def __init__(self, *args, **kwargs):
            pass

        def transcribe(self, audio, **kwargs):
            captured.update(kwargs)
            return ([], None)

    monkeypatch.setattr(faster_whisper, "WhisperModel", FakeModel)

    rec = r.Recognizer("small", "cpu", "int8", beam_size=1)
    list(rec.transcribe([0.0], "ja"))

    assert captured["hotwords"] is None
    assert captured["initial_prompt"] is None
