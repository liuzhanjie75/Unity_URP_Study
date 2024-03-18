Shader "Custom/CustomPBR"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _MetallicGlossMap("Metallic", 2D) = "white" {}
        _DetailNormalMapScale("Scale", Range(0.0, 2.0)) = 1.0
        [Normal] _DetailNormalMap("Normal Map", 2D) = "bump" {}

    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalRenderPipeline" 
            "RenderType"="Opaque"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        #pragma shader_feature_include _ USE_HIGH_PRECISION

        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            float _Smoothness;
        CBUFFER_END

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_MetallicGlossMap);
        SAMPLER(sampler_MetallicGlossMap);
        TEXTURE2D(_DetailNormalMap);
        SAMPLER(sampler_DetailNormalMap);

        struct Attributes
        {
            float4 positionOS:POSITION;
            float4 normalOS:NORMAL;
            float2 texcoord:TEXCOORD;
            float4 tangent: TANGENT;
        };

        struct Varyings
        {
            float4 positionCS:SV_POSITION;
            float2 texcoord:TEXCOORD;
            float3 positionWS:TEXCOORD1;
            float3 normalWS:TEXCOORD2;
            float3 tangent: TEXCOORD3;
            float3 bitangent: TEXCOORD4;
        };

        float3 lambertDiffuse(float3 albedo)
        {
            return albedo / PI;
        }

        float3 fresnelSchlick(float cosTheta, float3 F0)
        {
            return F0 + (float3(1.0, 1.0, 1.0) -F0) * pow(saturate(1.0 - cosTheta), 5.0);
        }

        float3 fresnelSchlickRoughness(float cosTheta, float3 F0, float roughness)
        {
            return F0 + (max((float3(1.0, 1.0, 1.0) - roughness), F0) -F0) * pow(saturate(1.0 - cosTheta), 5.0);
        }   

        float DistributionGGX(float3 N, float3 H, float roughness)
        {
            float a = roughness * roughness;
            float a2 = a * a;
            float NdotH = max(dot(N, H), 0.0);
            float NdotH2 = NdotH * NdotH;

            float num = a2;
            float denom = (NdotH2 * (a2 - 1.0) + 1.0);
            denom = PI * denom * denom;

            return num / denom;
        }

        float GeometrySchlickGGX(float NdotV, float roughness)
        {
            float r = roughness + 1.0;
            float k = (r * r) / 8.0f;

            float num = NdotV;
            float denom = NdotV * (1.0 - k) + k;

            return num / denom;
        }

        float GeometrySmith(float3 N, float3 V, float3 L, float roughness)
        {
            float NdotV = max(dot(N, V), 0.0);
            float NdotL = max(dot(N, L), 0.0);

            float ggx2 = GeometrySchlickGGX(NdotV, roughness);
            float ggx1 = GeometrySchlickGGX(NdotL, roughness);

            return ggx1 * ggx2;
        }

        
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
                o.texcoord = i.texcoord;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS.xyz, true);
                VertexPositionInputs positionInputs = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionWS = positionInputs.positionWS;
                o.positionCS = positionInputs.positionCS;
                o.tangent = normalize(mul(UNITY_MATRIX_M, i.tangent).xyz);
                o.bitangent = normalize(cross(o.normalWS, o.tangent.xyz));
                return o;
            }

            float3 GetNormalFromMap(float3 worldPos, float3 norm, float2 uv)
            {
                float3 normal = SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, uv).bgr * 2 - 1;
                float3 Q1 = ddx(worldPos);
                float3 Q2 = ddy(worldPos);
                float2 st1 = ddx(uv);
                float2 st2 = ddy(uv);
            
                float3 N   = normalize(norm);
                float3 T  = normalize(Q1 * st2.y - Q2 * st1.y);
                float3 B  = -normalize(cross(N, T));
                float3x3 TBN = float3x3(T, B, N);

                return normalize(mul(TBN, normal));
            }

            real4 frag(Varyings i):SV_TARGET
            {
                Light light = GetMainLight();
                
                //float3 N = normalize(i.normalWS);
                float3 V = normalize(GetCameraPositionWS() - i.positionWS);
                float3 L = normalize(light.direction);
                float3 H = normalize(V + L);
                float3 N = SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, i.texcoord).bgr * 2.0 - 1.0;
                float3x3 tangentMatrix = transpose(float3x3(i.tangent, i.bitangent, i.normalWS));
                N = mul(tangentMatrix, N);
                N = normalize(N);
                
                float4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.texcoord);
                albedo = pow(albedo, float4(2.2, 2.2, 2.2, 2.2));
                float metallic = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, i.texcoord).r;
                metallic = saturate(metallic);
                float roughness = 1.0 - _Smoothness;
                roughness = max(roughness * roughness, HALF_MIN_SQRT);
                float3 radiance = light.color;

                // Cook-Torrance BRDF
                float3 F0 = float3(0.04, 0.04, 0.04);
                F0 = lerp(F0, albedo, metallic);
                float3 F = fresnelSchlick(max(dot(H, V), 0.0), F0);
                float NDF = DistributionGGX(N, H, roughness);
                float G = GeometrySmith(N, V, L, roughness);

                float NdotL = max(dot(N, L), 0.0);
                float NdotV = max(dot(N, V), 0.0);
                float3 specular = NDF * G * F / 4.0 * NdotV * NdotL + 0.0001;

                float3 kS = F;
                float3 kD = float3(1.0, 1.0, 1.0) - kS;
                kD *= 1.0 - metallic;
                float3 diffuse = lambertDiffuse(albedo) * kD;
                float3 brdfDirect = (diffuse + specular) * radiance * NdotL;

                // ibl 的结果有问题，先屏蔽
                float3 diffuseIrradiance = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, N, UNITY_SPECCUBE_LOD_STEPS).rgb;
                diffuseIrradiance = pow(diffuseIrradiance, float4(2.2, 2.2, 2.2, 2.2));
                kS = fresnelSchlickRoughness(NdotV, F0, roughness);
                kD = float3(1.0, 1.0, 1.0) - kS;
                kD *= 1.0 - metallic;
                float3 ambientDiffuse = diffuseIrradiance * lambertDiffuse(albedo) * kD;
                
                float3 reflectVec = -reflect(V, N);
                float3 specularIrradiance = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, reflectVec, roughness * UNITY_SPECCUBE_LOD_STEPS).rgb;
                specularIrradiance = pow(specularIrradiance, float4(2.2, 2.2, 2.2, 2.2));
                float3 ambientSpecular = specularIrradiance * F;

                float3 color = ambientDiffuse + ambientSpecular + brdfDirect;
                // ambient lighting (note that the next IBL tutorial will replace 
                // this ambient lighting with environment lighting).
                color = brdfDirect + albedo * 0.04;
                
                color = color / (color + float3(1.0, 1.0, 1.0));
                color = pow(color, float3(1.0, 1.0, 1.0) / 2.2);

                
                return float4(color, 1.0);
            }
            ENDHLSL
        }

    }

}