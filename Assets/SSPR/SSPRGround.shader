Shader "SSPR/SSPRGround"
{
    Properties
    {
        [MainColor] _BaseColor("BaseColor", Color) = (1,1,1,1)
        [MainTexture] _BaseMap("BaseMap", 2D) = "black" {}

        _Roughness("_Roughness", range(0,1)) = 0.25 
        [NoScaleOffset]_SSPR_UVNoiseTex("_SSPR_UVNoiseTex", 2D) = "gray" {}
        _SSPR_NoiseIntensity("_SSPR_NoiseIntensity", range(-0.2,0.2)) = 0.0

        _UV_MoveSpeed("_UV_MoveSpeed (xy only)(for things like water flow)", Vector) = (0,0,0,0)

        [NoScaleOffset]_ReflectionAreaTex("_ReflectionArea", 2D) = "white" {}
    }
    
    SubShader
    {
        //if "LightMode"="MobileSSPR", this shader will only draw if MobileSSPRRendererFeature is on
        Tags{
            "LightMode"="MobileSSPR"
            "Queue"="Geometry"
            "RenderPipeline"="UniversalRenderPipeline"
            "RenderType"="Opaque"
        }
        LOD 100

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        //textures         
        TEXTURE2D(_MobileSSPR_ColorRT);
        sampler LinearClampSampler;

        struct ReflectionInput
        {
            float3 positionWS;
            float4 screenPos;
            float2 screenSpaceNoise;
            float roughness;
            float SSPR_Usage;
        };

        float3 GetResultReflection(ReflectionInput data)
        {
            //sample scene's reflection probe
            float3 viewWS = (data.positionWS - _WorldSpaceCameraPos);
            viewWS = normalize(viewWS);

            float3 reflectDirWS = viewWS * float3(1, -1, 1); //reflect at horizontal plane

            //call this function in Lighting.hlsl-> half3 GlossyEnvironmentReflection(half3 reflectVector, half perceptualRoughness, half occlusion)
            float3 reflectionProbeResult = GlossyEnvironmentReflection(reflectDirWS, data.roughness, 1);
            float4 SSPRResult = 0;
            #if _MobileSSPR
            half2 screenUV = data.screenPos.xy/data.screenPos.w;
            SSPRResult = SAMPLE_TEXTURE2D(_MobileSSPR_ColorRT,LinearClampSampler, screenUV + data.screenSpaceNoise); //use LinearClampSampler to make it blurry
            #endif

            //final reflection
            float3 finalReflection = lerp(reflectionProbeResult, SSPRResult.rgb, SSPRResult.a * data.SSPR_Usage);
            //combine reflection probe and SSPR

            return finalReflection;
        }
        ENDHLSL

        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MobileSSPR
            //================================================================================================

            //textures
            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            
            TEXTURE2D(_SSPR_UVNoiseTex);
            SAMPLER(sampler_SSPR_UVNoiseTex);
            TEXTURE2D(_ReflectionAreaTex);
            SAMPLER(sampler_ReflectionAreaTex);

            //cbuffer
            CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _SSPR_NoiseIntensity;
            float2 _UV_MoveSpeed;
            half _Roughness;
            CBUFFER_END
            
            struct Attributes
            {
                float4 positionOS   : POSITION;
                float2 uv           : TEXCOORD0;
            };

            struct Varyings
            {
                float2 uv           : TEXCOORD0;
                float4 screenPos    : TEXCOORD1;
                float3 positionWS   : TEXCOORD2;
                float4 positionCS   : SV_POSITION;
            };
            
            Varyings vert (Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap) + _Time.y * _UV_MoveSpeed;
                output.screenPos = ComputeScreenPos(output.positionCS);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                return output;
            }

            float4 frag (Varyings input) : SV_Target
            {
                //base color
                float3 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor.rgb;

                //noise texture
                float2 noise = SAMPLE_TEXTURE2D(_SSPR_UVNoiseTex,sampler_SSPR_UVNoiseTex, input.uv);
                noise = noise *2-1;
                noise.y = -abs(noise); //hide missing data, only allow offset to valid location
                noise.x *= 0.25;
                noise *= _SSPR_NoiseIntensity;

                //================================================================================================
                //GetResultReflection from SSPR

                ReflectionInput reflectionData;
                reflectionData.positionWS = input.positionWS;
                reflectionData.screenPos = input.screenPos;
                reflectionData.screenSpaceNoise = noise;
                reflectionData.roughness = _Roughness;
                reflectionData.SSPR_Usage = _BaseColor.a;

                float3 resultReflection = GetResultReflection(reflectionData);
                //================================================================================================

                //decide show reflection area
                half reflectionArea = SAMPLE_TEXTURE2D(_ReflectionAreaTex, sampler_ReflectionAreaTex, input.uv);

                float3 finalRGB = lerp(baseColor, resultReflection, reflectionArea);

                return float4(finalRGB,1);
            }
            ENDHLSL
        }
    }
}
