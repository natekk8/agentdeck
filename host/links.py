"""AgentDeck pairing, outgoing LAN relay, built-in remote relay, and local desktop channel."""

import base64
import ctypes
from ctypes import wintypes
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import ipaddress
import json
import os
from pathlib import Path
import queue
import re
import secrets
import socket
import subprocess
import threading
import time
from urllib.error import URLError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

from i18n import DEFAULT_LANG, tr
from relay import make_server as make_relay_server
from version import EXPECTED_STICK_FIRMWARE

PORT = 8766
MAX_PACKET = 4096
DISCOVER = b"AD2?"
DISCOVER_REPLY = b"AD2!"
PAIR = b"AD2P"
PULL = b"AD2Q"
RESPONSE = b"AD2R"
CODE = re.compile(r"[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{16}\Z")
IDENTIFIER = re.compile(r"[a-f0-9]{24}\Z")


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def relay_address(value, lang=DEFAULT_LANG):
    value = value.strip().rstrip("/")
    if not value:
        return ""
    url = urlsplit(value)
    if (len(value) > 180 or not url.hostname or url.username is not None or url.password is not None
            or url.fragment or url.query or url.path or url.scheme != "http"
            or not re.fullmatch(r"[A-Za-z0-9.-]+", url.hostname)):
        raise ValueError(tr(lang, "relay_invalid_url"))
    if url.port == 0:
        raise ValueError(tr(lang, "relay_invalid_port"))
    return value


def relay_channel(key):
    return hmac.digest(key, b"AgentDeck/remote-mailbox/v1", "sha256").hex()


def relay_auth(key):
    return hmac.digest(key, b"AgentDeck/remote-auth/v1", "sha256")


