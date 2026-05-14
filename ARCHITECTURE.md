# Architecture — Gumball / MetaGimbalVision

> Living document. Update status and phases as work progresses.

---

## Problem statement

The TP-Link Kasa KC410S is a consumer pan/tilt IP camera with no RTSP support and a proprietary local protocol. The goal is to stream its live H.264 feed into a Meta Quest 3 VR headset and drive its pan/tilt motors from the right thumbstick, with optional YOLOv8 object detection overlaid on the video — all over a local network, no cloud relay.

---

## System diagram

```
+----------------------+   HTTPS :19443 (H.264 multipart)   +------------------+
|  Kasa KC410S         |<----------------------------------->|  kasa_camera.py  |
|  (IP camera, LAN)    |   HTTPS :10443 (Linkie/XOR JSON)   |  kasa_motor_     |
|                      |<----------------------------------->|  control.py      |
+----------------------+                                     +--------+---------+
                                                                      |
                                                             server.py (asyncio)
                                                            /         |          \
                                             WS :8081       |   WS :8082   WS :9000
                                           (stream)         |   (PTZ)      (detect)
                                                            |         |          |
+----------------------+          WebSocket (LAN)           +----------+----------+
|  Meta Quest 3        |<-------------------------------------------- |
|  (VR headset)        |                                    Unity C# components
|                      |                                    VideoStreamReceiver
|  right thumbstick    |----> GimbalInputController ------> KasaPTZController
|  VR display          |<---- VRVideoDisplay <------------- VideoStreamReceiver
|  detection overlay   |<---- DetectionOverlay <----------- ServerDetectionClient
+----------------------+
```

---

## Components

### PythonBackend

| File | Role |
|------|------|
| `server.py` | Async WebSocket server; routes stream, PTZ, and detection traffic |
| `kasa_camera.py` | Reads H.264 multipart stream from HTTPS :19443; decodes to BGR frames |
| `kasa_motor_control.py` | Sends PTZ commands via Linkie protocol to HTTPS :10443 |
| `detector.py` | YOLOv8 inference + BoT-SORT tracking; returns detection JSON per frame |
| `camera_discovery.py` | UDP broadcast scan for KC-series cameras; persists choice to `settings.json` |
| `diagnostic.py` | Interactive runtime dashboard (stream FPS, PTZ position, detection rate) |
| `tracker.py` | Desktop lock-on tracker: click a YOLO box, camera follows; ColorAuditor validates identity; returns home on loss |
| `color_auditor.py` | HSV histogram anchor store per track ID; EMA drift update; best-match recovery for ID swaps |
| `custom_botsort.yaml` | BoT-SORT tracker config with Re-ID enabled; replaces bytetrack.yaml in detector.py |
| `config.py` | Committed config; reads all values from `.env` via python-dotenv |
| `.env` | Gitignored secrets file: IPs, Kasa credentials, model settings |
| `.env.example` | Committed placeholder template; copy to `.env` and fill in values |
| `start.ps1` | Launches `server.py` + `diagnostic.py` in sequence |

### UnityProject / Assets / Scripts

| Folder | Scripts | Role |
|--------|---------|------|
| `Video/` | `VideoStreamReceiver`, `VRVideoDisplay`, `VideoFrameFollow`, `AlignToTransform` | Receive JPEG frames over WS; apply to VR quad texture |
| `Gimbal/` | `KasaPTZController`, `GimbalNetworkController`, `GimbalSerialController`, `IGimbalController` | PTZ implementations behind a common interface |
| `Input/` | `GimbalInputController` | Maps Quest 3 thumbstick -> `IGimbalController.SetAngularVelocity` |
| `Detection/` | `ServerDetectionClient`, `ObjectDetectionManager`, `DetectionOverlay`, `DetectionResult`, `OnDeviceDetector` | Send frames to server; render bounding boxes in VR |
| `UI/` | `ControlPanel`, `MenuToggle`, `PanelDragController` | In-VR control panel: camera scan, IP select, status readout |
| `VR/` | `ControllerRayPointer` | Ray-cast pointer for VR UI interaction |
| `Editor/` | `NetworkConfig`, `SceneSetup`, `OculusConfigSetup`, `RaycastSetup`, `ControlPanelSetup` | One-click Unity Editor tools for project setup |

