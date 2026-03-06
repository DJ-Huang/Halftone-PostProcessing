using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class HalftoneRenderFeature : ScriptableRendererFeature
{
    
    [System.Serializable]
    public class HalftoneSettings
    {
        public Material halftoneMaterial;
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
        
        [Range(10f, 500f)] public float gridSize = 150f;
        [Range(0f, 1f)] public float dotSize = 0.6f;
        [Range(0.001f, 0.2f)] public float smoothness = 0.05f;
    }
    
    [SerializeField] HalftoneSettings settings =  new HalftoneSettings();
    HalftoneRenderFeaturePass halftonePass;

    /// <inheritdoc/>
    public override void Create()
    {
        if (settings.halftoneMaterial != null)
        {
            halftonePass = new HalftoneRenderFeaturePass(settings);
        }
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (halftonePass != null)
        {
            renderer.EnqueuePass(halftonePass);
        }
    }

    class HalftoneRenderFeaturePass : ScriptableRenderPass
    {
        private static readonly int GridSizeId = Shader.PropertyToID("_GridSize");
        private static readonly int DotSizeId = Shader.PropertyToID("_DotSize");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        
        readonly HalftoneSettings settings;
        
        public HalftoneRenderFeaturePass(HalftoneSettings settings)
        {
            this.settings = settings;
            renderPassEvent = settings.renderPassEvent;
        }

        // This class stores the data needed by the RenderGraph pass.
        // It is passed as a parameter to the delegate function that executes the RenderGraph pass.
        private class PassData
        {
            public TextureHandle source;
            public Material material;
        }

        // This static method is passed as the RenderFunc delegate to the RenderGraph render pass.
        // It is used to execute draw commands.
        static void ExecutePass(PassData data, RasterGraphContext context)
        {
            
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (settings.halftoneMaterial == null) return;
            
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.cameraType != CameraType.Game && cameraData.cameraType != CameraType.SceneView)
                return;
            
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            TextureHandle activeColor = resourceData.activeColorTexture;
            
            settings.halftoneMaterial.SetFloat(GridSizeId, settings.gridSize);
            settings.halftoneMaterial.SetFloat(DotSizeId, settings.dotSize);
            settings.halftoneMaterial.SetFloat(SmoothnessId, settings.smoothness);
            
            RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            TextureHandle tempColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "Halftone_Temp", false);
            
            if (!activeColor.IsValid()) return;
            
            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData))
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
            
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Halftone Copy Back", out var passData))
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
}
