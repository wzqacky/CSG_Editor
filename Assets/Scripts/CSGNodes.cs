using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public abstract class CSGNode
{
    public virtual bool IsComplete => true;
    public abstract float SignedDistance(Vector3 point);
    public abstract Color GetColor();
    // Returns an axis-aligned box containing the shape
    // as Marching cube requries a finite region to compute
    public abstract Bounds GetBounds();
}

[Serializable]
public abstract class PrimitiveNode : CSGNode
{
    public Vector3 center = Vector3.zero;
    public Vector3 rotation;
    public Color color = Color.gray;

    public override Color GetColor() => color;
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
    {
        return new Bounds(center, Vector3.one * (radius * 2f));
    }
}

[Serializable]
public sealed class BoxNode : PrimitiveNode {
    public Vector3 size = Vector3.one; // Width, height, depth
    [NonSerialized] private Vector3 m_sampleRotation;
    [NonSerialized] private Quaternion m_inverseRotation = Quaternion.identity;

    public override float SignedDistance(Vector3 point)
    {
        Vector3 offset = point - center;
        if (!rotation.Equals(Vector3.zero))
        {
            if (!rotation.Equals(m_sampleRotation))
            {
                m_inverseRotation = Quaternion.Inverse(Quaternion.Euler(rotation));
                m_sampleRotation = rotation;
            }
            offset = m_inverseRotation * offset;
        }
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
        Quaternion orientation = Quaternion.Euler(rotation);
        Vector3 worldSize = Abs(orientation * (Vector3.right * size.x))
            + Abs(orientation * (Vector3.up * size.y))
            + Abs(orientation * (Vector3.forward * size.z));
        return new Bounds(center, worldSize);
    }

    private static Vector3 Abs(Vector3 value) => new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
}

public enum OperationColorSource
{
    InputA,
    InputB,
    Custom
}

[Serializable]
public abstract class OperationNode : CSGNode
{
    [SerializeReference] public CSGNode leftChild;
    [SerializeReference] public CSGNode rightChild;
    public OperationColorSource colorSource = OperationColorSource.InputA;
    public Color customColor = Color.gray;
    public override bool IsComplete => leftChild?.IsComplete == true && rightChild?.IsComplete == true;

    public override Color GetColor()
    {
        switch (colorSource)
        {
            case OperationColorSource.InputA:
                Validate();
                return leftChild.GetColor();
            case OperationColorSource.InputB:
                Validate();
                return rightChild.GetColor();
            case OperationColorSource.Custom:
                return customColor;
            default:
                throw new InvalidOperationException($"Unknown operation color source: {colorSource}.");
        }
    }

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
public sealed class CustomShapeNode : CSGNode
{
    public string name;
    [SerializeReference] public List<CSGNode> members = new List<CSGNode>();
    private readonly List<CSGNode> m_outputs = new List<CSGNode>();

    internal Vector3? rotationPivot;
    public Vector3 Rotation { get; internal set; }
    public Vector3 Position => rotationPivot ?? GetBounds().center;
    public override bool IsComplete => m_outputs.Count > 0;

    public void MoveTo(Vector3 position) => Translate(position - Position);

    private void Translate(Vector3 delta)
    {
        rotationPivot += delta;
        foreach (var member in members)
        {
            if (member is PrimitiveNode primitive)
                primitive.center += delta;
            else if (member is CustomShapeNode shape)
                shape.Translate(delta);
        }
    }

    public void RotateTo(Vector3 angles)
    {
        Quaternion delta = Quaternion.Euler(angles) * Quaternion.Inverse(Quaternion.Euler(Rotation));
        RotateBy(delta, Position);
        Rotation = angles;
    }

    private void RotateBy(Quaternion delta, Vector3 pivot)
    {
        // Keep a fixed pivot even when rotation changes the center of the axis-aligned bounds.
        rotationPivot = pivot + delta * (Position - pivot);
        Rotation = (delta * Quaternion.Euler(Rotation)).eulerAngles;
        foreach (var member in members)
        {
            if (member is PrimitiveNode primitive)
            {
                primitive.center = pivot + delta * (primitive.center - pivot);
                primitive.rotation = (delta * Quaternion.Euler(primitive.rotation)).eulerAngles;
            }
            else if (member is CustomShapeNode shape)
                shape.RotateBy(delta, pivot);
        }
    }

    public void SetColor(Color color)
    {
        foreach (var member in members)
        {
            if (member is PrimitiveNode primitive)
                primitive.color = color;
            else if (member is OperationNode operation && operation.colorSource == OperationColorSource.Custom)
                operation.customColor = color;
            else if (member is CustomShapeNode shape)
                shape.SetColor(color);
        }
    }

    internal void RefreshOutputs(HashSet<CustomShapeNode> updated)
    {
        if (!updated.Add(this))
            return;
        foreach (var member in members)
            if (member is CustomShapeNode shape)
                shape.RefreshOutputs(updated);

        var consumed = new HashSet<CSGNode>();
        foreach (var member in members)
            if (member is OperationNode operation && operation.IsComplete)
            {
                consumed.Add(operation.leftChild);
                consumed.Add(operation.rightChild);
            }
        m_outputs.Clear();
        foreach (var member in members)
            if (member.IsComplete && !consumed.Contains(member))
                m_outputs.Add(member);
    }

    public override float SignedDistance(Vector3 point)
    {
        float distance = float.PositiveInfinity;
        foreach (var output in m_outputs)
            distance = Mathf.Min(distance, output.SignedDistance(point));
        return distance;
    }

    public override Color GetColor() => IsComplete ? m_outputs[0].GetColor() : Color.gray;

    public override Bounds GetBounds()
    {
        if (!IsComplete)
            return new Bounds();
        Bounds bounds = m_outputs[0].GetBounds();
        for (int i = 1; i < m_outputs.Count; i++)
            bounds.Encapsulate(m_outputs[i].GetBounds());
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
    public override Bounds GetBounds()
    {
        Validate();
        return leftChild.GetBounds();
    }

    public override float SignedDistance(Vector3 center)
    {
        Validate();
        return Mathf.Max(leftChild.SignedDistance(center),
                    -rightChild.SignedDistance(center));
    }
}
