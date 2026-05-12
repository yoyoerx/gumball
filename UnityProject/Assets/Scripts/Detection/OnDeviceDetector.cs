using System;
using System.Collections.Generic;
using Unity.Sentis;
using UnityEngine;

// Requires: com.unity.sentis >= 1.4.0
// Drop a YOLOv8n.onnx exported model into Assets/Models and assign it in the Inspector.
public class OnDeviceDetector : MonoBehaviour
{
    [SerializeField] private ModelAsset modelAsset;
    [SerializeField] private string[] classNames;
    [SerializeField] private float confidenceThreshold = 0.5f;
    [SerializeField] private int inputWidth = 640;
    [SerializeField] private int inputHeight = 640;

    public event Action<DetectionFrame> OnDetections;

    private Worker _worker;
    private Texture2D _inputTexture;
    private Tensor<float> _inputTensor;

    private void Awake()
    {
        if (modelAsset == null)
        {
            Debug.LogWarning("[OnDeviceDetector] No model asset assigned — on-device inference disabled.");
            enabled = false;
            return;
        }

        var model = ModelLoader.Load(modelAsset);
        _worker = new Worker(model, BackendType.GPUCompute);
        _inputTexture = new Texture2D(inputWidth, inputHeight, TextureFormat.RGB24, false);
        _inputTensor  = new Tensor<float>(new TensorShape(1, 3, inputHeight, inputWidth));
    }

    public void RunInference(byte[] jpegBytes)
    {
        if (_worker == null) return;

        var start = Time.realtimeSinceStartup;

        _inputTexture.LoadImage(jpegBytes);

        TextureConverter.ToTensor(_inputTexture, _inputTensor, new TextureTransform());
        _worker.Schedule(_inputTensor);

        using var output = _worker.PeekOutput("output0") as Tensor<float>;
        var detections = ParseYolov8Output(output);

        OnDetections?.Invoke(new DetectionFrame
        {
            detections = detections,
            inferenceTimeMs = (Time.realtimeSinceStartup - start) * 1000f,
            source = "on-device"
        });
    }

    // YOLOv8 output shape: [1, 4+numClasses, numAnchors] (feature-major)
    // Export with: model.export(format="onnx", opset=12, simplify=True)
    private List<Detection> ParseYolov8Output(Tensor<float> output)
    {
        var result = new List<Detection>();
        if (output == null) return result;

        int numClasses = classNames?.Length ?? 80;
        int numAnchors = output.shape[2];

        using var cpu = output.ReadbackAndClone();

        for (int i = 0; i < numAnchors; i++)
        {
            float maxConf = 0f;
            int maxClass = 0;
            for (int c = 0; c < numClasses; c++)
            {
                float conf = cpu[0, 4 + c, i];
                if (conf > maxConf) { maxConf = conf; maxClass = c; }
            }

            if (maxConf < confidenceThreshold) continue;

            float cx = cpu[0, 0, i];
            float cy = cpu[0, 1, i];
            float w  = cpu[0, 2, i];
            float h  = cpu[0, 3, i];

            result.Add(new Detection
            {
                label = classNames != null && maxClass < classNames.Length ? classNames[maxClass] : maxClass.ToString(),
                confidence = maxConf,
                classId = maxClass,
                boundingBox = new BoundingBox
                {
                    x = (cx - w / 2f) / inputWidth,
                    y = (cy - h / 2f) / inputHeight,
                    width  = w / inputWidth,
                    height = h / inputHeight
                }
            });
        }

        return result;
    }

    private void OnDestroy()
    {
        _worker?.Dispose();
        _inputTensor?.Dispose();
        if (_inputTexture) Destroy(_inputTexture);
    }
}
