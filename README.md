# Gumball

**VR camera control for the Meta Quest 3 — stream, see, and steer a real IP camera from inside a headset.**

Gumball connects a Meta Quest 3 to a TP-Link Kasa KC410S pan/tilt IP camera over a local network. The right thumbstick drives the physical camera in real time; the live H.264 feed plays back inside the headset. An optional YOLOv8 detection pipeline identifies and labels objects as bounding box overlays in VR.

---

## Features

- **Live video** — H.264 stream from the KC410S displayed on a full-field VR quad
- **VR pan/tilt** — right thumbstick drives camera pan/tilt via the Linkie PTZ protocol
- **Lock-on** — point at a location in the VR feed; camera slews to center it
- **Object detection** — YOLOv8n server-side inference with bounding boxes overlaid in VR
- **Camera discovery** — in-VR control panel scans the LAN and lets you switch cameras
- **No RTSP required** — uses the camera's native HTTPS multipart stream (port 19443) and local Linkie PTZ (port 10443)

---

## Hardware

| Item | Details |
|------|---------|
| Meta Quest 3 | Developer Mode enabled for sideloading |
| TP-Link Kasa KC410S | Pan/tilt IP camera, same Wi-Fi or wired LAN as the PC |
| Windows PC | Runs the Python backend; NVIDIA GPU recommended for YOLO inference |

---

## Requirements

**Python backend**
- Python 3.11+
- See `PythonBackend/requirements.txt`

**Unity**
- Unity 2022.3 LTS
- Meta XR SDK (via Unity Package Manager)
- NativeWebSocket
- Newtonsoft.Json for Unity

---

## Setup

### 1. Python backend

```bash
cd PythonBackend
pip install -r requirements.txt
copy config.example.py config.py
```

Edit `config.py` with your values:

```python
SERVER_HOST    = "192.168.1.x"     # This PC's LAN IP
KASA_CAMERA_IP = "192.168.1.x"     # Your KC410S LAN IP
KASA_USERNAME  = "your@email.com"  # Kasa cloud account email
KASA_PASSWORD  = "yourPassword"    # Kasa cloud account password
```

> **Note:** The KC410S authenticates with your Kasa cloud credentials, not a local device password.
> `config.py` is gitignored and must not be committed.

Start the server:

```powershell
.\start.ps1
```

This starts three WebSocket services:

| Port | Service |
|------|---------|
| 8081 | Video stream (binary JPEG frames) |
| 8082 | PTZ control (JSON commands) |
| 9000 | Object detection (JPEG in, JSON out) |

### 2. Unity project

1. Open `UnityProject/` in Unity 2022.3 LTS.
2. In the Unity menu bar choose **MetaGimbalVision > Apply Network Config**.
3. Set `PC_IP` in `NetworkConfig.cs` to your PC's LAN IP, then click the menu item to push it to all components.
4. Build for Android (Quest 3) and deploy via `adb install`.

---

## Usage

Put on the headset. The KC410S live feed fills the view. Use the **right thumbstick** to pan and tilt the camera. The in-VR **control panel** (visible on the left wrist or via the menu button) shows:

- Camera connection status and current PTZ coordinates
- A **Scan** button to discover KC-series cameras on the LAN
- A camera IP selector to switch between cameras

---

## Project structure

```
MetaGimbalVision/
+-- PythonBackend/
|   +-- server.py             # Main WebSocket server
|   +-- kasa_camera.py        # HTTPS stream reader
|   +-- kasa_motor_control.py # Linkie PTZ driver
|   +-- detector.py           # YOLOv8 wrapper
|   +-- camera_discovery.py   # LAN scanner + settings
|   +-- diagnostic.py         # Runtime health dashboard
|   +-- config.example.py     # Configuration template
|   +-- requirements.txt
|   +-- start.ps1             # Launch script (Windows)
+-- UnityProject/
|   +-- Assets/Scripts/       # All C# source
+-- docs/
|   +-- kasa-kc410s-stream-extraction.md
|   +-- kasa-kc410s-ptz-control.md
|   +-- kasa-kc410s-server-hmd-integration.md
+-- ARCHITECTURE.md
+-- LICENSE
```

See [ARCHITECTURE.md](ARCHITECTURE.md) for component diagrams, data flows, and design decisions.

---

## License

BSD 3-Clause — see [LICENSE](LICENSE).
