using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class HalftoneInteractiveFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public Material halftoneMaterial;
        
        [Range(4f, 32f)] public float pixelSize = 12f;
        [Range(1f, 10f)] public float dotSize = 2.5f;
        [Range(0f, 100f)] public float mouseStrength = 50f;
    }

    public Settings settings = new Settings();
    private HalftoneInteractivePass renderPass;

    // Shader IDs
    private static readonly int PixelSizeId = Shader.PropertyToID("_PixelSize");
    private static readonly int DotSizeId = Shader.PropertyToID("_DotSize");
    private static readonly int MouseStrengthId = Shader.PropertyToID("_MouseStrength");
    private static readonly int BrushTextureId = Shader.PropertyToID("_BrushTexture");

    class HalftoneInteractivePass : ScriptableRenderPass
    {
        private Settings settings;

        private class PassData
        {
            public TextureHandle source;
            public Material material;
        }

        public HalftoneInteractivePass(Settings settings)
        {
            this.settings = settings;
            this.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (settings.halftoneMaterial == null) return;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.cameraType != CameraType.Game) return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            TextureHandle activeColor = resourceData.activeColorTexture;

            if (!activeColor.IsValid()) return;

            // 设置材质参数
            settings.halftoneMaterial.SetFloat(PixelSizeId, settings.pixelSize);
            settings.halftoneMaterial.SetFloat(DotSizeId, settings.dotSize);
            settings.halftoneMaterial.SetFloat(MouseStrengthId, settings.mouseStrength);
            
            RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            TextureHandle tempColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "Halftone_Temp", false);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Halftone Interactive", out var passData))
            {
                passData.source = activeColor;
                passData.material = settings.halftoneMaterial;

                builder.UseTexture(passData.source, AccessFlags.Read);
                builder.SetRenderAttachment(tempColor, 0, AccessFlags.Write);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, 0);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Halftone Copy", out var passData))
            {
                passData.source = tempColor;
                builder.UseTexture(passData.source, AccessFlags.Read);
                builder.SetRenderAttachment(activeColor, 0, AccessFlags.Write);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, data.source, new Vector4(1, 1, 0, 0), 0.0f, false);
                });
            }
        }
    }

    public override void Create()
    {
        renderPass = new HalftoneInteractivePass(settings);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(renderPass);
    }
}