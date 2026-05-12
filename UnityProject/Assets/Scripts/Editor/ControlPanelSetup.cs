using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Called by SceneSetup — not a standalone menu item.
// Rename this file to ControlPanelSetup.cs in the Unity Project window.
//
// Canvas: 420×620 px at localScale 0.001 → 0.42 m × 0.62 m in world space.
// Layout (canvas px):
//   Header 60 | TabBar 36 | TabContent (flex, ~504) | root bottom-pad 12
//   spacing 4 between root children
//
// GimbalPage  ≈ 487 px  (Status 50 + Sep + PTZ 140 + Sep + Controls 140 + Sep + Buttons 130 + 6× spacing 4)
// CameraPage  ≈ 472 px  (Info 110 + Sep + CameraList 226 + Sep + Actions 102 + 4× spacing 4)
public static class ControlPanelSetup
{
    // ── Palette ────────────────────────────────────────────────────────────────
    static readonly Color CBg        = new(0.08f, 0.09f, 0.13f, 0.95f);
    static readonly Color CHeader    = new(0.12f, 0.15f, 0.23f, 1.00f);
    static readonly Color CSep       = new(0.25f, 0.28f, 0.40f, 0.70f);
    static readonly Color CLabel     = new(0.58f, 0.68f, 0.82f, 1.00f);
    static readonly Color CValue     = new(0.95f, 0.95f, 0.98f, 1.00f);
    static readonly Color CBlue      = new(0.22f, 0.48f, 0.88f, 1.00f);
    static readonly Color CGreen     = new(0.14f, 0.72f, 0.44f, 1.00f);
    static readonly Color CAccent    = new(0.45f, 0.55f, 0.72f, 1.00f);
    static readonly Color CTabActive = new(0.22f, 0.48f, 0.88f, 1.00f);
    static readonly Color CTabInact  = new(0.12f, 0.15f, 0.23f, 1.00f);
    static readonly Color CSlot      = new(0.18f, 0.22f, 0.32f, 1.00f);

    static Sprite _uiSprite;

    // ── Entry point ────────────────────────────────────────────────────────────

