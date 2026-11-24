using System;
using System.Collections.Generic;
using System.IO;
using SimpleFileBrowser;
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
    private const float CustomShapeCardHeight = 145f;
    private const float ExpandedShapeToolbarHeight = 29f;
    private const float MinZoom = 0.4f;
    private const float MaxZoom = 2f;
    private const float BoxSelectionThreshold = 4f;

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
        public VisualElement card;
        public VisualElement output;
        public VisualElement expandedToolbar;
        public VisualElement expandedOutput;
        public Action rebuildForm;
        public Action refreshPosition;
        public VisualElement left;
        public VisualElement right;
        public Label leftLabel;
        public Label rightLabel;
        public Button leftDisconnect;
        public Button rightDisconnect;
        public NodeView shapeOwner;
        public bool expanded;
        public bool IsVisible => element.style.display != DisplayStyle.None;
        public bool IsCardVisible => IsVisible && card.style.display != DisplayStyle.None;
    }

    private CSGStorage m_storage = new CSGStorage();
    private CSGHistory m_history;
    private bool m_restoring;
    private Button m_undoButton;
    private Button m_redoButton;
    private Button m_removeButton;
    private Button m_groupButton;
    private string m_statePath;
    private string m_objPath;
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
    private VisualElement m_selectionBox;
    private ScrollView m_graphScroll;
    private VisualElement m_addMenu;
    private VisualElement m_customMenu;
    private VisualElement m_dialog;
    private Label m_dialogTitle;
    private Label m_dialogMessage;
    private Label m_dialogError;
    private TextField m_dialogName;
    private Button m_dialogConfirm;
    private Action m_dialogAction;
    private Vector2 m_addPosition;
    private Vector2 m_addMenuPosition;
    private float m_graphZoom = 1f;
    private bool m_panning;
    private Vector2 m_panStart;
    private Vector2 m_panScrollStart;
    private VisualElement m_viewport;
    private VisualElement m_previewMenu;
    private Button m_pixelArtButton;
    private Button m_texelSplatButton;
    private TexelSplatProbe m_texelSplatProbe;
    private bool m_texelSplatCaptureRequested;
    private Vector2 m_previewMenuPosition;
    private Label m_status;
    private Camera m_camera;
    private bool m_cameraInitialized;
    private Rect m_originalCameraRect;
    private Vector3 m_originalCameraPosition;
    private Quaternion m_originalCameraRotation;
    private readonly HashSet<CSGNode> m_selected = new HashSet<CSGNode>();
    private bool m_boxSelecting;
    private bool m_addToSelection;
    private Vector2 m_boxSelectionStart;
    private NodeView m_draggedNode;
    private Vector2 m_dragOffset;
    private Vector2 m_dragMinimum;
    private Vector2 m_dragMaximum;
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
        m_statePath = Path.Combine(Application.persistentDataPath, "csg-state.json");
        m_objPath = Path.Combine(Application.persistentDataPath, "csg-object.obj");
        BuildInterface();
        m_history = new CSGHistory(CSGState.Serialize(m_storage));
        UpdateHistoryButtons();
    }

    private void LateUpdate()
    {
        if (!m_texelSplatCaptureRequested || m_texelSplatProbe == null)
            return;

        m_texelSplatCaptureRequested = false;
        if (!m_texelSplatProbe.CaptureAt(m_texelSplatProbe.Origin))
        {
            ReleaseTexelSplatProbe();
            m_status.text = "Texel splat could not capture the 3D preview.";
            return;
        }

        if (PixelArtRenderer.PreviewCamera == m_camera)
            PixelArtRenderer.PreviewCamera = null;
        TexelSplatRendererFeature.PreviewProbe = m_texelSplatProbe;
    }

    private void OnDisable()
    {
        ReleaseTexelSplatProbe();
    }

    private void OnDestroy()
    {
        if (FileBrowser.IsOpen)
            FileBrowser.HideDialog();
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
        root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

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
        var actions = new VisualElement();
        actions.AddToClassList("header-actions");
        actions.Add(new Button(NewDocument) { text = "New" });
        actions.Add(new Button(ShowSaveStateDialog) { text = "Save" });
        actions.Add(new Button(ShowImportStateDialog) { text = "Import" });
        actions.Add(new Button(ShowExportObjDialog) { text = "Export OBJ" });
        header.Add(actions);
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
        graphHeader.Add(StyledLabel("Drag empty space to select  /  Shift to add  /  Right-click for actions  /  Scroll to zoom", "section-hint"));
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
        m_canvas.focusable = true;
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
        m_selectionBox = new VisualElement { name = "selection-box", pickingMode = PickingMode.Ignore };
        m_selectionBox.AddToClassList("selection-box");
        m_selectionBox.style.display = DisplayStyle.None;
        m_graphViewport.Add(m_selectionBox);

        m_addMenu = new ScrollView(ScrollViewMode.Vertical) { name = "add-menu" };
        m_addMenu.AddToClassList("add-menu");
        m_addMenu.style.display = DisplayStyle.None;
        graphPanel.Add(m_addMenu);
        m_addMenu.RegisterCallback<GeometryChangedEvent>(_ => ClampAddMenu());
        m_addMenu.Add(StyledLabel("EDIT", "menu-heading"));
        bool mac = Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor;
        string shortcut = mac ? "Cmd+" : "Ctrl+";
        m_undoButton = new Button(() => { CloseAddMenu(); m_canvas.Focus(); Undo(); }) { text = "Undo    " + shortcut + "Z" };
        m_redoButton = new Button(() => { CloseAddMenu(); m_canvas.Focus(); Redo(); }) { text = "Redo    " + shortcut + "Y" };
        m_undoButton.AddToClassList("menu-item");
        m_redoButton.AddToClassList("menu-item");
        m_addMenu.Add(m_undoButton);
        m_addMenu.Add(m_redoButton);
        m_removeButton = new Button(() =>
        {
            CloseAddMenu();
            m_canvas.Focus();
            RemoveNodes(new HashSet<CSGNode>(m_selected));
        }) { text = "Remove selected" };
        m_removeButton.AddToClassList("menu-item");
        m_removeButton.SetEnabled(false);
        m_addMenu.Add(m_removeButton);
        m_groupButton = new Button(ShowCreateShapeDialog) { text = "Create custom shape..." };
        m_groupButton.AddToClassList("menu-item");
        m_groupButton.SetEnabled(false);
        m_addMenu.Add(m_groupButton);
        m_addMenu.Add(StyledLabel("SHAPES", "menu-heading"));
        AddMenuButton("Sphere", () => new SphereNode());
        AddMenuButton("Box", () => new BoxNode());
        m_addMenu.Add(StyledLabel("OPERATIONS", "menu-heading"));
        AddMenuButton("Union", () => new UnionNode());
        AddMenuButton("Intersect", () => new IntersectNode());
        AddMenuButton("Difference", () => new DiffNode());
        m_customMenu = new VisualElement();
        m_addMenu.Add(m_customMenu);
        RefreshCustomMenu();
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
            bool enablePixelArt = PixelArtRenderer.PreviewCamera != m_camera;
            if (enablePixelArt)
                ReleaseTexelSplatProbe();
            PixelArtRenderer.PreviewCamera = enablePixelArt ? m_camera : null;
            m_previewMenu.style.display = DisplayStyle.None;
        }) { name = "pixel-art-toggle" };
        m_pixelArtButton.AddToClassList("menu-item");
        m_previewMenu.Add(m_pixelArtButton);
        m_texelSplatButton = new Button(ToggleTexelSplat) { name = "texel-splat-toggle" };
        m_texelSplatButton.AddToClassList("menu-item");
        m_previewMenu.Add(m_texelSplatButton);
        m_previewMenu.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
        m_canvas.RegisterCallback<PointerMoveEvent>(OnGraphPointerMove);
        m_canvas.RegisterCallback<PointerUpEvent>(OnGraphPointerUp);
        m_canvas.RegisterCallback<PointerCancelEvent>(evt =>
        {
            if (m_boxSelecting && evt.pointerId == m_activePointer)
            {
                ClearGraphDrag();
                m_canvas.ReleasePointer(evt.pointerId);
            }
        });
        m_canvas.RegisterCallback<PointerCaptureOutEvent>(OnGraphCaptureOut);
        BuildDialog(root);
    }

    private static Label StyledLabel(string text, string className)
    {
        var label = new Label(text);
        label.AddToClassList(className);
        return label;
    }

    private void BuildDialog(VisualElement root)
    {
        m_dialog = new VisualElement { name = "confirmation-dialog" };
        m_dialog.AddToClassList("dialog-overlay");
        m_dialog.style.display = DisplayStyle.None;
        var panel = new VisualElement();
        panel.AddToClassList("dialog-panel");
        m_dialog.Add(panel);
        m_dialogTitle = StyledLabel("", "dialog-title");
        panel.Add(m_dialogTitle);
        m_dialogMessage = StyledLabel("", "dialog-message");
        panel.Add(m_dialogMessage);
        m_dialogName = new TextField("Name") { maxLength = 60 };
        panel.Add(m_dialogName);
        m_dialogError = StyledLabel("", "dialog-error");
        panel.Add(m_dialogError);
        var buttons = new VisualElement();
        buttons.AddToClassList("dialog-actions");
        buttons.Add(new Button(CloseDialog) { text = "Cancel" });
        m_dialogConfirm = new Button(() => m_dialogAction?.Invoke());
        buttons.Add(m_dialogConfirm);
        panel.Add(buttons);
        m_dialog.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                CloseDialog();
                evt.StopPropagation();
            }
        });
        root.Add(m_dialog);
    }

    private void ShowDialog(string title, string message, string confirm, bool showName, Action action)
    {
        CloseAddMenu();
        m_previewMenu.style.display = DisplayStyle.None;
        m_dialogTitle.text = title;
        m_dialogMessage.text = message;
        m_dialogError.text = "";
        m_dialogName.style.display = showName ? DisplayStyle.Flex : DisplayStyle.None;
        m_dialogConfirm.text = confirm;
        m_dialogAction = action;
        m_dialog.style.display = DisplayStyle.Flex;
        m_dialog.BringToFront();
        if (showName) m_dialogName.Focus();
        else m_dialogConfirm.Focus();
    }

    private void CloseDialog()
    {
        m_dialog.style.display = DisplayStyle.None;
        m_dialogAction = null;
        m_canvas.Focus();
    }

    private void NewDocument()
    {
        if (FileBrowser.IsOpen) return;
        if (m_storage.Entities.Count == 0 && m_storage.CustomShapes.Count == 0)
        {
            ResetDocument();
            return;
        }
        ShowDialog("New canvas?", "This discards the current graph and custom shapes. This cannot be undone.",
            "New", false, () => { CloseDialog(); ResetDocument(); });
    }

    private void ResetDocument()
    {
        RestoreState(new CSGStorage());
        m_history = new CSGHistory(CSGState.Serialize(m_storage));
        UpdateHistoryButtons();
        m_graphZoom = 1f;
        m_canvas.style.scale = new Scale(Vector3.one);
        m_canvasContainer.style.width = CanvasWidth;
        m_canvasContainer.style.height = CanvasHeight;
        m_graphScroll.scrollOffset = Vector2.zero;
        m_statePath = Path.Combine(Application.persistentDataPath, "csg-state.json");
        m_objPath = Path.Combine(Application.persistentDataPath, "csg-object.obj");
        CloseAddMenu();
        m_previewMenu.style.display = DisplayStyle.None;
        m_status.text = "New canvas.";
    }

    private void ShowCreateShapeDialog()
    {
        if (m_selected.Count < 2)
        {
            m_status.text = "Select at least two nodes to create a custom shape.";
            return;
        }
        int number = m_storage.CustomShapes.Count + 1;
        string name;
        do
        {
            name = "Custom shape " + number++;
        }
        while (HasCustomShapeName(name));
        m_dialogName.value = name;
        ShowDialog("Create custom shape", "Save a snapshot of the selected nodes and their inputs.",
            "Create", true, CreateCustomShape);
    }

    private bool HasCustomShapeName(string name)
    {
        foreach (var shape in m_storage.CustomShapes)
            if (string.Equals(shape.name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void CreateCustomShape()
    {
        try
        {
            string snapshot = CSGCustomShapes.Capture(m_storage, m_selected);
            m_storage.AddCustomShape(m_dialogName.value, snapshot);
        }
        catch (ArgumentException exception)
        {
            m_dialogError.text = exception.Message;
            return;
        }
        RefreshCustomMenu();
        RecordHistory();
        CloseDialog();
        m_status.text = "Custom shape saved to the add menu.";
    }

    private void RefreshCustomMenu()
    {
        m_customMenu.Clear();
        if (m_storage.CustomShapes.Count == 0)
            return;
        m_customMenu.Add(StyledLabel("CUSTOM SHAPES", "menu-heading"));
        foreach (var shape in m_storage.CustomShapes)
        {
            var row = new VisualElement();
            row.AddToClassList("custom-shape-row");
            var add = new Button(() => AddCustomShape(shape)) { text = shape.name, tooltip = "Add " + shape.name };
            add.AddToClassList("menu-item");
            add.AddToClassList("custom-shape-add");
            row.Add(add);
            var remove = new Button(() =>
            {
                m_storage.RemoveCustomShape(shape);
                RefreshCustomMenu();
                RecordHistory();
                m_status.text = "Custom shape removed.";
            }) { text = "x", tooltip = "Remove custom shape" };
            remove.AddToClassList("node-delete");
            row.Add(remove);
            m_customMenu.Add(row);
        }
    }

    private void AddCustomShape(CSGStorage.CustomShape shape)
    {
        var instance = CSGCustomShapes.AddInstance(m_storage, shape, out var added);
        float maxX = CanvasWidth - NodeWidth - 8;
        instance.graphPosition = new Vector2(Mathf.Clamp(m_addPosition.x, 8, maxX),
            Mathf.Clamp(m_addPosition.y, 8, CanvasHeight - CustomShapeCardHeight - 8));
        instance.hasGraphPosition = true;

        Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (var entity in added)
        {
            min = Vector2.Min(min, entity.graphPosition);
            max = Vector2.Max(max, entity.graphPosition);
        }
        Vector2 origin = instance.graphPosition + Vector2.up * (ExpandedShapeToolbarHeight + 16);
        float maxY = CanvasHeight - 145 - 8;
        float above = instance.graphPosition.y - (max.y - min.y) - 145 - 16;
        if (origin.y + max.y - min.y > maxY && above >= 8)
            origin.y = above;
        Vector2 offset = origin - min;
        if (8 - min.x <= maxX - max.x)
            offset.x = Mathf.Clamp(offset.x, 8 - min.x, maxX - max.x);
        if (8 - min.y <= maxY - max.y)
            offset.y = Mathf.Clamp(offset.y, 8 - min.y, maxY - max.y);
        foreach (var entity in added)
        {
            Vector2 position = entity.graphPosition + offset;
            entity.graphPosition = new Vector2(Mathf.Clamp(position.x, 8, maxX), Mathf.Clamp(position.y, 8, maxY));
            entity.hasGraphPosition = true;
            AddNodeView(entity);
        }
        AddNodeView(instance);
        UpdateShapeOwners();
        foreach (var entity in added)
            if (entity.node is OperationNode operation)
                UpdateOperationView(m_nodes[entity.node], operation);
        m_emptyGraph.style.display = DisplayStyle.None;
        SelectNode(instance.node);
        CloseAddMenu();
        m_canvas.Focus();
        RefreshAllPreviews();
        RecordHistory();
        if (!m_previewError) m_status.text = "Added " + shape.name + ".";
    }

    private void OnKeyDown(KeyDownEvent evt)
    {
        if (FileBrowser.IsOpen || m_activePointer != -1 || m_dialog.style.display == DisplayStyle.Flex)
            return;
        if ((evt.actionKey || evt.ctrlKey) && !evt.altKey &&
            (evt.keyCode == KeyCode.Z || evt.keyCode == KeyCode.Y))
        {
            if (evt.keyCode == KeyCode.Y || evt.shiftKey) Redo();
            else Undo();
            evt.StopPropagation();
            evt.PreventDefault();
        }
        else if (evt.keyCode == KeyCode.Escape &&
            (m_addMenu.style.display == DisplayStyle.Flex || m_previewMenu.style.display == DisplayStyle.Flex))
        {
            CloseAddMenu();
            m_previewMenu.style.display = DisplayStyle.None;
            evt.StopPropagation();
        }
    }

    private void RecordHistory()
    {
        if (m_restoring) return;
        m_history.Record(CSGState.Serialize(m_storage));
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        m_undoButton.SetEnabled(m_history.CanUndo);
        m_redoButton.SetEnabled(m_history.CanRedo);
    }

    private void Undo()
    {
        if (!m_history.CanUndo) return;
        RestoreState(CSGState.Deserialize(m_history.Undo()));
        UpdateHistoryButtons();
        if (!m_previewError) m_status.text = "Undid graph edit.";
    }

    private void Redo()
    {
        if (!m_history.CanRedo) return;
        RestoreState(CSGState.Deserialize(m_history.Redo()));
        UpdateHistoryButtons();
        if (!m_previewError) m_status.text = "Redid graph edit.";
    }

    private void RestoreState(CSGStorage storage)
    {
        m_restoring = true;
        try
        {
            foreach (var node in new List<CSGNode>(m_previews.Keys))
                RemovePreview(node);
            foreach (var view in m_nodes.Values)
                m_canvas.Remove(view.element);
            m_nodes.Clear();
            m_selected.Clear();
            m_storage = storage;
            foreach (var entity in m_storage.Entities)
                AddNodeView(entity);
            UpdateShapeOwners();
            foreach (var entity in m_storage.Entities)
                if (entity.node is OperationNode operation)
                    UpdateOperationView(m_nodes[entity.node], operation);
            m_emptyGraph.style.display = m_storage.Entities.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateSelection();
            RefreshCustomMenu();
            m_lines.MarkDirtyRepaint();
            RefreshAllPreviews();
        }
        finally
        {
            m_restoring = false;
        }
    }

    private void ShowSaveStateDialog()
    {
        ShowFileDialog(true, m_statePath, "Save CSG state", "CSG State", ".json", SaveState);
    }

    private void ShowImportStateDialog()
    {
        ShowFileDialog(false, m_statePath, "Import CSG state", "CSG State", ".json", ImportState);
    }

    private void ShowExportObjDialog()
    {
        if (m_previewError)
        {
            ReportFileError("OBJ export failed", new InvalidOperationException("Fix the preview error before exporting."));
            return;
        }
        ShowFileDialog(true, m_objPath, "Export OBJ", "OBJ", ".obj", ExportObj);
    }

    private void ShowFileDialog(bool save, string path, string title, string filterName,
        string extension, Action<string> onSelected)
    {
#if UNITY_EDITOR || UNITY_STANDALONE
        if (FileBrowser.IsOpen)
            return;
        FileBrowser.SetFilters(false, new FileBrowser.Filter(filterName, extension));
        FileBrowser.SetDefaultFilter(extension);
        FileBrowser.OnSuccess success = paths =>
        {
            m_canvas.Focus();
            if (paths != null && paths.Length == 1 && !string.IsNullOrEmpty(paths[0]))
                onSelected(paths[0]);
            else
                ReportFileError("File selection failed", new InvalidOperationException("No file was selected."));
        };
        FileBrowser.OnCancel cancel = () => m_canvas.Focus();
        if (save)
            FileBrowser.ShowSaveDialog(success, cancel, FileBrowser.PickMode.Files, false,
                Path.GetDirectoryName(path), Path.GetFileName(path), title, "Save");
        else
            FileBrowser.ShowLoadDialog(success, cancel, FileBrowser.PickMode.Files, false,
                Path.GetDirectoryName(path), null, title, "Open");
#else
        m_status.text = "File dialogs are supported on Windows, macOS, and Linux.";
        Debug.LogError(m_status.text, this);
#endif
    }

    private void SaveState(string path)
    {
        try
        {
            File.WriteAllText(path, CSGState.Serialize(m_storage));
            m_statePath = Path.GetFullPath(path);
            m_status.text = "Saved state to " + m_statePath;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
            exception is ArgumentException || exception is NotSupportedException)
        {
            ReportFileError("Save failed", exception);
        }
    }

    private void ImportState(string path)
    {
        try
        {
            CSGStorage storage = CSGState.Deserialize(File.ReadAllText(path));
            RestoreState(storage);
            RecordHistory();
            m_statePath = Path.GetFullPath(path);
            if (!m_previewError) m_status.text = "Imported state from " + m_statePath;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
            exception is ArgumentException || exception is NotSupportedException || exception is FormatException)
        {
            ReportFileError("Import failed", exception);
        }
    }

    private void ExportObj(string path)
    {
        try
        {
            if (m_previewError)
                throw new InvalidOperationException("Fix the preview error before exporting.");
            var meshes = new List<KeyValuePair<string, Mesh>>();
            foreach (var entity in m_storage.Entities)
                if (m_previews.TryGetValue(entity.node, out var preview))
                    meshes.Add(new KeyValuePair<string, Mesh>("CSG_" + entity.id, preview.mesh));
            string obj = CSGObjExporter.Serialize(meshes);
            File.WriteAllText(path, obj);
            m_objPath = Path.GetFullPath(path);
            m_status.text = "Exported OBJ to " + m_objPath;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
            exception is ArgumentException || exception is NotSupportedException || exception is InvalidOperationException)
        {
            ReportFileError("OBJ export failed", exception);
        }
    }

    private void ReportFileError(string message, Exception exception)
    {
        m_status.text = message + ": " + exception.Message;
        Debug.LogError(m_status.text, this);
    }

    private void AddMenuButton(string name, Func<CSGNode> factory)
    {
        var button = new Button(() =>
        {
            m_canvas.Focus();
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
        if (evt.button == 0 && (evt.target == m_canvas || evt.target == m_canvasContainer ||
            evt.target == m_graphScroll.contentContainer || evt.target == m_graphViewport))
        {
            m_canvas.Focus();
            if (evt.pointerId != PointerId.mousePointerId)
            {
                ClearSelection();
                return;
            }
            m_boxSelecting = true;
            m_addToSelection = evt.shiftKey;
            m_boxSelectionStart = evt.position;
            m_activePointer = evt.pointerId;
            m_canvas.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }
        else if (evt.button == 2)
        {
            CloseAddMenu();
            m_panning = true;
            m_activePointer = evt.pointerId;
            m_panStart = evt.position;
            m_panScrollStart = m_graphScroll.scrollOffset;
            m_canvas.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }
        else if (evt.button == 1)
        {
            if (evt.target is VisualElement target &&
                (target.GetFirstAncestorOfType<TextField>() != null ||
                 target.GetFirstAncestorOfType<FloatField>() != null))
                return;
            m_canvas.Focus();
            OpenAddMenu(evt.position);
            evt.StopPropagation();
        }
    }

    private void OpenAddMenu(Vector2 screenPosition)
    {
        m_addPosition = m_canvas.WorldToLocal(screenPosition);
        m_addMenuPosition = m_addMenu.parent.WorldToLocal(screenPosition);
        m_addMenu.style.left = m_addMenuPosition.x;
        m_addMenu.style.top = m_addMenuPosition.y;
        m_addMenu.style.maxHeight = Mathf.Max(0, m_graphViewport.worldBound.height);
        m_addMenu.style.display = DisplayStyle.Flex;
        m_addMenu.BringToFront();
        ClampAddMenu();
    }

    private void ClampAddMenu()
    {
        if (m_addMenu == null || m_addMenu.style.display != DisplayStyle.Flex)
            return;
        Rect bounds = m_addMenu.parent.WorldToLocal(m_graphViewport.worldBound);
        float width = m_addMenu.resolvedStyle.width;
        float height = m_addMenu.resolvedStyle.height;
        if (float.IsNaN(width) || float.IsNaN(height) || width <= 0 || height <= 0)
            return;
        m_addMenu.style.left = Mathf.Clamp(m_addMenuPosition.x, bounds.xMin, Mathf.Max(bounds.xMin, bounds.xMax - width));
        m_addMenu.style.top = Mathf.Clamp(m_addMenuPosition.y, bounds.yMin, Mathf.Max(bounds.yMin, bounds.yMax - height));
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
        evt.StopPropagation();
        if (m_boxSelecting)
            return;
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
        RecordHistory();
    }

    private void AddNodeView(CSGStorage.Entity entity)
    {
        var element = new VisualElement();
        element.style.position = Position.Absolute;
        element.style.width = NodeWidth;
        element.style.left = entity.graphPosition.x;
        element.style.top = entity.graphPosition.y;
        var card = element;
        if (entity.node is CustomShapeNode)
        {
            card = new VisualElement { name = "custom-shape-card" };
            element.Add(card);
        }
        var view = new NodeView { entity = entity, element = element, card = card };
        card.AddToClassList("node-card");
        card.style.borderLeftColor = entity.node is OperationNode
            ? new Color(0.81f, 0.47f, 0.16f) : new Color(0.18f, 0.48f, 0.85f);
        card.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.button != 0 || !evt.shiftKey || m_activePointer != -1)
                return;
            SelectNode(entity.node, true);
            m_canvas.Focus();
            evt.StopPropagation();
            evt.PreventDefault();
        }, TrickleDown.TrickleDown);

        var header = new VisualElement();
        header.AddToClassList("node-header");
        card.Add(header);
        var title = new Label(NodeName(entity));
        title.style.height = 29;
        title.AddToClassList("node-title");
        header.Add(title);
        title.RegisterCallback<PointerDownEvent>(evt => StartNodeDrag(evt, view));
        if (entity.node is CustomShapeNode)
        {
            card.style.minHeight = CustomShapeCardHeight;
            title.AddToClassList("custom-shape-title");
            var expand = new Button(() => SetShapeExpanded(view, true)) { text = "Expand", name = "custom-shape-toggle" };
            expand.AddToClassList("custom-shape-toggle");
            header.Add(expand);
        }
        var delete = new Button(() => RemoveNodes(new HashSet<CSGNode> { entity.node }))
            { text = "x", tooltip = "Delete node" };
        delete.AddToClassList("node-delete");
        header.Add(delete);

        if (entity.node is OperationNode operation)
        {
            card.style.minHeight = 145;
            view.left = AddPort(card, null, true, 75);
            view.right = AddPort(card, null, true, 107);
            AddInputRow(view, operation, true, 70);
            AddInputRow(view, operation, false, 102);
            AddNodeForm(view, "operation-form", form => AddOperationFields(form, operation, view));
        }
        else if (entity.node is PrimitiveNode primitive)
        {
            AddNodeForm(view, "node-form", form => AddPrimitiveFields(form, primitive));
        }
        else if (entity.node is CustomShapeNode shape)
        {
            AddNodeForm(view, "node-form", form => AddCustomShapeFields(form, shape));
            AddExpandedShapeToolbar(view);
        }

        view.output = AddPort(card, "Output  ", false, 44);
        view.output.RegisterCallback<PointerDownEvent>(evt => StartConnection(evt, view));
        card.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.button == 0)
                SelectNode(entity.node);
        });
        m_nodes.Add(entity.node, view);
        m_canvas.Add(element);
        if (entity.node is OperationNode addedOperation &&
            addedOperation.leftChild == null && addedOperation.rightChild == null)
            UpdateOperationView(view, addedOperation);
        element.RegisterCallback<GeometryChangedEvent>(_ =>
        {
            if (!view.IsVisible)
                return;
            float maxY = Mathf.Max(8, CanvasHeight - element.resolvedStyle.height - 8);
            if (entity.graphPosition.y > maxY)
            {
                entity.graphPosition.y = maxY;
                element.style.top = maxY;
            }
            m_lines.MarkDirtyRepaint();
        });
        m_lines.MarkDirtyRepaint();
    }

    private static void AddNodeForm(NodeView view, string className, Action<VisualElement> build)
    {
        var form = new VisualElement();
        form.AddToClassList(className);
        view.card.Add(form);
        view.rebuildForm = () =>
        {
            form.Clear();
            build(form);
        };
        view.rebuildForm();
    }

    private void AddExpandedShapeToolbar(NodeView view)
    {
        var toolbar = new VisualElement { name = "custom-shape-toolbar" };
        toolbar.AddToClassList("custom-shape-toolbar");
        toolbar.style.height = ExpandedShapeToolbarHeight;
        toolbar.style.display = DisplayStyle.None;
        var collapse = new Button(() => SetShapeExpanded(view, false)) { text = "Collapse", name = "custom-shape-collapse" };
        collapse.AddToClassList("custom-shape-toggle");
        toolbar.Add(collapse);
        toolbar.Add(StyledLabel(NodeName(view.entity), "custom-shape-title"));
        view.expandedOutput = AddPort(toolbar, null, false, 5);
        view.expandedOutput.tooltip = "Custom shape output";
        view.expandedOutput.RegisterCallback<PointerDownEvent>(evt => StartConnection(evt, view));
        view.expandedToolbar = toolbar;
        view.element.Add(toolbar);
    }

    private void SetShapeExpanded(NodeView view, bool expanded)
    {
        m_canvas.Focus();
        view.expanded = expanded;
        SelectNode(expanded ? null : view.entity.node);
        UpdateNodeVisibility();
    }

    private void UpdateShapeOwners()
    {
        foreach (var view in m_nodes.Values)
            if (view.entity.node is CustomShapeNode shape)
                foreach (var member in shape.members)
                    m_nodes[member].shapeOwner = view;
        UpdateNodeVisibility();
    }

    private void UpdateNodeVisibility()
    {
        foreach (var view in m_nodes.Values)
        {
            bool visible = true;
            for (var owner = view.shapeOwner; owner != null && visible; owner = owner.shapeOwner)
                visible = owner.expanded;
            bool showCard = visible && !view.expanded;
            if (showCard && !view.IsCardVisible)
                view.rebuildForm?.Invoke();
            view.element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (view.expandedToolbar != null)
            {
                view.card.style.display = showCard ? DisplayStyle.Flex : DisplayStyle.None;
                view.expandedToolbar.style.display = visible && view.expanded ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }
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
        m_canvas.Focus();
        m_draggedNode = view;
        m_dragOffset = m_canvas.WorldToLocal(evt.position) - view.entity.graphPosition;
        var minDelta = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        var maxDelta = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        GetMoveLimits(view, ref minDelta, ref maxDelta);
        m_dragMinimum = view.entity.graphPosition + Vector2.Min(minDelta, Vector2.zero);
        m_dragMaximum = view.entity.graphPosition + Vector2.Max(maxDelta, Vector2.zero);
        m_activePointer = evt.pointerId;
        m_canvas.CapturePointer(evt.pointerId);
        evt.StopPropagation();
    }

    private void GetMoveLimits(NodeView view, ref Vector2 minDelta, ref Vector2 maxDelta)
    {
        Vector2 position = view.entity.graphPosition;
        float minHeight = view.entity.node is CustomShapeNode ? CustomShapeCardHeight : 145f;
        float layoutHeight = view.element.resolvedStyle.height;
        float height = float.IsNaN(layoutHeight) ? minHeight : Mathf.Max(minHeight, layoutHeight);
        minDelta = Vector2.Max(minDelta, new Vector2(8, 8) - position);
        maxDelta = Vector2.Min(maxDelta, new Vector2(CanvasWidth - NodeWidth - 8, CanvasHeight - height - 8) - position);
        if (view.entity.node is CustomShapeNode shape)
            foreach (var member in shape.members)
                GetMoveLimits(m_nodes[member], ref minDelta, ref maxDelta);
    }

    private void StartConnection(PointerDownEvent evt, NodeView view)
    {
        if (evt.button != 0 || m_activePointer != -1)
            return;
        if (!view.expanded)
            SelectNode(view.entity.node);
        m_canvas.Focus();
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
        evt.StopPropagation();
        if (m_boxSelecting)
        {
            UpdateBoxSelection(evt.position, false);
            return;
        }
        if (m_panning)
        {
            m_graphScroll.scrollOffset = m_panScrollStart - ((Vector2)evt.position - m_panStart);
        }
        else if (m_draggedNode != null)
        {
            Vector2 position = m_canvas.WorldToLocal(evt.position) - m_dragOffset;
            position = Vector2.Max(m_dragMinimum, Vector2.Min(position, m_dragMaximum));
            MoveNode(m_draggedNode, position);
        }
        else if (m_connectionSource != null)
            m_connectionEnd = m_canvas.WorldToLocal(evt.position);
        m_lines.MarkDirtyRepaint();
    }

    private void MoveNode(NodeView view, Vector2 position)
    {
        Vector2 delta = position - view.entity.graphPosition;
        view.entity.graphPosition = position;
        view.element.style.left = position.x;
        view.element.style.top = position.y;
        if (view.entity.node is CustomShapeNode shape)
            foreach (var member in shape.members)
            {
                var memberView = m_nodes[member];
                MoveNode(memberView, memberView.entity.graphPosition + delta);
            }
    }

    private void OnGraphPointerUp(PointerUpEvent evt)
    {
        if (evt.pointerId != m_activePointer)
            return;
        if (m_boxSelecting)
            UpdateBoxSelection(evt.position, true);
        else if (m_connectionSource != null)
        {
            bool hit = false;
            var sourceView = m_nodes[m_connectionSource];
            foreach (var view in m_nodes.Values)
            {
                if (!view.IsVisible || !(view.entity.node is OperationNode operation))
                    continue;
                bool withinViewport = m_graphViewport.worldBound.Contains(evt.position);
                bool left = withinViewport && view.left.worldBound.Contains(evt.position);
                bool right = withinViewport && view.right.worldBound.Contains(evt.position);
                if (!left && !right)
                    continue;
                hit = true;
                if (sourceView.shapeOwner != view.shapeOwner)
                    m_status.text = "Connect nodes within the same custom shape, or use the shape's output.";
                else if (DependsOn(m_connectionSource, operation, new HashSet<CSGNode>()))
                    m_status.text = "Cannot connect: this would create a cycle.";
                else
                {
                    operation.SetInput(left, m_connectionSource);
                    m_status.text = "Connected. Drag another output to replace an input.";
                    RefreshAllPreviews();
                    UpdateOperationView(view, operation);
                    RecordHistory();
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
        if (m_boxSelecting)
        {
            m_boxSelecting = false;
            m_selectionBox.style.display = DisplayStyle.None;
            foreach (var entry in m_nodes)
                entry.Value.card.EnableInClassList("node-selected", m_selected.Contains(entry.Key));
        }
        if (m_draggedNode != null)
            RecordHistory();
        m_activePointer = -1;
        m_draggedNode = null;
        m_connectionSource = null;
        m_panning = false;
        m_lines.MarkDirtyRepaint();
    }

    private void UpdateBoxSelection(Vector2 position, bool commit)
    {
        bool isDrag = (position - m_boxSelectionStart).sqrMagnitude >= BoxSelectionThreshold * BoxSelectionThreshold;
        Rect viewport = m_graphViewport.worldBound;
        Vector2 end = Vector2.Max(viewport.min, Vector2.Min(position, viewport.max));
        Vector2 min = Vector2.Min(m_boxSelectionStart, end);
        Vector2 max = Vector2.Max(m_boxSelectionStart, end);
        Rect bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);

        m_selectionBox.style.display = isDrag && !commit ? DisplayStyle.Flex : DisplayStyle.None;
        if (isDrag && !commit)
        {
            Rect localBounds = m_graphViewport.WorldToLocal(bounds);
            m_selectionBox.style.left = localBounds.x;
            m_selectionBox.style.top = localBounds.y;
            m_selectionBox.style.width = localBounds.width;
            m_selectionBox.style.height = localBounds.height;
        }

        foreach (var entry in m_nodes)
        {
            if (!entry.Value.IsCardVisible)
                continue;
            bool selected = (m_addToSelection && m_selected.Contains(entry.Key)) ||
                (isDrag && bounds.Overlaps(entry.Value.element.worldBound));
            entry.Value.card.EnableInClassList("node-selected", selected);
            if (!commit)
                continue;
            if (selected)
                m_selected.Add(entry.Key);
            else
                m_selected.Remove(entry.Key);
        }
        if (commit)
            UpdateSelection();
    }

    private void DrawConnections(MeshGenerationContext context)
    {
        var painter = context.painter2D;
        painter.strokeColor = new Color(0.24f, 0.47f, 0.76f);
        painter.lineWidth = 2.5f;
        foreach (var view in m_nodes.Values)
        {
            if (!view.IsVisible || !(view.entity.node is OperationNode operation))
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
        VisualElement port = input ? (left ? view.left : view.right) : (view.expanded ? view.expandedOutput : view.output);
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
        if (entity.node is CustomShapeNode shape)
            return shape.name + " #" + entity.id;
        string name = entity.node is DiffNode ? "Difference" : entity.node.GetType().Name.Replace("Node", "");
        return name + " #" + entity.id;
    }

    private void ClearSelection()
    {
        foreach (var node in m_selected)
            if (m_nodes.TryGetValue(node, out var view))
                view.card.RemoveFromClassList("node-selected");
        m_selected.Clear();
        UpdateSelection();
    }

    private void SelectNode(CSGNode node, bool additive = false)
    {
        if (!additive)
            ClearSelection();
        if (node != null && m_nodes.TryGetValue(node, out var selected))
        {
            if (additive && m_selected.Contains(node))
            {
                m_selected.Remove(node);
                selected.card.RemoveFromClassList("node-selected");
            }
            else
            {
                m_selected.Add(node);
                selected.card.AddToClassList("node-selected");
            }
        }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        m_removeButton.SetEnabled(m_selected.Count > 0);
        m_groupButton.SetEnabled(m_selected.Count >= 2);
        if (m_previewError)
            return;
        if (m_selected.Count > 1)
            m_status.text = m_selected.Count + " nodes selected. Right-click for actions.";
        else if (m_selected.Count == 1)
            foreach (var node in m_selected)
                m_status.text = "Editing " + NodeName(m_nodes[node].entity) + ".";
        else
            m_status.text = "Right-click the canvas to add a node.";
    }

    private void AddPrimitiveFields(VisualElement form, PrimitiveNode primitive)
    {
        AddVectorFields(form, "Position", primitive.center, false, value => primitive.center = value);
        AddVectorFields(form, "Rotation (°)", primitive.rotation, false, value => primitive.rotation = value)
            .name = "rotation-fields";
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
                RecordHistory();
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

    private void AddCustomShapeFields(VisualElement form, CustomShapeNode shape)
    {
        AddVectorFields(form, "Position", shape.Position, false, shape.MoveTo);
        AddVectorFields(form, "Rotation (°)", shape.Rotation, false, shape.RotateTo).name = "rotation-fields";
        form.Add(StyledLabel("Color", "field-label"));
        AddColorPicker(form, shape.GetColor(), color =>
        {
            shape.SetColor(color);
            RefreshPreviewColors();
        });
        form.SetEnabled(shape.IsComplete);
    }

    private void AddOperationFields(VisualElement form, OperationNode operation, NodeView view)
    {
        var position = AddVectorFields(form, "Position", operation.Position, false, operation.MoveTo, out var setPosition);
        position.name = "position-fields";
        view.refreshPosition = () =>
        {
            position.SetEnabled(operation.IsComplete);
            setPosition(operation.Position);
        };
        view.refreshPosition();

        AddVectorFields(form, "Size (×)", operation.scale, true, value => operation.scale = value).name = "size-fields";
        AddVectorFields(form, "Rotation (°)", operation.rotation, false, value => operation.rotation = value)
            .name = "rotation-fields";

        form.Add(StyledLabel("Color source", "field-label"));
        var choices = new List<string> { "Input A (Left)", "Input B (Right)", "Custom" };
        var source = new DropdownField(choices, (int)operation.colorSource);
        source.AddToClassList("form-field");
        form.Add(source);

        form.Add(StyledLabel("Custom color", "field-label"));
        AddColorPicker(form, operation.customColor, color =>
        {
            operation.customColor = color;
            operation.colorSource = OperationColorSource.Custom;
            source.SetValueWithoutNotify(choices[(int)OperationColorSource.Custom]);
            RefreshPreviewColors();
        });
        source.RegisterValueChangedCallback(_ =>
        {
            operation.colorSource = (OperationColorSource)source.index;
            RefreshPreviewColors();
            RecordHistory();
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
            element.RegisterCallback<PointerCaptureOutEvent>(_ => RecordHistory());
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

    private VisualElement AddVectorFields(VisualElement form, string label, Vector3 value, bool positive, Action<Vector3> apply)
    {
        return AddVectorFields(form, label, value, positive, apply, out _);
    }

    private VisualElement AddVectorFields(VisualElement form, string label, Vector3 value, bool positive, Action<Vector3> apply,
        out Action<Vector3> setValue)
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
        void SetFields(Vector3 next)
        {
            x.SetValueWithoutNotify(next.x);
            y.SetValueWithoutNotify(next.y);
            z.SetValueWithoutNotify(next.z);
        }

        void UpdateFields()
        {
            var next = new Vector3(x.value, y.value, z.value);
            if (positive)
                next = Vector3.Max(Vector3.one * 0.01f, next);
            SetFields(next);
            apply(next);
            RefreshAllPreviews();
            RecordHistory();
        }
        x.RegisterValueChangedCallback(_ => UpdateFields());
        y.RegisterValueChangedCallback(_ => UpdateFields());
        z.RegisterValueChangedCallback(_ => UpdateFields());
        form.Add(row);
        setValue = SetFields;
        return row;
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
            operation.SetInput(left, null);
            RefreshAllPreviews();
            UpdateOperationView(view, operation);
            m_lines.MarkDirtyRepaint();
            RecordHistory();
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

    private void RemoveNodes(HashSet<CSGNode> nodes)
    {
        int selectedCount = nodes.Count;
        if (selectedCount == 0)
            return;

        foreach (var node in new List<CSGNode>(nodes))
            IncludeShapeMembers(node, nodes);
        foreach (var entity in m_storage.Entities)
        {
            if (entity.node is CustomShapeNode shape && !nodes.Contains(shape))
                shape.members.RemoveAll(nodes.Contains);
            if (entity.node is OperationNode operation && !nodes.Contains(operation))
            {
                if (nodes.Contains(operation.leftChild)) operation.SetInput(true, null);
                if (nodes.Contains(operation.rightChild)) operation.SetInput(false, null);
                UpdateOperationView(m_nodes[operation], operation);
            }
        }
        foreach (var node in nodes)
        {
            RemovePreview(node);
            m_canvas.Remove(m_nodes[node].element);
            m_nodes.Remove(node);
            m_storage.Remove(node);
            m_selected.Remove(node);
        }
        m_emptyGraph.style.display = m_storage.Entities.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
        UpdateSelection();
        RefreshAllPreviews();
        m_lines.MarkDirtyRepaint();
        if (!m_previewError)
            m_status.text = selectedCount == 1 ? "Node removed." : selectedCount + " nodes removed.";
        RecordHistory();
    }

    private static void IncludeShapeMembers(CSGNode node, HashSet<CSGNode> nodes)
    {
        if (node is CustomShapeNode shape)
            foreach (var member in shape.members)
                if (nodes.Add(member))
                    IncludeShapeMembers(member, nodes);
    }

    private static bool DependsOn(CSGNode node, CSGNode target, HashSet<CSGNode> visited)
    {
        if (ReferenceEquals(node, target))
            return true;
        if (node == null || !visited.Add(node) || !(node is OperationNode operation))
            return false;
        return DependsOn(operation.leftChild, target, visited) || DependsOn(operation.rightChild, target, visited);
    }

    private static void ConsumeOutput(CSGNode node, HashSet<CSGNode> consumed)
    {
        if (consumed.Add(node) && node is CustomShapeNode shape)
            foreach (var member in shape.members)
                ConsumeOutput(member, consumed);
    }

    private void RefreshAllPreviews()
    {
        bool hadPreviewError = m_previewError;
        m_previewError = false;
        m_storage.RefreshCustomShapeOutputs();
        foreach (var view in m_nodes.Values)
            view.refreshPosition?.Invoke();
        var consumed = new HashSet<CSGNode>();
        foreach (var entity in m_storage.Entities)
            if (entity.node is OperationNode operation && operation.IsComplete)
            {
                ConsumeOutput(operation.leftChild, consumed);
                ConsumeOutput(operation.rightChild, consumed);
            }

        foreach (var entity in m_storage.Entities)
        {
            // An unconnected custom shape keeps the previews of its individual outputs.
            if (entity.node is CustomShapeNode || consumed.Contains(entity.node) || !entity.node.IsComplete)
            {
                RemovePreview(entity.node);
                continue;
            }
            if (!m_previews.ContainsKey(entity.node))
                CreatePreview(entity.node, entity.id);
            UpdatePreview(entity.node);
        }
        if (m_texelSplatProbe != null)
            m_texelSplatCaptureRequested = true;
        if (hadPreviewError && !m_previewError)
            m_status.text = "Preview updated.";
    }

    private void RefreshPreviewColors()
    {
        foreach (var preview in m_previews)
            preview.Value.material.color = preview.Key.GetColor();
        if (m_texelSplatProbe != null)
            m_texelSplatCaptureRequested = true;
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
        if (preview.gameObject != null)
        {
            preview.gameObject.SetActive(false);
            Destroy(preview.gameObject);
        }
        if (preview.mesh != null) Destroy(preview.mesh);
        if (preview.material != null) Destroy(preview.material);
    }

    private void ToggleTexelSplat()
    {
        if (m_texelSplatProbe != null)
        {
            ReleaseTexelSplatProbe();
        }
        else
        {
            m_texelSplatProbe = new TexelSplatProbe(m_camera, TexelSplatProbe.DefaultFaceSize);
            m_texelSplatCaptureRequested = true;
        }
        m_previewMenu.style.display = DisplayStyle.None;
    }

    private void ReleaseTexelSplatProbe()
    {
        if (m_texelSplatProbe == null)
            return;

        if (TexelSplatRendererFeature.PreviewProbe == m_texelSplatProbe)
            TexelSplatRendererFeature.PreviewProbe = null;
        m_texelSplatProbe.Dispose();
        m_texelSplatProbe = null;
        m_texelSplatCaptureRequested = false;
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
            m_texelSplatButton.text = m_texelSplatProbe != null
                ? "Disable texel splat" : "Enable texel splat";
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
