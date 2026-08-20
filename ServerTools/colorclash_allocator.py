#!/usr/bin/env python3
"""Small single-match allocator for the Color Clash prototype VPS.

It restarts the dedicated-server systemd service, waits for its fresh Relay join
code in server.log, and returns that code to the Unity lobby creator.  A single
CX23 instance intentionally permits one active room at a time.
"""

import json
import os
import re
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = "127.0.0.1"
PORT = int(os.environ.get("COLORCLASH_ALLOCATOR_PORT", "8080"))
SERVER_UNIT = "colorclash.service"
SERVER_LOG = "/opt/colorclash/server.log"
STATE_FILE = "/opt/colorclash/allocator-state.json"
JOIN_CODE_PATTERN = re.compile(r"RELAY_JOIN_CODE=([A-Z0-9]{6})")
LOCK = threading.Lock()


def read_state():
    try:
        with open(STATE_FILE, "r", encoding="utf-8") as source:
            return json.load(source)
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def write_state(state):
    temporary = STATE_FILE + ".tmp"
    with open(temporary, "w", encoding="utf-8") as destination:
        json.dump(state, destination)
    os.replace(temporary, STATE_FILE)


def wait_for_join_code(timeout_seconds=35):
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        try:
            with open(SERVER_LOG, "r", encoding="utf-8", errors="replace") as source:
                matches = JOIN_CODE_PATTERN.findall(source.read())
            if matches:
                return matches[-1]
        except FileNotFoundError:
            pass
        time.sleep(0.5)
    raise TimeoutError("Dedicated server did not produce a Relay join code in time.")


class AllocatorHandler(BaseHTTPRequestHandler):
    def end_headers(self):
        # Prototype only. Restrict this to the deployed Web build origin before
        # a public release.
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        super().end_headers()

    def write_json(self, status, payload):
        encoded = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def do_OPTIONS(self):
        self.send_response(204)
        self.end_headers()

    def do_GET(self):
        if self.path != "/health":
            self.write_json(404, {"error": "not found"})
            return
        state = read_state()
        self.write_json(200, {"ok": True, "activeLobbyId": state.get("lobbyId", "")})

    def do_POST(self):
        if self.path != "/allocate":
            self.write_json(404, {"error": "not found"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            payload = json.loads(self.rfile.read(length).decode("utf-8"))
            lobby_id = str(payload.get("lobbyId", "")).strip()
            if not lobby_id:
                raise ValueError("lobbyId is required")
        except (ValueError, json.JSONDecodeError) as error:
            self.write_json(400, {"error": str(error)})
            return

        with LOCK:
            state = read_state()
            active_lobby = state.get("lobbyId", "")
            if active_lobby and active_lobby != lobby_id:
                self.write_json(409, {"error": "A Color Clash room is already using this prototype server."})
                return

            try:
                subprocess.run(["systemctl", "restart", SERVER_UNIT], check=True, timeout=15)
                join_code = wait_for_join_code()
                write_state({"lobbyId": lobby_id, "relayJoinCode": join_code, "allocatedAt": int(time.time())})
                self.write_json(200, {"relayJoinCode": join_code})
            except (subprocess.SubprocessError, TimeoutError, OSError) as error:
                self.write_json(503, {"error": str(error)})

    def log_message(self, format_string, *args):
        print("[Color Clash Allocator] " + format_string % args, flush=True)


if __name__ == "__main__":
    print(f"Color Clash allocator listening on {HOST}:{PORT}", flush=True)
    ThreadingHTTPServer((HOST, PORT), AllocatorHandler).serve_forever()
