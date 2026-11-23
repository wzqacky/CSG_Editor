using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;


public sealed class PixelArtRenderer : ScriptableRendererFeature
{
    [SerializeField, Range(2,16)]
    private int pixelSize = 6;

    public static bool SceneViewEnabled { get; set; } = false;
    public static Camera PreviewCamera { get; set; }

    private PixelArtPass pass;

    public override void Create()
    // Construct the pass and select its place in the rendering sequence
    {
        pass = new PixelArtPass
        {
            // Schedules the effect after URP's normal postprocessing
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    // Called for a camera, enqueue the pass only if this camera should receive the effect
    {
        if (!(SceneViewEnabled && renderingData.cameraData.isSceneViewCamera) &&
            (PreviewCamera == null || renderingData.cameraData.camera != PreviewCamera))
        {
            return;
        }

        pass.PixelSize = pixelSize;
        renderer.EnqueuePass(pass);
    }

    private sealed class PixelArtPass : ScriptableRenderPass
    {
        public int PixelSize { get; set; } = 6;

        public PixelArtPass()
        {
            // The pass needs to read the camera image as a texture.
            requiresIntermediateTexture = true;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        // Describe the temporary images and image-copy operations that URP should perform
        {
            var resources = frameData.Get<UniversalResourceData>();

            if (resources.isActiveTargetBackBuffer)
                // Check to avoid trying to sample a back buffer that is not available
                return;
            
            // TextureHandle is a Render Graph reference to the current camera image
            TextureHandle source = resources.activeColorTexture;
            var sourceDesc = renderGraph.GetTextureDesc(source);

            // Image metadata
            var smallDesc = sourceDesc;

            // Create the downsampled image texture info
            smallDesc.name = "PixelArt_small";
            smallDesc.width = Mathf.Max(1, Mathf.CeilToInt(sourceDesc.width / (float)PixelSize));
            smallDesc.height = Mathf.Max(1, Mathf.CeilToInt(sourceDesc.height / (float)PixelSize));
            smallDesc.msaaSamples = MSAASamples.None;
            smallDesc.depthBufferBits = 0;
            smallDesc.clearBuffer = false;

            TextureHandle smallImage = renderGraph.CreateTexture(smallDesc);

            // Make a full-size texture for the final image
            var outputDesc = sourceDesc;
            outputDesc.name = "PixelArt_output";
            outputDesc.msaaSamples = MSAASamples.None;
            outputDesc.depthBufferBits = 0;
            outputDesc.clearBuffer = false;

            TextureHandle output = renderGraph.CreateTexture(outputDesc);

            // Add stage: shrink the rendered scene
            // NOTE: Using nearest-neighbor downsampling here to ensure crisper features
            renderGraph.AddBlitPass(
                source, smallImage, Vector2.one, Vector2.zero,
                filterMode: RenderGraphUtils.BlitFilterMode.ClampNearest,
                passName: "Pixel Art Downsample"
            );
            
            // Add stage: enlarge without smoothing the small pixels
            renderGraph.AddBlitPass(
                smallImage, output, Vector2.one, Vector2.zero,
                filterMode: RenderGraphUtils.BlitFilterMode.ClampNearest,
                passName: "Pixel Art Upsample"
            );

            // Finally, set the output render
            resources.cameraColor = output;
        }
    }
}
