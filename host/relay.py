"""Short-lived mailbox for encrypted AgentDeck packets."""

import argparse
from collections import deque
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import re
import threading
from time import monotonic


MAX_PACKET = 4096 + 12
TTL = 12
CHANNEL = re.compile(r"[0-9a-f]{64}\Z")
MAILBOX = re.compile(r"/v1/([0-9a-f]{64})/(pc|stick)\Z")


def make_server(address, channels):
    messages = {}
    lock = threading.Lock()
    allowed = dict(channels)
    nonces = {}

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_POST(self):
            target = MAILBOX.fullmatch(self.path)
            try:
                size = int(self.headers["Content-Length"])
            except (TypeError, ValueError):
                size = -1
            if not target or target[1] not in allowed:
                self.send_error(404)
                return
            if not 0 <= size <= MAX_PACKET:
                self.send_error(413)
                return
            self.connection.settimeout(4)
            content = self.rfile.read(size)
            direction = target[2]
            if len(content) != size or (content and (
                    not content.startswith(b"AD2Q") if direction == "pc" else content[12:16] != b"AD2R")):
                self.send_error(400)
                return
            nonce = self.headers.get("X-AgentDeck-Nonce", "")
            supplied = self.headers.get("X-AgentDeck-Auth", "")
            if not re.fullmatch(r"[0-9a-f]{24}", nonce) or not CHANNEL.fullmatch(supplied):
                self.send_error(403)
                return
            data = direction.encode("ascii") + bytes.fromhex(nonce) + content
            expected = hmac.digest(allowed[target[1]], data, "sha256").hex()
            if not hmac.compare_digest(supplied, expected):
                self.send_error(403)
                return
            with lock:
                now = monotonic()
                recent = nonces.setdefault((target[1], direction), deque(maxlen=128))
                replay = nonce in recent
                if not replay:
                    recent.append(nonce)
                if content and not replay:
                    messages[target[1], direction] = (now, content)
                when, result = messages.get((target[1], "stick" if direction == "pc" else "pc"), (0, b""))
                if now - when > TTL or replay:
                    result = b""
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(result)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            try:
                self.wfile.write(result)
            except (BrokenPipeError, ConnectionResetError):
                pass

    return ThreadingHTTPServer(address, Handler)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--port", default=8793, type=int)
    parser.add_argument("--channels", required=True, type=argparse.FileType("r", encoding="utf-8"),
                        help="JSON channel list exported by AgentDeck")
    args = parser.parse_args()
    with args.channels as source:
        records = json.load(source)
    if not records or not isinstance(records, list) or not all(
            isinstance(row, dict) and all(CHANNEL.fullmatch(str(row.get(key, ""))) for key in ("channel", "auth"))
            for row in records):
        parser.error("Invalid channel list")
    channels = {row["channel"]: bytes.fromhex(row["auth"]) for row in records}
    with make_server((args.bind, args.port), channels) as server:
        server.serve_forever()


if __name__ == "__main__":
    main()
