#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using SimpleFileBrowser;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

public class CSGStateTests
{
    [Test]
    public void HistorySupportsUndoRedoAndDiscardingFutureEdits()
    {
        var storage = new CSGStorage();
        var history = new CSGHistory(CSGState.Serialize(storage));
        storage.Add(new SphereNode());
        history.Record(CSGState.Serialize(storage));
        storage.Add(new BoxNode());
        history.Record(CSGState.Serialize(storage));
        Assert.AreEqual(1, CSGState.Deserialize(history.Undo()).Entities.Count);
        Assert.AreEqual(0, CSGState.Deserialize(history.Undo()).Entities.Count);
        Assert.IsFalse(history.CanUndo);
        Assert.AreEqual(1, CSGState.Deserialize(history.Redo()).Entities.Count);
        storage = CSGState.Deserialize(history.Undo());
        storage.Add(new UnionNode());
        history.Record(CSGState.Serialize(storage));
        Assert.IsFalse(history.CanRedo);
        history.Undo();
        Assert.IsInstanceOf<UnionNode>(CSGState.Deserialize(history.Redo()).Entities[0].node);
    }

    [Test]
    public void StateRoundTripPreservesSharedConnectionsAndNextId()
    {
        var storage = new CSGStorage();
        var sphere = new SphereNode { center = new Vector3(1, 2, 3), radius = 2, color = Color.red };
        var sphereEntity = storage.Add(sphere);
        sphereEntity.graphPosition = new Vector2(40, 80);
        sphereEntity.hasGraphPosition = true;
        var operation = new UnionNode { leftChild = sphere, rightChild = sphere, colorSource = OperationColorSource.Custom, customColor = Color.blue };
        storage.Add(operation);
        storage.Remove(operation);
        storage.Add(operation);

        var restored = CSGState.Deserialize(CSGState.Serialize(storage));
        var restoredSphere = (SphereNode)restored.Entities[0].node;
        var restoredOperation = (UnionNode)restored.Entities[1].node;
        Assert.AreEqual(sphere.center, restoredSphere.center);
        Assert.AreEqual(sphere.radius, restoredSphere.radius);
        Assert.AreEqual(Color.red, restoredSphere.color);
        Assert.AreEqual(sphereEntity.graphPosition, restored.Entities[0].graphPosition);
        Assert.IsTrue(restored.Entities[0].hasGraphPosition);
        Assert.AreSame(restoredSphere, restoredOperation.leftChild);
        Assert.AreSame(restoredSphere, restoredOperation.rightChild);
        Assert.AreEqual(Color.blue, restoredOperation.customColor);
        Assert.AreEqual(4, restored.Add(new BoxNode()).id);
    }

    [Test]
    public void InvalidConnectionsAndCyclesAreRejected()
    {
        var storage = new CSGStorage();
        var operation = new UnionNode();
        storage.Add(operation);
        operation.leftChild = operation;
        string cyclic = CSGState.Serialize(storage);
        Assert.Throws<FormatException>(() => CSGState.Deserialize(cyclic));
        string missing = cyclic.Replace("\"left\": 1", "\"left\": 999");
        Assert.AreNotEqual(cyclic, missing);
        Assert.Throws<FormatException>(() => CSGState.Deserialize(missing));
        Assert.Throws<FormatException>(() => CSGState.Deserialize("{}"));
        Assert.AreEqual(0, CSGState.Deserialize("{\"version\":1,\"nextId\":1,\"nodes\":[]}").CustomShapes.Count);
    }

