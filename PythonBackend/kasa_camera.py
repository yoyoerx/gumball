"""
Kasa KC410S camera interface — HTTPS multipart H.264 stream.

The KC410S does not use RTSP. It exposes an HTTPS endpoint on port 19443:
  https://<ip>:19443/https/stream/mixed?video=h264&audio=g711&resolution=hd
Authentication: HTTP Basic Auth with Kasa account email + password.
Stream format: multipart/x-mixed-replace with per-frame H.264 NAL units.

Usage:
    cam = KasaCamera()
    if cam.connect():
        frame = cam.read_frame()   # returns BGR numpy array or None
        cam.release()
"""

import ssl
import logging
import base64
import threading

import av
import av.logging
import numpy as np
import requests
from requests.adapters import HTTPAdapter

import config

av.logging.set_level(av.logging.FATAL)

log = logging.getLogger(__name__)

BOUNDARY = b"--data-boundary--"

def _stream_path() -> str:
    """Build stream URL path from config.  Drop audio to avoid parsing
    interleaved G.711 chunks that add overhead without benefit."""
    res = getattr(config, "KASA_STREAM_TYPE", "hd")
    return f"/https/stream/mixed?video=h264&resolution={res}"


class _LegacySSL(HTTPAdapter):
    """Allow TLS 1.0 — KC410S firmware uses an old SSL stack."""
    def init_poolmanager(self, *args, **kwargs):
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        ctx.check_hostname = False
        ctx.verify_mode    = ssl.CERT_NONE
        ctx.set_ciphers("DEFAULT:@SECLEVEL=0")
        try:
            ctx.minimum_version = ssl.TLSVersion.TLSv1
        except AttributeError:
            pass
        try:
            ctx.options |= getattr(ssl, "OP_LEGACY_SERVER_CONNECT", 0)
        except Exception:
            pass
        kwargs["ssl_context"] = ctx
        super().init_poolmanager(*args, **kwargs)


class KasaCamera:
    def __init__(self, host=None, username=None, password=None):
        self.host     = host     or config.KASA_CAMERA_IP
        self.username = username or config.KASA_USERNAME
        self.password = password or config.KASA_PASSWORD

        self._session:  requests.Session | None = None
        self._response: requests.Response | None = None
        self._iter      = None   # single iter_content generator, never recreated
        self._codec:    av.CodecContext   | None = None
        self._buf       = b""
        self._lock      = threading.Lock()
        self._open      = False

    # ── Public API ─────────────────────────────────────────────────────────────

    @property
    def stream_url(self) -> str:
        return f"https://{self.host}:19443{_stream_path()}"

    def connect(self) -> bool:
        url = self.stream_url
        log.info(f"[KasaCamera] Connecting -> {url}")

        session = requests.Session()
        session.mount("https://", _LegacySSL())

        auth_header = "Basic " + base64.b64encode(
            f"{self.username}:{self.password}".encode()
        ).decode()

        try:
            import urllib3
            urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

            r = session.get(
                url,
                headers={"Authorization": auth_header},
                verify=False,
                stream=True,
                timeout=10,
            )

            if r.status_code != 200:
                log.error(f"[KasaCamera] HTTP {r.status_code} — check credentials.")
                return False

            ct = r.headers.get("Content-Type", "")
            if "multipart" not in ct:
                log.error(f"[KasaCamera] Unexpected Content-Type: {ct}")
                return False

            self._session  = session
            self._response = r
            self._iter     = r.iter_content(chunk_size=65536)
            self._codec    = av.CodecContext.create("h264", "r")
            self._open     = True
            log.info("[KasaCamera] Connected — H.264 multipart stream open.")
            return True

        except Exception as e:
            log.error(f"[KasaCamera] Connect failed: {e}")
            return False

    def read_frame(self) -> np.ndarray | None:
        """
        Parse one H.264 frame from the HTTPS multipart stream.
        Returns a BGR numpy array, or None on connection loss/EOF.
        Decode errors on individual P-frames are skipped non-fatally.
        """
        if not self._open or self._response is None:
            return None

        for _ in range(16):   # try up to 16 chunks to get one decoded frame
            try:
                h264_data = self._read_next_chunk()
            except Exception as e:
                log.warning(f"[KasaCamera] stream read error: {e}")
                self._open = False
                return None

            if h264_data is None:
                self._open = False
                return None

            try:
                packet = av.Packet(h264_data)
                frames = self._codec.decode(packet)
                for frame in frames:
                    return frame.to_ndarray(format="bgr24")
            except av.FFmpegError as e:
                log.debug(f"[KasaCamera] decode skip (P-frame?): {e}")
                continue   # non-fatal — try next chunk

        return None

    def is_open(self) -> bool:
        return self._open

    def release(self):
        self._open = False
        if self._response:
            try:
                self._response.close()
            except Exception:
                pass
            self._response = None
        if self._session:
            self._session.close()
            self._session = None
        log.info("[KasaCamera] Released.")

    # ── Multipart stream parser ────────────────────────────────────────────────

    def _fill_buf(self, n: int) -> bool:
        """Read from the HTTP stream until _buf has at least n bytes."""
        while len(self._buf) < n:
            try:
                chunk = next(self._iter)
                self._buf += chunk
            except StopIteration:
                return False
        return True

    def _read_next_chunk(self) -> bytes | None:
        """
        Consume one multipart part and return its raw payload bytes.
        Format:
            --data-boundary--\r\n
            Header: value\r\n
            ...\r\n
            \r\n
            <Content-Length bytes of H.264 data>
        """
        # Locate boundary
        while True:
            idx = self._buf.find(BOUNDARY)
            if idx != -1:
                self._buf = self._buf[idx + len(BOUNDARY):]
                break
            if not self._fill_buf(len(self._buf) + 65536):
                return None

        # Skip CRLF after boundary
        while not self._buf.startswith(b"\r\n"):
            if not self._fill_buf(len(self._buf) + 2):
                return None
        self._buf = self._buf[2:]

        # Parse headers until blank line
        content_length = 0
        while True:
            end = self._buf.find(b"\r\n")
            if end == -1:
                if not self._fill_buf(len(self._buf) + 256):
                    return None
                continue
            line = self._buf[:end]
            self._buf = self._buf[end + 2:]
            if line == b"":
                break
            if line.lower().startswith(b"content-length:"):
                content_length = int(line.split(b":", 1)[1].strip())

        if content_length == 0:
            return None

        # Read payload
        while len(self._buf) < content_length:
            if not self._fill_buf(content_length):
                return None

        payload = self._buf[:content_length]
        self._buf = self._buf[content_length:]
        return payload

    # ── Device discovery (unchanged) ───────────────────────────────────────────

    @staticmethod
    async def discover(timeout: float = 5.0) -> str | None:
        try:
            from kasa import Discover
        except ImportError:
            log.warning("[KasaCamera] python-kasa not installed.")
            return None

        devices = await Discover.discover(timeout=timeout)
        for ip, dev in devices.items():
            try:
                await dev.update()
                model = getattr(dev, "model", "")
                alias = getattr(dev, "alias", "")
                if "KC" in model.upper() or "camera" in alias.lower():
                    return ip
            except Exception:
                pass
        return None
