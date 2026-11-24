using UnityEditor;
using UnityEngine;

public sealed class PixelArtCameraComparisonWindow : EditorWindow
{
    private const float ViewSize = 7f;
    private const float PanDistance = 2.5f;
    private const double PanDuration = 8.0;
    private const int ChecksPerSide = 4;

    private enum ReplayMode
    {
        Normal,
        ScreenSpace,
        TexelSplat,
        Orthographic
    }

    private SceneView sceneView;
    private Vector3 previousPivot;
    private Quaternion previousRotation;
    private float previousSize;
    private bool previousOrthographic;
    private bool previous2DMode;
    private bool previousPixelArtEnabled;
    private bool previousTexelSplatEnabled;
    private bool hasPreviousView;
    private bool isPlaying;
    private ReplayMode replayMode;
    private double startTime;

    private GameObject demoRoot;
    private Material demoMaterial;
    private Texture2D demoTexture;

    [MenuItem("Tools/Pixel Art/Camera Comparison")]
    private static void Open()
    {
        GetWindow<PixelArtCameraComparisonWindow>("Camera Comparison");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Pixel-art camera comparison", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "The perspective replays use the same Scene view framing and straight pan. " +
            "The texel-splat run captures one fixed probe at the starting position.",
            MessageType.Info);

        if (GUILayout.Button("Create temporary near/far targets"))
            CreateTargets();

        if (GUILayout.Button("Replay perspective (normal render)"))
            StartPan(ReplayMode.Normal);

        if (GUILayout.Button("Replay perspective (screen-space)"))
            StartPan(ReplayMode.ScreenSpace);

        if (GUILayout.Button("Replay perspective (texel splat)"))
            StartPan(ReplayMode.TexelSplat);

        if (GUILayout.Button("Replay orthographic (snap on)"))
            StartPan(ReplayMode.Orthographic);

        using (new EditorGUI.DisabledScope(!isPlaying))
        {
            if (GUILayout.Button("Stop"))
                StopPan();
        }

        if (GUILayout.Button("Restore previous Scene view"))
            RestoreView();

        string modeName = replayMode switch
        {
            ReplayMode.ScreenSpace => "screen-space pixel art",
            ReplayMode.TexelSplat => "texel splat",
            ReplayMode.Orthographic => "orthographic pixel art",
            _ => "normal render"
        };
        EditorGUILayout.LabelField(isPlaying ? $"Playing {modeName} pan" : "Ready");
    }

    private void StartPan(ReplayMode mode)
    {
        if (sceneView == null)
            sceneView = SceneView.lastActiveSceneView;

        if (sceneView == null)
        {
            Debug.LogWarning("Open a Scene view before starting the camera comparison.");
            return;
        }

        if (!hasPreviousView)
        {
            previousPivot = sceneView.pivot;
            previousRotation = sceneView.rotation;
            previousSize = sceneView.size;
            previousOrthographic = sceneView.orthographic;
            previous2DMode = sceneView.in2DMode;
            previousPixelArtEnabled = PixelArtRenderController.Enabled;
            previousTexelSplatEnabled = TexelSplatPrototypeController.Enabled;
            hasPreviousView = true;
        }

        StopPan();
        sceneView.in2DMode = false;
        replayMode = mode;
        ApplyPan(0f);

        switch (mode)
        {
            case ReplayMode.Normal:
                TexelSplatPrototypeController.SetEnabled(false);
                PixelArtRenderController.SetEnabled(false);
                break;
            case ReplayMode.TexelSplat:
                if (!TexelSplatPrototypeController.SetEnabled(true))
                    return;
                TexelSplatPrototypeController.Recapture();
                break;
            default:
                if (!PixelArtRenderController.SetEnabled(true))
                    return;
                break;
        }

        isPlaying = true;
        startTime = EditorApplication.timeSinceStartup;
        EditorApplication.update += UpdatePan;
        Repaint();
    }

    private void UpdatePan()
    {
        if (sceneView == null)
        {
            StopPan();
            return;
        }

        float progress = Mathf.Clamp01(
            (float)((EditorApplication.timeSinceStartup - startTime) / PanDuration));
        ApplyPan(progress);

        if (progress >= 1f)
            StopPan();
    }

    private void ApplyPan(float progress)
    {
        float x = Mathf.Lerp(-PanDistance * 0.5f, PanDistance * 0.5f, progress);
        sceneView.LookAt(new Vector3(x, 0f, 0f), Quaternion.identity,
            ViewSize, replayMode == ReplayMode.Orthographic, true);
        sceneView.Repaint();
    }

    private void StopPan()
    {
        EditorApplication.update -= UpdatePan;
        isPlaying = false;
        Repaint();
    }

    private void RestoreView()
    {
        StopPan();
        if (!hasPreviousView || sceneView == null)
            return;

        sceneView.LookAt(previousPivot, previousRotation, previousSize,
            previousOrthographic, true);
        sceneView.in2DMode = previous2DMode;
        sceneView.Repaint();
        TexelSplatPrototypeController.SetEnabled(previousTexelSplatEnabled);
        PixelArtRenderController.SetEnabled(previousPixelArtEnabled);
        hasPreviousView = false;
    }

    private void CreateTargets()
    {
        if (demoRoot != null)
            return;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            Debug.LogError("URP Unlit shader not found; demo targets were not created.");
            return;
        }

        demoTexture = new Texture2D(ChecksPerSide, ChecksPerSide, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave
        };

        for (int y = 0; y < ChecksPerSide; y++)
        {
            for (int x = 0; x < ChecksPerSide; x++)
                demoTexture.SetPixel(x, y, ((x + y) & 1) == 0
                    ? Color.white : Color.black);
        }
        demoTexture.Apply();

        demoMaterial = new Material(shader) { hideFlags = HideFlags.DontSave };
        demoMaterial.SetTexture("_BaseMap", demoTexture);
        demoMaterial.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

        demoRoot = new GameObject("Temporary Pixel Art Comparison Targets")
        {
            hideFlags = HideFlags.DontSave
        };
        CreateTarget("NEAR", new Vector3(-1.8f, 0f, -2.5f));
        CreateTarget("FAR", new Vector3(1.8f, 0f, 2.5f));
        TexelSplatPrototypeController.InvalidateCapture();
        SceneView.RepaintAll();
    }

    private void CreateTarget(string label, Vector3 position)
    {
        GameObject target = GameObject.CreatePrimitive(PrimitiveType.Quad);
        target.name = label;
        target.transform.SetParent(demoRoot.transform, false);
        target.transform.position = position;
        target.transform.localScale = new Vector3(2.2f, 2.2f, 1f);
        target.GetComponent<MeshRenderer>().sharedMaterial = demoMaterial;
        DestroyImmediate(target.GetComponent<Collider>());
        target.hideFlags = HideFlags.DontSave;
    }

    private void OnDisable()
    {
        RestoreView();
        if (demoRoot != null)
            DestroyImmediate(demoRoot);
        if (demoMaterial != null)
            DestroyImmediate(demoMaterial);
        if (demoTexture != null)
            DestroyImmediate(demoTexture);
    }
}
