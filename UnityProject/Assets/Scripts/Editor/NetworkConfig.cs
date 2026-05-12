using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// MetaGimbalVision > Apply Network Config
// Sets WebSocket URLs on VideoStreamReceiver and ServerDetectionClient from the values below.
public static class NetworkConfig
{
    const string PC_IP        = "YOUR_SERVER_IP"; // Set to your PC's LAN IP before applying
    const string STREAM_URL   = "ws://" + PC_IP + ":8081/stream";
    const string DETECTION_URL= "ws://" + PC_IP + ":9000/detect";

    [MenuItem("MetaGimbalVision/Apply Network Config")]
    public static void Apply()
    {
        int changed = 0;

        foreach (var recv in Object.FindObjectsOfType<VideoStreamReceiver>())
        {
            var so = new SerializedObject(recv);
            var p  = so.FindProperty("streamUrl");
            if (p != null) { p.stringValue = STREAM_URL; so.ApplyModifiedProperties(); changed++; }
            Debug.Log($"[NetworkConfig] VideoStreamReceiver.streamUrl = {STREAM_URL}");
        }

        foreach (var client in Object.FindObjectsOfType<ServerDetectionClient>())
        {
            var so = new SerializedObject(client);
            var p  = so.FindProperty("serverUrl");
            if (p != null) { p.stringValue = DETECTION_URL; so.ApplyModifiedProperties(); changed++; }
            Debug.Log($"[NetworkConfig] ServerDetectionClient.serverUrl = {DETECTION_URL}");
        }

        if (changed == 0)
            Debug.LogWarning("[NetworkConfig] No components found — run Setup Scene first.");
        else
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
    }
}
