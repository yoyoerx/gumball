using UnityEngine;

/// <summary>
/// Keeps the VideoScreen quad softly orbiting in front of the headset.
///
/// Manual override — all modes pause soft-follow until the A button is pressed:
///   Controller index trigger aimed at panel  →  drag panel to new position
///   Hand index pinch near panel              →  drag panel to new position
///   Left thumbstick                          →  pan panel (up/down/left/right relative to head)
///   Right thumbstick Y                       →  push/pull panel depth
///   A button                                 →  resume soft-follow from current position
///
/// Attach to the VideoScreen quad. A BoxCollider is auto-added for ray hit-testing.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class VideoFrameFollow : MonoBehaviour
{
    [Header("Soft Follow")]
    [SerializeField] float followDistance    = 2.5f;
    [SerializeField] float positionLerpSpeed = 1.5f;
    [SerializeField] float rotationLerpSpeed = 2.0f;

    [Header("Thumbstick")]
    [SerializeField] float panSpeed   = 0.8f;
    [SerializeField] float depthSpeed = 0.5f;
    [SerializeField] float minDepth   = 0.5f;
    [SerializeField] float maxDepth   = 6.0f;

    [Header("Controller Drag")]
    [SerializeField] float dragDepthScale = 10f;  // controller push/pull amplification

    Transform    _cam;
    OVRCameraRig _rig;
    OVRHand      _rightHand;
    OVRHand      _leftHand;

    bool _paused;

    float _startupDelay = 1.2f;  // wait for OVR tracking to initialize before first SoftFollow

    bool                _controllerDragging;
    float               _dragDistance;
    OVRInput.Controller _dragController;
    Vector3             _lastControllerOrigin;
    Vector3             _hitOffset;          // panel pivot → ray-hit offset, kept during drag

    bool    _handDragging;
    Vector3 _handDragOffset;

    void Awake()
    {
        _rig = FindObjectOfType<OVRCameraRig>();
        if (_rig != null) _cam = _rig.centerEyeAnchor;
        if (_cam == null && Camera.main != null) _cam = Camera.main.transform;
        if (_cam == null) _cam = transform;   // fallback; should not happen

        if (_rig != null)
        {
            _rightHand = _rig.rightHandAnchor.GetComponentInChildren<OVRHand>(true);
            _leftHand  = _rig.leftHandAnchor.GetComponentInChildren<OVRHand>(true);
        }

        // Thin slab collider covering the quad face (quad local size = 1×1)
        var col   = GetComponent<BoxCollider>();
        col.center = Vector3.zero;
        col.size   = new Vector3(1f, 1f, 0.02f);
    }

    void Update()
    {
        if (_startupDelay > 0f)
        {
            _startupDelay -= Time.deltaTime;
            return;
        }

        HandleThumbstick();
        HandleControllerDrag();
        HandleHandPinch();

        bool manualActive = _controllerDragging || _handDragging;

        if (!manualActive)
        {
            if (_paused && OVRInput.GetDown(OVRInput.RawButton.A))
                _paused = false;

            if (!_paused)
                SoftFollow();
        }
    }

    // ── Soft follow ──────────────────────────────────────────────────────────────

    void SoftFollow()
    {
        // Cap dt so a large first-frame deltaTime can never cause a sudden jump
        float dt = Mathf.Min(Time.deltaTime, 0.05f);

        Vector3 targetPos = _cam.position + _cam.forward * followDistance;
        transform.position = Vector3.Lerp(transform.position, targetPos, positionLerpSpeed * dt);

        // Yaw locked to camera's horizontal forward; pitch follows elevation to panel
        if (TryFacingRotation(out Quaternion targetRot))
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotationLerpSpeed * dt);
    }

    // ── Thumbstick ───────────────────────────────────────────────────────────────

    void HandleThumbstick()
    {
        // Left stick: pan relative to head orientation
        Vector2 pan = OVRInput.Get(OVRInput.RawAxis2D.LThumbstick);
        if (pan.sqrMagnitude > 0.01f)
        {
            PauseFollow();
            transform.position += (_cam.right * pan.x + _cam.up * pan.y) * panSpeed * Time.deltaTime;
            UpdateFollowDistance();
        }

        // Right stick Y: push/pull depth
        float push = OVRInput.Get(OVRInput.RawAxis2D.RThumbstick).y;
        if (Mathf.Abs(push) > 0.1f)
        {
            PauseFollow();
            transform.position += _cam.forward * push * depthSpeed * Time.deltaTime;
            UpdateFollowDistance();
        }
    }

    // ── Controller trigger drag ──────────────────────────────────────────────────

    void HandleControllerDrag()
    {
        if (!_controllerDragging)
        {
            TryBeginControllerDrag(OVRInput.Controller.RTouch, OVRInput.RawButton.RIndexTrigger);
            TryBeginControllerDrag(OVRInput.Controller.LTouch, OVRInput.RawButton.LIndexTrigger);
        }
        else
        {
            bool held = _dragController == OVRInput.Controller.RTouch
                ? OVRInput.Get(OVRInput.RawButton.RIndexTrigger)
                : OVRInput.Get(OVRInput.RawButton.LIndexTrigger);

            if (held)
            {
                GetControllerRay(_dragController, out Vector3 origin, out Vector3 dir);

                // Project controller movement onto the ray to push/pull depth
                float depthDelta = Vector3.Dot(origin - _lastControllerOrigin, dir);
                _dragDistance = Mathf.Clamp(_dragDistance + depthDelta * dragDepthScale, minDepth, maxDepth);
                _lastControllerOrigin = origin;

                // _hitOffset keeps the grab point on the panel under the ray tip
                transform.position = origin + dir * _dragDistance + _hitOffset;
                FaceCamera();
                UpdateFollowDistance();
            }
            else
            {
                _controllerDragging = false;
            }
        }
    }

    void TryBeginControllerDrag(OVRInput.Controller ctrl, OVRInput.RawButton trigger)
    {
        if (!OVRInput.GetDown(trigger)) return;
        GetControllerRay(ctrl, out Vector3 origin, out Vector3 dir);
        if (Physics.Raycast(origin, dir, out RaycastHit hit, 10f)
            && hit.collider.gameObject == gameObject)
        {
            _dragDistance         = hit.distance;
            _lastControllerOrigin = origin;
            _dragController       = ctrl;
            _controllerDragging   = true;
            _hitOffset            = transform.position - (origin + dir * hit.distance);
            PauseFollow();
        }
    }

    void GetControllerRay(OVRInput.Controller ctrl, out Vector3 origin, out Vector3 dir)
    {
        Vector3    localPos = OVRInput.GetLocalControllerPosition(ctrl);
        Quaternion localRot = OVRInput.GetLocalControllerRotation(ctrl);
        if (_rig != null)
        {
            origin = _rig.transform.TransformPoint(localPos);
            dir    = _rig.transform.TransformDirection(localRot * Vector3.forward);
        }
        else
        {
            origin = localPos;
            dir    = localRot * Vector3.forward;
        }
    }

    // ── Hand pinch drag ──────────────────────────────────────────────────────────

    void HandleHandPinch()
    {
        OVRHand pinching = ActivePinchHand();

        if (!_handDragging && pinching != null)
        {
            Vector3 tip = IndexTipPosition(pinching);
            if (Vector3.Distance(tip, transform.position) < 0.5f)
            {
                _handDragOffset = transform.position - tip;
                _handDragging   = true;
                PauseFollow();
            }
        }

        if (_handDragging)
        {
            if (pinching != null)
            {
                Vector3 tip = IndexTipPosition(pinching);
                transform.position = tip + _handDragOffset;
                FaceCamera();
                UpdateFollowDistance();
            }
            else
            {
                _handDragging = false;
            }
        }
    }

    OVRHand ActivePinchHand()
    {
        if (_rightHand != null && _rightHand.IsTracked
            && _rightHand.GetFingerIsPinching(OVRHand.HandFinger.Index))
            return _rightHand;
        if (_leftHand != null && _leftHand.IsTracked
            && _leftHand.GetFingerIsPinching(OVRHand.HandFinger.Index))
            return _leftHand;
        return null;
    }

    Vector3 IndexTipPosition(OVRHand hand)
    {
        var skeleton = hand.GetComponent<OVRSkeleton>();
        if (skeleton != null && skeleton.IsInitialized)
        {
            foreach (var bone in skeleton.Bones)
            {
                if (bone.Id == OVRSkeleton.BoneId.Hand_IndexTip)
                    return bone.Transform.position;
            }
        }
        return hand.transform.position;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    void FaceCamera()
    {
        if (TryFacingRotation(out Quaternion rot))
            transform.rotation = rot;
    }

    // Yaw = camera's horizontal forward (panel never swings left/right to track position).
    // Pitch = elevation angle from camera to panel (panel tips toward/away with height).
    bool TryFacingRotation(out Quaternion rot)
    {
        Vector3 camFlat = new Vector3(_cam.forward.x, 0f, _cam.forward.z);
        if (camFlat.sqrMagnitude < 0.001f) { rot = transform.rotation; return false; }
        camFlat.Normalize();

        Vector3 toPanel = transform.position - _cam.position;
        float   hDist   = new Vector2(toPanel.x, toPanel.z).magnitude;
        float   elevDeg = Mathf.Atan2(toPanel.y, Mathf.Max(hDist, 0.01f)) * Mathf.Rad2Deg;

        rot = Quaternion.LookRotation(camFlat) * Quaternion.Euler(-elevDeg, 0f, 0f);
        return true;
    }

    void UpdateFollowDistance()
    {
        followDistance = Mathf.Clamp(
            Vector3.Distance(_cam.position, transform.position), minDepth, maxDepth);
    }

    void PauseFollow()
    {
        _paused = true;
    }
}
