Shader "Custom/CustomPBR"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)
        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _MetallicGlossMap("Metallic", 2D) = "white" {}
        _DetailNormalMapScale("Scale", Range(0.0, 2.0)) = 1.0
        [Normal] _DetailNormalMap("Normal Map", 2D) = "bump" {}

    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalRenderPipeline" "RenderType"="Opaque"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        #pragma shader_feature_include _ USE_HIGH_PRECISION

        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
        CBUFFER_END

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        struct Attributes
        {
            float4 positionOS:POSITION;
            float4 color:COLOR;
            float4 normalOS:NORMAL;
            float2 texcoord:TEXCOORD;
        };

        struct Varyings
        {
            float4 positionCS:SV_POSITION;
            float4 color:COLOR;
            float2 texcoord:TEXCOORD;
            float3 viewDirWS:TEXCOORD2;
            float3 normalWS:TEXCOORD3;
        };
        ENDHLSL

        pass
        {
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.color = i.color;
                o.texcoord = i.texcoord;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS.xyz, true);
                VertexPositionInputs positionInputs = GetVertexPositionInputs(i.positionOS.xyz);
                o.viewDirWS = GetCameraPositionWS() - positionInputs.positionWS;
                return o;
            }

            real4 frag(Varyings i):SV_TARGET
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.texcoord);
                return albedo * _BaseColor;
            }
            ENDHLSL
        }

    }

}