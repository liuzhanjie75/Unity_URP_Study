Shader "HiZ/DepthTextureMipmapCalculator"
{
    Properties
    {
        [HideInInspector] _MainTex("Previous Mipmap", 2D) = "black" {}
    }
    SubShader
    {
        Tags{
            "RenderPipeline"="UniversalRenderPipeline"
        }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            //#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE2D(_MainTex);
            SAMPLER(sampler__MainTex);
            float4 _SourceSize;
            float4 _MainTex_ST;

            struct Attributes
            {
                float4 positionOS:POSITION;
                float2 texcoord:TEXCOORD;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 texcoord : TEXCOORD0;
            };

            Varyings Vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.texcoord = TRANSFORM_TEX(i.texcoord, _MainTex);

                return o;
            }

            inline float CalculatorMipmapDepth(float2 uv)
            {
                float4 depth;
                float2 invSize = _SourceSize.xy;
                depth.x = SAMPLE_TEXTURE2D(_MainTex, sampler__MainTex, uv + float2(-0.5f, -0.5f) * invSize);
                depth.y = SAMPLE_TEXTURE2D(_MainTex, sampler__MainTex, uv + float2(0.5f, -0.5f) * invSize);
                depth.z = SAMPLE_TEXTURE2D(_MainTex, sampler__MainTex, uv + float2(-0.5f, 0.5f) * invSize);
                depth.w = SAMPLE_TEXTURE2D(_MainTex, sampler__MainTex, uv + float2(0.5f, 0.5f) * invSize);
                #if defined(UNITY_REVERSED_Z)
                    return min(min(depth.x, depth.y), min(depth.z, depth.w));
                #else
                    return max(max(depth.x, depth.y), max(depth.z, depth.w));
                #endif
            }
            

            float4 Frag(Varyings input) : SV_Target
            {
                float depth = CalculatorMipmapDepth(input.texcoord);
                return float4(depth, 0, 0, 1.0f);
            }
            ENDHLSL
        }
    }
}