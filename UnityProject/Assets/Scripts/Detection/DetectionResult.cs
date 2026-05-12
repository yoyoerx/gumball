using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class BoundingBox
{
    public float x, y, width, height;

    public Rect ToRect() => new Rect(x, y, width, height);
}

[Serializable]
public class Detection
{
    public string label;
    public float confidence;
    public int classId;
    public int trackId;
    public BoundingBox boundingBox;
}

[Serializable]
public class DetectionFrame
{
    public List<Detection> detections = new();
    public float inferenceTimeMs;
    public string source; // "on-device" or "server"
}
