"""
WebSocket server — detection, camera stream relay, and PTZ control.

Ports (config.py):
  9000 — detection: Unity sends JPEG bytes, receives JSON detections
  8081 — stream:    broadcasts MJPEG frames to Unity
  8082 — PTZ:       Unity sends JSON commands, receives JSON replies

Run: python server.py
"""

import asyncio
import json
import logging
import time
import threading

import cv2
import websockets

import config
from detector import ObjectDetector
from kasa_camera import KasaCamera
from kasa_motor_control import KasaMotorControl

logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
log = logging.getLogger(__name__)

detector = ObjectDetector()

motor        = KasaMotorControl()
_motor_ready = False


# ── Detection server ──────────────────────────────────────────────────────────

async def handle_detection(ws):
    addr = ws.remote_address
    log.info(f"[Detection] Client connected: {addr}")
    try:
        async for msg in ws:
            if isinstance(msg, bytes):
                frame = detector.detect(msg)
                await ws.send(json.dumps(frame.to_dict()))
    except websockets.ConnectionClosed:
        pass
    log.info(f"[Detection] Client disconnected: {addr}")


# ── PTZ control WebSocket ─────────────────────────────────────────────────────
#
# Commands from Unity:
#   {"cmd": "ptz_start",       "direction": "right", "speed": 0-100}
#   {"cmd": "ptz_stop"}
#   {"cmd": "ptz_goto",        "x": <int>, "y": <int>}
#   {"cmd": "ptz_get_position"}
#   {"cmd": "ptz_rectify"}
#
# Replies:
#   {"ok": true,  "motor_ready": true}
#   {"ok": false, "error": "..."}
#   {"ok": true,  "x": <int>, "y": <int>}   ← ptz_get_position

async def handle_ptz(ws):
    addr = ws.remote_address
    log.info(f"[PTZ] Client connected: {addr}")
    try:
        async for msg in ws:
            try:
                data = json.loads(msg)
                cmd  = data.get("cmd", "")

                if not _motor_ready:
                    await ws.send(json.dumps({"ok": False, "error": "motor not ready"}))
                    continue

                if cmd == "ptz_start":
                    direction = data.get("direction", "right")
                    speed     = max(1, min(10, int(data.get("speed", 5))))  # Unity sends 1-10; camera expects 1-10
                    ok = motor.start_moving(direction, speed)
                    await ws.send(json.dumps({"ok": ok, "motor_ready": True}))

                elif cmd == "ptz_stop":
                    ok = motor.stop_moving()
                    await ws.send(json.dumps({"ok": ok, "motor_ready": True}))

                elif cmd == "ptz_goto":
                    x, y = int(data["x"]), int(data["y"])
                    ok = motor.move_to_position(x, y)
                    await ws.send(json.dumps({"ok": ok, "motor_ready": True}))

                elif cmd == "ptz_get_position":
                    pos = motor.get_position()
                    if pos is not None and pos.get("err_code", -1) == 0:
                        await ws.send(json.dumps({"ok": True, "x": pos["x"], "y": pos["y"]}))
                    else:
                        await ws.send(json.dumps({"ok": False, "error": "position read failed"}))

                elif cmd == "ptz_rectify":
                    ok = motor._send({"smartlife.cam.ipcamera.ptz": {"set_motor_rectify": {}}})
                    await ws.send(json.dumps({"ok": ok, "motor_ready": True}))

                elif cmd == "get_config":
                    from camera_discovery import load_settings
                    s = load_settings()
                    await ws.send(json.dumps({
                        "ok": True, "cmd_type": "config",
                        "camera_ip": s.get("camera_ip", config.KASA_CAMERA_IP),
                        "server_host": config.SERVER_HOST,
                    }))

                elif cmd == "scan_cameras":
                    from camera_discovery import discover_cameras as _disc
                    cameras = await asyncio.to_thread(_disc)
                    await ws.send(json.dumps({
                        "ok": True, "cmd_type": "scan", "cameras": cameras,
                    }))

                elif cmd == "set_camera":
                    new_ip = data.get("ip", "")
                    if new_ip:
                        from camera_discovery import save_settings
                        save_settings({"camera_ip": new_ip})
                        await ws.send(json.dumps({
                            "ok": True, "cmd_type": "set_camera", "restart_required": True,
                        }))
                    else:
                        await ws.send(json.dumps({"ok": False, "error": "no ip provided"}))

                else:
                    await ws.send(json.dumps({"ok": False, "error": f"unknown cmd {cmd!r}"}))

            except Exception as e:
                log.warning(f"[PTZ] command error: {e}")
                await ws.send(json.dumps({"ok": False, "error": str(e)}))
    except websockets.ConnectionClosed:
        pass
    log.info(f"[PTZ] Client disconnected: {addr}")


