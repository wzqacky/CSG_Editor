using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;

public class CsgEditorWindow : EditorWindow
{
    private VisualElement m_rightPane;
    private readonly List<string> m_nodes = new List<string> { "Sphere", "Box", "Union" };

    private SphereNode m_sphere = new SphereNode();

    [SerializeField] private GameObject m_preview;
    [SerializeField] private Mesh m_mesh;

    private TreeView m_tree;
    private VisualElement m_propertiesPane;
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

    public void CreateGUI()
    {   
        // Each editor window contains a root VisualElement object
        rootVisualElement.Add(new Label("CSG toolbox"));

        // Create split view
        var splitView = new TwoPaneSplitView(0, 250, TwoPaneSplitViewOrientation.Horizontal);
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

        m_tree = new TreeView { fixedItemHeight = 24 };
        m_tree.style.height = 240;
        m_tree.makeItem = () => new Label();
        m_tree.bindItem = (item, index) =>
        {
            ((Label)item).text = $"Sphere {m_tree.GetIdForIndex(index)}";
        };
        m_tree.SetRootItems(m_treeItems);
        m_rightPane.Add(m_tree);

        m_propertiesPane = new VisualElement();
        m_rightPane.Add(m_propertiesPane);

        dropZone.style.height = 40;
        m_tree.style.height = 40;

        m_rightPane.RegisterCallback<DragUpdatedEvent>(evt =>
        {
            var primitive = DragAndDrop.GetGenericData("CSGPrimitive") as string;
            DragAndDrop.visualMode = primitive == "Sphere"
                ? DragAndDropVisualMode.Copy
                : DragAndDropVisualMode.Rejected;
            evt.StopPropagation();
        }, TrickleDown.TrickleDown);

        m_rightPane.RegisterCallback<DragPerformEvent>(evt =>
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
            ShowSphereProperties(sphere);
            UpdatePreview();
            evt.StopPropagation();
        }, TrickleDown.TrickleDown);

        // Clicking an existing tree entry selects that sphere for editing and preview.
        m_tree.selectedIndicesChanged += indices =>
        {
            foreach (int index in indices)
            {
                m_sphere = m_tree.GetItemDataForIndex<SphereNode>(index);
                ShowSphereProperties(m_sphere);
                UpdatePreview();
                break;
            }
        };
    }

    private void ShowSphereProperties(SphereNode sphere)
    {
        m_propertiesPane.Clear();
        m_propertiesPane.Add(new Label("Sphere properties"));

        var position = new Vector3Field("Position") { value = sphere.center };
        position.RegisterValueChangedCallback(evt =>
        {
            sphere.center = evt.newValue;
            UpdatePreview();
        });
        m_propertiesPane.Add(position);

        var radius = new FloatField("Radius") { value = sphere.radius };
        radius.RegisterValueChangedCallback(evt =>
        {
            sphere.radius = Mathf.Max(0.01f, evt.newValue);
            UpdatePreview();
        });
        m_propertiesPane.Add(radius);
    }
}