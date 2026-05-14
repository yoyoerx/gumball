using System;
using UnityEngine;

/// <summary>
/// Lock-on tracking state machine — VR port of tracker.py _tracking_thread logic.
///
/// Attach to any GameObject in scene. Wire dependencies in Inspector.
///
/// State transitions:
///   Idle    --BeginTracking(det)--> Tracking
///   Tracking --target visible--> Tracking  (sends ptz_start / ptz_stop each tick)
///   Tracking --lost > lostTimeoutSecs--> Idle  (stops camera in place)
///   Tracking --StopTracking()--> Idle  (stops camera in place)
///
/// Detection rate must be fast enough (~12 Hz) for smooth control.
/// Set ObjectDetectionManager.serverOffloadIntervalSeconds = 0.08 in Inspector.
/// </summary>
public class LockOnTracker : MonoBehaviour
{
    // ── Dependencies ───────────────────────────────────────────────────────────

    [Header("Dependencies")]
    [SerializeField] public KasaPTZController gimbal;
    [SerializeField] private ObjectDetectionManager detectionManager;

    // ── Tuning ─────────────────────────────────────────────────────────────────

    [Header("Tracking Tuning")]
    [Tooltip("Normalized half-width of the center dead zone. Object inside = no move.")]
    [SerializeField] [Range(0.01f, 0.30f)] private float deadZone = 0.01f;

    [Tooltip("PTZ speed sent when target is outside dead zone (1-10).")]
    [SerializeField] [Range(1, 10)] private int maxSpeed = 4;

    [Tooltip("Seconds target must be absent before tracking is abandoned.")]
    [SerializeField] private float lostTimeoutSecs = 2f;

    // ── Public state ───────────────────────────────────────────────────────────

    public bool IsTracking   => _state == State.Tracking;
    public bool IsLost       => _state == State.Tracking && _lostTimer > 0f;
    public int  LockedTrackId { get; private set; }
    public string LockedLabel { get; private set; } = "";

    // ── Events ─────────────────────────────────────────────────────────────────

    /// Fired (on Unity main thread) when tracking begins.
    public event Action<int, string> OnTrackingStarted;  // (trackId, label)

    /// Fired when the target is re-acquired after a brief loss (label re-ID).
    public event Action<int> OnTrackIdReassigned;        // (newTrackId)

    /// Fired when the lost timeout expires and tracking is abandoned.
    public event Action OnTrackingLost;

    /// Fired when StopTracking() is called explicitly.
    public event Action OnTrackingStopped;

    // ── Private ────────────────────────────────────────────────────────────────

    private enum State { Idle, Tracking }
    private State _state = State.Idle;

    private DetectionFrame _latestFrame;
    private float          _lostTimer;      // counts up while target absent; reset each time seen
    private string         _prevDir  = "";
    private int            _prevSpeed;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Start()
    {
        if (detectionManager != null)
            detectionManager.OnDetectionsUpdated += OnDetectionsUpdated;
    }

    private void OnDestroy()
    {
        if (detectionManager != null)
            detectionManager.OnDetectionsUpdated -= OnDetectionsUpdated;
    }

    private void OnDetectionsUpdated(DetectionFrame frame)
    {
        _latestFrame = frame;
    }

    // ── Update — 60 Hz tracking tick ──────────────────────────────────────────

