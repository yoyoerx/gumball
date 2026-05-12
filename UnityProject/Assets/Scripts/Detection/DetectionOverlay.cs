using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach to a world-space Canvas aligned to VRVideoDisplay.
// Assign boxPrefab (Image outline + TextMeshProUGUI label child) and overlayRoot in the Inspector.
public class DetectionOverlay : MonoBehaviour
{
    [SerializeField] private RectTransform overlayRoot;
    [SerializeField] private GameObject    boxPrefab;

    private readonly List<GameObject> _active = new();
    private readonly List<GameObject> _pool   = new();

    // Untracked detections (trackId == 0) use this color.
    private static readonly Color UntrackedColor = new(0f, 1f, 0.2f, 0.55f);

    public void UpdateDetections(DetectionFrame frame)
    {
        ReturnToPool();
        foreach (var det in frame.detections)
            SpawnBox(det);
    }

    private void SpawnBox(Detection det)
    {
        var box = _pool.Count > 0 ? _pool[^1] : Instantiate(boxPrefab, overlayRoot);
        if (_pool.Count > 0) _pool.RemoveAt(_pool.Count - 1);
        box.SetActive(true);
        _active.Add(box);

        // Position: convert normalized image coords (y-down) to Unity anchor space (y-up)
        var bb = det.boundingBox;
        var rt = box.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(bb.x,           1f - bb.y - bb.height);
        rt.anchorMax = new Vector2(bb.x + bb.width, 1f - bb.y);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        // Box color — unique per track ID, grey for untracked
        var img = box.GetComponentInChildren<Image>();
        if (img != null)
            img.color = det.trackId > 0 ? TrackColor(det.trackId) : UntrackedColor;

        // Label — show track ID when available
        var label = box.GetComponentInChildren<TextMeshProUGUI>();
        if (label != null)
        {
            label.text = det.trackId > 0
                ? $"[{det.trackId}] {det.label} {det.confidence:P0}"
                : $"{det.label} {det.confidence:P0}";
        }
    }

    private void ReturnToPool()
    {
        foreach (var b in _active) { b.SetActive(false); _pool.Add(b); }
        _active.Clear();
    }

    // Distributes hues evenly using the golden-angle increment so nearby IDs
    // are visually distinct.
    private static Color TrackColor(int trackId)
    {
        float hue = (trackId * 137.508f) % 360f / 360f;
        var c = Color.HSVToRGB(hue, 0.85f, 0.95f);
        c.a = 0.75f;
        return c;
    }
}
