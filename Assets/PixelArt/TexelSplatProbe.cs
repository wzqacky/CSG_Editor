using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// One fixed, six-face probe. The view camera can move without changing these samples.
public sealed class TexelSplatProbe : System.IDisposable
{
    // 6 faces for the cubemap
    public const int FaceCount = 6;
    public const int DefaultFaceSize = 128;

    // Facing direction of each face
    private static readonly Vector3[] Forwards =
    {
        Vector3.right, Vector3.left, Vector3.up,
        Vector3.down, Vector3.forward, Vector3.back
    };
    // Upward (+y) direction of each face
    private static readonly Vector3[] Ups =
    {
        Vector3.up, Vector3.up, Vector3.back,
        Vector3.forward, Vector3.up, Vector3.up
    };

    public readonly RenderTexture[] Colors = new RenderTexture[FaceCount];
    // Intersection depth at that pixel, i.e. first hit
    public readonly RenderTexture[] Depths = new RenderTexture[FaceCount];
    public readonly RTHandle[] ColorHandles = new RTHandle[FaceCount];
    public readonly RTHandle[] DepthHandles = new RTHandle[FaceCount];

    public Camera ViewCamera { get; }
    public Vector3 Origin { get; private set; }
    public int FaceSize { get; }
    public bool Ready { get; private set; }

    private readonly GameObject captureObject;
    private readonly Camera captureCamera;
    private readonly TexelSplatCaptureCamera captureMarker;

    public TexelSplatProbe(Camera viewCamera, int faceSize)
    {
        ViewCamera = viewCamera;
        FaceSize = Mathf.Clamp(faceSize, 128, 384);
        Origin = viewCamera.transform.position;

        captureObject = new GameObject("Texel Splat Capture Camera")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        captureCamera = captureObject.AddComponent<Camera>();
        captureCamera.enabled = false;
        captureCamera.orthographic = false;
        captureCamera.fieldOfView = 90f;
        captureCamera.aspect = 1f;
        captureCamera.nearClipPlane = 0.05f;
        captureCamera.farClipPlane = 500f;
        captureCamera.allowMSAA = false;
        captureCamera.allowHDR = false;
        captureCamera.clearFlags = CameraClearFlags.SolidColor;
        captureCamera.backgroundColor = Color.black;
        captureCamera.cullingMask = viewCamera.cullingMask;

        var cameraData = captureObject.GetComponent<UniversalAdditionalCameraData>();
        if (cameraData == null)
            cameraData = captureObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.renderPostProcessing = false;
        cameraData.requiresDepthTexture = true;

        captureMarker = captureObject.AddComponent<TexelSplatCaptureCamera>();

        for (int face = 0; face < FaceCount; face++)
        {
            // Render Graph imports each sampled face as a color-only resource.
            Colors[face] = new RenderTexture(FaceSize, FaceSize, 0, RenderTextureFormat.ARGB32)
            {
                name = $"Texel Splat Color {face}",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                antiAliasing = 1
            };
            Depths[face] = new RenderTexture(FaceSize, FaceSize, 0, RenderTextureFormat.RFloat)
            {
                name = $"Texel Splat Depth {face}",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                antiAliasing = 1
            };
            Colors[face].Create();
            Depths[face].Create();
            ColorHandles[face] = RTHandles.Alloc(Colors[face]);
            DepthHandles[face] = RTHandles.Alloc(Depths[face]);
        }
    }

    public bool CaptureAt(Vector3 origin)
    {
        Ready = false;
        Origin = origin;
        captureCamera.cullingMask = ViewCamera.cullingMask;
        captureObject.transform.position = origin;

        // URP needs a depth attachment while rendering geometry. The capture
        // passes write quantized color and linear depth into separate face textures.
        var targetDesc = Colors[0].descriptor;
        targetDesc.depthBufferBits = 24;
        var cameraTarget = RenderTexture.GetTemporary(targetDesc);
        try
        {
            Ready = CaptureFaces(cameraTarget);
        }
        finally
        {
            RenderTexture.ReleaseTemporary(cameraTarget);
        }
        return Ready;
    }

    private bool CaptureFaces(RenderTexture cameraTarget)
    // Take six pictures from the same position, pointing the capture camera in a different direction each time.
    {
        for (int face = 0; face < FaceCount; face++)
        {
            // Here, transform scene's objects into face's local frame
            captureObject.transform.rotation = Quaternion.LookRotation(Forwards[face], Ups[face]);
            captureMarker.ColorTarget = ColorHandles[face];
            captureMarker.DepthTarget = DepthHandles[face];
            captureMarker.ColorPassRecorded = false;
            captureMarker.DepthPassRecorded = false;

            var request = new UniversalRenderPipeline.SingleCameraRequest
            {
                destination = cameraTarget
            };
            if (!RenderPipeline.SupportsRenderRequest(captureCamera, request))
            {
                Debug.LogError("URP does not support the texel-splat capture request.");
                return false;
            }
            // Unity renders the scene through that camera.
            // Unity’s normal projection and rasterization turn visible scene geometry into pixels here.
            RenderPipeline.SubmitRenderRequest(captureCamera, request);
            if (!captureMarker.DepthPassRecorded)
            {
                Debug.LogError("Texel-splat depth capture did not run. Install the renderer feature first.");
                return false;
            }
            if (!captureMarker.ColorPassRecorded)
            {
                Debug.LogError("Texel-splat color quantization did not run. Check the renderer feature and its Lab shader.");
                return false;
            }
        }

        return true;
    }

    public Vector3 GetForward(int face) => Forwards[face]; // +Z
    public Vector3 GetUp(int face) => Ups[face]; // +Y
    public Vector3 GetRight(int face) => Vector3.Cross(Ups[face], Forwards[face]); // +X

    public void Dispose()
    {
        Ready = false;
        for (int face = 0; face < FaceCount; face++)
        {
            ColorHandles[face]?.Release();
            DepthHandles[face]?.Release();
            CoreUtils.Destroy(Colors[face]);
            CoreUtils.Destroy(Depths[face]);
        }
        CoreUtils.Destroy(captureObject);
    }
}

