using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Renamed from GimbalControlPanel → ControlPanel.
// Rename the .cs file to ControlPanel.cs in the Unity Project window.
public class ControlPanel : MonoBehaviour
{
    // ── Dependencies ───────────────────────────────────────────────────────────
    [Header("Dependencies")]
    [SerializeField] private KasaPTZController    gimbal;
    [SerializeField] private GimbalInputController inputController;
    [SerializeField] public  LockOnTracker         lockOnTracker;

    // ── Tab pages ──────────────────────────────────────────────────────────────
    [Header("Tabs")]
    [SerializeField] private GameObject gimbalPage;
    [SerializeField] private GameObject cameraPage;
    [SerializeField] private Image      gimbalTabBg;
    [SerializeField] private Image      cameraTabBg;
    [SerializeField] private Button     gimbalTabBtn;
    [SerializeField] private Button     cameraTabBtn;

    // ── Gimbal tab UI ──────────────────────────────────────────────────────────
    [Header("Gimbal — Status")]
    [SerializeField] private Image           statusDot;
    [SerializeField] private TextMeshProUGUI statusLabel;

    [Header("Gimbal — Position")]
    [SerializeField] private TextMeshProUGUI yawValue;
    [SerializeField] private TextMeshProUGUI pitchValue;
    [SerializeField] private TextMeshProUGUI rollValue;

    [Header("Gimbal — Controls")]
    [SerializeField] private Toggle          followModeToggle;
    [SerializeField] private Slider          speedSlider;
    [SerializeField] private TextMeshProUGUI speedValueLabel;

    [Header("Gimbal — Buttons")]
    [SerializeField] private Button          centerButton;
    [SerializeField] private Button          lockOnButton;       // repurposed: Stop Tracking
    [SerializeField] private TextMeshProUGUI lockOnButtonLabel;
    [SerializeField] private TextMeshProUGUI trackingStatusLabel;
    [SerializeField] private Button          connectButton;
    [SerializeField] private TextMeshProUGUI connectButtonLabel;
    [SerializeField] private Image           connectButtonBg;

    // ── Camera tab UI ──────────────────────────────────────────────────────────
    [Header("Camera — Info")]
    [SerializeField] private TextMeshProUGUI serverHostLabel;
    [SerializeField] private TextMeshProUGUI cameraIpLabel;

    [Header("Camera — Slots (4 discovered cameras)")]
    [SerializeField] private Button[]          cameraSlotBtns;
    [SerializeField] private TextMeshProUGUI[] cameraSlotLabels;
    [SerializeField] private Image[]           cameraSlotBgs;

    [Header("Camera — Actions")]
    [SerializeField] private Button          scanBtn;
    [SerializeField] private Button          applyBtn;
    [SerializeField] private TextMeshProUGUI scanStatusLabel;

    // ── Colors ─────────────────────────────────────────────────────────────────
    private static readonly Color Green        = new(0.15f, 0.72f, 0.44f, 1f);
    private static readonly Color Red          = new(0.85f, 0.22f, 0.22f, 1f);
    private static readonly Color Blue         = new(0.22f, 0.48f, 0.88f, 1f);
    private static readonly Color TabActive    = new(0.22f, 0.48f, 0.88f, 1f);
    private static readonly Color TabInactive  = new(0.12f, 0.15f, 0.23f, 1f);
    private static readonly Color SlotDefault  = new(0.18f, 0.22f, 0.32f, 1f);
    private static readonly Color SlotSelected = new(0.22f, 0.48f, 0.88f, 1f);

    // ── Runtime state ──────────────────────────────────────────────────────────
    private readonly string[] _slotIps = new string[4];
    private string _selectedIp = "";

    // ── Unity lifecycle ────────────────────────────────────────────────────────

    private void Start()
    {
        // Gimbal tab
        centerButton.onClick.AddListener(() => gimbal?.SetAngles(0f, 0f, 0f));
        lockOnButton.onClick.AddListener(OnStopTracking);
        connectButton.onClick.AddListener(OnConnectToggle);

        // Lock-on tracker events
        if (lockOnTracker != null)
        {
            lockOnTracker.OnTrackingStarted  += OnTrackingStarted;
            lockOnTracker.OnTrackingLost     += OnTrackingLost;
            lockOnTracker.OnTrackingStopped  += OnTrackingStopped;
        }
        UpdateTrackingUI();
        followModeToggle.onValueChanged.AddListener(v => gimbal?.SetFollowMode(v));
        speedSlider.onValueChanged.AddListener(OnSpeedChanged);
        OnSpeedChanged(speedSlider.value);

        // Tab buttons
        gimbalTabBtn.onClick.AddListener(ShowGimbalTab);
        cameraTabBtn.onClick.AddListener(ShowCameraTab);
        ShowGimbalTab();

        // Camera slot buttons
        for (int i = 0; i < cameraSlotBtns.Length; i++)
        {
            int idx = i;
            cameraSlotBtns[i].onClick.AddListener(() => SelectSlot(idx));
        }

        // Camera action buttons
        scanBtn .onClick.AddListener(OnScan);
        applyBtn.onClick.AddListener(OnApply);

        // Gimbal events
        if (gimbal != null)
        {
            gimbal.OnConfigReceived += OnConfigReceived;
            gimbal.OnScanComplete   += OnScanComplete;
            gimbal.OnCameraApplied  += OnCameraApplied;
        }

        ClearSlots();
        SetApplyEnabled(false);
    }

