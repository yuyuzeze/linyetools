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
