Shader "HiZ/InstanceGrass"
{
    Properties
    {
        [MainTexture] _MainTex("MainTex",2D)="White"{}
        [MainColor] _BaseColor("BaseColor",Color)=(1,1,1,1)
    }
    SubShader
    {
        Tags{
            "Queue"="Geometry"
            "RenderPipeline"="UniversalRenderPipeline"
            "RenderType"="Opaque"
        }
        LOD 100
        
        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            half4 _BaseColor;
            StructuredBuffer<float4x4> positionBuffer;
        CBUFFER_END

        TEXTURE2D (_MainTex);
        SAMPLER(sampler_MainTex);

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
            float3 positionWS:TEXCOORD2;
        };
        ENDHLSL

        Pass
        {
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag


            Varyings vert (Attributes v, uint instanceID : SV_InstanceID)
            {
                Varyings o;
                unity_ObjectToWorld = positionBuffer[instanceID];
                //unity_WorldToObject = unity_ObjectToWorld;
                //unity_WorldToObject._14_24_34 *= -1;
				//unity_WorldToObject._11_22_33 = 1.0f / unity_WorldToObject._11_22_33;
                o.positionWS = mul(unity_ObjectToWorld, v.positionOS).xyz;
                o.positionCS = mul(unity_MatrixVP, float4(o.positionWS, 1.0));
                o.color = v.color;
                return o;
            }

            float4 frag (Varyings i) : SV_Target
            {
                float4 albedo = SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,i.texcoord);
                albedo = albedo * i.color * _BaseColor;
                return albedo;
            }
            ENDHLSL
        }
    }
}
