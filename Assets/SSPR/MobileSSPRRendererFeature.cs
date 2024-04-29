using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

// https://github.com/ColinLeung-NiloCat/UnityURP-MobileScreenSpacePlanarReflection
namespace MobileSSPR
{
    [Serializable]
    internal class Settings
    {
        public bool ShouldRenderSSPR = true;
        public float HorizontalReflectionPlaneHeightWS = 0.01f; //default higher than ground a bit, to avoid ZFighting if user placed a ground plane at y=0
        [Range(0.01f, 1f)]
        public float FadeOutScreenBorderWithVerticle = 0.25f;
        [Range(0.01f, 1f)] 
        public float FadeOutScreenBorderWithHorizontal = 0.35f;
        [Range(0f, 8f)] 
        public float ScreenLRStretchIntensity = 4;
        [Range(-1f,1f)]
        public float ScreenLRStretchThreshold = 0.7f;
        [ColorUsage(true,true)]
        public Color TintColor = Color.white;
        
        //////////////////////////////////////////////////////////////////////////////////
        [Header("Performance Settings")]
        [Range(128, 1024)]
        [Tooltip("set to 512 or below for better performance, if visual quality lost is acceptable")]
        public int RT_height = 512;
        [Tooltip("can set to false for better performance, if visual quality lost is acceptable")]
        public bool UseHDR = true;
        [Tooltip("can set to false for better performance, if visual quality lost is acceptable")]
        public bool ApplyFillHoleFix = true;
        [Tooltip("can set to false for better performance, if flickering is acceptable")]
        public bool ShouldRemoveFlickerFinalControl = true;

        //////////////////////////////////////////////////////////////////////////////////
        [Header("Danger Zone")]
        [Tooltip("You should always turn this on, unless you want to debug")]
        public bool EnablePerPlatformAutoSafeGuard = true;
    }

    internal class MobileSSRPRenderPass : ScriptableRenderPass
    {
        private Settings _settings;
        private ComputeShader _computeShader;
        
        private const int SHADER_NUMTHREAD_X = 8; //must match compute shader's [numthread(x)]
        private const int SHADER_NUMTHREAD_Y = 8; //must match compute shader's [numthread(y)]
        
        private readonly ProfilingSampler _profilingSampler = new("MobileSSPR");
        private RenderTextureDescriptor _reflectionDescriptor;
        private RTHandle _colorRT;
        private RTHandle _posWSyRT;
        private RTHandle _packedDataRT;
        private RTHandle _cameraColorTexture;
        private RTHandle _cameraDepthTexture;
        private const string ColorTextureName = "_ColorTextureName";
        private const string PosWSyTextureName = "_PosWSyTextureName";
        private const string PackedDataTextureName = "_packedDataTextureName";
        
        private int _mobilePathSinglePass;
        private int _nonMobilePathClear;
        private int _nonMobilePathRenderHashRT;
        private int _nonMobilePathResolveColorRT;
        private int _fillHoles;

        private static readonly int RTSizeID = Shader.PropertyToID("_RTSize");
        private static readonly int HorizontalPlaneHeightWS = Shader.PropertyToID("_HorizontalPlaneHeightWS");
        private static readonly int FadeOutScreenBorderWithVerticle = Shader.PropertyToID("_FadeOutScreenBorderWidthVerticle");
        private static readonly int FadeOutScreenBorderWidthHorizontal = Shader.PropertyToID("_FadeOutScreenBorderWidthHorizontal");
        private static readonly int CameraDirection = Shader.PropertyToID("_CameraDirection");
        private static readonly int ScreenLRStretchIntensity = Shader.PropertyToID("_ScreenLRStretchIntensity");
        private static readonly int ScreenLRStretchThreshold = Shader.PropertyToID("_ScreenLRStretchThreshold");
        private static readonly int FinalTintColor = Shader.PropertyToID("_FinalTintColor");
        private static readonly int VPMatrix = Shader.PropertyToID("_VPMatrix");
        private static readonly int ColorRT = Shader.PropertyToID("_ColorRT");
        private static readonly int PosWSyRT = Shader.PropertyToID("_PosWSyRT");
        private static readonly int HashRT = Shader.PropertyToID("_HashRT");
        private static readonly int CameraOpaqueTexture = Shader.PropertyToID("_CameraOpaqueTexture");
        private static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
        // reflection plane renderer's material's shader must use this LightMode
        private readonly ShaderTagId _lightModeSspr = new ShaderTagId("MobileSSPR"); 
        private int GetRTHeight()
        {
            return Mathf.CeilToInt(_settings.RT_height / (float)SHADER_NUMTHREAD_Y) * SHADER_NUMTHREAD_Y;
        }

