using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

// Called by SceneSetup after all scene objects exist.
// Wires OVRInputModule on the EventSystem and OVRRaycaster on interactive canvases,
// then attaches ControllerRayPointer + LineRenderer to the right controller anchor.
public static class RaycastSetup
{
    public static void Setup()
    {
        var rightAnchor = FindRightControllerAnchor();

        SetupEventSystem(rightAnchor);
        SetupGimbalPanelRaycaster(rightAnchor);
        SetupRayPointer(rightAnchor);
    }

    // ── EventSystem ────────────────────────────────────────────────────────────

    static void SetupEventSystem(Transform raySource)
    {
        // Find or create the EventSystem
        var es = Object.FindObjectOfType<EventSystem>();
        if (es == null)
        {
            var esGO = new GameObject("EventSystem");
            es = esGO.AddComponent<EventSystem>();
        }

        // StandaloneInputModule fights with OVRInputModule in VR — remove it
        var standalone = es.GetComponent<StandaloneInputModule>();
        if (standalone != null) Object.DestroyImmediate(standalone);

        var ovrInput = es.GetComponent<OVRInputModule>();
        if (ovrInput == null) ovrInput = es.gameObject.AddComponent<OVRInputModule>();

        if (raySource != null)
        {
            var so = new SerializedObject(ovrInput);

            // rayTransform field name varies by SDK version
            SetRef(so, raySource, "rayTransform", "m_RayTransform", "_rayTransform");

            // Use the index trigger as the UI "click" — most natural for pointer interaction
            var btnProp = so.FindProperty("joyPadClickButton")
                       ?? so.FindProperty("m_JoyPadClickButton");
            if (btnProp != null)
                btnProp.intValue = (int)OVRInput.Button.PrimaryIndexTrigger;

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        Debug.Log($"[RaycastSetup] EventSystem → OVRInputModule. Ray: {raySource?.name ?? "none"}");
    }

    // ── Canvas raycaster ───────────────────────────────────────────────────────

    // Only the GimbalControlPanel canvas is interactive; DetectionCanvas is display-only.
    static void SetupGimbalPanelRaycaster(Transform pointer)
    {
        var panelRoot = GameObject.Find("GimbalControlPanel");
        if (panelRoot == null)
        {
            Debug.LogWarning("[RaycastSetup] GimbalControlPanel not found — run SetupScene first.");
            return;
        }

        var canvas = panelRoot.GetComponent<Canvas>();
        if (canvas == null || canvas.renderMode != RenderMode.WorldSpace)
        {
            Debug.LogWarning("[RaycastSetup] GimbalControlPanel has no world-space Canvas.");
            return;
        }

        // Swap GraphicRaycaster → OVRRaycaster
        var gr = canvas.GetComponent<GraphicRaycaster>();
        if (gr != null && gr.GetType() != typeof(OVRRaycaster))
            Object.DestroyImmediate(gr);

        var ovrRC = canvas.GetComponent<OVRRaycaster>();
        if (ovrRC == null) ovrRC = canvas.gameObject.AddComponent<OVRRaycaster>();

        if (pointer != null)
        {
            var so = new SerializedObject(ovrRC);
            SetRef(so, pointer, "pointer", "m_Pointer", "_pointer");
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // BoxCollider lets ControllerRayPointer's Physics.Raycast find the panel surface.
        // Size is in canvas local units; world size = localUnits × localScale (0.001).
        if (panelRoot.GetComponent<BoxCollider>() == null)
        {
            var col  = panelRoot.AddComponent<BoxCollider>();
            var rect = canvas.GetComponent<RectTransform>();
            col.size      = new Vector3(rect.sizeDelta.x, rect.sizeDelta.y, 4f);
            col.isTrigger = false; // solid so Physics.Raycast can hit it
        }

        // Must be on the UI layer for the LayerMask in ControllerRayPointer
        panelRoot.layer = LayerMask.NameToLayer("UI");

        Debug.Log("[RaycastSetup] GimbalControlPanel → OVRRaycaster + BoxCollider configured.");
    }

    // ── Controller ray pointer ─────────────────────────────────────────────────

    static void SetupRayPointer(Transform controllerAnchor)
    {
        if (controllerAnchor == null)
        {
            Debug.LogWarning("[RaycastSetup] Right controller anchor not found — skipping ray pointer.");
            return;
        }

        // LineRenderer ─────────────────────────────────────────────────────────
        var lr = controllerAnchor.GetComponent<LineRenderer>();
        if (lr == null) lr = controllerAnchor.gameObject.AddComponent<LineRenderer>();

        lr.positionCount     = 2;
        lr.useWorldSpace     = true;
        lr.startWidth        = 0.004f;
        lr.endWidth          = 0.001f;
        lr.shadowCastingMode = ShadowCastingMode.Off;
        lr.receiveShadows    = false;
        lr.material          = GetOrCreateMaterial(
            "Assets/Materials/RayPointerMaterial.mat",
            "Sprites/Default",
            Color.white);

        // Cursor disc ──────────────────────────────────────────────────────────
        // GameObject.Find skips inactive objects, so search the full scene graph instead.
        var cursor = FindInScene("RayCursor");
        if (cursor == null)
        {
            cursor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            cursor.name = "RayCursor";
            Object.DestroyImmediate(cursor.GetComponent<SphereCollider>());
            cursor.transform.localScale = Vector3.one * 0.02f;
            cursor.GetComponent<MeshRenderer>().material = GetOrCreateMaterial(
                "Assets/Materials/RayCursorMaterial.mat",
                "Sprites/Default",
                new Color(0.3f, 0.85f, 1f, 0.9f));
        }
        cursor.SetActive(false);

        // ControllerRayPointer ─────────────────────────────────────────────────
        var pointer = controllerAnchor.GetComponent<ControllerRayPointer>();
        if (pointer == null) pointer = controllerAnchor.gameObject.AddComponent<ControllerRayPointer>();

        var so = new SerializedObject(pointer);
        var cursorProp = so.FindProperty("cursor");
        if (cursorProp != null) cursorProp.objectReferenceValue = cursor;
        so.ApplyModifiedPropertiesWithoutUndo();

        Debug.Log($"[RaycastSetup] ControllerRayPointer added to {controllerAnchor.name}.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    static Transform FindRightControllerAnchor()
    {
        var rig = Object.FindObjectOfType<OVRCameraRig>();
        if (rig != null && rig.rightControllerAnchor != null)
            return rig.rightControllerAnchor;

        // Fallback for manually built rigs
        return GameObject.Find("RightControllerAnchor")?.transform;
    }

    // Tries each candidate field name until one is found
    static void SetRef(SerializedObject so, Object value, params string[] candidates)
    {
        foreach (var name in candidates)
        {
            var prop = so.FindProperty(name);
            if (prop == null) continue;
            prop.objectReferenceValue = value;
            return;
        }
        Debug.LogWarning($"[RaycastSetup] None of [{string.Join(", ", candidates)}] " +
                         $"found on {so.targetObject.GetType().Name} — SDK version mismatch?");
    }

    static Material GetOrCreateMaterial(string assetPath, string shaderName, Color color)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        if (mat != null) return mat;

        mat = new Material(Shader.Find(shaderName)) { color = color };
        AssetDatabase.CreateAsset(mat, assetPath);
        return mat;
    }

    // Like GameObject.Find but also finds inactive objects in the loaded scene.
    static GameObject FindInScene(string name)
    {
        var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            if (go.scene == active && go.name == name) return go;
        return null;
    }
}
