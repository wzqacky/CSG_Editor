using System;
using UnityEngine;

[Serializable]
public abstract class CSGNode
{
    public abstract float SignedDistance(Vector3 point);
    public abstract Bounds GetBounds();
}

[Serializable]
public abstract class PrimitiveNode : CSGNode
{
    public Vector3 center = Vector3.zero;
}

[Serializable]
public sealed class SphereNode : PrimitiveNode
{
    public float radius = 1f;
    
    public override float SignedDistance(Vector3 point)
    {
        return Vector3.Distance(point, center) - radius;
    }

    public override Bounds GetBounds()
    // Returns an axis-aligned box containing the shape
    // as Marching cube requries a finite region to compute
    {
        return new Bounds(center, Vector3.one * (radius * 2f));
    }
}

[Serializable]
public sealed class BoxNode : PrimitiveNode {
    public Vector3 size = Vector3.one; // Width, height, depth

    public override float SignedDistance(Vector3 point)
    {
        Vector3 offset = point - center;
        Vector3 q = new Vector3(
            Mathf.Abs(offset.x),
            Mathf.Abs(offset.y),
            Mathf.Abs(offset.z)
        ) - size * 0.5f;

        float outside = Vector3.Max(q, Vector3.zero).magnitude;
        float inside = Mathf.Min(
            Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f);
        return outside + inside;
    }

    public override Bounds GetBounds()
    {
        return new Bounds(center, size);
    }
}

[Serializable]
public abstract class OperationNode : CSGNode
{
    [SerializeReference] public CSGNode leftChild;
    [SerializeReference] public CSGNode rightChild;

    protected void Validate()
    // Safety guard for empty child that would have made operations break
    {
        if (leftChild == null || rightChild == null)
            throw new InvalidOperationException($"{GetType().Name} needs a left and right child.");
    }

    public override Bounds GetBounds()
    {
        Validate();
        Bounds bounds = leftChild.GetBounds();
        bounds.Encapsulate(rightChild.GetBounds());
        return bounds;
    }
}

[Serializable]
public sealed class UnionNode : OperationNode
{
    public override float SignedDistance(Vector3 center)
    {
        Validate();
        return Mathf.Min(leftChild.SignedDistance(center),
                    rightChild.SignedDistance(center));
    }
}

[Serializable]
public sealed class IntersectNode : OperationNode
{
    public override float SignedDistance(Vector3 center)
    {
        Validate();
        return Mathf.Max(leftChild.SignedDistance(center),
                    rightChild.SignedDistance(center));
    }
}

[Serializable]
public sealed class DiffNode : OperationNode
{
    public override float SignedDistance(Vector3 center)
    {
        Validate();
        return Mathf.Max(leftChild.SignedDistance(center),
                    -rightChild.SignedDistance(center));
    }
}
