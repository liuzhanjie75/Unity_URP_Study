Shader "HiZ/InstanceGrass"
{
    Properties
    {
        [MainTexture] _MainTex("MainTex",2D)="White"{}
        [MainColor] _BaseColor("BaseColor",Color)=(1,1,1,1)
                
        [Header(Wind)]
        _WindAIntensity("_WindAIntensity", Float) = 1.77
        _WindAFrequency("_WindAFrequency", Float) = 4
        _WindATiling("_WindATiling", Vector) = (0.1,0.1,0)
        _WindAWrap("_WindAWrap", Vector) = (0.5,0.5,0)

        _WindBIntensity("_WindBIntensity", Float) = 0.25
        _WindBFrequency("_WindBFrequency", Float) = 7.7
        _WindBTiling("_WindBTiling", Vector) = (.37,3,0)
        _WindBWrap("_WindBWrap", Vector) = (0.5,0.5,0)


        _WindCIntensity("_WindCIntensity", Float) = 0.125
        _WindCFrequency("_WindCFrequency", Float) = 11.7
        _WindCTiling("_WindCTiling", Vector) = (0.77,3,0)
        _WindCWrap("_WindCWrap", Vector) = (0.5,0.5,0)
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
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            half4 _BaseColor;
        
            float _WindAIntensity;
            float _WindAFrequency;
            float2 _WindATiling;
            float2 _WindAWrap;

            float _WindBIntensity;
            float _WindBFrequency;
            float2 _WindBTiling;
            float2 _WindBWrap;

            float _WindCIntensity;
            float _WindCFrequency;
            float2 _WindCTiling;
            float2 _WindCWrap;

            StructuredBuffer<float4x4> positionBuffer;
        CBUFFER_END

        TEXTURE2D (_MainTex);
        SAMPLER(sampler_MainTex);

        struct Attributes
        {
            float4 positionOS:POSITION;
            float4 normalOS:NORMAL;
            float2 texcoord:TEXCOORD;
        };
        
        struct Varyings
        {
            float4 positionCS:SV_POSITION;
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
                #if SHADER_TARGET >= 45
                    float4x4 data = positionBuffer[instanceID];
                #else
                    float4x4 data = 0;
                #endif

                o.positionWS = mul(data, v.positionOS).xyz;
                float3 perGrassPivotPosWS = o.positionWS;
                float3 cameraTransformRightWS = UNITY_MATRIX_V[0].xyz;//UNITY_MATRIX_V[0].xyz == world space camera Right unit vector
                                //wind animation (biilboard Left Right direction only sin wave)            
                float wind = 0;
                wind += (sin(_Time.y * _WindAFrequency + perGrassPivotPosWS.x * _WindATiling.x + perGrassPivotPosWS.z * _WindATiling.y)*_WindAWrap.x+_WindAWrap.y) * _WindAIntensity; //windA
                wind += (sin(_Time.y * _WindBFrequency + perGrassPivotPosWS.x * _WindBTiling.x + perGrassPivotPosWS.z * _WindBTiling.y)*_WindBWrap.x+_WindBWrap.y) * _WindBIntensity; //windB
                wind += (sin(_Time.y * _WindCFrequency + perGrassPivotPosWS.x * _WindCTiling.x + perGrassPivotPosWS.z * _WindCTiling.y)*_WindCWrap.x+_WindCWrap.y) * _WindCIntensity; //windC
                wind *= v.positionOS.y; //wind only affect top region, don't affect root region
                float3 windOffset = cameraTransformRightWS * wind; //swing using billboard left right direction
            
                o.positionWS += windOffset;
                o.positionCS = mul(UNITY_MATRIX_VP, float4(o.positionWS, 1.0));
                o.texcoord = v.texcoord;
                return o;
            }

            float4 frag (Varyings i) : SV_Target
            {
                float4 albedo = SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,i.texcoord);
                Light light =  GetMainLight();
                albedo = albedo * _BaseColor;
                albedo.rgb *= light.color;
                return albedo;
            }
            ENDHLSL
        }
    }
}