### docs/

| File | Content |
|------|---------|
| `kasa-kc410s-stream-extraction.md` | Medium article: reverse-engineering the HTTPS H.264 stream |
| `kasa-kc410s-ptz-control.md` | Medium article: Linkie PTZ protocol and XOR cipher |
| `kasa-kc410s-server-hmd-integration.md` | Integration guide: full Python + Unity wiring |

---

## Data flows

### Video stream

```
KC410S HTTPS :19443
  -> H.264 multipart (--data-boundary--)
  -> kasa_camera.py: _read_next_chunk() -> av.Packet -> BGR numpy array
  -> cv2.imencode JPEG
  -> server.py broadcast()
  -> WS :8081 (binary)
  -> Unity VideoStreamReceiver.OnFrameReceived
  -> VRVideoDisplay: Texture2D.LoadImage -> VR quad material
```

### PTZ control

```
Quest 3 right thumbstick
  -> GimbalInputController.Update()
  -> KasaPTZController.SetAngularVelocity(yawRate, pitchRate)
  -> WS :8082 JSON {"cmd":"ptz_start","direction":"right","speed":7}
  -> server.py handle_ptz()
  -> KasaMotorControl.start_moving("right", 7)
  -> HTTPS :10443 POST content=<base64(xor(json))>
  -> KC410S motor moves
```

### Object detection

```
VideoStreamReceiver.OnFrameReceived
  -> ServerDetectionClient.SendFrame(jpegBytes)
  -> WS :9000 (binary)
  -> server.py handle_detection()
  -> ObjectDetector.detect(jpegBytes) -> YOLOv8 + BoT-SORT inference
  -> DetectionFrame JSON {"detections":[{label, confidence, trackId, boundingBox},...]}
  -> WS :9000 back
  -> ServerDetectionClient.OnDetections
  -> DetectionOverlay: draw bounding boxes on VR canvas
```

### Lock-on tracking (tracker.py)

```
tracker.py _detect_loop
  -> snapshot _latest_jpeg_for_audit alongside detection JSON
  -> _latest_dets updated (normalized bounding boxes + track IDs)

tracker.py _tracking_thread (10 Hz)
  -> find target by track_id in _latest_dets
  -> [if absent] Re-ID: search same-label dets closest to frame center -> re-adopt ID
  -> decode _latest_jpeg_for_audit -> extract crop at bounding box coords
  -> ColorAuditor.check_identity(track_id, crop)
      -> HSV histogram vs. stored anchor (HISTCMP_CORREL)
      -> score > threshold: EMA-update anchor, proceed
      -> score <= threshold: call best_match() against all anchors
          -> match found: re-adopt that track_id
          -> no match:    trigger return-to-home
  -> compute normalized offset (dx, dy) from frame center
  -> dead zone check (configurable via UI slider)
  -> ptz_start / ptz_stop -> _ptz_cmd_q -> WS :8082
  -> [on loss > LOST_TIMEOUT_S] ptz_stop + ptz_goto(home_x, home_y)
```

---

## Architecture decision records

### ADR-001: No RTSP
The KC410S has no RTSP endpoint. Port 554 times out. Stream access is exclusively via HTTPS multipart on port 19443.

### ADR-002: Legacy TLS adapter
The KC410S firmware requires TLS 1.0 with `SECLEVEL=0`. A custom `_LegacySSL` / `_LegacyTLSAdapter` `HTTPAdapter` subclass overrides Python's default SSL context in both `kasa_camera.py` and `kasa_motor_control.py`.