    [Test]
    public void CustomShapeSnapshotsKeepInputsAndRemainIndependent()
    {
        var storage = new CSGStorage();
        var sphere = new SphereNode { center = Vector3.right, radius = 2 };
        var box = new BoxNode { center = Vector3.up };
        var union = new UnionNode { leftChild = sphere, rightChild = box };
        storage.Add(sphere);
        storage.Add(box);
        storage.Add(union);
        string snapshot = CSGCustomShapes.Capture(storage, new CSGNode[] { union, sphere });
        storage.AddCustomShape("Pair", snapshot);
        sphere.radius = 5;
        var restored = CSGState.Deserialize(CSGState.Serialize(storage));
        Assert.AreEqual(1, restored.CustomShapes.Count);
        var copies = CSGCustomShapes.AddCopy(restored, restored.CustomShapes[0]);
        Assert.AreEqual(3, copies.Count);
        var copiedSphere = (SphereNode)copies[0].node;
        var copiedBox = (BoxNode)copies[1].node;
        var copiedUnion = (UnionNode)copies[2].node;
        Assert.AreEqual(2, copiedSphere.radius);
        Assert.AreSame(copiedSphere, copiedUnion.leftChild);
        Assert.AreSame(copiedBox, copiedUnion.rightChild);
        copiedSphere.radius = 3;
        Assert.AreEqual(5, ((SphereNode)restored.Entities[0].node).radius);
        Assert.Throws<ArgumentException>(() => storage.AddCustomShape("pair", snapshot));
        Assert.Throws<ArgumentException>(() => CSGCustomShapes.Capture(storage, new CSGNode[] { sphere }));
    }

    [Test]
    public void ObjIndicesUseSeparateVertexAndNormalOffsets()
    {
        var first = new Mesh();
        var second = new Mesh();
        try
        {
            var vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            first.vertices = vertices;
            first.triangles = new[] { 0, 1, 2 };
            second.vertices = vertices;
            second.triangles = new[] { 0, 1, 2 };
            second.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            string obj = CSGObjExporter.Serialize(new[]
            {
                new KeyValuePair<string, Mesh>("first", first),
                new KeyValuePair<string, Mesh>("second", second)
            });
            StringAssert.Contains("f 1 2 3\n", obj);
            StringAssert.Contains("f 4//1 5//2 6//3\n", obj);
            Assert.Throws<InvalidOperationException>(() => CSGObjExporter.Serialize(new KeyValuePair<string, Mesh>[0]));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(first);
            UnityEngine.Object.DestroyImmediate(second);
        }
    }

