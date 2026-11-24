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

    protected static Bounds TransformBounds(Bounds bounds, Vector3 pivot, Matrix4x4 transform)
    {
        Vector3 size = bounds.size;
        Vector3 worldSize = Abs(transform.MultiplyVector(Vector3.right * size.x))
            + Abs(transform.MultiplyVector(Vector3.up * size.y))
            + Abs(transform.MultiplyVector(Vector3.forward * size.z));
        return new Bounds(pivot + transform.MultiplyVector(bounds.center - pivot), worldSize);
    }

    private static Vector3 Abs(Vector3 value) => new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
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
        return TransformBounds(new Bounds(center, size), center, Matrix4x4.Rotate(Quaternion.Euler(rotation)));
    }
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
    public Vector3 scale = Vector3.one;
    public Vector3 rotation;
    public OperationColorSource colorSource = OperationColorSource.InputA;
    public Color customColor = Color.gray;
    internal Vector3 outputFrame;
    internal Vector3? outputPivot;
    internal Vector3? outputPosition;
    [NonSerialized] private Vector3 m_cachedScale;
    [NonSerialized] private Vector3 m_cachedRotation;
    [NonSerialized] private Vector3 m_cachedFrame;
    [NonSerialized] private Matrix4x4 m_outputTransform;
    [NonSerialized] private Matrix4x4 m_inverseOutputTransform;

    private bool HasTransform => outputPosition.HasValue || !scale.Equals(Vector3.one) || !rotation.Equals(Vector3.zero);
    public override bool IsComplete => leftChild?.IsComplete == true && rightChild?.IsComplete == true;
    public Vector3 Position => outputPosition ?? (IsComplete ? (outputPivot ?? GetOperandBounds().center) : Vector3.zero);

    public void MoveTo(Vector3 position) => outputPosition = position;

    internal void SetInput(bool left, CSGNode node)
    {
        if ((left ? leftChild : rightChild) == node)
            return;
        if (left) leftChild = node;
        else rightChild = node;
        outputPivot = null;
    }

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

    public sealed override float SignedDistance(Vector3 point)
    {
        Validate();
        if (!HasTransform)
            return Evaluate(point);

        UpdateTransform();
        Vector3 pivot = GetOutputPivot();
        Vector3 sample = pivot + m_inverseOutputTransform.MultiplyVector(point - outputPosition.GetValueOrDefault(pivot));
        // The smallest scale keeps the distance conservative for nonuniform scaling.
        return Evaluate(sample) * Mathf.Min(scale.x, Mathf.Min(scale.y, scale.z));
    }

    public sealed override Bounds GetBounds()
    {
        Validate();
        Bounds bounds = GetOperandBounds();
        if (!HasTransform)
            return bounds;

        UpdateTransform();
        outputPivot ??= bounds.center;
        Bounds result = TransformBounds(bounds, outputPivot.Value, m_outputTransform);
        if (outputPosition.HasValue)
            result.center += outputPosition.Value - outputPivot.Value;
        return result;
    }

    protected abstract float Evaluate(Vector3 point);

    protected virtual Bounds GetOperandBounds()
    {
        Bounds bounds = leftChild.GetBounds();
        bounds.Encapsulate(rightChild.GetBounds());
        return bounds;
    }

    private Vector3 GetOutputPivot() => outputPivot ??= GetOperandBounds().center;

    internal void PrepareOutputPivot()
    {
        if (HasTransform && IsComplete)
            GetOutputPivot();
    }

    internal void RotateOutputFrame(Quaternion delta, Vector3 pivot)
    {
        if (outputPivot.HasValue)
            outputPivot = pivot + delta * (outputPivot.Value - pivot);
        if (outputPosition.HasValue)
            outputPosition = pivot + delta * (outputPosition.Value - pivot);
        outputFrame = (delta * Quaternion.Euler(outputFrame)).eulerAngles;
    }

    private void UpdateTransform()
    {
        if (m_cachedScale.Equals(scale) && m_cachedRotation.Equals(rotation) && m_cachedFrame.Equals(outputFrame))
            return;

        Quaternion frame = Quaternion.Euler(outputFrame);
        m_outputTransform = Matrix4x4.TRS(Vector3.zero, frame * Quaternion.Euler(rotation), scale)
            * Matrix4x4.Rotate(Quaternion.Inverse(frame));
        m_inverseOutputTransform = m_outputTransform.inverse;
        m_cachedScale = scale;
        m_cachedRotation = rotation;
        m_cachedFrame = outputFrame;
    }
}

[Serializable]
public sealed class CustomShapeNode : CSGNode
{
    public string name;
    [SerializeReference] public List<CSGNode> members = new List<CSGNode>();
    private readonly List<CSGNode> m_outputs = new List<CSGNode>();

