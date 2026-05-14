#!/usr/bin/env python3
"""
tracker.py -- Object lock-on tracker for MetaGimbalVision.

Connects to the same server as diagnostic.py:
  WS :{STREAM_PORT}  -- camera stream  (receive JPEG frames)
  WS :{DETECT_PORT}  -- detection      (send JPEG, receive detections)
  WS :{PTZ_PORT}     -- PTZ control    (track object, return to home)

Click any bounding box to lock on. The camera centers and follows
the selected object. When the object is lost the camera returns to
its pre-tracking position.

Usage:
    python tracker.py [host]     # host defaults to config.SERVER_HOST

Dependencies:
    pip install websockets pillow
"""

import asyncio
import colorsys
import json
import queue
import sys
import threading
import time
import tkinter as tk
from io import BytesIO

import cv2
import numpy as np
import websockets

from color_auditor import ColorAuditor

try:
    from PIL import Image, ImageDraw, ImageTk
except ImportError:
    print("ERROR: Pillow is required.  pip install pillow")
    sys.exit(1)

try:
    import config as _cfg
    DEFAULT_HOST = _cfg.SERVER_HOST
except Exception:
    DEFAULT_HOST = "localhost"

# ── Ports ─────────────────────────────────────────────────────────────────────

STREAM_PORT = 8081
PTZ_PORT    = 8082
DETECT_PORT = 9000

# ── Display ───────────────────────────────────────────────────────────────────

CANVAS_W = 640
CANVAS_H = 360
UI_MS    = 67        # ~15 Hz UI refresh -- matches camera frame rate

# ── Tracking parameters ───────────────────────────────────────────────────────
# All values tuned for 15 fps (KC410S hardware limit).
# At 15 fps: 1 frame = 67 ms, LOST_TIMEOUT_S=2.0 -> 30 frames of tolerance.

DEAD_ZONE      = 0.12   # center no-move zone (fraction of frame)
MIN_SPEED      = 1
MAX_SPEED      = 4      # conservative default; raise via UI slider if needed
LOST_TIMEOUT_S = 2.0    # seconds absent before track considered lost (30 frames @ 15 fps)
DETECT_HZ      = 12     # detection requests per second (sends ~80% of frames at 15 fps)
TRACK_HZ       = 15     # PTZ command rate -- match camera frame rate
RECONNECT_S    = 3

# ── Palette ───────────────────────────────────────────────────────────────────

BG, BG2, BG3   = "#1b1d27", "#23263a", "#2e3247"
FG, FG_DIM     = "#e0e4f0", "#7a84a8"
GREEN, RED     = "#22c55e", "#ef4444"
YELLOW, ORANGE = "#ffd740", "#ff9800"
ACCENT         = "#6366f1"

# ── Cross-thread shared state ─────────────────────────────────────────────────

_lock   = threading.Lock()
_state  = {"jpeg": None}     # latest JPEG bytes from stream

_dets_lock   = threading.Lock()
_latest_dets: list = []       # latest detection list from detect_loop

_track_lock  = threading.Lock()
_track_state = {
    "track_id":     None,      # int | None
    "track_label":  "",
    "home_pos":     None,      # {"x": int, "y": int} | None
    "last_seen":    0.0,
    "offset":       (0.0, 0.0),
    "auditor_score": 1.0,      # latest ColorAuditor correlation score
}

_audit_lock              = threading.Lock()
_latest_jpeg_for_audit: bytes | None = None

# ── Queues ────────────────────────────────────────────────────────────────────

_status_q   = queue.Queue()           # ("stream"|"detect"|"ptz", ok: bool)
_frame_q    = queue.Queue(maxsize=2)  # (jpeg: bytes, fps: float)
_ptz_resp_q = queue.Queue(maxsize=8)  # dict PTZ response
_ptz_cmd_q  = queue.Queue()           # dict PTZ commands to send

# True while a ptz_start is in flight; audit is skipped during panning
# to avoid false rejects caused by background shift.
_ptz_moving = threading.Event()