    [UnityTest]
    public IEnumerator RuntimeHistoryAndFilesRestoreTheGraph()
    {
        yield return new EnterPlayMode();
        EditorSceneManager.LoadSceneInPlayMode("Assets/Scenes/SampleScene.unity",
            new LoadSceneParameters(LoadSceneMode.Single));
        yield return null;
        var ui = UnityEngine.Object.FindAnyObjectByType<CSGRuntimeUI>();
        if (ui == null)
            ui = new GameObject("CSG Runtime UI Test").AddComponent<CSGRuntimeUI>();
        Assert.IsTrue(ui.enabled);
        string prefix = Path.Combine(Path.GetTempPath(), "csg-test-" + Guid.NewGuid().ToString("N"));
        string statePath = prefix + ".json";
        string objPath = prefix + ".obj";
        try
        {
            Call(ui, "AddNode", new SphereNode(), (Vector2?)new Vector2(30, 30));
            Call(ui, "AddNode", new UnionNode(), (Vector2?)new Vector2(350, 30));
            var storage = (CSGStorage)Field(ui, "m_storage");
            var operation = (UnionNode)storage.Entities[1].node;
            operation.leftChild = storage.Entities[0].node;
            operation.rightChild = storage.Entities[0].node;
            Call(ui, "RecordHistory");
            SendShortcut(ui, KeyCode.Z);
            storage = (CSGStorage)Field(ui, "m_storage");
            Assert.IsNull(((UnionNode)storage.Entities[1].node).leftChild);
            SendShortcut(ui, KeyCode.Y);
            storage = (CSGStorage)Field(ui, "m_storage");
            Assert.AreSame(storage.Entities[0].node, ((UnionNode)storage.Entities[1].node).leftChild);
            Call(ui, "OpenAddMenu", Vector2.zero);
            Assert.AreEqual(DisplayStyle.Flex, ((VisualElement)Field(ui, "m_addMenu")).style.display.value);
            Assert.IsTrue(((Button)Field(ui, "m_undoButton")).enabledSelf);
            Assert.IsFalse(((Button)Field(ui, "m_redoButton")).enabledSelf);
            Call(ui, "CloseAddMenu");
            Call(ui, "ShowSaveStateDialog");
            Assert.IsTrue(FileBrowser.IsOpen);
            SendShortcut(ui, KeyCode.Z);
            Assert.AreSame(storage, Field(ui, "m_storage"));
            FileBrowser.HideDialog(true);
            Assert.IsFalse(FileBrowser.IsOpen);
            Call(ui, "ShowSaveStateDialog");
            SelectFile(statePath);
            Assert.IsTrue(File.Exists(statePath));
            Call(ui, "ShowExportObjDialog");
            Assert.IsTrue(FileBrowser.IsOpen);
            SelectFile(objPath);
            Assert.IsTrue(File.Exists(objPath));
            StringAssert.Contains("\nf ", File.ReadAllText(objPath));
            Call(ui, "AddNode", new BoxNode(), (Vector2?)new Vector2(700, 30));
            Call(ui, "ShowImportStateDialog");
            Assert.IsTrue(FileBrowser.IsOpen);
            SelectFile(statePath);
            Assert.AreEqual(2, ((CSGStorage)Field(ui, "m_storage")).Entities.Count);
            Call(ui, "Undo");
            Assert.AreEqual(3, ((CSGStorage)Field(ui, "m_storage")).Entities.Count);
            File.WriteAllText(statePath, "{}");
            LogAssert.Expect(LogType.Error, "Import failed: Invalid or unsupported CSG state file.");
            Call(ui, "ImportState", statePath);
            Assert.AreEqual(3, ((CSGStorage)Field(ui, "m_storage")).Entities.Count);
            storage = (CSGStorage)Field(ui, "m_storage");
            Call(ui, "SelectNode", storage.Entities[0].node, false);
            ShiftClickNode(ui, storage.Entities[2].node, true);
            Assert.AreEqual(2, ((HashSet<CSGNode>)Field(ui, "m_selected")).Count);
            ShiftClickNode(ui, storage.Entities[2].node);
            Assert.AreEqual(1, ((HashSet<CSGNode>)Field(ui, "m_selected")).Count);
            ShiftClickNode(ui, storage.Entities[2].node);
            Call(ui, "ShowCreateShapeDialog");
            ((TextField)Field(ui, "m_dialogName")).value = "";
            Call(ui, "CreateCustomShape");
            Assert.AreEqual(0, storage.CustomShapes.Count);
            Assert.IsNotEmpty(((Label)Field(ui, "m_dialogError")).text);
            ((TextField)Field(ui, "m_dialogName")).value = "My shape";
            Call(ui, "CreateCustomShape");
            Assert.AreEqual(1, storage.CustomShapes.Count);
            Assert.AreEqual(1, CSGState.Deserialize(CSGState.Serialize(storage)).CustomShapes.Count);
            Call(ui, "Undo");
            storage = (CSGStorage)Field(ui, "m_storage");
            Assert.AreEqual(0, storage.CustomShapes.Count);
            Call(ui, "Redo");
            storage = (CSGStorage)Field(ui, "m_storage");
            Assert.AreEqual(1, storage.CustomShapes.Count);
            float originalSpacing = storage.Entities[2].graphPosition.x - storage.Entities[0].graphPosition.x;
            var customMenu = (VisualElement)Field(ui, "m_customMenu");
            var addShape = customMenu.Q<Button>(className: "custom-shape-add");
            Assert.AreEqual("My shape", addShape.text);
            Click(addShape);
            Assert.AreEqual(5, storage.Entities.Count);
            Assert.AreEqual(originalSpacing, storage.Entities[4].graphPosition.x - storage.Entities[3].graphPosition.x);
            Call(ui, "Undo");
            storage = (CSGStorage)Field(ui, "m_storage");
            Assert.AreEqual(3, storage.Entities.Count);
            Assert.AreEqual(1, storage.CustomShapes.Count);
            Click(((VisualElement)Field(ui, "m_customMenu")).Q<Button>(className: "node-delete"));
            Assert.AreEqual(0, storage.CustomShapes.Count);
            Assert.AreEqual(3, storage.Entities.Count);
            Call(ui, "Undo");
            storage = (CSGStorage)Field(ui, "m_storage");
            Assert.AreEqual(1, storage.CustomShapes.Count);
            Call(ui, "NewDocument");
            Assert.AreEqual(DisplayStyle.Flex, ((VisualElement)Field(ui, "m_dialog")).style.display.value);
            Call(ui, "CloseDialog");
            Assert.AreEqual(3, ((CSGStorage)Field(ui, "m_storage")).Entities.Count);
            Call(ui, "NewDocument");
            ((Action)Field(ui, "m_dialogAction"))();
            storage = (CSGStorage)Field(ui, "m_storage");
            Assert.AreEqual(0, storage.Entities.Count);
            Assert.AreEqual(0, storage.CustomShapes.Count);
            Assert.IsFalse(((CSGHistory)Field(ui, "m_history")).CanUndo);
            Assert.IsFalse(((CSGHistory)Field(ui, "m_history")).CanRedo);
            Call(ui, "NewDocument");
            Assert.AreEqual(DisplayStyle.None, ((VisualElement)Field(ui, "m_dialog")).style.display.value);
        }
        finally
        {
            if (FileBrowser.IsOpen) FileBrowser.HideDialog(true);
            if (File.Exists(statePath)) File.Delete(statePath);
            if (File.Exists(objPath)) File.Delete(objPath);
        }
        yield return new ExitPlayMode();
    }

