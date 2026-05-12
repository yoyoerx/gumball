# Reverse Engineering the TP-Link Kasa KC410S: Pan/Tilt Control Without an API

*Part 2 of building a VR gimbal system with Meta Quest 3 — this time we crack open the APK to make a security camera move*

---

In [Part 1](https://medium.com/@yoyoerx/reverse-engineering-the-tp-link-kasa-kc410s-extracting-a-live-video-stream-without-rtsp-7e703f4b13fe) we found that the KC410S exposes no RTSP endpoint — connecting to port 554 just times out. Instead, the camera streams raw H.264 frames over HTTPS multipart on port 19443 using your Kasa cloud credentials as HTTP Basic auth. After reverse-engineering that protocol and decoding the H.264 bitstream with PyAV, we had live video flowing into a Meta Quest 3 headset over WebSockets.

The camera was on the wall, the video was in VR, but the pan-tilt motors were completely inert. You could only see whatever the camera happened to be pointed at when it was mounted.

The goal for this part was simple to state and hard to execute: **make the camera physically pan and tilt from Python**, so the VR client can steer it.

TP-Link doesn't publish a local API for the KC410S. The Kasa app controls it, but through what protocol? That's what we had to find out.

---

## The Plan: Read the App

The Kasa Android app is a black box from the outside, but APKs are just ZIP files containing compiled Java bytecode. Tools like `apktool` can disassemble that bytecode back into Smali — a human-readable representation of Dalvik VM instructions. It's not source code, but it's close enough to understand data structures and protocol logic.

```bash
apktool d kasa.apk -o smali_full
```

That produced 90,409 Smali files. Somewhere in there was everything we needed.

---

## Dead End: The Cloud API

The first lead came from `assets/sdkconfig.xml`, which contained the base URL for TP-Link's cloud service:

```xml
<TPLINKAppServer>
  <BaseURL>https://n-wap.tplinkcloud.com</BaseURL>
  <AppType>Android_kasa</AppType>
  <AccessKey>e37525375f8845999bcc56d5e6faa76d</AccessKey>
  <SecretKey>314bc6700b3140ca80bc655e527cb062</SecretKey>
</TPLINKAppServer>
```

The app uses a v2 cloud API — `POST /api/v2/account/login`, then `/api/v2/common/getDeviceList`, then `/api/v2/common/passthrough` to relay commands to the camera. Each request is signed with an HMAC-SHA1 signature computed from the request body, a hardcoded timestamp (`9999999999` — yes, literally), and a nonce.

The signing algorithm came from `AppServerHttpClientHelper.smali`:

```
Content-MD5: Base64(MD5(body_utf8))          ← NOT hex, Base64
Signature:   HmacSHA1(md5 + "\n" + ts + "\n" + nonce + "\n" + path, secret_key)
X-Authorization: TimeStamp=9999999999, Nonce=<uuid>, AccessKey=<key>, Signature=<hex>
```

One subtle trap: an early implementation used `hashlib.md5(...).hexdigest()` for the Content-MD5 header. The server returned `-10301 Signature does not match`. Reading `kb0.a.smali` revealed that `getContentMD5()` calls `Base64.encodeToString(bytes, 2)` — Base64 with the `NO_WRAP` flag, not hex. Fixing that got past the signature check.

But then the real wall appeared: `-20708 Authentication failed`. The credentials were correct — we verified them by logging into the Play Store app on mobile data. The cloud endpoint was simply rejecting our PC's IP, likely due to rate limiting from earlier testing. No amount of retrying from different connections resolved it.

The cloud path was abandoned.

---

## The Discovery: Local Protocol

While the cloud API was failing, we noticed the installed `python-kasa` library on the dev machine contains its own transport implementations. One of them — `kasa/transports/linkietransport.py` — was exactly what we needed:

```python
# from python-kasa source (paraphrased)
encrypted_cmd = XorEncryption.encrypt(request)[4:]   # strip 4-byte length header
b64_cmd = base64.b64encode(encrypted_cmd).decode()
url_safe_cmd = quote(b64_cmd, safe="!~*'()")
data = f"content={url_safe_cmd}".encode()
# POST to https://<ip>:10443/data/LINKIE2.json
```

The KC410S runs an HTTPS server on port 10443 — the same port we already knew about from the streaming article, but serving a completely different purpose. It speaks a local protocol called **Linkie** — no cloud, no tokens, no signing. Just an HTTP POST with a XOR-encrypted JSON body.

The XOR cipher is a simple streaming cipher with IV `0xAB`:

```python
def xor_encrypt(data: bytes) -> bytes:
    key = 0xAB
    out = bytearray()
    for b in data:
        key ^= b
        out.append(key)
    return bytes(out)

def xor_decrypt(data: bytes) -> bytes:
    key = 0xAB
    out = bytearray()
    for b in data:
        out.append(key ^ b)
        key = b
    return bytes(out)
```

The encrypt function normally prepends a 4-byte big-endian length header. The transport strips those 4 bytes before sending, so only the raw XOR-encrypted payload goes over the wire. The camera's response is the same format in reverse: base64-decode, XOR-decrypt, JSON-parse.

The full request encoding:

```python
def encode(cmd: dict) -> bytes:
    raw      = json.dumps(cmd, separators=(",", ":")).encode()
    enc      = xor_encrypt(raw)
    b64      = base64.b64encode(enc).decode()
    url_safe = quote(b64, safe="!~*'()")
    return f"content={url_safe}".encode()
```

---

## Finding the Command Structure

With the transport layer understood, we still needed to know what JSON to send. The Smali files contained the answer in `LinkieCameraCommand.smali` — a Gson-serialized Java class that maps to the over-the-wire JSON format.

The outer key for PTZ commands came from a `@SerializedName` annotation:

```smali
.field public panTiltZoomControl:Lcom/.../LinkieCameraCommand$PanTiltZoomControl;
    .annotation runtime Lcom/google/gson/annotations/SerializedName;
        value = "smartlife.cam.ipcamera.ptz"
    .end annotation
```

Inside `LinkieCameraCommand$PanTiltZoomControl.smali`, the method fields:

```smali
.field public set_move:...StartMovingDevice;
.field public set_stop:...StopMovingDevice;
.field public set_target:...MoveToPosition;
.field public get_position:...GetCurrentPosition;
```

And `StartMovingDevice.smali` told us the parameter names:

```smali
.field public direction:Ljava/lang/String;
.field public speed:Ljava/lang/Integer;
```

The direction values came from `PanTiltZoomMoveDirection.smali` — an enum that maps Java constants to wire strings:

```smali
const-string v2, "top"       # TOP
const-string v2, "bottom"    # BOTTOM
const-string v2, "left"      # LEFT
const-string v2, "right"     # RIGHT
const-string v2, "top left"  # TOP_LEFT
const-string v2, "top right" # TOP_RIGHT
...
```

So a "move right" command looks like:

```json
{
  "smartlife.cam.ipcamera.ptz": {
    "set_move": { "direction": "right", "speed": 5 }
  }
}
```

And stop:

```json
{
  "smartlife.cam.ipcamera.ptz": {
    "set_stop": {}
  }
}
```

---

## Three More Bugs to Squash

Getting the transport and command structure right still left three problems to solve.

### Bug 1: SSL Handshake Failure

The first request to port 10443 died immediately:

```
SSLError: [SSL: SSLV3_ALERT_HANDSHAKE_FAILURE] sslv3 alert handshake failure
```

This is the same issue from Part 1 — the KC410S runs old firmware with a weak TLS configuration that Python 3.10+'s default SSL context rejects. The same `LegacySSLAdapter` pattern applies here:

```python
class _LegacyTLSAdapter(HTTPAdapter):
    def init_poolmanager(self, *args, **kwargs):
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        ctx.check_hostname = False
        ctx.verify_mode    = ssl.CERT_NONE
        ctx.set_ciphers("DEFAULT:@SECLEVEL=0")
        ctx.minimum_version = ssl.TLSVersion.TLSv1
        kwargs["ssl_context"] = ctx
        super().init_poolmanager(*args, **kwargs)

session = requests.Session()
session.mount("https://", _LegacyTLSAdapter())
```

### Bug 2: Wrong Credentials

The python-kasa library documents `DEFAULT_CREDENTIALS["KASACAMERA"]` as `admin` with password `21232f297a57a5a743894a0e4a801fc3` — the MD5 hash of the string "admin". The camera returns `-10007 Unauthorized Error` for those.

A credential sweep against the live camera identified what actually works:

| Username | Password | Result |
|---|---|---|
| `admin` | `21232f297a57a5a743894a0e4a801fc3` | `-10007 Unauthorized` |
| `admin` | `admin` | `-10007 Unauthorized` |
| `your@email.com` | `yourKasaPassword` | ✅ `err_code: 0` |
| `admin` | MD5(`yourKasaPassword`) | ✅ `err_code: 0` |

The same Kasa cloud credentials that authenticate the streaming endpoint (port 19443) also authenticate the control endpoint (port 10443). The camera adopted those credentials when it was paired with the Kasa account during setup.

### Bug 3: Speed Out of Range

The first successful `set_move` command sent `speed=50`. Response:

```json
{"err_code": -41203, "err_msg": "The parameter value error."}
```

Testing a range of values revealed the camera enforces **speed 1–10**. Values above 10 are rejected. The API accepts no percentage scale — it's a fixed 1-to-10 integer.

```python
# Camera firmware enforces 1-10. Clamp unconditionally.
speed = max(1, min(10, speed))
```

In the WebSocket server, the PTZ handler clamps the incoming value to the valid range:

```python
speed = max(1, min(10, int(data.get("speed", 5))))  # camera enforces 1-10
```

The Unity side produces a value already in the 1–10 range by scaling joystick magnitude against a configurable `maxRate`:

```csharp
int speed = Mathf.Clamp(Mathf.RoundToInt(mag / maxRate * 10), 1, 10);
```

Don't divide by 10 on the server as well — that collapses the entire range to 1.

---

## The Final Implementation

```python
"""
KC410S pan/tilt motor control via local Linkie protocol (no cloud required).

URL:  https://<camera_ip>:10443/data/LINKIE2.json
Auth: HTTP Basic — Kasa account email + plain password
Body: content=<url_safe_base64(xor_encrypt(json_cmd))>
XOR:  IV=0xAB streaming cipher
"""

DIRECTIONS = frozenset({
    "top", "bottom", "left", "right",
    "top left", "top right", "bottom left", "bottom right",
})

class KasaMotorControl:
    def __init__(self):
        self._session = requests.Session()
        self._session.mount("https://", _LegacyTLSAdapter())
        self._auth = (config.KASA_USERNAME, config.KASA_PASSWORD)

    def login(self) -> bool:
        return True  # no-op — local Linkie needs no token

    def start_moving(self, direction: str, speed: int = 5) -> bool:
        if direction not in DIRECTIONS:
            raise ValueError(f"Invalid direction {direction!r}")
        speed = max(1, min(10, speed))
        return self._send({
            "smartlife.cam.ipcamera.ptz": {
                "set_move": {"direction": direction, "speed": speed}
            }
        })

    def stop_moving(self) -> bool:
        return self._send({"smartlife.cam.ipcamera.ptz": {"set_stop": {}}})

    def move_to_position(self, x: int, y: int) -> bool:
        return self._send({
            "smartlife.cam.ipcamera.ptz": {"set_target": {"x": x, "y": y}}
        })

    def get_position(self) -> dict | None:
        resp = self._send_raw({"smartlife.cam.ipcamera.ptz": {"get_position": {}}})
        if resp is None:
            return None
        return resp.get("smartlife.cam.ipcamera.ptz", {}).get("get_position", {})
```

And the smoke test output that confirmed it all worked:

```
Target: https://192.168.1.199:10443/data/LINKIE2.json
Current position: {'x': -49, 'y': 237, 'err_code': 0}
Moving RIGHT for 1 second...
Done. New position: {'x': 34, 'y': 237, 'err_code': 0}
```

x moved from `-49` to `34` in one second. The camera physically panned right. y stayed at `237` — the tilt didn't change, as expected.

---

## Integration with the VR Backend

The PTZ control plugs into `server.py` alongside the existing detection and stream WebSocket servers. A third endpoint on port 8082 accepts JSON commands from Unity:

```python
# Unity sends:
# {"cmd": "ptz_start", "direction": "right", "speed": 7}   # 1-10
# {"cmd": "ptz_stop"}
# {"cmd": "ptz_goto", "x": 78, "y": 281}
```

Motor initialization runs in a daemon thread so it doesn't block server startup. Because local Linkie needs no cloud token, `login()` returns immediately and the motor is ready before the first client connects.

```python
def _motor_init_thread():
    global _motor_ready
    if motor.login():       # returns True instantly
        _motor_ready = True
        log.info("[PTZ] Motor ready.")

threading.Thread(target=_motor_init_thread, daemon=True).start()
ptz_server = websockets.serve(handle_ptz, config.SERVER_HOST, config.PTZ_WS_PORT)
```

---

## Key Takeaways

1. **Port 10443 is dual-purpose** — the same port that appears in the streaming article also hosts the Linkie control API, but at a completely different path (`/data/LINKIE2.json`).
2. **python-kasa is an underrated reference** — before decompiling an APK, check if someone has already done it and shipped it as a pip package. `linkietransport.py` contained the full protocol.
3. **Smali is readable enough for data structures** — `@SerializedName` annotations, field declarations, and enum constructors come through almost as cleanly as the original Java.
4. **The cloud API is a dead end** — rate limiting makes it unreliable for development, and the local protocol is strictly better for LAN use: no tokens, no signing, sub-10ms round trips.
5. **Same credentials, same TLS problem** — the auth and SSL quirks from Part 1 reappear unchanged on the control endpoint. Reuse the same adapter and credentials.
6. **Speed is 1–10, not 0–100** — the camera silently rejects values outside this range with `-41203`. Always probe the actual accepted range rather than assuming a percentage scale.

---

## What's Next

The camera is now streaming video to the Quest 3 and accepting pan/tilt commands from Python. The next problem is object detection — specifically getting YOLOv8 running both on the PC backend (for high accuracy) and as an ONNX model directly on the Quest 3 (for low latency), and wiring the detected bounding boxes to the camera's PTZ controls so the gimbal can track a subject automatically.

That's part 3.

---

## Addendum: Discovering the Camera IP Automatically

The implementation above hardcodes the camera's IP address. That works fine for a fixed installation, but it breaks the moment the router assigns a different address — or when you want to run the same code against a different camera on the network.

The KC410S exposes port 19443 (the HTTPS stream endpoint from Part 1) regardless of whether anyone is connected to it. That makes it a reliable fingerprint for a local discovery scan.

```python
import socket
from concurrent.futures import ThreadPoolExecutor

CAMERA_PORT = 19443
_WORKERS    = 64
_TIMEOUT    = 0.4

def _probe(ip: str) -> bool:
    try:
        with socket.create_connection((ip, CAMERA_PORT), timeout=_TIMEOUT):
            return True
    except OSError:
        return False

def discover_cameras(subnet: str = None) -> list[str]:
    if subnet is None:
        # Auto-detect local subnet
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(("8.8.8.8", 80))
            subnet = ".".join(s.getsockname()[0].split(".")[:3])

    candidates = [f"{subnet}.{i}" for i in range(1, 255)]
    with ThreadPoolExecutor(max_workers=_WORKERS) as pool:
        hits = [ip for ip, ok in zip(candidates, pool.map(_probe, candidates)) if ok]
    return hits
```

A 64-thread pool with a 0.4-second timeout scans the full /24 in about two to three seconds. Port 19443 isn't common enough to produce false positives on a typical home network.

The result feeds a small settings file that `config.py` reads at import time:

```python
# config.py
import json, os
_s = {}
try:
    with open(os.path.join(os.path.dirname(__file__), "settings.json")) as f:
        _s = json.load(f)
except Exception:
    pass

KASA_CAMERA_IP = _s.get("camera_ip", "192.168.1.199")
```

```json
// settings.json (written by discovery UI or camera selection panel)
{"camera_ip": "192.168.1.199"}
```

When the user selects a camera the new IP is written to `settings.json`. The server reads it on next startup — no code changes required to switch between cameras.

---

*Protocol discovery method for the streaming endpoint adapted from [Reverse Engineering TP-Link KC100](https://medium.com/@hu3vjeen/reverse-engineering-tp-link-kc100-bac4641bf1cd) by hu3vjeen.*
