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
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE2D(_MainTex);
            SAMPLER(sampler__MainTex);
            float4 _SourceSize;
            

            inline float CalculatorMipmapDepth(float2 uv)
            {
                float4 depth;
                float offset = _SourceSize.x / 2;
                depth.x = SAMPLE_TEXTURE2D(_MainTex, sampler__MainTex, uv).r;
                depth.y = SAMPLE_TEXTURE2D(_MainTex, sampler__MainTex, uv + float2(0, offset)).r;
                depth.z = SAMPLE_TEXTURE2D(_MainTex, sampler__MainTex, uv + float2(offset, 0)).r;
                depth.w = SAMPLE_TEXTURE2D(_MainTex, sampler__MainTex, uv + float2(offset, offset)).r;
                #if defined(UNITY_REVERSED_Z)
                    return min(min(depth.x, depth.y), min(depth.z, depth.w));
                #else
                    return max(max(depth.x, depth.y), max(depth.z, depth.w));
                #endif
            }
            

            float4 Frag(Varyings input) : SV_Target
            {
                //float depth = CalculatorMipmapDepth(input.texcoord);
                float depth = SAMPLE_TEXTURE2D(_MainTex, sampler__MainTex, input.texcoord).r;
                return float4(depth, 0, 0, 1.0f);
            }
            ENDHLSL
        }
    }
}