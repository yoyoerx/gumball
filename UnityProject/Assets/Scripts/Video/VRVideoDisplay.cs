using UnityEngine;

// Attach to a Quad mesh in the VR scene. Set its scale to match your camera's aspect ratio.
// Assign the VideoStreamReceiver from the scene in the Inspector.
[RequireComponent(typeof(Renderer))]
public class VRVideoDisplay : MonoBehaviour
{
    [SerializeField] private VideoStreamReceiver streamReceiver;
    [SerializeField] private int textureWidth  = 1920;
    [SerializeField] private int textureHeight = 1080;

    private Texture2D _tex;
    private Renderer  _renderer;

    private void Awake()
    {
        _tex = new Texture2D(textureWidth, textureHeight, TextureFormat.RGB24, false);
        _renderer = GetComponent<Renderer>();
        _renderer.material.mainTexture = _tex;
        streamReceiver.OnFrameReceived += UpdateTexture;
    }

    private void UpdateTexture(byte[] jpegBytes)
    {
        _tex.LoadImage(jpegBytes); // LoadImage auto-resizes to decoded dimensions
        _tex.Apply();
    }

    private void OnDestroy()
    {
        if (streamReceiver) streamReceiver.OnFrameReceived -= UpdateTexture;
        Destroy(_tex);
    }
}
