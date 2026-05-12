"""
KC410S pan/tilt motor control via local Linkie protocol (no cloud required).

Protocol from python-kasa linkietransport.py:
  URL:  https://<camera_ip>:10443/data/LINKIE2.json
  Auth: HTTP Basic  admin : 21232f297a57a5a743894a0e4a801fc3  (MD5 of "admin")
  Body: content=<url_safe_base64(xor_encrypt(json_cmd))>
  XOR:  IV=0xAB streaming cipher — encrypt()[4:] strips the length header
"""

import base64
import json
import logging
import ssl
from urllib.parse import quote

import requests
import urllib3
from requests.adapters import HTTPAdapter

import config

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)
log = logging.getLogger(__name__)


class _LegacyTLSAdapter(HTTPAdapter):
    """Allow the camera's old TLS stack (low cipher security level, no cert check)."""
    def init_poolmanager(self, *args, **kwargs):
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        ctx.check_hostname = False
        ctx.verify_mode    = ssl.CERT_NONE
        ctx.set_ciphers("DEFAULT:@SECLEVEL=0")
        try:
            ctx.minimum_version = ssl.TLSVersion.TLSv1
        except AttributeError:
            pass
        kwargs["ssl_context"] = ctx
        super().init_poolmanager(*args, **kwargs)

_XOR_IV = 0xAB

DIRECTIONS = frozenset({
    "top", "bottom", "left", "right",
    "top left", "top right", "bottom left", "bottom right",
})


def _xor_encrypt(data: bytes) -> bytes:
    key = _XOR_IV
    out = bytearray()
    for b in data:
        key ^= b
        out.append(key)
    return bytes(out)


def _xor_decrypt(data: bytes) -> bytes:
    key = _XOR_IV
    out = bytearray()
    for b in data:
        out.append(key ^ b)
        key = b
    return bytes(out)


def _encode(cmd: dict) -> bytes:
    raw      = json.dumps(cmd, separators=(",", ":")).encode()
    enc      = _xor_encrypt(raw)
    b64      = base64.b64encode(enc).decode()
    url_safe = quote(b64, safe="!~*'()")
    return f"content={url_safe}".encode()


def _decode(text: str) -> dict:
    raw       = base64.b64decode(text.strip())
    decrypted = _xor_decrypt(raw)
    try:
        return json.loads(decrypted)
    except json.JSONDecodeError:
        return json.loads(decrypted[4:])  # strip 4-byte length header if present


class KasaMotorControl:
    def __init__(self):
        self._session = requests.Session()
        self._session.mount("https://", _LegacyTLSAdapter())
        self._auth = (config.KASA_USERNAME, config.KASA_PASSWORD)
        self._url  = f"https://{config.KASA_CAMERA_IP}:10443/data/LINKIE2.json"

    def set_camera_ip(self, ip: str) -> None:
        self._url = f"https://{ip}:10443/data/LINKIE2.json"
        log.info(f"[KasaMotor] Camera IP updated -> {ip}")

    def login(self) -> bool:
        """No-op — local Linkie uses HTTP Basic auth, always ready."""
        log.info(f"[KasaMotor] Local Linkie at {self._url} — no login needed.")
        return True

    # ── PTZ commands ──────────────────────────────────────────────────────────

    def start_moving(self, direction: str, speed: int = 5) -> bool:
        """direction: one of DIRECTIONS; speed: 1-10 (camera enforces this range)."""
        if direction not in DIRECTIONS:
            raise ValueError(f"Invalid direction {direction!r}")
        speed = max(1, min(10, speed))
        return self._send({"smartlife.cam.ipcamera.ptz": {"set_move": {"direction": direction, "speed": speed}}})

    def stop_moving(self) -> bool:
        return self._send({"smartlife.cam.ipcamera.ptz": {"set_stop": {}}})

    def move_to_position(self, x: int, y: int) -> bool:
        return self._send({"smartlife.cam.ipcamera.ptz": {"set_target": {"x": x, "y": y}}})

    def get_position(self) -> dict | None:
        resp = self._send_raw({"smartlife.cam.ipcamera.ptz": {"get_position": {}}})
        if resp is None:
            return None
        return resp.get("smartlife.cam.ipcamera.ptz", {}).get("get_position", {})

    # ── Internal ──────────────────────────────────────────────────────────────

    def _send(self, cmd: dict) -> bool:
        resp = self._send_raw(cmd)
        if resp is None:
            return False
        ptz = resp.get("smartlife.cam.ipcamera.ptz", {})
        for v in ptz.values():
            if isinstance(v, dict) and v.get("err_code", 0) != 0:
                log.warning(f"[KasaMotor] PTZ error: {v}")
                return False
        return True

    def _send_raw(self, cmd: dict) -> dict | None:
        try:
            r = self._session.post(
                self._url,
                data=_encode(cmd),
                auth=self._auth,
                headers={"Content-Type": "application/x-www-form-urlencoded"},
                timeout=8,
                verify=False,
            )
            r.raise_for_status()
            log.debug(f"[KasaMotor] raw response: {r.text!r}")
            return _decode(r.text)
        except Exception as e:
            log.error(f"[KasaMotor] Send error: {e}")
            return None

    def release(self):
        self._session.close()


# ── Smoke test ────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import time
    logging.basicConfig(level=logging.DEBUG, format="%(levelname)s %(message)s")

    motor = KasaMotorControl()
    print(f"Target: {motor._url}")

    pos = motor.get_position()
    print(f"Current position: {pos}")

    print("Moving RIGHT for 1 second...")
    if motor.start_moving("right", speed=5):
        time.sleep(1)
        motor.stop_moving()
        print(f"Done. New position: {motor.get_position()}")
    else:
        print("FAILED — check config.KASA_CAMERA_IP and that camera is on LAN")

    motor.release()