_stop = threading.Event()

# Live-tunable parameters (written by UI sliders, read by tracking thread)
_tune = {"max_speed": MAX_SPEED, "dead_zone": DEAD_ZONE}

# Shared ColorAuditor instance (accessed by tracking thread and _stop_tracking)
auditor = ColorAuditor()

# ── Helpers ───────────────────────────────────────────────────────────────────

def _launch_async(coro_fn, *args):
    def run():
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        loop.run_until_complete(coro_fn(*args))
        loop.close()
    threading.Thread(target=run, daemon=True).start()


def _launch(fn, *args):
    threading.Thread(target=fn, args=args, daemon=True).start()


def _box_color(tid: int) -> str:
    """Unique color per track ID via golden-angle hue."""
    h = (tid * 137.508) % 360 / 360
    r, g, b = colorsys.hsv_to_rgb(h, 0.85, 0.92)
    return "#{:02x}{:02x}{:02x}".format(int(r * 255), int(g * 255), int(b * 255))


# ── Async loops ───────────────────────────────────────────────────────────────

async def _stream_loop(host: str):
    url = f"ws://{host}:{STREAM_PORT}"
    while not _stop.is_set():
        try:
            async with websockets.connect(url, max_size=20_000_000) as ws:
                _status_q.put(("stream", True))
                n, t0 = 0, time.monotonic()
                async for msg in ws:
                    if _stop.is_set():
                        break
                    if not isinstance(msg, bytes):
                        continue
                    n += 1
                    fps = n / max(time.monotonic() - t0, 1e-6)
                    with _lock:
                        _state["jpeg"] = msg
                    try:
                        _frame_q.put_nowait((msg, fps))
                    except queue.Full:
                        pass
        except Exception:
            _status_q.put(("stream", False))
        if not _stop.is_set():
            await asyncio.sleep(RECONNECT_S)
    _status_q.put(("stream", False))


async def _detect_loop(host: str):
    url      = f"ws://{host}:{DETECT_PORT}"
    interval = 1.0 / DETECT_HZ
    while not _stop.is_set():
        try:
            async with websockets.connect(url, max_size=5_000_000) as ws:
                _status_q.put(("detect", True))
                last_send = 0.0
                while not _stop.is_set():
                    now = time.monotonic()
                    if now - last_send >= interval:
                        with _lock:
                            jpeg = _state["jpeg"]
                        if jpeg is not None:
                            await ws.send(jpeg)
                            last_send = now
                    try:
                        raw  = await asyncio.wait_for(ws.recv(), timeout=0.05)
                        dets = json.loads(raw).get("detections", [])
                        with _lock:
                            audit_jpeg = _state["jpeg"]
                        with _dets_lock:
                            global _latest_dets
                            _latest_dets = dets
                        global _latest_jpeg_for_audit
                        with _audit_lock:
                            _latest_jpeg_for_audit = audit_jpeg
                    except asyncio.TimeoutError:
                        pass
        except Exception:
            _status_q.put(("detect", False))
        if not _stop.is_set():
            await asyncio.sleep(RECONNECT_S)
    _status_q.put(("detect", False))


async def _ptz_loop(host: str):
    url = f"ws://{host}:{PTZ_PORT}"
    while not _stop.is_set():
        try:
            async with websockets.connect(url) as ws:
                _status_q.put(("ptz", True))
                while not _stop.is_set():
                    while not _ptz_cmd_q.empty():
                        try:
                            await ws.send(json.dumps(_ptz_cmd_q.get_nowait()))
                        except queue.Empty:
                            break
                    try:
                        raw = await asyncio.wait_for(ws.recv(), timeout=0.05)
                        try:
                            _ptz_resp_q.put_nowait(json.loads(raw))
                        except queue.Full:
                            pass
                    except asyncio.TimeoutError:
                        await asyncio.sleep(0.02)
        except Exception:
            _status_q.put(("ptz", False))
        if not _stop.is_set():
            await asyncio.sleep(RECONNECT_S)
    _status_q.put(("ptz", False))


