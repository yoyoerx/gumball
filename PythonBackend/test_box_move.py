import time
import logging
import sys
import os

sys.path.insert(0, os.path.dirname(__file__))
from kasa_motor_control import KasaMotorControl

logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

STEP_SECS   = 1.0
SPEED       = 5
CORNER_HOLD = 0.5  # pause at each corner after stopping

motor = KasaMotorControl()

pos = motor.get_position()
print(f"Start position: x={pos['x']:4d}  y={pos['y']:4d}")

moves = [
    ("right",  "Right"),
    ("bottom", "Down"),
    ("left",   "Left"),
    ("top",    "Up"),
]

for direction, label in moves:
    print(f"\n  Moving {label}...")
    ok = motor.start_moving(direction, speed=SPEED)
    if not ok:
        print(f"FAILED on {label} — aborting")
        motor.stop_moving()
        motor.release()
        sys.exit(1)
    time.sleep(STEP_SECS)
    motor.stop_moving()
    time.sleep(CORNER_HOLD)
    pos = motor.get_position()
    print(f"  Corner position: x={pos['x']:4d}  y={pos['y']:4d}")

print(f"\nEnd position:   x={pos['x']:4d}  y={pos['y']:4d}")
motor.release()
print("Box move complete.")
