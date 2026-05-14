import csv
import os
import time
from dataclasses import dataclass, field, asdict
from datetime import datetime
from typing import List

import cv2
import numpy as np
from ultralytics import YOLO

import config


# ── Data classes ──────────────────────────────────────────────────────────────

@dataclass
class BoundingBox:
    x: float
    y: float
    width: float
    height: float


@dataclass
class Detection:
    label: str
    confidence: float
    classId: int
    boundingBox: BoundingBox
    trackId: int = 0


@dataclass
class DetectionFrame:
    detections: List[Detection] = field(default_factory=list)
    inferenceTimeMs: float = 0.0
    source: str = "server"

    def to_dict(self):
        return asdict(self)


# ── Observation log ───────────────────────────────────────────────────────────

_LOG_PATH    = os.path.join(os.path.dirname(__file__), "observations.csv")
_LOG_HEADER  = ["track_id", "label", "start_time", "end_time", "duration_s", "max_confidence"]
_TRACK_TIMEOUT_S = 2.0   # seconds without a sighting before a track is considered ended


@dataclass
class _ActiveTrack:
    track_id: int
    label: str
    start_time: float
    last_seen: float
    max_confidence: float


def _fmt(ts: float) -> str:
    return datetime.fromtimestamp(ts).strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]


class ObservationLog:
    """
    Maintains a dict of currently-visible tracks.
    When a track disappears for > _TRACK_TIMEOUT_S seconds it is flushed
    to observations.csv with its begin/end timestamps.
    """

    def __init__(self, path: str = _LOG_PATH):
        self._path   = path
        self._active: dict[int, _ActiveTrack] = {}

        if not os.path.exists(path):
            with open(path, "w", newline="") as f:
                csv.writer(f).writerow(_LOG_HEADER)

    def update(self, detections: List[Detection]) -> None:
        now = time.time()
        seen_ids: set[int] = set()

        for det in detections:
            tid = det.trackId
            if tid <= 0:
                continue
            seen_ids.add(tid)
            if tid in self._active:
                t = self._active[tid]
                t.last_seen      = now
                t.max_confidence = max(t.max_confidence, det.confidence)
            else:
                self._active[tid] = _ActiveTrack(
                    track_id       = tid,
                    label          = det.label,
                    start_time     = now,
                    last_seen      = now,
                    max_confidence = det.confidence,
                )

        # Expire tracks that have been absent long enough
        expired = [tid for tid, t in self._active.items()
                   if now - t.last_seen > _TRACK_TIMEOUT_S]
        for tid in expired:
            self._write(self._active.pop(tid), end_time=now)

    def flush(self) -> None:
        """Close all open tracks — call on server shutdown."""
        now = time.time()
        for t in self._active.values():
            self._write(t, end_time=now)
        self._active.clear()

    def _write(self, t: _ActiveTrack, end_time: float) -> None:
        duration = round(end_time - t.start_time, 3)
        with open(self._path, "a", newline="") as f:
            csv.writer(f).writerow([
                t.track_id,
                t.label,
                _fmt(t.start_time),
                _fmt(end_time),
                duration,
                round(t.max_confidence, 3),
            ])


# ── Object detector ───────────────────────────────────────────────────────────

class ObjectDetector:
    def __init__(self):
        self.model   = YOLO(config.YOLO_MODEL)
        self.model.to(config.DEVICE)
        self.obs_log = ObservationLog()
        print(f"[Detector] {config.YOLO_MODEL} on {config.DEVICE}")
        print(f"[Detector] Observations → {_LOG_PATH}")

    def reload(self) -> None:
        self.obs_log.flush()
        self.model = YOLO(config.YOLO_MODEL)
        self.model.to(config.DEVICE)
        print(f"[Detector] Reloaded {config.YOLO_MODEL} on {config.DEVICE}")

    def detect(self, jpeg_bytes: bytes) -> DetectionFrame:
        arr   = np.frombuffer(jpeg_bytes, np.uint8)
        frame = cv2.imdecode(arr, cv2.IMREAD_COLOR)
        if frame is None:
            return DetectionFrame()

        h, w = frame.shape[:2]
        t0   = time.perf_counter()

        results = self.model.track(
            frame,
            conf    = config.CONFIDENCE,
            iou     = config.IOU,
            persist = True,
            tracker = "custom_botsort.yaml",
            verbose = False,
        )[0]

        elapsed_ms = (time.perf_counter() - t0) * 1000.0

        # Extract track IDs — None when BoT-SORT has no assignments this frame
        ids = (results.boxes.id.int().tolist()
               if results.boxes.id is not None
               else [0] * len(results.boxes))

        detections = []
        for box, tid in zip(results.boxes, ids):
            x1, y1, x2, y2 = box.xyxy[0].tolist()
            detections.append(Detection(
                label      = results.names[int(box.cls)],
                confidence = float(box.conf),
                classId    = int(box.cls),
                trackId    = int(tid),
                boundingBox = BoundingBox(
                    x      = x1 / w,
                    y      = y1 / h,
                    width  = (x2 - x1) / w,
                    height = (y2 - y1) / h,
                ),
            ))

        self.obs_log.update(detections)
        return DetectionFrame(detections=detections, inferenceTimeMs=elapsed_ms)
