#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// Editor-only driver for the single fixed probe used in the Scene view comparison.
[InitializeOnLoad]
public static class TexelSplatPrototypeController
{
    private const string PreferenceKey = "TexelSplatPrototype.Enabled";
    private const string RendererPath = "Assets/Settings/PC_Renderer.asset";
    private const int FaceSize = 384;

    private static TexelSplatProbe probe;
    private static bool captureRequested;

    static TexelSplatPrototypeController()
    {
        EditorApplication.update += Update;
        Undo.postprocessModifications += OnInspectorEdits;
        Undo.undoRedoPerformed += InvalidateCapture;
        AssemblyReloadEvents.beforeAssemblyReload += ReleaseProbe;
        EditorApplication.quitting += ReleaseProbe;
    }

    public static bool Enabled => EditorPrefs.GetBool(PreferenceKey, false);

    // Rebuild all six faces on the next update at the existing probe position.
    public static void InvalidateCapture()
    {
        if (Enabled)
            captureRequested = true;
    }

    private static UndoPropertyModification[] OnInspectorEdits(UndoPropertyModification[] changes)
    {
        foreach (var change in changes)
        {
            var target = change.currentValue.target;
            if (target is Light || (target is Transform t && t.GetComponentInChildren<Light>() != null))
            {
                InvalidateCapture();
                break;
            }
        }
        return changes;
    }

    [MenuItem("Tools/Pixel Art/Enable Texel Splat Prototype")]
    public static void Enable() => SetEnabled(true);

    [MenuItem("Tools/Pixel Art/Disable Texel Splat Prototype")]
    public static void Disable() => SetEnabled(false);

    [MenuItem("Tools/Pixel Art/Recapture Texel Splat Probe")]
    public static void Recapture()
    {
        if (!Enabled)
            return;
        ReleaseProbe();
        captureRequested = true;
    }

    public static bool SetEnabled(bool enabled)
    {
        if (enabled && !EnsureRendererFeature())
            return false;

        if (enabled)
            PixelArtRenderController.SetEnabled(false);

        EditorPrefs.SetBool(PreferenceKey, enabled);
        if (enabled)
            captureRequested = true;
        else
            ReleaseProbe();

        SceneView.RepaintAll();
        return true;
    }

    private static void Update()
    {
        if (!Enabled)
            return;

        SceneView sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null || sceneView.camera == null)
            return;

        if (probe == null || probe.ViewCamera != sceneView.camera)
        {
            ReleaseProbe();
            probe = new TexelSplatProbe(sceneView.camera, FaceSize);
            captureRequested = true;
        }

        if (!captureRequested)
            return;

        captureRequested = false;
        TexelSplatRendererFeature.ActiveProbe = probe;
        probe.CaptureAt(probe.Origin);
        SceneView.RepaintAll();
    }

    private static void ReleaseProbe()
    {
        TexelSplatRendererFeature.ActiveProbe = null;
        probe?.Dispose();
        probe = null;
    }

    private static bool EnsureRendererFeature()
    {
        var rendererData = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererPath);
        if (rendererData == null)
        {
            Debug.LogError($"Renderer asset not found: {RendererPath}");
            return false;
        }

        foreach (var feature in rendererData.rendererFeatures)
        {
            if (feature is TexelSplatRendererFeature existing)
            {
                if (!existing.isActive)
                {
                    existing.SetActive(true);
                    EditorUtility.SetDirty(existing);
                    AssetDatabase.SaveAssets();
                }
                return true;
            }
        }

        var created = ScriptableObject.CreateInstance<TexelSplatRendererFeature>();
        created.name = nameof(TexelSplatRendererFeature);
        created.hideFlags = HideFlags.HideInHierarchy;
        AssetDatabase.AddObjectToAsset(created, rendererData);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(created, out _, out long localId);

        var serialized = new SerializedObject(rendererData);
        serialized.Update();
        var features = serialized.FindProperty("m_RendererFeatures");
        var featureMap = serialized.FindProperty("m_RendererFeatureMap");
        int index = features.arraySize;
        features.arraySize = index + 1;
        features.GetArrayElementAtIndex(index).objectReferenceValue = created;
        featureMap.arraySize = index + 1;
        featureMap.GetArrayElementAtIndex(index).longValue = localId;
        serialized.ApplyModifiedPropertiesWithoutUndo();

        rendererData.SetDirty();
        EditorUtility.SetDirty(created);
        EditorUtility.SetDirty(rendererData);
        AssetDatabase.SaveAssets();
        return true;
    }
}
#endif
