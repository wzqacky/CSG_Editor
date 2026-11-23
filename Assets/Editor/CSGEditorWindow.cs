using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;
using UnityEditor.UIElements;
using UnityEditor.Experimental.GraphView;
using System.Linq;

public class CsgEditorWindow : EditorWindow
{
    private GraphView m_graph;
    private readonly Dictionary<CSGNode, Node> m_graphNodes = new Dictionary<CSGNode, Node>();
    private const string DragKey = "CSGNode";

    private sealed class PreviewInstance
    {
        // Important two Unity objects used for display
        public GameObject gameObject;
        public Mesh mesh;
        public Material material;
    }
    // Holds CSG node in the scene: params and relationships
    [SerializeField] private CSGStorage m_storage = new CSGStorage();

    // Maps each node to the GameObject and mesh currently displaying in the scene view
    private readonly Dictionary<CSGNode, PreviewInstance> m_previews = new Dictionary<CSGNode, PreviewInstance>();

    private readonly List<string> m_nodes = new List<string>
    {
        "Sphere", "Box", "Union", "Intersect", "Diff"
    };

    [MenuItem("Tools/CSG Editor")]
    public static void Open()
    {
        GetWindow<CsgEditorWindow>("CSG Editor");
    }

    private void RefreshAllPreviews()
    {
        var consumed = new HashSet<CSGNode>();
        foreach (var entity in m_storage.Entities)
            if (entity.node is OperationNode operation && IsComplete(operation, new HashSet<CSGNode>()))
            {
                consumed.Add(operation.leftChild);
                consumed.Add(operation.rightChild);
            }

        foreach (var entity in m_storage.Entities)
        {
            if (consumed.Contains(entity.node) || !IsComplete(entity.node, new HashSet<CSGNode>()))
            {
                RemovePreview(entity.node);
                continue;
            }

            if (m_previews.TryGetValue(entity.node, out var preview) &&
                preview.gameObject != null && preview.mesh != null)
                UpdatePreview(entity.node);
            else
                CreatePreview(entity.node, entity.id);
        }
    }

    private static bool IsComplete(CSGNode node, HashSet<CSGNode> visiting)
    {
        if (node == null || !visiting.Add(node))
            return false;
        bool complete = !(node is OperationNode operation) ||
            (IsComplete(operation.leftChild, visiting) && IsComplete(operation.rightChild, visiting));
        visiting.Remove(node);
        return complete;
    }

    private void CreatePreview(CSGNode node, int id)
    // Creates a temp GameObject and mesh for a node
    {
        if (node is OperationNode operation &&
            (operation.leftChild == null || operation.rightChild == null))
            return;

        // Handle a stale preview
        if (m_previews.TryGetValue(node, out var existing))
        {   
            // m_previews already has a valid GameObject and mesh
            if (existing.gameObject != null && existing.mesh != null)
                return;
            // Removes only when stored pair is incomplete
            RemovePreview(node);
        }

        var gameObject = ObjectFactory.CreatePrimitive(PrimitiveType.Sphere);
        gameObject.name = $"CSG Preview {id}";
        gameObject.hideFlags = HideFlags.DontSave;
        gameObject.transform.position = Vector3.zero;

        // The generated mesh may differ from the primitive's collider.
        var collider = gameObject.GetComponent<Collider>();
        if (collider != null)
            DestroyImmediate(collider);

        var mesh = new Mesh { name = $"CSG Mesh {id}", hideFlags = HideFlags.DontSave };
        gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
        var meshRenderer = gameObject.GetComponent<MeshRenderer>();
        var previewMaterial = new Material(meshRenderer.sharedMaterial)
        {
            name = $"CSG Preview Material {id}",
            hideFlags = HideFlags.DontSave
        };
        meshRenderer.sharedMaterial = previewMaterial;

        m_previews.Add(node, new PreviewInstance
        {
            gameObject = gameObject,
            mesh = mesh,
            material = previewMaterial
        });

        UpdatePreview(node);
    }

