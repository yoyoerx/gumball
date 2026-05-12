# Reverse Engineering the TP-Link Kasa KC410S: Extracting a Live Video Stream Without RTSP

*How we discovered the camera's hidden HTTPS streaming API and built a Python client for it*

---

## Standing on the Shoulders of Giants

This investigation would not have started in the right direction without [Reverse Engineering TP-Link KC100](https://medium.com/@hu3vjeen/reverse-engineering-tp-link-kc100-bac4641bf1cd) by **hu3vjeen** on Medium. That article documented the KC100's unusual HTTPS-based streaming protocol — a finding we confirmed applies equally to the KC410S, TP-Link's newer pan-tilt model.

Key differences between the two cameras worth noting:

| | KC100 | KC410S |
|---|---|---|
| Pan/tilt | Fixed | Yes (motorized) |
| Resolution | 1080p | 2K (1440p) |
| Stream endpoint | Port 19443 | Port 19443 ✓ |
| Auth | HTTP Basic | HTTP Basic ✓ |
| Credentials | Local admin password | **Kasa cloud email + password** |

The credential difference is significant — the KC410S uses your Kasa account credentials rather than a local device password. Keep reading for details.

---

## The Problem

I was building a VR application for Meta Quest 3 that needed a live video feed from a TP-Link Kasa KC410S security camera. The standard approach for IP cameras is RTSP — a well-known protocol that OpenCV handles natively with `cv2.VideoCapture("rtsp://...")`.

The problem: the KC410S has no RTSP option. None at all. The Kasa app settings don't offer it, and connecting directly to `rtsp://192.168.1.199:554/stream1` produces a 30-second timeout.

---

## Step 1: Confirm the Camera Is on the Network

```bash
ping 192.168.1.199
```

Reply from 192.168.1.199 — 2ms RTT. The camera is there. It's just not talking RTSP.

---

## Step 2: Port Scan

A TCP scan of ports 1–10000 reveals that standard ports (80, 443, 554, 8080) are all closed. But three unusual HTTPS ports are open, exactly as hu3vjeen found on the KC100:

| Port | Protocol |
|------|----------|
| 10443 | HTTPS |
| 18443 | HTTPS |
| **19443** | **HTTPS (streaming)** |

Port 9999 is also open — TP-Link's proprietary Kasa UDP discovery protocol, which the `python-kasa` library uses for device management.

```python
# Quick Python port check
import socket

for port in [10443, 18443, 19443, 9999]:
    try:
        s = socket.create_connection(("192.168.1.199", port), timeout=2)
        s.close()
        print(f"OPEN  {port}")
    except Exception:
        print(f"closed {port}")
```

---

## Step 3: The TLS Compatibility Problem

Python 3.10+ enforces minimum TLS 1.2. The KC410S firmware (like the KC100) uses an older TLS stack (TLS 1.0/1.1). A direct `urllib.request` call fails with `SSLV3_ALERT_HANDSHAKE_FAILURE`.

The fix: a custom `requests` SSL adapter that dials the security level back:

```python
import ssl
import requests
from requests.adapters import HTTPAdapter

class LegacySSLAdapter(HTTPAdapter):
    def init_poolmanager(self, *args, **kwargs):
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        ctx.check_hostname = False
        ctx.verify_mode    = ssl.CERT_NONE
        ctx.set_ciphers("DEFAULT:@SECLEVEL=0")
        ctx.minimum_version = ssl.TLSVersion.TLSv1
        ctx.options |= getattr(ssl, "OP_LEGACY_SERVER_CONNECT", 0)
        kwargs["ssl_context"] = ctx
        super().init_poolmanager(*args, **kwargs)

session = requests.Session()
session.mount("https://", LegacySSLAdapter())
```

---

## Step 4: Finding the Stream Endpoint

With TLS working, we probe port 19443. Most paths return 404. But one path returns **401 Unauthorized** — meaning the endpoint exists, it just needs credentials:

```
GET https://192.168.1.199:19443/https/stream/mixed?video=h264&audio=g711&resolution=hd
→ 401 Unauthorized
```

---

## Step 5: Authentication

This is where the KC410S diverges from the KC100. The KC100 uses a local device password. The KC410S uses your **Kasa cloud account email and password** as HTTP Basic Auth credentials.

> **Important:** The `@` symbol in an email address is a URL delimiter. If you embed credentials directly in a URL string, the `@` will be parsed as the user/host separator and break the URL. Always pass credentials via the `Authorization` header, not the URL.

```python
import base64

email    = "your@email.com"
password = "yourKasaPassword"

auth_header = "Basic " + base64.b64encode(f"{email}:{password}".encode()).decode()
```

With this header, port 19443 returns **HTTP 200** and starts streaming.

---

## Step 6: The Stream Format

The response `Content-Type` is:

```
multipart/x-mixed-replace;boundary=data-boundary--
```

Each part contains one H.264 video frame:

```
--data-boundary--
Content-Type: video/x-h264
Content-Length: 82261
X-UtcTime: 1778367331
X-FrameType: 0
X-FrameRate: 15.0
X-Audio: 1

[raw H.264 Annex-B NAL unit bytes — starts with 0x00 0x00 0x00 0x01 ...]
```

This is **not MJPEG**. It's raw H.264 Annex-B bitstream delivered frame-by-frame over HTTPS multipart. OpenCV's `VideoCapture` can't read this format directly — you need an H.264 decoder.

---

## Step 7: Decoding H.264 Frames with PyAV

[PyAV](https://pyav.org) (`pip install av`) is a Python binding for FFmpeg. It decodes individual H.264 NAL unit packets directly:

```python
import av
import av.logging

av.logging.set_level(av.logging.FATAL)   # suppress verbose NAL warnings

codec = av.CodecContext.create("h264", "r")

# For each chunk extracted from the multipart stream:
packet = av.Packet(h264_data)
frames = codec.decode(packet)
for frame in frames:
    bgr_array = frame.to_ndarray(format="bgr24")  # numpy array for OpenCV
```

**Important**: The first few packets may not produce decoded frames (the decoder needs the SPS/PPS keyframe to initialize — `X-FrameType: 0`). Subsequent P-frames occasionally trigger benign `av.FFmpegError` exceptions. Catch and skip them rather than tearing down the connection:

```python
try:
    frames = codec.decode(av.Packet(h264_data))
    for frame in frames:
        return frame.to_ndarray(format="bgr24")
except av.FFmpegError:
    continue   # non-fatal — try next chunk
```

Also critical: **store the `iter_content()` generator once and reuse it**. Calling `response.iter_content()` multiple times does not rewind the HTTP stream; it creates a new generator reading from the current socket position, leading to unpredictable behaviour. Always do this:

```python
self._iter = response.iter_content(chunk_size=65536)  # store once in connect()

# Then in your read loop:
chunk = next(self._iter)   # NOT: next(response.iter_content(...))
```

---

## Full Python Client

```python
import ssl, base64, av, av.logging, numpy as np, requests
from requests.adapters import HTTPAdapter

av.logging.set_level(av.logging.FATAL)

class LegacySSL(HTTPAdapter):
    def init_poolmanager(self, *args, **kwargs):
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        ctx.set_ciphers("DEFAULT:@SECLEVEL=0")
        ctx.minimum_version = ssl.TLSVersion.TLSv1
        ctx.options |= getattr(ssl, "OP_LEGACY_SERVER_CONNECT", 0)
        kwargs["ssl_context"] = ctx
        super().init_poolmanager(*args, **kwargs)

CAMERA_IP  = "192.168.1.199"
USERNAME   = "your@email.com"       # Kasa cloud account email
PASSWORD   = "yourKasaPassword"     # Kasa cloud account password
STREAM_URL = (f"https://{CAMERA_IP}:19443"
              "/https/stream/mixed?video=h264&audio=g711&resolution=hd")
BOUNDARY   = b"--data-boundary--"


class KasaCamera:
    def __init__(self):
        self._session = requests.Session()
        self._session.mount("https://", LegacySSL())
        self._response = None
        self._iter     = None
        self._buf      = b""
        self._codec    = av.CodecContext.create("h264", "r")
        self._open     = False

    def connect(self) -> bool:
        auth = "Basic " + base64.b64encode(f"{USERNAME}:{PASSWORD}".encode()).decode()
        import urllib3; urllib3.disable_warnings()
        r = self._session.get(STREAM_URL,
                              headers={"Authorization": auth},
                              verify=False, stream=True, timeout=10)
        if r.status_code != 200:
            return False
        self._response = r
        self._iter     = r.iter_content(chunk_size=65536)   # store once!
        self._open     = True
        return True

    def read_frame(self) -> np.ndarray | None:
        if not self._open:
            return None
        for _ in range(16):
            try:
                chunk = self._next_chunk()
                if chunk is None:
                    self._open = False
                    return None
                frames = self._codec.decode(av.Packet(chunk))
                for f in frames:
                    return f.to_ndarray(format="bgr24")
            except av.FFmpegError:
                continue   # skip P-frames that fail to decode
        return None

    def is_open(self) -> bool:
        return self._open

    def release(self):
        self._open = False
        if self._response:
            self._response.close()

    # ── Multipart stream parser ──────────────────────────────────────────────

    def _fill(self, n: int) -> bool:
        while len(self._buf) < n:
            try:
                self._buf += next(self._iter)
            except StopIteration:
                return False
        return True

    def _next_chunk(self) -> bytes | None:
        # Find boundary
        while True:
            idx = self._buf.find(BOUNDARY)
            if idx != -1:
                self._buf = self._buf[idx + len(BOUNDARY):]
                break
            if not self._fill(len(self._buf) + 65536):
                return None

        # Skip \r\n after boundary
        if not self._fill(2):
            return None
        self._buf = self._buf[2:]

        # Parse headers
        length = 0
        while True:
            end = self._buf.find(b"\r\n")
            if end == -1:
                if not self._fill(len(self._buf) + 256):
                    return None
                continue
            line = self._buf[:end]
            self._buf = self._buf[end + 2:]
            if not line:
                break
            if line.lower().startswith(b"content-length:"):
                length = int(line.split(b":", 1)[1].strip())

        if not length or not self._fill(length):
            return None

        payload       = self._buf[:length]
        self._buf     = self._buf[length:]
        return payload
```

---

## Key Takeaways

1. **No RTSP** — The KC410S (and KC100) expose no RTSP endpoint. Don't waste time scanning port 554.
2. **HTTPS on port 19443** — This is the streaming port. Ports 10443 and 18443 are open but serve other purposes.
3. **Cloud credentials, not local** — The KC410S authenticates with your Kasa account email + password via HTTP Basic Auth. The KC100 uses a local device password instead.
4. **Old TLS stack** — Lower Python's minimum TLS version to connect; the firmware is TLS 1.0.
5. **H.264 multipart, not MJPEG** — Each part is a raw H.264 Annex-B frame. Decode with PyAV (FFmpeg binding).
6. **Store the iterator once** — Never call `iter_content()` more than once on the same response object.
7. **P-frame errors are non-fatal** — Catch `av.FFmpegError` per-packet and skip; don't tear down the connection.

---

## Credits

Protocol discovery method adapted from [Reverse Engineering TP-Link KC100](https://medium.com/@hu3vjeen/reverse-engineering-tp-link-kc100-bac4641bf1cd) by hu3vjeen. The port layout (10443/18443/19443), multipart stream format, and TLS workaround all originate from that work.
