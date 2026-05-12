"""
Discover Kasa KC410S cameras on the local network by probing port 19443
(the KC410S HTTPS multipart stream port).  Also provides settings.json
read/write so the selected camera persists across server restarts.
"""

import concurrent.futures
import json
import os
import socket
from typing import List

SETTINGS_FILE = os.path.join(os.path.dirname(__file__), "settings.json")
CAMERA_PORT   = 19443   # KC410S HTTPS stream/API port
_WORKERS      = 64
_TIMEOUT      = 0.4     # seconds per probe


def get_local_subnet() -> str:
    """Return the /24 prefix of the machine's default interface, e.g. '192.168.1'."""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
    except OSError:
        ip = "127.0.0.1"
    finally:
        s.close()
    return ".".join(ip.split(".")[:3])


def _probe(ip: str) -> bool:
    try:
        with socket.create_connection((ip, CAMERA_PORT), timeout=_TIMEOUT):
            return True
    except OSError:
        return False


def discover_cameras(subnet: str | None = None) -> List[str]:
    """
    Scan all 254 hosts on the /24 subnet for KC410S cameras.
    Returns a list of IP strings that responded on CAMERA_PORT.
    Typically completes in under 3 seconds.
    """
    if subnet is None:
        subnet = get_local_subnet()
    ips = [f"{subnet}.{i}" for i in range(1, 255)]
    with concurrent.futures.ThreadPoolExecutor(max_workers=_WORKERS) as ex:
        return [ip for ip, ok in zip(ips, ex.map(_probe, ips)) if ok]


def load_settings() -> dict:
    if os.path.exists(SETTINGS_FILE):
        try:
            with open(SETTINGS_FILE) as f:
                return json.load(f)
        except Exception:
            pass
    return {}


def save_settings(updates: dict) -> None:
    s = load_settings()
    s.update(updates)
    with open(SETTINGS_FILE, "w") as f:
        json.dump(s, f, indent=2)


def get_camera_ip(fallback: str = "192.168.1.199") -> str:
    return load_settings().get("camera_ip", fallback)
