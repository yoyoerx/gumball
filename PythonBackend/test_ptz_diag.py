"""
Diagnostic: check rectify state, show raw move response, try set_motor_rectify.
"""

import time
import sys
import os
import logging

sys.path.insert(0, os.path.dirname(__file__))
from kasa_motor_control import KasaMotorControl

logging.basicConfig(level=logging.DEBUG, format="%(levelname)s %(message)s")

motor = KasaMotorControl()

# 1. Current position
print("=== get_position ===")
print(motor.get_position())

# 2. Rectify state
print("\n=== get_ptz_rectify_state ===")
raw = motor._send_raw({"smartlife.cam.ipcamera.ptz": {"get_ptz_rectify_state": {}}})
print(raw)

# 3. Raw set_move to see full response
print("\n=== set_move right (raw) ===")
raw = motor._send_raw({"smartlife.cam.ipcamera.ptz": {"set_move": {"direction": "right", "speed": 5}}})
print(raw)
time.sleep(1)
motor.stop_moving()
print(f"Position after 1s right: {motor.get_position()}")

# 4. set_motor_rectify — fires motor init/calibration
print("\n=== set_motor_rectify ===")
raw = motor._send_raw({"smartlife.cam.ipcamera.ptz": {"set_motor_rectify": {}}})
print(raw)
print("Waiting 5s for rectify to complete...")
time.sleep(5)
print(f"Position after rectify: {motor.get_position()}")

# 5. Try move again after rectify
print("\n=== set_move right after rectify (raw) ===")
raw = motor._send_raw({"smartlife.cam.ipcamera.ptz": {"set_move": {"direction": "right", "speed": 5}}})
print(raw)
time.sleep(1)
motor.stop_moving()
print(f"Position after 1s right: {motor.get_position()}")

motor.release()