    private static object Field(object target, string name)
    {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }

    private static void Call(object target, string name, params object[] arguments)
    {
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments);
    }

    private static void SelectFile(string path)
    {
        Call(FileBrowser.Instance, "OnOperationSuccessful", (object)new[] { path });
        Assert.IsFalse(FileBrowser.IsOpen);
    }

    private static void Click(Button button)
    {
        typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Invoke(button.clickable, new object[] { null });
    }

    private static void ShiftClickNode(CSGRuntimeUI ui, CSGNode node, bool field = false)
    {
        var nodes = Field(ui, "m_nodes");
        var item = nodes.GetType().GetProperty("Item").GetValue(nodes, new object[] { node });
        var card = (VisualElement)item.GetType().GetField("element").GetValue(item);
        VisualElement target = field ? (VisualElement)card.Q<FloatField>() : card.Q<Label>(className: "node-title");
        Assert.IsNotNull(target);
        using (var evt = PointerDownEvent.GetPooled(new Event
        {
            type = EventType.MouseDown, button = 0, modifiers = EventModifiers.Shift
        }))
        {
            Assert.IsTrue(evt.shiftKey);
            evt.target = target;
            target.SendEvent(evt);
        }
    }

    private static void SendShortcut(CSGRuntimeUI ui, KeyCode key)
    {
        var root = ((UIDocument)Field(ui, "m_document")).rootVisualElement;
        var canvas = (VisualElement)Field(ui, "m_canvas");
        canvas.Focus();
        using (var evt = KeyDownEvent.GetPooled(new Event
        {
            type = EventType.KeyDown, keyCode = key, modifiers = EventModifiers.Control
        }))
        {
            Assert.IsTrue(evt.actionKey);
            Assert.IsNotNull(root.panel);
            canvas.SendEvent(evt);
        }
    }
}
#endif
