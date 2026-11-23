using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

public sealed class CSGHistory
{
    private readonly List<string> m_states = new List<string>();
    private int m_index;
    public bool CanUndo => m_index > 0;
    public bool CanRedo => m_index < m_states.Count - 1;

    public CSGHistory(string initialState)
    {
        m_states.Add(initialState);
    }

    public void Record(string state)
    {
        if (m_states[m_index] == state) return;
        m_states.RemoveRange(m_index + 1, m_states.Count - m_index - 1);
        m_states.Add(state);
        if (m_states.Count > 100) m_states.RemoveAt(0);
        m_index = m_states.Count - 1;
    }

    public string Undo()
    {
        if (!CanUndo) throw new InvalidOperationException("Nothing to undo.");
        return m_states[--m_index];
    }

    public string Redo()
    {
        if (!CanRedo) throw new InvalidOperationException("Nothing to redo.");
        return m_states[++m_index];
    }
}

public static class CSGState
{
    [Serializable]
    private sealed class Document
    {
        public int version;
        public int nextId;
        public List<NodeData> nodes;
        public List<CSGStorage.CustomShape> customShapes;
    }

    [Serializable]
    private sealed class NodeData
    {
        public int id;
        public string type;
        public Vector2 position;
        public bool hasPosition;
        public Vector3 center;
        public Vector3 size;
        public float radius;
        public Color color;
        public int left;
        public int right;
        public OperationColorSource colorSource;
    }

    public static string Serialize(CSGStorage storage)
    {
        var document = new Document
        {
            version = 1, nextId = storage.NextId, nodes = new List<NodeData>(),
            customShapes = new List<CSGStorage.CustomShape>(storage.CustomShapes)
        };
        var ids = new Dictionary<CSGNode, int>();
        foreach (var entity in storage.Entities)
            ids.Add(entity.node, entity.id);

        foreach (var entity in storage.Entities)
        {
            var data = new NodeData
            {
                id = entity.id, type = entity.node.GetType().Name,
                position = entity.graphPosition, hasPosition = entity.hasGraphPosition
            };
            if (entity.node is PrimitiveNode primitive)
            {
                data.center = primitive.center;
                data.color = primitive.color;
                if (primitive is SphereNode sphere) data.radius = sphere.radius;
                else if (primitive is BoxNode box) data.size = box.size;
            }
            else if (entity.node is OperationNode operation)
            {
                data.left = operation.leftChild == null ? 0 : ids[operation.leftChild];
                data.right = operation.rightChild == null ? 0 : ids[operation.rightChild];
                data.colorSource = operation.colorSource;
                data.color = operation.customColor;
            }
            else
                throw new InvalidOperationException("Unsupported CSG node type.");
            document.nodes.Add(data);
        }
        return JsonUtility.ToJson(document, true);
    }

    public static CSGStorage Deserialize(string json)
    {
        return Deserialize(json, true);
    }

