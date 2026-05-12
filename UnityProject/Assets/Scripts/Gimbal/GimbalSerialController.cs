using UnityEngine;

// Stub for direct USB/UART gimbal connection (e.g., SimpleBGC, Gremsy, custom firmware).
// Implement protocol-specific framing and restore System.IO.Ports when ready.
public class GimbalSerialController : MonoBehaviour, IGimbalController
{
    [SerializeField] private string portName = "COM3";
    [SerializeField] private int baudRate = 115200;

    public bool IsConnected => _connected;

    private bool _connected;

    public void Connect()
    {
        _connected = true;
        Debug.Log($"[Gimbal] Serial stub connected on {portName}@{baudRate}");
    }

    public void Disconnect() => _connected = false;

    public void SetAngles(float yaw, float pitch, float roll)
        => throw new System.NotImplementedException("Implement serial protocol framing");

    public void SetAngularVelocity(float yawRate, float pitchRate)
        => throw new System.NotImplementedException("Implement serial protocol framing");

    public void SetFollowMode(bool enabled)
        => throw new System.NotImplementedException("Implement serial protocol framing");

    public void LockTarget(Vector2 coord)
        => throw new System.NotImplementedException("Implement serial protocol framing");

    private void OnDestroy() => Disconnect();
}
