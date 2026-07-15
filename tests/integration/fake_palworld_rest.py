"""Small dependency-free fake used by local Control API integration smoke tests."""

from __future__ import annotations

import argparse
import base64
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import re
import shutil
import socketserver
import struct
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
    pd_token = "integration-pd-token"
    pd_game_version = "1.0.0.100427"
    pd_adapter_version = "1.8.1.3933"
    pd_give_count = 0
    rcon_delete_count = 0
    rcon_commands: list[str] = []
    login_codes: dict[str, str] = {}
    pd_inventories: dict[str, dict[str, int]] = {
        "steam_111": {"Leather": 5},
        "steam_222": {"Leather": 7},
    }
    pd_players: list[dict[str, object]] = [
        {
            "Name": "Portal Tester One",
            "UserId": "steam_111",
            "PlayerUID": "11111111111111111111111111111111",
            "Status": "Online",
            "MapLocation": {"x": 0.0, "y": 0.0},
        },
        {
            "Name": "Portal Tester Two",
            "UserId": "steam_222",
            "PlayerUID": "22222222222222222222222222222222",
            "Status": "Online",
            "MapLocation": {"x": 10.0, "y": 10.0},
        },
    ]
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
        if self.path.startswith("/v1/pdapi/"):
            if not self._pd_authorized():
                self._json(401, {"error": "unauthorized"})
                return
            if self.path == "/v1/pdapi/version":
                self._json(
                    200,
                    {
                        "game_version": self.pd_game_version,
                        "paldefender": {"full": self.pd_adapter_version},
                    },
                )
                return
            if self.path == "/v1/pdapi/players":
                with self.state_lock:
                    players = list(type(self).pd_players)
                self._json(200, {"Players": players})
                return
            if self.path.startswith("/v1/pdapi/items/"):
                user_id = self.path.removeprefix("/v1/pdapi/items/")
                with self.state_lock:
                    inventory = dict(type(self).pd_inventories.get(user_id, {}))
                slots = [
                    {"ItemID": item_id, "Count": count}
                    for item_id, count in sorted(inventory.items())
                    if count > 0
                ]
                self._json(
                    200,
                    {
                        "Inventory": {
                            "Items": {"Slots": slots},
                            "Food": {"Slots": []},
                            "DropSlot": {"Slots": []},
                        }
                    },
                )
                return
            self._json(404, {"error": "not found"})
            return
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
                    "pdGiveCount": self.pd_give_count,
                    "rconDeleteCount": self.rcon_delete_count,
                    "rconCommands": list(self.rcon_commands),
                    "loginCodes": dict(self.login_codes),
                    "pdInventories": self.pd_inventories,
                }
            self._json(200, payload)
            return
        self._json(404, {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        if self.path.startswith("/v1/pdapi/give/items/"):
            if not self._pd_authorized():
                self._json(401, {"error": "unauthorized"})
                return
            body = self._read_json()
            items = None if body is None else body.get("Items")
            if not isinstance(items, list) or len(items) != 1:
                self._json(400, {"error": "exactly one item is required"})
                return
            item = items[0]
            if not isinstance(item, dict):
                self._json(400, {"error": "item object is required"})
                return
            item_id = item.get("ItemID")
            count = item.get("Count")
            if not isinstance(item_id, str) or not isinstance(count, int) or count <= 0:
                self._json(400, {"error": "valid ItemID and Count are required"})
                return
            user_id = self.path.removeprefix("/v1/pdapi/give/items/")
            with self.state_lock:
                inventory = type(self).pd_inventories.setdefault(user_id, {})
                inventory[item_id] = inventory.get(item_id, 0) + count
                type(self).pd_give_count += 1
            self._json(200, {"Granted": {"Items": count}})
            return
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

    def _pd_authorized(self) -> bool:
        return self.headers.get("Authorization") == f"Bearer {self.pd_token}"

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


class FakeSourceRconHandler(socketserver.BaseRequestHandler):
    """Minimal Source RCON peer for player-login and settlement HTTP smoke tests."""

    password = "integration-rcon-password"

    def handle(self) -> None:
        try:
            auth_id, packet_type, password = self._read_packet()
            if packet_type != 3 or password != self.password:
                self._write_packet(-1, 2, "")
                return
            self._write_packet(auth_id, 2, "")
            command_id, packet_type, command = self._read_packet()
            if packet_type != 2:
                self._write_packet(command_id, 0, "error invalid command packet")
                return
            response = self._execute(command)
            # PalDefender commonly uses request id zero for command responses.
            self._write_packet(0, 0, response)
        except (ConnectionError, OSError, ValueError):
            return

    def _execute(self, command: str) -> str:
        with FakePalworldHandler.state_lock:
            FakePalworldHandler.rcon_commands.append(command)

        if command == "getrconcmds":
            return "send:3 delitems:2 clearinv:2"
        if command == "version":
            return json.dumps(
                {
                    "game_version": FakePalworldHandler.pd_game_version,
                    "paldefender": {"full": FakePalworldHandler.pd_adapter_version},
                },
                separators=(",", ":"),
            )
        if command.startswith("send ilog "):
            parts = command.split(" ", 3)
            if len(parts) != 4:
                return "error invalid send arguments"
            match = re.search(r"(?<!\d)(\d{8})(?!\d)", parts[3])
            if match is None:
                return "error login code missing"
            with FakePalworldHandler.state_lock:
                FakePalworldHandler.login_codes[parts[2]] = match.group(1)
            return "ok"
        if command.startswith("delitems "):
            parts = command.split()
            if len(parts) < 3:
                return "error invalid delitems arguments"
            user_id = parts[1]
            requested: list[tuple[str, int]] = []
            try:
                for value in parts[2:]:
                    item_id, raw_count = value.rsplit(":", 1)
                    count = int(raw_count)
                    if not item_id or count <= 0:
                        raise ValueError
                    requested.append((item_id, count))
            except ValueError:
                return "error invalid delitems item"
            with FakePalworldHandler.state_lock:
                inventory = FakePalworldHandler.pd_inventories.setdefault(user_id, {})
                if any(inventory.get(item_id, 0) < count for item_id, count in requested):
                    return "error insufficient items"
                for item_id, count in requested:
                    inventory[item_id] = inventory.get(item_id, 0) - count
                FakePalworldHandler.rcon_delete_count += 1
            return "ok"
        return "error unknown command"

    def _read_packet(self) -> tuple[int, int, str]:
        length_raw = self._read_exact(4)
        (length,) = struct.unpack("<i", length_raw)
        if length < 10 or length > 1_048_572:
            raise ValueError("invalid Source RCON length")
        payload = self._read_exact(length)
        if payload[-2:] != b"\x00\x00":
            raise ValueError("invalid Source RCON terminator")
        request_id, packet_type = struct.unpack("<ii", payload[:8])
        return request_id, packet_type, payload[8:-2].decode("utf-8")

    def _write_packet(self, request_id: int, packet_type: int, body: str) -> None:
        body_bytes = body.encode("utf-8")
        payload = struct.pack("<ii", request_id, packet_type) + body_bytes + b"\x00\x00"
        self.request.sendall(struct.pack("<i", len(payload)) + payload)

    def _read_exact(self, length: int) -> bytes:
        chunks = bytearray()
        while len(chunks) < length:
            chunk = self.request.recv(length - len(chunks))
            if not chunk:
                raise ConnectionError("Source RCON connection closed")
            chunks.extend(chunk)
        return bytes(chunks)


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
    parser.add_argument(
        "--rcon-port",
        type=int,
        help="Optional loopback Source RCON port used by portal/economy smoke tests.",
    )
    parser.add_argument(
        "--rcon-password",
        default=FakeSourceRconHandler.password,
        help="Password accepted by the optional Source RCON fake.",
    )
    args = parser.parse_args()
    FakePalworldHandler.save_root = args.save_root.resolve() if args.save_root else None
    FakePalworldHandler.world_guid = args.world_guid
    rcon_server: socketserver.ThreadingTCPServer | None = None
    if args.rcon_port is not None:
        FakeSourceRconHandler.password = args.rcon_password
        socketserver.ThreadingTCPServer.allow_reuse_address = True
        rcon_server = socketserver.ThreadingTCPServer(
            ("127.0.0.1", args.rcon_port), FakeSourceRconHandler
        )
        rcon_server.daemon_threads = True
        threading.Thread(target=rcon_server.serve_forever, daemon=True).start()

    http_server = ThreadingHTTPServer(("127.0.0.1", args.port), FakePalworldHandler)
    try:
        http_server.serve_forever()
    finally:
        http_server.server_close()
        if rcon_server is not None:
            rcon_server.shutdown()
            rcon_server.server_close()


if __name__ == "__main__":
    main()