    private void UpdatePreview(CSGNode node)
    // Samples the node's SDF and builds the mesh
    {
        if (!m_previews.TryGetValue(node, out var preview))
            return;

        Bounds bounds = node.GetBounds();
        bounds.Expand(bounds.size * 0.25f);

        float[,,] values;
        try
        {
            values = Sdf.Sample(node.SignedDistance, bounds, 32);
        }
        catch (System.InvalidOperationException exception)
        {
            preview.mesh.Clear();
            Debug.LogError(exception.Message);
            SceneView.RepaintAll();
            return;
        }
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        MarchingCube.Extract(values, bounds, vertices, triangles);

        preview.mesh.Clear();
        preview.mesh.indexFormat = vertices.Count > 65535
            ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        preview.mesh.SetVertices(vertices);
        preview.mesh.SetTriangles(triangles, 0);
        preview.mesh.RecalculateNormals();
        preview.mesh.RecalculateBounds();
        preview.material.color = node.GetColor();
        SceneView.RepaintAll();
    }

    private void RefreshPreviewColors()
    {
        foreach (var preview in m_previews)
            preview.Value.material.color = preview.Key.GetColor();
        SceneView.RepaintAll();
    }

    private void RemovePreview(CSGNode node)
    // Destroys a node's preview GameObject and mesh
    {
        if (!m_previews.TryGetValue(node, out var preview))
            return;

        if (Selection.activeGameObject == preview.gameObject)
            Selection.activeGameObject = null;

        m_previews.Remove(node);
        if (preview.gameObject != null)
            DestroyImmediate(preview.gameObject);
        if (preview.mesh != null)
            DestroyImmediate(preview.mesh);
        if (preview.material != null)
            DestroyImmediate(preview.material);
    }

    private void OnDisable()
    // Cleans up preview when the window is closed/reloaded, an Unity lifecycle method
    {
        foreach (var node in new List<CSGNode>(m_previews.Keys))
            RemovePreview(node);
    }

    private VisualElement CreateNodeCard()
    // Creates the editable UI controls for primitive nodes
    {
        var card = new VisualElement();
        card.style.paddingLeft = 10;
        card.style.paddingRight = 10;
        card.style.marginTop = 4;
        card.style.marginBottom = 4;
        card.style.backgroundColor = new Color(0.17f, 0.20f, 0.26f);

        // Primitve position
        var position = new Vector3Field("Position") { name = "node-position" };
        position.RegisterValueChangedCallback(evt =>
        {
            if (card.userData is PrimitiveNode primitive)
            {
                primitive.center = evt.newValue;
                RefreshAllPreviews();
            }
        });
        card.Add(position);

        // Pritmive radius (for sphere only)
        var radius = new FloatField("Radius") { name = "node-radius" };
        radius.RegisterValueChangedCallback(evt =>
        {
            if (card.userData is SphereNode sphere)
            {
                sphere.radius = Mathf.Max(0.01f, evt.newValue);
                radius.SetValueWithoutNotify(sphere.radius);
                RefreshAllPreviews();
            }
        });
        card.Add(radius);

        // Primitve size (for box only)
        var size = new Vector3Field("Size") { name = "node-size" };
        size.RegisterValueChangedCallback(evt =>
        {
            if (card.userData is BoxNode box)
            {
                box.size = Vector3.Max(evt.newValue, Vector3.one * 0.01f);
                size.SetValueWithoutNotify(box.size);
                RefreshAllPreviews();
            }
        });
        card.Add(size);

        // Primitive color attribute
        var hex = new TextField("Hex")
        {
            name = "node-hex",
            isReadOnly = true
        };
        // Color picker (from Unity Editor UI Control)
        var color = new ColorField("Color")
        {
            name = "node-color",
            showAlpha = false,
            hdr = false
        };

        var colorSource = new EnumField("Color source", OperationColorSource.InputA)
        {
            name = "node-color-source"
        };
        colorSource.RegisterValueChangedCallback(evt =>
        {
            if (card.userData is OperationNode operation)
            {
                operation.colorSource = (OperationColorSource)evt.newValue;
                color.style.display = operation.colorSource == OperationColorSource.Custom
                    ? DisplayStyle.Flex : DisplayStyle.None;
                RefreshPreviewColors();
            }
        });
        card.Add(colorSource);

        color.RegisterValueChangedCallback(evt =>
        {
            if (card.userData is PrimitiveNode primitive)
            {
                primitive.color = evt.newValue;
                hex.SetValueWithoutNotify(
                    "#" + ColorUtility.ToHtmlStringRGB(primitive.color));
            }
            else if (card.userData is OperationNode operation)
                operation.customColor = evt.newValue;
            RefreshPreviewColors();
        });
        card.Add(color);
        card.Add(hex);

        return card;
    }

