using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class HalftoneMobileFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        [Header("Shaders")]
        public ComputeShader particleCompute;
        public Material splatMaterial;

        [Header("Grid & Physics (Mobile Optimized)")]
        [Tooltip("网格划分数量，移动端建议 128-180")]
        public int gridSize = 128;
        public float dotBaseSize = 0.012f;
        
        [Range(0.01f, 1f)] public float mouseForce = 0.5f;
        [Range(10f, 100f)] public float springStiffness = 50f;
        [Range(1f, 20f)] public float damping = 8f;
        [Range(0.01f, 0.5f)] public float smoothTime = 0.1f;
    }

    public Settings settings = new Settings();
    private HalftoneParticlePass renderPass;

    // 核心 VRAM 资源
    private ComputeBuffer particleBuffer;
    private Mesh hexagonMesh;
    private bool isInitialized = false;

    // 平滑输入的持久化状态
    private Vector2 smoothedMousePos;
    private Vector2 smoothPosVelocity;

    // Shader Property IDs
    private static readonly int ParticlesId = Shader.PropertyToID("_Particles");
    private static readonly int ResolutionId = Shader.PropertyToID("_Resolution");
    private static readonly int MousePosId = Shader.PropertyToID("_MousePos");
    private static readonly int MouseForceId = Shader.PropertyToID("_MouseForce");
    private static readonly int DeltaTimeId = Shader.PropertyToID("_DeltaTime");
    private static readonly int GridSizeId = Shader.PropertyToID("_GridSize");
    private static readonly int SpringStiffnessId = Shader.PropertyToID("_SpringStiffness");
    private static readonly int DampingId = Shader.PropertyToID("_Damping");
    private static readonly int DotBaseSizeId = Shader.PropertyToID("_DotBaseSize");
    private static readonly int BlitTextureId = Shader.PropertyToID("_BlitTexture");

    public override void Create()
    {
        if (settings.particleCompute == null || settings.splatMaterial == null) return;

        // 1. 生成紧凑的六边形几何体，拯救移动端填充率
        if (hexagonMesh == null) hexagonMesh = CreateHexagonMesh();

        // 2. 初始化 Compute Buffer (严格 32 字节对齐：2 个 float4)
        int totalParticles = settings.gridSize * settings.gridSize;
        if (particleBuffer == null || particleBuffer.count != totalParticles)
        {
            ReleaseResources(); // 先清理旧的
            particleBuffer = new ComputeBuffer(totalParticles, 32);
            isInitialized = false;
        }

        renderPass = new HalftoneParticlePass(this);
    }

    // 生命周期管理：极其重要，防止显存泄漏
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
        // 检查基础资产是否赋值
        if (settings.particleCompute == null || settings.splatMaterial == null) 
            return false;

        // 检查 Mesh 是否存活，死亡或未创建则原地复活
        if (hexagonMesh == null) 
        {
            hexagonMesh = CreateHexagonMesh();
        }

        // 检查 Buffer 是否存活及尺寸匹配
        int totalParticles = settings.gridSize * settings.gridSize;
        if (particleBuffer == null || particleBuffer.count != totalParticles)
        {
            if (particleBuffer != null) particleBuffer.Release();
            particleBuffer = new ComputeBuffer(totalParticles, 32);
            isInitialized = false;
        }

        return true;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!EnsureResources()) return;
        
        if (renderPass != null && particleBuffer != null)
        {
            // 在入队前更新物理模拟状态
            UpdatePhysicsSimulation(ref renderingData);
            renderer.EnqueuePass(renderPass);
        }
    }

    private void UpdatePhysicsSimulation(ref RenderingData renderingData)
    {
        // 仅在游戏运行时响应输入，防止编辑器模式下报错
        if (!Application.isPlaying) return;

        int initKernel = settings.particleCompute.FindKernel("InitParticles");
        int updateKernel = settings.particleCompute.FindKernel("UpdateParticles");

        // 首次运行：分发 Init Kernel
        if (!isInitialized)
        {
            settings.particleCompute.SetInt(GridSizeId, settings.gridSize);
            settings.particleCompute.SetBuffer(initKernel, ParticlesId, particleBuffer);
            
            // 使用 64 线程组大小适配移动端
            int threadGroupsInit = Mathf.CeilToInt((settings.gridSize * settings.gridSize) / 64.0f);
            settings.particleCompute.Dispatch(initKernel, threadGroupsInit, 1, 1);
            isInitialized = true;
        }

        // 处理鼠标/触摸平滑输入
        Camera cam = renderingData.cameraData.camera;
        Vector2 targetMousePos = new Vector2(Input.mousePosition.x / cam.pixelWidth, Input.mousePosition.y / cam.pixelHeight);
        smoothedMousePos = Vector2.SmoothDamp(smoothedMousePos, targetMousePos, ref smoothPosVelocity, settings.smoothTime);

        // 更新 Compute Shader 参数
        settings.particleCompute.SetVector(ResolutionId, new Vector2(cam.pixelWidth, cam.pixelHeight));
        settings.particleCompute.SetVector(MousePosId, smoothedMousePos);
        settings.particleCompute.SetFloat(MouseForceId, settings.mouseForce);
        settings.particleCompute.SetFloat(DeltaTimeId, Time.deltaTime);
        settings.particleCompute.SetFloat(SpringStiffnessId, settings.springStiffness);
        settings.particleCompute.SetFloat(DampingId, settings.damping);

        settings.particleCompute.SetBuffer(updateKernel, ParticlesId, particleBuffer);

        // 分发 Update Kernel 执行物理积分
        int threadGroupsUpdate = Mathf.CeilToInt((settings.gridSize * settings.gridSize) / 64.0f);
        settings.particleCompute.Dispatch(updateKernel, threadGroupsUpdate, 1, 1);
    }

    // 纯代码生成六边形网格
    private Mesh CreateHexagonMesh()
    {
        Mesh mesh = new Mesh { name = "Tight_Hexagon" };
        
        mesh.hideFlags = HideFlags.HideAndDontSave;
        
        Vector3[] vertices = new Vector3[7];
        Vector2[] uvs = new Vector2[7];

        vertices[0] = Vector3.zero; uvs[0] = Vector2.zero;
        for (int i = 0; i < 6; i++)
        {
            float angle = i * Mathf.PI / 3.0f;
            vertices[i + 1] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
            uvs[i + 1] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }

        int[] triangles = new int[] { 0,1,2, 0,2,3, 0,3,4, 0,4,5, 0,5,6, 0,6,1 };
        mesh.vertices = vertices; mesh.uv = uvs; mesh.triangles = triangles;
        mesh.UploadMeshData(true);
        return mesh;
    }

    // =========================================================================
    // RenderGraph Pass：绘制实例粒子
    // =========================================================================
    class HalftoneParticlePass : ScriptableRenderPass
    {
        private HalftoneMobileFeature feature;
        private MaterialPropertyBlock mpb = new MaterialPropertyBlock();

        private class PassData
        {
            public TextureHandle sourceColor;
            public Material material;
            public ComputeBuffer particles;
            public Mesh hexMesh;
            public int particleCount;
            public MaterialPropertyBlock propertyBlock; // 传入 PassData 避免闭包分配
        }

        public HalftoneParticlePass(HalftoneMobileFeature feature)
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

            // 1. 创建一张临时 RT，用于拷贝原画面，供粒子 Shader 采样亮度
            RenderTextureDescriptor copyDesc = cameraData.cameraTargetDescriptor;
            copyDesc.depthBufferBits = 0;
            TextureHandle copiedColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, copyDesc, "Halftone_BgCopy", false);

            // [Copy Pass]: 主画面 -> 临时 RT
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Halftone Copy BG", out var passData))
            {
                passData.sourceColor = activeColor;
                builder.UseTexture(activeColor, AccessFlags.Read);
                builder.SetRenderAttachment(copiedColor, 0, AccessFlags.Write);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, data.sourceColor, new Vector4(1, 1, 0, 0), 0.0f, false);
                });
            }

            // [Draw Pass]: 直接在主画面上使用 DrawMeshInstancedProcedural 绘制十万个六边形粒子
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Halftone Draw Particles", out var passData))
            {
                passData.material = feature.settings.splatMaterial;
                passData.particles = feature.particleBuffer;
                passData.hexMesh = feature.hexagonMesh;
                passData.particleCount = feature.settings.gridSize * feature.settings.gridSize;
                passData.sourceColor = copiedColor; 
            
                // 2. 将类级别的 mpb 引用传给 PassData
                passData.propertyBlock = this.mpb; 

                builder.UseTexture(copiedColor, AccessFlags.Read);
                builder.SetRenderAttachment(activeColor, 0, AccessFlags.Write);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    context.cmd.ClearRenderTarget(false, true, new Color(0.75f, 0.75f, 0.75f, 1.0f));
                    
                    // 3. 核心修改：清空并使用 MaterialPropertyBlock 赋值，绝不直接碰 data.material
                    data.propertyBlock.Clear();
                    data.propertyBlock.SetBuffer(ParticlesId, data.particles);
                    data.propertyBlock.SetTexture(BlitTextureId, data.sourceColor);
                    data.propertyBlock.SetFloat(DotBaseSizeId, feature.settings.dotBaseSize);
                    data.propertyBlock.SetVector(ResolutionId, new Vector2(cameraData.camera.pixelWidth, cameraData.camera.pixelHeight));

                    // 4. 将 mpb 作为第六个参数完美传入
                    context.cmd.DrawMeshInstancedProcedural(data.hexMesh, 0, data.material, 0, data.particleCount, data.propertyBlock);
                });
            }
        }
    }
}