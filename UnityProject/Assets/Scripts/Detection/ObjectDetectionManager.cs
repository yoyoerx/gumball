using System;
using UnityEngine;

public class ObjectDetectionManager : MonoBehaviour
{
    [SerializeField] private VideoStreamReceiver streamReceiver;
    [SerializeField] private OnDeviceDetector onDeviceDetector;
    [SerializeField] private ServerDetectionClient serverClient;
    [SerializeField] private DetectionOverlay overlay;

    [Header("Server Offload")]
    [SerializeField] private bool enableServerOffload = true;
    [SerializeField] private float serverOffloadIntervalSeconds = 0.5f;

    public event Action<DetectionFrame> OnDetectionsUpdated;

    private float _nextServerSend;

    private void Start()
    {
        streamReceiver.OnFrameReceived += HandleFrame;

        if (onDeviceDetector != null)
            onDeviceDetector.OnDetections += PublishDetections;

        if (serverClient != null)
            serverClient.OnDetections += PublishDetections;
    }

    private void HandleFrame(byte[] jpeg)
    {
        onDeviceDetector?.RunInference(jpeg);

        if (enableServerOffload && Time.time >= _nextServerSend)
        {
            _nextServerSend = Time.time + serverOffloadIntervalSeconds;
            serverClient?.SendFrame(jpeg);
        }
    }

    private void PublishDetections(DetectionFrame frame)
    {
        overlay?.UpdateDetections(frame);
        OnDetectionsUpdated?.Invoke(frame);
    }

    private void OnDestroy()
    {
        if (streamReceiver) streamReceiver.OnFrameReceived -= HandleFrame;
        if (onDeviceDetector) onDeviceDetector.OnDetections -= PublishDetections;
        if (serverClient) serverClient.OnDetections -= PublishDetections;
    }
}
