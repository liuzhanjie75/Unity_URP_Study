Shader "Hidden/SSR"
{

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalRenderPipeline" 
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        
        #pragma multi_compile _ _JITTER_ON
        
        float4 _ProjectionParams2;
        float4 _CameraViewTopLeftCorner;
        float4 _CameraViewXExtent;
        float4 _CameraViewYExtent;
        float4 _SourceSize;
        float4 _SSRParams0;
        float4 _SSRParams1;

        // jitter dither map
        static half dither[16] = {
            0.0, 0.5, 0.125, 0.625,
            0.75, 0.25, 0.875, 0.375,
            0.187, 0.687, 0.0625, 0.562,
            0.937, 0.437, 0.812, 0.312
        };

        //float intensity = _SSRParams1.y;

        void swap(inout float v0, inout float v1)
        {
            float temp = v0;
            v0 = v1;
            v1 = temp;
        }

        float4 GetSource(float2 uv)
        {
            return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearRepeat, uv, _BlitMipLevel);
        }

        // 根据线性深度值和屏幕UV，还原世界空间下，相机到顶点的位置偏移向量
        float3 ReconstructViewPos(float2 uv, float linearEyeDepth)
        {
            // Screen is y-inverted
            uv.y = 1.0f - uv.y;
            
            // divide by near plane
            float scale = linearEyeDepth * _ProjectionParams2.x;
            float3 viewPos = _CameraViewTopLeftCorner.xyz + _CameraViewXExtent.xyz * uv.x + _CameraViewYExtent.xyz * uv.y;
            viewPos *= scale;
            return viewPos;
        }

        float3 ReconstructViewPos2(float2 uv, float deviceDepth)
        {
            float4 NdcPos = float4(uv * 2.0f - 1.0f, deviceDepth, 1.0f);
            NdcPos = mul(UNITY_MATRIX_I_P, NdcPos);
            NdcPos /= NdcPos.w;
            float3 viewPos = NdcPos.xyz;
            return viewPos;
        }

        void ReconStructUVAndDepth(float3 wpos, out float2 uv, out float depth)
        {
            float4 cpos = mul(UNITY_MATRIX_VP, wpos);
            uv = float2(cpos.x, cpos.y * _ProjectionParams.x) / cpos.w * 0.5f + 0.5f;
            depth = cpos.w;
        }

        // 从视角坐标转裁剪屏幕 ao 坐标
        float4 TransformViewToHScreen(float3 vpos, float2 screenSize)
        {
            float4 cpos = mul(UNITY_MATRIX_P, vpos);
            cpos.xy = float2(cpos.x, cpos.y * _ProjectionParams.x) * 0.5f + 0.5f * cpos.w;
            cpos.xy *= screenSize;
            return cpos;
        }

        #define INTENSITY 1.0

        half4 SSRFinalPassFragment(Varyings input) : SV_Target
        {
            return half4(GetSource(input.texcoord).rgb * INTENSITY, 1.0);
        }

        bool ScreenSpaceRayMarching(inout float2 P, inout float3 Q, inout float K, float2 dp, float3 dq, float dk,
                                    float rayZ, bool permute, out float depthDistance, inout float2 hitUV)
        {
            float rayZMin = rayZ;
            float rayZMax = rayZ;
            float preZ = rayZ;

            int step_count = _SSRParams0.z;
            UNITY_LOOP
            for (int i = 0; i < step_count; ++i)
            {
                P += dp;
                Q += dq;
                K += dk;

                rayZMin = preZ;
                rayZMax = (dq.z * 0.5 + Q.z) / (dk * 0.5 + K);
                preZ = rayZMax;
                if (rayZMin > rayZMax)
                    swap(rayZMin, rayZMax);

                hitUV = permute > 0.5 ? P.yx : P;
                hitUV *= _ScreenSize.zw;
                if (any(hitUV < 0.0) || any(hitUV > 1.0))
                    return false;

                float surfaceDepth = -LinearEyeDepth(SampleSceneDepth(hitUV), _ZBufferParams);
                bool isBehind = (rayZMin + 0.1 < surfaceDepth); // 加一个bias 防止stride过小，自反射  

                depthDistance = abs(surfaceDepth - rayZMax);
                if (isBehind)
                    return true;
            }

            return false;
        }

        bool BinarySearchRaymarching(float3 startView, float3 rDir, inout float2 hitUV)
        {
            float magnitude = _SSRParams0.x; // max_distance
            float end = startView.z + rDir.z * magnitude;
            if (end > -_ProjectionParams.y)
                magnitude = (-_ProjectionParams.y - startView.z) / rDir.z;
            float3 endView = startView + rDir * magnitude;

            // 齐次屏幕空间坐标 
            float4 startHScreen = TransformViewToHScreen(startView, _SourceSize.xy);
            float4 endHScreen = TransformViewToHScreen(endView, _SourceSize.xy);

            // inverse w
            float startK = 1.0 / startHScreen.w;
            float endk = 1.0 / endHScreen.w;

            // 结束屏幕空间坐标
            float2 startScreen = startHScreen.xy * startK;
            float2 endScreen = endHScreen.xy * endk;

            // 经过齐次除法的视角坐标
            float3 startQ = startView * startK;
            float3 endQ = endView * endk;

            // 根据斜率将 dx = 1, dy = delta
            float2 diff = endScreen - startScreen;
            bool permute = false;
            if (abs(diff.x) < abs(diff.y))
            {
                permute = true;
                diff = diff.yx;
                startScreen = startScreen.yx;
                endScreen = endScreen.yx;
            }

            // 计算屏幕坐标，齐次坐标， inverse-w 的线性增量
            float dir = sign(diff.x);
            float invdx = dir / diff.x;
            float2 dp = float2(dir, invdx * diff.y);
            float3 dq = (endQ - startQ) * invdx;
            float dk = (endk - startK) * invdx;

            float strid = _SSRParams0.y;
            dp *= strid;
            dq *= strid;
            dk *= strid;

            // 缓存当前的深度和位置
            float rayZ = startView.z;
 
            float2 P = startScreen;
            float3 Q = startQ;
            float K = startK;

            
            int binary_count = _SSRParams1.x;
            float thickness = _SSRParams0.w;
            float depthDistance = 0.0;
            UNITY_LOOP
            for (int i = 0; i < binary_count; i++)
            {
                #ifdef  _JITTER_ON
                float2 ditherUV = fmod(P, 4);
                float jitter = dither[ditherUV.x * 4 + ditherUV.y];
                P += dp * jitter;
                Q += dq * jitter;
                K += dk * jitter;
                #endif
                
                if (ScreenSpaceRayMarching(P, Q, K, dp, dq, dk, rayZ, permute, depthDistance, hitUV))
                {

                    if (depthDistance < thickness)
                        return true;
                    P -= dp;
                    Q -= dq;
                    K -= dk;
                    rayZ = Q / K;

                    dp *= 0.5;
                    dq *= 0.5;
                    dk *= 0.5;
                }
                else
                {
                    return false;
                }
            }

            return false;
        }

        float _MaxHierarchicalZBufferTextureMipLevel;
        TEXTURE2D(_HierarchicalZBufferTexture);
        SAMPLER(sampler_HierarchicalZBufferTexture);

        bool HierarchicalZScreenSpaceRayMarching(float3 startView, float3 rDir, inout float2 hitUV)
        {
            float magnitude = _SSRParams0.x; // max_distance
            float end = startView.z + rDir.z * magnitude;
            if (end > -_ProjectionParams.y)
                magnitude = (-_ProjectionParams.y - startView.z) / rDir.z;
            float3 endView = startView + rDir * magnitude;

            // 齐次屏幕空间坐标 
            float4 startHScreen = TransformViewToHScreen(startView, _SourceSize.xy);
            float4 endHScreen = TransformViewToHScreen(endView, _SourceSize.xy);

            // inverse w
            float startK = 1.0 / startHScreen.w;
            float endk = 1.0 / endHScreen.w;

            // 结束屏幕空间坐标
            float2 startScreen = startHScreen.xy * startK;
            float2 endScreen = endHScreen.xy * endk;

            // 经过齐次除法的视角坐标
            float3 startQ = startView * startK;
            float3 endQ = endView * endk;

            // 根据斜率将 dx = 1, dy = delta
            float2 diff = endScreen - startScreen;
            bool permute = false;
            if (abs(diff.x) < abs(diff.y))
            {
                permute = true;
                diff = diff.yx;
                startScreen = startScreen.yx;
                endScreen = endScreen.yx;
            }

            // 计算屏幕坐标，齐次坐标， inverse-w 的线性增量
            float dir = sign(diff.x);
            float invdx = dir / diff.x;
            float2 dp = float2(dir, invdx * diff.y);
            float3 dq = (endQ - startQ) * invdx;
            float dk = (endk - startK) * invdx;

            float strid = _SSRParams0.y;
            dp *= strid;
            dq *= strid;
            dk *= strid;

            // 缓存当前的深度和位置
            float rayZMin = startView.z;
            float rayZMax = startView.z;
            float preZ = startView.z;

            float2 P = startScreen;
            float3 Q = startQ;
            float K = startK;

            float thickness = _SSRParams0.w;
            int step_count = _SSRParams0.z;
            float mipLevel = 0.0;
            UNITY_LOOP
            for (int i = 0; i < step_count; i++)
            {
                // 步近
                P += dp * exp2(mipLevel);
                Q += dq * exp2(mipLevel);
                K += dk * exp2(mipLevel);

                // 得到步近前后两点的深度
                rayZMin = preZ;
                rayZMax = (dq.z * exp2(mipLevel) * 0.5 + Q.z) / (dk * exp2(mipLevel) * 0.5 + K);
                preZ = rayZMax;
                if (rayZMin > rayZMax)
                    swap(rayZMin, rayZMax);

                // 得到交点uv
                hitUV = permute ? P.yx : P;
                hitUV *= _ScreenSize.zw;

                if (any(hitUV < 0.0) || any(hitUV > 1.0))
                    return false;

                float rawDepth = SAMPLE_TEXTURE2D_X_LOD(_HierarchicalZBufferTexture, sampler_HierarchicalZBufferTexture,
                                                        hitUV, mipLevel);
                float surfaceDepth = -LinearEyeDepth(rawDepth, _ZBufferParams);

                bool behind = rayZMin + 0.1 <= surfaceDepth;

                if (!behind)
                {
                    mipLevel = min(mipLevel + 1, _MaxHierarchicalZBufferTextureMipLevel);
                }
                else
                {
                    if (mipLevel == 0)
                    {
                        if (abs(surfaceDepth - rayZMax) < thickness)
                        {
                            //return float4(hitUV, rayZMin, 1.0);
                            return true;
                        }
                    }
                    else
                    {
                        P -= dp * exp2(mipLevel);
                        Q -= dq * exp2(mipLevel);
                        K -= dk * exp2(mipLevel);
                        preZ = Q.z / K;

                        mipLevel--;
                    }
                }
            }

            return false;
            //return float4(hitUV, rayZMin, 0.0);
        }
        ENDHLSL
        
        Pass
        {
            Name "SSR Raymarching"
            ZTest Off
            ZWrite Off
            Cull Off
            
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment SSRFrag

            float4 SSRFrag(Varyings input) : SV_Target
            {
                float rawDepth = SampleSceneDepth(input.texcoord);
                float linerDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float3 vpos = ReconstructViewPos(input.texcoord, linerDepth);
                //float3 vpos = ReconstructViewPos2(input.texcoord, rawDepth);
                float3 normal = SampleSceneNormals(input.texcoord);
                float3 vDir = normalize(vpos);
                float3 rDir = TransformWorldToViewDir(normalize(reflect(vDir, normal)));

                // view space coordinates
                float3 wpos = _WorldSpaceCameraPos + vpos;
                float3 startView = TransformWorldToView(wpos);
                float2 hitUV = input.texcoord;
                if (BinarySearchRaymarching(startView, rDir, hitUV))
                    return GetSource(hitUV);
                    

                // if (HierarchicalZScreenSpaceRayMarching(startView, rDir, hitUV))
                //     return GetSource(hitUV);

                return float4(0, 0, 0, 0);

            }
           ENDHLSL
        }

        Pass
        {
            Name "SSR Blur"
            ZTest Off
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur

            float4 _SSRBlurRadius;

            float4 FragBlur(Varyings input) : SV_Target
            {
                const float weight[3] = {0.4026, 0.2442, 0.0545};
                float2 uv[5];

                uv[0] = input.texcoord;
                uv[1] = input.texcoord - float2(1.0, 1.0) * _ScreenSize.zw * _SSRBlurRadius.xy; // _SSRBlurRadius.xy (0, y) or (x, 0)
                uv[2] = input.texcoord + float2(1.0, 1.0) * _ScreenSize.zw * _SSRBlurRadius.xy;
                uv[3] = input.texcoord - float2(2.0, 2.0) * _ScreenSize.zw * _SSRBlurRadius.xy;
                uv[4] = input.texcoord + float2(2.0, 2.0) * _ScreenSize.zw * _SSRBlurRadius.xy;

                float3 sum = GetSource(uv[0]).rgb * weight[0];

                for (int it = 1; it < 3; it++)
                {
                    sum += GetSource(uv[it * 2 - 1]).rgb * weight[it];
                    sum += GetSource(uv[it * 2]).rgb * weight[it];
                }
                
                return float4(sum, 1.0);
            }
            ENDHLSL
        }
        Pass
        {
            Name "SSR Addtive"
            ZTest NotEqual
            ZWrite Off
            Cull Off
            Blend One One, One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment SSRFinalPassFragment

            TEXTURE2D_X_FLOAT(_CameraColorTexture);
            SAMPLER(sampler_CameraColorTexture);

            float4 CombineColor(Varyings input) : SV_Target
            {
                float4 color = GetSource(input.texcoord);
                float4 camera_color = SAMPLE_TEXTURE2D_X(_CameraColorTexture, sampler_CameraColorTexture,
                                                         input.texcoord);
                return float4(camera_color.rgb + color.rgb, 1.0f);
            }
            ENDHLSL
        }

    }
}