# ── Tracking thread ───────────────────────────────────────────────────────────

def _tracking_thread():
    """
    Reads latest detections and sends PTZ commands to follow the tracked object.
    Runs at TRACK_HZ; holds last direction when object is briefly absent.
    Returns camera to home position after LOST_TIMEOUT_S seconds of absence.
    """
    interval   = 1.0 / TRACK_HZ
    prev_dir   = None
    prev_speed = 0

    while not _stop.is_set():
        time.sleep(interval)

        with _dets_lock:
            dets = list(_latest_dets)

        with _track_lock:
            tid       = _track_state["track_id"]
            last_seen = _track_state["last_seen"]

        if tid is None:
            # No active track -- ensure camera is stopped
            if prev_dir is not None:
                _ptz_cmd_q.put({"cmd": "ptz_stop"})
                prev_dir   = None
                prev_speed = 0
            continue

        target = next((d for d in dets if d.get("trackId") == tid), None)

        if target is None:
            # Re-identify: fast pan can cause ByteTrack to assign a new ID to
            # the same physical object.  Search for any detection of the same
            # label and adopt the one closest to the frame center (where we've
            # been steering the camera).
            with _track_lock:
                track_label = _track_state["track_label"]
            same_label = [d for d in dets if d.get("label") == track_label]
            if same_label:
                def _center_dist(d):
                    bb = d.get("boundingBox", {})
                    cx = bb.get("x", 0.5) + bb.get("width",  0) / 2.0
                    cy = bb.get("y", 0.5) + bb.get("height", 0) / 2.0
                    return (cx - 0.5) ** 2 + (cy - 0.5) ** 2
                best    = min(same_label, key=_center_dist)
                new_tid = best.get("trackId", 0)
                if new_tid > 0:
                    with _track_lock:
                        _track_state["track_id"]  = new_tid
                        _track_state["last_seen"] = time.time()
                    tid    = new_tid
                    target = best

        if target is None:
            if time.time() - last_seen > LOST_TIMEOUT_S:
                # Track lost -- stop and return to home
                _ptz_cmd_q.put({"cmd": "ptz_stop"})
                with _track_lock:
                    home = _track_state["home_pos"]
                    _track_state.update({
                        "track_id":     None,
                        "track_label":  "",
                        "home_pos":     None,
                        "offset":       (0.0, 0.0),
                        "auditor_score": 1.0,
                    })
                auditor.clear()
                _ptz_moving.clear()
                if home:
                    _ptz_cmd_q.put({"cmd": "ptz_goto",
                                    "x": home["x"], "y": home["y"]})
                prev_dir   = None
                prev_speed = 0
            # Else: briefly absent -- hold current direction until timeout
            continue

        # Object found -- update last_seen timestamp
        with _track_lock:
            _track_state["last_seen"] = time.time()
            current_tid = _track_state["track_id"]

        # ── Color audit ───────────────────────────────────────────────────────
        # Skip entirely while the camera is panning: background pixels in the
        # crop shift with the scene, making the histogram unreliable.  The
        # audit resumes once the camera settles (ptz_stop clears _ptz_moving).
        if not _ptz_moving.is_set():
            with _audit_lock:
                jpeg_for_crop = _latest_jpeg_for_audit

            if jpeg_for_crop is not None:
                arr = np.frombuffer(jpeg_for_crop, np.uint8)
                img = cv2.imdecode(arr, cv2.IMREAD_COLOR)
                if img is not None:
                    h_img, w_img = img.shape[:2]
                    bb_a = target.get("boundingBox", {})
                    bw_n = bb_a.get("width",  0)
                    bh_n = bb_a.get("height", 0)
                    if bw_n >= 0.05 and bh_n >= 0.05:
                        x1 = int(bb_a.get("x", 0) * w_img)
                        y1 = int(bb_a.get("y", 0) * h_img)
                        x2 = int((bb_a.get("x", 0) + bw_n) * w_img)
                        y2 = int((bb_a.get("y", 0) + bh_n) * h_img)
                        crop = img[y1:y2, x1:x2]

                        audit_valid, audit_score = auditor.check_identity(current_tid, crop)

                        if not audit_valid:
                            # Try color-based recovery before giving up
                            recovered_id, rec_score = auditor.best_match(crop)
                            if recovered_id is not None and rec_score >= auditor.threshold:
                                with _track_lock:
                                    _track_state["track_id"]      = recovered_id
                                    _track_state["last_seen"]     = time.time()
                                    _track_state["auditor_score"] = round(rec_score, 3)
                                current_tid = recovered_id
                            else:
                                # Color mismatch with no recoverable anchor -- go home
                                _ptz_cmd_q.put({"cmd": "ptz_stop"})
                                _ptz_moving.clear()
                                with _track_lock:
                                    home = _track_state["home_pos"]
                                    _track_state.update({
                                        "track_id":      None,
                                        "track_label":   "",
                                        "home_pos":      None,
                                        "offset":        (0.0, 0.0),
                                        "auditor_score": 0.0,
                                    })
                                auditor.clear()
                                if home:
                                    _ptz_cmd_q.put({"cmd": "ptz_goto",
                                                    "x": home["x"], "y": home["y"]})
                                prev_dir   = None
                                prev_speed = 0
                                continue
                        else:
                            with _track_lock:
                                _track_state["auditor_score"] = round(audit_score, 3)

        # Read live-tunable parameters
        dead_zone = _tune["dead_zone"]
        max_speed = _tune["max_speed"]

        # Compute center offset (normalized, origin at frame center)
        bb = target.get("boundingBox", {})
        cx = bb.get("x", 0.5) + bb.get("width",  0) / 2.0
        cy = bb.get("y", 0.5) + bb.get("height", 0) / 2.0
        dx = cx - 0.5   # positive -> object is right of center
        dy = cy - 0.5   # positive -> object is below center

        with _track_lock:
            _track_state["offset"] = (round(dx, 3), round(dy, 3))

        mag = max(abs(dx), abs(dy))

        if mag < dead_zone:
            if prev_dir is not None:
                _ptz_cmd_q.put({"cmd": "ptz_stop"})
                _ptz_moving.clear()
                prev_dir   = None
                prev_speed = 0
        else:
            h_dir = ("right" if dx > 0 else "left") if abs(dx) > dead_zone else None
            v_dir = ("bottom" if dy > 0 else "top") if abs(dy) > dead_zone else None

            if h_dir and v_dir:
                direction = f"{v_dir} {h_dir}"
            elif h_dir:
                direction = h_dir
            else:
                direction = v_dir

            speed = int(MIN_SPEED + (max_speed - MIN_SPEED) * min(mag / 0.35, 1.0))

            if direction != prev_dir or abs(speed - prev_speed) >= 2:
                _ptz_cmd_q.put({"cmd": "ptz_start",
                                 "direction": direction, "speed": speed})
                _ptz_moving.set()
                prev_dir   = direction
                prev_speed = speed


