Shader "Custom/HalftoneSplatMobile"
{
    Properties
    {
        _DotBaseSize ("Base Dot Size", Float) = 0.01
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" "RenderPipeline"="UniversalPipeline" }
        // Premultiplied Alpha Blend for better mobile composite
        Blend One OneMinusSrcAlpha 
        ZWrite Off ZTest Always Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing 
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Particle
            {
                float4 posData;
                float4 velData;
            };

            StructuredBuffer<Particle> _Particles;
            
            float _DotBaseSize;
            float2 _Resolution;
            TEXTURE2D(_BlitTexture);

            struct Attributes
            {
                float4 positionOS : POSITION; 
                float2 uv : TEXCOORD0;        
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_Position;
                half2 uv : TEXCOORD0;         
                half3 color : COLOR;          
                half  size : TEXCOORD1;       
                half2 velocityUV : TEXCOORD2; // 【新增】传递速度到片元用于色差
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);

                Particle p = _Particles[input.instanceID];
                float2 currentPos = p.posData.xy;
                float2 originalPos = p.posData.zw;
                float2 velocity = p.velData.xy;

                half3 srcColor = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_LinearClamp, originalPos, 0).rgb;
                half luma = dot(half3(0.2126h, 0.7152h, 0.0722h), srcColor);
                half darkness = smoothstep(0.0h, 3.0h, 1.0h - luma);
                float finalSize = darkness > 0.175h ? darkness * _DotBaseSize * p.velData.z : 0.0;

                // ==========================================
                // 【核心张力 1：基于速度的形变与旋转 (Squash & Stretch)】
                // ==========================================
                float speed = length(velocity);
                
                // 1. 获取运动方向 (容错处理，避免除以0)
                float2 dir = speed > 0.001 ? velocity / speed : float2(1.0, 0.0);
                
                // 2. 构建 2D 旋转矩阵，将网格的 X 轴对齐到速度方向
                float2x2 rotMatrix = float2x2(dir.x, -dir.y, dir.y, dir.x);
                
                // 3. 计算拉伸系数 (速度越快，X拉得越长，Y压得越扁)
                // 乘数 10.0 和 5.0 是张力系数，可提取为材质参数
                float stretchX = 1.0 + speed * 10.0; 
                float stretchY = 1.0 / (1.0 + speed * 5.0); 

                // 4. 应用形变与旋转到局部坐标
                float2 modifiedOS = input.positionOS.xy;
                modifiedOS.x *= stretchX;
                modifiedOS.y *= stretchY;
                modifiedOS = mul(rotMatrix, modifiedOS);

                // ------------------------------------------

                float2 aspect = float2(1.0, _Resolution.x / _Resolution.y);
                float2 vertexPosUV = currentPos + modifiedOS * finalSize * aspect;

                output.positionCS = float4(vertexPosUV * 2.0 - 1.0, 0.0, 1.0);
                
                #if UNITY_UV_STARTS_AT_TOP
                output.positionCS.y = -output.positionCS.y;
                #endif

                // 传递局部形变 UV (让内部画圆的逻辑也跟着拉伸)
                output.uv = (half2)modifiedOS;
                output.color = srcColor;
                output.size = (half)finalSize;
                
                // 【新增】将速度映射到局部 UV 空间，传递给 Fragment 做色差偏移
                // 乘数 0.5 决定了色差撕裂的宽度
                output.velocityUV = (half2)(velocity * 0.5); 
                
                return output;
            }

            // 提取画空心圆的复用函数
            half GetRingAlpha(half2 uv, half fw)
            {
                half dist = length(uv);
                half outer = 1.0h - smoothstep(1.0h - fw, 1.0h + fw, dist);
                half inner = smoothstep(0.6h - fw, 0.6h + fw, dist);
                return outer * inner;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                if (input.size <= 0.001h) discard;

                half fw = 0.05h; 
                
                // ==========================================
                // 【核心张力 2：动能色板撕裂 (Kinetic Chromatic Aberration)】
                // ==========================================
                // 基于速度方向，计算红、绿、蓝三个通道各自的 UV 偏移
                half2 uvR = input.uv + input.velocityUV;
                half2 uvG = input.uv;
                half2 uvB = input.uv - input.velocityUV;

                // 分别绘制三个颜色的圆环遮罩
                half alphaR = GetRingAlpha(uvR, fw);
                half alphaG = GetRingAlpha(uvG, fw);
                half alphaB = GetRingAlpha(uvB, fw);

                // 取最大 Alpha 作为该像素的最终覆盖率
                half finalAlpha = max(alphaR, max(alphaG, alphaB));
                if (finalAlpha < 0.01h) discard;

                // 将原图色彩拆分为 RGB，并乘上各自的遮罩
                // 当处于错位边缘时，会出现纯净的红、绿、蓝色块；重合处则还原原图色彩
                half3 splitColor = half3(
                    input.color.r * alphaR,
                    input.color.g * alphaG,
                    input.color.b * alphaB
                );

                // 为了防止多色重合时亮度丢失，进行一个简单的能量守恒补偿
                half sumAlpha = alphaR + alphaG + alphaB;
                half3 finalColor = sumAlpha > 0.0h ? (splitColor) : half3(0,0,0);

                // 输出 (注意这里使用 Premultiplied Alpha 与 C# 端的灰色背景进行完美融合)
                return half4(finalColor * finalAlpha, finalAlpha);
            }
            ENDHLSL
        }
    }
}