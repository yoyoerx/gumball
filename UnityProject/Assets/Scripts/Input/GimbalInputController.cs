using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

// Right thumbstick → gimbal pan/tilt.
// Right trigger hold → lock-on: sends the center detection's normalized coord.
public class GimbalInputController : MonoBehaviour
{
    [SerializeField] private KasaPTZController gimbal;
    [SerializeField] private ObjectDetectionManager detectionManager;
    [SerializeField] private float joystickSensitivity = 60f; // degrees/sec
    [SerializeField] private bool invertPitch;

    public float JoystickSensitivity
    {
        get => joystickSensitivity;
        set => joystickSensitivity = value;
    }

    private InputDevice _right;
    private DetectionFrame _lastFrame;

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

        if (_right.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger) && trigger)
            LockOnBestDetection();
    }

    private void LockOnBestDetection()
    {
        if (_lastFrame == null || _lastFrame.detections.Count == 0) return;

        var best = _lastFrame.detections[0];
        var bb = best.boundingBox;
        var center = new Vector2(bb.x + bb.width / 2f, bb.y + bb.height / 2f);
        gimbal.LockTarget(center);
    }

    private void OnDestroy() => InputDevices.deviceConnected -= OnDeviceConnected;
}
