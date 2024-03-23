Shader "Hidden/DepthTextureMipmap"
{

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
            
            float _HierarchicalZBufferTextureFromMipLevel;
            float _HierarchicalZBufferTextureToMipLevel;
            float4 _SourceSize;

            half4 GetSource(half2 uv, float2 offset = 0.0, float mipLevel = 0.0)
            {
                offset *= _SourceSize.zw;
                return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearRepeat, uv + offset, mipLevel);
            }

            inline float CalculatorMipmapDepth(float2 uv)
            {
                float4 depth;
                float2 invSize = _SourceSize.xy;
                depth.x = GetSource(uv, float2(-1, -1), _HierarchicalZBufferTextureFromMipLevel);
                depth.y = GetSource(uv, float2(-1, 1), _HierarchicalZBufferTextureFromMipLevel);
                depth.z = GetSource(uv, float2(1, -1), _HierarchicalZBufferTextureFromMipLevel);
                depth.w = GetSource(uv, float2(1, 1), _HierarchicalZBufferTextureFromMipLevel);
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