    private void BindNodeCard(VisualElement card, CSGNode node)
    {
        card.userData = node;

        var position = card.Q<Vector3Field>("node-position");
        position.style.display = node is PrimitiveNode
            ? DisplayStyle.Flex : DisplayStyle.None;
        if (node is PrimitiveNode primitive)
            position.SetValueWithoutNotify(primitive.center);

        var radius = card.Q<FloatField>("node-radius");
        radius.style.display = node is SphereNode
            ? DisplayStyle.Flex : DisplayStyle.None;
        if (node is SphereNode sphere)
            radius.SetValueWithoutNotify(sphere.radius);

        var size = card.Q<Vector3Field>("node-size");
        size.style.display = node is BoxNode
            ? DisplayStyle.Flex : DisplayStyle.None;
        if (node is BoxNode box)
            size.SetValueWithoutNotify(box.size);

        var color = card.Q<ColorField>("node-color");
        var hex = card.Q<TextField>("node-hex");
        var colorSource = card.Q<EnumField>("node-color-source");

        colorSource.style.display = node is OperationNode ? DisplayStyle.Flex : DisplayStyle.None;
        hex.style.display = node is PrimitiveNode ? DisplayStyle.Flex : DisplayStyle.None;
        color.style.display = node is PrimitiveNode ||
            (node is OperationNode colorOperation && colorOperation.colorSource == OperationColorSource.Custom)
            ? DisplayStyle.Flex : DisplayStyle.None;

        if (node is PrimitiveNode colorPrimitive)
        {
            color.SetValueWithoutNotify(colorPrimitive.color);
            hex.SetValueWithoutNotify(
                "#" + ColorUtility.ToHtmlStringRGB(colorPrimitive.color));
        }
        else if (node is OperationNode operation)
        {
            colorSource.SetValueWithoutNotify(operation.colorSource);
            color.SetValueWithoutNotify(operation.customColor);
        }

    }

    private sealed class CsgGraphView : GraphView
    {
        public System.Action<string, Vector2> createNode;
        protected override bool canCopySelection => false;
        protected override bool canCutSelection => false;
        protected override bool canDuplicateSelection => false;

        public CsgGraphView()
        {
            style.flexGrow = 1;
            Insert(0, new GridBackground());
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter adapter)
        {
            return ports.ToList().Where(port => port.node != startPort.node &&
                port.direction != startPort.direction && port.portType == startPort.portType &&
                CanConnect(startPort, port)).ToList();
        }