        private int GetRTWidth()
        {
            var aspect = (float)Screen.width / Screen.height;
            return Mathf.CeilToInt(GetRTHeight() * aspect / (float)SHADER_NUMTHREAD_X) * SHADER_NUMTHREAD_X;
        }
        
        /// <summary>
        /// If user enabled PerPlatformAutoSafeGuard, this function will return true if we should use mobile path
        /// </summary>
        bool ShouldUseSinglePassUnsafeAllowFlickeringDirectResolve()
        {
            if (_settings.EnablePerPlatformAutoSafeGuard)
            {
                //if RInt RT is not supported, use mobile path
                if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RInt))
                    return true;

                // tested Metal(even on a Mac) can't use InterlockedMin().
                // so if metal, use mobile path
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal)
                    return true;
#if UNITY_EDITOR
                // PC(DirectX) can use RenderTextureFormat.RInt + InterlockedMin() without any problem, use Non-Mobile path.
                // Non-Mobile path will NOT produce any flickering
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12)
                    return false;
#elif UNITY_ANDROID
                // - samsung galaxy A70(Adreno612) will fail if use RenderTextureFormat.RInt + InterlockedMin() in compute shader
                // - but Lenovo S5(Adreno506) is correct, WTF???
                // because behavior is different between android devices, we assume all android are not safe to use RenderTextureFormat.RInt + InterlockedMin() in compute shader
                // so android always go mobile path
                return true;
