#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Keeps the CSG window toggle in sync with the Scene view render feature.
[InitializeOnLoad]
public static class PixelArtRenderController
{
    private const string PreferenceKey = "PixelArtRender.Enabled";
    private const string RendererPath = "Assets/Settings/PC_Renderer.asset";
    private static readonly Dictionary<Camera, Vector3> OriginalCameraPositions = new();

    static PixelArtRenderController()
    {
        PixelArtRenderer.SceneViewEnabled = EditorPrefs.GetBool(PreferenceKey, false);
        RenderPipelineManager.beginCameraRendering += SnapSceneCamera;
        RenderPipelineManager.endCameraRendering += RestoreSceneCamera;
    }

    public static bool Enabled => PixelArtRenderer.SceneViewEnabled;

    public static bool SetEnabled(bool enabled)
    {
        if (enabled && !EnsureRendererFeature())
            return false;

        if (enabled && TexelSplatPrototypeController.Enabled)
            TexelSplatPrototypeController.SetEnabled(false);

        PixelArtRenderer.SceneViewEnabled = enabled;
        EditorPrefs.SetBool(PreferenceKey, enabled);
        SceneView.RepaintAll(); // Makes the visual change appear
        return true;
    }

    private static void SnapSceneCamera(ScriptableRenderContext context, Camera camera)
    {
        if (!Enabled || camera.cameraType != CameraType.SceneView ||
            !camera.orthographic || camera.pixelHeight <= 0 ||
            OriginalCameraPositions.ContainsKey(camera))
            return;

        int pixelSize = GetPixelSize();
        if (pixelSize <= 0)
            return;

        int lowHeight = Mathf.Max(1, Mathf.CeilToInt(camera.pixelHeight / (float)pixelSize));
        float worldUnitsPerPixel = 2f * camera.orthographicSize / lowHeight;
        if (worldUnitsPerPixel <= 0f)
            return;

        Vector3 original = camera.transform.position;
        Vector3 right = camera.transform.right;
        Vector3 up = camera.transform.up;
        float x = Vector3.Dot(original, right);
        float y = Vector3.Dot(original, up);

        OriginalCameraPositions.Add(camera, original);
        camera.transform.position = original
            + right * (Mathf.Round(x / worldUnitsPerPixel) * worldUnitsPerPixel - x)
            + up * (Mathf.Round(y / worldUnitsPerPixel) * worldUnitsPerPixel - y);
    }

    private static void RestoreSceneCamera(ScriptableRenderContext context, Camera camera)
    {
        if (!OriginalCameraPositions.TryGetValue(camera, out Vector3 original))
            return;

        camera.transform.position = original;
        OriginalCameraPositions.Remove(camera);
    }

    private static int GetPixelSize()
    {
        var rendererData = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererPath);
        if (rendererData == null)
            return 0;

        foreach (var feature in rendererData.rendererFeatures)
        {
            if (feature is PixelArtRenderer pixelArtRenderer && feature.isActive)
                return pixelArtRenderer.PixelSize;
        }

        return 0;
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
