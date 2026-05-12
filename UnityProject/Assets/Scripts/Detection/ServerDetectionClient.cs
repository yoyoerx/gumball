using System;
using System.Text;
using System.Threading.Tasks;
using NativeWebSocket;
using Newtonsoft.Json;
using UnityEngine;

public class ServerDetectionClient : MonoBehaviour
{
    [SerializeField] private string serverUrl = "ws://YOUR_SERVER_IP:9000"; // Set via MetaGimbalVision > Apply Network Config
    [SerializeField] private int reconnectDelaySeconds = 3;

    public event Action<DetectionFrame> OnDetections;

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
                _ws = new WebSocket(serverUrl);
                _ws.OnMessage += HandleMessage;
                _ws.OnError += err => Debug.LogWarning($"[DetectionClient] {err}");
                await _ws.Connect();

                while (_ws.State == WebSocketState.Open && _running)
                {
                    _ws.DispatchMessageQueue();
                    await Task.Delay(1);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DetectionClient] Connect failed: {e.Message}");
            }

            if (_running)
                await Task.Delay(reconnectDelaySeconds * 1000);
        }
    }

    public async void SendFrame(byte[] jpegBytes)
    {
        if (_ws?.State != WebSocketState.Open) return;
        await _ws.Send(jpegBytes);
    }

    private void HandleMessage(byte[] data)
    {
        try
        {
            var json = Encoding.UTF8.GetString(data);
            var frame = JsonConvert.DeserializeObject<DetectionFrame>(json);
            if (frame != null) OnDetections?.Invoke(frame);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[DetectionClient] Parse error: {e.Message}");
        }
    }

    private void Update() => _ws?.DispatchMessageQueue();

    private async void OnDestroy()
    {
        _running = false;
        if (_ws != null) await _ws.Close();
    }
}
