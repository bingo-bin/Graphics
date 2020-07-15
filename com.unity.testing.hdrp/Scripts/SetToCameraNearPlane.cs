using System.Collections;
using System.Collections.Generic;
using UnityEngine;
//using UnityEngine.TestTools.Graphics; // Don't know why but it doesn't compile when building player

public class SetToCameraNearPlane : MonoBehaviour
{
    public MeshRenderer renderer2;
    public Camera camera2;
    //public GraphicsTestSettings testSettings;

    [Range(0, 1)]
    public float screenSize = 0.8f;
    public float nearPlaneOffset = 0.001f;

    public Vector2 extend = Vector2.one;

    void Start()
    {
        PlaceObject();
    }

    void PlaceObject ()
    {
        float captureRatio = 1.0f; // testSettings.ImageComparisonSettings.TargetWidth * 1.0f / testSettings.ImageComparisonSettings.TargetHeight;
        float objectRatio = extend.x / extend.y;

        bool scaleBaseOnX = objectRatio >= captureRatio;

        float camDistance = camera2.nearClipPlane + nearPlaneOffset;

        float nearPlaneTargetSize = 1f;

        if (camera2.orthographic)
        {
            nearPlaneTargetSize = camera2.orthographicSize * ((scaleBaseOnX) ? captureRatio : 1f) * screenSize;
        }
        else
        {
            nearPlaneTargetSize = Mathf.Sin(camera2.fieldOfView * 0.5f * Mathf.Deg2Rad * ((scaleBaseOnX) ? captureRatio : 1f)) * camDistance * screenSize;
        }

        renderer2.transform.parent = camera2.transform;
        renderer2.transform.localPosition = new Vector3(0, 0, camDistance);
        renderer2.transform.localRotation = Quaternion.identity;
        renderer2.transform.localScale = Vector3.one * Mathf.Abs(nearPlaneTargetSize / ( (scaleBaseOnX) ? extend.x : extend.y ) );

    }

    public bool place = false;
    private void OnValidate()
    {
        if (place)
        {
            place = false;
            PlaceObject();
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (renderer2 == null) return;

        Gizmos.matrix = renderer2.transform.localToWorldMatrix;
        Gizmos.DrawWireCube(Vector3.zero, extend * 2f);
    }
}