    private void Update()
    {
        if (_state != State.Tracking) return;
        if (_latestFrame == null)     return;

        var det = FindTarget();

        if (det == null)
        {
            // Target not found this frame
            _lostTimer += Time.deltaTime;
            if (_lostTimer >= lostTimeoutSecs)
                AbandonTracking();
            return;
        }

        // Target found — reset lost timer
        _lostTimer = 0f;

        // Compute normalized offset from frame center (positive x = right, positive y = up)
        var bb = det.boundingBox;
        float cx = bb.x + bb.width  * 0.5f;
        float cy = bb.y + bb.height * 0.5f;
        float dx = cx - 0.5f;   // positive = target right of center -> pan right
        float dy = cy - 0.5f;   // positive = target below center   -> tilt down (y-down image)

        bool inDeadZone = Mathf.Abs(dx) <= deadZone && Mathf.Abs(dy) <= deadZone;

        if (inDeadZone)
        {
            SendStop();
            return;
        }

        string dir   = ToDirection(dx, dy);
        int    speed = maxSpeed;

        if (dir == _prevDir && speed == _prevSpeed) return;

        _prevDir   = dir;
        _prevSpeed = speed;
        gimbal?.StopMoving();   // stop before direction change to avoid motor buffering
        SendStart(dir, speed);
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Begin tracking the given detection.</summary>
    public void BeginTracking(Detection det)
    {
        if (gimbal == null) return;

        gimbal.StopMoving();
        _prevDir   = "";
        _prevSpeed = 0;
        _lostTimer = 0f;

        LockedTrackId = det.trackId;
        LockedLabel   = det.label;
        _state        = State.Tracking;

        OnTrackingStarted?.Invoke(LockedTrackId, LockedLabel);
        Debug.Log($"[LockOn] Tracking started — id={LockedTrackId} label={LockedLabel}");
    }

    /// <summary>Stop tracking immediately; camera stops in place.</summary>
    public void StopTracking()
    {
        if (_state == State.Idle) return;
        _state = State.Idle;
        gimbal?.StopMoving();
        _prevDir   = "";
        _prevSpeed = 0;
        LockedTrackId = 0;
        LockedLabel   = "";
        _latestFrame  = null;

        OnTrackingStopped?.Invoke();
        Debug.Log("[LockOn] Tracking stopped by user.");
    }

    // ── Internal helpers ───────────────────────────────────────────────────────

    private Detection FindTarget()
    {
        if (_latestFrame == null) return null;
        var dets = _latestFrame.detections;

        // Fast path: find by track ID
        foreach (var d in dets)
            if (d.trackId == LockedTrackId) return d;

        // Label-based re-ID: tracker.py fallback — find same-label detection
        // closest to frame center (camera has been steering toward it).
        Detection best     = null;
        float     bestDist = float.MaxValue;
        foreach (var d in dets)
        {
            if (d.label != LockedLabel) continue;
            var bb  = d.boundingBox;
            float cx = bb.x + bb.width  * 0.5f - 0.5f;
            float cy = bb.y + bb.height * 0.5f - 0.5f;
            float dist = cx * cx + cy * cy;
            if (dist < bestDist) { best = d; bestDist = dist; }
        }

        if (best != null)
        {
            int oldId = LockedTrackId;
            LockedTrackId = best.trackId;
            OnTrackIdReassigned?.Invoke(LockedTrackId);
            Debug.Log($"[LockOn] Re-ID: track {oldId} -> {LockedTrackId} (label match)");
        }

        return best;
    }

    private void AbandonTracking()
    {
        _state = State.Idle;
        gimbal?.StopMoving();
        _prevDir      = "";
        _prevSpeed    = 0;
        LockedTrackId = 0;
        LockedLabel   = "";
        _latestFrame  = null;

        OnTrackingLost?.Invoke();
        Debug.Log("[LockOn] Target lost — camera stopped in place.");
    }

    private void SendStart(string dir, int speed)
    {
        // KasaPTZController.SetAngularVelocity maps degrees/s -> PTZ speed internally.
        // For discrete directions we bypass that and call StopMoving + a direction-mapped velocity.
        // Encode as a normalized directional vector scaled to joystick sensitivity (maxRate=60).
        // Simpler: reach into the SendRaw path via the public interface.
        // KasaPTZController doesn't expose SendRaw, so we approximate via SetAngularVelocity.
        float magnitude = speed / 10f * 60f;  // maps speed 1-10 -> 6-60 deg/s (maxRate=60)
        float nx, ny;
        DirectionToNorm(dir, out nx, out ny);
        gimbal?.SetAngularVelocity(nx * magnitude, ny * magnitude);
    }

    private void SendStop()
    {
        if (_prevDir == "" && _prevSpeed == 0) return;
        _prevDir   = "";
        _prevSpeed = 0;
        gimbal?.StopMoving();
    }

    // Converts a normalized offset (dx right, dy down-in-image = pitch-down) to a PTZ direction string.
    // Image y-down: positive dy means target is below center -> camera should tilt down.
    // PTZ pitch: SetAngularVelocity pitchRate > 0 = up (IGimbalController convention).
    private static string ToDirection(float dx, float dy)
    {
        bool right = dx >  0.05f, left = dx < -0.05f;
        bool down  = dy >  0.05f, up   = dy < -0.05f;  // dy positive = below center = tilt down
        if (up   && right) return "top right";
        if (up   && left)  return "top left";
        if (down && right) return "bottom right";
        if (down && left)  return "bottom left";
        if (up)            return "top";
        if (down)          return "bottom";
        if (right)         return "right";
        if (left)          return "left";
        return "right";
    }

    private static void DirectionToNorm(string dir, out float nx, out float ny)
    {
        nx = 0f; ny = 0f;
        switch (dir)
        {
            case "right":        nx =  1f; break;
            case "left":         nx = -1f; break;
            case "top":          ny =  1f; break;
            case "bottom":       ny = -1f; break;
            case "top right":    nx =  1f; ny =  1f; break;
            case "top left":     nx = -1f; ny =  1f; break;
            case "bottom right": nx =  1f; ny = -1f; break;
            case "bottom left":  nx = -1f; ny = -1f; break;
        }
        // Normalize diagonal
        float mag = Mathf.Sqrt(nx * nx + ny * ny);
        if (mag > 0f) { nx /= mag; ny /= mag; }
    }
}
