using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

// First texel-splatting prototype: a fixed probe with color/depth and no eye probe.
public sealed class TexelSplatRendererFeature : ScriptableRendererFeature
{
    private const string DepthShaderName = "Hidden/TexelSplat/CopyLinearDepth";
    private const string SplatShaderName = "Hidden/TexelSplat/Splat";

    public static TexelSplatProbe ActiveProbe;

    private Material depthMaterial;
    private Material splatMaterial;
    private DepthCapturePass depthPass;
    private SplatPass splatPass;

    public override void Create()
    {
        CoreUtils.Destroy(depthMaterial);
        CoreUtils.Destroy(splatMaterial);
        depthMaterial = CoreUtils.CreateEngineMaterial(DepthShaderName);
        splatMaterial = CoreUtils.CreateEngineMaterial(SplatShaderName);
        depthPass = new DepthCapturePass { renderPassEvent = RenderPassEvent.AfterRenderingOpaques };
        splatPass = new SplatPass { renderPassEvent = RenderPassEvent.BeforeRenderingSkybox };
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(depthMaterial);
        CoreUtils.Destroy(splatMaterial);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        Camera camera = renderingData.cameraData.camera;
        if (camera != null && camera.TryGetComponent(out TexelSplatCaptureCamera marker))
        {
            if (depthMaterial != null && marker.DepthTarget != null)
            {
                depthPass.Setup(marker, depthMaterial);
                renderer.EnqueuePass(depthPass);
            }
            return;
        }

        TexelSplatProbe probe = ActiveProbe;
        if (probe == null || !probe.Ready || camera != probe.ViewCamera || splatMaterial == null)
            return;

        splatPass.Setup(probe, splatMaterial);
        renderer.EnqueuePass(splatPass);
    }

    private sealed class DepthCapturePass : ScriptableRenderPass
    {
        private TexelSplatCaptureCamera marker;
        private Material material;

        public void Setup(TexelSplatCaptureCamera captureMarker, Material depthCopyMaterial)
        {
            marker = captureMarker;
            material = depthCopyMaterial;
        }

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        /*
        Notice how at this point, visibility tests or ray-casting has already been conducted.
        This function simply schedules a pass that reads the existing z-buffer, converts its values
        to linear depth, and write them to depth[face]
        */
        {
            if (marker == null || marker.DepthTarget == null || material == null)
                return;

            var resources = frameData.Get<UniversalResourceData>();
            // Get the camera's current depth buffer
            TextureHandle source = resources.activeDepthTexture;
            if (!source.IsValid())
                return;

            TextureHandle destination = graph.ImportTexture(marker.DepthTarget);
            // Scheduling input params for TexelSplatDepthCopy.shader
            var parameters = new RenderGraphUtils.BlitMaterialParameters(
                source, destination, material, 0);
            graph.AddBlitPass(parameters, passName: "Texel Splat Copy Probe Depth");
            marker.DepthPassRecorded = true;
        }
    }

    private sealed class SplatPass : ScriptableRenderPass
    {
        private TexelSplatProbe probe;
        private Material material;

        private sealed class PassData
        // One shared splat material, basically tells the GPU how to draw a splat.
        {
            public TexelSplatProbe probe;
            public Material material;
            public Color background;
        }

        public void Setup(TexelSplatProbe activeProbe, Material splatMaterial)
        {
            probe = activeProbe;
            material = splatMaterial;
        }

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            using (var builder = graph.AddRasterRenderPass<PassData>(
                "Texel Splat Project Probe Texels", out var data))
            {
                data.probe = probe;
                data.material = material;
                data.background = frameData.Get<UniversalCameraData>().backgroundColor;
                for (int face = 0; face < TexelSplatProbe.FaceCount; face++)
                {
                    builder.UseTexture(graph.ImportTexture(probe.ColorHandles[face]));
                    builder.UseTexture(graph.ImportTexture(probe.DepthHandles[face]));
                }
                builder.SetRenderAttachment(resources.activeColorTexture, 0);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Write);
                builder.SetRenderFunc(static (PassData passData, RasterGraphContext context) =>
                {
                    // Replace opaque geometry. URP draws the sky and transparent objects next.
                    context.cmd.ClearRenderTarget(true, true, passData.background);
                    var properties = new MaterialPropertyBlock();
                    for (int face = 0; face < TexelSplatProbe.FaceCount; face++)
                    {
                        properties.Clear();
                        // Pass info. for each face into the call
                        properties.SetTexture("_ProbeColor", passData.probe.Colors[face]);
                        properties.SetTexture("_ProbeDepth", passData.probe.Depths[face]);
                        properties.SetVector("_ProbeOrigin", passData.probe.Origin);
                        properties.SetVector("_FaceForward", passData.probe.GetForward(face));
                        properties.SetVector("_FaceUp", passData.probe.GetUp(face));
                        properties.SetVector("_FaceRight", passData.probe.GetRight(face));
                        properties.SetInt("_FaceSize", passData.probe.FaceSize);
                        context.cmd.DrawProcedural(Matrix4x4.identity, passData.material,
                            0, MeshTopology.Triangles, 6,
                            passData.probe.FaceSize * passData.probe.FaceSize, properties);
                    }
                });
            }
        }
    }
}
