import base64

import pytest

import protocol as p


def _valid_initialize():
    return p.dumps({"v": 1, "type": "initialize", "sessionId": "s1", "seq": 1, "language": "ja"})


def test_parse_valid_initialize():
    msg = p.parse_line(_valid_initialize())
    assert msg["type"] == "initialize"
    assert msg["sessionId"] == "s1"


def test_parse_invalid_json():
    with pytest.raises(p.ProtocolError) as e:
        p.parse_line("{not json")
    assert e.value.code == "invalid_json"


def test_parse_version_mismatch():
    line = p.dumps({"v": 99, "type": "flush", "sessionId": "s", "seq": 1})
    with pytest.raises(p.ProtocolError) as e:
        p.parse_line(line)
    assert e.value.code == "version_mismatch"


def test_parse_unknown_type():
    line = p.dumps({"v": 1, "type": "nope", "sessionId": "s", "seq": 1})
    with pytest.raises(p.ProtocolError) as e:
        p.parse_line(line)
    assert e.value.code == "unknown_type"


@pytest.mark.parametrize("field", ["sessionId", "seq"])
def test_parse_missing_required_field(field):
    body = {"v": 1, "type": "flush", "sessionId": "s", "seq": 1}
    del body[field]
    with pytest.raises(p.ProtocolError) as e:
        p.parse_line(p.dumps(body))
    assert e.value.code == "missing_field"


def test_decode_audio_valid():
    pcm = (1234).to_bytes(2, "little") * 8  # 16 bytes, 8 int16 samples
    msg = {"type": "audio", "pcm": base64.b64encode(pcm).decode(), "frames": 8}
    assert p.decode_audio(msg) == pcm


def test_decode_audio_invalid_base64():
    msg = {"type": "audio", "pcm": "!!!not-base64!!!"}
    with pytest.raises(p.ProtocolError) as e:
        p.decode_audio(msg)
    assert e.value.code == "invalid_base64"


def test_decode_audio_odd_length():
    msg = {"type": "audio", "pcm": base64.b64encode(b"\x01\x02\x03").decode()}
    with pytest.raises(p.ProtocolError) as e:
        p.decode_audio(msg)
    assert e.value.code == "invalid_pcm"


def test_decode_audio_too_large():
    big = b"\x00\x00" * (p.MAX_AUDIO_BYTES // 2 + 1)
    msg = {"type": "audio", "pcm": base64.b64encode(big).decode()}
    with pytest.raises(p.ProtocolError) as e:
        p.decode_audio(msg)
    assert e.value.code == "message_too_large"


def test_decode_audio_frames_mismatch():
    pcm = b"\x00\x00" * 4
    msg = {"type": "audio", "pcm": base64.b64encode(pcm).decode(), "frames": 99}
    with pytest.raises(p.ProtocolError) as e:
        p.decode_audio(msg)
    assert e.value.code == "invalid_pcm"
