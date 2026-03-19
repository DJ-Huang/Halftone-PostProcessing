using UnityEngine;

public class MouseTrailController : MonoBehaviour
{
    public ComputeShader trailCompute;
    
    [Header("Trail Settings")]
    [Tooltip("轨迹图的分辨率，不需要太高，256即可")]
    public int resolution = 256; 
    [Range(0.01f, 0.5f)] public float brushRadius = 0.05f;
    [Range(0.5f, 0.999f)] public float decayRate = 0.92f;
    [Range(0.01f, 1f)] public float smoothTime = 0.1f;

    private RenderTexture trailRT;
    private int kernelHandle;
    
    private Vector2 smoothedMousePos;
    private Vector2 smoothPosVelocity; // SmoothDamp 内部使用的引用变量
    private Vector2 lastSmoothedPos;

    void Start()
    {
        // 创建支持负数且可随机读写的浮点型 RenderTexture (RGHalf 足够记录 XY 速度)
        trailRT = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RGHalf);
        trailRT.enableRandomWrite = true;
        trailRT.Create();

        kernelHandle = trailCompute.FindKernel("UpdateTrail");
        
        // 记录初始鼠标位置
        Vector2 initialMouse = new Vector2(Input.mousePosition.x / Screen.width, Input.mousePosition.y / Screen.height);
        smoothedMousePos = initialMouse;
        lastSmoothedPos = initialMouse;
        
        // 全局传递给 Halftone Shader
        Shader.SetGlobalTexture("_BrushTexture", trailRT);
    }

    void Update()
    {
        // 1. 归一化鼠标位置 (0~1)
        Vector2 targetMousePos = new Vector2(Input.mousePosition.x / Screen.width, Input.mousePosition.y / Screen.height);
        // 2. 鼠标位置的阻尼平滑 (核心改进，完美还原 React Easing 手感)
        smoothedMousePos = Vector2.SmoothDamp(smoothedMousePos, targetMousePos, ref smoothPosVelocity, smoothTime);
        
        // 3. 基于平滑后的位置计算真实的 Delta 速度，并解绑帧率
        Vector2 currentVelocity = (smoothedMousePos - lastSmoothedPos) / Time.deltaTime;

        // 4. 传递数据给 Compute Shader
        trailCompute.SetFloat("_Radius", brushRadius);
        trailCompute.SetFloat("_Decay", decayRate);
        trailCompute.SetFloat("_DeltaTime", Time.deltaTime);
        trailCompute.SetVector("_Resolution", new Vector2(resolution, resolution));
        trailCompute.SetVector("_MousePos", smoothedMousePos);
        trailCompute.SetVector("_MouseVelocity", currentVelocity);
        
        trailCompute.SetTexture(kernelHandle, "Result", trailRT);

        // 5. 调度计算着色器
        int threadGroupsX = Mathf.CeilToInt(resolution / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(resolution / 8.0f);
        trailCompute.Dispatch(kernelHandle, threadGroupsX, threadGroupsY, 1);

        // 记录状态用于下一帧
        lastSmoothedPos = smoothedMousePos;
    }

    void OnDestroy()
    {
        if (trailRT != null)
        {
            trailRT.Release();
        }
    }
}