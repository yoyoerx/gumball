"""
Drive the camera to each physical limit, detect it via position stasis,
then probe the wall with one more move command to capture the error code.
"""

import time
import sys
import os
import logging

sys.path.insert(0, os.path.dirname(__file__))
from kasa_motor_control import KasaMotorControl, _LINKIE_URL, _encode, _decode

import requests, urllib3
urllib3.disable_warnings()

logging.basicConfig(level=logging.WARNING)

SPEED          = 5
POLL_INTERVAL  = 0.3   # seconds between position polls while moving
STASIS_COUNT   = 3     # consecutive unchanged reads = wall hit


def probe_raw(motor, direction):
    """Send a move and return the raw decoded response to see err_code."""
    cmd = {"smartlife.cam.ipcamera.ptz": {"set_move": {"direction": direction, "speed": SPEED}}}
    return motor._send_raw(cmd)


def find_limit(motor, direction, label):
    print(f"\n--- Finding {label} limit ---")
    motor.start_moving(direction, speed=SPEED)

    prev = motor.get_position()
    unchanged = 0

    while unchanged < STASIS_COUNT:
        time.sleep(POLL_INTERVAL)
        pos = motor.get_position()
        print(f"  x={pos['x']:5d}  y={pos['y']:5d}")
        if pos['x'] == prev['x'] and pos['y'] == prev['y']:
            unchanged += 1
        else:
            unchanged = 0
        prev = pos

    motor.stop_moving()
    time.sleep(0.2)

    # Probe the wall — camera should return a non-zero err_code here
    raw = probe_raw(motor, direction)
    motor.stop_moving()

    ptz = raw.get("smartlife.cam.ipcamera.ptz", {}) if raw else {}
    move_result = ptz.get("set_move", {})
    err_code = move_result.get("err_code", "N/A")
    print(f"  Wall probe err_code: {err_code}  (full: {move_result})")
    print(f"  Limit position: x={pos['x']}  y={pos['y']}")
    return pos, err_code


motor = KasaMotorControl()
start = motor.get_position()
print(f"Start: x={start['x']}  y={start['y']}")

limits = {}
limits['right'],  ec_r = find_limit(motor, 'right',  'Right (x max)')
limits['left'],   ec_l = find_limit(motor, 'left',   'Left  (x min)')
limits['top'],    ec_u = find_limit(motor, 'top',    'Up    (y max)')
limits['bottom'], ec_d = find_limit(motor, 'bottom', 'Down  (y min)')

x_min = limits['left']['x']
x_max = limits['right']['x']
y_min = limits['bottom']['y']
y_max = limits['top']['y']

print("\n========== Movement Extremes ==========")
print(f"  X  left  limit : {x_min:5d}   (err_code={ec_l})")
print(f"  X  right limit : {x_max:5d}   (err_code={ec_r})")
print(f"  X  span        : {x_max - x_min}")
print(f"  Y  down  limit : {y_min:5d}   (err_code={ec_d})")
print(f"  Y  up    limit : {y_max:5d}   (err_code={ec_u})")
print(f"  Y  span        : {y_max - y_min}")
print("========================================")

motor.release()