### ADR-003: Kasa cloud credentials for local auth
The KC410S authenticates with the user's Kasa cloud email + plain password via HTTP Basic Auth. The python-kasa library `DEFAULT_CREDENTIALS["KASACAMERA"]` (`admin` + MD5 hash) returns `-10007 Unauthorized`. Both plain email/password and `admin` + MD5(password) work once discovered.

### ADR-004: Linkie XOR cipher
PTZ control uses TP-Link's local Linkie protocol on port 10443. POST body: `content=<url-safe-base64(XOR-encrypted-JSON)>`. XOR streaming cipher: IV = `0xAB`; each byte of key XOR'd with plaintext byte before encrypting (key evolves). Outer JSON key: `"smartlife.cam.ipcamera.ptz"`.

### ADR-005: PTZ speed range 1-10
The KC410S strictly requires speed values 1-10. Values >= 11 return `-41203` (parameter value error). Missing speed returns `-41202`. Unity 0-100 joystick range maps to 1-10 with `Mathf.Clamp`.

### ADR-006: Three WebSocket ports
Separate ports for stream (8081), PTZ (8082), and detection (9000) rather than one multiplexed connection. Stream is binary; PTZ and detection are JSON text. Separation simplifies routing and lets Unity components connect independently.

### ADR-007: IGimbalController interface
`KasaPTZController`, `GimbalNetworkController`, and `GimbalSerialController` all implement `IGimbalController`. `GimbalInputController` is hardware-agnostic and works with any implementation via the Inspector reference.

### ADR-008: Camera discovery via python-kasa UDP
`camera_discovery.py` uses python-kasa's `Discover.discover()` (UDP broadcast) to find KC-series devices. The selected camera IP is persisted to `settings.json` and loaded by `config.py` at startup, surviving server restarts.

### ADR-009: Credentials in .env via python-dotenv
`config.py` is committed and contains no secrets — it reads all sensitive values from a `.env` file using python-dotenv. `.env` is gitignored and never committed. `.env.example` (placeholder values) is committed as the setup template. `settings.json` (runtime camera selection written by `camera_discovery.py`) is also gitignored.

### ADR-010: YOLOv8n default model
YOLOv8 nano is the default for inference speed on the Python server. The `.pt` model file is gitignored; ultralytics downloads it on first run. Switch to `yolov8s.pt` / `yolov8m.pt` in `config.py` for better accuracy.

### ADR-011: BoT-SORT with Re-ID replaces ByteTrack
ByteTrack assigns new track IDs when fast camera pans cause bounding boxes to not overlap between frames (low IoU). BoT-SORT with `with_reid: True` adds an OSNet appearance embedding alongside IoU matching, maintaining ID continuity through occlusion and motion blur. Re-ID weights (~50 MB) are downloaded by ultralytics on first run with the new YAML. Config lives in `custom_botsort.yaml`; `track_buffer: 45` gives a ~3-second memory window at 15 fps (the KC410S hardware limit).

### ADR-012: ColorAuditor as client-side identity guard layer
Server-side BoT-SORT Re-ID can still swap IDs when two objects of similar appearance cross. `color_auditor.py` adds a second defense: per-ID HSV histogram anchors stored in `tracker.py`. On each tracking tick, a crop is extracted from the latest JPEG using normalized bounding box coordinates, and compared to the anchor via `cv2.HISTCMP_CORREL`. An EMA (alpha=0.1) slowly drifts the anchor to handle lighting changes without losing identity. On mismatch, `best_match()` scans all anchors to attempt recovery before triggering return-to-home. The auditor runs client-side (in `tracker.py`) because it needs pixel crops — only the detection JSON is transmitted over the WebSocket, not the image data.

### ADR-013: Audit frame snapshotted alongside detection response
The stream (WS :8081) and detection (WS :9000) connections are independent. When a detection response arrives, the current `_state["jpeg"]` may be a frame or two newer than the one that was detected. For HSV histogram comparison this timing imprecision is acceptable (~100 ms at 10 Hz detection / 30 fps stream); the color distribution of a person's clothing does not change frame-to-frame. A sequence-tagged ring buffer can be added if false-positives are observed in practice.