    public static ControlPanel Create(
        KasaPTZController     gimbalController,
        GimbalInputController inputController)
    {
        var existingGO = FindInScene("ControlPanel");
        if (existingGO != null)
        {
            Debug.Log("[ControlPanelSetup] ControlPanel found — destroying for rebuild.");
            Object.DestroyImmediate(existingGO);
        }

        _uiSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");

        var root = new GameObject("ControlPanel");
        root.transform.position   = new Vector3(-2.05f, 1.35f, 3.0f);
        root.transform.localScale = Vector3.one * 0.001f;

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<CanvasScaler>();
        root.AddComponent<GraphicRaycaster>();
        root.AddComponent<OVROverlayCanvas>();

        var rootRT = root.GetComponent<RectTransform>();
        rootRT.sizeDelta = new Vector2(420f, 620f);

        var bgRT = UIRect("Background", rootRT, addImage: true, color: CBg);
        var bgMat = CreateOrLoadPanelMaterial();
        if (bgMat != null) bgRT.GetComponent<Image>().material = bgMat;
        Stretch(bgRT);

        var vlg = bgRT.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.padding                = new RectOffset(0, 0, 0, 12);
        vlg.spacing                = 4f;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;

        // ── Header ─────────────────────────────────────────────────────────────
        BuildHeader(bgRT);

        // ── Tab bar ────────────────────────────────────────────────────────────
        var (gimbalTabBg, cameraTabBg, gimbalTabBtn, cameraTabBtn) = BuildTabBar(bgRT);

        // ── Tab content (fills remaining height) ───────────────────────────────
        var tabContentRT = UIRect("TabContent", bgRT);
        var tabContentLE = tabContentRT.gameObject.AddComponent<LayoutElement>();
        tabContentLE.flexibleHeight = 1f;
        tabContentLE.flexibleWidth  = 1f;

        // ── Gimbal page ────────────────────────────────────────────────────────
        var gimbalPageRT = UIRect("GimbalPage", tabContentRT);
        Stretch(gimbalPageRT);
        var gvlg = gimbalPageRT.gameObject.AddComponent<VerticalLayoutGroup>();
        gvlg.spacing                = 4f;
        gvlg.childControlWidth      = true;
        gvlg.childControlHeight     = true;
        gvlg.childForceExpandWidth  = true;
        gvlg.childForceExpandHeight = false;

        var (statusDot, statusLabel) = BuildStatus(gimbalPageRT);
        Separator(gimbalPageRT);
        var (yawVal, pitchVal, rollVal) = BuildPtzPosition(gimbalPageRT);
        Separator(gimbalPageRT);
        var (followToggle, speedSlider, speedValLabel) = BuildControls(gimbalPageRT);
        Separator(gimbalPageRT);
        var (centerBtn, lockOnBtn, connectBtn, connectLabel, connectBg) = BuildButtons(gimbalPageRT);

        // ── Camera page ────────────────────────────────────────────────────────
        var cameraPageRT = UIRect("CameraPage", tabContentRT);
        Stretch(cameraPageRT);
        cameraPageRT.gameObject.SetActive(false);   // starts hidden
        var cvlg = cameraPageRT.gameObject.AddComponent<VerticalLayoutGroup>();
        cvlg.spacing                = 4f;
        cvlg.childControlWidth      = true;
        cvlg.childControlHeight     = true;
        cvlg.childForceExpandWidth  = true;
        cvlg.childForceExpandHeight = false;

        var (serverLbl, cameraLbl) = BuildCameraInfo(cameraPageRT);
        Separator(cameraPageRT);
        var (slotBtns, slotLabels, slotBgs) = BuildCameraList(cameraPageRT);
        Separator(cameraPageRT);
        var (scanBtn, applyBtn, scanStatus) = BuildCameraActions(cameraPageRT);

        // ── Wire ControlPanel component ────────────────────────────────────────
        var panel = root.AddComponent<ControlPanel>();
        var so    = new SerializedObject(panel);

        // Dependencies
        SetRef(so, "gimbal",           gimbalController);
        SetRef(so, "inputController",  inputController);

        // Tabs
        SetRef(so, "gimbalPage",    gimbalPageRT.gameObject);
        SetRef(so, "cameraPage",    cameraPageRT.gameObject);
        SetRef(so, "gimbalTabBg",   gimbalTabBg);
        SetRef(so, "cameraTabBg",   cameraTabBg);
        SetRef(so, "gimbalTabBtn",  gimbalTabBtn);
        SetRef(so, "cameraTabBtn",  cameraTabBtn);

        // Gimbal tab
        SetRef(so, "statusDot",          statusDot);
        SetRef(so, "statusLabel",        statusLabel);
        SetRef(so, "yawValue",           yawVal);
        SetRef(so, "pitchValue",         pitchVal);
        SetRef(so, "rollValue",          rollVal);
        SetRef(so, "followModeToggle",   followToggle);
        SetRef(so, "speedSlider",        speedSlider);
        SetRef(so, "speedValueLabel",    speedValLabel);
        SetRef(so, "centerButton",       centerBtn);
        SetRef(so, "lockOnButton",       lockOnBtn);
        SetRef(so, "connectButton",      connectBtn);
        SetRef(so, "connectButtonLabel", connectLabel);
        SetRef(so, "connectButtonBg",    connectBg);

        // Camera tab
        SetRef(so, "serverHostLabel", serverLbl);
        SetRef(so, "cameraIpLabel",   cameraLbl);
        SetRef(so, "scanBtn",         scanBtn);
        SetRef(so, "applyBtn",        applyBtn);
        SetRef(so, "scanStatusLabel", scanStatus);

        SetRefArray(so, "cameraSlotBtns",   slotBtns);
        SetRefArray(so, "cameraSlotLabels", slotLabels);
        SetRefArray(so, "cameraSlotBgs",    slotBgs);

        so.ApplyModifiedPropertiesWithoutUndo();
        return panel;
    }

