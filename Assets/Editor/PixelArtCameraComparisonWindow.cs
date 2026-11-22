using UnityEditor;
using UnityEngine;

public sealed class PixelArtCameraComparisonWindow : EditorWindow
{
    private const float ViewSize = 7f;
    private const float PanDistance = 2.5f;
    private const double PanDuration = 8.0;

    private SceneView sceneView;
    private Vector3 previousPivot;
    private Quaternion previousRotation;
    private float previousSize;
    private bool previousOrthographic;
    private bool previous2DMode;
    private bool previousPixelArtEnabled;
    private bool hasPreviousView;
    private bool isPlaying;
    private bool playOrthographic;
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
            "Both runs use the same Scene view angle, framing, and straight pan. " +
            "The existing camera snap applies only to the orthographic run.",
            MessageType.Info);

        if (GUILayout.Button("Create temporary near/far targets"))
            CreateTargets();

        if (GUILayout.Button("Replay orthographic (snap on)"))
            StartPan(true);

        if (GUILayout.Button("Replay perspective (snap skipped)"))
            StartPan(false);

        using (new EditorGUI.DisabledScope(!isPlaying))
        {
            if (GUILayout.Button("Stop"))
                StopPan();
        }

        if (GUILayout.Button("Restore previous Scene view"))
            RestoreView();

        EditorGUILayout.LabelField(isPlaying
            ? $"Playing {(playOrthographic ? "orthographic" : "perspective")} pan"
            : "Ready");
    }

    private void StartPan(bool orthographic)
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
            hasPreviousView = true;
        }

        if (!PixelArtRenderController.SetEnabled(true))
            return;

        StopPan();
        sceneView.in2DMode = false;
        playOrthographic = orthographic;
        isPlaying = true;
        startTime = EditorApplication.timeSinceStartup;
        ApplyPan(0f);
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
            ViewSize, playOrthographic, true);
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

        demoTexture = new Texture2D(16, 16, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave
        };

        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
                demoTexture.SetPixel(x, y, ((x / 2 + y / 2) & 1) == 0
                    ? Color.white : Color.black);
        }
        demoTexture.Apply();

        demoMaterial = new Material(shader) { hideFlags = HideFlags.DontSave };
        demoMaterial.SetTexture("_BaseMap", demoTexture);

        demoRoot = new GameObject("Temporary Pixel Art Comparison Targets")
        {
            hideFlags = HideFlags.DontSave
        };
        CreateTarget("NEAR", new Vector3(-1.8f, 0f, -2.5f));
        CreateTarget("FAR", new Vector3(1.8f, 0f, 2.5f));
        SceneView.RepaintAll();
    }

    private void CreateTarget(string label, Vector3 position)
    {
        GameObject target = GameObject.CreatePrimitive(PrimitiveType.Cube);
        target.name = label;
        target.transform.SetParent(demoRoot.transform, false);
        target.transform.position = position;
        target.transform.localScale = new Vector3(2.2f, 2.2f, 0.05f);
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
