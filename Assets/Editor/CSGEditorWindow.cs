using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;

public class CsgEditorWindow : EditorWindow
{
    private VisualElement m_rightPane;

    private sealed class PreviewInstance
    {
        // Important two Unity objects used for display
        public GameObject gameObject;
        public Mesh mesh;
    }
    // Holds CSG node in the scene: params and relationships
    [SerializeField] private CSGStorage m_storage = new CSGStorage();

    // Maps each node to the GameObject and mesh currently displaying in the scene view
    private readonly Dictionary<CSGNode, PreviewInstance> m_previews = new Dictionary<CSGNode, PreviewInstance>();

    private TreeView m_tree;
    private readonly List<TreeViewItemData<CSGNode>> m_treeItems = new List<TreeViewItemData<CSGNode>>();

    private readonly List<string> m_nodes = new List<string>
    {
        "Sphere", "Box", "Union", "Intersect", "Diff"
    };

    [MenuItem("Tools/CSG Editor")]
    public static void Open()
    {
        GetWindow<CsgEditorWindow>("CSG Editor");
    }

    private void RefreshTree()
    // Rebuilds the node list from the nodes in storage
    {
        m_treeItems.Clear();

        foreach (var entity in m_storage.Entities)
        {
            m_treeItems.Add(new TreeViewItemData<CSGNode>(entity.id, entity.node));
        }

        m_tree.SetRootItems(m_treeItems);
        m_tree.Rebuild();
    }

