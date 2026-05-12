using UnityEngine;

// Lets the user reposition a world-space panel by aiming with a controller and squeezing
// the grip trigger. Uses grip (not index trigger) so UI button presses are unaffected.
// A BoxCollider sized to the canvas rect is auto-configured in Awake.
[RequireComponent(typeof(BoxCollider))]
public class PanelDragController : MonoBehaviour
{
    OVRCameraRig        _rig;
    Transform           _cam;
    bool                _dragging;
    float               _dragDistance;
    OVRInput.Controller _dragController;
    Vector3             _hitOffset;

    void Awake()
    {
        _rig = FindObjectOfType<OVRCameraRig>();
        if (_rig != null) _cam = _rig.centerEyeAnchor;
        if (_cam == null && Camera.main != null) _cam = Camera.main.transform;

        var rt  = GetComponent<RectTransform>();
        var col = GetComponent<BoxCollider>();
        col.center = Vector3.zero;
        col.size   = rt != null
            ? new Vector3(rt.sizeDelta.x, rt.sizeDelta.y, 20f)
            : new Vector3(420f, 580f, 20f);
    }

    void Update()
    {
        if (!_dragging)
        {
            TryBeginDrag(OVRInput.Controller.RTouch, OVRInput.RawButton.RHandTrigger);
            TryBeginDrag(OVRInput.Controller.LTouch, OVRInput.RawButton.LHandTrigger);
        }
        else
        {
            bool held = _dragController == OVRInput.Controller.RTouch
                ? OVRInput.Get(OVRInput.RawButton.RHandTrigger)
                : OVRInput.Get(OVRInput.RawButton.LHandTrigger);

            if (held)
            {
                GetControllerRay(_dragController, out Vector3 origin, out Vector3 dir);
                transform.position = origin + dir * _dragDistance + _hitOffset;
                FaceCamera();
            }
            else
            {
                _dragging = false;
            }
        }
    }

    void TryBeginDrag(OVRInput.Controller ctrl, OVRInput.RawButton grip)
    {
        if (!OVRInput.GetDown(grip)) return;
        GetControllerRay(ctrl, out Vector3 origin, out Vector3 dir);
        if (Physics.Raycast(origin, dir, out RaycastHit hit, 10f)
            && hit.collider.gameObject == gameObject)
        {
            _dragDistance   = hit.distance;
            _dragController = ctrl;
            _dragging       = true;
            _hitOffset      = transform.position - (origin + dir * hit.distance);
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

    void FaceCamera()
    {
        if (_cam != null && transform.position != _cam.position)
            transform.rotation = Quaternion.LookRotation(transform.position - _cam.position);
    }
}
