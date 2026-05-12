# config.example.py — copy this file to config.py and fill in your values.
# config.py is gitignored and must never be committed to the repository.

import json as _json, os as _os

# Load camera IP from settings.json if the in-VR scanner has saved one.
_settings_path = _os.path.join(_os.path.dirname(__file__), "settings.json")
try:
    with open(_settings_path, encoding="utf-8") as _f:
        _s = _json.load(_f)
except Exception:
    _s = {}

# IP address of the PC running server.py on your local network.
SERVER_HOST = "YOUR_SERVER_IP"        # e.g. "192.168.1.10"
DETECTION_WS_PORT = 9000

# ── Kasa KC410S camera ────────────────────────────────────────────────────────
KASA_CAMERA_IP   = _s.get("camera_ip", "YOUR_CAMERA_IP")  # e.g. "192.168.1.199"
KASA_USERNAME    = "your@email.com"    # Kasa cloud account email
KASA_PASSWORD    = "yourKasaPassword"  # Kasa cloud account password (plain text)
KASA_STREAM_TYPE = "hd"               # "hd" or "sd"

STREAM_WS_PORT   = 8081
PTZ_WS_PORT      = 8082

# Detection model — yolov8n for speed, yolov8s/m/l for accuracy.
# The model file is downloaded automatically by ultralytics on first run.
YOLO_MODEL = "yolov8n.pt"
CONFIDENCE  = 0.50
IOU         = 0.45
DEVICE      = "cuda"   # "cpu" if no GPU
