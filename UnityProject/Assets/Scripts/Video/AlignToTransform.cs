using UnityEngine;

// Keeps this GameObject's world position and rotation locked to another transform each frame.
// Used so DetectionCanvas (sibling of VideoScreen) stays aligned when VideoFrameFollow moves it.
[DefaultExecutionOrder(100)]
public class AlignToTransform : MonoBehaviour
{
    [SerializeField] Transform target;

    void LateUpdate()
    {
        if (target != null)
            transform.SetPositionAndRotation(target.position, target.rotation);
    }
}
