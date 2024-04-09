using UnityEngine;

namespace HiZ
{
    public class GrassGenerator : MonoBehaviour
    {
        public Mesh GrassMesh;
        public Material GrassMeshMaterial;
        public int SubMeshIndex = 0;
        public int GrassCountPerRaw = 300;
        public ComputeShader compute;

        private int _grassCount;
        private const float Range = 100f;
        private ComputeBuffer _grassMatrixBuffer;
        private ComputeBuffer _cullResultBuffer;

        private int _kernel;
        private ComputeBuffer _argsBuffer;
        private readonly uint[] _args = new uint[5] { 0, 0, 0, 0, 0 };

        private Camera _main;

        private static readonly int PositionBuffer = Shader.PropertyToID("positionBuffer");
        private static readonly int GrassCount = Shader.PropertyToID("grassCount");
        private static readonly int GrassMatrixBuffer = Shader.PropertyToID("grassMatrixBuffer");
        private static readonly int VPMatrixId = Shader.PropertyToID("vpMatrix");
        private static readonly int CullResultBufferId = Shader.PropertyToID("cullResultBuffer");
        private static readonly int BoundsMax = Shader.PropertyToID("boundsMax");
        private static readonly int BoundsMin = Shader.PropertyToID("boundsMin");
        private static readonly int HizTextureId = Shader.PropertyToID("hizTexture");
        private static readonly int DepthTextureSize = Shader.PropertyToID("depthTextureSize");

        // Start is called before the first frame update
        void Start()
        {
            _grassCount = GrassCountPerRaw * GrassCountPerRaw;
            _main = Camera.main;
            if (_main == null)
                return;

            InitComputeBuffer();
            InitGrassPosition();
            InitComputeShader();
        }

        // Update is called once per frame
        void Update()
        {
            var main = Camera.main;
            if (main == null || HiZDepthTextureManager.DepthRenderTexture == null)
                return;

            var matrix = GL.GetGPUProjectionMatrix(main.projectionMatrix, false) * main.worldToCameraMatrix;
            compute.SetMatrix(VPMatrixId, matrix);
            compute.SetInt(DepthTextureSize, HiZDepthTextureManager.DepthTextureSize);
            compute.SetTexture(_kernel, HizTextureId, HiZDepthTextureManager.DepthRenderTexture);
            _cullResultBuffer.SetCounterValue(0);
            compute.SetBuffer(_kernel, CullResultBufferId, _cullResultBuffer);
            compute.Dispatch(_kernel, 1 + _grassCount / 1024, 1, 1);

            GrassMeshMaterial.SetBuffer(PositionBuffer, _cullResultBuffer);
            ComputeBuffer.CopyCount(_cullResultBuffer, _argsBuffer, sizeof(uint));
            _argsBuffer.GetData(_args);

            Graphics.DrawMeshInstancedIndirect(GrassMesh, SubMeshIndex, GrassMeshMaterial,
                new Bounds(Vector3.zero, Vector3.one * Range), _argsBuffer);
        }

        void OnDisable()
        {
            _grassMatrixBuffer?.Release();
            _grassMatrixBuffer = null;

            _cullResultBuffer?.Release();
            _cullResultBuffer = null;

            _argsBuffer?.Release();
            _argsBuffer = null;
        }

        private void InitComputeShader()
        {
            var main = Camera.main;
            if (main == null)
                return;

            _kernel = compute.FindKernel("GrassCulling");
            compute.SetInt(GrassCount, _grassCount);
            compute.SetBuffer(_kernel, GrassMatrixBuffer, _grassMatrixBuffer);

            var o = new GameObject();
            var meshCollider = o.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = GrassMesh;
            var bounds = meshCollider.bounds;
            compute.SetVector(BoundsMax, bounds.max);
            compute.SetVector(BoundsMin, bounds.min);
            o.SetActive(false);
            GameObject.DestroyImmediate(o);
        }

        private void InitComputeBuffer()
        {
            _argsBuffer = new ComputeBuffer(1, _args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
            SubMeshIndex = Mathf.Clamp(SubMeshIndex, 0, GrassMesh.subMeshCount - 1);
            _args[0] = (uint)GrassMesh.GetIndexCount(SubMeshIndex);
            _args[1] = (uint)_grassCount;
            _args[2] = (uint)GrassMesh.GetIndexStart(SubMeshIndex);
            _args[3] = (uint)GrassMesh.GetBaseVertex(SubMeshIndex);
            _argsBuffer.SetData(_args);

            _grassMatrixBuffer = new ComputeBuffer(_grassCount, sizeof(float) * 16);
            _cullResultBuffer = new ComputeBuffer(_grassCount, sizeof(float) * 16, ComputeBufferType.Append);
        }

        private void InitGrassPosition()
        {
            const float padding = 2f;
            var width = Range - padding * 2;
            var widthStart = -width / 2;
            var step = width / GrassCountPerRaw;
            var grassMatrix4X4S = new Matrix4x4[_grassCount];

            for (var i = 0; i < GrassCountPerRaw; i++)
            {
                for (var j = 0; j < GrassCountPerRaw; j++)
                {
                    var x = widthStart + step * i;
                    var z = widthStart + step * j;
                    var pos = new Vector3(x, GetGroundHeight(x, z), z);
                    grassMatrix4X4S[i * GrassCountPerRaw + j] = Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one);
                }
            }

            _grassMatrixBuffer.SetData(grassMatrix4X4S);
        }

        private float GetGroundHeight(float x, float z)
        {
            const float height = 10;
            if (Physics.Raycast(new Vector3(x, height, z), Vector3.down, out var hit, height * 2))
                return height - hit.distance;

            return 0;
        }
    }
}