def _motor_init_thread():
    global _motor_ready
    log.info(f"[PTZ] Connecting to KC410S at {config.KASA_CAMERA_IP} via local Linkie...")
    if motor.login():
        _motor_ready = True
        log.info("[PTZ] Motor ready.")
    else:
        log.warning("[PTZ] Motor init failed — PTZ commands will be rejected.")


# ── Camera stream relay ───────────────────────────────────────────────────────

_STREAM_FPS     = 30
_STREAM_QUALITY = 80   # JPEG quality 0-100
_RECONNECT_SECS = 5    # delay between reconnect attempts

stream_clients: set = set()


async def handle_stream(ws):
    stream_clients.add(ws)
    log.info(f"[Stream] Client connected: {ws.remote_address}")
    try:
        await ws.wait_closed()
    finally:
        stream_clients.discard(ws)
        log.info(f"[Stream] Client disconnected: {ws.remote_address}")


def capture_loop(loop: asyncio.AbstractEventLoop):
    frame_interval = 1.0 / _STREAM_FPS

    while True:
        cam = KasaCamera()
        if not cam.connect():
            log.warning(f"[Stream] KC410S not reachable at {config.KASA_CAMERA_IP} — "
                        f"retrying in {_RECONNECT_SECS}s")
            time.sleep(_RECONNECT_SECS)
            continue

        log.info(f"[Stream] KC410S connected → ws port {config.STREAM_WS_PORT}")
        try:
            while cam.is_open():
                t0    = time.monotonic()
                frame = cam.read_frame()
                if frame is None:
                    break

                if stream_clients:
                    _, jpeg = cv2.imencode(".jpg", frame,
                                          [cv2.IMWRITE_JPEG_QUALITY, _STREAM_QUALITY])
                    asyncio.run_coroutine_threadsafe(broadcast(jpeg.tobytes()), loop)

                elapsed = time.monotonic() - t0
                wait    = frame_interval - elapsed
                if wait > 0:
                    time.sleep(wait)
        finally:
            cam.release()

        log.warning(f"[Stream] Stream dropped — reconnecting in {_RECONNECT_SECS}s")
        time.sleep(_RECONNECT_SECS)


async def broadcast(data: bytes):
    if not stream_clients:
        return
    await asyncio.gather(
        *[ws.send(data) for ws in list(stream_clients)],
        return_exceptions=True,
    )


# ── Entry point ───────────────────────────────────────────────────────────────

async def main():
    loop = asyncio.get_running_loop()

    threading.Thread(target=capture_loop,       args=(loop,), daemon=True).start()
    threading.Thread(target=_motor_init_thread,               daemon=True).start()

    detect_server = websockets.serve(handle_detection, config.SERVER_HOST, config.DETECTION_WS_PORT)
    stream_server = websockets.serve(handle_stream,    config.SERVER_HOST, config.STREAM_WS_PORT)
    ptz_server    = websockets.serve(handle_ptz,       config.SERVER_HOST, config.PTZ_WS_PORT)

    log.info(f"[Server] Detection WS  → ws://{config.SERVER_HOST}:{config.DETECTION_WS_PORT}")
    log.info(f"[Server] Camera stream → ws://{config.SERVER_HOST}:{config.STREAM_WS_PORT}")
    log.info(f"[Server] PTZ control   → ws://{config.SERVER_HOST}:{config.PTZ_WS_PORT}")

    try:
        async with detect_server, stream_server, ptz_server:
            await asyncio.Future()
    finally:
        detector.obs_log.flush()
        log.info("[Detector] Observation log flushed.")


if __name__ == "__main__":
    asyncio.run(main())
