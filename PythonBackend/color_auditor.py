"""
color_auditor.py -- HSV histogram identity guard for object tracking.

Maintains a per-track-ID color "anchor" (normalized HSV histogram).
On each frame, compares the current crop to the anchor via Pearson
correlation.  If the score drops below threshold, the ID is flagged as
a potential swap and best_match() can be called to find the correct ID.

The anchor is updated slowly via an exponential moving average (EMA) to
tolerate gradual lighting changes without losing identity.
"""

import cv2
import numpy as np


class ColorAuditor:
    def __init__(
        self,
        threshold: float  = 0.55,  # correlation below this -> flag as swap
        alpha: float      = 0.10,  # EMA learning rate for anchor drift
        min_crop_px: int  = 20,    # skip audit for crops smaller than this
    ):
        self.threshold   = threshold
        self.alpha       = alpha
        self.min_crop_px = min_crop_px
        self.anchors: dict[int, np.ndarray] = {}  # track_id -> histogram

    # ── Public API ────────────────────────────────────────────────────────────

    def check_identity(self, track_id: int, crop: np.ndarray) -> tuple[bool, float]:
        """
        Compare crop to stored anchor for track_id.

        Returns:
            (is_valid, score)
            is_valid: True if color matches anchor or this is the first sighting
            score:    Pearson correlation in [-1, 1]; 1.0 = perfect match
        """
        if crop is None or crop.size == 0:
            return True, 1.0
        if crop.shape[0] < self.min_crop_px or crop.shape[1] < self.min_crop_px:
            return True, 1.0  # too small to audit reliably; skip

        current = self._hist(crop)

        if track_id not in self.anchors:
            self.anchors[track_id] = current
            return True, 1.0

        score = float(cv2.compareHist(
            self.anchors[track_id], current, cv2.HISTCMP_CORREL
        ))

        if score >= self.threshold:
            # Slowly drift anchor toward current appearance (lighting tolerance)
            self.anchors[track_id] = (
                (1.0 - self.alpha) * self.anchors[track_id]
                + self.alpha * current
            )
            return True, score

        return False, score

    def best_match(self, crop: np.ndarray) -> tuple[int | None, float]:
        """
        Find the anchor whose color best matches crop.

        Returns:
            (track_id, score) of the best match, or (None, 0.0) if store is empty.
        """
        if crop is None or crop.size == 0 or not self.anchors:
            return None, 0.0

        current   = self._hist(crop)
        best_id   = None
        best_score = -1.0

        for tid, anchor in self.anchors.items():
            s = float(cv2.compareHist(anchor, current, cv2.HISTCMP_CORREL))
            if s > best_score:
                best_id, best_score = tid, s

        return best_id, best_score

    def remove(self, track_id: int) -> None:
        """Drop the anchor for a track that is no longer relevant."""
        self.anchors.pop(track_id, None)

    def clear(self) -> None:
        """Remove all anchors (call when stopping a tracking session)."""
        self.anchors.clear()

    # ── Internal ──────────────────────────────────────────────────────────────

    @staticmethod
    def _hist(crop: np.ndarray) -> np.ndarray:
        """Normalized 2-D HSV histogram using the inner 70% of the crop.

        Trimming the border reduces background contamination when the
        bounding box is slightly larger than the object or the camera
        is panning (background pixels shift while the object color stays
        the same).
        """
        h, w = crop.shape[:2]
        # Trim 15% off each edge -> keep central 70%
        dy = max(1, int(h * 0.15))
        dx = max(1, int(w * 0.15))
        inner = crop[dy:h - dy, dx:w - dx]
        if inner.size == 0:
            inner = crop  # fallback: box too small to trim
        hsv  = cv2.cvtColor(inner, cv2.COLOR_BGR2HSV)
        hist = cv2.calcHist(
            [hsv], [0, 1], None,
            [180, 256],
            [0, 180, 0, 256],
        )
        return cv2.normalize(hist, hist).flatten()
