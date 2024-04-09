using System;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HiZ
{
    public static class HiZDepthTextureManager
    {
        public static RenderTexture DepthRenderTexture;
        public static int DepthTextureSize;
        public static int DepthTextureMip;

        private static void CreateHiZDepthTexture(int screenWidth)
        {
            if (DepthRenderTexture != null) 
                return;
            
            var (w, mip) = GetDepthTextureWidthFromScreen(screenWidth);
            DepthTextureSize = w;
            DepthTextureMip = mip;

            var depthRT = new RenderTexture(w, w, 0, RenderTextureFormat.RFloat, mip)
            {
                name = "hizDepthTexture",
                useMipMap = true,
                autoGenerateMips = false,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
            depthRT.Create();
            DepthRenderTexture = depthRT;
        }
        
        private static (int, int) GetDepthTextureWidthFromScreen(int screenWidth)
        {
            return screenWidth switch
            {
                >= 2048 => (2048, 10),
                >= 1024 => (1024, 9),
                _ => (512, 8)
            };
        }
        
        public static void UpdateHiZDepthTextureWidth(int screenWidth)
        {
            var (width, mip) = GetDepthTextureWidthFromScreen(screenWidth);

            if (width == DepthTextureSize) 
                return;

            Dispose();
            DepthTextureSize = width;
            DepthTextureMip = mip;
            
            CreateHiZDepthTexture(screenWidth);
        }

        public static void Dispose()
        {
            if (DepthRenderTexture != null) 
                DepthRenderTexture.Release();
            DepthRenderTexture = null;
            DepthTextureSize = 0;
            DepthTextureMip = 0;
        }

    }
    
    public class DepthTextureGeneratorRendererFeature : ScriptableRendererFeature
    {
        public ComputeShader GenerateMipmap;
        private HierarchicalZBufferPass _hizPass;

        public override void Create()
        {
            _hizPass ??= new HierarchicalZBufferPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox - 1
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!renderingData.cameraData.postProcessEnabled)
                return;

            if (_hizPass.Setup(GenerateMipmap))
                renderer.EnqueuePass(_hizPass);
        }
        
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            _hizPass?.Dispose();
            _hizPass = null;
        }

        private class HierarchicalZBufferPass : ScriptableRenderPass
        {
            private ComputeShader _generateMipmap;
            private int _genMipmapKernel;

            private static readonly int SourceTexID = Shader.PropertyToID("_SourceTexture");
            private static readonly int DestTexId = Shader.PropertyToID("_DestTexture");
            private static readonly int DepthTexSizeId = Shader.PropertyToID("_DepthTextureSize");
            
            private static readonly int HiZBufferTexID = Shader.PropertyToID("_HierarchicalZBufferTexture");
            private readonly ProfilingSampler _profilingSampler = new("HiZ");

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                if (renderingData.cameraData.isSceneViewCamera)
                    return;
                
                base.OnCameraSetup(cmd, ref renderingData);
                var renderer = renderingData.cameraData.renderer;

                var desc = renderingData.cameraData.cameraTargetDescriptor;
                var height = Mathf.NextPowerOfTwo(desc.height);
                var width = Mathf.NextPowerOfTwo(desc.width);
                HiZDepthTextureManager.UpdateHiZDepthTextureWidth(math.max(height,width));

                // 配置目标和清除
                // ConfigureTarget(renderer.cameraDepthTargetHandle);
                // ConfigureClear(ClearFlag.None, Color.white);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (renderingData.cameraData.isSceneViewCamera)
                    return;
                if (_generateMipmap == null)
                {
                    Debug.LogErrorFormat(
                        "{0}.Execute(): Missing ComputeShader. Check for missing reference in the renderer resources.",
                        GetType().Name);
                    return;
                }

                var cmd = CommandBufferPool.Get();
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                var cameraDepthTexture = renderingData.cameraData.renderer.cameraDepthTargetHandle;

                var texture = HiZDepthTextureManager.DepthRenderTexture;
                using (new ProfilingScope(cmd, _profilingSampler))
                {
                    Graphics.Blit(cameraDepthTexture, texture);
                        
                    float w = HiZDepthTextureManager.DepthTextureSize;
                    float h = w;
                    for (var i = 1; i < HiZDepthTextureManager.DepthTextureMip; i++)
                    {
                        w = MathF.Max(w / 2, 1);
                        h = MathF.Max(h / 2, 1);
                        
                        cmd.SetComputeTextureParam(_generateMipmap, _genMipmapKernel, SourceTexID, texture, i - 1);
                        cmd.SetComputeTextureParam(_generateMipmap, _genMipmapKernel, DestTexId, texture, i);
                        cmd.SetComputeVectorParam(_generateMipmap,  DepthTexSizeId, new Vector4(w, h, 1f / w, 1f / h));
                        
                        cmd.DispatchCompute(_generateMipmap, 0, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
                    }
                }
                
                cmd.SetGlobalTexture(HiZBufferTexID, texture);

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            public bool Setup(ComputeShader shader)
            {
                _generateMipmap = shader;
                ConfigureInput(ScriptableRenderPassInput.Depth);
                _genMipmapKernel = _generateMipmap.FindKernel("GenerateMipmap");

                return _generateMipmap != null;
            }

            public void Dispose()
            {
                HiZDepthTextureManager.Dispose();
            }
        }
    }
}