    private void RefreshAllPreviews()
    // Regenerates preview after a node change
    {
        foreach (var entity in m_storage.Entities)
            UpdatePreview(entity.node);
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

        m_previews.Add(node, new PreviewInstance
        {
            gameObject = gameObject,
            mesh = mesh
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

        float[,,] values = Sdf.Sample(node.SignedDistance, bounds, 16);
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        MarchingCube.Extract(values, bounds, vertices, triangles);

        preview.mesh.Clear();
        preview.mesh.SetVertices(vertices);
        preview.mesh.SetTriangles(triangles, 0);
        preview.mesh.RecalculateNormals();
        preview.mesh.RecalculateBounds();
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
    }

    private void OnDisable()
    // Cleans up preview when the window is closed/reloaded, an Unity lifecycle method
    {
        foreach (var node in new List<CSGNode>(m_previews.Keys))
            RemovePreview(node);
    }

    private VisualElement CreateNodeCard()
    // Creates the editable UI controls for tree elements
    {
        var card = new VisualElement();
        card.style.paddingLeft = 10;
        card.style.paddingRight = 10;
        card.style.marginTop = 4;
        card.style.marginBottom = 4;
        card.style.backgroundColor = new Color(0.17f, 0.20f, 0.26f);

        card.Add(new Label { name = "node-title" });

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

        card.Add(new Label { name = "node-left" });
        card.Add(new Label { name = "node-right" });

        card.Add(new Button(() =>
        {
            if (card.userData is CSGNode node)
                DeleteNode(node);
        })
        { text = "Delete" });

        return card;
    }

    private void BindNodeCard(VisualElement card, int index)
    // Fills a tree row with the selected node's values and fields
    {
        var node = m_tree.GetItemDataForIndex<CSGNode>(index);
        card.userData = node;

        card.Q<Label>("node-title").text =
            $"{node.GetType().Name} {m_tree.GetIdForIndex(index)}";

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

        var left = card.Q<Label>("node-left");
        var right = card.Q<Label>("node-right");
        left.style.display = right.style.display = node is OperationNode
            ? DisplayStyle.Flex : DisplayStyle.None;

        if (node is OperationNode operation)
        {
            left.text = $"Left: {operation.leftChild?.GetType().Name ?? "None"}";
            right.text = $"Right: {operation.rightChild?.GetType().Name ?? "None"}";
        }
    }

    private void AddNode(CSGNode node)
    // Adds a node to storage, creates its preview, refreshes the tree
    {
        var entity = m_storage.Add(node);

        CreatePreview(node, entity.id);
        RefreshTree();
    }

    private bool IsReferenced(CSGNode node)
    // Checks whether an operation directly uses this node as a child
    {
        foreach (var entity in m_storage.Entities)
        {
            if (entity.node is OperationNode operation &&
                (ReferenceEquals(operation.leftChild, node) ||
                ReferenceEquals(operation.rightChild, node)))
                return true;
        }
        return false;
    }

    private void DeleteNode(CSGNode node)
    // Removes an unused node from storage, the tree, and preview
    {
        // Some nodes depending on this, so cannot directly remove
        if (IsReferenced(node))
        {
            Debug.LogWarning("Delete the operation using this node first.");
            return;
        }

        if (!m_storage.Remove(node))
            return;

        m_tree.ClearSelection();
        RemovePreview(node);
        RefreshTree();
    }

    private CSGNode CreateDraggedNode(string kind)
    // Creates a primitive or an operation using two selected nodes
    {
        if (kind == "Sphere") return new SphereNode();
        if (kind == "Box") return new BoxNode();
        if (kind != "Union" && kind != "Intersect" && kind != "Diff")
            return null;

        // Operations need exactly two selected rows.
        var indices = new List<int>(m_tree.selectedIndices);
        if (indices.Count != 2)
            return null;

        indices.Sort(); // Upper row = left child.
        var left = m_tree.GetItemDataForIndex<CSGNode>(indices[0]);
        var right = m_tree.GetItemDataForIndex<CSGNode>(indices[1]);

        // TODO: CSG operations still do not function well for now,
        // e.g. need to add pos/offset to the shape
        OperationNode operation;
        switch (kind)
        {
            case "Union": operation = new UnionNode(); break;
            case "Intersect": operation = new IntersectNode(); break;
            case "Diff": operation = new DiffNode(); break;
            default: return null;
        }

        operation.leftChild = left;
        operation.rightChild = right;
        return operation;
    }

    private void OnDragUpdate(DragUpdatedEvent evt)
    // Shows whether the current drag can be dropped in the right pane
    {
        var kind = DragAndDrop.GetGenericData("CSGNode") as string;
        bool primitive = kind == "Sphere" || kind == "Box";
        bool operation = kind == "Union" || kind == "Intersect" || kind == "Diff";
        bool twoSelected = new List<int>(m_tree.selectedIndices).Count == 2;

        DragAndDrop.visualMode = primitive || (operation && twoSelected)
            ? DragAndDropVisualMode.Copy
            : DragAndDropVisualMode.Rejected;

        evt.StopPropagation();
    }

    private void OnDragPerform(DragPerformEvent evt)
    // Accepts a valid drop and adds the resulting node
    {
        var kind = DragAndDrop.GetGenericData("CSGNode") as string;
        var node = CreateDraggedNode(kind);
        if (node == null)
            return;

        DragAndDrop.AcceptDrag();
        AddNode(node);
        evt.StopPropagation();
    }

    public void CreateGUI()
    {
        rootVisualElement.Clear();
        if (m_storage == null)
            m_storage = new CSGStorage();

        // Each editor window contains a root VisualElement object
        rootVisualElement.Add(new Label("CSG toolbox"));

        // Toggle for pixel-art render
        var pixelArtToggle = new Toggle("Pixel-art render")
        {
            value = PixelArtRenderController.Enabled
        };
        pixelArtToggle.RegisterValueChangedCallback(evt =>
        {
            if (!PixelArtRenderController.SetEnabled(evt.newValue))
                pixelArtToggle.SetValueWithoutNotify(false);
        });
        rootVisualElement.Add(pixelArtToggle);

        // Create split view
        var splitView = new TwoPaneSplitView(0, 250, TwoPaneSplitViewOrientation.Horizontal);
        splitView.style.flexGrow = 1;
        rootVisualElement.Add(splitView);

        // Two panels as children
        var leftPane = new ListView();
        splitView.Add(leftPane);
        m_rightPane =  new VisualElement();
        splitView.Add(m_rightPane);

        // Start an Editor drag
        leftPane.makeItem = () => 
        {
            var label = new Label();
            label.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !(label.userData is string kind))
                    return;
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.SetGenericData("CSGNode", kind);
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

        // A dedicated drop zone
        var dropZone = new Label("Drop a shape here. To use CSG operations, select two rows first (upper = left).");
        m_rightPane.Add(dropZone);
        dropZone.style.height = 40;

        // fixedItemHeight adjust the primitive card's height
        // Increase this when more space needed
        m_tree = new TreeView
            {
                fixedItemHeight = 140,
                selectionType = SelectionType.Multiple
            };
    
        // Take up the remaining space in the right pane
        m_tree.style.flexGrow = 1;
        m_tree.makeItem = CreateNodeCard;
        m_tree.bindItem = BindNodeCard;
        m_tree.unbindItem = (item, index) => item.userData = null;
        m_rightPane.Add(m_tree);
        RefreshTree();
        foreach (var entity in m_storage.Entities)
            CreatePreview(entity.node, entity.id);

        // Runs while you drag over the pane
        m_rightPane.RegisterCallback<DragUpdatedEvent>(OnDragUpdate, TrickleDown.TrickleDown);
        // Runs when release the drag
        m_rightPane.RegisterCallback<DragPerformEvent>(OnDragPerform, TrickleDown.TrickleDown);
    }
}
