using System;
using System.Collections;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;

/// <summary>
/// Controls the Kasa KC410S pan/tilt camera via the Python WebSocket PTZ server.
/// Implements IGimbalController so it plugs directly into GimbalInputController and GimbalControlPanel.
///
/// Right thumbstick → SetAngularVelocity → ptz_start / ptz_stop
/// Center button    → SetAngles(0,0,0)   → ptz_goto to mid-range
/// Lock-On button   → LockTarget(coord)  → ptz_goto mapped from normalized image coord
/// </summary>
public class KasaPTZController : MonoBehaviour, IGimbalController
{
    [Header("Connection")]
    [SerializeField] string serverHost         = "YOUR_SERVER_IP"; // Set via MetaGimbalVision > Apply Network Config
    [SerializeField] int    ptzPort            = 8082;
    [SerializeField] int    reconnectDelaySecs = 3;

    [Header("Camera Coordinate Range")]
    [Tooltip("From test_find_limits.py")]
    [SerializeField] int xMin = 39,  xMax = 117;
    [SerializeField] int yMin = 243, yMax = 320;

    [Header("Input Scaling")]
    [Tooltip("Joystick rate (deg/s) that maps to camera speed 10")]
    [SerializeField] float maxRate = 60f;

    // ── Public state ───────────────────────────────────────────────────────────

    public bool IsConnected { get; private set; }
    public int  CurrentX    { get; private set; }
    public int  CurrentY    { get; private set; }

    // GimbalControlPanel reads these for its readout fields
    public float CommandedYaw   => CurrentX;
    public float CommandedPitch => CurrentY;
    public float CommandedRoll  => 0f;

    // ── Events fired when named server responses arrive ────────────────────────

    /// Fired when the server responds to get_config: (camera_ip, server_host)
    public event Action<string, string> OnConfigReceived;

    /// Fired when the server responds to scan_cameras with the discovered IP list
    public event Action<string[]> OnScanComplete;

    /// Fired when the server confirms a set_camera command
    public event Action OnCameraApplied;

    // ── Private state ──────────────────────────────────────────────────────────

    private WebSocket _ws;
    private bool      _running;
    private bool      _isMoving;
    private string    _lastDir   = "";
    private int       _lastSpeed;

    private string WsUrl => $"ws://{serverHost}:{ptzPort}";

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Start()
    {
        Connect();
        StartCoroutine(PositionPollLoop());
    }

    public void Connect()
    {
        if (_running) return;
        _ = ConnectLoop();
    }

    public void Disconnect()
    {
        _running    = false;
        IsConnected = false;
        if (_ws != null) _ = _ws.Close();
    }

    private async Task ConnectLoop()
    {
        _running = true;
        while (_running)
        {
            try
            {
                _ws = new WebSocket(WsUrl);

                _ws.OnOpen += () =>
                {
                    IsConnected = true;
                    SendRaw("{\"cmd\":\"ptz_stop\"}");
                    Debug.Log($"[PTZ] Connected → {WsUrl}");
                };

                _ws.OnClose += _ =>
                {
                    IsConnected = false;
                    _isMoving   = false;
                    Debug.Log("[PTZ] Disconnected.");
                };

                _ws.OnError += err => Debug.LogWarning($"[PTZ] {err}");

                _ws.OnMessage += bytes =>
                {
                    try
                    {
                        var json = System.Text.Encoding.UTF8.GetString(bytes);
                        var r    = JsonUtility.FromJson<PtzResponse>(json);
                        if (!r.ok) return;

                        if (!string.IsNullOrEmpty(r.cmd_type))
                        {
                            switch (r.cmd_type)
                            {
                                case "config":
                                    OnConfigReceived?.Invoke(r.camera_ip, r.server_host);
                                    break;
                                case "scan":
                                    OnScanComplete?.Invoke(r.cameras ?? Array.Empty<string>());
                                    break;
                                case "set_camera":
                                    OnCameraApplied?.Invoke();
                                    break;
                            }
                        }
                        else if (r.x > 0)
                        {
                            // Plain position response from ptz_get_position
                            CurrentX = r.x;
                            CurrentY = r.y;
                        }
                    }
                    catch { }
                };

                await _ws.Connect();

                while (_ws.State == WebSocketState.Open && _running)
                    await Task.Delay(250);
            }
            catch (Exception e)
            {
                IsConnected = false;
                Debug.LogWarning($"[PTZ] connect failed: {e.Message}");
            }

            if (_running)
                await Task.Delay(reconnectDelaySecs * 1000);
        }
    }