    // ── Section builders ───────────────────────────────────────────────────────

    static void BuildHeader(RectTransform parent)
    {
        var row = Row(parent, "Header", height: 60f, addImage: true, color: CHeader);
        row.padding = new RectOffset(20, 20, 0, 0);
        TMP("Title", row.transform, "CONTROL PANEL", 22f, CValue,
            TextAlignmentOptions.Center, flexWidth: 1f);
    }

    static (Image gimbalTabBg, Image cameraTabBg, Button gimbalBtn, Button cameraBtn)
        BuildTabBar(RectTransform parent)
    {
        var barRT = UIRect("TabBar", parent);
        var barLE = barRT.gameObject.AddComponent<LayoutElement>();
        barLE.preferredHeight = 36f;
        barLE.flexibleWidth   = 1f;

        var hlg = barRT.gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.childControlWidth      = true;
        hlg.childControlHeight     = true;
        hlg.childForceExpandWidth  = true;
        hlg.childForceExpandHeight = true;

        var (gimbalBtn, _, gimbalBg) = BtnWithRefs(barRT, "GIMBAL",  CTabActive);
        var (cameraBtn, _, cameraBg) = BtnWithRefs(barRT, "CAMERA",  CTabInact);

        // Use smaller font for tab labels
        foreach (var t in barRT.GetComponentsInChildren<TextMeshProUGUI>())
            t.fontSize = 16f;

        return (gimbalBg, cameraBg, gimbalBtn, cameraBtn);
    }

    static (Image dot, TextMeshProUGUI label) BuildStatus(RectTransform parent)
    {
        var row = Row(parent, "StatusRow", height: 50f);
        row.padding = new RectOffset(20, 20, 0, 0);
        row.spacing = 12f;

        var dotRT = UIRect("StatusDot", (RectTransform)row.transform,
                           addImage: true, color: new Color(0.85f, 0.22f, 0.22f));
        dotRT.gameObject.GetComponent<Image>().sprite = _uiSprite;
        var dotLE = dotRT.gameObject.AddComponent<LayoutElement>();
        dotLE.preferredWidth  = 12f;
        dotLE.preferredHeight = 12f;
        dotLE.flexibleWidth   = 0f;

        var label = TMP("StatusLabel", row.transform, "Disconnected", 18f, CLabel,
                        TextAlignmentOptions.Left, flexWidth: 1f);
        return (dotRT.GetComponent<Image>(), label);
    }

    static (TextMeshProUGUI panX, TextMeshProUGUI tiltY, TextMeshProUGUI unused)
        BuildPtzPosition(RectTransform parent)
    {
        var section = Section(parent, "PtzSection", height: 140f, title: "PTZ POSITION");
        return (PosRow(section, "PAN X"), PosRow(section, "TILT Y"), PosRow(section, "---"));
    }

    static TextMeshProUGUI PosRow(RectTransform section, string label)
    {
        var row = Row(section, label.Replace(" ", "") + "Row", height: 34f);
        row.padding = new RectOffset(24, 20, 0, 0);
        TMP(label + "Label", row.transform, label, 18f, CLabel,
            TextAlignmentOptions.Left, flexWidth: 1f);
        return TMP(label + "Val", row.transform, "—", 19f, CValue,
                   TextAlignmentOptions.Right, preferredWidth: 80f);
    }

