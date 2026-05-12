using UnityEngine;

// Attach to RightControllerAnchor (added automatically by RaycastSetup).
// Draws a visual beam and positions a cursor dot where the ray hits an interactable surface.
// Actual UI event dispatch is handled by OVRInputModule + OVRRaycaster — this is visuals only.
[RequireComponent(typeof(LineRenderer))]
public class ControllerRayPointer : MonoBehaviour
{
    [SerializeField] private OVRInput.Controller controller   = OVRInput.Controller.RTouch;
    [SerializeField] private LayerMask           interactMask = 1 << 5; // UI layer
    [SerializeField] private float               maxLength    = 8f;
    [SerializeField] private GameObject          cursor;

    [Header("Beam colours")]
    [SerializeField] private Color colorIdle = new(1f,  1f,  1f,  0.35f);
    [SerializeField] private Color colorHit  = new(0.3f, 0.85f, 1f, 0.85f);

    private LineRenderer _lr;

    private void Awake() => _lr = GetComponent<LineRenderer>();

    private void Update()
    {
        bool active = OVRInput.IsControllerConnected(controller);
        _lr.enabled = active;

        if (cursor) cursor.SetActive(false);
        if (!active) return;

        Vector3 origin = transform.position;
        Vector3 dir    = transform.forward;

        _lr.SetPosition(0, origin);

        if (Physics.Raycast(origin, dir, out RaycastHit hit, maxLength, interactMask,
                            QueryTriggerInteraction.Collide))
        {
            _lr.SetPosition(1, hit.point);
            SetColor(colorHit);

            if (cursor)
            {
                cursor.transform.position = hit.point - dir * 0.001f;
                cursor.transform.forward  = dir;
                cursor.SetActive(true);
            }
        }
        else
        {
            _lr.SetPosition(1, origin + dir * maxLength);
            // Fade to transparent at tip when pointing at nothing
            _lr.startColor = colorIdle;
            _lr.endColor   = new Color(colorIdle.r, colorIdle.g, colorIdle.b, 0f);
        }
    }

    private void SetColor(Color c) { _lr.startColor = c; _lr.endColor = c; }
}