        private static bool CanConnect(Port first, Port second)
        {
            Port output = first.direction == Direction.Output ? first : second;
            Port input = first.direction == Direction.Input ? first : second;
            var source = ((CSGStorage.Entity)output.node.userData).node;
            var target = ((CSGStorage.Entity)input.node.userData).node;
            return !DependsOn(source, target, new HashSet<CSGNode>());
        }

        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            base.BuildContextualMenu(evt);
            Vector2 position = contentViewContainer.WorldToLocal(evt.mousePosition);
            foreach (string kind in new[] { "Sphere", "Box", "Union", "Intersect", "Diff" })
                evt.menu.AppendAction("Add/" + kind, _ => createNode(kind, position));
        }
    }

    private static CSGNode CreateNode(string kind)
    {
        switch (kind)
        {
            case "Sphere": return new SphereNode();
            case "Box": return new BoxNode();
            case "Union": return new UnionNode();
            case "Intersect": return new IntersectNode();
            case "Diff": return new DiffNode();
            default: return null;
        }
    }

    private void AddNode(string kind, Vector2 position)
    {
        CSGNode node = CreateNode(kind);
        if (node == null)
            return;
        var entity = m_storage.Add(node);
        entity.graphPosition = position;
        entity.hasGraphPosition = true;
        AddGraphNode(entity);
        RefreshAllPreviews();
    }

    private void AddGraphNode(CSGStorage.Entity entity)
    {
        var graphNode = new Node { title = entity.node.GetType().Name + " " + entity.id, userData = entity };
        graphNode.style.width = 270;
        var output = graphNode.InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(CSGNode));
        output.portName = "Shape";
        graphNode.outputContainer.Add(output);

        if (entity.node is OperationNode)
        {
            foreach (string name in new[] { "Left", "Right" })
            {
                var input = graphNode.InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Single, typeof(CSGNode));
                input.portName = name;
                graphNode.inputContainer.Add(input);
            }
        }
        var card = CreateNodeCard();
        BindNodeCard(card, entity.node);
        graphNode.extensionContainer.Add(card);

        graphNode.RefreshExpandedState();
        graphNode.RefreshPorts();
        graphNode.SetPosition(new Rect(entity.graphPosition, new Vector2(270, 200)));
        m_graphNodes.Add(entity.node, graphNode);
        m_graph.AddElement(graphNode);
    }

    private static bool DependsOn(CSGNode node, CSGNode target, HashSet<CSGNode> visited)
    {
        if (ReferenceEquals(node, target))
            return true;
        if (node == null || !visited.Add(node) || !(node is OperationNode operation))
            return false;
        return DependsOn(operation.leftChild, target, visited) ||
            DependsOn(operation.rightChild, target, visited);
    }

    private static void SetInput(Edge edge, CSGNode value)
    {
        var entity = (CSGStorage.Entity)edge.input.node.userData;
        var operation = (OperationNode)entity.node;
        if (edge.input.portName == "Left")
            operation.leftChild = value;
        else
            operation.rightChild = value;
    }

    private GraphViewChange OnGraphChanged(GraphViewChange change)
    {
        bool changed = false;
        if (change.elementsToRemove != null)
        {
            var removedNodes = new HashSet<CSGNode>();
            foreach (var element in change.elementsToRemove)
            {
                if (element is Edge edge && edge.input != null && edge.output != null)
                {
                    var source = ((CSGStorage.Entity)edge.output.node.userData).node;
                    var operation = (OperationNode)((CSGStorage.Entity)edge.input.node.userData).node;
                    CSGNode current = edge.input.portName == "Left" ? operation.leftChild : operation.rightChild;
                    if (ReferenceEquals(source, current))
                        SetInput(edge, null);
                    changed = true;
                }
                else if (element is Node graphNode && graphNode.userData is CSGStorage.Entity entity)
                    removedNodes.Add(entity.node);
            }

            foreach (var entity in m_storage.Entities)
            {
                if (entity.node is OperationNode operation)
                {
                    if (removedNodes.Contains(operation.leftChild)) operation.leftChild = null;
                    if (removedNodes.Contains(operation.rightChild)) operation.rightChild = null;
                }
            }
            foreach (var node in removedNodes)
            {
                RemovePreview(node);
                m_storage.Remove(node);
                m_graphNodes.Remove(node);
                changed = true;
            }
        }

        if (change.edgesToCreate != null)
        {
            foreach (var edge in change.edgesToCreate.ToArray())
            {
                var source = ((CSGStorage.Entity)edge.output.node.userData).node;
                var target = ((CSGStorage.Entity)edge.input.node.userData).node;
                if (DependsOn(source, target, new HashSet<CSGNode>()))
                {
                    change.edgesToCreate.Remove(edge);
                    ShowNotification(new GUIContent("A connection cannot create a cycle."));
                    continue;
                }
                SetInput(edge, source);
                changed = true;
            }
        }

        if (change.movedElements != null)
            foreach (var element in change.movedElements)
                if (element is Node graphNode && graphNode.userData is CSGStorage.Entity entity)
                {
                    entity.graphPosition = graphNode.GetPosition().position;
                    entity.hasGraphPosition = true;
                }
        if (changed)
            RefreshAllPreviews();
        return change;
    }

    private void RestoreEdge(CSGNode source, Node target, string inputName)
    {
        if (source == null || !m_graphNodes.TryGetValue(source, out var sourceNode))
            return;
        var output = sourceNode.outputContainer.Children().OfType<Port>().First();
        var input = target.inputContainer.Children().OfType<Port>().First(port => port.portName == inputName);
        m_graph.AddElement(output.ConnectTo(input));
    }

    public void CreateGUI()
    {
        rootVisualElement.Clear();
        if (m_storage == null)
            m_storage = new CSGStorage();

        var toolbar = new Toolbar();
        toolbar.Add(new Label("CSG Graph"));
        var pixelArtToggle = new Toggle("Pixel-art render")
        {
            value = PixelArtRenderController.Enabled
        };
        pixelArtToggle.RegisterValueChangedCallback(evt =>
        {
            if (!PixelArtRenderController.SetEnabled(evt.newValue))
                pixelArtToggle.SetValueWithoutNotify(false);
        });
        toolbar.Add(pixelArtToggle);
        rootVisualElement.Add(toolbar);
        rootVisualElement.Add(new Label("Drag a node from the toolbox or right-click the canvas. Drag sockets to connect; select and Delete to remove."));

        var splitView = new TwoPaneSplitView(0, 160, TwoPaneSplitViewOrientation.Horizontal);
        splitView.style.flexGrow = 1;
        rootVisualElement.Add(splitView);

        var leftPane = new ListView();
        splitView.Add(leftPane);
        var graph = new CsgGraphView { createNode = AddNode };
        m_graph = graph;
        splitView.Add(graph);

        leftPane.makeItem = () =>
        {
            var label = new Label();
            label.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !(label.userData is string kind))
                    return;
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.SetGenericData(DragKey, kind);
                DragAndDrop.StartDrag(kind);
                evt.StopPropagation();

            });
            return label;
        };

        leftPane.bindItem = (item, index) => 
        {
            var label = item as Label;
            label.text = m_nodes[index];
            label.userData = m_nodes[index];
        };
        leftPane.itemsSource = m_nodes;

        m_graphNodes.Clear();
        int gridIndex = 0;
        foreach (var entity in m_storage.Entities)
        {
            if (!entity.hasGraphPosition)
            {
                entity.graphPosition = new Vector2(40 + (gridIndex % 3) * 310, 40 + (gridIndex / 3) * 280);
                entity.hasGraphPosition = true;
            }
            AddGraphNode(entity);
            gridIndex++;
        }
        foreach (var entity in m_storage.Entities)
            if (entity.node is OperationNode operation)
            {
                RestoreEdge(operation.leftChild, m_graphNodes[entity.node], "Left");
                RestoreEdge(operation.rightChild, m_graphNodes[entity.node], "Right");
            }
        graph.graphViewChanged = OnGraphChanged;
        RefreshAllPreviews();

        graph.RegisterCallback<DragUpdatedEvent>(evt =>
        {
            if (!(DragAndDrop.GetGenericData(DragKey) is string kind) || !m_nodes.Contains(kind))
                return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            evt.StopPropagation();
        }, TrickleDown.TrickleDown);
        graph.RegisterCallback<DragPerformEvent>(evt =>
        {
            if (!(DragAndDrop.GetGenericData(DragKey) is string kind) || !m_nodes.Contains(kind))
                return;
            DragAndDrop.AcceptDrag();
            AddNode(kind, graph.contentViewContainer.WorldToLocal(evt.mousePosition));
            DragAndDrop.SetGenericData(DragKey, null);
            evt.StopPropagation();
        }, TrickleDown.TrickleDown);
    }
}
