#!/usr/bin/env python3
"""Periodically exercise the OMNI multimodal API without touching the app.

By default this sends one realistic Cut Once desk-inspection request every 15
minutes.  Each request contains text, the repo's synthetic desk image, and a
generated WAV diagnostic signal.  Results are emitted as one JSON object per
line, which makes the script suitable for a terminal, systemd, or a log shipper.

Configuration is read from the process environment, then from .env.local:
OMNI_API_KEY, OMNI_BASE_URL, OMNI_MODEL, and OMNI_AUDIO.  No third-party Python
packages are required.

Examples:
  python scripts/omni_soak.py
  python scripts/omni_soak.py --once
  python scripts/omni_soak.py --audio captured-question.wav --log omni-soak.jsonl
"""

from __future__ import annotations

import argparse
import base64
import io
import json
import math
import os
from pathlib import Path
import signal
import struct
import sys
import time
from typing import Any, Iterable
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
import wave


REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PHOTO = REPO_ROOT / "data" / "fixtures" / "frame_0001.jpg"
DEFAULT_INTERVAL_SECONDS = 15 * 60


def load_env_file(path: Path) -> None:
    """Load the simple KEY=value form used by this repo, without overriding env."""
    if not path.is_file():
        return
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key, value = key.strip(), value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        if key:
            os.environ.setdefault(key, value)


def diagnostic_wav(seconds: float = 1.2, rate: int = 16_000) -> bytes:
    """Make a quiet, valid mono WAV with two tones and silence (never speech)."""
    frames = bytearray()
    for index in range(round(seconds * rate)):
        t = index / rate
        # Alternating tones with quiet gaps exercise audio transport predictably.
        phase = t % 0.4
        hz = 440 if int(t / 0.4) % 2 == 0 else 660
        sample = 0 if phase >= 0.28 else round(4500 * math.sin(2 * math.pi * hz * t))
        frames.extend(struct.pack("<h", sample))
    output = io.BytesIO()
    with wave.open(output, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.writeframes(frames)
    return output.getvalue()


def data_url(mime: str, data: bytes) -> str:
    return f"data:{mime};base64,{base64.b64encode(data).decode('ascii')}"


def make_payload(
    *, model: str, sequence: int, photo: bytes, audio: bytes,
    audio_encoding: str, supplied_audio: bool,
) -> dict[str, Any]:
    captured_at = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    audio_data = base64.b64encode(audio).decode("ascii")
    if audio_encoding == "dataurl":
        audio_data = f"data:;base64,{audio_data}"
    state = {
        "probe_sequence": sequence,
        "captured_at": captured_at,
        "site": "mock workshop bay A",
        "task": "inspect a partially assembled work desk before the next step",
        "measured_parts": [
            {"id": "top", "kind": "wood panel", "size_cm": [90, 45, 3]},
            {"id": "leg_left", "kind": "steel leg", "height_cm": 42},
            {"id": "leg_right", "kind": "steel leg", "height_cm": 42},
            {"id": "brace", "kind": "steel cross brace", "length_cm": 42},
        ],
        "operator_note": "Check whether the visible assembly looks plausible and name one safe next check.",
        "audio_fixture": "captured operator speech" if supplied_audio else "generated diagnostic tones; no speech expected",
    }
    system = (
        "You are a reliability probe for Cut Once, a mixed-reality construction copilot. "
        "Use the text, image, and audio together. Reply with exactly one compact JSON object "
        "with these fields: status ('ok' or 'warning'), summary (string), visible_objects "
        "(array of strings), audio_observation (string), and next_action (string). "
        "Do not use Markdown or invent a dangerous instruction."
    )
    return {
        "model": model,
        "stream": True,
        "modalities": ["text"],
        "messages": [
            {"role": "system", "content": system},
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": "Synthetic reliability test input:\n" + json.dumps(state, separators=(",", ":"))},
                    {"type": "image_url", "image_url": {"url": data_url("image/jpeg", photo)}},
                    {"type": "input_audio", "input_audio": {"data": audio_data, "format": "wav"}},
                ],
            },
        ],
    }


def streamed_text(lines: Iterable[bytes], request_started: float | None = None) -> tuple[str, int | None]:
    """Read OpenAI-compatible server-sent events and return text + first-token ms."""
    started = request_started if request_started is not None else time.monotonic()
    first_token_ms: int | None = None
    chunks: list[str] = []
    for raw in lines:
        line = raw.decode("utf-8", errors="replace").strip()
        if not line.startswith("data:"):
            continue
        data = line[5:].strip()
        if data == "[DONE]":
            break
        try:
            event = json.loads(data)
            content = event.get("choices", [{}])[0].get("delta", {}).get("content")
        except (json.JSONDecodeError, IndexError, AttributeError, TypeError):
            continue
        if isinstance(content, str) and content:
            if first_token_ms is None:
                first_token_ms = round((time.monotonic() - started) * 1000)
            chunks.append(content)
    return "".join(chunks), first_token_ms


def json_objects(text: str) -> list[Any]:
    """Find JSON objects in a reply; the final valid object is normally the answer."""
    decoder = json.JSONDecoder()
    found: list[Any] = []
    for index, char in enumerate(text):
        if char != "{":
            continue
        try:
            value, _ = decoder.raw_decode(text[index:])
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict):
            found.append(value)
    return found


