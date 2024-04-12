using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SSPR
{
    [Serializable]
    internal class SSPRSettings
    {
        [SerializeField] internal int RTSize = 512;
        [SerializeField] internal float ReflectHeight = 0.2f;
        [SerializeField] [Range(0, 1)] internal float StretchIntensity = 0.1f;
        [SerializeField] [Range(0, 1)] internal float StretchThreshold = 0.3f;
        [SerializeField] internal float EdgeFadeOut = 0.6f;

        internal int GroupThreadX;
        internal int GroupThreadY;
        internal int GroupX;
        internal int GroupY;
    }

    internal class SSPRPass : ScriptableRenderPass
    {
        private SSPRSettings _setting;
        private ComputeShader _computeShader;
        private int _ssprKernelId;
        private int _fillHoleKernelId;

        private ProfilingSampler _profilingSampler = new ProfilingSampler("SSPR");
        private RenderTextureDescriptor _ssprReflectionDescriptor;
        private RTHandle _ssprReflectionTexture;
        private RTHandle _ssprHeightTexture;
        private RTHandle _cameraColorTexture;
        private RTHandle _cameraDepthTexture;

        private const string SSPRKernelName = "SSPR";
        private const string FillHoleKernelName = "FillHole";
        private const string SSPRReflectionTextureName = "_SSPRReflectionTexture";
        private const string SSPRHeightTextureName = "_SSPRHeightBufferTexture";

        private static readonly int ReflectPlaneHeihgtID = Shader.PropertyToID("_ReflectPlaneHeight");
        private static readonly int RTSizeID = Shader.PropertyToID("_SSPRReflectionSize");
        private static readonly int SSPRReflectionTextureID = Shader.PropertyToID("_SSPRReflectionTexture");
        private static readonly int CameraColorTextureID = Shader.PropertyToID("_CameraColorTexture");
        private static readonly int CameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int SSPRHeightBufferID = Shader.PropertyToID("_SSPRHeightBuffer");
        private static readonly int CameraDirectionID = Shader.PropertyToID("_CameraDirection");
        private static readonly int StretchParamsID = Shader.PropertyToID("_StretchParams");
        private static readonly int EdgeFadeOutID = Shader.PropertyToID("_EdgeFadeOut");

        internal bool Setup(ref SSPRSettings featureSettings, ref ComputeShader computeShader)
        {
            _computeShader = computeShader;
            _setting = featureSettings;

            ConfigureInput(ScriptableRenderPassInput.Normal);

            return _computeShader != null && _setting != null;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            // 配置目标和清除
            var renderer = renderingData.cameraData.renderer;
            ConfigureTarget(renderer.cameraColorTargetHandle);
            ConfigureClear(ClearFlag.None, Color.white);
            _cameraColorTexture = renderer.cameraColorTargetHandle;
            _cameraDepthTexture = renderer.cameraDepthTargetHandle;

            float aspect = (float)Screen.height / Screen.width;
            // 计算线程组数量
            _setting.GroupThreadX = 8;
            _setting.GroupThreadY = 8;
            // 计算线程组线程
            _setting.GroupY = Mathf.RoundToInt((float)_setting.RTSize / _setting.GroupThreadY);
            _setting.GroupX = Mathf.RoundToInt(_setting.GroupY / aspect);

            // 分配RTHandle
            _ssprReflectionDescriptor = new RenderTextureDescriptor(_setting.GroupThreadX * _setting.GroupX,
                _setting.GroupThreadY * _setting.GroupY, RenderTextureFormat.BGRA32, 0, 0);
            _ssprReflectionDescriptor.enableRandomWrite = true; // 开启UAV随机读写
            RenderingUtils.ReAllocateIfNeeded(ref _ssprReflectionTexture, _ssprReflectionDescriptor,
                FilterMode.Bilinear, TextureWrapMode.Clamp, name: SSPRReflectionTextureName);

            // 只要r channel
            _ssprReflectionDescriptor.colorFormat = RenderTextureFormat.RFloat;
            RenderingUtils.ReAllocateIfNeeded(ref _ssprHeightTexture, _ssprReflectionDescriptor, FilterMode.Bilinear,
                TextureWrapMode.Clamp, name: SSPRHeightTextureName);

            // 设置ComputeShader属性
            _ssprKernelId = _computeShader.FindKernel(SSPRKernelName);

            _computeShader.SetFloat(ReflectPlaneHeihgtID, _setting.ReflectHeight);
            _computeShader.SetVector(RTSizeID,
                new Vector4(_ssprReflectionDescriptor.width, _ssprReflectionDescriptor.height,
                    1.0f / (float)_ssprReflectionDescriptor.width, 1.0f / (float)_ssprReflectionDescriptor.height));
            _computeShader.SetTexture(_ssprKernelId, SSPRReflectionTextureID, _ssprReflectionTexture);
            _computeShader.SetTexture(_ssprKernelId, SSPRHeightBufferID, _ssprHeightTexture);
            _computeShader.SetTexture(_ssprKernelId, CameraColorTextureID, _cameraColorTexture);
            _computeShader.SetTexture(_ssprKernelId, CameraDepthTextureID, _cameraDepthTexture);
            _computeShader.SetVector(CameraDirectionID, renderingData.cameraData.camera.transform.forward);
            _computeShader.SetVector(StretchParamsID,
                new Vector4(_setting.StretchIntensity, _setting.StretchThreshold, 0.0f, 0.0f));
            _computeShader.SetFloat(EdgeFadeOutID, _setting.EdgeFadeOut);

            // _fillHoleKernelId = _computeShader.FindKernel(FillHoleKernelName);
            // _computeShader.SetTexture(_fillHoleKernelId, SSPRReflectionTextureID, _ssprReflectionTexture);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_computeShader == null)
            {
                Debug.LogErrorFormat(
                    "{0}.Execute(): Missing computeShader. SSPR pass will not execute. Check for missing reference in the renderer resources.",
                    GetType().Name);
                return;
            }

            var cmd = CommandBufferPool.Get();
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            using (new ProfilingScope(cmd, _profilingSampler))
            {
                // Dispatch ComputeShader
                cmd.DispatchCompute(_computeShader, _ssprKernelId, _setting.GroupX, _setting.GroupY, 1);
                //cmd.DispatchCompute(_computeShader, _fillHoleKernelId, _setting.GroupX / 2, _setting.GroupY / 2, 1);

                // 设置全局数据，让反射物采样
                cmd.SetGlobalTexture(SSPRReflectionTextureID, _ssprReflectionTexture);
                cmd.SetGlobalVector(RTSizeID,
                    new Vector4(_ssprReflectionDescriptor.width, _ssprReflectionDescriptor.height,
                        1.0f / (float)_ssprReflectionDescriptor.width, 1.0f / (float)_ssprReflectionDescriptor.height));
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            _cameraColorTexture = null;
        }

        public void Dispose()
        {
            // 释放RTHandle
            _ssprReflectionTexture?.Release();
            _ssprReflectionTexture = null;

            _ssprHeightTexture?.Release();
            _ssprHeightTexture = null;
        }
    }

    [DisallowMultipleRendererFeature("SSPRRendererFeature")]
    public class SSPRRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] private SSPRSettings setting = new();
        [SerializeField] private ComputeShader ssprComputeShader;

        private SSPRPass _ssprPass;

        public override void Create()
        {
            _ssprPass ??= new SSPRPass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_ssprPass.Setup(ref setting, ref ssprComputeShader))
                renderer.EnqueuePass(_ssprPass);
        }
    }
}