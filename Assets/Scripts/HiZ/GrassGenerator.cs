using UnityEngine;

public class GrassGenerator : MonoBehaviour
{
    public Mesh GrassMesh;
    public Material GrassMeshMaterial;
    public int SubMeshIndex = 0;
    public int GrassCountPerRaw = 300;
    
    private int _grassCount;
    private const float Range = 100f;
    private ComputeBuffer _grassMatrixBuffer;
    
    private int cachedInstanceCount = -1;
    private int cachedSubMeshIndex = -1;
    private ComputeBuffer argsBuffer;
    private uint[] args = new uint[5] { 0, 0, 0, 0, 0 };
    
    
    private static readonly int PositionBuffer = Shader.PropertyToID("positionBuffer");


    // Start is called before the first frame update
    void Start()
    {
        _grassCount = GrassCountPerRaw * GrassCountPerRaw;

        InitComputeBuffer();
        InitGrassPosition();
    }

    // Update is called once per frame
    void Update()
    {
        
        GrassMeshMaterial.SetBuffer(PositionBuffer, _grassMatrixBuffer);
        Graphics.DrawMeshInstancedIndirect(GrassMesh, SubMeshIndex, GrassMeshMaterial, new Bounds(Vector3.zero, Vector3.one * Range), argsBuffer);
    }
    

    private void InitComputeBuffer()
    {
        argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        SubMeshIndex = Mathf.Clamp(SubMeshIndex, 0, GrassMesh.subMeshCount - 1);
        args[0] = (uint)GrassMesh.GetIndexCount(SubMeshIndex);
        args[1] = (uint)_grassCount;
        args[2] = (uint)GrassMesh.GetIndexStart(SubMeshIndex);
        args[3] = (uint)GrassMesh.GetBaseVertex(SubMeshIndex);
        argsBuffer.SetData(args);
        
        _grassMatrixBuffer = new ComputeBuffer(_grassCount, sizeof(float) * 16);
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