    static (Toggle follow, Slider speed, TextMeshProUGUI speedLabel)
        BuildControls(RectTransform parent)
    {
        var section = Section(parent, "ControlSection", height: 140f, title: "CONTROLS",
                              spacingInner: 4f, padTop: 6, padBottom: 8);

        var fRow = Row(section, "FollowRow", height: 46f);
        fRow.padding = new RectOffset(24, 20, 0, 0);
        TMP("FollowLabel", fRow.transform, "Follow Mode", 18f, CLabel,
            TextAlignmentOptions.Left, flexWidth: 1f);
        var toggle = MakeToggle(fRow.transform);

        var sRow = Row(section, "SpeedRow", height: 46f);
        sRow.padding = new RectOffset(24, 20, 0, 0);
        sRow.spacing = 8f;
        TMP("SpeedLabel", sRow.transform, "Speed", 18f, CLabel,
            TextAlignmentOptions.Left, preferredWidth: 58f);
        var slider     = MakeSlider(sRow.transform, min: 10f, max: 120f, value: 60f);
        var speedLabel = TMP("SpeedVal", sRow.transform, "60°/s", 18f, CValue,
                             TextAlignmentOptions.Right, preferredWidth: 66f);
        return (toggle, slider, speedLabel);
    }

    static (Button center, Button lockOn, Button connect,
            TextMeshProUGUI connectLabel, Image connectBg)
        BuildButtons(RectTransform parent)
    {
        var section = Section(parent, "ButtonSection", height: 130f, title: null,
                              spacingInner: 10f, padTop: 16, padBottom: 16);

        var row1 = Row(section, "BtnRow1", height: 44f);
        row1.padding = new RectOffset(20, 20, 0, 0);
        row1.spacing = 10f;
        var center = Btn(row1.transform, "Center",  CGreen);
        var lockOn = Btn(row1.transform, "Lock On", CBlue);

        var row2 = Row(section, "BtnRow2", height: 44f);
        row2.padding = new RectOffset(20, 20, 0, 0);
        var (connectBtn, connectLabel, connectBg) = BtnWithRefs(row2.transform, "Connect", CBlue);

        return (center, lockOn, connectBtn, connectLabel, connectBg);
    }

    // ── Camera page builders ───────────────────────────────────────────────────

    static (TextMeshProUGUI serverLbl, TextMeshProUGUI cameraLbl)
        BuildCameraInfo(RectTransform parent)
    {
        var section = Section(parent, "InfoSection", height: 110f, title: "CONNECTION",
                              spacingInner: 4f, padTop: 6, padBottom: 6);

        var serverRow = Row(section, "ServerRow", height: 32f);
        serverRow.padding = new RectOffset(24, 20, 0, 0);
        TMP("ServerLabel", serverRow.transform, "SERVER", 16f, CLabel,
            TextAlignmentOptions.Left, preferredWidth: 80f);
        var serverVal = TMP("ServerVal", serverRow.transform, "—", 16f, CValue,
                            TextAlignmentOptions.Left, flexWidth: 1f);

        var camRow = Row(section, "CameraRow", height: 32f);
        camRow.padding = new RectOffset(24, 20, 0, 0);
        TMP("CameraLabel", camRow.transform, "CAMERA", 16f, CLabel,
            TextAlignmentOptions.Left, preferredWidth: 80f);
        var cameraVal = TMP("CameraVal", camRow.transform, "—", 16f, CValue,
                            TextAlignmentOptions.Left, flexWidth: 1f);

        return (serverVal, cameraVal);
    }

    static (Button[] btns, TextMeshProUGUI[] labels, Image[] bgs)
        BuildCameraList(RectTransform parent)
    {
        var section = Section(parent, "CameraListSection", height: 226f,
                              title: "AVAILABLE CAMERAS",
                              spacingInner: 4f, padTop: 6, padBottom: 6);

        var btns   = new Button[4];
        var labels = new TextMeshProUGUI[4];
        var bgs    = new Image[4];

        for (int i = 0; i < 4; i++)
        {
            var row = Row(section, $"SlotRow{i}", height: 44f);
            row.padding = new RectOffset(20, 20, 0, 0);
            var (btn, label, bg) = BtnWithRefs(row.transform, "—", CSlot);
            btn.interactable = false;
            label.fontSize   = 17f;
            label.alignment  = TextAlignmentOptions.Left;
            // inset label padding
            var labelRT = (RectTransform)label.transform;
            labelRT.offsetMin = new Vector2(10f, 0f);
            btns[i]   = btn;
            labels[i] = label;
            bgs[i]    = bg;
        }

        return (btns, labels, bgs);
    }