#endif
            }

            //let user decide if we still don't know the correct answer
            return !_settings.ShouldRemoveFlickerFinalControl;
        }
        
        public bool Setup(Settings settings, ComputeShader shader)
        {
            _settings = settings;
            _computeShader = shader;
            ConfigureInput(ScriptableRenderPassInput.Normal);
            
            return _settings != null && _computeShader != null;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            if (_settings.ShouldRenderSSPR)
                return;
            
            var renderer = renderingData.cameraData.renderer;
            ConfigureTarget(renderer.cameraColorTargetHandle);
            ConfigureClear(ClearFlag.None, Color.white);
            _cameraColorTexture = renderer.cameraColorTargetHandle;
            _cameraDepthTexture = renderer.cameraDepthTargetHandle;
            
            var cameraTextureDescriptor = renderingData.cameraData.cameraTargetDescriptor;
            // RTHandle
            _reflectionDescriptor = new RenderTextureDescriptor(GetRTWidth(), GetRTHeight(), RenderTextureFormat.BGRA32, 0, 0)
            {
                enableRandomWrite = true
            };
            var shouldUseHDRColorRT = _settings.UseHDR;
            if (cameraTextureDescriptor.colorFormat == RenderTextureFormat.ARGB32)
                shouldUseHDRColorRT = false;// if there are no HDR info to reflect anyway, no need a HDR colorRT
            _reflectionDescriptor.colorFormat = shouldUseHDRColorRT ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32; // we need alpha! (usually LDR is enough, ignore HDR is acceptable for reflection)
            RenderingUtils.ReAllocateIfNeeded(ref _colorRT, _reflectionDescriptor,
                FilterMode.Bilinear, TextureWrapMode.Clamp, name: ColorTextureName);
            
            //PackedData RT
            if (ShouldUseSinglePassUnsafeAllowFlickeringDirectResolve())
            {
                //use unsafe method if mobile
                //posWSy RT (will use this RT for posWSy compare test, just like the concept of regular depth buffer)
                _reflectionDescriptor.colorFormat = RenderTextureFormat.RFloat;
                RenderingUtils.ReAllocateIfNeeded(ref _posWSyRT, _reflectionDescriptor,
                    FilterMode.Bilinear, TextureWrapMode.Clamp, name: PosWSyTextureName);
            }
            else
            {
                //use 100% correct method if console/PC
                _reflectionDescriptor.colorFormat = RenderTextureFormat.RInt;
                RenderingUtils.ReAllocateIfNeeded(ref _packedDataRT, _reflectionDescriptor,
                    FilterMode.Bilinear, TextureWrapMode.Clamp, name: PackedDataTextureName);
            }
            
            
            _computeShader.SetVector(RTSizeID, new Vector4(GetRTWidth(), GetRTHeight(), 1.0f / GetRTWidth(), 1.0f / GetRTHeight()));
            _computeShader.SetVector(CameraDirection, renderingData.cameraData.camera.transform.forward);
            _computeShader.SetVector(FinalTintColor, _settings.TintColor);
            _computeShader.SetFloat(HorizontalPlaneHeightWS, _settings.HorizontalReflectionPlaneHeightWS);
            _computeShader.SetFloat(FadeOutScreenBorderWithVerticle, _settings.FadeOutScreenBorderWithVerticle);
            _computeShader.SetFloat(FadeOutScreenBorderWidthHorizontal, _settings.FadeOutScreenBorderWithHorizontal);
            _computeShader.SetFloat(ScreenLRStretchIntensity, _settings.ScreenLRStretchIntensity);
            _computeShader.SetFloat(ScreenLRStretchThreshold, _settings.ScreenLRStretchThreshold);
            
            //we found that on metal, UNITY_MATRIX_VP is not correct, so we will pass our own VP matrix to compute shader
            var camera = renderingData.cameraData.camera;
            var vp = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix;
            _computeShader.SetMatrix(VPMatrix, vp);
            
            
            if (ShouldUseSinglePassUnsafeAllowFlickeringDirectResolve())
            {
                _mobilePathSinglePass = _computeShader.FindKernel("MobilePathSinglePassColorRTDirectResolve");
                _computeShader.SetTexture(_mobilePathSinglePass, ColorRT, _colorRT);
                _computeShader.SetTexture(_mobilePathSinglePass, PosWSyRT, _posWSyRT);
                _computeShader.SetTexture(_mobilePathSinglePass, CameraOpaqueTexture, _cameraColorTexture);
                _computeShader.SetTexture(_mobilePathSinglePass, CameraDepthTexture, _cameraDepthTexture);
            }
            else
            {
                ////////////////////////////////////////////////
                //Non-Mobile Path (PC/console)
                ////////////////////////////////////////////////

                //kernel NonMobilePathClear
                _nonMobilePathClear = _computeShader.FindKernel("NonMobilePathClear");
                _computeShader.SetTexture(_nonMobilePathClear, ColorRT, _colorRT);
                _computeShader.SetTexture(_nonMobilePathClear, HashRT, _packedDataRT);
                
                _nonMobilePathRenderHashRT = _computeShader.FindKernel("NonMobilePathRenderHashRT");
                _computeShader.SetTexture(_nonMobilePathRenderHashRT, HashRT, _packedDataRT);
                _computeShader.SetTexture(_nonMobilePathRenderHashRT, CameraDepthTexture, _cameraDepthTexture);

                _nonMobilePathResolveColorRT = _computeShader.FindKernel("NonMobilePathResolveColorRT");
                _computeShader.SetTexture(_nonMobilePathResolveColorRT, CameraOpaqueTexture, _cameraColorTexture);
                _computeShader.SetTexture(_nonMobilePathResolveColorRT, ColorRT, _colorRT);
                _computeShader.SetTexture(_nonMobilePathResolveColorRT, HashRT, _packedDataRT);
            }
            
            //optional shared pass to improve result only: fill RT hole
            if(_settings.ApplyFillHoleFix)
            {
                _fillHoles = _computeShader.FindKernel("FillHoles");
                _computeShader.SetTexture(_fillHoles, ColorRT, _colorRT);
                _computeShader.SetTexture(_fillHoles, HashRT, _packedDataRT);
            }
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_computeShader == null)
            {
                return;
            }

            var cmd = CommandBufferPool.Get();
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            if (_settings.ShouldRenderSSPR)
            {
                using (new ProfilingScope(cmd, _profilingSampler))
                {
                    var dispatchThreadGroupXCount = GetRTWidth() / SHADER_NUMTHREAD_X; //divide by shader's numthreads.x
                    var dispatchThreadGroupYCount = GetRTHeight() / SHADER_NUMTHREAD_Y; //divide by shader's numthreads.y
                    var dispatchThreadGroupZCount = 1; // divide by shader's numthreads.z
                
                    // Dispatch ComputeShader
                    if (ShouldUseSinglePassUnsafeAllowFlickeringDirectResolve())
                    {
                        cmd.DispatchCompute(_computeShader, _mobilePathSinglePass, dispatchThreadGroupXCount, dispatchThreadGroupYCount, dispatchThreadGroupZCount);
                    }
                    else
                    {
                        cmd.DispatchCompute(_computeShader, _nonMobilePathClear, dispatchThreadGroupXCount, dispatchThreadGroupYCount, dispatchThreadGroupZCount);
                        cmd.DispatchCompute(_computeShader, _nonMobilePathRenderHashRT, dispatchThreadGroupXCount, dispatchThreadGroupYCount, dispatchThreadGroupZCount);
                        cmd.DispatchCompute(_computeShader, _nonMobilePathResolveColorRT, dispatchThreadGroupXCount, dispatchThreadGroupYCount, dispatchThreadGroupZCount);
                    
                    }

                    if (_settings.ApplyFillHoleFix)
                    {
                        cmd.DispatchCompute(_computeShader, _fillHoles, Mathf.CeilToInt(dispatchThreadGroupXCount / 2f), 
                            Mathf.CeilToInt(dispatchThreadGroupYCount / 2f), dispatchThreadGroupZCount);
                    }

                    cmd.SetGlobalTexture(ColorRT, _colorRT);
                    cmd.SetGlobalVector(RTSizeID, new Vector4(GetRTWidth(), GetRTHeight(), 1.0f / GetRTWidth(), 1.0f / GetRTHeight()));
                
                    cmd.EnableShaderKeyword("_MobileSSPR");
                }
            }
            else
            {
                cmd.DisableShaderKeyword("_MobileSSPR");
            }


            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            
            //======================================================================
            //draw objects(e.g. reflective wet ground plane) with lightmode "MobileSSPR", which will sample _MobileSSPR_ColorRT
            var drawingSettings = CreateDrawingSettings(_lightModeSspr, ref renderingData, SortingCriteria.CommonOpaque);
            var filteringSettings = new FilteringSettings(RenderQueueRange.all);
            context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref filteringSettings);
        }
        
        public override void FrameCleanup(CommandBuffer cmd)
        {
            _colorRT.Release();

            if(ShouldUseSinglePassUnsafeAllowFlickeringDirectResolve())
                _posWSyRT.Release();
            else
                _packedDataRT.Release();
        }
    }
    
    public class MobileSSPRRendererFeature : ScriptableRendererFeature
    {
        [SerializeField]
        private Settings setting = new();
        [SerializeField]
        private ComputeShader computeShader;

        private MobileSSRPRenderPass _pass;
        
        public override void Create()
        {
            _pass ??= new MobileSSRPRenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass.Setup(setting, computeShader))
                renderer.EnqueuePass(_pass);
        }
    }
}

