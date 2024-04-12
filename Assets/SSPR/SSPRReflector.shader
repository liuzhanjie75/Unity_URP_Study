Shader "SSPR/SSPRReflector"
{
    Properties
    {
    }
    
    SubShader
    {
        Tags{
            "Queue"="Overlay"
            "RenderPipeline"="UniversalRenderPipeline"
        }
        LOD 100

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        ENDHLSL

        Pass
        {
            Name "SSPR Reflector Pass"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            //textures
            TEXTURE2D(_SSPRReflectionTexture);
            SAMPLER(sampler_SSPRReflectionTexture);

            //cbuffer
            CBUFFER_START(UnityPerMaterial)

            CBUFFER_END
            
            struct Attributes
            {
                float4 positionOS   : POSITION;
                float2 uv           : TEXCOORD0;
            };

            struct Varyings
            {
                float2 uv           : TEXCOORD0;
                float4 positionNDC  : TEXCOORD1;
                float4 positionCS   : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs vertexInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInputs.positionCS;
                output.positionNDC = vertexInputs.positionNDC;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float2 suv = input.positionNDC.xy / input.positionNDC.w;
                half3 finalRGB = SAMPLE_TEXTURE2D(_SSPRReflectionTexture, sampler_SSPRReflectionTexture, suv);

                return float4(finalRGB, 1);
            }
            ENDHLSL
        }
    }
}
