using UnityEngine;

// Toggles UI panel(s) on:
//   - Left controller Menu (Start) button press
//   - Left hand open-palm-facing-camera gesture, held for gestureHoldSeconds
[DefaultExecutionOrder(50)]
public class MenuToggle : MonoBehaviour
{
    [SerializeField] GameObject[] panels;
    [SerializeField] float gestureHoldSeconds = 0.6f;

    OVRHand     _leftHand;
    OVRSkeleton _leftSkeleton;
    Transform   _cam;

    float _gestureTimer;
    bool  _visible = true;

    void Awake()
    {
        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig != null)
        {
            _cam      = rig.centerEyeAnchor;
            _leftHand = rig.leftHandAnchor.GetComponentInChildren<OVRHand>(true);
            if (_leftHand != null)
                _leftSkeleton = _leftHand.GetComponent<OVRSkeleton>();
        }
        if (_cam == null && Camera.main != null)
            _cam = Camera.main.transform;
    }

    void Update()
    {
        if (OVRInput.GetDown(OVRInput.RawButton.Start))
        {
            TogglePanels();
            _gestureTimer = 0f;
            return;
        }

        // TODO: hand gesture disabled pending calibration
        // if (IsOpenPalmFacingCamera())
        // {
        //     _gestureTimer += Time.deltaTime;
        //     if (_gestureTimer >= gestureHoldSeconds)
        //     {
        //         _gestureTimer = 0f;
        //         TogglePanels();
        //     }
        // }
        // else
        // {
        //     _gestureTimer = 0f;
        // }
    }

    void TogglePanels()
    {
        _visible = !_visible;
        foreach (var p in panels)
            if (p != null) p.SetActive(_visible);
    }

    bool IsOpenPalmFacingCamera()
    {
        if (_leftHand == null || !_leftHand.IsTracked || _cam == null)
            return false;

        // All four fingers must be open
        if (_leftHand.GetFingerIsPinching(OVRHand.HandFinger.Index)  ||
            _leftHand.GetFingerIsPinching(OVRHand.HandFinger.Middle) ||
            _leftHand.GetFingerIsPinching(OVRHand.HandFinger.Ring)   ||
            _leftHand.GetFingerIsPinching(OVRHand.HandFinger.Pinky))
            return false;

        if (!TryGetPalmNormal(out Vector3 palmNormal))
            return false;

        Vector3 toCamera = (_cam.position - _leftHand.transform.position).normalized;
        return Vector3.Dot(palmNormal, toCamera) > 0.6f;
    }

    // For the left hand: -Cross(index1-wrist, middle1-wrist) gives the outward palm normal.
    // Cross product direction verified: when palm faces sky, result is (0,-1,0), so negation = (0,+1,0) = up. Correct.
    bool TryGetPalmNormal(out Vector3 palmNormal)
    {
        palmNormal = Vector3.up;
        if (_leftSkeleton == null || !_leftSkeleton.IsInitialized)
            return false;

        Transform wrist = null, index1 = null, middle1 = null;
        foreach (var bone in _leftSkeleton.Bones)
        {
            switch (bone.Id)
            {
                case OVRSkeleton.BoneId.Hand_WristRoot: wrist   = bone.Transform; break;
                case OVRSkeleton.BoneId.Hand_Index1:    index1  = bone.Transform; break;
                case OVRSkeleton.BoneId.Hand_Middle1:   middle1 = bone.Transform; break;
            }
        }

        if (wrist == null || index1 == null || middle1 == null)
            return false;

        Vector3 cross = Vector3.Cross(index1.position - wrist.position,
                                      middle1.position - wrist.position);
        if (cross.sqrMagnitude < 0.0001f) return false;

        palmNormal = -cross.normalized;
        return true;
    }
}
