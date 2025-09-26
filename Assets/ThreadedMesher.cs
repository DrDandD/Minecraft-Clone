using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Background mesher: builds chunk meshes off the main thread and applies results on Update.
/// Drop this once in your scene or call EnsureInstance() before first use.
/// </summary>
public class ThreadedMesher : MonoBehaviour
{
    public static ThreadedMesher Instance { get; private set; }

    [Tooltip("Number of worker Tasks building meshes in parallel.")]
    [Range(1, 8)] public int maxWorkers = 2;

    public struct BuildRequest
    {
        public Chunk chunk;
        public Vector2Int coord;
        public int size;
        public int height;
        public byte[] vox; // flattened snapshot: x + z*size + y*size*size
    }
    public struct BuildResult
    {
        public Chunk chunk;
        public Vector3[] verts;
        public Vector2[] uvs;
        public int[] tris;
    }

    readonly ConcurrentQueue<BuildRequest> q = new();
    readonly ConcurrentQueue<BuildResult> done = new();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        StartWorkers(Mathf.Max(1, maxWorkers));
    }

    public static void EnsureInstance()
    {
        if (Instance == null) new GameObject("ThreadedMesher").AddComponent<ThreadedMesher>();
    }

    void StartWorkers(int n)
    {
        for (int i = 0; i < n; i++)
            Task.Run(() => WorkerLoop());
    }

    public void Enqueue(Chunk ch, byte[] voxSnapshot)
    {
        q.Enqueue(new BuildRequest
        {
            chunk = ch,
            coord = ch.coord,
            size = Chunk.Size,
            height = Chunk.Height,
            vox = voxSnapshot
        });
    }

    void Update()
    {
        // Apply up to 2 finished meshes per frame to keep frames smooth
        int applied = 0;
        while (applied < 2 && done.TryDequeue(out var r))
        {
            if (r.chunk != null) r.chunk.ApplyBuiltMesh(r.verts, r.uvs, r.tris);
            applied++;
        }
    }

    // ================== worker-side code (NO Unity API calls here) ==================
    static readonly Vector3[] faceNormals = {
        Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back
    };
    static readonly Vector3[][] faceVerts = {
        new Vector3[]{ new(1,0,0), new(1,0,1), new(1,1,1), new(1,1,0) },
        new Vector3[]{ new(0,0,1), new(0,0,0), new(0,1,0), new(0,1,1) },
        new Vector3[]{ new(0,1,0), new(1,1,0), new(1,1,1), new(0,1,1) },
        new Vector3[]{ new(0,0,1), new(1,0,1), new(1,0,0), new(0,0,0) },
        new Vector3[]{ new(1,0,1), new(0,0,1), new(0,1,1), new(1,1,1) },
        new Vector3[]{ new(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0) },
    };
    static readonly int[] quadTris = { 0, 2, 1, 0, 3, 2 };

    static int Idx(int x, int y, int z, int size, int height) => x + z * size + y * size * size;

    static BlockType Get(byte[] vox, int x, int y, int z, int size, int height)
    {
        if (x < 0 || x >= size || y < 0 || y >= height || z < 0 || z >= size) return BlockType.Air;
        return (BlockType)vox[Idx(x, y, z, size, height)];
    }

    void WorkerLoop()
    {
        var verts = new List<Vector3>(100_000);
        var uvs = new List<Vector2>(100_000);
        var tris = new List<int>(150_000);

        while (true)
        {
            if (!q.TryDequeue(out var req))
            {
                System.Threading.Thread.Sleep(1);
                continue;
            }

            verts.Clear(); uvs.Clear(); tris.Clear();

            int size = req.size, height = req.height;
            for (int x = 0; x < size; x++)
                for (int y = 0; y < height; y++)
                    for (int z = 0; z < size; z++)
                    {
                        BlockType bt = (BlockType)req.vox[Idx(x, y, z, size, height)];
                        if (bt == BlockType.Air) continue;
                        var bdef = VoxelDefs.Blocks[(int)bt];
                        byte[] texIndex = { bdef.sideTex, bdef.sideTex, bdef.topTex, bdef.bottomTex, bdef.sideTex, bdef.sideTex };

                        for (int f = 0; f < 6; f++)
                        {
                            BlockType nb = Get(req.vox,
                                x + (int)Mathf.Round(faceNormals[f].x),
                                y + (int)Mathf.Round(faceNormals[f].y),
                                z + (int)Mathf.Round(faceNormals[f].z),
                                size, height);

                            // --- Face culling rules (water-inside faces removed)
                            var currDef = VoxelDefs.Blocks[(int)bt];
                            var neighDef = (nb == BlockType.Air) ? default : VoxelDefs.Blocks[(int)nb];

                            bool sameType = nb == bt;
                            bool neighSolid = (nb != BlockType.Air) && neighDef.solid;
                            bool neighTrans = (nb != BlockType.Air) && neighDef.transparent;

                            bool draw = false;
                            if (sameType)
                            {
                                // No faces between identical blocks (stone/stone, water/water, etc.)
                                draw = false;
                            }
                            else if (bt == BlockType.Water)
                            {
                                // Water renders only against air or opaque solids
                                draw = (nb == BlockType.Air) || (neighSolid && !neighTrans);
                            }
                            else if (currDef.transparent)
                            {
                                // Other transparents (e.g., leaves): render against air or any solid
                                draw = (nb == BlockType.Air) || neighSolid;
                            }
                            else
                            {
                                // Opaque: render against air or transparent neighbors
                                draw = (nb == BlockType.Air) || (neighTrans && !sameType);
                            }

                            if (!draw) continue;

                            int v0 = verts.Count;
                            var fv = faceVerts[f];
                            verts.Add(new Vector3(x, y, z) + fv[0]);
                            verts.Add(new Vector3(x, y, z) + fv[1]);
                            verts.Add(new Vector3(x, y, z) + fv[2]);
                            verts.Add(new Vector3(x, y, z) + fv[3]);

                            var tuv = VoxelDefs.TileUVs(texIndex[f]);
                            uvs.AddRange(tuv);

                            tris.Add(v0 + quadTris[0]); tris.Add(v0 + quadTris[1]); tris.Add(v0 + quadTris[2]);
                            tris.Add(v0 + quadTris[3]); tris.Add(v0 + quadTris[4]); tris.Add(v0 + quadTris[5]);
                        }
                    }

            done.Enqueue(new BuildResult
            {
                chunk = req.chunk,
                verts = verts.ToArray(),
                uvs = uvs.ToArray(),
                tris = tris.ToArray()
            });
        }
    }
}
