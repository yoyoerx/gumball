# Wiring It All Together: Python Backend Server and Meta Quest 3 Connection

*How a three-endpoint WebSocket server bridges the Kasa KC410S camera and the VR headset*

---

In [Part 1](https://medium.com/@yoyoerx/reverse-engineering-the-tp-link-kasa-kc410s-extracting-a-live-video-stream-without-rtsp-7e703f4b13fe) we extracted a live H.264 video stream from the KC410S over HTTPS multipart. In [Part 2](https://medium.com/@yoyoerx) we reverse-engineered the Linkie protocol and built a Python client that physically moves the pan-tilt motors. Both pieces worked in isolation — Python could see the video and move the camera. The missing piece was getting both into a Meta Quest 3 headset in real time, with the right thumbstick controlling where the camera points.

That required a relay layer: a Python server running on the local PC that talks to the camera over HTTPS and exposes its services over WebSocket to the Quest app.

---

## The Architecture

```
┌────────────────────────────────────────────────────────────┐
│  Kasa KC410S  (192.168.1.199)                              │
│    :19443  HTTPS H.264 multipart stream                    │
│    :10443  HTTPS Linkie PTZ control                        │
└───────────────────────┬────────────────────────────────────┘
                        │  same LAN
┌───────────────────────▼────────────────────────────────────┐
│  PC  —  server.py  (192.168.1.222)                         │
│                                                            │
│    :8081  WebSocket  ←→  MJPEG stream relay               │
│    :8082  WebSocket  ←→  PTZ JSON commands                │
│    :9000  WebSocket  ←→  YOLOv8 detection results         │
└───────────────────────┬────────────────────────────────────┘
                        │  Wi-Fi
┌───────────────────────▼────────────────────────────────────┐
│  Meta Quest 3                                              │
│    VideoStreamReceiver   ws://192.168.1.222:8081           │
│    KasaPTZController     ws://192.168.1.222:8082           │
│    ServerDetectionClient ws://192.168.1.222:9000           │
└────────────────────────────────────────────────────────────┘
```

The server doesn't relay raw H.264 — it decodes each frame to a BGR NumPy array with PyAV, re-encodes it as JPEG, and broadcasts it to every connected WebSocket client. This keeps the Unity side simple: it just calls `LoadImage(jpegBytes)` on a Texture2D.

---

## Prerequisites

### Python packages

```bash
pip install websockets opencv-python av requests urllib3 ultralytics torch
```

PyAV (`av`) is the H.264 decoder. `websockets` 10+ is required — the server uses the async context manager form (`async with websockets.serve(...)`).

### Hardware

- Camera and PC **must be on the same LAN** (wired or Wi-Fi). The camera's HTTPS endpoints are LAN-only.
- Quest 3 must be on the **same Wi-Fi network** as the PC. Quest Developer Hub or side-loading is required to install the app.

---

## Configuration

All addresses and ports live in `config.py`. Open it and set `SERVER_HOST` to the **PC's LAN IP**, not `localhost`:

```python
# config.py
SERVER_HOST = "192.168.1.222"   # ← PC's LAN IP (Quest connects to this)
DETECTION_WS_PORT = 9000
STREAM_WS_PORT    = 8081
PTZ_WS_PORT       = 8082

KASA_CAMERA_IP = "192.168.1.199"
KASA_USERNAME  = "your@email.com"       # Kasa cloud account email
KASA_PASSWORD  = "yourKasaPassword"     # Kasa cloud account password
```

**Why `SERVER_HOST` matters:** The WebSocket servers bind to `SERVER_HOST`, not `0.0.0.0`. If it's set to `localhost` or `127.0.0.1`, the Quest won't be able to reach them.

To find the PC's LAN IP:

```powershell
# Windows
ipconfig | findstr "IPv4"

# or
(Get-NetIPAddress -AddressFamily IPv4 | Where InterfaceAlias -like "*Wi-Fi*").IPAddress
```

### Firewall

Windows Firewall blocks inbound connections by default. Open the three server ports:

```powershell
# Run as Administrator
New-NetFirewallRule -DisplayName "MetaGimbalVision" -Direction Inbound `
    -Protocol TCP -LocalPort 8081,8082,9000 -Action Allow
```

---

## Starting the Server

```bash
cd PythonBackend
python server.py
```

On a successful start with the camera reachable:

```
INFO [Server] Detection WS  → ws://192.168.1.222:9000
INFO [Server] Camera stream → ws://192.168.1.222:8081
INFO [Server] PTZ control   → ws://192.168.1.222:8082
INFO [KasaCamera] Connected — H.264 multipart stream open.
INFO [Stream] KC410S connected → ws port 8081
INFO [PTZ] Connecting to KC410S at 192.168.1.199 via local Linkie...
INFO [PTZ] Motor ready.
```

The stream relay and PTZ motor init each run in a daemon thread, so all three WebSocket servers are listening immediately. If the camera is offline, the stream thread logs a warning and retries every 5 seconds — the server itself stays up.

### What each thread does

| Thread | What it does |
|--------|-------------|
| `capture_loop` | Opens the HTTPS H.264 stream, decodes frames with PyAV, JPEG-encodes at up to 30 fps, broadcasts to all connected stream clients |
| `_motor_init_thread` | Calls `motor.login()` (a no-op for local Linkie), sets `_motor_ready = True` |
| asyncio event loop | Runs all three WebSocket servers concurrently |

---

## The Three Endpoints

### Port 8081 — Video Stream

The server broadcasts raw JPEG bytes as binary WebSocket messages. Clients do not send anything to this endpoint — it's push-only.

The relay throttles to 30 fps regardless of how fast the camera pushes frames, preventing the Quest from being flooded:

```python
_STREAM_FPS = 30
frame_interval = 1.0 / _STREAM_FPS
# ...
elapsed = time.monotonic() - t0
wait = frame_interval - elapsed
if wait > 0:
    time.sleep(wait)
```

If the camera drops the HTTPS connection, `capture_loop` releases the old session and reconnects after 5 seconds. Clients just stop receiving frames during the gap and resume when the stream recovers.

### Port 8082 — PTZ Control

Accepts JSON commands, returns JSON replies. The full command set:

| Command | Payload | Response |
|---------|---------|----------|
| `ptz_start` | `{"cmd":"ptz_start","direction":"right","speed":50}` | `{"ok":true,"motor_ready":true}` |
| `ptz_stop` | `{"cmd":"ptz_stop"}` | `{"ok":true,"motor_ready":true}` |
| `ptz_goto` | `{"cmd":"ptz_goto","x":78,"y":281}` | `{"ok":true,"motor_ready":true}` |
| `ptz_get_position` | `{"cmd":"ptz_get_position"}` | `{"ok":true,"x":78,"y":281}` |
| `ptz_rectify` | `{"cmd":"ptz_rectify"}` | `{"ok":true,"motor_ready":true}` |

`speed` maps the Quest controller's 0–100 range to the camera's 1–10 scale via `max(1, round(speed / 10))`. Values outside 1–10 return `err_code -41203` from the camera firmware, so the server clamps before sending.

`ptz_goto` uses the camera's internal coordinate system. The physical limits measured during development:

| Axis | Min | Max | Span |
|------|-----|-----|------|
| X (pan) | 39 | 117 | 78 units |
| Y (tilt) | 243 | 320 | 77 units |

`ptz_rectify` triggers `set_motor_rectify` — the camera sweeps its motors to find the physical hard stops and re-calibrates its internal coordinate zero. This takes ~5–10 seconds and blocks all other PTZ commands during that time (`err_code -41204` if you send a command mid-rectify). Use it if the camera loses its position reference after a power cycle.

### Port 9000 — Detection

Unity sends raw JPEG bytes; the server returns a JSON detection frame from YOLOv8. This endpoint is covered in detail in the next article.

---

## Unity Side: Stream Connection

`VideoStreamReceiver` opens a WebSocket to port 8081 and fires an event for each received binary message:

```csharp
// VideoStreamReceiver.cs (simplified)
[SerializeField] string streamUrl = "ws://192.168.1.222:8081";

_ws.OnMessage += frame => OnFrameReceived?.Invoke(frame);
```

`VRVideoDisplay` subscribes to that event and pushes the bytes into a `Texture2D`:

```csharp
// VRVideoDisplay.cs
private void UpdateTexture(byte[] jpegBytes)
{
    _tex.LoadImage(jpegBytes);   // LoadImage decodes JPEG and auto-resizes
    _tex.Apply();
}
```

The texture is bound to a material on a Quad mesh (`VideoScreen`) placed 3 m in front of the origin at eye height. Unity's `Texture2D.LoadImage` handles JPEG decode on the main thread — no additional decoder needed on the Quest side.

### Frame latency

The latency chain is:
1. Camera captures frame (hardware)
2. HTTPS multipart delivery to Python (~1–2 ms LAN)
3. PyAV H.264 decode (~2–5 ms on CPU)
4. JPEG re-encode at quality 80 (~1–2 ms)
5. WebSocket send from PC → Quest (~1–5 ms Wi-Fi)
6. `LoadImage` decode on Quest main thread (~2–4 ms)

**Total: approximately 10–20 ms** under good Wi-Fi conditions. At 30 fps that's comfortably within the Quest 3's 72 Hz refresh.

---

## Unity Side: PTZ Control

`KasaPTZController` connects to port 8082 and implements `IGimbalController`. It wires to the right thumbstick via `GimbalInputController`:

```
Right thumbstick x/y
  → GimbalInputController.Update()
    → KasaPTZController.SetAngularVelocity(yawRate, pitchRate)
      → ptz_start {"direction":"right","speed":5}  (throttled — only sent on change)
```

When the thumbstick returns to center, `GimbalInputController` calls `SetAngularVelocity(0, 0)`, which sends `ptz_stop`.

The direction → Linkie string mapping:

```
Thumbstick quadrants:
  x > 0.3, y neutral → "right"
  x < -0.3, y neutral → "left"
  y > 0.3, x neutral → "top"
  y < -0.3, x neutral → "bottom"
  x > 0.3, y > 0.3 → "top right"   (etc. for all four diagonals)
```

The speed maps proportionally: `speed = clamp(round(magnitude / maxRate * 10), 1, 10)` where `maxRate = 60` deg/s (the `GimbalInputController.joystickSensitivity` default).

The controller polls `ptz_get_position` every second so the panel always shows the current camera coordinates, not just the last commanded position.

### Center and Lock-On

The **Center** button in the Gimbal Control Panel calls `SetAngles(0, 0, 0)`, which the controller maps to `ptz_goto` at the mid-range coordinates `(78, 281)`.

The **Lock On** button calls `LockTarget(0.5, 0.5)` — center of image — which maps to the same position. When used with a detection result, `LockTarget` receives the bounding box center as a normalized image coordinate and maps it to the camera's coordinate range, pointing the camera at the detected object.

---

## Testing the Connection

### 1. Verify the server is reachable from the Quest network

From a PC command prompt, confirm the server is listening:

```powershell
netstat -an | findstr "8081\|8082\|9000"
```

You should see three `LISTENING` lines.

### 2. Test the stream with a browser

Modern browsers support WebSocket. Open the browser's developer console on any device on the same network and run:

```javascript
const ws = new WebSocket("ws://192.168.1.222:8081");
ws.binaryType = "blob";
ws.onmessage = e => console.log("frame:", e.data.size, "bytes");
```

You should see frame size logs appearing at ~30 fps (typically 20–80 KB per frame at quality 80).

### 3. Test PTZ from a terminal

```python
# Quick PTZ smoke test
import asyncio, websockets, json

async def test():
    async with websockets.connect("ws://192.168.1.222:8082") as ws:
        await ws.send(json.dumps({"cmd": "ptz_get_position"}))
        print(await ws.recv())
        await ws.send(json.dumps({"cmd": "ptz_start", "direction": "right", "speed": 30}))
        print(await ws.recv())
        await asyncio.sleep(1)
        await ws.send(json.dumps({"cmd": "ptz_stop"}))
        print(await ws.recv())

asyncio.run(test())
```

Expected output:
```json
{"ok": true, "x": 78, "y": 281}
{"ok": true, "motor_ready": true}
{"ok": true, "motor_ready": true}
```

The camera should visibly pan right for one second.

### 4. Quest app

Build and deploy the Unity project to the Quest. On launch, the `VideoScreen` quad should display the live camera feed within 1–2 seconds of the server starting. Move the right thumbstick — the camera should respond.

If the stream doesn't appear, check:
- `SERVER_HOST` in `config.py` matches the PC's LAN IP
- Firewall ports are open
- Quest and PC are on the same Wi-Fi network (some routers isolate wireless clients from each other — check the router's "AP isolation" or "client isolation" setting)

---

## Reconnect Behaviour

Both the stream relay and the PTZ controller handle disconnects gracefully:

**Server side:** `capture_loop` retries the camera HTTPS connection every 5 seconds on failure. The WebSocket servers stay up the entire time — Quest clients reconnect to a waiting server, not a crashed one.

**Quest side:** `VideoStreamReceiver` and `KasaPTZController` both run `ConnectLoop` — an async loop that retries the WebSocket connection every 3 seconds on failure. If the PC reboots or the server restarts, the Quest app recovers automatically without needing to be relaunched.

On PTZ reconnect, the controller immediately sends `ptz_stop` to prevent the camera from running away if it was mid-movement when the connection dropped.

---

## Key Takeaways

1. **Bind to the LAN IP, not localhost** — `SERVER_HOST` in `config.py` must be the PC's LAN address so the Quest can reach it over Wi-Fi.
2. **The server re-encodes to JPEG** — Unity's `Texture2D.LoadImage` handles JPEG natively; there's no H.264 decoder on the Quest side.
3. **Throttle to 30 fps server-side** — without the frame cap, a tight `read_frame()` loop saturates the CPU and floods the WebSocket with more frames than the Quest can display.
4. **ptz_start is stateful** — the camera keeps moving after a single `set_move` command. Always send `ptz_stop` when the thumbstick centers, or the camera will run to its physical limit.
5. **AP isolation breaks Quest connectivity** — if the Quest can reach the internet but not the PC, check the router for client isolation settings.
6. **ptz_rectify is a calibration command, not an init** — it triggers a full motor sweep (~10 s) and blocks PTZ commands during that time. Don't call it as part of normal startup.
