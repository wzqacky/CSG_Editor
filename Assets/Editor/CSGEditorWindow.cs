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

    [MenuItem("Tools/CSG Editor")]
    public static void Open()
    {
        GetWindow<CsgEditorWindow>("CSG Editor");
    }

    public void CreateGUI()
    {
        rootVisualElement.Add(new Label("My CSG tool"));

        var button = new Button(() =>
        {
            var bounds = new Bounds(
                m_sphere.center, Vector3.one * (m_sphere.radius * 3f));

            float[,,] vals = Sdf.Sample(
                m_sphere.SignedDistance, bounds, 16);

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
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

            m_preview.GetComponent<MeshFilter>().sharedMesh = m_mesh;
            m_preview.transform.position = Vector3.zero;
            m_preview.transform.localScale = Vector3.one;

            Selection.activeGameObject = m_preview;
        });
    
        rootVisualElement.Add(button);

        // Create split view
        var splitView = new TwoPaneSplitView(0, 250, TwoPaneSplitViewOrientation.Horizontal);
        rootVisualElement.Add(splitView);

        // Two panels as children
        var leftPane = new ListView();
        splitView.Add(leftPane);
        m_rightPane =  new ScrollView(ScrollViewMode.VerticalAndHorizontal);
        splitView.Add(m_rightPane);

        // Initialize the list view with all sprites' names
        leftPane.makeItem = () => new Label();
        leftPane.bindItem = (item, index) => { (item as Label).text = m_nodes[index]; };
        leftPane.itemsSource = m_nodes;

        // React to selection
        leftPane.selectionChanged += OnPrimitivesSelectionChange;

    }

    private void OnPrimitivesSelectionChange(IEnumerable<object> selectedItems)
    {
         // Clear all previous content from the pane.
        m_rightPane.Clear();

        // Get the selected primitive and display it.
        var enumerator = selectedItems.GetEnumerator();
        if (enumerator.MoveNext())
        {
            var selectedPrimitive = enumerator.Current as string;
            if (selectedPrimitive == "Sphere")
            {
                var radius = new FloatField("Radius") { value = m_sphere.radius };
                radius.RegisterValueChangedCallback(evt => {
                    m_sphere.radius = Mathf.Max(0.01f, evt.newValue);  // Ensure radius is positive
                });
                m_rightPane.Add(radius);
            }
        }
    }
}