### ADR-014: Minimum crop size before anchoring
The first appearance of a track ID sets its color anchor. If the object is heavily occluded at first appearance, the histogram will be polluted with background pixels. Anchoring is skipped for crops where either dimension is below `min_crop_px` (default 20 px), allowing a cleaner anchor to be established once the object is fully visible.

---

## Project structure

```
MetaGimbalVision/
+-- ARCHITECTURE.md          <- This file
+-- README.md
+-- LICENSE                  <- BSD-3-Clause
+-- .gitignore
|
+-- PythonBackend/
|   +-- server.py
|   +-- kasa_camera.py
|   +-- kasa_motor_control.py
|   +-- detector.py
|   +-- camera_discovery.py
|   +-- diagnostic.py
|   +-- tracker.py           <- Lock-on object tracker (click box -> camera follows)
|   +-- color_auditor.py     <- HSV histogram identity guard (planned: Phase 9)
|   +-- custom_botsort.yaml  <- BoT-SORT tracker config with Re-ID (planned: Phase 9)
|   +-- config.py            <- Committed; reads from .env via python-dotenv
|   +-- .env.example         <- Committed template; copy to .env
|   +-- .env                 <- Gitignored; fill in real credentials
|   +-- settings.json        <- Gitignored; written by camera_discovery
|   +-- requirements.txt
|   +-- start.ps1
|   +-- test_box_move.py     <- PTZ coordinate range calibration
|   +-- test_find_limits.py
|   +-- test_ptz_diag.py
|
+-- UnityProject/
|   +-- Assets/
|   |   +-- Scripts/
|   |   |   +-- Detection/
|   |   |   +-- Editor/
|   |   |   +-- Gimbal/
|   |   |   +-- Input/
|   |   |   +-- UI/
|   |   |   +-- Video/
|   |   |   +-- VR/
|   |   +-- Scenes/
|   |   +-- TextMesh Pro/
|   +-- Packages/
|   +-- ProjectSettings/
|
+-- docs/
    +-- kasa-kc410s-stream-extraction.md
    +-- kasa-kc410s-ptz-control.md
    +-- kasa-kc410s-server-hmd-integration.md
```

---

## Development phases

| Phase | Description | Status |
|-------|-------------|--------|
| 1 | HTTPS stream extraction (port 19443, H.264 multipart) | Done |
| 2 | VR display — Unity quad + WebSocket frame relay | Done |
| 3 | PTZ control — Linkie reverse-engineering, motor driver | Done |
| 4 | YOLOv8 server-side detection + VR bounding box overlay | Done |
| 5 | Camera discovery + in-VR control panel | Done |
| 6 | Lock-on — click VR feed to slew camera | Done |
| 9 | Robust tracking — BoT-SORT Re-ID + ColorAuditor HSV identity guard | In Progress |
| 7 | On-device detection — export YOLOv8n to ONNX, Unity Sentis | TODO |
| 8 | Quest 3 production deployment — APK, wireless perf tuning | TODO |

---

## Open questions

- KC410S streams at 15 fps (hardware limit, confirmed by spec and measurement). 4MP sensor downscaled to 1280x720 over HTTPS. No software path to higher frame rate.
- Phase 9: ColorAuditor threshold — should it relax automatically while camera is actively panning (motion blur changes apparent color)?
- Phase 9: If two objects of the same class and similar color are in frame, best_match() may mis-assign on ID swap; no solution yet beyond raising the match threshold
- Phase 7: Sentis ONNX runtime vs. server-side: latency trade-off at wire speed
- Phase 8: JPEG quality vs. frame rate budget over Wi-Fi at Quest 3 wireless bitrate

---

*Last updated: 2026-05-14 — Phase 9 added: BoT-SORT Re-ID + ColorAuditor tracking plan; ADRs 011-014*