def encode(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def write_json(path, value):
    temporary = path.with_suffix(".writing")
    temporary.write_bytes(encode(value))
    temporary.replace(path)


class Blob(ctypes.Structure):
    _fields_ = [("length", wintypes.DWORD), ("bytes", ctypes.c_void_p)]


def local_secret(value, decrypt=False):
    if os.name != "nt":
        return value
    buffer = ctypes.create_string_buffer(value)
    source = Blob(len(value), ctypes.cast(buffer, ctypes.c_void_p))
    target = Blob()
    api = ctypes.windll.crypt32
    function = api.CryptUnprotectData if decrypt else api.CryptProtectData
    if not function(ctypes.byref(source), None, None, None, None, 1, ctypes.byref(target)):
        raise ctypes.WinError()
    release = ctypes.windll.kernel32.LocalFree
    release.argtypes = [ctypes.c_void_p]
    release.restype = ctypes.c_void_p
    try:
        return ctypes.string_at(target.bytes, target.length)
    finally:
        release(target.bytes)


def protected(data):
    return base64.b64encode(local_secret(data)).decode("ascii")


def unprotected(value):
    return local_secret(base64.b64decode(value, validate=True), decrypt=True)


def channel(key):
    return tuple(AESGCM(hmac.digest(key, tag, "sha256"))
                 for tag in (b"AgentDeck/pc-to-stick/v2", b"AgentDeck/stick-to-pc/v2"))


def pairing_key(code, lang=DEFAULT_LANG):
    normalized = normalize_code(code, lang=lang)
    return hashlib.sha256(b"AgentDeck pairing v2\0" + normalized.encode("ascii")).digest()


def normalize_code(code, lang=DEFAULT_LANG):
    normalized = re.sub(r"[\s-]+", "", code).upper()
    if not CODE.fullmatch(normalized):
        raise ValueError(tr(lang, "enter_16_code"))
    return normalized


def make_packet(kind, header, payload, key, lang=DEFAULT_LANG):
    first, _ = channel(key)
    nonce = secrets.token_bytes(12)
    aad = kind + header
    packet = aad + nonce + first.encrypt(nonce, encode(payload), aad)
    if len(packet) > MAX_PACKET:
        raise ValueError(tr(lang, "invalid_frame"))
    return nonce, packet


def read_response(packet, nonce, key, lang=DEFAULT_LANG):
    if RESPONSE != packet[:4] or len(packet) <= 32 or len(packet) > MAX_PACKET:
        raise ValueError(tr(lang, "invalid_stick_reply"))
    _, second = channel(key)
    result = json.loads(second.decrypt(packet[4:16], packet[16:], nonce))
    if not isinstance(result, dict):
        raise ValueError(tr(lang, "invalid_stick_reply"))
    return result


def interfaces():
    if os.name != "nt":
        return []
    powershell = Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"
    script = (
        "$items = foreach($nic in Get-NetAdapter -Physical | Where-Object Status -eq 'Up') {"
        " foreach($addr in Get-NetIPAddress -InterfaceIndex $nic.ifIndex -AddressFamily IPv4"
        " | Where-Object AddressState -eq 'Preferred') { "
        " [pscustomobject]@{ip=$addr.IPAddress;prefix=$addr.PrefixLength;mac=$nic.MacAddress} } };"
        " ConvertTo-Json -InputObject @($items) -Compress"
    )
    try:
        answer = subprocess.run([str(powershell), "-NoProfile", "-NonInteractive", "-Command", script],
                                capture_output=True, encoding="utf-8", errors="replace",
                                creationflags=subprocess.CREATE_NO_WINDOW, timeout=6, check=True)
        output = []
        for nic in json.loads(answer.stdout):
            ip = ipaddress.IPv4Interface(f"{nic['ip']}/{nic['prefix']}")
            mac = nic["mac"].replace("-", "").lower()
            if ip.ip.is_link_local or ip.ip.is_loopback or not re.fullmatch(r"[a-f0-9]{12}", mac):
                continue
            output.append({"ip": str(ip.ip), "network": ip.network, "mac": mac})
        return output
    except (OSError, ValueError, subprocess.SubprocessError):
        return []


class DeviceAction:
    def __init__(self, request):
        self.request = request
        self.ready = threading.Event()
        self.answer = None
        self.expires = time.monotonic() + 6


class Links:
    def __init__(self, data, deck, done):
        self.data = data
        self.deck = deck
        self.done = done
        self.lock = threading.RLock()
        self.usb = queue.Queue(maxsize=1)
        self.usb_ready = False
        self.usb_device = ""
        self.usb_port = ""
        self.usb_paused = threading.Event()
        self.seen = {}
        self.online = {}
        self.remote_online = {}
        self.relay_test_until = {}
        self.relay_pending = {}
        self.relay_opener = build_opener(ProxyHandler({}), NoRedirect())
        self.snapshots = {}
        self.selected = {}
        self.controls = {}
        self.lang_synced = {}
        self.local_relay_server = None
        self.local_relay_thread = None
        self.local_relay_bind = "127.0.0.1"
        self.local_relay_port = 8793
        self.path = data / "devices.json"
        self.config = json.loads(self.path.read_bytes()) if self.path.is_file() else {
            "host": secrets.token_hex(12), "devices": {},
        }
        if not self.path.is_file():
            write_json(self.path, self.config)
        self.host = self.config["host"]
        self.host_raw = bytes.fromhex(self.host)
        self.ifaces = []
        self.ifaces_at = 0

    def lang(self):
        getter = getattr(self.deck, "language", None)
        return getter() if callable(getter) else DEFAULT_LANG

    def interface_snapshot(self):
        with self.lock:
            if self.ifaces_at and time.monotonic() - self.ifaces_at < 25:
                return self.ifaces
        available = interfaces()
        with self.lock:
            self.ifaces = available
            self.ifaces_at = time.monotonic()
        return available

    def host_info(self, key, address=None):
        choices = self.interface_snapshot()
        if address:
            ip = ipaddress.IPv4Address(address)
            choices = [item for item in choices if ip in item["network"]]
        return {
            "host": self.host, "label": socket.gethostname()[:48],
            "mac": choices[0]["mac"] if choices else "000000000000",
            "key": key.hex(),
        }

    def scan(self, manual=None):
        ifaces = self.interface_snapshot()
        lang = self.lang()
        if manual:
            dest = ipaddress.IPv4Address(manual)
            if dest.is_loopback or dest.is_multicast or dest.is_unspecified:
                raise ValueError(tr(lang, "enter_local_ip"))
            if ifaces and not any(dest in item["network"] for item in ifaces):
                raise ValueError(tr(lang, "same_network_ip"))
            points = [(None, (str(dest), PORT))]
        else:
            points = [(item["ip"], (str(item["network"].broadcast_address), PORT)) for item in ifaces]
            if not points:
                points = [(None, ("255.255.255.255", PORT))]
        found = {}
        nonce = secrets.token_bytes(12)
        for source, destination in points:
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as endpoint:
                    endpoint.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
                    if source:
                        endpoint.bind((source, 0))
                    endpoint.sendto(DISCOVER + nonce, destination)
                    deadline = time.monotonic() + .30
                    while time.monotonic() < deadline:
                        endpoint.settimeout(max(.01, deadline - time.monotonic()))
                        try:
                            result, peer = endpoint.recvfrom(128)
                        except socket.timeout:
                            break
                        if len(result) == 29 and result.startswith(DISCOVER_REPLY + nonce) and peer[1] == PORT:
                            ident = result[16:28].hex()
                            found[ident] = {"id": ident, "address": peer[0], "pairing": result[28] == 1}
            except OSError:
                continue
        with self.lock:
            self.seen.update(found)
        return found

    def exchange(self, destination, kind, header, key, payload, timeout=.8):
        lang = self.lang()
        nonce, packet = make_packet(kind, header, payload, key, lang=lang)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as endpoint:
            endpoint.settimeout(timeout)
            endpoint.connect((destination, PORT))
            endpoint.send(packet)
            arrive = endpoint.recv(MAX_PACKET + 1)
        return read_response(arrive, nonce, key, lang=lang)

    def save(self, device, address, key):
        if not IDENTIFIER.fullmatch(str(device)):
            raise ValueError(tr(self.lang(), "invalid_stick_id"))
        with self.lock:
            self.config["devices"][device] = {"key": protected(key), "address": address or ""}
            write_json(self.path, self.config)

    def pair_lan(self, address, code):
        lang = self.lang()
        auth = pairing_key(code, lang=lang)
        found = self.scan(address or None)
        ready = [item for item in found.values() if item["pairing"]]
        if len(ready) != 1:
            raise ValueError(tr(lang, "open_pairing_screen"))
        candidate = ready[0]
        key = secrets.token_bytes(32)
        info = self.host_info(key, candidate["address"])
        info["device"] = candidate["id"]
        try:
            answer = self.exchange(candidate["address"], PAIR, b"", auth, info)
        except (OSError, ValueError, InvalidTag) as error:
            raise ValueError(tr(lang, "stick_rejected_code")) from error
        if not answer.get("ok") or answer.get("device") != candidate["id"]:
            raise ValueError(tr(lang, "stick_rejected_pair"))
        self.save(candidate["id"], candidate["address"], key)
        if self.local_relay_server is not None:
            self.local_relay_start(self.local_relay_bind, self.local_relay_port)
        return candidate["id"]

    def usb_action(self, request):
        lang = self.lang()
        if not self.usb_ready:
            raise ValueError(tr(lang, "connect_usb"))
        action = DeviceAction(request)
        try:
            self.usb.put_nowait(action)
        except queue.Full as error:
            raise ValueError(tr(lang, "usb_busy")) from error
        if not action.ready.wait(4.5) or not action.answer:
            action.expires = 0
            raise ValueError(tr(lang, "stick_no_reply_usb"))
        return self.action_result(action.answer)

    def action_result(self, result):
        if not result.get("ok"):
            raise ValueError(result.get("error") or tr(self.lang(), "stick_rejected_action"))
        return result

    def pair_usb(self, code=""):
        key = secrets.token_bytes(32)
        request = {"id": secrets.token_hex(12), "op": "pair_pc", **self.host_info(key)}
        if code.strip():
            request["code"] = normalize_code(code, lang=self.lang())
        answer = self.usb_action(request)
        address = answer.get("address") or ""
        if address:
            address = str(ipaddress.IPv4Address(address))
        device = answer["device"]
        self.save(device, address, key)
        if self.local_relay_server is not None:
            self.local_relay_start(self.local_relay_bind, self.local_relay_port)
        return device

    def observe(self, snapshot, usb=False):
        if not isinstance(snapshot, dict) or not IDENTIFIER.fullmatch(str(snapshot.get("device", ""))):
            return
        device = snapshot["device"]
        sync_target = None
        with self.lock:
            if usb:
                self.usb_device = device
                self.usb_ready = True
            self.snapshots[device] = snapshot
            address = snapshot.get("address")
            if address:
                self.seen[device] = {"id": device, "address": str(ipaddress.IPv4Address(address))}
            stick_lang = snapshot.get("lang")
            pc_lang = self.lang()
            if stick_lang in ("en", "pl") and stick_lang != pc_lang and self.lang_synced.get(device) != pc_lang:
                self.lang_synced[device] = pc_lang
                sync_target = pc_lang
        if sync_target:
            threading.Thread(target=self.push_language, args=(sync_target,), daemon=True).start()

    def devices(self):
        with self.lock:
            choices = self.paired()
            if self.usb_ready and self.usb_device and not any(p["id"] == self.usb_device for p in choices):
                choices.append({"id": self.usb_device, "address": "", "online": False, "paired": False})
            values = []
            needs_fw = False
            stick_fw = ""
            for item in choices:
                device = item["id"]
                snap = self.snapshots.get(device, {})
                fw = str(snap.get("firmware") or "")
                if fw:
                    stick_fw = fw
                    if fw != EXPECTED_STICK_FIRMWARE:
                        needs_fw = True
                values.append({**item, "usb": self.usb_ready and self.usb_device == device,
                               "snapshot": snap})
            return {
                "paired": choices,
                "usb": self.usb_ready,
                "usb_port": self.usb_port,
                "devices": values,
                "lang": self.lang(),
                "expected_firmware": EXPECTED_STICK_FIRMWARE,
                "stick_firmware": stick_fw,
                "firmware_update_needed": needs_fw,
            }

    def command(self, device, action, **values):
        lang = self.lang()
        ident = secrets.token_hex(12)
        with self.lock:
            wireless_test = time.monotonic() < self.relay_test_until.get(device, 0)
            use_usb = self.usb_ready and (not device or device == self.usb_device) and (
                not wireless_test or action == "relay_config")
        if use_usb:
            return self.usb_action({"id": ident, "op": "device_control", "action": action, **values})
        order = DeviceAction({"id": ident, "action": action, **values})
        with self.lock:
            if not device:
                device = next((d for d in self.config["devices"] if self.selected.get(d, False)), "")
            if device not in self.config["devices"] or not self.selected.get(device, False) or (
                    not self.is_online(device)):
                raise ValueError(tr(lang, "connect_usb_or_select_pc"))
            if device in self.controls:
                raise ValueError(tr(lang, "wifi_busy"))
            self.controls[device] = order
        try:
            if not order.ready.wait(9) or not order.answer:
                raise ValueError(tr(lang, "stick_no_reply"))
            return self.action_result(order.answer)
        finally:
            with self.lock:
                self.controls.pop(device, None)

    def push_language(self, language):
        """Best-effort language sync to any connected Stick."""
        with self.lock:
            target = self.usb_device if self.usb_ready else next(
                (d for d in self.config["devices"] if self.selected.get(d, False) and self.is_online(d)), ""
            )
        if not target:
            return False
        try:
            self.command(target, "set_language", lang=language)
            return True
        except ValueError:
            return False

    def is_online(self, device):
        now = time.monotonic()
        return now - self.online.get(device, -100) < 4 or now - self.remote_online.get(device, -100) < 9

    def paired(self):
        with self.lock:
            return [{"id": device, "address": self.seen.get(device, {}).get("address", entry["address"]),
                     "online": self.is_online(device),
                     "remote": time.monotonic() - self.remote_online.get(device, -100) < 9,
                     "paired": True}
                    for device, entry in self.config["devices"].items()]

    def forget(self, device):
        with self.lock:
            if self.config["devices"].pop(device, None):
                self.relay_test_until.pop(device, None)
                write_json(self.path, self.config)

    def configure_relay(self, value):
        endpoint = relay_address(value, lang=self.lang())
        with self.lock:
            self.config["relay"] = endpoint
            write_json(self.path, self.config)
        return self.relay_config()

    def relay_config(self):
        with self.lock:
            keys = [unprotected(entry["key"]) for entry in self.config["devices"].values()]
            channels = [{"channel": relay_channel(key), "auth": relay_auth(key).hex()} for key in keys]
            return {
                "endpoint": self.config.get("relay", ""),
                "channels": channels,
                "local_relay": self.local_relay_status(),
            }

    def _port_open(self, host, port):
        try:
            with socket.create_connection((host, int(port)), timeout=0.2):
                return True
        except OSError:
            return False

    def local_relay_status(self):
        with self.lock:
            internal = self.local_relay_server is not None
            external = (not internal) and self._port_open(self.local_relay_bind, self.local_relay_port)
            return {
                "running": internal or external,
                "external": external,
                "bind": self.local_relay_bind,
                "port": self.local_relay_port,
                "channels": len(self.config.get("devices", {})),
            }

    def local_relay_start(self, bind="127.0.0.1", port=8793):
        self.local_relay_stop()
        with self.lock:
            if not self.config.get("devices"):
                raise ValueError(tr(self.lang(), "pair_first"))
            keys = [unprotected(entry["key"]) for entry in self.config["devices"].values()]
            channels = {relay_channel(key): relay_auth(key) for key in keys}
            try:
                server = make_relay_server((bind, int(port)), channels)
            except OSError as error:
                if int(port) > 0 and self._port_open(bind, int(port)):
                    self.local_relay_bind = bind
                    self.local_relay_port = int(port)
                    return self.local_relay_status()
                raise ValueError(tr(self.lang(), "relay_local_failed", port=port, error=error)) from error
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            self.local_relay_server = server
            self.local_relay_thread = thread
            self.local_relay_bind = bind
            self.local_relay_port = int(server.server_port)
            return self.local_relay_status()

    def local_relay_stop(self):
        with self.lock:
            server = self.local_relay_server
            thread = self.local_relay_thread
            self.local_relay_server = None
            self.local_relay_thread = None
        if server is not None:
            server.shutdown()
            server.server_close()
        if thread is not None:
            thread.join(timeout=1.5)
        return self.local_relay_status()

    def test_relay(self, device, enabled):
        with self.lock:
            if device not in self.config["devices"] or not self.config.get("relay"):
                raise ValueError(tr(self.lang(), "select_paired_and_relay"))
            if enabled:
                self.relay_test_until[device] = time.monotonic() + 60
            else:
                self.relay_test_until.pop(device, None)
            return {"testing": enabled}

    def exchange_remote(self, device, endpoint, key, payload):
        lang = self.lang()
        nonce, packet = make_packet(PULL, self.host_raw, payload, key, lang=lang)
        url = endpoint + "/v1/" + relay_channel(key) + "/pc"
        pending = self.relay_pending.setdefault(device, {})
        pending[nonce] = time.monotonic()
        for old, when in tuple(pending.items()):
            if time.monotonic() - when > 12:
                pending.pop(old, None)
        auth = hmac.digest(relay_auth(key), b"pc" + nonce + packet, "sha256").hex()
        request = Request(url, data=packet, method="POST", headers={
            "Content-Type": "application/octet-stream",
            "X-AgentDeck-Nonce": nonce.hex(), "X-AgentDeck-Auth": auth,
        })
        with self.relay_opener.open(request, timeout=2) as response:
            result = response.read(MAX_PACKET + 13)
        nonce = result[:12]
        if not 44 < len(result) <= MAX_PACKET + 12 or nonce not in pending:
            return {}
        reply = read_response(result[12:], nonce, key, lang=lang)
        pending.pop(nonce, None)
        return reply

    def run(self):
        answers = {}
        tickets = {}
        next_scan = 0
        try:
            while not self.done.is_set():
                with self.lock:
                    peers = dict(self.config["devices"])
                    endpoint = self.config.get("relay", "")
                if not peers:
                    next_scan = 0
                    self.done.wait(.45)
                    continue
                if time.monotonic() >= next_scan:
                    self.scan()
                    next_scan = time.monotonic() + 12
                for device, saved in peers.items():
                    with self.lock:
                        address = self.seen.get(device, {}).get("address") or saved["address"]
                        testing = time.monotonic() < self.relay_test_until.get(device, 0)
                    try:
                        key = unprotected(saved["key"])
                        payload = {"answer": answers.get(device)}
                        with self.lock:
                            order = self.controls.get(device)
                            if order and tickets.get(device) and not order.ready.is_set():
                                payload["control"] = {**order.request, "ticket": tickets[device]}
                        for interface in self.ifaces:
                            if address and ipaddress.IPv4Address(address) in interface["network"]:
                                payload["mac"] = interface["mac"]
                                break
                        result = {}
                        if address and not (endpoint and testing):
                            try:
                                result = self.exchange(address, PULL, self.host_raw, key, payload, timeout=.24)
                            except (OSError, ValueError, InvalidTag, KeyError):
                                pass
                        remote = not result and bool(endpoint)
                        if remote:
                            payload.pop("mac", None)
                            result = self.exchange_remote(device, endpoint, key, payload)
                        if result.get("device") != device:
                            continue
                        with self.lock:
                            (self.remote_online if remote else self.online)[device] = time.monotonic()
                            self.selected[device] = result.get("selected") is True
                        if result.get("snapshot"):
                            self.observe(result["snapshot"])
                        tickets[device] = result.get("ticket")
                        control_result = result.get("control_result")
                        if order and isinstance(control_result, dict) and control_result.get("id") == order.request["id"]:
                            order.answer = control_result
                            order.ready.set()
                        request = result.get("request")
                        answers[device] = self.deck.request(request) if isinstance(request, dict) else None
                    except (OSError, URLError, ValueError, InvalidTag, KeyError):
                        continue
                self.done.wait(.32)
        finally:
            self.local_relay_stop()


def desktop_server(deck, links):
    token = secrets.token_hex(32)

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *unused):
            pass

        def do_POST(self):
            try:
                bearer = self.headers.get("Authorization", "")
                authorized = hmac.compare_digest(bearer.encode("utf-8"), ("Bearer " + token).encode())
                if self.path != "/desktop" or not authorized:
                    self.send_error(403)
                    return
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= 350000:
                    self.send_error(413)
                    return
                self.connection.settimeout(20)
                request = json.loads(self.rfile.read(length))
                operation = request["op"]
                if operation == "discover":
                    result = {"devices": list(links.scan(request.get("address")).values())}
                elif operation == "pair_lan":
                    result = {"device": links.pair_lan(request.get("address"), request["code"])}
                elif operation == "pair_usb":
                    result = {"device": links.pair_usb(request.get("code", ""))}
                elif operation == "forget":
                    links.forget(request["device"])
                    result = {}
                elif operation == "devices":
                    result = links.devices()
                elif operation == "relay_get":
                    result = links.relay_config()
                elif operation == "relay_save":
                    result = links.configure_relay(request["endpoint"])
                elif operation == "relay_sync":
                    device = request.get("device") or links.usb_device
                    if device not in links.config["devices"]:
                        raise ValueError(tr(links.lang(), "pair_first"))
                    result = links.command(device, "relay_config", host=links.host,
                                           endpoint=links.relay_config()["endpoint"])
                elif operation == "relay_test":
                    result = links.test_relay(request.get("device"), request.get("enabled", True) is True)
                elif operation == "local_relay_status":
                    result = links.local_relay_status()
                elif operation == "local_relay_start":
                    result = links.local_relay_start(
                        request.get("bind", "127.0.0.1"),
                        int(request.get("port", 8793)),
                    )
                elif operation == "local_relay_stop":
                    result = links.local_relay_stop()
                elif operation in {"wifi_scan", "wifi_list", "wifi_connect"}:
                    connection = {k: request[k] for k in ("ssid", "password")} if operation == "wifi_connect" else {}
                    result = links.command(request.get("device"), operation, **connection)
                elif operation == "show_project":
                    project = deck.desktop("project_target", request)
                    result = links.command(request.get("device"), "open_project", **project)
                elif operation == "show_pairing":
                    if not links.usb_ready:
                        raise ValueError(tr(links.lang(), "open_usb_or_pairing"))
                    result = links.command(links.usb_device, "show_pairing")
                elif operation == "stick_flash_firmware":
                    result = deck.flash_stick_firmware(links, request.get("port", ""))
                elif operation == "settings_save":
                    result = deck.desktop("settings_save", request)
                    if "language" in request:
                        result["stick_synced"] = links.push_language(result["language"])
                else:
                    result = deck.desktop(operation, request)
                payload = encode({"ok": True, **result})
            except (KeyError, TypeError, ValueError, OSError) as error:
                payload = encode({"ok": False, "error": str(error)})
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    marker = deck.directory / "desktop.json"
    write_json(marker, {"port": server.server_port, "token": protected(token.encode())})
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, marker


def desktop_request(data, op, **fields):
    config = json.loads((data / "desktop.json").read_bytes())
    token = unprotected(config["token"]).decode("ascii")
    from http.client import HTTPConnection
    with_local = HTTPConnection("127.0.0.1", int(config["port"]), timeout=20)
    try:
        with_local.request("POST", "/desktop", encode({"op": op, **fields}),
                           {"Content-Type": "application/json", "Authorization": "Bearer " + token})
        answer = with_local.getresponse()
        if answer.status != 200:
            raise OSError(tr(DEFAULT_LANG, "bridge_connect_error"))
        result = json.loads(answer.read())
    finally:
        with_local.close()
    if not result.get("ok"):
        raise ValueError(result.get("error") or tr(DEFAULT_LANG, "op_failed"))
    return result
