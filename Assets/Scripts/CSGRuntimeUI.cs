using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UIElements;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

public sealed class CSGRuntimeUI : MonoBehaviour
{
    private const float CanvasWidth = 1800f;
    private const float CanvasHeight = 1200f;
    private const float NodeWidth = 250f;
    private const float MinZoom = 0.4f;
    private const float MaxZoom = 2f;

    private sealed class PreviewInstance
    {
        public GameObject gameObject;
        public Mesh mesh;
        public Material material;
    }

    private sealed class NodeView
    {
        public CSGStorage.Entity entity;
        public VisualElement element;
        public VisualElement output;
        public VisualElement left;
        public VisualElement right;
        public Label leftLabel;
        public Label rightLabel;
        public Button leftDisconnect;
        public Button rightDisconnect;
    }

    private readonly CSGStorage m_storage = new CSGStorage();
    private readonly Dictionary<CSGNode, PreviewInstance> m_previews = new Dictionary<CSGNode, PreviewInstance>();
    private readonly Dictionary<CSGNode, NodeView> m_nodes = new Dictionary<CSGNode, NodeView>();
    private bool m_previewError;
    private PanelSettings m_panelSettings;
    private Material m_previewMaterial;
    private UIDocument m_document;
    private VisualElement m_canvas;
    private VisualElement m_canvasContainer;
    private VisualElement m_lines;
    private VisualElement m_emptyGraph;
    private VisualElement m_graphViewport;
    private ScrollView m_graphScroll;
    private VisualElement m_addMenu;
    private Vector2 m_addPosition;
    private float m_graphZoom = 1f;
    private bool m_panning;
    private Vector2 m_panStart;
    private Vector2 m_panScrollStart;
    private VisualElement m_viewport;
    private VisualElement m_previewMenu;
    private Button m_pixelArtButton;
    private Vector2 m_previewMenuPosition;
    private Label m_status;
    private Camera m_camera;
    private bool m_cameraInitialized;
    private Rect m_originalCameraRect;
    private Vector3 m_originalCameraPosition;
    private Quaternion m_originalCameraRotation;
    private CSGNode m_selected;
    private NodeView m_draggedNode;
    private Vector2 m_dragOffset;
    private CSGNode m_connectionSource;
    private Vector2 m_connectionEnd;
    private int m_activePointer = -1;
    private Vector2 m_lastPointer;
    private Vector3 m_cameraTarget = Vector3.zero;
    private float m_cameraDistance = 10f;
    private float m_yaw;
    private float m_pitch;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<CSGRuntimeUI>() == null)
            new GameObject("CSG Runtime UI").AddComponent<CSGRuntimeUI>();
    }

    private void Awake()
    {
        m_camera = Camera.main;
        if (m_camera == null)
        {
            Debug.LogError("CSG workspace requires a camera tagged MainCamera.", this);
            enabled = false;
            return;
        }

        var theme = Resources.Load<ThemeStyleSheet>("CSGTheme");
        if (theme == null)
        {
            Debug.LogError("CSG workspace could not load its runtime UI theme.", this);
            enabled = false;
            return;
        }
        var stylesheet = Resources.Load<StyleSheet>("CSGWorkspace");
        if (stylesheet == null)
        {
            Debug.LogError("CSG workspace could not load its stylesheet.", this);
            enabled = false;
            return;
        }
        m_previewMaterial = Resources.Load<Material>("CSGPreviewMaterial");
        if (m_previewMaterial == null)
        {
            Debug.LogError("CSG workspace could not load its preview material.", this);
            enabled = false;
            return;
        }

        m_originalCameraRect = m_camera.rect;
        m_originalCameraPosition = m_camera.transform.position;
        m_originalCameraRotation = m_camera.transform.rotation;
        m_cameraInitialized = true;
        Vector3 direction = m_camera.transform.position - m_cameraTarget;
        m_cameraDistance = Mathf.Max(direction.magnitude, 0.1f);
        m_yaw = Mathf.Atan2(direction.x, -direction.z) * Mathf.Rad2Deg;
        m_pitch = Mathf.Asin(Mathf.Clamp(direction.y / m_cameraDistance, -1f, 1f)) * Mathf.Rad2Deg;

        if (FindAnyObjectByType<EventSystem>() == null)
        {
            var eventSystem = new GameObject("CSG Event System");
            eventSystem.transform.SetParent(transform);
            eventSystem.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            eventSystem.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();
#else
            eventSystem.AddComponent<StandaloneInputModule>();
#endif
        }

        m_panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
        m_panelSettings.themeStyleSheet = theme;
        m_panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
        m_document = gameObject.AddComponent<UIDocument>();
        m_document.panelSettings = m_panelSettings;
        m_document.rootVisualElement.styleSheets.Add(stylesheet);
        BuildInterface();
    }

    private void OnDestroy()
    {
        if (PixelArtRenderer.PreviewCamera == m_camera)
            PixelArtRenderer.PreviewCamera = null;
        foreach (var node in new List<CSGNode>(m_previews.Keys))
            RemovePreview(node);
        if (m_cameraInitialized && m_camera != null)
        {
            m_camera.rect = m_originalCameraRect;
            m_camera.transform.SetPositionAndRotation(m_originalCameraPosition, m_originalCameraRotation);
        }
        if (m_panelSettings != null)
            Destroy(m_panelSettings);
    }

    private void BuildInterface()
    {
        var root = m_document.rootVisualElement;
        root.AddToClassList("workspace-root");
        root.RegisterCallback<PointerDownEvent>(DismissMenuOnOutsideClick, TrickleDown.TrickleDown);
        root.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode == KeyCode.Escape &&
                (m_addMenu.style.display == DisplayStyle.Flex || m_previewMenu.style.display == DisplayStyle.Flex))
            {
                CloseAddMenu();
                m_previewMenu.style.display = DisplayStyle.None;
                evt.StopPropagation();
            }
        });

        var header = new VisualElement();
        header.AddToClassList("app-header");
        var brand = new Label("CSG");
        brand.AddToClassList("brand-mark");
        header.Add(brand);
        var headerText = new VisualElement();
        headerText.AddToClassList("header-text");
        headerText.Add(StyledLabel("CSG Studio", "app-title"));
        headerText.Add(StyledLabel("Constructive solid geometry", "app-subtitle"));
        header.Add(headerText);
        root.Add(header);

        var workspace = new VisualElement { name = "workspace" };
        workspace.AddToClassList("workspace");
        root.Add(workspace);

        var sidebar = new VisualElement { name = "sidebar" };
        sidebar.AddToClassList("sidebar");
        workspace.Add(sidebar);

        var graphPanel = new VisualElement { name = "graph-panel" };
        graphPanel.AddToClassList("graph-panel");
        sidebar.Add(graphPanel);
        var graphHeader = new VisualElement();
        graphHeader.AddToClassList("section-header");
        graphHeader.Add(StyledLabel("CSG tree", "section-title"));
        graphHeader.Add(StyledLabel("Right-click to add  /  Scroll to zoom  /  Middle-drag to pan", "section-hint"));
        graphPanel.Add(graphHeader);

        m_graphScroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal) { name = "graph-scroll" };
        m_graphScroll.AddToClassList("graph-scroll");
        graphPanel.Add(m_graphScroll);
        m_graphViewport = m_graphScroll.contentViewport;
        m_graphViewport.RegisterCallback<WheelEvent>(OnGraphWheel, TrickleDown.TrickleDown);
        m_graphViewport.RegisterCallback<PointerDownEvent>(OnCanvasPointerDown);
        m_canvasContainer = new VisualElement { name = "canvas-container" };
        m_canvasContainer.style.width = CanvasWidth;
        m_canvasContainer.style.height = CanvasHeight;
        m_graphScroll.Add(m_canvasContainer);
        m_canvas = new VisualElement();
        m_canvas.name = "canvas";
        m_canvas.style.position = Position.Absolute;
        m_canvas.style.width = CanvasWidth;
        m_canvas.style.height = CanvasHeight;
        m_canvas.style.transformOrigin = new TransformOrigin(0, 0);
        m_canvasContainer.Add(m_canvas);

        m_lines = new VisualElement();
        m_lines.pickingMode = PickingMode.Ignore;
        m_lines.style.position = Position.Absolute;
        m_lines.style.width = CanvasWidth;
        m_lines.style.height = CanvasHeight;
        m_lines.generateVisualContent += DrawConnections;
        m_canvas.Add(m_lines);
        m_emptyGraph = StyledLabel("Right-click the canvas to add a shape or operation.", "graph-empty");
        m_emptyGraph.pickingMode = PickingMode.Ignore;
        m_canvas.Add(m_emptyGraph);

        m_addMenu = new VisualElement { name = "add-menu" };
        m_addMenu.AddToClassList("add-menu");
        m_addMenu.style.display = DisplayStyle.None;
        graphPanel.Add(m_addMenu);
        m_addMenu.Add(StyledLabel("SHAPES", "menu-heading"));
        AddMenuButton("Sphere", () => new SphereNode());
        AddMenuButton("Box", () => new BoxNode());
        m_addMenu.Add(StyledLabel("OPERATIONS", "menu-heading"));
        AddMenuButton("Union", () => new UnionNode());
        AddMenuButton("Intersect", () => new IntersectNode());
        AddMenuButton("Difference", () => new DiffNode());
        m_addMenu.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());

        m_status = new Label("Right-click the canvas to add a node.") { name = "status" };
        m_status.AddToClassList("status");
        sidebar.Add(m_status);

        var previewPanel = new VisualElement { name = "preview-panel" };
        previewPanel.AddToClassList("preview-panel");
        workspace.Add(previewPanel);
        var previewHeader = new VisualElement();
        previewHeader.AddToClassList("section-header");
        previewHeader.AddToClassList("preview-header");
        previewHeader.Add(StyledLabel("3D preview", "section-title"));
        previewHeader.Add(StyledLabel("Drag to orbit  /  Scroll to zoom  /  Right-click for options", "section-hint"));
        previewPanel.Add(previewHeader);
        m_viewport = new VisualElement { name = "viewport" };
        m_viewport.AddToClassList("viewport");
        previewPanel.Add(m_viewport);
        m_viewport.RegisterCallback<GeometryChangedEvent>(_ =>
        {
            UpdateCameraRect();
            ClampPreviewMenu();
        });
        m_viewport.RegisterCallback<PointerDownEvent>(OnViewportDown);
        m_viewport.RegisterCallback<PointerMoveEvent>(OnViewportMove);
        m_viewport.RegisterCallback<PointerUpEvent>(OnViewportUp);
        m_viewport.RegisterCallback<PointerCaptureOutEvent>(evt =>
        {
            if (evt.pointerId == m_activePointer)
                m_activePointer = -1;
        });
        m_viewport.RegisterCallback<WheelEvent>(OnViewportWheel);
        m_previewMenu = new VisualElement { name = "preview-menu" };
        m_previewMenu.AddToClassList("add-menu");
        m_previewMenu.style.display = DisplayStyle.None;
        previewPanel.Add(m_previewMenu);
        m_previewMenu.RegisterCallback<GeometryChangedEvent>(_ => ClampPreviewMenu());
        m_pixelArtButton = new Button(() =>
        {
            PixelArtRenderer.PreviewCamera = PixelArtRenderer.PreviewCamera == m_camera ? null : m_camera;
            m_previewMenu.style.display = DisplayStyle.None;
        }) { name = "pixel-art-toggle" };
        m_pixelArtButton.AddToClassList("menu-item");
        m_previewMenu.Add(m_pixelArtButton);
        m_previewMenu.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
        m_canvas.RegisterCallback<PointerMoveEvent>(OnGraphPointerMove);
        m_canvas.RegisterCallback<PointerUpEvent>(OnGraphPointerUp);
        m_canvas.RegisterCallback<PointerCaptureOutEvent>(OnGraphCaptureOut);
    }

    private static Label StyledLabel(string text, string className)
    {
        var label = new Label(text);
        label.AddToClassList(className);
        return label;
    }

    private void AddMenuButton(string name, Func<CSGNode> factory)
    {
        var button = new Button(() =>
        {
            AddNode(factory(), m_addPosition);
            CloseAddMenu();
        }) { text = name };
        button.AddToClassList("menu-item");
        m_addMenu.Add(button);
    }

    private void OnCanvasPointerDown(PointerDownEvent evt)
    {
        if (m_activePointer != -1)
            return;
        if (evt.button == 2)
        {
            CloseAddMenu();
            m_panning = true;
            m_activePointer = evt.pointerId;
            m_panStart = evt.position;
            m_panScrollStart = m_graphScroll.scrollOffset;
            m_canvas.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }
        else if (evt.button == 1 && (evt.target == m_canvas || evt.target == m_canvasContainer ||
            evt.target == m_graphScroll.contentContainer || evt.target == m_graphViewport))
        {
            OpenAddMenu(evt.position);
            evt.StopPropagation();
        }
    }

    private void OpenAddMenu(Vector2 screenPosition)
    {
        m_addPosition = m_canvas.WorldToLocal(screenPosition);
        Vector2 position = m_addMenu.parent.WorldToLocal(screenPosition);
        Rect bounds = m_addMenu.parent.WorldToLocal(m_graphViewport.worldBound);
        position.x = Mathf.Clamp(position.x, bounds.xMin, Mathf.Max(bounds.xMin, bounds.xMax - 180));
        position.y = Mathf.Clamp(position.y, bounds.yMin, Mathf.Max(bounds.yMin, bounds.yMax - 220));
        m_addMenu.style.left = position.x;
        m_addMenu.style.top = position.y;
        m_addMenu.style.display = DisplayStyle.Flex;
        m_addMenu.BringToFront();
    }

    private void DismissMenuOnOutsideClick(PointerDownEvent evt)
    {
        if (m_addMenu != null && m_addMenu.style.display == DisplayStyle.Flex &&
            !m_addMenu.worldBound.Contains(evt.position))
            CloseAddMenu();
        if (m_previewMenu != null && m_previewMenu.style.display == DisplayStyle.Flex &&
            !m_previewMenu.worldBound.Contains(evt.position))
            m_previewMenu.style.display = DisplayStyle.None;
    }

    private void CloseAddMenu()
    {
        m_addMenu.style.display = DisplayStyle.None;
    }

    private void OnGraphWheel(WheelEvent evt)
    {
        float zoom = Mathf.Clamp(m_graphZoom * Mathf.Exp(-evt.delta.y * 0.05f), MinZoom, MaxZoom);
        if (!Mathf.Approximately(zoom, m_graphZoom))
        {
            CloseAddMenu();
            Vector2 point = m_canvas.WorldToLocal(evt.mousePosition);
            Vector2 cursor = m_graphViewport.WorldToLocal(evt.mousePosition);
            m_graphZoom = zoom;
            m_canvas.style.scale = new Scale(new Vector3(zoom, zoom, 1));
            m_canvasContainer.style.width = CanvasWidth * zoom;
            m_canvasContainer.style.height = CanvasHeight * zoom;
            Vector2 offset = point * zoom - cursor;
            m_graphScroll.schedule.Execute(() => m_graphScroll.scrollOffset = offset);
        }
        evt.StopPropagation();
    }

    private void AddNode(CSGNode node, Vector2? position)
    {
        var entity = m_storage.Add(node);
        int index = m_storage.Entities.Count - 1;
        float width = Mathf.Max(NodeWidth + 40, m_graphViewport.resolvedStyle.width / m_graphZoom);
        int columns = Mathf.Max(1, Mathf.FloorToInt((width - 20) / (NodeWidth + 20)));
        Vector2 visibleOrigin = m_canvas.WorldToLocal(m_graphViewport.worldBound.position);
        Vector2 initial = position ?? (visibleOrigin + new Vector2(20 + (index % columns) * (NodeWidth + 20),
            20 + (index / columns) * 260));
        entity.graphPosition = new Vector2(Mathf.Clamp(initial.x, 8, CanvasWidth - NodeWidth - 8),
            Mathf.Clamp(initial.y, 8, CanvasHeight - 270));
        entity.hasGraphPosition = true;
        m_emptyGraph.style.display = DisplayStyle.None;
        AddNodeView(entity);
        SelectNode(node);
        RefreshAllPreviews();
    }

    private void AddNodeView(CSGStorage.Entity entity)
    {
        var view = new NodeView { entity = entity, element = new VisualElement() };
        var card = view.element;
        card.style.position = Position.Absolute;
        card.style.width = NodeWidth;
        card.style.left = entity.graphPosition.x;
        card.style.top = entity.graphPosition.y;
        card.AddToClassList("node-card");
        card.style.borderLeftColor = entity.node is PrimitiveNode
            ? new Color(0.18f, 0.48f, 0.85f) : new Color(0.81f, 0.47f, 0.16f);

        var header = new VisualElement();
        header.AddToClassList("node-header");
        card.Add(header);
        var title = new Label(NodeName(entity));
        title.style.height = 29;
        title.AddToClassList("node-title");
        header.Add(title);
        title.RegisterCallback<PointerDownEvent>(evt => StartNodeDrag(evt, view));
        var delete = new Button(() => RemoveNode(entity.node)) { text = "x", tooltip = "Delete node" };
        delete.AddToClassList("node-delete");
        header.Add(delete);

        if (entity.node is OperationNode operation)
        {
            card.style.minHeight = 145;
            view.left = AddPort(card, null, true, 75);
            view.right = AddPort(card, null, true, 107);
            AddInputRow(view, operation, true, 70);
            AddInputRow(view, operation, false, 102);
            var form = new VisualElement();
            form.AddToClassList("operation-form");
            card.Add(form);
            AddOperationColorFields(form, operation);
        }
        else if (entity.node is PrimitiveNode primitive)
        {
            var form = new VisualElement();
            form.AddToClassList("node-form");
            card.Add(form);
            AddPrimitiveFields(form, primitive);
        }

        view.output = AddPort(card, "Output  ", false, 44);
        view.output.RegisterCallback<PointerDownEvent>(evt => StartConnection(evt, view));
        card.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.button == 0)
                SelectNode(entity.node);
        });
        m_nodes.Add(entity.node, view);
        m_canvas.Add(card);
        if (entity.node is OperationNode addedOperation)
            UpdateOperationView(view, addedOperation);
        card.RegisterCallback<GeometryChangedEvent>(_ =>
        {
            float maxY = Mathf.Max(8, CanvasHeight - card.resolvedStyle.height - 8);
            if (entity.graphPosition.y > maxY)
            {
                entity.graphPosition.y = maxY;
                card.style.top = maxY;
            }
            m_lines.MarkDirtyRepaint();
        });
        m_lines.MarkDirtyRepaint();
    }

    private static VisualElement AddPort(VisualElement card, string text, bool input, float top)
    {
        var port = new VisualElement();
        port.style.position = Position.Absolute;
        port.style.top = top;
        port.style.width = 18;
        port.style.height = 18;
        if (input)
            port.style.left = -7;
        else
            port.style.right = -7;
        port.style.backgroundColor = input ? new Color(0.81f, 0.47f, 0.16f) : new Color(0.18f, 0.48f, 0.85f);
        port.style.borderTopLeftRadius = 9;
        port.style.borderTopRightRadius = 9;
        port.style.borderBottomLeftRadius = 9;
        port.style.borderBottomRightRadius = 9;
        card.Add(port);

        if (text != null)
        {
            var label = new Label(text);
            label.AddToClassList("node-caption");
            label.pickingMode = PickingMode.Ignore;
            label.style.position = Position.Absolute;
            label.style.top = top;
            label.style.right = 15;
            card.Add(label);
        }
        return port;
    }

    private void StartNodeDrag(PointerDownEvent evt, NodeView view)
    {
        if (evt.button != 0 || m_activePointer != -1)
            return;
        SelectNode(view.entity.node);
        m_draggedNode = view;
        m_dragOffset = m_canvas.WorldToLocal(evt.position) - view.entity.graphPosition;
        m_activePointer = evt.pointerId;
        m_canvas.CapturePointer(evt.pointerId);
        evt.StopPropagation();
    }

    private void StartConnection(PointerDownEvent evt, NodeView view)
    {
        if (evt.button != 0 || m_activePointer != -1)
            return;
        SelectNode(view.entity.node);
        m_connectionSource = view.entity.node;
        m_connectionEnd = m_canvas.WorldToLocal(evt.position);
        m_activePointer = evt.pointerId;
        m_canvas.CapturePointer(evt.pointerId);
        m_lines.MarkDirtyRepaint();
        evt.StopPropagation();
    }

    private void OnGraphPointerMove(PointerMoveEvent evt)
    {
        if (evt.pointerId != m_activePointer)
            return;
        if (m_panning)
        {
            m_graphScroll.scrollOffset = m_panScrollStart - ((Vector2)evt.position - m_panStart);
        }
        else if (m_draggedNode != null)
        {
            Vector2 position = m_canvas.WorldToLocal(evt.position);
            position -= m_dragOffset;
            position.x = Mathf.Clamp(position.x, 8, CanvasWidth - NodeWidth - 8);
            float height = m_draggedNode.element.resolvedStyle.height;
            position.y = Mathf.Clamp(position.y, 8, CanvasHeight - Mathf.Max(145, height) - 8);
            m_draggedNode.entity.graphPosition = position;
            m_draggedNode.element.style.left = position.x;
            m_draggedNode.element.style.top = position.y;
        }
        else if (m_connectionSource != null)
            m_connectionEnd = m_canvas.WorldToLocal(evt.position);
        m_lines.MarkDirtyRepaint();
        evt.StopPropagation();
    }

    private void OnGraphPointerUp(PointerUpEvent evt)
    {
        if (evt.pointerId != m_activePointer)
            return;
        if (m_connectionSource != null)
        {
            bool hit = false;
            foreach (var view in m_nodes.Values)
            {
                if (!(view.entity.node is OperationNode operation))
                    continue;
                bool withinViewport = m_graphViewport.worldBound.Contains(evt.position);
                bool left = withinViewport && view.left.worldBound.Contains(evt.position);
                bool right = withinViewport && view.right.worldBound.Contains(evt.position);
                if (!left && !right)
                    continue;
                hit = true;
                if (DependsOn(m_connectionSource, operation, new HashSet<CSGNode>()))
                    m_status.text = "Cannot connect: this would create a cycle.";
                else
                {
                    if (left)
                        operation.leftChild = m_connectionSource;
                    else
                        operation.rightChild = m_connectionSource;
                    m_status.text = "Connected. Drag another output to replace an input.";
                    RefreshAllPreviews();
                    UpdateOperationView(view, operation);
                }
                break;
            }
            if (!hit)
                m_status.text = "Drop the output on a left or right input socket.";
        }
        m_canvas.ReleasePointer(evt.pointerId);
        ClearGraphDrag();
        evt.StopPropagation();
    }

    private void OnGraphCaptureOut(PointerCaptureOutEvent evt)
    {
        if (evt.pointerId == m_activePointer)
            ClearGraphDrag();
    }

    private void ClearGraphDrag()
    {
        m_activePointer = -1;
        m_draggedNode = null;
        m_connectionSource = null;
        m_panning = false;
        m_lines.MarkDirtyRepaint();
    }

    private void DrawConnections(MeshGenerationContext context)
    {
        var painter = context.painter2D;
        painter.strokeColor = new Color(0.24f, 0.47f, 0.76f);
        painter.lineWidth = 2.5f;
        foreach (var view in m_nodes.Values)
        {
            if (!(view.entity.node is OperationNode operation))
                continue;
            DrawConnection(painter, operation.leftChild, view, true);
            DrawConnection(painter, operation.rightChild, view, false);
        }
        if (m_connectionSource != null && m_nodes.TryGetValue(m_connectionSource, out var source))
            StrokeConnection(painter, PortPosition(source, false, false), m_connectionEnd);
    }

    private void DrawConnection(Painter2D painter, CSGNode source, NodeView target, bool left)
    {
        if (source != null && m_nodes.TryGetValue(source, out var sourceView))
            StrokeConnection(painter, PortPosition(sourceView, false, false), PortPosition(target, true, left));
    }

    private Vector2 PortPosition(NodeView view, bool input, bool left)
    {
        VisualElement port = input ? (left ? view.left : view.right) : view.output;
        return m_canvas.WorldToLocal(port.worldBound.center);
    }

    private static void StrokeConnection(Painter2D painter, Vector2 start, Vector2 end)
    {
        float curve = Mathf.Max(45f, Mathf.Abs(end.x - start.x) * 0.5f);
        painter.BeginPath();
        painter.MoveTo(start);
        painter.BezierCurveTo(start + Vector2.right * curve, end + Vector2.left * curve, end);
        painter.Stroke();
    }

    private static string NodeName(CSGStorage.Entity entity)
    {
        string name = entity.node is DiffNode ? "Difference" : entity.node.GetType().Name.Replace("Node", "");
        return name + " #" + entity.id;
    }

    private void SelectNode(CSGNode node)
    {
        if (m_selected != null && m_nodes.TryGetValue(m_selected, out var previous))
            previous.element.RemoveFromClassList("node-selected");
        m_selected = node;
        if (node != null && m_nodes.TryGetValue(node, out var selected))
        {
            selected.element.AddToClassList("node-selected");
            if (!m_previewError)
                m_status.text = "Editing " + NodeName(selected.entity) + ".";
        }
    }

    private void AddPrimitiveFields(VisualElement form, PrimitiveNode primitive)
    {
        AddVectorFields(form, "Position", primitive.center, false, value => primitive.center = value);
        if (primitive is SphereNode sphere)
        {
            form.Add(StyledLabel("Radius", "field-label"));
            var radius = new FloatField { value = sphere.radius, isDelayed = true };
            radius.AddToClassList("form-field");
            radius.RegisterValueChangedCallback(evt =>
            {
                sphere.radius = Mathf.Max(0.01f, evt.newValue);
                radius.SetValueWithoutNotify(sphere.radius);
                RefreshAllPreviews();
            });
            form.Add(radius);
        }
        else if (primitive is BoxNode box)
            AddVectorFields(form, "Size", box.size, true, value => box.size = value);
        form.Add(StyledLabel("Color", "field-label"));
        AddColorPicker(form, primitive.color, color =>
        {
            primitive.color = color;
            RefreshPreviewColors();
        });
    }

    private void AddOperationColorFields(VisualElement form, OperationNode operation)
    {
        form.Add(StyledLabel("Color source", "field-label"));
        var choices = new List<string> { "Input A (Left)", "Input B (Right)", "Custom" };
        var source = new DropdownField(choices, (int)operation.colorSource);
        source.AddToClassList("form-field");
        form.Add(source);

        var custom = new VisualElement();
        custom.style.display = operation.colorSource == OperationColorSource.Custom
            ? DisplayStyle.Flex : DisplayStyle.None;
        form.Add(custom);
        AddColorPicker(custom, operation.customColor, color =>
        {
            operation.customColor = color;
            RefreshPreviewColors();
        });
        source.RegisterValueChangedCallback(_ =>
        {
            operation.colorSource = (OperationColorSource)source.index;
            custom.style.display = operation.colorSource == OperationColorSource.Custom
                ? DisplayStyle.Flex : DisplayStyle.None;
            RefreshPreviewColors();
        });
    }

    private void AddColorPicker(VisualElement form, Color initialColor, Action<Color> apply)
    {
        Color.RGBToHSV(initialColor, out float hue, out float saturation, out float value);
        Color currentColor = initialColor;
        var picker = new VisualElement { name = "color-picker" };
        picker.AddToClassList("color-picker");
        picker.style.display = DisplayStyle.None;
        var button = new Button(() =>
        {
            bool opening = picker.style.display == DisplayStyle.None;
            picker.style.display = opening ? DisplayStyle.Flex : DisplayStyle.None;
            if (opening)
            {
                var card = form;
                while (card.parent != m_canvas && card.parent != null)
                    card = card.parent;
                card.BringToFront();
            }
        }) { text = "Choose color" };
        button.AddToClassList("color-picker-button");
        var swatch = new VisualElement { name = "color-swatch", pickingMode = PickingMode.Ignore };
        swatch.AddToClassList("color-swatch");
        swatch.style.backgroundColor = initialColor;
        button.Add(swatch);
        form.Add(button);

        var saturationValue = new VisualElement { name = "color-saturation-value" };
        saturationValue.AddToClassList("color-saturation-value");
        var colorMarker = new VisualElement { pickingMode = PickingMode.Ignore };
        colorMarker.AddToClassList("color-picker-marker");
        saturationValue.Add(colorMarker);
        saturationValue.generateVisualContent += context =>
        {
            Rect rect = saturationValue.contentRect;
            DrawColorGradient(context, rect, Color.white, Color.HSVToRGB(hue, 1, 1),
                Color.white, Color.HSVToRGB(hue, 1, 1));
            DrawColorGradient(context, rect, Color.clear, Color.clear, Color.black, Color.black);
        };
        picker.Add(saturationValue);

        var hueBar = new VisualElement { name = "color-hue" };
        hueBar.AddToClassList("color-hue");
        var hueMarker = new VisualElement { pickingMode = PickingMode.Ignore };
        hueMarker.AddToClassList("color-picker-marker");
        hueBar.Add(hueMarker);
        hueBar.generateVisualContent += context =>
        {
            Rect rect = hueBar.contentRect;
            for (int i = 0; i < 6; i++)
            {
                var segment = new Rect(rect.x + rect.width * i / 6f, rect.y, rect.width / 6f, rect.height);
                Color left = Color.HSVToRGB(i / 6f, 1, 1);
                Color right = Color.HSVToRGB((i + 1) / 6f, 1, 1);
                DrawColorGradient(context, segment, left, right, left, right);
            }
        };
        picker.Add(hueBar);
        form.Add(picker);

        void UpdateMarkers()
        {
            colorMarker.style.left = Length.Percent(saturation * 100);
            colorMarker.style.top = Length.Percent((1 - value) * 100);
            hueMarker.style.left = Length.Percent(hue * 100);
            hueMarker.style.top = Length.Percent(50);
            saturationValue.MarkDirtyRepaint();
        }

        void SetFromPointer(VisualElement element, Vector2 position, bool isHue)
        {
            Vector2 point = element.WorldToLocal(position);
            Rect rect = element.contentRect;
            if (rect.width <= 0 || rect.height <= 0)
                return;
            float x = Mathf.Clamp01((point.x - rect.x) / rect.width);
            if (isHue)
                hue = x;
            else
            {
                saturation = x;
                value = 1 - Mathf.Clamp01((point.y - rect.y) / rect.height);
            }
            Color next = Color.HSVToRGB(hue, saturation, value);
            next.a = currentColor.a;
            currentColor = next;
            swatch.style.backgroundColor = next;
            UpdateMarkers();
            apply(next);
        }

        void RegisterPickerInput(VisualElement element, bool isHue)
        {
            element.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0)
                    return;
                element.CapturePointer(evt.pointerId);
                SetFromPointer(element, evt.position, isHue);
                evt.StopPropagation();
            });
            element.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!element.HasPointerCapture(evt.pointerId))
                    return;
                SetFromPointer(element, evt.position, isHue);
                evt.StopPropagation();
            });
            element.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (!element.HasPointerCapture(evt.pointerId))
                    return;
                element.ReleasePointer(evt.pointerId);
                evt.StopPropagation();
            });
        }

        RegisterPickerInput(saturationValue, false);
        RegisterPickerInput(hueBar, true);
        UpdateMarkers();
    }

    private static void DrawColorGradient(MeshGenerationContext context, Rect rect,
        Color topLeft, Color topRight, Color bottomLeft, Color bottomRight)
    {
        MeshWriteData mesh = context.Allocate(4, 6);
        mesh.SetNextVertex(new Vertex { position = new Vector3(rect.xMin, rect.yMin, Vertex.nearZ), tint = topLeft });
        mesh.SetNextVertex(new Vertex { position = new Vector3(rect.xMax, rect.yMin, Vertex.nearZ), tint = topRight });
        mesh.SetNextVertex(new Vertex { position = new Vector3(rect.xMax, rect.yMax, Vertex.nearZ), tint = bottomRight });
        mesh.SetNextVertex(new Vertex { position = new Vector3(rect.xMin, rect.yMax, Vertex.nearZ), tint = bottomLeft });
        mesh.SetNextIndex(0);
        mesh.SetNextIndex(1);
        mesh.SetNextIndex(2);
        mesh.SetNextIndex(2);
        mesh.SetNextIndex(3);
        mesh.SetNextIndex(0);
    }

    private void AddVectorFields(VisualElement form, string label, Vector3 value, bool positive, Action<Vector3> apply)
    {
        form.Add(StyledLabel(label, "field-label"));
        var row = new VisualElement();
        row.AddToClassList("vector-row");
        var x = new FloatField("X") { value = value.x, isDelayed = true };
        var y = new FloatField("Y") { value = value.y, isDelayed = true };
        var z = new FloatField("Z") { value = value.z, isDelayed = true };
        foreach (var field in new[] { x, y, z })
        {
            field.AddToClassList("vector-field");
            row.Add(field);
        }
        Action update = () =>
        {
            var next = new Vector3(x.value, y.value, z.value);
            if (positive)
                next = Vector3.Max(Vector3.one * 0.01f, next);
            x.SetValueWithoutNotify(next.x);
            y.SetValueWithoutNotify(next.y);
            z.SetValueWithoutNotify(next.z);
            apply(next);
            RefreshAllPreviews();
        };
        x.RegisterValueChangedCallback(_ => update());
        y.RegisterValueChangedCallback(_ => update());
        z.RegisterValueChangedCallback(_ => update());
        form.Add(row);
    }

    private void AddInputRow(NodeView view, OperationNode operation, bool left, float top)
    {
        var row = new VisualElement();
        row.AddToClassList("input-row");
        row.style.top = top;
        var description = new Label();
        description.AddToClassList("input-description");
        row.Add(description);
        var disconnect = new Button(() =>
        {
            if (left) operation.leftChild = null;
            else operation.rightChild = null;
            RefreshAllPreviews();
            UpdateOperationView(view, operation);
            m_lines.MarkDirtyRepaint();
        }) { text = "x", tooltip = "Disconnect input" };
        disconnect.AddToClassList("node-disconnect");
        row.Add(disconnect);
        view.element.Add(row);
        if (left)
        {
            view.leftLabel = description;
            view.leftDisconnect = disconnect;
        }
        else
        {
            view.rightLabel = description;
            view.rightDisconnect = disconnect;
        }
    }

    private void UpdateOperationView(NodeView view, OperationNode operation)
    {
        UpdateInput(view.leftLabel, view.leftDisconnect, "Left", operation.leftChild);
        UpdateInput(view.rightLabel, view.rightDisconnect, "Right", operation.rightChild);
    }

    private void UpdateInput(Label label, Button disconnect, string name, CSGNode child)
    {
        label.text = name + ": " + (child == null ? "Not connected" : NodeName(m_nodes[child].entity));
        disconnect.SetEnabled(child != null);
    }

    private void RemoveNode(CSGNode node)
    {
        foreach (var entity in m_storage.Entities)
        {
            if (entity.node is OperationNode operation)
            {
                if (ReferenceEquals(operation.leftChild, node)) operation.leftChild = null;
                if (ReferenceEquals(operation.rightChild, node)) operation.rightChild = null;
                UpdateOperationView(m_nodes[operation], operation);
            }
        }
        RemovePreview(node);
        m_canvas.Remove(m_nodes[node].element);
        m_nodes.Remove(node);
        m_storage.Remove(node);
        m_emptyGraph.style.display = m_storage.Entities.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
        if (ReferenceEquals(m_selected, node))
            m_selected = null;
        RefreshAllPreviews();
        m_lines.MarkDirtyRepaint();
        m_status.text = "Node deleted.";
    }

    private static bool DependsOn(CSGNode node, CSGNode target, HashSet<CSGNode> visited)
    {
        if (ReferenceEquals(node, target))
            return true;
        if (node == null || !visited.Add(node) || !(node is OperationNode operation))
            return false;
        return DependsOn(operation.leftChild, target, visited) || DependsOn(operation.rightChild, target, visited);
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

    private void RefreshAllPreviews()
    {
        bool hadPreviewError = m_previewError;
        m_previewError = false;
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
            if (!m_previews.ContainsKey(entity.node))
                CreatePreview(entity.node, entity.id);
            UpdatePreview(entity.node);
        }
        if (hadPreviewError && !m_previewError)
            m_status.text = "Preview updated.";
    }

    private void RefreshPreviewColors()
    {
        foreach (var preview in m_previews)
            preview.Value.material.color = preview.Key.GetColor();
    }

    private void CreatePreview(CSGNode node, int id)
    {
        var gameObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        gameObject.name = "CSG Preview " + id;
        Destroy(gameObject.GetComponent<Collider>());
        var mesh = new Mesh { name = "CSG Mesh " + id };
        gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = gameObject.GetComponent<MeshRenderer>();
        var material = new Material(m_previewMaterial) { name = "CSG Preview Material " + id };
        renderer.sharedMaterial = material;
        m_previews.Add(node, new PreviewInstance { gameObject = gameObject, mesh = mesh, material = material });
    }

    private void UpdatePreview(CSGNode node)
    {
        var preview = m_previews[node];
        Bounds bounds = node.GetBounds();
        bounds.Expand(bounds.size * 0.25f);
        float[,,] values;
        try
        {
            values = Sdf.Sample(node.SignedDistance, bounds, 64);
        }
        catch (InvalidOperationException exception)
        {
            preview.mesh.Clear();
            m_previewError = true;
            m_status.text = exception.Message;
            Debug.LogError(exception.Message, this);
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
    }

    private void RemovePreview(CSGNode node)
    {
        if (!m_previews.TryGetValue(node, out var preview))
            return;
        m_previews.Remove(node);
        if (preview.gameObject != null) Destroy(preview.gameObject);
        if (preview.mesh != null) Destroy(preview.mesh);
        if (preview.material != null) Destroy(preview.material);
    }

    private void UpdateCameraRect()
    {
        if (m_camera == null || Screen.width == 0 || Screen.height == 0)
            return;
        Rect bounds = m_viewport.worldBound;
        m_camera.rect = new Rect(bounds.x / Screen.width, 1f - bounds.yMax / Screen.height,
            bounds.width / Screen.width, bounds.height / Screen.height);
    }

    private void OnViewportDown(PointerDownEvent evt)
    {
        if (m_activePointer != -1)
            return;
        if (evt.button == 1)
        {
            m_pixelArtButton.text = PixelArtRenderer.PreviewCamera == m_camera
                ? "Disable pixel art" : "Enable pixel art";
            m_previewMenuPosition = m_previewMenu.parent.WorldToLocal(evt.position);
            m_previewMenu.style.left = m_previewMenuPosition.x;
            m_previewMenu.style.top = m_previewMenuPosition.y;
            m_previewMenu.style.display = DisplayStyle.Flex;
            m_previewMenu.BringToFront();
            ClampPreviewMenu();
            evt.StopPropagation();
            return;
        }
        if (evt.button != 0)
            return;
        m_activePointer = evt.pointerId;
        m_lastPointer = evt.position;
        m_viewport.CapturePointer(evt.pointerId);
        evt.StopPropagation();
    }

    private void ClampPreviewMenu()
    {
        if (m_previewMenu == null || m_previewMenu.style.display != DisplayStyle.Flex)
            return;
        Rect bounds = m_previewMenu.parent.WorldToLocal(m_viewport.worldBound);
        float width = m_previewMenu.resolvedStyle.width;
        float height = m_previewMenu.resolvedStyle.height;
        if (float.IsNaN(width) || float.IsNaN(height) || width <= 0 || height <= 0)
            return;
        float x = Mathf.Clamp(m_previewMenuPosition.x, bounds.xMin, Mathf.Max(bounds.xMin, bounds.xMax - width));
        float y = Mathf.Clamp(m_previewMenuPosition.y, bounds.yMin, Mathf.Max(bounds.yMin, bounds.yMax - height));
        if (m_previewMenu.style.left.value.value != x)
            m_previewMenu.style.left = x;
        if (m_previewMenu.style.top.value.value != y)
            m_previewMenu.style.top = y;
    }

    private void OnViewportMove(PointerMoveEvent evt)
    {
        if (evt.pointerId != m_activePointer || !m_viewport.HasPointerCapture(evt.pointerId))
            return;
        Vector2 delta = (Vector2)evt.position - m_lastPointer;
        m_lastPointer = evt.position;
        m_yaw += delta.x * 0.4f;
        m_pitch = Mathf.Clamp(m_pitch - delta.y * 0.4f, -85f, 85f);
        UpdateCameraPosition();
        evt.StopPropagation();
    }

    private void OnViewportUp(PointerUpEvent evt)
    {
        if (evt.pointerId != m_activePointer || !m_viewport.HasPointerCapture(evt.pointerId))
            return;
        m_viewport.ReleasePointer(evt.pointerId);
        m_activePointer = -1;
        evt.StopPropagation();
    }

    private void OnViewportWheel(WheelEvent evt)
    {
        m_cameraDistance = Mathf.Clamp(m_cameraDistance * Mathf.Exp(evt.delta.y * 0.05f), 0.5f, 100f);
        UpdateCameraPosition();
        evt.StopPropagation();
    }

    private void UpdateCameraPosition()
    {
        Quaternion rotation = Quaternion.Euler(m_pitch, m_yaw, 0);
        m_camera.transform.position = m_cameraTarget + rotation * (Vector3.back * m_cameraDistance);
        m_camera.transform.LookAt(m_cameraTarget);
    }
}