    // Polls actual camera position every second while connected.
    private IEnumerator PositionPollLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(1f);
            if (IsConnected)
                SendRaw("{\"cmd\":\"ptz_get_position\"}");
        }
    }

    private void Update()    => _ws?.DispatchMessageQueue();
    private void OnDestroy() => Disconnect();

    // ── IGimbalController ──────────────────────────────────────────────────────

    public void SetAngularVelocity(float yawRate, float pitchRate)
    {
        if (!IsConnected) return;

        bool atRest = Mathf.Abs(yawRate) < 1f && Mathf.Abs(pitchRate) < 1f;
        if (atRest)
        {
            if (_isMoving) StopMoving();
            return;
        }

        float  mag   = Mathf.Max(Mathf.Abs(yawRate), Mathf.Abs(pitchRate));
        int    speed = Mathf.Clamp(Mathf.RoundToInt(mag / maxRate * 10), 1, 10);
        string dir   = ToDirection(yawRate / mag, pitchRate / mag);

        if (dir == _lastDir && speed == _lastSpeed) return;

        _lastDir = dir; _lastSpeed = speed; _isMoving = true;
        SendRaw($"{{\"cmd\":\"ptz_start\",\"direction\":\"{dir}\",\"speed\":{speed}}}");
    }

    public void SetAngles(float yaw, float pitch, float roll)
    {
        StopMoving();
        SendRaw($"{{\"cmd\":\"ptz_goto\",\"x\":{(xMin + xMax) / 2},\"y\":{(yMin + yMax) / 2}}}");
    }

    public void SetFollowMode(bool enabled)
        => Debug.Log("[PTZ] Follow mode not supported on KC410S.");

    public void LockTarget(Vector2 coord)
    {
        int x = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(xMin, xMax, coord.x)),        xMin, xMax);
        int y = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(yMax, yMin, coord.y)), yMin, yMax);
        StopMoving();
        SendRaw($"{{\"cmd\":\"ptz_goto\",\"x\":{x},\"y\":{y}}}");
    }

    // ── Camera config commands ─────────────────────────────────────────────────

    public void GetConfig()              => SendRaw("{\"cmd\":\"get_config\"}");
    public void ScanCameras()            => SendRaw("{\"cmd\":\"scan_cameras\"}");
    public void SetCamera(string ip)     => SendRaw($"{{\"cmd\":\"set_camera\",\"ip\":\"{ip}\"}}");

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void StopMoving()
    {
        _isMoving = false; _lastDir = "";
        SendRaw("{\"cmd\":\"ptz_stop\"}");
    }

    private void SendRaw(string json)
    {
        if (_ws?.State == WebSocketState.Open)
            _ = _ws.SendText(json);
    }

    private static string ToDirection(float nx, float ny)
    {
        bool r = nx >  0.3f, l = nx < -0.3f;
        bool u = ny >  0.3f, d = ny < -0.3f;
        if (u && r) return "top right";
        if (u && l) return "top left";
        if (d && r) return "bottom right";
        if (d && l) return "bottom left";
        if (u)      return "top";
        if (d)      return "bottom";
        if (r)      return "right";
        if (l)      return "left";
        return nx >= 0f ? "right" : "left";
    }

    [Serializable]
    private class PtzResponse
    {
        public bool     ok;
        public int      x, y;
        public string   cmd_type;
        public string   camera_ip;
        public string   server_host;
        public string[] cameras;
        public bool     restart_required;
    }
}