# ── Tkinter app ───────────────────────────────────────────────────────────────

class App(tk.Tk):
    def __init__(self, host: str):
        super().__init__()
        self.title("Gumball Tracker")
        self.configure(bg=BG)
        self.resizable(False, False)
        self.protocol("WM_DELETE_WINDOW", self._quit)

        self._photo       = None   # PhotoImage GC guard
        self._img_item    = None   # canvas image item (created in _build_video)
        self._dets_cache  = []     # snapshot for click hit-testing
        self._fps         = 0.0
        self._conn        = {"stream": False, "detect": False, "ptz": False}
        self._dots        = {}

        # "start tracking" flow -- waiting for ptz_get_position response
        self._home_pending  = False
        self._pending_tid   = None
        self._pending_label = ""

        self._build_ui()

        _launch_async(_stream_loop, host)
        _launch_async(_detect_loop, host)
        _launch_async(_ptz_loop,    host)
        _launch(_tracking_thread)

        self._tick()

    # ── Quit ──────────────────────────────────────────────────────────────────

    def _quit(self):
        _stop.set()
        self.destroy()

    # ── UI construction ───────────────────────────────────────────────────────

    def _build_ui(self):
        hdr = tk.Frame(self, bg=BG2, height=42)
        hdr.pack(fill=tk.X)
        hdr.pack_propagate(False)

        tk.Label(hdr, text="Gumball  Tracker",
                 bg=BG2, fg=FG, font=("Arial", 12, "bold")).pack(side=tk.LEFT, padx=14)

        for key, name in (("detect", "Detect"), ("ptz", "PTZ"), ("stream", "Stream")):
            f   = tk.Frame(hdr, bg=BG2)
            f.pack(side=tk.RIGHT, padx=8, pady=10)
            dot = tk.Label(f, text="●", bg=BG2, fg=FG_DIM, font=("Arial", 11))
            dot.pack(side=tk.LEFT)
            tk.Label(f, text=name, bg=BG2, fg=FG_DIM,
                     font=("Arial", 9)).pack(side=tk.LEFT, padx=(2, 0))
            self._dots[key] = dot

        body = tk.Frame(self, bg=BG)
        body.pack(fill=tk.BOTH, padx=10, pady=(8, 8))

        self._build_video(body)
        self._build_panel(body)

    def _build_video(self, parent):
        lf = tk.Frame(parent, bg=BG)
        lf.pack(side=tk.LEFT)

        self._canvas = tk.Canvas(lf, width=CANVAS_W, height=CANVAS_H,
                                 bg="black", highlightthickness=0, cursor="crosshair")
        self._canvas.pack()
        self._canvas.bind("<Button-1>", self._on_canvas_click)

        # Create placeholder items once; update each tick rather than recreating
        self._img_item = self._canvas.create_image(0, 0, anchor=tk.NW)

        self._fps_lbl = tk.Label(lf, text="Stream: --",
                                 bg=BG, fg=FG_DIM, font=("Courier", 9))
        self._fps_lbl.pack(anchor=tk.W, pady=(3, 0))

    def _build_panel(self, parent):
        rp = tk.Frame(parent, bg=BG, padx=12)
        rp.pack(side=tk.LEFT, fill=tk.Y, anchor=tk.N)

        def section(label):
            tk.Frame(rp, bg=BG3, height=1).pack(fill=tk.X, pady=(10, 4))
            tk.Label(rp, text=label, bg=BG, fg=ACCENT,
                     font=("Arial", 9, "bold")).pack(anchor=tk.W)

        section("TRACKING")

        self._track_lbl = tk.Label(
            rp, text="Not tracking",
            bg=BG, fg=FG_DIM, font=("Courier", 10),
            wraplength=210, justify=tk.LEFT,
        )
        self._track_lbl.pack(anchor=tk.W, pady=(2, 8))

        bstyle = dict(bg=BG3, fg=FG, font=("Arial", 9),
                      relief=tk.FLAT, padx=10, pady=4, cursor="hand2")
        tk.Button(rp, text="Stop / Go Home", **bstyle,
                  command=self._stop_tracking).pack(anchor=tk.W)

        section("OFFSET")
        self._offset_lbl = tk.Label(rp, text="x:  --      y:  --",
                                    bg=BG, fg=FG, font=("Courier", 9))
        self._offset_lbl.pack(anchor=tk.W, pady=(2, 0))
        self._audit_lbl = tk.Label(rp, text="color: --",
                                   bg=BG, fg=FG_DIM, font=("Courier", 9))
        self._audit_lbl.pack(anchor=tk.W, pady=(1, 0))

        section("TUNING")

        def _slider(label, from_, to, resolution, key, fmt=str):
            tk.Label(rp, text=label, bg=BG, fg=FG_DIM,
                     font=("Arial", 8)).pack(anchor=tk.W, pady=(4, 0))
            s = tk.Scale(
                rp, from_=from_, to=to, resolution=resolution,
                orient=tk.HORIZONTAL, length=210,
                bg=BG, fg=FG, troughcolor=BG3, highlightthickness=0,
                showvalue=True, font=("Courier", 8),
                command=lambda v: _tune.update({key: fmt(v)}),
            )
            s.set(_tune[key])
            s.pack(anchor=tk.W)
            return s

        self._speed_slider = _slider("Max speed  (1-10)", 1, 10, 1,
                                     "max_speed", int)
        self._dz_slider    = _slider("Dead zone  (0.05-0.30)", 0.05, 0.30, 0.01,
                                     "dead_zone", float)

        section("HOW TO USE")
        for line in (
            "Click a box to lock on.",
            "Camera centers & follows.",
            "Lost -> returns to start.",
            "Orange box = active track.",
        ):
            tk.Label(rp, text=line, bg=BG, fg=FG_DIM,
                     font=("Arial", 8), wraplength=210,
                     justify=tk.LEFT).pack(anchor=tk.W, pady=1)

    # ── Canvas click handler ──────────────────────────────────────────────────

    def _on_canvas_click(self, event):
        if self._home_pending:
            return  # already waiting for a position query to complete
        nx = event.x / CANVAS_W
        ny = event.y / CANVAS_H
        for det in self._dets_cache:
            bb = det.get("boundingBox", {})
            x  = bb.get("x",      0)
            y  = bb.get("y",      0)
            w  = bb.get("width",  0)
            h  = bb.get("height", 0)
            if x <= nx <= x + w and y <= ny <= y + h:
                tid   = det.get("trackId", 0)
                label = det.get("label", "?")
                if tid > 0:
                    self._begin_track(tid, label)
                return

    def _begin_track(self, tid: int, label: str):
        """
        Initiate a new lock-on: clear current track state, stop motion,
        then send ptz_get_position so we can save the home position.
        Tracking only starts after the position response arrives in _tick().
        """
        # Clear any active tracking so the tracking thread stops sending commands
        with _track_lock:
            _track_state.update({
                "track_id":    None,
                "track_label": "",
                "home_pos":    None,
                "offset":      (0.0, 0.0),
            })

        # Drain stale tracking commands from previous session
        while not _ptz_cmd_q.empty():
            try:
                _ptz_cmd_q.get_nowait()
            except queue.Empty:
                break

        _ptz_cmd_q.put({"cmd": "ptz_stop"})
        _ptz_cmd_q.put({"cmd": "ptz_get_position"})

        self._home_pending  = True
        self._pending_tid   = tid
        self._pending_label = label

    def _stop_tracking(self):
        """Stop tracking and return camera to the saved home position."""
        with _track_lock:
            home = _track_state["home_pos"]
            _track_state.update({
                "track_id":     None,
                "track_label":  "",
                "home_pos":     None,
                "offset":       (0.0, 0.0),
                "auditor_score": 1.0,
            })
        auditor.clear()
        _ptz_moving.clear()
        self._home_pending = False
        _ptz_cmd_q.put({"cmd": "ptz_stop"})
        if home:
            _ptz_cmd_q.put({"cmd": "ptz_goto", "x": home["x"], "y": home["y"]})

    # ── Main-thread tick (~30 Hz) ─────────────────────────────────────────────

    def _tick(self):
        # ── Connection dots ───────────────────────────────────────────────────
        while not _status_q.empty():
            try:
                key, ok = _status_q.get_nowait()
                self._conn[key] = ok
            except queue.Empty:
                break
        for key, dot in self._dots.items():
            dot.config(fg=GREEN if self._conn[key] else RED)

        # ── FPS readout ───────────────────────────────────────────────────────
        while not _frame_q.empty():
            try:
                _, fps = _frame_q.get_nowait()
                self._fps = fps
            except queue.Empty:
                break
        self._fps_lbl.config(text=f"Stream: {self._fps:.1f} fps")

        # ── Current detections snapshot (for click hit-test) ──────────────────
        with _dets_lock:
            dets = list(_latest_dets)
        self._dets_cache = dets

        with _track_lock:
            active_tid = _track_state["track_id"]

        # ── Render frame + bounding-box overlays ──────────────────────────────
        with _lock:
            jpeg = _state["jpeg"]

        if jpeg is not None:
            img = Image.open(BytesIO(jpeg)).convert("RGB")
            img = img.resize((CANVAS_W, CANVAS_H))
            draw = ImageDraw.Draw(img)

            for det in dets:
                bb    = det.get("boundingBox", {})
                bx    = int(bb.get("x",      0) * CANVAS_W)
                by    = int(bb.get("y",      0) * CANVAS_H)
                bw    = int(bb.get("width",  0) * CANVAS_W)
                bh    = int(bb.get("height", 0) * CANVAS_H)
                tid   = det.get("trackId",    0)
                label = det.get("label",     "?")
                conf  = det.get("confidence", 0.0)

                is_target = (tid == active_tid)
                color     = ORANGE if is_target else _box_color(tid)
                width     = 3 if is_target else 2

                draw.rectangle([bx, by, bx + bw, by + bh], outline=color, width=width)
                draw.text((bx + 4, by + 2), f"{label} {conf:.0%}", fill=color)

            self._photo = ImageTk.PhotoImage(img)
            self._canvas.itemconfigure(self._img_item, image=self._photo)

            # Crosshair at frame center (delete + redraw each tick)
            self._canvas.delete("xhair")
            cx, cy = CANVAS_W // 2, CANVAS_H // 2
            for x0, y0, x1, y1 in (
                (cx - 14, cy, cx - 4, cy),
                (cx + 4,  cy, cx + 14, cy),
                (cx, cy - 14, cx, cy - 4),
                (cx, cy + 4,  cx, cy + 14),
            ):
                self._canvas.create_line(x0, y0, x1, y1,
                                         fill="#888888", width=1, tags="xhair")

        # ── PTZ position response -> complete home-pos handshake ──────────────
        while not _ptz_resp_q.empty():
            try:
                resp = _ptz_resp_q.get_nowait()
                if (self._home_pending
                        and resp.get("ok")
                        and "x" in resp
                        and "y" in resp):
                    home = {"x": resp["x"], "y": resp["y"]}
                    with _track_lock:
                        _track_state.update({
                            "track_id":    self._pending_tid,
                            "track_label": self._pending_label,
                            "home_pos":    home,
                            "last_seen":   time.time(),
                        })
                    self._home_pending = False
            except queue.Empty:
                break

        # ── Tracking status labels ────────────────────────────────────────────
        with _track_lock:
            tid           = _track_state["track_id"]
            label         = _track_state["track_label"]
            offset        = _track_state["offset"]
            auditor_score = _track_state["auditor_score"]

        if self._home_pending:
            self._track_lbl.config(
                text=f"Locking on...\n({self._pending_label})",
                fg=YELLOW,
            )
            self._offset_lbl.config(text="x:  --      y:  --")
            self._audit_lbl.config(text="color: --", fg=FG_DIM)
        elif tid is not None:
            self._track_lbl.config(
                text=f"Tracking: {label}  [ID {tid}]",
                fg=ORANGE,
            )
            dx, dy = offset
            self._offset_lbl.config(text=f"x: {dx:+.3f}    y: {dy:+.3f}")
            score_fg = GREEN if auditor_score >= 0.55 else YELLOW if auditor_score >= 0.35 else RED
            self._audit_lbl.config(text=f"color: {auditor_score:.2f}", fg=score_fg)
        else:
            self._track_lbl.config(text="Not tracking", fg=FG_DIM)
            self._offset_lbl.config(text="x:  --      y:  --")
            self._audit_lbl.config(text="color: --", fg=FG_DIM)

        self.after(UI_MS, self._tick)


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    host = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_HOST
    App(host).mainloop()
