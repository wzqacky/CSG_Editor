using UnityEngine;

[System.Serializable]
public class SphereNode
{
    public Vector3 center = Vector3.zero;
    public float radius = 1f;

    public float SignedDistance(Vector3 point)
    {
        return Vector3.Distance(point, center) - radius;
    }
}