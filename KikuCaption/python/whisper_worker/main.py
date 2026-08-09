"""KikuCaption resident Whisper worker (Milestone 2).

Reads JSON Lines from stdin, writes JSON Lines protocol messages to stdout, and writes all
diagnostics to stderr. The faster-whisper model is loaded once on `initialize` and reused for
the whole session.
"""
from __future__ import annotations

import argparse
import sys
import time
from typing import Any, Optional

import protocol as p


def log(message: str) -> None:
    """Diagnostics go to stderr only — never pollute the stdout protocol stream."""
    print(message, file=sys.stderr, flush=True)


def send(obj: dict[str, Any]) -> None:
    sys.stdout.write(p.dumps(obj) + "\n")
    sys.stdout.flush()


def send_error(session_id: str, seq: int, code: str, message: str) -> None:
    send({
        "v": p.PROTOCOL_VERSION,
        "type": p.T_ERROR,
        "sessionId": session_id,
        "seq": seq,
        "code": code,
        "message": message,
    })


def main() -> int:
    parser = argparse.ArgumentParser(description="KikuCaption Whisper worker")
    parser.add_argument("--download-root", default=None,
                        help="Model cache directory (overridden by initialize.modelCacheDir).")
    args, _ = parser.parse_known_args()

    recognizer: Optional[Any] = None
    buffer: Optional[Any] = None
    session_id = ""
    out_seq = 0

    def next_seq() -> int:
        nonlocal out_seq
        out_seq += 1
        return out_seq

    log(f"worker started (protocol v{p.PROTOCOL_VERSION})")

    for raw in sys.stdin:
        line = raw.strip()
        if not line:
            continue

        try:
            msg = p.parse_line(line)
        except p.ProtocolError as exc:
            send_error(session_id, 0, exc.code, exc.message)
            continue

        message_type = msg["type"]
        sid = msg["sessionId"]
        seq = msg["seq"]

        try:
            if message_type == p.T_INITIALIZE:
                if recognizer is not None:
                    send_error(sid, seq, "already_initialized", "Worker 已初始化。")
                    continue

                language = msg.get("language")
                if language not in ("ja", "zh"):
                    send_error(sid, seq, "invalid_language", "language 必须为 ja 或 zh。")
                    continue

                model = msg.get("model", "small")
                device = msg.get("device", "cpu")
                compute_type = msg.get("computeType", "int8")
                beam_size = int(msg.get("beamSize", 1))
                download_root = msg.get("modelCacheDir") or args.download_root

                from recognizer import Recognizer
                from streaming import AudioBuffer

                start = time.time()
                recognizer = Recognizer(model, device, compute_type, beam_size, download_root)
                buffer = AudioBuffer(recognizer, language)
                load_ms = (time.time() - start) * 1000.0
                session_id = sid
                log(f"model '{model}' loaded in {load_ms:.0f} ms (device={device}, compute={compute_type})")

                send({
                    "v": p.PROTOCOL_VERSION, "type": p.T_READY, "sessionId": sid, "seq": next_seq(),
                    "modelLoadMs": round(load_ms, 1), "model": model, "device": device,
                    "computeType": compute_type,
                })

            elif message_type == p.T_AUDIO:
                if buffer is None:
                    send_error(sid, seq, "not_initialized", "未初始化。")
                    continue
                if sid != session_id:
                    send_error(sid, seq, "session_mismatch", "sessionId 不匹配。")
                    continue
                buffer.append(p.decode_audio(msg))

            elif message_type == p.T_FLUSH:
                if buffer is None:
                    send_error(sid, seq, "not_initialized", "未初始化。")
                    continue

                final_count = 0
                for kind, seg in buffer.flush():
                    if kind == "partial":
                        send({
                            "v": p.PROTOCOL_VERSION, "type": p.T_PARTIAL, "sessionId": sid,
                            "seq": next_seq(), "start": seg["start"], "end": seg["end"],
                            "text": seg["text"],
                        })
                    else:
                        final_count += 1
                        send({
                            "v": p.PROTOCOL_VERSION, "type": p.T_FINAL_CANDIDATE, "sessionId": sid,
                            "seq": next_seq(), "start": seg["start"], "end": seg["end"],
                            "text": seg["text"], "confidence": seg["confidence"],
                        })

                send({
                    "v": p.PROTOCOL_VERSION, "type": p.T_FLUSHED, "sessionId": sid,
                    "seq": next_seq(), "count": final_count,
                })

            elif message_type == p.T_SHUTDOWN:
                log("shutdown requested")
                break

        except p.ProtocolError as exc:
            send_error(sid, seq, exc.code, exc.message)
        except Exception as exc:  # never leak a traceback to stdout
            log(f"unhandled error: {exc!r}")
            send_error(sid, seq, "internal_error", str(exc))

    log("worker exiting")
    return 0


if __name__ == "__main__":
    sys.exit(main())
