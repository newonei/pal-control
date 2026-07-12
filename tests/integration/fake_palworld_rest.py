"""Small dependency-free fake used by local Control API integration smoke tests."""

from __future__ import annotations

import argparse
import base64
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import shutil
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class FakePalworldHandler(BaseHTTPRequestHandler):
    announce_count = 0
    last_announce: dict[str, object] | None = None
    players_request_count = 0
    players_available = True
    save_count = 0
    save_mode = "success"
    save_root: Path | None = None
    last_save_snapshot: str | None = None
    world_guid = "ABCDEF0123456789ABCDEF0123456789"
    players: list[dict[str, object]] = [
        {
            "name": "Map Tester One",
            "accountName": "private-account-one",
            "playerId": "11111111111111111111111111111111",
            "userId": "steam_111",
            "ip": "203.0.113.10",
            "ping": 21.5,
            "location_x": -123888.0,
            "location_y": 158000.0,
            "level": 35,
            "building_count": 4,
        },
        {
            "name": "Map Tester Two",
            "accountName": "private-account-two",
            "playerId": "22222222222222222222222222222222",
            "userId": "steam_222",
            "ip": "203.0.113.11",
            "ping": 31.0,
            "location_x": 120000.5,
            "location_y": -240000.25,
            "level": 19,
            "building_count": 1,
        },
    ]
    state_lock = threading.Lock()

    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        if self.path.startswith("/v1/api/") and not self._authorized():
            self._json(401, {"error": "unauthorized"})
            return
        if self.path == "/v1/api/info":
            self._json(
                200,
                {
                    "version": "test",
                    "servername": "Fake Palworld",
                    "description": "integration",
                    "worldguid": self.world_guid,
                },
            )
            return
        if self.path == "/v1/api/players":
            with self.state_lock:
                type(self).players_request_count += 1
                available = type(self).players_available
                players = list(type(self).players)
            if not available:
                self._json(503, {"error": "players unavailable"})
                return
            self._json(200, {"players": players})
            return
        if self.path == "/__state":
            with self.state_lock:
                payload = {
                    "count": self.announce_count,
                    "lastBody": self.last_announce,
                    "playersRequestCount": self.players_request_count,
                    "playersAvailable": self.players_available,
                    "players": self.players,
                    "saveCount": self.save_count,
                    "saveMode": self.save_mode,
                    "saveRoot": str(self.save_root) if self.save_root else None,
                    "lastSaveSnapshot": self.last_save_snapshot,
                }
            self._json(200, payload)
            return
        self._json(404, {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        if self.path == "/__players":
            body = self._read_json()
            if body is None or not isinstance(body.get("players"), list):
                self._json(400, {"error": "players array is required"})
                return
            with self.state_lock:
                type(self).players = body["players"]
                type(self).players_available = bool(body.get("available", True))
            self._json(200, {"ok": True})
            return
        if self.path == "/__players/availability":
            body = self._read_json()
            if body is None or not isinstance(body.get("available"), bool):
                self._json(400, {"error": "available boolean is required"})
                return
            with self.state_lock:
                type(self).players_available = body["available"]
            self._json(200, {"ok": True})
            return
        if self.path == "/__save/mode":
            body = self._read_json()
            supported = {"success", "failure", "uncertain", "no-snapshot"}
            if body is None or body.get("mode") not in supported:
                self._json(
                    400,
                    {"error": f"mode must be one of: {', '.join(sorted(supported))}"},
                )
                return
            with self.state_lock:
                type(self).save_mode = str(body["mode"])
            self._json(200, {"ok": True, "mode": body["mode"]})
            return
        if not self._authorized():
            self._json(401, {"error": "unauthorized"})
            return
        if self.path == "/v1/api/save":
            with self.state_lock:
                type(self).save_count += 1
                mode = type(self).save_mode
                save_number = type(self).save_count
            if mode != "no-snapshot" and type(self).save_root is not None:
                snapshot = self._create_world_snapshot(type(self).save_root, save_number)
                with self.state_lock:
                    type(self).last_save_snapshot = str(snapshot)
            if mode == "failure":
                self._json(503, {"error": "save unavailable"})
                return
            if mode == "uncertain":
                # The caller times out after the save side effect, modeling a lost reply.
                time.sleep(3)
            self._json(200, {})
            return
        if self.path != "/v1/api/announce":
            self._json(404, {"error": "not found"})
            return
        if not self.headers.get("Content-Type", "").lower().startswith("application/json"):
            self._json(400, {"error": "application/json is required"})
            return

        body = self._read_json()
        if body is None:
            self._json(400, {"error": "invalid json"})
            return
        if not isinstance(body.get("message"), str) or not body["message"].strip():
            self._json(400, {"error": "message is required"})
            return

        with self.state_lock:
            type(self).announce_count += 1
            type(self).last_announce = body
        if "UNCERTAIN_TEST" in body["message"]:
            time.sleep(3)
        self._json(200, {})

    def log_message(self, _format: str, *_args: object) -> None:
        return

    def _authorized(self) -> bool:
        token = base64.b64encode(b"admin:test-password").decode("ascii")
        return self.headers.get("Authorization") == f"Basic {token}"

    @staticmethod
    def _create_world_snapshot(world_root: Path, save_number: int) -> Path:
        """Create the stable native backup shape produced by Palworld's world save."""
        backup_root = world_root / "backup" / "world"
        backup_root.mkdir(parents=True, exist_ok=True)
        snapshot_time = datetime.now(timezone.utc) + timedelta(seconds=save_number)
        destination = backup_root / snapshot_time.strftime("%Y.%m.%d-%H.%M.%S")
        while destination.exists():
            snapshot_time += timedelta(seconds=1)
            destination = backup_root / snapshot_time.strftime("%Y.%m.%d-%H.%M.%S")
        destination.mkdir()
        for source in world_root.iterdir():
            if source.name.lower() == "backup":
                continue
            target = destination / source.name
            if source.is_dir():
                shutil.copytree(source, target)
            else:
                shutil.copy2(source, target)
        return destination

    def _read_json(self) -> dict[str, object] | None:
        length = int(self.headers.get("Content-Length", "0"))
        try:
            payload = json.loads(self.rfile.read(length) or b"{}")
        except json.JSONDecodeError:
            return None
        return payload if isinstance(payload, dict) else None

    def _json(self, status: int, payload: object) -> None:
        content = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        try:
            self.wfile.write(content)
        except (BrokenPipeError, ConnectionResetError):
            pass


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=18212)
    parser.add_argument(
        "--save-root",
        type=Path,
        help="Active Palworld world directory copied into backup/world on save.",
    )
    parser.add_argument(
        "--world-guid",
        default=FakePalworldHandler.world_guid,
        help="World GUID returned by the official info endpoint.",
    )
    args = parser.parse_args()
    FakePalworldHandler.save_root = args.save_root.resolve() if args.save_root else None
    FakePalworldHandler.world_guid = args.world_guid
    ThreadingHTTPServer(("127.0.0.1", args.port), FakePalworldHandler).serve_forever()


if __name__ == "__main__":
    main()
