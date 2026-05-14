using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

// Right thumbstick -> gimbal pan/tilt (suppressed while LockOnTracker is active).
// Right trigger (rising edge) -> BeginTracking best detection if idle; StopTracking if tracking.
public class GimbalInputController : MonoBehaviour
{
    [SerializeField] private KasaPTZController      gimbal;
    [SerializeField] private ObjectDetectionManager detectionManager;
    [SerializeField] public  LockOnTracker          lockOnTracker;
    [SerializeField] private float joystickSensitivity = 60f; // degrees/sec
    [SerializeField] private bool  invertPitch;

    public float JoystickSensitivity
    {
        get => joystickSensitivity;
        set => joystickSensitivity = value;
    }

    private InputDevice    _right;
    private DetectionFrame _lastFrame;
    private bool           _triggerWasDown;

    private void Start()
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, devices);
        if (devices.Count > 0) _right = devices[0];

        InputDevices.deviceConnected += OnDeviceConnected;
        if (detectionManager) detectionManager.OnDetectionsUpdated += f => _lastFrame = f;
    }

    private void OnDeviceConnected(InputDevice device)
    {
        if (device.characteristics.HasFlag(InputDeviceCharacteristics.Right |
                                            InputDeviceCharacteristics.Controller))
            _right = device;
    }

    private void Update()
    {
        if (!_right.isValid || !gimbal.IsConnected) return;

        bool tracking = lockOnTracker != null && lockOnTracker.IsTracking;

        // Thumbstick: suppressed while tracker is active (tracker drives the gimbal)
        if (!tracking)
        {
            if (_right.TryGetFeatureValue(CommonUsages.primary2DAxis, out var stick) &&
                stick.sqrMagnitude > 0.01f)
            {
                float yawRate   = stick.x * joystickSensitivity;
                float pitchRate = (invertPitch ? -stick.y : stick.y) * joystickSensitivity;
                gimbal.SetAngularVelocity(yawRate, pitchRate);
            }
            else
            {
                gimbal.SetAngularVelocity(0f, 0f);
            }
        }

        // Right trigger rising edge: toggle tracking
        _right.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerDown);
        if (triggerDown && !_triggerWasDown)
        {
            if (tracking)
                lockOnTracker?.StopTracking();
            else
                TryBeginTracking();
        }
        _triggerWasDown = triggerDown;
    }

    private void TryBeginTracking()
    {
        if (lockOnTracker == null) return;
        if (_lastFrame == null || _lastFrame.detections.Count == 0) return;

        // Lock on the detection closest to the frame center
        Detection best     = null;
        float     bestDist = float.MaxValue;
        foreach (var d in _lastFrame.detections)
        {
            var bb  = d.boundingBox;
            float cx = bb.x + bb.width  * 0.5f - 0.5f;
            float cy = bb.y + bb.height * 0.5f - 0.5f;
            float dist = cx * cx + cy * cy;
            if (dist < bestDist) { best = d; bestDist = dist; }
        }

        if (best != null)
            lockOnTracker.BeginTracking(best);
    }

    private void OnDestroy() => InputDevices.deviceConnected -= OnDeviceConnected;
}
