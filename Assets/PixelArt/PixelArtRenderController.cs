#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// Keeps the CSG window toggle in sync with the Scene view render feature.
[InitializeOnLoad]
public static class PixelArtRenderController
{
    private const string PreferenceKey = "PixelArtRender.Enabled";
    private const string RendererPath = "Assets/Settings/PC_Renderer.asset";

    static PixelArtRenderController()
    {
        PixelArtRenderer.SceneViewEnabled = EditorPrefs.GetBool(PreferenceKey, false);
    }

    public static bool Enabled => PixelArtRenderer.SceneViewEnabled;

    public static bool SetEnabled(bool enabled)
    {
        if (enabled && !EnsureRendererFeature())
            return false;

        PixelArtRenderer.SceneViewEnabled = enabled;
        EditorPrefs.SetBool(PreferenceKey, enabled);
        SceneView.RepaintAll(); // Makes the visual change appear
        return true;
    }

    // Also available from the Tools menu if the renderer feature needs reinstalling.
    [MenuItem("Tools/Pixel Art/Install Renderer Feature")]
    public static void InstallRendererFeature()
    {
        EnsureRendererFeature();
    }

    private static bool EnsureRendererFeature()
    // Loads PC_Renderer.asset, check its feature list, and creates a PixelArtRenderer
    {
        var rendererData = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererPath);
        if (rendererData == null)
        {
            Debug.LogError($"Pixel art renderer asset not found: {RendererPath}");
            return false;
        }

        foreach (var feature in rendererData.rendererFeatures)
        {
            if (feature is PixelArtRenderer existingPixelArtFeature)
            {
                if (!existingPixelArtFeature.isActive)
                {
                    existingPixelArtFeature.SetActive(true);
                    EditorUtility.SetDirty(existingPixelArtFeature);
                    AssetDatabase.SaveAssets();
                    rendererData.SetDirty();
                }
                return true;
            }
        }

        var pixelArtFeature = ScriptableObject.CreateInstance<PixelArtRenderer>();
        pixelArtFeature.name = nameof(PixelArtRenderer);
        pixelArtFeature.hideFlags = HideFlags.HideInHierarchy;
        AssetDatabase.AddObjectToAsset(pixelArtFeature, rendererData);

        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
            pixelArtFeature, out _, out long localId);

        // Keep URP's feature list and local-ID map aligned, as its Inspector does.
        var serializedRenderer = new SerializedObject(rendererData);
        serializedRenderer.Update();
        var features = serializedRenderer.FindProperty("m_RendererFeatures");
        var featureMap = serializedRenderer.FindProperty("m_RendererFeatureMap");
        int index = features.arraySize;
        features.arraySize = index + 1;
        features.GetArrayElementAtIndex(index).objectReferenceValue = pixelArtFeature;
        featureMap.arraySize = index + 1;
        featureMap.GetArrayElementAtIndex(index).longValue = localId;
        serializedRenderer.ApplyModifiedPropertiesWithoutUndo();

        rendererData.SetDirty();
        EditorUtility.SetDirty(pixelArtFeature);
        EditorUtility.SetDirty(rendererData);
        AssetDatabase.SaveAssets();
        Debug.Log("PixelArtRenderer added to PC_Renderer.", rendererData);
        return true;
    }
}
#endif
