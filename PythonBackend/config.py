import json as _json
import os as _os
from dotenv import load_dotenv

load_dotenv(_os.path.join(_os.path.dirname(__file__), ".env"))

# Camera IP can be overridden at runtime by the in-VR scanner (saved to settings.json).
_settings_path = _os.path.join(_os.path.dirname(__file__), "settings.json")
try:
    with open(_settings_path, encoding="utf-8") as _f:
        _s = _json.load(_f)
except Exception:
    _s = {}

SERVER_HOST       = _os.environ.get("SERVER_HOST", "localhost")
DETECTION_WS_PORT = 9000

# ── Kasa KC410S camera ────────────────────────────────────────────────────────
KASA_CAMERA_IP   = _s.get("camera_ip", _os.environ.get("KASA_CAMERA_IP", ""))
KASA_USERNAME    = _os.environ.get("KASA_USERNAME", "")
KASA_PASSWORD    = _os.environ.get("KASA_PASSWORD", "")
KASA_STREAM_TYPE = _os.environ.get("KASA_STREAM_TYPE", "hd")

STREAM_WS_PORT = 8081
PTZ_WS_PORT    = 8082

# Detection model — yolov8n for speed, yolov8s/m/l for accuracy
YOLO_MODEL = _os.environ.get("YOLO_MODEL", "yolov8n.pt")
CONFIDENCE  = float(_os.environ.get("CONFIDENCE", "0.50"))
IOU         = float(_os.environ.get("IOU", "0.45"))
DEVICE      = _os.environ.get("DEVICE", "cuda")
