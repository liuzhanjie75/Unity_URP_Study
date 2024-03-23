using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SSR
{
    [SerializeField]
    public enum BlendMode
    {
        Addtive,
        Balance
    }

    [SerializeField]
    internal class SSRSettings
    {
        [SerializeField] internal BlendMode blendMode = BlendMode.Addtive;
        [SerializeField] internal float Intensity = 0.8f;
        [SerializeField] internal float MaxDistance = 10f;
        [SerializeField] internal float Thickness = 0.5f;
        [SerializeField] internal float BlurRadius = 1f;
        [SerializeField] internal int Stride = 30;
        [SerializeField] internal int StepCount = 12;
        [SerializeField] internal int BinaryCount = 6;
        [SerializeField] internal bool JitterDither = true;
    }


    public class SSRRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] private SSRSettings Settings = new();
        public Shader SSRShader;

        private SSRRenderPass _renderPass;
        private Material _material;

        public override void Create()
        {
            _renderPass ??= new SSRRenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!renderingData.cameraData.postProcessEnabled)
                return;
            if (!GetMaterials())
            {
                Debug.LogErrorFormat("{0}.AddRenderPasses(): Missing material. {1} render pass will not be added.",
                    GetType().Name, name);
                return;
            }

            if (_renderPass.Setup(ref Settings, _material))
                renderer.EnqueuePass(_renderPass);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            _renderPass?.Dispose();
            _renderPass = null;
        }

        private bool GetMaterials()
        {
            _material ??= CoreUtils.CreateEngineMaterial(SSRShader);
            return _material != null;
        }

        public class SSRRenderPass : ScriptableRenderPass
        {
            private enum ShaderPass
            {
                Raymarching,
                Blur,
                Addtive,
                Balance,
            }

            private SSRSettings _ssrSettings;
            private Material _material;

            private readonly ProfilingSampler _profilingSampler = new("SSR");
            private RenderTextureDescriptor _textureDescriptor;
            private RTHandle _sourceRTHandle;
            private RTHandle _destinationRTHandle;
            private RTHandle _ssrTexture0;
            private RTHandle _ssrTexture1;
            private const string SsrTextureName0 = "SSRTexture0";
            private const string SsrTextureName1 = "SSRTexture1";
            private static readonly int ProjectionParams2ID = Shader.PropertyToID("_ProjectionParams2");
            private static readonly int CameraViewTopLeftCornerID = Shader.PropertyToID("_CameraViewTopLeftCorner");
            private static readonly int CameraViewXExtentID = Shader.PropertyToID("_CameraViewXExtent");
            private static readonly int CameraViewYExtentID = Shader.PropertyToID("_CameraViewYExtent");
            private static readonly int SourceSizeID = Shader.PropertyToID("_SourceSize");
            private static readonly int SSRParams0ID = Shader.PropertyToID("_SSRParams0");
            private static readonly int SSRParams1ID = Shader.PropertyToID("_SSRParams1");
            private static readonly int BlurRadiusID = Shader.PropertyToID("_SSRBlurRadius");

            private const string JitterKeyword = "_JITTER_ON";

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                base.OnCameraSetup(cmd, ref renderingData);

                var cameraData = renderingData.cameraData;
                var renderer = cameraData.renderer;

                var viewMatrix = cameraData.GetViewMatrix();
                var projectionMatrix = cameraData.GetProjectionMatrix();
                var vp = projectionMatrix * viewMatrix;

                // 将 camera view space 的平移置 0， 用来计算 world space 下相对于相机的 vector
                var cView = viewMatrix;
                cView.SetColumn(3, new Vector4(0, 0, 0, 1f));
                var cViewProjection = projectionMatrix * cView;

                // 计算 world space 下， 近平面的四角坐标。
                var cVpInverse = cViewProjection.inverse;
                var near = cameraData.camera.nearClipPlane;
                var topLeftCorner = cVpInverse.MultiplyPoint(new Vector3(-1f, 1f, -1f));
                var topRightCorner = cVpInverse.MultiplyPoint(new Vector3(1f, 1f, -1f));
                var bottomLeftCorner = cVpInverse.MultiplyPoint(new Vector3(-1f, -1f, -1f));

                var cameraXExtent = topRightCorner - topLeftCorner;
                var cameraYExtent = bottomLeftCorner - topLeftCorner;

                _material.SetVector(CameraViewTopLeftCornerID, topLeftCorner);
                _material.SetVector(CameraViewXExtentID, cameraXExtent);
                _material.SetVector(CameraViewYExtentID, cameraYExtent);
                _material.SetVector(ProjectionParams2ID,
                    new Vector4(1f / near, cameraData.worldSpaceCameraPos.x, cameraData.worldSpaceCameraPos.y,
                        cameraData.worldSpaceCameraPos.z));

                _material.SetVector(SourceSizeID,
                    new Vector4(_textureDescriptor.width, _textureDescriptor.height, 1.0f / _textureDescriptor.width,
                        1.0f / _textureDescriptor.height));

                // 发送SSR参数
                _material.SetVector(SSRParams0ID,
                    new Vector4(_ssrSettings.MaxDistance, _ssrSettings.Stride, _ssrSettings.StepCount,
                        _ssrSettings.Thickness));
                _material.SetVector(SSRParams1ID,
                    new Vector4(_ssrSettings.BinaryCount, _ssrSettings.Intensity, 0.0f, 0.0f));

                // 设置全局keyword
                if (_ssrSettings.JitterDither)
                {
                    _material.EnableKeyword(JitterKeyword);
                }
                else
                {
                    _material.DisableKeyword(JitterKeyword);
                }

                _textureDescriptor = cameraData.cameraTargetDescriptor;
                _textureDescriptor.msaaSamples = 1;
                _textureDescriptor.depthBufferBits = 0;
                RenderingUtils.ReAllocateIfNeeded(ref _ssrTexture0, _textureDescriptor, name: SsrTextureName0);
                RenderingUtils.ReAllocateIfNeeded(ref _ssrTexture1, _textureDescriptor, name: SsrTextureName1);

                ConfigureTarget(renderer.cameraColorTargetHandle);
                ConfigureClear(ClearFlag.None, Color.white);
            }

            public override void OnCameraCleanup(CommandBuffer cmd)
            {
                base.OnCameraCleanup(cmd);
                _sourceRTHandle = null;
                _destinationRTHandle = null;
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (_material == null)
                {
                    Debug.LogErrorFormat(
                        "{0}.Execute(): Missing material. ScreenSpaceAmbientOcclusion pass will not execute. Check for missing reference in the renderer resources.",
                        GetType().Name);
                    return;
                }

                var cmd = CommandBufferPool.Get("SSR");
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                _sourceRTHandle = renderingData.cameraData.renderer.cameraColorTargetHandle;
                _destinationRTHandle = renderingData.cameraData.renderer.cameraColorTargetHandle;

                using (new ProfilingScope(cmd, _profilingSampler))
                {
                    // SSR
                    Blitter.BlitCameraTexture(cmd, _sourceRTHandle, _ssrTexture0, _material,
                        (int)ShaderPass.Raymarching);
                    
                    // Horizontal blur
                    cmd.SetGlobalVector(BlurRadiusID, new Vector4(_ssrSettings.BlurRadius, 0, 0, 0));
                    Blitter.BlitCameraTexture(cmd, _ssrTexture0, _ssrTexture1, _material, (int)ShaderPass.Blur);
                    
                    // Vertical Blur
                    cmd.SetGlobalVector(BlurRadiusID, new Vector4(0, _ssrSettings.BlurRadius, 0, 0));
                    Blitter.BlitCameraTexture(cmd, _ssrTexture1, _ssrTexture0, _material, (int)ShaderPass.Blur);
                    
                    // Additive Pass
                    Blitter.BlitCameraTexture(cmd, _ssrTexture0, _destinationRTHandle, _material
                        , _ssrSettings.blendMode == BlendMode.Addtive ? (int)ShaderPass.Addtive : (int)ShaderPass.Balance);
                    
                    //Blitter.BlitCameraTexture(cmd, _ssrTexture0, _destinationRTHandle);
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            internal bool Setup(ref SSRSettings ssrSettings, Material material)
            {
                _ssrSettings = ssrSettings;
                _material = material;

                ConfigureInput(ScriptableRenderPassInput.Normal);

                return _material != null && _ssrSettings != null;
            }

            public void Dispose()
            {
                _ssrTexture0?.Release();
                _ssrTexture1?.Release();
                _ssrTexture0 = null;
                _ssrTexture1 = null;
            }
        }
    }
}