Shader "Hidden/PostProcessing/Halftone"
{
    Properties
    {
        _GridSize ("Grid Size", Float) = 100.0
        _DotSize ("Dot Size (Max Radius)", Float) = 0.5
        _Smoothness ("Smoothness", Float) = 0.05
    }
    
    SubShader
    {
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        ENDHLSL

        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline"}
        LOD 100
        ZWrite Off Cull Off
        Pass
        {
            Name "NewBlitScriptableRenderPipelineShader"

            HLSLPROGRAM
            
            #pragma vertex Vert
            #pragma fragment Frag

            float _GridSize;
            float _DotSize;
            float _Smoothness;
            float _OffsetUV;
            float _LumaSize;

            float4 Frag (Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // aspect ratio ensure the circle is drawn perfectly round
                float2 uv = input.texcoord;
                float aspect = _ScreenParams.x / _ScreenParams.y;
                float2 aspectUV = uv * float2(aspect, 1.0);

                float2 gridUV = aspectUV * _GridSize;
                float2 cellId = floor(gridUV);
                float2 cellUv = frac(gridUV);

                float2 referenceUV = (cellId + 0.5) / _GridSize;
                referenceUV.x /= aspect;


                half4 referenceColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, referenceUV);
                half4 currentColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                float luma = dot(referenceColor.rgb, float3(0.2126, 0.7152, 0.0722)) * _LumaSize;
                float radius = _DotSize * (1.0 - luma);

                float dist = length(cellUv - 0.5);
                float circle = smoothstep(radius + _Smoothness, radius - _Smoothness, dist);

                half3 finalColor = lerp(0, referenceColor, circle);
                return half4(finalColor.rgb, 1.0);
            }
            
            ENDHLSL
        }
    }
}
