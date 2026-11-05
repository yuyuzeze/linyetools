import streaming


class FakeSegment:
    def __init__(self, start, end, text, avg_logprob=-0.2):
        self.start = start
        self.end = end
        self.text = text
        self.avg_logprob = avg_logprob


class FakeRecognizer:
    def __init__(self, segments):
        self._segments = segments
        self.calls = 0

    def transcribe(self, audio_f32, language):
        self.calls += 1
        self.last_language = language
        return list(self._segments)


def _pcm(n):
    # n int16 samples of a simple ramp.
    return b"".join((i % 100).to_bytes(2, "little", signed=True) for i in range(n))


def test_flush_emits_partial_then_final():
    rec = FakeRecognizer([FakeSegment(0.0, 1.0, " hello "), FakeSegment(1.0, 2.0, " world ")])
    buf = streaming.AudioBuffer(rec, "ja")
    buf.append(_pcm(1600))

    events = list(buf.flush())
    kinds = [k for k, _ in events]

    assert kinds == ["partial", "partial", "final", "final"]
    finals = [seg for k, seg in events if k == "final"]
    assert finals[0]["text"] == "hello"
    assert finals[0]["start"] == 0.0 and finals[0]["end"] == 1.0
    assert finals[0]["confidence"] is not None
    assert rec.last_language == "ja"


def test_flush_empty_buffer_yields_nothing():
    rec = FakeRecognizer([FakeSegment(0, 1, "x")])
    buf = streaming.AudioBuffer(rec, "zh")
    assert list(buf.flush()) == []
    assert rec.calls == 0


def test_buffer_clears_after_flush():
    rec = FakeRecognizer([])
    buf = streaming.AudioBuffer(rec, "ja")
    buf.append(_pcm(800))
    assert buf.buffered_bytes == 1600
    list(buf.flush())
    assert buf.buffered_bytes == 0


class _BoomRecognizer:
    """A recognizer whose decode fails the way ctranslate2 does when the prompt fills the window."""

    def transcribe(self, audio_f32, language):
        raise RuntimeError("The maximum decoding length must be > 0")


def test_flush_swallows_decode_error_instead_of_crashing_stream():
    # A failed decode cycle must NOT propagate (which would surface as "实时字幕中断");
    # the next cycle re-transcribes from the C#-side authoritative buffer.
    buf = streaming.AudioBuffer(_BoomRecognizer(), "ja")
    buf.append(_pcm(1600))
    assert list(buf.flush()) == []  # no events, no exception
    # Buffer was still consumed, so the next flush on silence is a clean no-op.
    assert buf.buffered_bytes == 0


class _PartialThenBoomRecognizer:
    """Decodes one segment, then fails — the decoded segment must still be committed."""

    def transcribe(self, audio_f32, language):
        yield FakeSegment(0.0, 1.0, " kept ")
        raise RuntimeError("The maximum decoding length must be > 0")


def test_flush_keeps_already_decoded_segments_on_mid_cycle_error():
    buf = streaming.AudioBuffer(_PartialThenBoomRecognizer(), "ja")
    buf.append(_pcm(1600))
    events = list(buf.flush())
    kinds = [k for k, _ in events]
    # The one decoded segment is emitted as partial AND final; the error after it is swallowed.
    assert kinds == ["partial", "final"]
    assert events[-1][1]["text"] == "kept"