    private static CSGStorage Deserialize(string json, bool allowCustomShapes)
    {
        var document = JsonUtility.FromJson<Document>(json);
        if (document == null || document.version != 1 || document.nodes == null || document.nextId <= 0)
            throw new FormatException("Invalid or unsupported CSG state file.");
        if (!allowCustomShapes && document.customShapes != null && document.customShapes.Count != 0)
            throw new FormatException("Custom shapes cannot contain other custom shapes.");
        var storage = new CSGStorage();
        var nodes = new Dictionary<int, CSGNode>();
        foreach (var data in document.nodes)
        {
            if (data == null || data.id <= 0 || data.id == int.MaxValue || nodes.ContainsKey(data.id) ||
                !Finite(data.position.x) || !Finite(data.position.y) || !Finite(data.color))
                throw new FormatException("Invalid node data or duplicate node ID.");
            CSGNode node;
            switch (data.type)
            {
                case "SphereNode":
                    if (!Finite(data.center) || !Finite(data.radius) || data.radius <= 0)
                        throw new FormatException("Invalid sphere dimensions or position.");
                    node = new SphereNode { center = data.center, color = data.color, radius = data.radius };
                    break;
                case "BoxNode":
                    if (!Finite(data.center) || !Finite(data.size) ||
                        data.size.x <= 0 || data.size.y <= 0 || data.size.z <= 0)
                        throw new FormatException("Invalid box dimensions or position.");
                    node = new BoxNode { center = data.center, color = data.color, size = data.size };
                    break;
                case "UnionNode": node = new UnionNode(); break;
                case "IntersectNode": node = new IntersectNode(); break;
                case "DiffNode": node = new DiffNode(); break;
                default: throw new FormatException("Unknown CSG node type: " + data.type);
            }
            if (node is OperationNode operation)
            {
                if (!Enum.IsDefined(typeof(OperationColorSource), data.colorSource))
                    throw new FormatException("Invalid operation color source.");
                operation.colorSource = data.colorSource;
                operation.customColor = data.color;
            }
            var entity = storage.AddWithId(data.id, node);
            entity.graphPosition = data.position;
            entity.hasGraphPosition = data.hasPosition;
            nodes.Add(data.id, node);
        }
        if (document.nextId < storage.NextId)
            throw new FormatException("Invalid next node ID.");
        storage.SetNextId(document.nextId);
        foreach (var data in document.nodes)
        {
            if (!(nodes[data.id] is OperationNode operation))
                continue;
            operation.leftChild = Resolve(nodes, data.left);
            operation.rightChild = Resolve(nodes, data.right);
        }
        var visiting = new HashSet<CSGNode>();
        var visited = new HashSet<CSGNode>();
        foreach (var entity in storage.Entities)
            ValidateAcyclic(entity.node, visiting, visited);
        if (document.customShapes != null)
        {
            foreach (var shape in document.customShapes)
            {
                if (shape == null || string.IsNullOrWhiteSpace(shape.name) || string.IsNullOrWhiteSpace(shape.state))
                    throw new FormatException("Invalid custom shape.");
                var snapshot = Deserialize(shape.state, false);
                if (snapshot.Entities.Count < 2)
                    throw new FormatException("A custom shape needs at least two nodes.");
                try
                {
                    storage.AddCustomShape(shape.name, shape.state);
                }
                catch (ArgumentException exception)
                {
                    throw new FormatException("Invalid custom shape: " + exception.Message, exception);
                }
            }
        }
        return storage;
    }

    private static CSGNode Resolve(Dictionary<int, CSGNode> nodes, int id)
    {
        if (id == 0) return null;
        if (!nodes.TryGetValue(id, out var node))
            throw new FormatException("A connection references a missing node.");
        return node;
    }

    private static void ValidateAcyclic(CSGNode node, HashSet<CSGNode> visiting, HashSet<CSGNode> visited)
    {
        if (node == null || visited.Contains(node)) return;
        if (!visiting.Add(node)) throw new FormatException("CSG connections cannot contain cycles.");
        if (node is OperationNode operation)
        {
            ValidateAcyclic(operation.leftChild, visiting, visited);
            ValidateAcyclic(operation.rightChild, visiting, visited);
        }
        visiting.Remove(node);
        visited.Add(node);
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    private static bool Finite(Color value) => Finite(value.r) && Finite(value.g) && Finite(value.b) && Finite(value.a);
}

public static class CSGCustomShapes
{
    public static string Capture(CSGStorage storage, ICollection<CSGNode> selection)
    {
        if (selection == null || selection.Count < 2)
            throw new ArgumentException("Select at least two nodes.", nameof(selection));
        var selected = new HashSet<CSGNode>(selection);
        if (selected.Contains(null) || selected.Count < 2)
            throw new ArgumentException("Select at least two different nodes.", nameof(selection));
        var included = new HashSet<CSGNode>();
        foreach (var node in selected)
            IncludeDependencies(node, included);

        var snapshot = new CSGStorage();
        var copies = new Dictionary<CSGNode, CSGNode>();
        foreach (var entity in storage.Entities)
        {
            if (!included.Contains(entity.node)) continue;
            var copy = CopyNode(entity.node);
            copies.Add(entity.node, copy);
            var added = snapshot.AddWithId(entity.id, copy);
            added.graphPosition = entity.graphPosition;
            added.hasGraphPosition = entity.hasGraphPosition;
        }
        if (copies.Count != included.Count)
            throw new InvalidOperationException("The selected graph contains a missing node.");
        foreach (var pair in copies)
            if (pair.Key is OperationNode original && pair.Value is OperationNode copy)
            {
                copy.leftChild = original.leftChild == null ? null : copies[original.leftChild];
                copy.rightChild = original.rightChild == null ? null : copies[original.rightChild];
            }
        return CSGState.Serialize(snapshot);
    }