    private void OnDestroy()
    {
        if (gimbal != null)
        {
            gimbal.OnConfigReceived -= OnConfigReceived;
            gimbal.OnScanComplete   -= OnScanComplete;
            gimbal.OnCameraApplied  -= OnCameraApplied;
        }
        if (lockOnTracker != null)
        {
            lockOnTracker.OnTrackingStarted -= OnTrackingStarted;
            lockOnTracker.OnTrackingLost    -= OnTrackingLost;
            lockOnTracker.OnTrackingStopped -= OnTrackingStopped;
        }
    }

    private void Update()
    {
        if (gimbal == null) return;

        bool connected = gimbal.IsConnected;
        statusDot.color         = connected ? Green : Red;
        statusLabel.text        = connected ? "Connected" : "Disconnected";
        connectButtonLabel.text = connected ? "Disconnect" : "Connect";
        connectButtonBg.color   = connected ? Red : Blue;

        yawValue  .text = gimbal.CurrentX.ToString();
        pitchValue.text = gimbal.CurrentY.ToString();
        rollValue .text = "—";
    }

    // ── Tab switching ──────────────────────────────────────────────────────────

    private void ShowGimbalTab()
    {
        gimbalPage.SetActive(true);
        cameraPage.SetActive(false);
        gimbalTabBg.color = TabActive;
        cameraTabBg.color = TabInactive;
    }

    private void ShowCameraTab()
    {
        gimbalPage.SetActive(false);
        cameraPage.SetActive(true);
        gimbalTabBg.color = TabInactive;
        cameraTabBg.color = TabActive;
        gimbal?.GetConfig();   // refresh displayed config whenever tab opens
    }

    // ── Gimbal tab handlers ────────────────────────────────────────────────────

    private void OnConnectToggle()
    {
        if (gimbal == null) return;
        if (gimbal.IsConnected) gimbal.Disconnect();
        else                    gimbal.Connect();
    }

    private void OnStopTracking() => lockOnTracker?.StopTracking();

    private void OnTrackingStarted(int trackId, string label) => UpdateTrackingUI();
    private void OnTrackingLost()                              => UpdateTrackingUI();
    private void OnTrackingStopped()                           => UpdateTrackingUI();

    private void UpdateTrackingUI()
    {
        bool tracking = lockOnTracker != null && lockOnTracker.IsTracking;

        if (trackingStatusLabel != null)
        {
            if (lockOnTracker == null)
                trackingStatusLabel.text = "";
            else if (tracking)
                trackingStatusLabel.text = $"Tracking [{lockOnTracker.LockedTrackId}] {lockOnTracker.LockedLabel}";
            else
                trackingStatusLabel.text = "Idle";
        }

        if (lockOnButton != null)    lockOnButton.interactable = tracking;
        if (lockOnButtonLabel != null)
            lockOnButtonLabel.text = "Stop Tracking";
    }

    private void OnSpeedChanged(float value)
    {
        speedValueLabel.text = $"{value:F0}°/s";
        if (inputController != null)
            inputController.JoystickSensitivity = value;
    }

    // ── Camera tab handlers ────────────────────────────────────────────────────

    private void OnScan()
    {
        if (gimbal == null || !gimbal.IsConnected) return;
        ClearSlots();
        SetApplyEnabled(false);
        scanStatusLabel.text = "Scanning…";
        scanBtn.interactable = false;
        gimbal.ScanCameras();
    }

    private void OnApply()
    {
        if (string.IsNullOrEmpty(_selectedIp) || gimbal == null) return;
        applyBtn.interactable = false;
        scanStatusLabel.text  = "Applying…";
        gimbal.SetCamera(_selectedIp);
    }

    private void SelectSlot(int idx)
    {
        if (idx >= _slotIps.Length || string.IsNullOrEmpty(_slotIps[idx])) return;
        _selectedIp = _slotIps[idx];
        for (int i = 0; i < cameraSlotBgs.Length; i++)
            cameraSlotBgs[i].color = (i == idx) ? SlotSelected : SlotDefault;
        SetApplyEnabled(true);
    }

    // ── Gimbal event handlers (called on Unity main thread via DispatchMessageQueue) ──

    private void OnConfigReceived(string camIp, string serverHost)
    {
        if (cameraIpLabel  != null) cameraIpLabel.text  = camIp;
        if (serverHostLabel != null) serverHostLabel.text = serverHost;
    }

    private void OnScanComplete(string[] cameras)
    {
        ClearSlots();
        for (int i = 0; i < cameras.Length && i < _slotIps.Length; i++)
        {
            _slotIps[i]              = cameras[i];
            cameraSlotLabels[i].text = cameras[i];
            cameraSlotBtns[i].interactable = true;
        }
        scanStatusLabel.text  = cameras.Length > 0
            ? $"Found {cameras.Length} camera(s)"
            : "No cameras found";
        scanBtn.interactable = true;
    }

    private void OnCameraApplied()
    {
        cameraIpLabel.text    = _selectedIp;
        scanStatusLabel.text  = "Saved — restart server to apply";
        applyBtn.interactable = false;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void ClearSlots()
    {
        for (int i = 0; i < _slotIps.Length; i++)
        {
            _slotIps[i]              = "";
            cameraSlotLabels[i].text = "—";
            cameraSlotBgs[i].color   = SlotDefault;
            cameraSlotBtns[i].interactable = false;
        }
    }

    private void SetApplyEnabled(bool enabled)
    {
        if (applyBtn != null) applyBtn.interactable = enabled;
    }
}
