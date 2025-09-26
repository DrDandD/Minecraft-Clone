using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class Chunk : MonoBehaviour
{
    public const int Size = 16;
    public const int Height = 328;

    public Vector2Int coord;
    public WorldGenerator world;

    public bool VoxelsReady { get; private set; } = false;


    BlockType[,,] voxels = new BlockType[Size, Height, Size];
    MeshFilter mf; MeshCollider mc;

    void Awake() { mf = GetComponent<MeshFilter>(); mc = GetComponent<MeshCollider>(); }

    // Lightweight bootstrap (no heavy generation here)
    public void Bootstrap(WorldGenerator w, Vector2Int c)
    {
        world = w; coord = c; name = $"Chunk_{coord.x}_{coord.y}";
    }

    // Called by WorldGenerator when async gen result arrives
    public void ApplyGeneratedVoxels(byte[] flat)
    {
        int i = 0;
        for (int y = 0; y < Height; y++)
            for (int z = 0; z < Size; z++)
                for (int x = 0; x < Size; x++)
                    voxels[x, y, z] = (BlockType)flat[i++];
        VoxelsReady = true;           // <-- mark ready AFTER data is in
    }

    // Mesh build (threaded)
    public void RebuildMeshAsync()
    {
        ThreadedMesher.EnsureInstance();
        var flat = new byte[Size * Size * Height];
        int i = 0;
        for (int y = 0; y < Height; y++)
            for (int z = 0; z < Size; z++)
                for (int x = 0; x < Size; x++)
                    flat[i++] = (byte)voxels[x, y, z];
        ThreadedMesher.Instance.Enqueue(this, flat);
    }

    // Applied by ThreadedMesher on main thread
    public void ApplyBuiltMesh(Vector3[] verts, Vector2[] uvs, int[] tris)
    {
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mf.sharedMesh = mesh;
        mc.sharedMesh = mesh;
    }

    // Voxel access used by gameplay edits
    public BlockType GetBlock(int x, int y, int z)
    {
        if (x < 0 || x >= Size || y < 0 || y >= Height || z < 0 || z >= Size)
        {
            Vector3Int wp = new Vector3Int(x + coord.x * Size, y, z + coord.y * Size);
            return world != null ? world.GetBlockGlobal(wp) : BlockType.Air;
        }
        return voxels[x, y, z];
    }
    public void SetBlock(int x, int y, int z, BlockType b, bool rebuild = true)
    {
        if (x < 0 || x >= Size || y < 0 || y >= Height || z < 0 || z >= Size)
        {
            Vector3Int wp = new Vector3Int(x + coord.x * Size, y, z + coord.y * Size);
            world?.SetBlockGlobal(wp, b);
            return;
        }
        voxels[x, y, z] = b;
        if (rebuild) RebuildMeshAsync();
    }
    public BlockType GetBlockGlobal(Vector3Int wp)
    {
        int lx = wp.x - coord.x * Size, ly = wp.y, lz = wp.z - coord.y * Size;
        if (lx < 0 || lx >= Size || ly < 0 || ly >= Height || lz < 0 || lz >= Size) return BlockType.Air;
        return voxels[lx, ly, lz];
    }
    public void SetBlockGlobal(Vector3Int wp, BlockType b, bool rebuild = true)
    {
        int lx = wp.x - coord.x * Size, ly = wp.y, lz = wp.z - coord.y * Size;
        if (lx < 0 || lx >= Size || ly < 0 || ly >= Height || lz < 0 || lz >= Size) return;
        voxels[lx, ly, lz] = b;
        if (rebuild) RebuildMeshAsync();
    }
}