    public static List<CSGStorage.Entity> AddCopy(CSGStorage storage, CSGStorage.CustomShape shape)
    {
        var snapshot = CSGState.Deserialize(shape.state);
        var added = new List<CSGStorage.Entity>();
        foreach (var entity in snapshot.Entities)
        {
            var copy = storage.Add(entity.node);
            copy.graphPosition = entity.graphPosition;
            copy.hasGraphPosition = entity.hasGraphPosition;
            added.Add(copy);
        }
        return added;
    }

    private static void IncludeDependencies(CSGNode node, HashSet<CSGNode> included)
    {
        if (node == null || !included.Add(node)) return;
        if (node is OperationNode operation)
        {
            IncludeDependencies(operation.leftChild, included);
            IncludeDependencies(operation.rightChild, included);
        }
    }

    private static CSGNode CopyNode(CSGNode node)
    {
        if (node is SphereNode sphere)
            return new SphereNode { center = sphere.center, color = sphere.color, radius = sphere.radius };
        if (node is BoxNode box)
            return new BoxNode { center = box.center, color = box.color, size = box.size };
        OperationNode result;
        if (node is UnionNode) result = new UnionNode();
        else if (node is IntersectNode) result = new IntersectNode();
        else if (node is DiffNode) result = new DiffNode();
        else throw new InvalidOperationException("Unsupported CSG node type.");
        var original = (OperationNode)node;
        result.colorSource = original.colorSource;
        result.customColor = original.customColor;
        return result;
    }
}

public static class CSGObjExporter
{
    public static string Serialize(IEnumerable<KeyValuePair<string, Mesh>> meshes)
    {
        var output = new StringBuilder("# CSG Studio OBJ\n");
        int offset = 1;
        int normalOffset = 1;
        int count = 0;
        foreach (var item in meshes)
        {
            Mesh mesh = item.Value;
            if (mesh == null || mesh.vertexCount == 0 || mesh.triangles.Length == 0)
                continue;
            output.Append("o ").Append(item.Key.Replace('\n', '_').Replace('\r', '_')).Append('\n');
            foreach (Vector3 vertex in mesh.vertices)
                output.Append("v ").Append(Format(vertex.x)).Append(' ').Append(Format(vertex.y))
                    .Append(' ').Append(Format(vertex.z)).Append('\n');
            Vector3[] normals = mesh.normals;
            bool hasNormals = normals.Length == mesh.vertexCount;
            foreach (Vector3 normal in normals)
                output.Append("vn ").Append(Format(normal.x)).Append(' ').Append(Format(normal.y))
                    .Append(' ').Append(Format(normal.z)).Append('\n');
            int[] triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                output.Append("f");
                for (int j = 0; j < 3; j++)
                {
                    int index = triangles[i + j] + offset;
                    output.Append(' ').Append(index);
                    if (hasNormals) output.Append("//").Append(triangles[i + j] + normalOffset);
                }
                output.Append('\n');
            }
            offset += mesh.vertexCount;
            normalOffset += normals.Length;
            count++;
        }
        if (count == 0) throw new InvalidOperationException("There are no preview meshes to export.");
        return output.ToString();
    }

    private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}
