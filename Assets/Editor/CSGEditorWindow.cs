using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;

public class CsgEditorWindow : EditorWindow
{
    private VisualElement m_rightPane;
    private readonly List<string> m_nodes = new List<string> { "Sphere", "Box", "Union" };

    // TODO: later on, maybe need a common base for all primitives
    // plus, a List to hold all existing primitives?
    private SphereNode m_sphere = new SphereNode();

    [SerializeField] private GameObject m_preview;
    [SerializeField] private Mesh m_mesh;

    private TreeView m_tree;
    private readonly List<TreeViewItemData<SphereNode>> m_treeItems = 
        new List<TreeViewItemData<SphereNode>>();
    private int m_nextNodeId = 1;

    [MenuItem("Tools/CSG Editor")]
    public static void Open()
    {
        GetWindow<CsgEditorWindow>("CSG Editor");
    }

    private void UpdatePreview()
    {
        if (m_sphere == null) return;

        var bounds = new Bounds(
                m_sphere.center, Vector3.one * (m_sphere.radius * 3f));

        float[,,] vals = Sdf.Sample(
            m_sphere.SignedDistance, bounds, 16);

        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        // Place the vertices in world coordinates
        MarchingCube.Extract(vals, bounds, vertices, triangles);

        if (m_preview == null) 
        {
            m_preview = ObjectFactory.CreatePrimitive(PrimitiveType.Sphere);
            m_preview.name = "CSG Sphere Preview";
        }

        if (m_mesh == null)
        {
            m_mesh = new Mesh { name = "Generated Sphere" };
        }
        m_mesh.Clear();
        m_mesh.SetVertices(vertices);
        m_mesh.SetTriangles(triangles, 0);
        m_mesh.RecalculateNormals();
        m_mesh.RecalculateBounds();

        // MeshFilter holds the mesh and MeshRenderer renders it
        m_preview.GetComponent<MeshFilter>().sharedMesh = m_mesh;

        m_preview.transform.position = Vector3.zero;
        m_preview.transform.rotation = Quaternion.identity;
        m_preview.transform.localScale = Vector3.one;

        // Place to the viewer
        Selection.activeGameObject = m_preview;
    }

    private VisualElement CreatePrimitiveCard()
    {
        var card = new VisualElement();
        card.style.flexGrow = 1;
        card.style.marginTop = 4;
        card.style.marginBottom = 4;
        card.style.marginLeft = 4;
        card.style.marginRight = 6;
        card.style.paddingLeft = 10;
        card.style.justifyContent = Justify.Center;

        // Can think of using different color for primitives v.s. operators
        card.style.backgroundColor = new Color(0.17f, 0.20f, 0.26f);
        card.style.borderLeftWidth = 4;
        card.style.borderLeftColor = new Color(0.35f, 0.70f, 0.95f);

        var title = new Label { name = "node-title" };
        title.style.color = Color.white;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        card.Add(title);

        var type = new Label("Primitive");
        type.style.color = new Color(0.7f, 0.75f, 0.8f);
        type.style.fontSize = 10;
        card.Add(type);

        var position = new Vector3Field("Position") { name = "node-position" };
        position.RegisterValueChangedCallback(evt =>
        {
            var sphere = card.userData as SphereNode;
            if (sphere == null) return;

            sphere.center = evt.newValue;
            m_sphere = sphere;
            UpdatePreview();
        });
        card.Add(position);

        var radius = new FloatField("Radius") { name = "node-radius" };
        radius.RegisterValueChangedCallback(evt =>
        {
            var sphere = card.userData as SphereNode;
            if (sphere == null) return;

            sphere.radius = Mathf.Max(0.01f, evt.newValue);
            radius.SetValueWithoutNotify(sphere.radius);
            m_sphere = sphere;
            UpdatePreview();
        });
        card.Add(radius);

        var deleteButton = new Button(() =>
        {
            var sphere = card.userData as SphereNode;
            if (sphere != null) DeleteSphere(sphere);
        }) { text = "Delete" };
        deleteButton.style.alignSelf = Align.FlexEnd;
        card.Add(deleteButton);

        return card;
    }