def validate_answer(text: str) -> dict[str, Any]:
    required = {"status", "summary", "visible_objects", "audio_observation", "next_action"}
    for candidate in reversed(json_objects(text)):
        if required <= candidate.keys() and candidate.get("status") in {"ok", "warning"}:
            if isinstance(candidate.get("visible_objects"), list):
                return candidate
    raise ValueError("OMNI returned text, but not the requested JSON object")


def call_omni(
    *, base_url: str, api_key: str, payload: dict[str, Any], timeout_seconds: float,
) -> tuple[dict[str, Any], int | None]:
    url = base_url.rstrip("/") + "/chat/completions"
    request = Request(
        url,
        data=json.dumps(payload, separators=(",", ":")).encode("utf-8"),
        headers={"Authorization": f"Bearer {api_key}", "Content-Type": "application/json"},
        method="POST",
    )
    request_started = time.monotonic()
    try:
        with urlopen(request, timeout=timeout_seconds) as response:
            text, first_token_ms = streamed_text(response, request_started)
    except HTTPError as error:
        detail = error.read(1024).decode("utf-8", errors="replace").strip()
        raise RuntimeError(f"HTTP {error.code}: {detail or error.reason}") from error
    except (URLError, TimeoutError, OSError) as error:
        raise RuntimeError(f"request failed: {error}") from error
    if not text.strip():
        raise ValueError("OMNI stream completed without any text")
    return validate_answer(text), first_token_ms


def emit(record: dict[str, Any], log_path: Path | None) -> None:
    line = json.dumps(record, ensure_ascii=False, separators=(",", ":"))
    print(line, flush=True)
    if log_path:
        log_path.parent.mkdir(parents=True, exist_ok=True)
        with log_path.open("a", encoding="utf-8") as output:
            output.write(line + "\n")


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run a bounded OMNI multimodal request every 15 minutes.")
    parser.add_argument("--once", action="store_true", help="send one request and exit")
    parser.add_argument("--interval-seconds", type=float, default=DEFAULT_INTERVAL_SECONDS)
    parser.add_argument("--timeout-seconds", type=float, default=60.0)
    parser.add_argument("--photo", type=Path, default=DEFAULT_PHOTO)
    parser.add_argument("--audio", type=Path, help="optional mono WAV with realistic operator speech")
    parser.add_argument("--log", type=Path, help="also append JSON-line results to this file")
    parser.add_argument("--base-url", default=os.environ.get("OMNI_BASE_URL", ""))
    parser.add_argument("--api-key", default=os.environ.get("OMNI_API_KEY", ""), help=argparse.SUPPRESS)
    parser.add_argument("--model", default=os.environ.get("OMNI_MODEL", "qwen3.5-omni-flash"))
    parser.add_argument(
        "--audio-encoding", choices=("dataurl", "base64"),
        default="base64" if os.environ.get("OMNI_AUDIO") == "base64" else "dataurl",
    )
    args = parser.parse_args(argv)
    if args.interval_seconds <= 0 or args.timeout_seconds <= 0:
        parser.error("interval and timeout must be positive")
    if not args.api_key or not args.base_url:
        parser.error("set OMNI_API_KEY and OMNI_BASE_URL in the environment or .env.local")
    if not args.photo.is_file():
        parser.error(f"photo does not exist: {args.photo}")
    if args.audio and not args.audio.is_file():
        parser.error(f"audio does not exist: {args.audio}")
    return args


def main(argv: list[str] | None = None) -> int:
    load_env_file(REPO_ROOT / ".env.local")
    args = parse_args(sys.argv[1:] if argv is None else argv)
    photo = args.photo.read_bytes()
    audio = args.audio.read_bytes() if args.audio else diagnostic_wav()
    stopping = False

    def stop(_signum: int, _frame: Any) -> None:
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGINT, stop)
    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, stop)

    sequence = 0
    failures = 0
    next_due = time.monotonic()
    while not stopping:
        wait = next_due - time.monotonic()
        if wait > 0:
            time.sleep(min(wait, 1.0))
            continue
        sequence += 1
        started = time.monotonic()
        record: dict[str, Any] = {
            "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "sequence": sequence,
            "model": args.model,
        }
        try:
            payload = make_payload(
                model=args.model, sequence=sequence, photo=photo, audio=audio,
                audio_encoding=args.audio_encoding, supplied_audio=bool(args.audio),
            )
            answer, first_token_ms = call_omni(
                base_url=args.base_url, api_key=args.api_key, payload=payload,
                timeout_seconds=args.timeout_seconds,
            )
            failures = 0
            record.update(ok=True, first_token_ms=first_token_ms, answer=answer)
        except Exception as error:  # A soak test must survive endpoint and parsing failures.
            failures += 1
            record.update(ok=False, consecutive_failures=failures, error=str(error))
        record["latency_ms"] = round((time.monotonic() - started) * 1000)
        emit(record, args.log)
        if args.once:
            return 0 if record["ok"] else 1
        # Keep a fixed cadence, but never overlap or "catch up" with a request burst.
        next_due += args.interval_seconds
        if next_due <= time.monotonic():
            next_due = time.monotonic() + args.interval_seconds
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
