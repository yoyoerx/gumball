using UnityEngine;

public interface IGimbalController
{
    bool IsConnected { get; }
    void Connect();
    void Disconnect();

    // Absolute position control (degrees)
    void SetAngles(float yaw, float pitch, float roll);

    // Velocity control (degrees/sec) — preferred for joystick input
    void SetAngularVelocity(float yawRate, float pitchRate);

    void SetFollowMode(bool enabled);

    // Point camera toward a normalized image coordinate [0,1]
    void LockTarget(Vector2 normalizedImageCoord);
}