    private void BindPrimitiveCard(VisualElement card, int index)
    {
        var sphere = m_tree.GetItemDataForIndex<SphereNode>(index);
        card.userData = sphere;

        // Properties shown here
        card.Q<Label>("node-title").text =
            $"Sphere {index + 1}";
        card.Q<Vector3Field>("node-position")
            .SetValueWithoutNotify(sphere.center);
        card.Q<FloatField>("node-radius")
            .SetValueWithoutNotify(sphere.radius);
    }

    private void DeleteSphere(SphereNode sphere)
    {
        // TODO: Hard-coded delete function, need to change later on
        int index = m_treeItems.FindIndex(item => item.data == sphere);
        if (index < 0) return;

        bool wasPreviewed = m_sphere == sphere;
        m_tree.ClearSelection();
        m_treeItems.RemoveAt(index);
        m_tree.SetRootItems(m_treeItems);
        m_tree.Rebuild();

        if (!wasPreviewed) return;

        if (m_treeItems.Count > 0)
        {
            m_sphere = m_treeItems[Mathf.Min(index, m_treeItems.Count - 1)].data;
            UpdatePreview();
            return;
        }

        m_sphere = null;
        if (m_preview != null)
        {
            if (Selection.activeGameObject == m_preview)
                Selection.activeGameObject = null;
            DestroyImmediate(m_preview);
            m_preview = null;
        }
        if (m_mesh != null)
        {
            DestroyImmediate(m_mesh);
            m_mesh = null;
        }
    }

    private void OnDragUpdate(DragUpdatedEvent evt)
    {
        var primitive = DragAndDrop.GetGenericData("CSGPrimitive") as string;
        DragAndDrop.visualMode = primitive == "Sphere"
            ? DragAndDropVisualMode.Copy
            : DragAndDropVisualMode.Rejected;
        evt.StopPropagation();
    }

    private void OnDragPerform(DragPerformEvent evt)
    {
        if (DragAndDrop.GetGenericData("CSGPrimitive") as string != "Sphere")
            return;

        DragAndDrop.AcceptDrag();

        var sphere = new SphereNode();
        int id = m_nextNodeId++;
        m_treeItems.Add(new TreeViewItemData<SphereNode>(id, sphere));
        m_tree.SetRootItems(m_treeItems);
        m_tree.Rebuild();

        m_sphere = sphere;
        UpdatePreview();
        evt.StopPropagation();
    }

    public void CreateGUI()
    {   
        // Each editor window contains a root VisualElement object
        rootVisualElement.Add(new Label("CSG toolbox"));

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
                if (evt.button != 0 || label.userData as string != "Sphere")
                {
                    return;
                }
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.SetGenericData("CSGPrimitive", "Sphere");
                DragAndDrop.StartDrag("Sphere");
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
        var dropZone = new Label("Drop here");
        m_rightPane.Add(dropZone);
        dropZone.style.height = 40;

        // fixedItemHeight adjust the primitive card's height
        // Increase this when more space needed
        m_tree = new TreeView { fixedItemHeight = 100 };
        // Take up the remaining space in the right pane
        m_tree.style.flexGrow = 1;
        m_tree.makeItem = CreatePrimitiveCard;
        m_tree.bindItem = BindPrimitiveCard;
        m_tree.unbindItem = (item, index) => item.userData = null;
        m_tree.SetRootItems(m_treeItems);
        m_rightPane.Add(m_tree);

        m_rightPane.RegisterCallback<DragUpdatedEvent>(OnDragUpdate, TrickleDown.TrickleDown);

        m_rightPane.RegisterCallback<DragPerformEvent>(OnDragPerform, TrickleDown.TrickleDown);
    }
}
