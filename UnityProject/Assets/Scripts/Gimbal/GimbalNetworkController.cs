using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

// UDP command protocol — swap the command strings to match your gimbal's API.
// Default strings are generic; replace with SimpleBGC binary framing or your custom format.
public class GimbalNetworkController : MonoBehaviour, IGimbalController
{
    [SerializeField] private string gimbalHost = "192.168.1.100";
    [SerializeField] private int gimbalPort = 8888;

    public bool IsConnected { get; private set; }

    // Last-commanded angles — updated by SetAngles; shown in the control panel.
    public float CommandedYaw   { get; private set; }
    public float CommandedPitch { get; private set; }
    public float CommandedRoll  { get; private set; }

    private UdpClient _udp;
    private IPEndPoint _endpoint;

    private void Start() => Connect();

    public void Connect()
    {
        try
        {
            _endpoint = new IPEndPoint(IPAddress.Parse(gimbalHost), gimbalPort);
            _udp = new UdpClient();
            IsConnected = true;
            Debug.Log($"[Gimbal] UDP connected → {gimbalHost}:{gimbalPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Gimbal] Connect failed: {e.Message}");
        }
    }

    public void Disconnect()
    {
        _udp?.Close();
        IsConnected = false;
    }

    public void SetAngles(float yaw, float pitch, float roll)
    {
        CommandedYaw   = yaw;
        CommandedPitch = pitch;
        CommandedRoll  = roll;
        Send($"SET_ANGLE {yaw:F2} {pitch:F2} {roll:F2}");
    }

    public void SetAngularVelocity(float yawRate, float pitchRate)
        => Send($"SET_RATE {yawRate:F2} {pitchRate:F2}");

    public void SetFollowMode(bool enabled)
        => Send($"FOLLOW {(enabled ? 1 : 0)}");

    public void LockTarget(Vector2 coord)
        => Send($"LOCK_TARGET {coord.x:F4} {coord.y:F4}");

    private void Send(string cmd)
    {
        if (!IsConnected) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(cmd);
            _udp.Send(bytes, bytes.Length, _endpoint);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Gimbal] Send failed: {e.Message}");
        }
    }

    private void OnDestroy() => Disconnect();
}
