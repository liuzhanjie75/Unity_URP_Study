using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(Camera))]
public class DepthTextureGenerator : MonoBehaviour
{
    public Shader DepthTextureShader;

    public RenderTexture DepthRenderTexture { get; set; } = null;

    private int _depthTextureSize = 0;
    public int DepthTextureSize
    {
        get
        {
            if (_depthTextureSize == 0)
                _depthTextureSize = Mathf.NextPowerOfTwo(Mathf.Max(Screen.width, Screen.height));
            return _depthTextureSize;
        }
    }

    private Material _depthTextureMaterial;
    private readonly int _depthTextureId = Shader.PropertyToID("_CameraDepthTexture");
    private static readonly int SourceSize = Shader.PropertyToID("_SourceSize");
    
    // Start is called before the first frame update
    void Start()
    {
        _depthTextureMaterial = CoreUtils.CreateEngineMaterial(DepthTextureShader);
        if (Camera.main != null) 
            Camera.main.depthTextureMode |= DepthTextureMode.Depth;

        if (DepthRenderTexture != null)
            return;
        
        DepthRenderTexture = RenderTexture.GetTemporary(DepthTextureSize, DepthTextureSize, 0, RenderTextureFormat.RFloat);
        DepthRenderTexture.autoGenerateMips = false;
        DepthRenderTexture.useMipMap = true;
        DepthRenderTexture.filterMode = FilterMode.Point;
        DepthRenderTexture.Create();
    }
    
    void OnEnable()
    {
        RenderPipelineManager.endCameraRendering += RenderPipelineManager_endCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.endCameraRendering -= RenderPipelineManager_endCameraRendering;
    }

    private void RenderPipelineManager_endCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        var width = DepthRenderTexture.width;
        var mipmapLevel = 0;

        RenderTexture preRenderTexture = null;

        while (width > 8)
        {
            var currentRenderTexture = RenderTexture.GetTemporary(width, width, 0, RenderTextureFormat.RFloat);
            currentRenderTexture.filterMode = FilterMode.Point;
            if (preRenderTexture == null)
                Graphics.Blit(Shader.GetGlobalTexture(_depthTextureId), currentRenderTexture);
            else
            {
                var w = preRenderTexture.width;
                _depthTextureMaterial.SetVector(SourceSize, new Vector4(1.0f / w, 1.0f / w, w, w));
                Graphics.Blit(preRenderTexture, currentRenderTexture, _depthTextureMaterial);
                RenderTexture.ReleaseTemporary(preRenderTexture);
            }
            
            Graphics.CopyTexture(currentRenderTexture, 0, 0 , DepthRenderTexture, 0, mipmapLevel);
            preRenderTexture = currentRenderTexture;
            
            width /= 2;
            mipmapLevel++;
        }
        
        RenderTexture.ReleaseTemporary(preRenderTexture);
    }

    private void OnDestroy()
    {
        if (DepthRenderTexture != null)
            DepthRenderTexture.Release();
        
        RenderTexture.ReleaseTemporary(DepthRenderTexture);
        DepthRenderTexture = null;
    }
}
