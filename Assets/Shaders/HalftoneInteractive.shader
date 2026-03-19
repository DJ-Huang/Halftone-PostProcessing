Shader "Custom/HalftoneInteractive"
{
    Properties
    {
        _PixelSize ("Pixel Size", Float) = 12.0
        _DotSize ("Dot Size", Float) = 2.5
        _MouseStrength ("Mouse Strength", Float) = 50.0
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
    float _PixelSize;
    float _DotSize;
    float _MouseStrength;
    TEXTURE2D(_BrushTexture);

    float random(float2 st)
    {
        return frac(sin(dot(st.xy, float2(12.9898, 78.233))) * 43758.5453123);
    }

    half4 Fragment(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        float2 uv = input.texcoord;
        float2 resolution = _ScreenParams.xy;
        float2 pixelCoord = uv * resolution;

        float2 baseCellIndex = floor(pixelCoord / _PixelSize);

        float3 maxCircleRGB = float3(0.0, 0.0, 0.0);
        const int searchRadius = 8; // 注意：81次循环较为昂贵，建议后续优化至 2 或基于 Hex Grid [cite: 13]

        for (int dy = -searchRadius; dy <= searchRadius; dy++)
        {
            for (int dx = -searchRadius; dx <= searchRadius; dx++)
            {
                float2 cellIndex = baseCellIndex + float2((float)dx, (float)dy);
                float2 cellOffset = fmod(abs(cellIndex.y), 2.0) < 0.5 ? float2(0.0, 0.0) : float2(0.5, 0.0);
                float2 cellCenter = (cellIndex + 0.5 + cellOffset) * _PixelSize;

                // 采样笔刷与缓动逻辑保持不变 [cite: 16, 20]
                float2 brushUV = cellCenter / resolution;
                half4 brush = SAMPLE_TEXTURE2D_LOD(_BrushTexture, sampler_LinearClamp, brushUV, 0);
                float2 brushVel = clamp(brush.rg, -0.45, 0.45);
                float brushIntensity = length(brushVel);

                float t = smoothstep(0.0, 0.5, brushIntensity);
                float easeIn = t * t * t;
                float easedIntensity = lerp(easeIn, t, 0.5);

                float2 forwardDir = brushIntensity > 0.001 ? brushVel / brushIntensity : float2(0.0, 0.0);
                float2 perpDir = float2(-forwardDir.y, forwardDir.x);
                float side = sign(random(cellCenter) - 0.5);

                // 计算基础位移
                float2 displacement = forwardDir * 0.75 + perpDir * side * 0.25;
                float2 totalDisplacement = displacement * _MouseStrength * easedIntensity;

                // 【新增逻辑】计算色偏最大影响
                float2 maxColorOffset = brushVel * 2.5 * _PixelSize;

                // 【核心修复】计算总位移量，并限制在安全半径内
                // 这里的 3.0 是安全网格数 (留出 1 个网格给圆环自身的半径)
                float maxSafeDistance = 3.0 * _PixelSize;

                // 限制鼠标推力的最大距离
                if (length(totalDisplacement) > maxSafeDistance)
                {
                    totalDisplacement = normalize(totalDisplacement) * maxSafeDistance;
                }
                
                cellCenter += totalDisplacement;

                // -------------------------------------------------------------
                // 核心亮点：动态色板剥离 (Dynamic Plate Misregistration)
                // 基于笔刷速度，计算 R 和 B 通道的额外空间偏移量
                // 乘数 2.5 控制色差的拉开距离，可提取为 _ChromaticAberration 暴露给面板
                // -------------------------------------------------------------
                float2 offsetR = brushVel * 1.5 * _PixelSize;
                float2 offsetB = -brushVel * 1.5 * _PixelSize;

                // 采样底层原图亮度保持不变 [cite: 22]
                float2 srcUV = saturate(cellCenter / resolution);
                half3 srcColor = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_LinearClamp, srcUV, 0).rgb;
                float luma = dot(float3(0.2126, 0.7152, 0.0722), srcColor);
                float darkness = smoothstep(0.0, 3.0, 1.0 - luma);

                if (darkness > 0.175)
                {
                    // 分别计算像素到三个“色板”中心的距离
                    float distR = length(pixelCoord - (cellCenter + offsetR));
                    float distG = length(pixelCoord - cellCenter);
                    float distB = length(pixelCoord - (cellCenter + offsetB));

                    float outerRadius = darkness * _DotSize * _PixelSize;
                    float ringThickness = _PixelSize * 0.25;
                    float innerRadius = max(outerRadius - ringThickness, 0.0);
                    float aa = 1.0;

                    // 计算三个颜色的圆环遮罩
                    float innerSmooth = innerRadius + aa;
                    float outerSmooth = outerRadius + aa;

                    float ringR = smoothstep(innerRadius - aa, innerSmooth, distR) * (1.0 - smoothstep(
                        outerRadius - aa, outerSmooth, distR));
                    float ringG = smoothstep(innerRadius - aa, innerSmooth, distG) * (1.0 - smoothstep(
                        outerRadius - aa, outerSmooth, distG));
                    float ringB = smoothstep(innerRadius - aa, innerSmooth, distB) * (1.0 - smoothstep(
                        outerRadius - aa, outerSmooth, distB));

                    // 将三个遮罩累计到 RGB 向量中
                    maxCircleRGB = max(maxCircleRGB, float3(ringR, ringG, ringB));
                }
            }
        }

        // 2. 最终的色彩混合：通道独立插值
        half4 referenceColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
        half3 paperColor = half3(0.75, 0.75, 0.75); // 背景亮灰纸张色 

        // 使用拆分后的遮罩，对 RGB 三个通道分别进行插值
        half3 finalColor;
        finalColor.r = lerp(paperColor.r, referenceColor.r, maxCircleRGB.r);
        finalColor.g = lerp(paperColor.g, referenceColor.g, maxCircleRGB.g);
        finalColor.b = lerp(paperColor.b, referenceColor.b, maxCircleRGB.b);
        return half4(finalColor, 1.0);
    }
    ENDHLSL

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline"
        }
        LOD 100
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "HalftoneInteractivePass"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Fragment
            ENDHLSL
        }
    }
}