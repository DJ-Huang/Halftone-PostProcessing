using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class HalftoneFluidFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        [Header("核心着色器配置")]
        public ComputeShader particleCompute;
        [Tooltip("对应 LiquidBlobSplat 材质 (负责画加法软球)")]
        public Material blobMaterial;
        [Tooltip("对应 LiquidComposite 材质 (负责水滴融合与折射)")]
        public Material compositeMaterial;

        [Header("流体力学与网格配置")]
        public int gridSize = 128;
        public float dotBaseSize = 0.015f;
        
        [Range(0.01f, 1f)] public float mouseForce = 0.6f;
        [Range(1f, 30f)] public float springStiffness = 8f; // 调低：减弱弹簧感
        [Range(1f, 20f)] public float damping = 6f;         // 调高：增加黏滞感
        [Range(0.01f, 0.5f)] public float smoothTime = 0.15f;
    }

    public Settings settings = new Settings();
    private LiquidFluidPass renderPass;

    // 显存资源
    private ComputeBuffer particleBuffer;
    private Mesh hexagonMesh;
    private bool isInitialized = false;

    // 平滑输入
    private Vector2 smoothedMousePos;
    private Vector2 smoothPosVelocity;

    // Shader IDs
    private static readonly int ParticlesId = Shader.PropertyToID("_Particles");
    private static readonly int ResolutionId = Shader.PropertyToID("_Resolution");
    private static readonly int MousePosId = Shader.PropertyToID("_MousePos");
    private static readonly int MouseForceId = Shader.PropertyToID("_MouseForce");
    private static readonly int DeltaTimeId = Shader.PropertyToID("_DeltaTime");
    private static readonly int GridSizeId = Shader.PropertyToID("_GridSize");
    private static readonly int SpringStiffnessId = Shader.PropertyToID("_SpringStiffness");
    private static readonly int DampingId = Shader.PropertyToID("_Damping");
    private static readonly int DotBaseSizeId = Shader.PropertyToID("_DotBaseSize");
    private static readonly int FluidHeightMapId = Shader.PropertyToID("_FluidHeightMap");

    public override void Create()
    {
        renderPass = new LiquidFluidPass(this);
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseResources();
    }

    private void ReleaseResources()
    {
        if (particleBuffer != null)
        {
            particleBuffer.Release();
            particleBuffer = null;
        }
        if (hexagonMesh != null)
        {
            CoreUtils.Destroy(hexagonMesh);
            hexagonMesh = null;
        }
        isInitialized = false;
    }

    private bool EnsureResources()
    {
        // 确保材质和 Shader 已分配
        if (settings.particleCompute == null || settings.blobMaterial == null || settings.compositeMaterial == null) 
            return false;

        // 1. 初始化六边形网格 (如果不存在)
        if (hexagonMesh == null) 
        {
            // 这里的变量名统一使用 hexagonMesh
            hexagonMesh = new Mesh { name = "Tight_Hexagon", hideFlags = HideFlags.HideAndDontSave };
            
            Vector3[] vertices = new Vector3[7];
            Vector2[] uvs = new Vector2[7];
            
            // 中心点
            vertices[0] = Vector3.zero; 
            uvs[0] = Vector2.zero;
            
            // 六个顶点
            for (int i = 0; i < 6; i++)
            {
                float angle = i * Mathf.PI / 3.0f;
                vertices[i + 1] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
                uvs[i + 1] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }
            
            hexagonMesh.vertices = vertices; 
            hexagonMesh.uv = uvs;
            hexagonMesh.triangles = new int[] { 0,1,2, 0,2,3, 0,3,4, 0,4,5, 0,5,6, 0,6,1 };
            
            // 优化：上传到 GPU 后不再保留 CPU 内存
            hexagonMesh.UploadMeshData(true);
        }

        // 2. 初始化粒子 Buffer
        int totalParticles = settings.gridSize * settings.gridSize;
        if (particleBuffer == null || particleBuffer.count != totalParticles)
        {
            if (particleBuffer != null) particleBuffer.Release();
            // 每个粒子 32 字节 (float4 posData + float4 velData)
            particleBuffer = new ComputeBuffer(totalParticles, 32);
            isInitialized = false; // 触发 Compute Shader 的 InitKernel
        }

        return true;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!EnsureResources()) return;

        UpdatePhysicsSimulation(ref renderingData);
        renderer.EnqueuePass(renderPass);
    }

    private void UpdatePhysicsSimulation(ref RenderingData renderingData)
    {
        if (!Application.isPlaying) return;

        int initKernel = settings.particleCompute.FindKernel("InitParticles");
        int updateKernel = settings.particleCompute.FindKernel("UpdateParticles");

        if (!isInitialized)
        {
            settings.particleCompute.SetInt(GridSizeId, settings.gridSize);
            settings.particleCompute.SetBuffer(initKernel, ParticlesId, particleBuffer);
            int threadGroupsInit = Mathf.CeilToInt((settings.gridSize * settings.gridSize) / 64.0f);
            settings.particleCompute.Dispatch(initKernel, threadGroupsInit, 1, 1);
            isInitialized = true;
        }

        Camera cam = renderingData.cameraData.camera;
        Vector2 targetMousePos = new Vector2(Input.mousePosition.x / cam.pixelWidth, Input.mousePosition.y / cam.pixelHeight);
        smoothedMousePos = Vector2.SmoothDamp(smoothedMousePos, targetMousePos, ref smoothPosVelocity, settings.smoothTime);

        settings.particleCompute.SetVector(ResolutionId, new Vector2(cam.pixelWidth, cam.pixelHeight));
        settings.particleCompute.SetVector(MousePosId, smoothedMousePos);
        settings.particleCompute.SetFloat(MouseForceId, settings.mouseForce);
        settings.particleCompute.SetFloat(DeltaTimeId, Time.deltaTime);
        settings.particleCompute.SetFloat(SpringStiffnessId, settings.springStiffness);
        settings.particleCompute.SetFloat(DampingId, settings.damping);

        settings.particleCompute.SetBuffer(updateKernel, ParticlesId, particleBuffer);
        int threadGroupsUpdate = Mathf.CeilToInt((settings.gridSize * settings.gridSize) / 64.0f);
        settings.particleCompute.Dispatch(updateKernel, threadGroupsUpdate, 1, 1);
    }

    // =========================================================================
    // 渲染图通道 (Render Graph Pass) : 流体三步走管线
    // =========================================================================
    class LiquidFluidPass : ScriptableRenderPass
    {
        private HalftoneFluidFeature feature;
        private MaterialPropertyBlock mpb = new MaterialPropertyBlock();

        private class PassData
        {
            public TextureHandle activeColor;
            public TextureHandle bgCopy;
            public TextureHandle fluidRT;
            
            public Material blobMaterial;
            public Material compositeMaterial;
            public ComputeBuffer particles;
            public Mesh hexMesh;
            public int particleCount;
            public Vector2 resolution;
            public float dotSize;
            public MaterialPropertyBlock propertyBlock;
        }

        public LiquidFluidPass(HalftoneFluidFeature feature)
        {
            this.feature = feature;
            this.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.cameraType != CameraType.Game) return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            TextureHandle activeColor = resourceData.activeColorTexture;
            if (!activeColor.IsValid()) return;

            // 创建所需的全屏纹理
            RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            
            TextureHandle bgCopy = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "Fluid_BGCopy", false);
            
            // 为了性能优化，能量图不需要极其高精度的格式，标准的颜色格式即可
            TextureHandle fluidRT = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "Fluid_HeightMap", false);

            // ----------------------------------------------------------------
            // Step 1: 拷贝背景原图
            // ----------------------------------------------------------------
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Fluid: Copy BG", out var passData))
            {
                passData.activeColor = activeColor;
                builder.UseTexture(activeColor, AccessFlags.Read);
                builder.SetRenderAttachment(bgCopy, 0, AccessFlags.Write);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, data.activeColor, new Vector4(1, 1, 0, 0), 0.0f, false);
                });
            }

            // ----------------------------------------------------------------
            // Step 2: 绘制软球能量场 (加法混合)
            // ----------------------------------------------------------------
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Fluid: Draw Energy Blobs", out var passData))
            {
                passData.blobMaterial = feature.settings.blobMaterial;
                passData.particles = feature.particleBuffer;
                passData.hexMesh = feature.hexagonMesh;
                passData.particleCount = feature.settings.gridSize * feature.settings.gridSize;
                passData.resolution = new Vector2(cameraData.camera.pixelWidth, cameraData.camera.pixelHeight);
                passData.dotSize = feature.settings.dotBaseSize;
                passData.propertyBlock = this.mpb;

                builder.SetRenderAttachment(fluidRT, 0, AccessFlags.Write);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    // 核心：必须将能量场清空为纯黑 (能量为0)
                    context.cmd.ClearRenderTarget(false, true, Color.black);

                    data.propertyBlock.Clear();
                    data.propertyBlock.SetBuffer(ParticlesId, data.particles);
                    data.propertyBlock.SetFloat(DotBaseSizeId, data.dotSize);
                    data.propertyBlock.SetVector(ResolutionId, data.resolution);

                    // 实例化绘制所有加法软球
                    context.cmd.DrawMeshInstancedProcedural(data.hexMesh, 0, data.blobMaterial, 0, data.particleCount, data.propertyBlock);
                });
            }

            // ----------------------------------------------------------------
            // Step 3: 全屏流体融合与折射合成
            // ----------------------------------------------------------------
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Fluid: Composite & Refract", out var passData))
            {
                passData.bgCopy = bgCopy;
                passData.fluidRT = fluidRT;
                passData.compositeMaterial = feature.settings.compositeMaterial;

                builder.UseTexture(bgCopy, AccessFlags.Read);
                builder.UseTexture(fluidRT, AccessFlags.Read);
                builder.SetRenderAttachment(activeColor, 0, AccessFlags.Write);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    // 不要使用 cmd.SetGlobalTexture
                    // 而是创建一个临时属性块，或者直接利用材质
                    // 因为 Blitter.BlitTexture 支持传入 MaterialPropertyBlock (虽然 URP 接口有时比较隐晦)
    
                    // 最简单直接的方法：直接操作材质实例（在 Render Graph 允许范围内）
                    // 或者更严谨地，将纹理设置为该材质的属性
                    data.compositeMaterial.SetTexture(FluidHeightMapId, data.fluidRT);
    
                    // 执行合成
                    Blitter.BlitTexture(context.cmd, data.bgCopy, new Vector4(1, 1, 0, 0), data.compositeMaterial, 0);
                });
            }
        }
    }
}