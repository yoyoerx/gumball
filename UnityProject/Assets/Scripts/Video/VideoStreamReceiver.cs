using System;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;

// Expects the camera server to push raw JPEG frames as binary WebSocket messages.
// On the camera/gimbal side, use ffmpeg, GStreamer, or a Python server to relay RTSP → WS.
public class VideoStreamReceiver : MonoBehaviour
{
    [SerializeField] private string streamUrl = "ws://YOUR_SERVER_IP:8081"; // Set via MetaGimbalVision > Apply Network Config
    [SerializeField] private int reconnectDelaySeconds = 3;

    public event Action<byte[]> OnFrameReceived;

    private WebSocket _ws;
    private bool _running;

    private void Start() => _ = ConnectLoop();

    private async Task ConnectLoop()
    {
        _running = true;
        while (_running)
        {
            try
            {
                _ws = new WebSocket(streamUrl);
                _ws.OnMessage += frame => OnFrameReceived?.Invoke(frame);
                _ws.OnError += err => Debug.LogWarning($"[VideoStream] {err}");
                await _ws.Connect();

                while (_ws.State == WebSocketState.Open && _running)
                {
                    _ws.DispatchMessageQueue();
                    await Task.Delay(1);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VideoStream] Connect failed: {e.Message}");
            }

            if (_running)
                await Task.Delay(reconnectDelaySeconds * 1000);
        }
    }

    private void Update() => _ws?.DispatchMessageQueue();

    private async void OnDestroy()
    {
        _running = false;
        if (_ws != null) await _ws.Close();
    }
}
