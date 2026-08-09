"""JSON Lines protocol for the KikuCaption Whisper worker.

Contract (PROJECT.md 8.3, 13):
  * Every message is one line of JSON on stdout/stdin.
  * Only protocol messages go to stdout; all diagnostics go to stderr.
  * Untrusted input: validate version, required fields, Base64 and PCM length,
    and cap the audio message size. Invalid input produces a structured `error`
    message, never a raw traceback on stdout.
"""
from __future__ import annotations

import base64
import json
from typing import Any

PROTOCOL_VERSION = 1

# Max PCM bytes per audio message: 10 s of 16 kHz mono int16.
MAX_AUDIO_BYTES = 16000 * 2 * 10

# Incoming (C# -> worker)
T_INITIALIZE = "initialize"
T_AUDIO = "audio"
T_FLUSH = "flush"
T_SHUTDOWN = "shutdown"

# Outgoing (worker -> C#)
T_READY = "ready"
T_PARTIAL = "partial"
T_FINAL_CANDIDATE = "final_candidate"
T_FLUSHED = "flushed"
T_ERROR = "error"

INCOMING_TYPES = frozenset({T_INITIALIZE, T_AUDIO, T_FLUSH, T_SHUTDOWN})


class ProtocolError(Exception):
    """A validation error that must be reported as a structured `error` message."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


def parse_line(line: str) -> dict[str, Any]:
    """Parse and validate the envelope of one incoming message."""
    try:
        msg = json.loads(line)
    except json.JSONDecodeError as exc:
        raise ProtocolError("invalid_json", f"无法解析 JSON: {exc}") from exc

    if not isinstance(msg, dict):
        raise ProtocolError("invalid_message", "消息必须是 JSON 对象。")

    if msg.get("v") != PROTOCOL_VERSION:
        raise ProtocolError("version_mismatch", f"协议版本不匹配: {msg.get('v')}")

    if msg.get("type") not in INCOMING_TYPES:
        raise ProtocolError("unknown_type", f"未知消息类型: {msg.get('type')}")

    session_id = msg.get("sessionId")
    if not isinstance(session_id, str) or not session_id:
        raise ProtocolError("missing_field", "缺少 sessionId。")

    if not isinstance(msg.get("seq"), int):
        raise ProtocolError("missing_field", "缺少 seq。")

    return msg


def decode_audio(msg: dict[str, Any]) -> bytes:
    """Validate and decode the PCM payload of an `audio` message."""
    pcm_b64 = msg.get("pcm")
    if not isinstance(pcm_b64, str) or not pcm_b64:
        raise ProtocolError("missing_field", "audio 缺少 pcm。")

    try:
        pcm = base64.b64decode(pcm_b64, validate=True)
    except (ValueError, base64.binascii.Error) as exc:  # type: ignore[attr-defined]
        raise ProtocolError("invalid_base64", f"Base64 解码失败: {exc}") from exc

    if len(pcm) == 0:
        raise ProtocolError("invalid_pcm", "PCM 为空。")
    if len(pcm) % 2 != 0:
        raise ProtocolError("invalid_pcm", "PCM 长度不是 int16 的整数倍。")
    if len(pcm) > MAX_AUDIO_BYTES:
        raise ProtocolError("message_too_large", f"音频消息过大: {len(pcm)} > {MAX_AUDIO_BYTES}")

    frames = msg.get("frames")
    if isinstance(frames, int) and frames != len(pcm) // 2:
        raise ProtocolError("invalid_pcm", "frames 与 PCM 长度不一致。")

    return pcm


def dumps(obj: dict[str, Any]) -> str:
    """Serialize one outgoing message to a compact single-line JSON string."""
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":"))
