Shader "Custom/LiquidBlobSplat"
{
    Properties 
    { 
        _DotBaseSize ("Base Dot Size", Float) = 0.015 
    }
    SubShader
    {
        // 关键：RenderType 设为 Transparent，Queue 设为 Transparent+10
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10" "RenderPipeline"="UniversalPipeline" }
        
        // 核心：必须是加法混合，且关闭深度写入
        Blend One One 
        ZWrite Off 
        ZTest Always 
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            
            // 必须开启实例化
            #pragma multi_compile_instancing 
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Particle { float4 posData; float4 velData; };
            
            // 确保 C# 传递的 Buffer 名字对齐
            StructuredBuffer<Particle> _Particles;
            
            float _DotBaseSize;
            float2 _Resolution;

            struct Attributes
            {
                float4 positionOS : POSITION; 
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID; // 实例化 ID
            };

            struct Varyings
            {
                float4 positionCS : SV_Position;
                half2 localUV : TEXCOORD0;         
                uint instanceID : SV_InstanceID;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                
                // 【修正 1】必须初始化实例化 ID，否则无法正确访问 StructuredBuffer
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // 获取粒子数据
                Particle p = _Particles[input.instanceID];
                float2 currentPos = p.posData.xy; // 期望 0~1 范围
                
                // 水珠大小：_DotBaseSize 控制基础，可以稍微放大点方便融合
                float finalSize = _DotBaseSize * 2.0; 

                // 屏幕比例矫正
                float2 aspect = float2(1.0, _Resolution.x / _Resolution.y);
                
                // 将 0~1 的 currentPos 映射到顶点位移
                // input.positionOS.xy 是六边形网格的局部坐标（-1 到 1）
                float2 vertexPosUV = currentPos + input.positionOS.xy * finalSize * aspect;

                // 映射到裁切空间 (-1 到 1)
                output.positionCS = float4(vertexPosUV * 2.0 - 1.0, 0.0, 1.0);
                
                #if UNITY_UV_STARTS_AT_TOP
                output.positionCS.y = -output.positionCS.y;
                #endif

                output.localUV = (half2)input.positionOS.xy;
                
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // 【修正 2】更柔和的能量场函数 (Metaball 核心)
                // 距离中心越近，能量越强
                half dist = length(input.localUV);
                
                // 使用 (1 - r^2)^3 的公式，这是 Metaball 最经典的平滑衰减公式
                // 它比普通的 smoothstep 在多个球体融合时看起来更像液体
                half r = saturate(dist);
                half energy = (1.0h - r * r);
                energy = energy * energy * energy; 

                // 如果 R 还是 204 不变，可以尝试输出 instanceID 的百分比来测试是否在循环
                // return half4(input.instanceID / 10000.0, 0, 0, 1); 

                // 输出到 R 通道进行加法叠加
                return half4(energy, 0, 0, 1.0h);
            }
            ENDHLSL
        }
    }
}