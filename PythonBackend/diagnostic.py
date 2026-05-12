#!/usr/bin/env python3
"""
MetaGimbalVision — Server Diagnostic Utility

Live GUI that exercises all three server.py endpoints simultaneously:
  :8081  video stream   — displays live frames with detection overlays
  :8082  PTZ control    — D-pad + Center/Rectify buttons, live position readout
  :9000  detection      — sends frames every 0.5 s, shows tracked detections

Usage:
    python diagnostic.py [host]        # host defaults to config.SERVER_HOST

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
import tkinter.ttk as ttk
from datetime import datetime
from io import BytesIO

import websockets

try:
    from PIL import Image, ImageDraw, ImageTk
except ImportError:
    print("ERROR: Pillow is required.  pip install pillow")
    sys.exit(1)

from camera_discovery import discover_cameras, get_camera_ip, load_settings, save_settings

# ── Config ─────────────────────────────────────────────────────────────────────

try:
    import config as _cfg
    DEFAULT_HOST = _cfg.SERVER_HOST
except Exception:
    DEFAULT_HOST = "localhost"
STREAM_PORT     = 8081
PTZ_PORT        = 8082
DETECT_PORT     = 9000
VIDEO_W         = 640
VIDEO_H         = 360
DETECT_INTERVAL = 0.5   # seconds between frames sent to detector
UI_MS           = 33    # ~30 fps UI refresh
RECONNECT_S     = 3

# ── Palette ────────────────────────────────────────────────────────────────────

BG, BG2, BG3   = "#1b1d27", "#23263a", "#2e3247"
FG, FG_DIM     = "#e0e4f0", "#7a84a8"
GREEN, RED     = "#22c55e", "#ef4444"
BLUE, ACCENT   = "#3b82f6", "#6366f1"

# ── Cross-thread shared state ──────────────────────────────────────────────────

_lock        = threading.Lock()
_state       = {"jpeg": None}   # latest raw frame from stream

_status_q    = queue.Queue()          # (key, ok: bool, text)
_frame_q     = queue.Queue(maxsize=2) # (jpeg_bytes, fps)
_detect_q    = queue.Queue(maxsize=2) # list[dict] detections
_ptz_q       = queue.Queue(maxsize=8) # dict PTZ response
_log_q       = queue.Queue()          # str log lines
_ptz_cmd_q   = queue.Queue()          # dict PTZ commands from UI

_stop        = threading.Event()
_scan_q      = queue.Queue()   # list[str] camera IPs from discovery scan

# ── Helpers ────────────────────────────────────────────────────────────────────

def _ts() -> str:
    return datetime.now().strftime("%H:%M:%S.%f")[:-4]

def _log(msg: str):
    _log_q.put(f"[{_ts()}]  {msg}")

def _track_rgb(tid: int) -> tuple:
    """Unique color per track ID via golden-angle hue distribution."""
    h = (tid * 137.508) % 360 / 360
    r, g, b = colorsys.hsv_to_rgb(h, 0.85, 0.92)
    return int(r * 255), int(g * 255), int(b * 255)

def _hex(rgb: tuple) -> str:
    return "#{:02x}{:02x}{:02x}".format(*rgb)

# ── Background WebSocket coroutines ────────────────────────────────────────────

async def _stream_loop(host: str):
    url = f"ws://{host}:{STREAM_PORT}"
    while not _stop.is_set():
        try:
            async with websockets.connect(url, max_size=20_000_000) as ws:
                _status_q.put(("stream", True, "Connected"))
                _log(f"Stream connected → {url}")
                n = 0
                t0 = time.monotonic()
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
        except Exception as e:
            _status_q.put(("stream", False, str(e)))
            _log(f"Stream ✗  {e}")
        if not _stop.is_set():
            await asyncio.sleep(RECONNECT_S)
    _status_q.put(("stream", False, "–"))


async def _ptz_loop(host: str):
    url = f"ws://{host}:{PTZ_PORT}"
    while not _stop.is_set():
        try:
            async with websockets.connect(url) as ws:
                _status_q.put(("ptz", True, "Connected"))
                _log(f"PTZ connected → {url}")
                await ws.send(json.dumps({"cmd": "ptz_get_position"}))
                while not _stop.is_set():
                    while not _ptz_cmd_q.empty():
                        await ws.send(json.dumps(_ptz_cmd_q.get_nowait()))
                    try:
                        raw = await asyncio.wait_for(ws.recv(), timeout=0.05)
                        try:
                            _ptz_q.put_nowait(json.loads(raw))
                        except queue.Full:
                            pass
                    except asyncio.TimeoutError:
                        await asyncio.sleep(0.02)
        except Exception as e:
            _status_q.put(("ptz", False, str(e)))
            _log(f"PTZ ✗  {e}")
        if not _stop.is_set():
            await asyncio.sleep(RECONNECT_S)
    _status_q.put(("ptz", False, "–"))


async def _detect_loop(host: str):
    url = f"ws://{host}:{DETECT_PORT}"
    while not _stop.is_set():
        try:
            async with websockets.connect(url, max_size=5_000_000) as ws:
                _status_q.put(("detect", True, "Connected"))
                _log(f"Detection connected → {url}")
                last_send = 0.0
                while not _stop.is_set():
                    now = time.monotonic()
                    if now - last_send >= DETECT_INTERVAL:
                        with _lock:
                            jpeg = _state["jpeg"]
                        if jpeg is not None:
                            await ws.send(jpeg)
                            last_send = now
                    try:
                        raw = await asyncio.wait_for(ws.recv(), timeout=0.05)
                        data = json.loads(raw)
                        dets = data.get("detections", [])
                        try:
                            _detect_q.put_nowait(data)
                        except queue.Full:
                            pass
                        if dets:
                            summary = ", ".join(
                                (f"[{d.get('trackId',0)}]" if d.get('trackId') else "")
                                + f"{d['label']} {d['confidence']:.0%}"
                                for d in dets[:5]
                            )
                            _log(f"Detected ({len(dets)}): {summary}")
                    except asyncio.TimeoutError:
                        pass
        except Exception as e:
            _status_q.put(("detect", False, str(e)))
            _log(f"Detection ✗  {e}")
        if not _stop.is_set():
            await asyncio.sleep(RECONNECT_S)
    _status_q.put(("detect", False, "–"))


def _launch(coro_fn, *args):
    """Run an async coroutine on a new daemon thread with its own event loop."""
    def run():
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        loop.run_until_complete(coro_fn(*args))
        loop.close()
    threading.Thread(target=run, daemon=True).start()

# ── Tkinter UI ─────────────────────────────────────────────────────────────────

class App(tk.Tk):
    def __init__(self, host: str):
        super().__init__()
        self.title("MetaGimbalVision — Server Diagnostic")
        self.configure(bg=BG)
        self.resizable(False, False)
        self.protocol("WM_DELETE_WINDOW", self._quit)

        self._photo = None     # keep PhotoImage alive (GC guard)
        self._dets  = []       # current detection list for overlay

        self._build_ui()

        _launch(_stream_loop, host)
        _launch(_ptz_loop,    host)
        _launch(_detect_loop, host)

        self._tick()

    # ── UI construction ────────────────────────────────────────────────────────

    def _build_ui(self):
        self._build_header()
        self._build_camera_bar()
        body = tk.Frame(self, bg=BG)
        body.pack(fill=tk.BOTH, padx=10, pady=(6, 8))
        self._build_video(body)
        self._build_right(body)
        self._build_log()

    def _build_header(self):
        hdr = tk.Frame(self, bg=BG2, height=44)
        hdr.pack(fill=tk.X)
        hdr.pack_propagate(False)

        tk.Label(hdr, text="MetaGimbalVision  ·  Server Diagnostic",
                 bg=BG2, fg=FG, font=("Arial", 12, "bold")).pack(side=tk.LEFT, padx=14)

        self._dots = {}
        for key, name in (("detect", "Detect"), ("ptz", "PTZ"), ("stream", "Stream")):
            f = tk.Frame(hdr, bg=BG2)
            f.pack(side=tk.RIGHT, padx=8, pady=10)
            dot = tk.Label(f, text="●", bg=BG2, fg=FG_DIM, font=("Arial", 11))
            dot.pack(side=tk.LEFT)
            tk.Label(f, text=name, bg=BG2, fg=FG_DIM,
                     font=("Arial", 9)).pack(side=tk.LEFT, padx=(2, 0))
            self._dots[key] = dot

    def _build_camera_bar(self):
        bar = tk.Frame(self, bg=BG3, height=34)
        bar.pack(fill=tk.X)
        bar.pack_propagate(False)

        tk.Label(bar, text="Camera:", bg=BG3, fg=FG_DIM,
                 font=("Arial", 9)).pack(side=tk.LEFT, padx=(14, 4))

        current = get_camera_ip()
        self._cam_var = tk.StringVar(value=current)

        style = ttk.Style()
        style.theme_use("default")
        style.configure("Dark.TCombobox",
                        fieldbackground=BG2, background=BG2,
                        foreground=FG, selectbackground=BLUE,
                        arrowcolor=FG_DIM)

        self._cam_combo = ttk.Combobox(bar, textvariable=self._cam_var,
                                       width=15, style="Dark.TCombobox",
                                       values=[current], state="readonly")
        self._cam_combo.pack(side=tk.LEFT, pady=5)
        self._cam_combo.bind("<<ComboboxSelected>>", self._on_camera_select)

        self._scan_btn = tk.Button(bar, text="Scan", bg=BG2, fg=FG,
                                   font=("Arial", 9), relief=tk.FLAT,
                                   padx=8, cursor="hand2",
                                   command=self._start_scan)
        self._scan_btn.pack(side=tk.LEFT, padx=6)

        self._cam_status = tk.Label(bar, text="", bg=BG3, fg=FG_DIM,
                                    font=("Arial", 9))
        self._cam_status.pack(side=tk.LEFT)

        tk.Label(bar, text=f"default: {current}", bg=BG3, fg=FG_DIM,
                 font=("Arial", 8)).pack(side=tk.RIGHT, padx=14)
        self._default_lbl = bar.winfo_children()[-1]

    def _start_scan(self):
        self._scan_btn.configure(state=tk.DISABLED, text="Scanning…")
        self._cam_status.configure(text="", fg=FG_DIM)
        threading.Thread(target=self._do_scan, daemon=True).start()

    def _do_scan(self):
        found = discover_cameras()
        _scan_q.put(found)

    def _on_camera_select(self, _=None):
        ip = self._cam_var.get()
        save_settings({"camera_ip": ip})
        self._cam_status.configure(text="Saved — restart server to apply", fg=ACCENT)
        self._default_lbl.configure(text=f"default: {ip}")
        _log(f"Camera default set to {ip} — restart server to apply")

    def _build_video(self, parent):
        vf = tk.Frame(parent, bg="#000")
        vf.pack(side=tk.LEFT)

        self._canvas = tk.Canvas(vf, width=VIDEO_W, height=VIDEO_H,
                                 bg="#000", highlightthickness=0)
        self._canvas.pack()
        self._canvas.create_text(VIDEO_W // 2, VIDEO_H // 2,
                                 text="Connecting to stream…",
                                 fill=FG_DIM, font=("Arial", 14), tags="placeholder")

        self._fps_var = tk.StringVar(value="– fps")
        tk.Label(vf, textvariable=self._fps_var, bg=BG, fg=FG_DIM,
                 font=("Courier", 9)).pack(anchor=tk.W, pady=(3, 0))

    def _build_right(self, parent):
        rp = tk.Frame(parent, bg=BG, width=270)
        rp.pack(side=tk.LEFT, padx=(12, 0), fill=tk.Y)
        rp.pack_propagate(False)

        # Detections section
        tk.Label(rp, text="DETECTIONS", bg=BG, fg=ACCENT,
                 font=("Arial", 9, "bold")).pack(anchor=tk.W)

        det_box = tk.Frame(rp, bg=BG2)
        det_box.pack(fill=tk.X, pady=(2, 8))
        self._det_rows = []
        for _ in range(8):
            w = tk.Label(det_box, text="", bg=BG2, fg=FG_DIM,
                         font=("Courier", 9), anchor=tk.W, padx=6)
            w.pack(fill=tk.X, pady=1)
            self._det_rows.append(w)

        tk.Frame(rp, bg=BG3, height=1).pack(fill=tk.X, pady=4)

        # PTZ section
        tk.Label(rp, text="PTZ CONTROLS", bg=BG, fg=ACCENT,
                 font=("Arial", 9, "bold")).pack(anchor=tk.W)

        pr = tk.Frame(rp, bg=BG)
        pr.pack(anchor=tk.W, pady=(2, 6))
        tk.Label(pr, text="Position", bg=BG, fg=FG_DIM,
                 font=("Courier", 9)).pack(side=tk.LEFT)
        self._pos_lbl = tk.Label(pr, text="  x:—  y:—", bg=BG, fg=FG,
                                 font=("Courier", 9, "bold"))
        self._pos_lbl.pack(side=tk.LEFT)

        # D-pad
        dpad = tk.Frame(rp, bg=BG)
        dpad.pack()
        bc = dict(bg=BG3, fg=FG, font=("Arial", 14, "bold"), width=3, height=1,
                  relief=tk.FLAT, activebackground=BLUE, activeforeground="#fff",
                  borderwidth=0, cursor="hand2")
        self._btn_u = tk.Button(dpad, text="↑", **bc)
        self._btn_l = tk.Button(dpad, text="←", **bc)
        self._btn_s = tk.Button(dpad, text="■", bg=BG3, fg=RED,
                                font=("Arial", 14, "bold"), width=3, height=1,
                                relief=tk.FLAT, borderwidth=0, cursor="hand2")
        self._btn_r = tk.Button(dpad, text="→", **bc)
        self._btn_d = tk.Button(dpad, text="↓", **bc)

        self._btn_u.grid(row=0, column=1, padx=2, pady=2)
        self._btn_l.grid(row=1, column=0, padx=2, pady=2)
        self._btn_s.grid(row=1, column=1, padx=2, pady=2)
        self._btn_r.grid(row=1, column=2, padx=2, pady=2)
        self._btn_d.grid(row=2, column=1, padx=2, pady=2)

        for btn, direction in ((self._btn_u, "top"), (self._btn_l, "left"),
                               (self._btn_r, "right"), (self._btn_d, "bottom")):
            btn.bind("<ButtonPress-1>",   lambda e, d=direction: self._ptz_start(d))
            btn.bind("<ButtonRelease-1>", lambda e:               self._ptz_stop())

        self._btn_s.configure(command=self._ptz_stop)

        # Center / Rectify
        util = tk.Frame(rp, bg=BG)
        util.pack(pady=(10, 0), anchor=tk.W)
        for label, cmd in (("Center",  {"cmd": "ptz_goto",   "x": 78, "y": 281}),
                            ("Rectify", {"cmd": "ptz_rectify"})):
            tk.Button(util, text=label, bg=BG3, fg=FG, font=("Arial", 10),
                      relief=tk.FLAT, padx=10, pady=5, cursor="hand2",
                      command=lambda c=cmd: _ptz_cmd_q.put(c)).pack(side=tk.LEFT, padx=(0, 6))

        tk.Frame(rp, bg=BG3, height=1).pack(fill=tk.X, pady=(12, 4))

        # Inference time
        self._infer_var = tk.StringVar(value="Inference: – ms")
        tk.Label(rp, textvariable=self._infer_var, bg=BG, fg=FG_DIM,
                 font=("Courier", 9)).pack(anchor=tk.W)

    def _build_log(self):
        lf = tk.Frame(self, bg=BG2)
        lf.pack(fill=tk.X, padx=10, pady=(0, 8))
        tk.Label(lf, text="LOG", bg=BG2, fg=ACCENT,
                 font=("Arial", 8, "bold")).pack(anchor=tk.W, padx=6, pady=(4, 0))
        self._log_text = tk.Text(lf, height=5, bg=BG2, fg=FG_DIM,
                                 font=("Courier", 8), relief=tk.FLAT,
                                 state=tk.DISABLED, wrap=tk.WORD)
        self._log_text.pack(fill=tk.X, padx=6, pady=(0, 6))

    # ── PTZ button actions ─────────────────────────────────────────────────────

    def _ptz_start(self, direction: str):
        _ptz_cmd_q.put({"cmd": "ptz_start", "direction": direction, "speed": 5})

    def _ptz_stop(self):
        _ptz_cmd_q.put({"cmd": "ptz_stop"})
        _ptz_cmd_q.put({"cmd": "ptz_get_position"})

    # ── Main update loop ───────────────────────────────────────────────────────

    def _tick(self):
        try:
            # Connection status dots
            while not _status_q.empty():
                key, ok, _ = _status_q.get_nowait()
                if key in self._dots:
                    self._dots[key].configure(fg=GREEN if ok else RED)

            # Video frame — render with current detection overlay
            while not _frame_q.empty():
                jpeg, fps = _frame_q.get_nowait()
                self._fps_var.set(f"{fps:.1f} fps")
                try:
                    self._render_frame(jpeg)
                except Exception as e:
                    _log(f"Render error: {e}")

            # New detections from server
            while not _detect_q.empty():
                data = _detect_q.get_nowait()
                dets = data.get("detections", [])
                ms   = data.get("inferenceTimeMs", 0)
                self._dets = dets
                self._update_det_list(ms)

            # PTZ responses
            while not _ptz_q.empty():
                r = _ptz_q.get_nowait()
                if r.get("ok") and "x" in r:
                    self._pos_lbl.configure(text=f"  x:{r['x']}  y:{r['y']}")

            # Log messages
            while not _log_q.empty():
                msg = _log_q.get_nowait()
                self._log_text.configure(state=tk.NORMAL)
                self._log_text.insert(tk.END, msg + "\n")
                end_line = int(self._log_text.index("end-1c").split(".")[0])
                if end_line > 300:
                    self._log_text.delete("1.0", "101.0")
                self._log_text.see(tk.END)
                self._log_text.configure(state=tk.DISABLED)

            # Camera scan results
            while not _scan_q.empty():
                found = _scan_q.get_nowait()
                current = self._cam_var.get()
                options = sorted(set([current] + found))
                self._cam_combo.configure(values=options, state="readonly")
                self._scan_btn.configure(state=tk.NORMAL, text="Scan")
                if found:
                    self._cam_status.configure(
                        text=f"Found {len(found)} camera(s)", fg=GREEN)
                else:
                    self._cam_status.configure(text="No cameras found", fg=RED)

        except Exception as e:
            print(f"_tick error: {e}")

        self.after(UI_MS, self._tick)

    # ── Rendering ──────────────────────────────────────────────────────────────

    def _render_frame(self, jpeg: bytes):
        img = Image.open(BytesIO(jpeg)).resize((VIDEO_W, VIDEO_H), Image.BILINEAR)

        if self._dets:
            draw = ImageDraw.Draw(img)
            for det in self._dets:
                bb   = det.get("boundingBox", {})
                x1   = int(bb.get("x", 0)                          * VIDEO_W)
                y1   = int(bb.get("y", 0)                          * VIDEO_H)
                x2   = int((bb.get("x", 0) + bb.get("width",  0)) * VIDEO_W)
                y2   = int((bb.get("y", 0) + bb.get("height", 0)) * VIDEO_H)
                tid  = det.get("trackId", 0)
                rgb  = _track_rgb(tid) if tid > 0 else (0, 255, 50)
                lbl  = ((f"[{tid}] " if tid > 0 else "")
                        + f"{det.get('label','?')} {det.get('confidence', 0):.0%}")

                draw.rectangle([x1, y1, x2, y2], outline=rgb, width=2)

                # Label chip
                tw = len(lbl) * 6 + 6
                ty = max(0, y1 - 16)
                draw.rectangle([x1, ty, x1 + tw, ty + 15], fill=rgb)
                draw.text((x1 + 3, ty + 1), lbl, fill=(0, 0, 0))

        self._canvas.delete("placeholder")
        self._photo = ImageTk.PhotoImage(img)
        self._canvas.create_image(0, 0, anchor=tk.NW, image=self._photo)

    def _update_det_list(self, infer_ms: float = 0):
        for i, row in enumerate(self._det_rows):
            if i < len(self._dets):
                det  = self._dets[i]
                tid  = det.get("trackId", 0)
                rgb  = _track_rgb(tid) if tid > 0 else (0, 200, 50)
                name = det.get("label", "?")
                conf = det.get("confidence", 0)
                text = (f"[{tid}] {name:<14} {conf:.0%}"
                        if tid > 0 else
                        f"     {name:<14} {conf:.0%}")
                row.configure(text=text, fg=_hex(rgb))
            else:
                row.configure(text="", fg=FG_DIM)

        if infer_ms:
            self._infer_var.set(f"Inference: {infer_ms:.1f} ms")

    # ── Shutdown ───────────────────────────────────────────────────────────────

    def _quit(self):
        _stop.set()
        self.destroy()


# ── Entry point ────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    host = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_HOST
    print(f"Connecting to server at {host}…")
    App(host).mainloop()
    _stop.set()
