#!/usr/bin/env python3
"""
measure_fps.py -- Measure raw camera frame rate and JPEG encode overhead.

Connects directly to the KC410S without the server or any UI.
Reads 60 frames, prints per-10-frame progress, then a summary.

Usage:
    python measure_fps.py
"""
import sys
import time
import cv2

sys.path.insert(0, ".")
from kasa_camera import KasaCamera
import config

print(f"Connecting to {config.KASA_CAMERA_IP}  (KASA_STREAM_TYPE={config.KASA_STREAM_TYPE}) ...")
cam = KasaCamera()
if not cam.connect():
    print("FAILED to connect -- check KASA_CAMERA_IP and credentials in .env")
    sys.exit(1)

print(f"Connected -> {cam.stream_url}")
print("Reading 60 frames, please wait...\n")

read_ms: list[float] = []
enc_ms:  list[float] = []

for i in range(60):
    t0    = time.perf_counter()
    frame = cam.read_frame()
    t1    = time.perf_counter()

    if frame is None:
        print(f"Frame {i}: None -- stream dropped early")
        break

    _, _jpeg = cv2.imencode(".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, 80])
    t2 = time.perf_counter()

    read_ms.append((t1 - t0) * 1000)
    enc_ms.append( (t2 - t1) * 1000)

    if (i + 1) % 10 == 0:
        print(
            f"  frame {i+1:3d}  "
            f"read={read_ms[-1]:6.1f} ms  "
            f"encode={enc_ms[-1]:5.1f} ms  "
            f"shape={frame.shape}"
        )

cam.release()

if not read_ms:
    print("No frames received.")
    sys.exit(1)

total_s  = sum(read_ms) / 1000
cam_fps  = len(read_ms) / total_s
avg_read = sum(read_ms) / len(read_ms)
avg_enc  = sum(enc_ms)  / len(enc_ms)
combined = avg_read + avg_enc

print(f"\n--- Results ({len(read_ms)} frames) ---")
print(f"Camera FPS        : {cam_fps:.1f}  (read time is the bottleneck if > encode)")
print(f"Avg read time     : {avg_read:.1f} ms  "
      f"(min {min(read_ms):.1f} / max {max(read_ms):.1f})")
print(f"Avg encode time   : {avg_enc:.1f} ms  "
      f"(min {min(enc_ms):.1f} / max {max(enc_ms):.1f})")
print(f"Combined avg      : {combined:.1f} ms -> {1000/combined:.1f} fps theoretical ceiling")
print()
if avg_read > avg_enc * 3:
    print("-> Camera read is the dominant cost.  The KC410S likely caps at this FPS")
    print("   on the current resolution.  Try KASA_STREAM_TYPE=sd in .env.")
elif avg_enc > avg_read:
    print("-> JPEG encode is the dominant cost.  Reduce _STREAM_QUALITY in server.py.")
else:
    print("-> Read and encode are comparable.  Both may be improvable.")
