Shader "Hidden/LiquidComposite"
{
    Properties
    {
        _BaseColor("Empty Space Color", Color) = (0.1, 0.1, 0.1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // 必须引入，它处理了全屏三角形的顶点逻辑
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_FluidHeightMap); // 我们的加法能量图
            SAMPLER(sampler_FluidHeightMap);

            float4 _BaseColor;

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                // 1. 采样能量场
                half energy = SAMPLE_TEXTURE2D(_FluidHeightMap, sampler_FluidHeightMap, uv).r;

                // 2. 表面张力 Mask
                // 阈值范围：0.1 到 0.2 之间进行平滑，让边缘有肉感
                half waterMask = smoothstep(0.1h, 0.2h, energy);

                // --- 逻辑分支 ---
                // 如果没有水滴，直接返回你要求的纯色
                if (waterMask < 0.001h)
                {
                    return _BaseColor;
                }

                // 3. 计算法线 (基于能量梯度)
                float delta = 0.003; // 偏移步长，越大水珠边缘越圆润
                float eL = SAMPLE_TEXTURE2D(_FluidHeightMap, sampler_FluidHeightMap, uv + float2(-delta, 0)).r;
                float eR = SAMPLE_TEXTURE2D(_FluidHeightMap, sampler_FluidHeightMap, uv + float2(delta, 0)).r;
                float eU = SAMPLE_TEXTURE2D(_FluidHeightMap, sampler_FluidHeightMap, uv + float2(0, delta)).r;
                float eD = SAMPLE_TEXTURE2D(_FluidHeightMap, sampler_FluidHeightMap, uv + float2(0, -delta)).r;

                // 构建法线，Z轴系数 (0.1) 越小，折射越剧烈
                float3 normal = normalize(float3(eL - eR, eD - eU, 0.1));

                // 4. 水底折射背景
                // 使用法线的 xy 偏移来采样背景图
                float2 refractUV = uv + normal.xy * 0.05;
                half3 refractedBg = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, refractUV).rgb;

                // 5. 添加简单的高光质感
                float3 lightDir = normalize(float3(1.0, 1.0, 1.0));
                float spec = pow(max(0, dot(normal, lightDir)), 25.0);
                
                // 最终混合：纯色背景 -> 经过折射的背景
                // 这样水滴边缘会和纯色有一个平滑的过渡
                half3 finalColor = lerp(_BaseColor.rgb, refractedBg + spec * 0.5, waterMask);

                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
}