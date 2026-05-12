using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Run from the Unity menu: MetaGimbalVision > Setup Scene
// The scene must be saved at least once before running so assets can be written next to it.
public static class SceneSetup
{
    [MenuItem("MetaGimbalVision/Setup Scene")]
    public static void SetupScene()
    {
        EnsureFolders();

        var boxPrefab  = CreateOrUpdateBoundingBoxPrefab();
        var videoMat   = CreateOrLoadVideoMaterial();

        // ── Camera Rig ─────────────────────────────────────────────────────────
        SetupOVRCameraRig();

        // ── Floor ──────────────────────────────────────────────────────────────
        CreateFloor();

        // ── GimbalSystem ───────────────────────────────────────────────────────
        var gimbalSystem     = GetOrCreate("GimbalSystem");
        var gimbalController = GetOrAddComponent<KasaPTZController>(gimbalSystem);

        // ── VideoSystem ────────────────────────────────────────────────────────
        var videoSystem   = GetOrCreate("VideoSystem");
        var streamReceiver = GetOrAddComponent<VideoStreamReceiver>(videoSystem);

        // VideoScreen quad — 16:9 at 3 m in front of origin
        var videoScreen  = CreateVideoScreen(videoSystem, videoMat);
        var vrDisplay    = GetOrAddComponent<VRVideoDisplay>(videoScreen);
        GetOrAddComponent<VideoFrameFollow>(videoScreen);   // soft-follow + manual drag

        // World-space detection canvas parented to the quad
        var (overlayRT, detectionOverlay, canvasAligner) = CreateDetectionCanvas(videoSystem, videoScreen, boxPrefab);

        // ── DetectionSystem ────────────────────────────────────────────────────
        var detectionSystem  = GetOrCreate("DetectionSystem");
        var detectionManager = GetOrAddComponent<ObjectDetectionManager>(detectionSystem);
        var onDeviceDetector = GetOrAddComponent<OnDeviceDetector>(detectionSystem);
        var serverClient     = GetOrAddComponent<ServerDetectionClient>(detectionSystem);

        // ── InputSystem ────────────────────────────────────────────────────────
        var inputSystem  = GetOrCreate("InputSystem");
        var gimbalInput  = GetOrAddComponent<GimbalInputController>(inputSystem);

        // ── Control Panel UI
        var controlPanel = ControlPanelSetup.Create(gimbalController, gimbalInput);
        GetOrAddComponent<PanelDragController>(controlPanel.gameObject);

        // ── Menu toggle (left Menu button + open-palm gesture)
        var menuToggle = GetOrAddComponent<MenuToggle>(inputSystem);

        // ── VR Ray Casting (runs last — needs all canvases to exist first)
        RaycastSetup.Setup();

        // ── Wire serialized references ─────────────────────────────────────────
        Set(vrDisplay,         "streamReceiver",   streamReceiver);

        Set(detectionOverlay,  "overlayRoot",      overlayRT);
        Set(detectionOverlay,  "boxPrefab",        boxPrefab);
        Set(canvasAligner,     "target",           videoScreen.transform);

        Set(detectionManager,  "streamReceiver",   streamReceiver);
        Set(detectionManager,  "onDeviceDetector", onDeviceDetector);
        Set(detectionManager,  "serverClient",     serverClient);
        Set(detectionManager,  "overlay",          detectionOverlay);

        Set(gimbalInput,       "gimbal",           gimbalController);
        Set(gimbalInput,       "detectionManager", detectionManager);

        SetArray(menuToggle,   "panels",           new Object[] { controlPanel.gameObject });

        // ── Mark scene dirty ───────────────────────────────────────────────────
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log("[SceneSetup] Done. Remaining manual steps:\n" +
                  "  1. Assign ONNX model to OnDeviceDetector → modelAsset\n" +
                  "  2. Fill classNames array on OnDeviceDetector\n" +
                  "  3. Verify serverHost in KasaPTZController and VideoStreamReceiver match the PC IP\n" +
                  "  4. Verify OVRInputModule.rayTransform is set to RightControllerAnchor in Inspector\n" +
                  "  5. Rename GimbalControlPanel.cs → ControlPanel.cs and GimbalPanelSetup.cs → ControlPanelSetup.cs in the Unity Project window");
    }

    // ── OVRCameraRig ───────────────────────────────────────────────────────────