    static (Button scanBtn, Button applyBtn, TextMeshProUGUI statusLbl)
        BuildCameraActions(RectTransform parent)
    {
        var section = Section(parent, "ActionSection", height: 102f, title: null,
                              spacingInner: 8f, padTop: 10, padBottom: 10);

        var btnRow = Row(section, "ActionBtnRow", height: 44f);
        btnRow.padding = new RectOffset(20, 20, 0, 0);
        btnRow.spacing = 10f;
        var scan  = Btn(btnRow.transform, "Scan Network", CBlue);
        var apply = Btn(btnRow.transform, "Apply",        CGreen);
        apply.interactable = false;

        var statusRow = Row(section, "StatusRow", height: 30f);
        statusRow.padding = new RectOffset(24, 20, 0, 0);
        var status = TMP("ScanStatus", statusRow.transform, "", 15f, CLabel,
                         TextAlignmentOptions.Left, flexWidth: 1f);

        return (scan, apply, status);
    }

    // ── Material ───────────────────────────────────────────────────────────────

    static Material CreateOrLoadPanelMaterial()
    {
        const string path = "Assets/Materials/PanelBackgroundMaterial.mat";

        var shader = Shader.Find("MetaGimbalVision/PanelBackground");
        if (shader == null)
        {
            Debug.LogWarning("[ControlPanelSetup] PanelBackground shader not found.");
            return null;
        }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader) { name = "PanelBackgroundMaterial" };
            AssetDatabase.CreateAsset(mat, path);
        }
        else { mat.shader = shader; }

        mat.SetColor("_Color",        CBg);
        mat.SetFloat("_CornerRadius", 20f);
        mat.SetFloat("_Width",        420f);
        mat.SetFloat("_Height",       620f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // ── UI primitive helpers ───────────────────────────────────────────────────

    static RectTransform UIRect(string name, RectTransform parent,
                                bool addImage = false, Color? color = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        if (addImage) go.AddComponent<Image>().color = color ?? Color.white;
        return rt;
    }

    static RectTransform Section(RectTransform parent, string name, float height,
                                 string title, float spacingInner = 0f,
                                 int padTop = 6, int padBottom = 6)
    {
        var rt = UIRect(name, parent);
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = height;
        le.flexibleWidth   = 1f;

        var vlg = rt.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.padding               = new RectOffset(0, 0, padTop, padBottom);
        vlg.spacing               = spacingInner;
        vlg.childControlWidth     = true;
        vlg.childControlHeight    = true;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;

        if (title != null)
        {
            var titleRow = Row(rt, "SectionTitle", height: 26f);
            titleRow.padding = new RectOffset(20, 16, 0, 0);
            TMP("TitleTMP", titleRow.transform, title, 13f, CAccent,
                TextAlignmentOptions.Left, flexWidth: 1f);
        }

        return rt;
    }

    static HorizontalLayoutGroup Row(RectTransform parent, string name, float height,
                                     bool addImage = false, Color? color = null)
    {
        var rt = UIRect(name, parent, addImage, color);
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = height;
        le.flexibleWidth   = 1f;

        var hlg = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.childAlignment         = TextAnchor.MiddleLeft;
        hlg.childControlWidth      = true;
        hlg.childControlHeight     = true;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        return hlg;
    }

    static void Separator(RectTransform parent)
    {
        var rt = UIRect("Separator", parent, addImage: true, color: CSep);
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = 1f;
        le.flexibleWidth   = 1f;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    static TextMeshProUGUI TMP(string name, Transform parent, string text,
                               float fontSize, Color color, TextAlignmentOptions align,
                               float flexWidth = 0f, float preferredWidth = 0f)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>();
        if (preferredWidth > 0f) { le.preferredWidth = preferredWidth; le.flexibleWidth = 0f; }
        else                     { le.flexibleWidth  = flexWidth; }
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text               = text;
        tmp.fontSize           = fontSize;
        tmp.color              = color;
        tmp.alignment          = align;
        tmp.raycastTarget      = false;
        tmp.enableWordWrapping = false;
        tmp.overflowMode       = TextOverflowModes.Ellipsis;
        return tmp;
    }

    static Button Btn(Transform parent, string label, Color bgColor)
    {
        var (btn, _, _) = BtnWithRefs(parent, label, bgColor);
        return btn;
    }

    static (Button btn, TextMeshProUGUI label, Image bg)
        BtnWithRefs(Transform parent, string label, Color bgColor)
    {
        var go = new GameObject("Btn_" + label, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>();
        le.flexibleWidth  = 1f;
        le.flexibleHeight = 1f;

        var bg = go.AddComponent<Image>();
        bg.color  = bgColor;
        bg.sprite = _uiSprite;
        bg.type   = Image.Type.Sliced;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = bg;
        btn.colors = new ColorBlock
        {
            normalColor      = bgColor,
            highlightedColor = bgColor * 1.25f,
            pressedColor     = bgColor * 0.75f,
            selectedColor    = bgColor,
            disabledColor    = new Color(0.5f, 0.5f, 0.5f, 0.5f),
            colorMultiplier  = 1f,
            fadeDuration     = 0.08f,
        };

        var textGO = new GameObject("Label", typeof(RectTransform));
        textGO.transform.SetParent(go.transform, false);
        var textRT = (RectTransform)textGO.transform;
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = textRT.offsetMax = Vector2.zero;

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text               = label;
        tmp.fontSize           = 20f;
        tmp.color              = Color.white;
        tmp.alignment          = TextAlignmentOptions.Center;
        tmp.raycastTarget      = false;
        tmp.enableWordWrapping = false;
        tmp.overflowMode       = TextOverflowModes.Ellipsis;

        return (btn, tmp, bg);
    }

    static Toggle MakeToggle(Transform parent)
    {
        var go = DefaultControls.CreateToggle(GetDefaultResources());
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth  = 44f;
        le.preferredHeight = 28f;
        le.flexibleWidth   = 0f;
        var defaultLabel = go.transform.Find("Label");
        if (defaultLabel != null) Object.DestroyImmediate(defaultLabel.gameObject);
        return go.GetComponent<Toggle>();
    }

    static Slider MakeSlider(Transform parent, float min, float max, float value)
    {
        var go = DefaultControls.CreateSlider(GetDefaultResources());
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>();
        le.flexibleWidth   = 1f;
        le.preferredHeight = 24f;
        var slider = go.GetComponent<Slider>();
        slider.minValue = min;
        slider.maxValue = max;
        slider.value    = value;
        return slider;
    }

    static DefaultControls.Resources GetDefaultResources() => new()
    {
        standard   = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
        background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
        knob       = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
        checkmark  = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
    };

    static void SetRef(SerializedObject so, string field, Object value)
    {
        var prop = so.FindProperty(field);
        if (prop == null) { Debug.LogWarning($"[ControlPanelSetup] field '{field}' not found"); return; }
        prop.objectReferenceValue = value;
    }

    static void SetRefArray(SerializedObject so, string field, Object[] values)
    {
        var prop = so.FindProperty(field);
        if (prop == null) { Debug.LogWarning($"[ControlPanelSetup] array field '{field}' not found"); return; }
        prop.arraySize = values.Length;
        for (int i = 0; i < values.Length; i++)
            prop.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
    }

    static GameObject FindInScene(string name)
    {
        var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            if (go.scene == active && go.name == name) return go;
        return null;
    }
}