    internal Vector3? rotationPivot;
    internal float bakedScale = 1f;
    public Vector3 Rotation { get; internal set; }
    public Vector3 Scale { get; internal set; } = Vector3.one;
    public Vector3 Position => rotationPivot ?? GetOutputBounds().center;
    public override bool IsComplete => m_outputs.Count > 0;
    private bool HasScale => !Scale.Equals(Vector3.one * bakedScale);

    [NonSerialized] private Vector3 m_cachedScale;
    [NonSerialized] private Vector3 m_cachedRotation;
    [NonSerialized] private float m_cachedBakedScale;
    [NonSerialized] private Matrix4x4 m_scaleTransform;
    [NonSerialized] private Matrix4x4 m_inverseScaleTransform;
    [NonSerialized] private float m_distanceScale;

    public void MoveTo(Vector3 position) => Translate(position - Position);

    private void Translate(Vector3 delta)
    {
        rotationPivot += delta;
        foreach (var member in members)
        {
            if (member is PrimitiveNode primitive)
                primitive.center += delta;
            else if (member is OperationNode operation)
            {
                operation.outputPivot += delta;
                operation.outputPosition += delta;
            }
            else if (member is CustomShapeNode shape)
                shape.Translate(delta);
        }
    }

    public void ScaleTo(Vector3 size)
    {
        if (!(size.x > 0 && size.y > 0 && size.z > 0) ||
            float.IsInfinity(size.x) || float.IsInfinity(size.y) || float.IsInfinity(size.z))
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive and finite on every axis.");
        if (size.Equals(Scale))
            return;
        rotationPivot ??= Position;
        Scale = size;
    }

    internal Matrix4x4 GetScaleTransform()
    {
        if (!HasScale)
            return Matrix4x4.identity;

        UpdateScaleTransform();
        Vector3 pivot = rotationPivot ??= GetOutputBounds().center;
        Matrix4x4 transform = m_scaleTransform;
        Vector3 offset = pivot - transform.MultiplyVector(pivot);
        transform.SetColumn(3, new Vector4(offset.x, offset.y, offset.z, 1f));
        return transform;
    }

    private void UpdateScaleTransform()
    {
        if (m_cachedScale.Equals(Scale) && m_cachedRotation.Equals(Rotation) && m_cachedBakedScale == bakedScale)
            return;

        Vector3 factors = Scale / bakedScale;
        Quaternion orientation = Quaternion.Euler(Rotation);
        m_scaleTransform = Matrix4x4.TRS(Vector3.zero, orientation, factors) *
            Matrix4x4.Rotate(Quaternion.Inverse(orientation));
        m_inverseScaleTransform = m_scaleTransform.inverse;
        m_distanceScale = Mathf.Min(factors.x, Mathf.Min(factors.y, factors.z));
        m_cachedScale = Scale;
        m_cachedRotation = Rotation;
        m_cachedBakedScale = bakedScale;
    }

    public void RotateTo(Vector3 angles)
    {
        Quaternion delta = Quaternion.Euler(angles) * Quaternion.Inverse(Quaternion.Euler(Rotation));
        PrepareOperationPivots();
        RotateBy(delta, Position);
        Rotation = angles;
    }

    private void PrepareOperationPivots()
    {
        foreach (var member in members)
        {
            if (member is OperationNode operation)
                operation.PrepareOutputPivot();
            else if (member is CustomShapeNode shape)
                shape.PrepareOperationPivots();
        }
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
            else if (member is OperationNode operation)
                operation.RotateOutputFrame(delta, pivot);
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
        float distanceScale = 1f;
        if (HasScale)
        {
            UpdateScaleTransform();
            Vector3 pivot = rotationPivot ??= GetOutputBounds().center;
            point = pivot + m_inverseScaleTransform.MultiplyVector(point - pivot);
            distanceScale = m_distanceScale;
        }

        float distance = float.PositiveInfinity;
        foreach (var output in m_outputs)
            distance = Mathf.Min(distance, output.SignedDistance(point));
        return distance * distanceScale;
    }

    public override Color GetColor() => IsComplete ? m_outputs[0].GetColor() : Color.gray;

    public override Bounds GetBounds()
    {
        Bounds bounds = GetOutputBounds();
        if (!HasScale || !IsComplete)
            return bounds;

        UpdateScaleTransform();
        rotationPivot ??= bounds.center;
        return TransformBounds(bounds, rotationPivot.Value, m_scaleTransform);
    }

    private Bounds GetOutputBounds()
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
    protected override float Evaluate(Vector3 point) =>
        Mathf.Min(leftChild.SignedDistance(point), rightChild.SignedDistance(point));
}

[Serializable]
public sealed class IntersectNode : OperationNode
{
    protected override float Evaluate(Vector3 point) =>
        Mathf.Max(leftChild.SignedDistance(point), rightChild.SignedDistance(point));
}

[Serializable]
public sealed class DiffNode : OperationNode
{
    protected override Bounds GetOperandBounds() => leftChild.GetBounds();

    protected override float Evaluate(Vector3 point) =>
        Mathf.Max(leftChild.SignedDistance(point), -rightChild.SignedDistance(point));
}