    private static void SetupOVRCameraRig()
    {
        if (Object.FindObjectOfType<OVRCameraRig>() != null)
        {
            Debug.Log("[SceneSetup] OVRCameraRig already present — skipping.");
            return;
        }

        // Try the SDK prefab from known package paths first.
        string[] candidatePaths =
        {
            "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab",
            "Packages/com.meta.xr.sdk.all/Prefabs/OVRCameraRig.prefab",
            "Assets/Oculus/VR/Prefabs/OVRCameraRig.prefab",         // legacy asset-store import
        };

        foreach (var path in candidatePaths)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = "OVRCameraRig";
            instance.transform.position = Vector3.zero;
            ConfigureOVRManager(instance);
            Debug.Log($"[SceneSetup] OVRCameraRig instantiated from {path}");
            return;
        }

        // Prefab not found — build the hierarchy manually.
        Debug.LogWarning("[SceneSetup] OVRCameraRig prefab not found; building manually. " +
                         "Verify the Meta XR SDK package is installed.");
        BuildOVRCameraRigManually();
    }

    private static void BuildOVRCameraRigManually()
    {
        var root = new GameObject("OVRCameraRig");
        root.transform.position = Vector3.zero;

        var rig = root.AddComponent<OVRCameraRig>();
        ConfigureOVRManager(root);

        // TrackingSpace
        var tracking = new GameObject("TrackingSpace").transform;
        tracking.SetParent(root.transform, false);

        // Eye anchors
        var leftEye   = MakeAnchor("LeftEyeAnchor",   tracking, addCamera: true,  tag: "Untagged");
        var centerEye = MakeAnchor("CenterEyeAnchor", tracking, addCamera: true,  tag: "MainCamera");
        var rightEye  = MakeAnchor("RightEyeAnchor",  tracking, addCamera: true,  tag: "Untagged");

        // Configure stereo cameras
        SetupEyeCamera(leftEye.GetComponent<Camera>(),   -1);
        SetupEyeCamera(centerEye.GetComponent<Camera>(),  0);
        SetupEyeCamera(rightEye.GetComponent<Camera>(),   1);
        centerEye.gameObject.AddComponent<AudioListener>();

        // Hand / controller anchors
        var leftHand   = MakeAnchor("LeftHandAnchor",  tracking);
        var rightHand  = MakeAnchor("RightHandAnchor", tracking);
        var leftCtrl   = MakeAnchor("LeftControllerAnchor",  leftHand);
        var rightCtrl  = MakeAnchor("RightControllerAnchor", rightHand);
        var tracker    = MakeAnchor("TrackerAnchor",   tracking);

        // Wire OVRCameraRig serialized fields.
        // The Meta XR SDK uses underscore-prefixed names in newer versions (v55+);
        // older versions use bare names. Try both so the script survives version changes.
        var so = new SerializedObject(rig);
        OvrSetTransform(so, "trackingSpace",         tracking);
        OvrSetTransform(so, "leftEyeAnchor",         leftEye);
        OvrSetTransform(so, "centerEyeAnchor",       centerEye);
        OvrSetTransform(so, "rightEyeAnchor",        rightEye);
        OvrSetTransform(so, "leftHandAnchor",        leftHand);
        OvrSetTransform(so, "rightHandAnchor",       rightHand);
        OvrSetTransform(so, "trackerAnchor",         tracker);
        OvrSetTransform(so, "leftControllerAnchor",  leftCtrl);
        OvrSetTransform(so, "rightControllerAnchor", rightCtrl);
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static Transform MakeAnchor(string name, Transform parent, bool addCamera = false, string tag = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        if (tag != null) go.tag = tag;
        if (addCamera) go.AddComponent<Camera>();
        return go.transform;
    }

    private static void SetupEyeCamera(Camera cam, int eye)
    {
        // eye: -1 = left, 0 = center, 1 = right
        cam.clearFlags      = CameraClearFlags.Skybox;
        cam.nearClipPlane   = 0.1f;
        cam.farClipPlane    = 1000f;
        cam.stereoTargetEye = eye < 0 ? StereoTargetEyeMask.Left
                            : eye > 0 ? StereoTargetEyeMask.Right
                                       : StereoTargetEyeMask.Both;
    }

    private static void ConfigureOVRManager(GameObject root)
    {
        var mgr = root.GetComponentInChildren<OVRManager>();
        if (mgr == null) mgr = root.AddComponent<OVRManager>();
        var so  = new SerializedObject(mgr);

        // FloorLevel origin so y=0 is the physical floor — standard for standing Quest apps.
        var origin = so.FindProperty("trackingOriginType") ?? so.FindProperty("_trackingOriginType");
        if (origin != null) origin.intValue = (int)OVRManager.TrackingOrigin.FloorLevel;

        var msaa = so.FindProperty("useRecommendedMSAALevel") ?? so.FindProperty("_useRecommendedMSAALevel");
        if (msaa != null) msaa.boolValue = true;

        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // Tries the underscore-prefixed field name first (Meta XR SDK v55+), then bare name.
    private static void OvrSetTransform(SerializedObject so, string baseName, Transform value)
    {
        var prop = so.FindProperty($"_{baseName}") ?? so.FindProperty(baseName);
        if (prop == null)
        {
            Debug.LogWarning($"[SceneSetup] OVRCameraRig field '{baseName}' not found — " +
                             "anchor wiring skipped for this field.");
            return;
        }
        prop.objectReferenceValue = value;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static GameObject CreateVideoScreen(GameObject parent, Material mat)
    {
        var existing = GameObject.Find("VideoScreen");
        if (existing != null) return existing;

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "VideoScreen";
        quad.transform.SetParent(parent.transform, false);
        quad.transform.localPosition = new Vector3(0f, 1.6f, 3f);  // eye height, 3 m ahead
        quad.transform.localScale    = new Vector3(3.2f, 1.8f, 1f); // 16:9

        // Remove the default box collider — not needed for a display surface
        Object.DestroyImmediate(quad.GetComponent<MeshCollider>());

        quad.GetComponent<Renderer>().material = mat;
        return quad;
    }

    private static (RectTransform overlayRT, DetectionOverlay overlay, AlignToTransform aligner)
        CreateDetectionCanvas(GameObject videoSystem, GameObject videoScreen, GameObject boxPrefab)
    {
        // DetectionCanvas must NOT be a child of VideoScreen — VideoScreen's scale (3.2, 1.8, 1)
        // is non-uniform and OVROverlayCanvas rejects that. Instead, parent it to VideoSystem
        // (uniform scale) and let AlignToTransform track VideoScreen at runtime.

        // Remove any stale DetectionCanvas still parented to VideoScreen
        var staleChild = videoScreen.transform.Find("DetectionCanvas");
        if (staleChild != null) Object.DestroyImmediate(staleChild.gameObject);

        var existing = videoSystem.transform.Find("DetectionCanvas");
        GameObject canvasGO;
        if (existing != null)
        {
            canvasGO = existing.gameObject;
        }
        else
        {
            canvasGO = new GameObject("DetectionCanvas");
            canvasGO.transform.SetParent(videoSystem.transform, false);
        }

        var canvas = GetOrAddComponent<Canvas>(canvasGO);
        canvas.renderMode = RenderMode.WorldSpace;
        GetOrAddComponent<CanvasScaler>(canvasGO);
        GetOrAddComponent<GraphicRaycaster>(canvasGO);
        GetOrAddComponent<OVROverlayCanvas>(canvasGO);

        // Uniform scale + sizeDelta encodes 3.2 m × 1.8 m in world space (matches VideoScreen)
        canvasGO.transform.localPosition = videoScreen.transform.localPosition;
        canvasGO.transform.localRotation = videoScreen.transform.localRotation;
        canvasGO.transform.localScale    = Vector3.one * 0.001f;
        var canvasRT = canvasGO.GetComponent<RectTransform>();
        canvasRT.sizeDelta = new Vector2(3200f, 1800f);

        var aligner = GetOrAddComponent<AlignToTransform>(canvasGO);

        // OverlayRoot — fills the canvas, bounding boxes anchor inside this
        var overlayGO = canvasGO.transform.Find("OverlayRoot")?.gameObject
                        ?? new GameObject("OverlayRoot");
        overlayGO.transform.SetParent(canvasGO.transform, false);
        var overlayRT = GetOrAddComponent<RectTransform>(overlayGO);
        overlayRT.anchorMin = Vector2.zero;
        overlayRT.anchorMax = Vector2.one;
        overlayRT.offsetMin = Vector2.zero;
        overlayRT.offsetMax = Vector2.zero;

        var detectionOverlay = GetOrAddComponent<DetectionOverlay>(videoScreen);
        return (overlayRT, detectionOverlay, aligner);
    }

    private static GameObject CreateOrUpdateBoundingBoxPrefab()
    {
        const string path = "Assets/Prefabs/BoundingBox.prefab";
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing != null) return existing;

        var root = new GameObject("BoundingBox");
        root.AddComponent<RectTransform>();

        // Outline frame using an Image with a border sprite
        var frame = new GameObject("Frame");
        frame.transform.SetParent(root.transform, false);
        var frameRT = frame.AddComponent<RectTransform>();
        frameRT.anchorMin = Vector2.zero;
        frameRT.anchorMax = Vector2.one;
        frameRT.offsetMin = Vector2.zero;
        frameRT.offsetMax = Vector2.zero;
        var img = frame.AddComponent<Image>();
        img.color = new Color(0f, 1f, 0.2f, 0.6f);

        // Label above the box
        var label = new GameObject("Label");
        label.transform.SetParent(root.transform, false);
        var labelRT = label.AddComponent<RectTransform>();
        labelRT.anchorMin = new Vector2(0f, 1f);
        labelRT.anchorMax = new Vector2(1f, 1f);
        labelRT.pivot     = new Vector2(0.5f, 0f);
        labelRT.sizeDelta = new Vector2(0f, 0.04f); // canvas units
        var tmp = label.AddComponent<TextMeshProUGUI>();
        tmp.text      = "Label 100%";
        tmp.fontSize  = 0.03f;
        tmp.color     = Color.green;
        tmp.alignment = TextAlignmentOptions.Left;

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return prefab;
    }

    private static Material CreateOrLoadVideoMaterial()
    {
        const string path = "Assets/Materials/VideoMaterial.mat";

        var shader = Shader.Find("MetaGimbalVision/VideoScreen");
        if (shader == null)
        {
            Debug.LogWarning("[SceneSetup] VideoScreen shader not found — falling back to Unlit/Texture. " +
                             "Make sure Assets/Shaders/VideoScreen.shader exists and has compiled.");
            shader = Shader.Find("Unlit/Texture");
        }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader) { name = "VideoMaterial" };
            AssetDatabase.CreateAsset(mat, path);
        }
        else
        {
            mat.shader = shader;
        }

        mat.SetFloat("_CornerRadius", 0.08f);
        mat.SetFloat("_Width",        3.2f);
        mat.SetFloat("_Height",       1.8f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static void CreateFloor()
    {
        var existing = GameObject.Find("Floor");
        if (existing != null)
        {
            var r = existing.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = CreateOrLoadFloorMaterial();
            return;
        }

        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.position   = Vector3.zero;
        floor.transform.localScale = new Vector3(4f, 1f, 4f); // Unity Plane = 10 m, scaled → 40 m × 40 m
        Object.DestroyImmediate(floor.GetComponent<MeshCollider>());
        floor.GetComponent<Renderer>().sharedMaterial = CreateOrLoadFloorMaterial();
    }

    private static Material CreateOrLoadFloorMaterial()
    {
        const string path = "Assets/Materials/FloorMaterial.mat";

        var shader = Shader.Find("MetaGimbalVision/FloorGrid");
        if (shader == null)
        {
            Debug.LogWarning("[SceneSetup] FloorGrid shader not found. " +
                             "Make sure Assets/Shaders/FloorGrid.shader exists and has compiled.");
            shader = Shader.Find("Standard");
        }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader) { name = "FloorMaterial" };
            AssetDatabase.CreateAsset(mat, path);
        }
        else
        {
            mat.shader = shader;
        }

        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");
        if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
            AssetDatabase.CreateFolder("Assets", "Prefabs");
        if (!AssetDatabase.IsValidFolder("Assets/Models"))
            AssetDatabase.CreateFolder("Assets", "Models");
        if (!AssetDatabase.IsValidFolder("Assets/Shaders"))
            AssetDatabase.CreateFolder("Assets", "Shaders");
    }

    private static GameObject GetOrCreate(string name)
    {
        var go = GameObject.Find(name);
        return go != null ? go : new GameObject(name);
    }

    private static T GetOrAddComponent<T>(GameObject go) where T : Component
    {
        // ?? uses C# reference equality which doesn't respect Unity's fake-null destroyed objects.
        // Use Unity's != operator explicitly to handle the destroyed-component case.
        var c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    private static void Set(Component target, string field, Object value)
    {
        var so   = new SerializedObject(target);
        var prop = so.FindProperty(field);
        if (prop == null)
        {
            Debug.LogWarning($"[SceneSetup] '{field}' not found on {target.GetType().Name}");
            return;
        }
        prop.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void SetArray(Component target, string field, Object[] values)
    {
        var so   = new SerializedObject(target);
        var prop = so.FindProperty(field);
        if (prop == null)
        {
            Debug.LogWarning($"[SceneSetup] '{field}' not found on {target.GetType().Name}");
            return;
        }
        prop.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
            prop.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
