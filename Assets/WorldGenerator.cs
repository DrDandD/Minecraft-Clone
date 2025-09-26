using System.Collections.Generic;
using UnityEngine;

public class WorldGenerator : MonoBehaviour
{
    public Material blockMaterial;
    public int viewRadius = 2;
    public Transform player;

    // ---- SEED ----
    [Header("World Seed")]
    public int seed = 12345; // set in inspector; 0 = random at runtime

    // chunk map
    static Dictionary<Vector2Int, Chunk> chunks = new();  // static so generator callback can find them
    private Vector2Int lastPlayerChunk;

    [Header("Global Sea Level")]
    public int seaLevel = 26;

    // --- Voronoi + blend (same as before) ---
    [Header("Biome Partition (Voronoi)")]
    [Min(8)] public int biomeCellSizeMin = 96;
    [Min(8)] public int biomeCellSizeMax = 192;
    [Range(0f, 1f)] public float biomeJitter = 0.85f;

    [Header("Biome Blending")]
    [Range(0.2f, 1.2f)] public float weightSigmaFactor = 0.55f;
    [Min(1)] public int weightSearchRadius = 3;
    [Range(1f, 2f)] public float weightHardness = 1.15f;

    [Header("Height Smoothing")]
    [Range(0, 3)] public int heightSmoothIters = 1;

    [Header("Biomes (Scriptable Objects)")]
    public BiomeDB biomeDB;

    [Header("Structures (Scriptable Objects)")]
    public StructureDB structureDB; 

    [Header("Water Bodies")]
    [Range(0.002f, 0.03f)] public float riverNoiseFreq = 0.0045f;
    [Range(0.02f, 0.2f)] public float riverWidth = 0.10f;  // larger = wider rivers
    [Min(1)] public int riverDepth = 7;

    [Range(0.004f, 0.03f)] public float lakeNoiseFreq = 0.012f;
    [Range(0f, 0.98f)] public float lakeThreshold = 0.74f;   // lower = more lakes
    [Min(1)] public int lakeMaxDepth = 3;

    [Header("Continents / Oceans")]
    [Range(0.0008f, 0.004f)] public float continentFreq = 0.0016f;
    [Range(0f, 30f)] public float continentAmp = 18f;

    struct PendingBlock { public Vector3Int pos; public BlockType block; public bool onlyAir; }
    readonly Dictionary<Vector2Int, List<PendingBlock>> pendingWrites = new();

    private static readonly List<BoundsInt> structureReservations = new();

    // ====== Snapshots for generator ======
    ThreadedChunkGen.BiomeSnap[] BuildBiomeSnapshot()
    {
        int n = biomeDB != null && biomeDB.biomes != null ? biomeDB.biomes.Length : 0;
        if (n == 0) return new ThreadedChunkGen.BiomeSnap[] { DefaultBiomeSnap() };
        var arr = new ThreadedChunkGen.BiomeSnap[n];
        for (int i = 0; i < n; i++)
        {
            var b = biomeDB.biomes[i];
            var s = new ThreadedChunkGen.BiomeSnap
            {
                surfaceMode = (ThreadedChunkGen.SurfaceMode)(int)b.surfaceMode,
                snowLineY = b.snowLineY,
                beachBand = b.beachBand,
                heightBias = b.heightBias,
                surfaceBlock = (byte)b.surface,
                terrain = new ThreadedChunkGen.TerrainSnap
                {
                    baseHeight = b.terrain.baseHeight,
                    amplitude = b.terrain.amplitude,
                    frequency = b.terrain.frequency,
                    octaves = b.terrain.octaves,
                    ridged = b.terrain.ridged,
                    warpStrength = b.terrain.warpStrength,
                    warpFrequency = b.terrain.warpFrequency
                },
                subsurface = ToLayerSnaps(b.subsurface)
            };
            arr[i] = s;
        }
        return arr;
    }
    ThreadedChunkGen.LayerSnap[] ToLayerSnaps(LayerDef[] src)
    {
        if (src == null || src.Length == 0) return System.Array.Empty<ThreadedChunkGen.LayerSnap>();
        var a = new ThreadedChunkGen.LayerSnap[src.Length];
        for (int i = 0; i < src.Length; i++) a[i] = new ThreadedChunkGen.LayerSnap { block = (byte)src[i].block, thickness = src[i].thickness };
        return a;
    }
    ThreadedChunkGen.BiomeSnap DefaultBiomeSnap()
    {
        return new ThreadedChunkGen.BiomeSnap
        {
            surfaceMode = ThreadedChunkGen.SurfaceMode.Default,
            snowLineY = seaLevel + 30,
            beachBand = 1,
            heightBias = 0f,
            surfaceBlock = (byte)BlockType.Grass,
            terrain = new ThreadedChunkGen.TerrainSnap
            {
                baseHeight = seaLevel + 8,
                amplitude = 14,
                frequency = 0.03f,
                octaves = 4,
                ridged = 0f,
                warpStrength = 6f,
                warpFrequency = 0.02f
            },
            subsurface = new ThreadedChunkGen.LayerSnap[] { new ThreadedChunkGen.LayerSnap { block = (byte)BlockType.Dirt, thickness = 3 } }
        };
    }

    public static bool TryApplyGeneratedVoxels(ThreadedChunkGen.GenResult r)
    {
        if (!chunks.TryGetValue(r.coord, out var ch) || ch == null) return false;
        ch.ApplyGeneratedVoxels(r.voxels);          // fill voxel array fast
        var wg = ch.world;
        wg?.PopulateStructures(ch);                 // place structures (batched)
        wg?.ApplyPendingToChunk(ch);                // apply deferred cross-chunk
        ch.RebuildMeshAsync();                      // build mesh off-thread
        return true;
    }

    void EnqueuePending(Vector2Int chunkCoord, Vector3Int pos, BlockType b, bool onlyAir)
    {
        if (!pendingWrites.TryGetValue(chunkCoord, out var list))
        {
            list = new List<PendingBlock>(32);
            pendingWrites[chunkCoord] = list;
        }
        list.Add(new PendingBlock { pos = pos, block = b, onlyAir = onlyAir });
    }
    public void ApplyPendingToChunk(Chunk ch)
    {
        if (!pendingWrites.TryGetValue(ch.coord, out var list) || list.Count == 0) return;
        for (int i = 0; i < list.Count; i++)
        {
            var pb = list[i];
            if (pb.pos.y <= 0 || pb.pos.y >= Chunk.Height) continue;
            if (pb.onlyAir && GetBlockGlobal(pb.pos) != BlockType.Air) continue;
            ch.SetBlockGlobal(pb.pos, pb.block, rebuild: false);
        }
        pendingWrites.Remove(ch.coord);
    }

    static Vector3Int Rotate90Y(Vector3Int v, int rot)
    {
        // rot = 0,1,2,3 -> 0°,90°,180°,270°
        rot &= 3;
        switch (rot)
        {
            case 1: return new Vector3Int(-v.z, v.y, v.x);
            case 2: return new Vector3Int(-v.x, v.y, -v.z);
            case 3: return new Vector3Int(v.z, v.y, -v.x);
            default: return v;
        }
    }
    public void PopulateStructures(Chunk ch)
    {
        if (structureDB == null || structureDB.structures == null || structureDB.structures.Length == 0) return;

        // deterministic RNG per chunk based on world seed
        int s = (seed == 0 ? 12345 : seed);
        int rndSeed = ch.coord.x * 73856093 ^ ch.coord.y * 19349663 ^ s;
        System.Random rng = new System.Random(rndSeed);

        // ---- helpers ----
        static Vector3Int Rot90Y(Vector3Int v, int rot)
        {
            rot &= 3; // 0,1,2,3 -> 0°,90°,180°,270°
            return rot switch
            {
                1 => new Vector3Int(-v.z, v.y, v.x),
                2 => new Vector3Int(-v.x, v.y, -v.z),
                3 => new Vector3Int(v.z, v.y, -v.x),
                _ => v
            };
        }
        static BoundsInt MakeAabb(List<Vector3Int> points, int pad = 0)
        {
            if (points.Count == 0) return new BoundsInt(Vector3Int.zero, Vector3Int.zero);
            int minx = points[0].x, miny = points[0].y, minz = points[0].z;
            int maxx = minx, maxy = miny, maxz = minz;
            for (int i = 1; i < points.Count; i++)
            {
                var p = points[i];
                if (p.x < minx) minx = p.x; if (p.x > maxx) maxx = p.x;
                if (p.y < miny) miny = p.y; if (p.y > maxy) maxy = p.y;
                if (p.z < minz) minz = p.z; if (p.z > maxz) maxz = p.z;
            }
            var min = new Vector3Int(minx - pad, miny - pad, minz - pad);
            var size = new Vector3Int((maxx - minx) + 1 + pad * 2,
                                      (maxy - miny) + 1 + pad * 2,
                                      (maxz - minz) + 1 + pad * 2);
            return new BoundsInt(min, size);
        }
        static bool AABBOverlap(BoundsInt A, BoundsInt B)
        {
            // BoundsInt is min-inclusive, size-based. Convert to [min, maxExclusive)
            return
                (A.xMin < B.xMax && A.xMax > B.xMin) &&
                (A.yMin < B.yMax && A.yMax > B.yMin) &&
                (A.zMin < B.zMax && A.zMax > B.zMin);
        }

        static bool OverlapsAny(BoundsInt aabb, List<BoundsInt> list)
        {
            for (int i = 0; i < list.Count; i++)
                if (AABBOverlap(aabb, list[i])) return true;
            return false;
        }
        bool IsSolid(BlockType b) => (b != BlockType.Air) && VoxelDefs.Blocks[(int)b].solid;

        bool IsPendingOccupied(Vector3Int pos)
        {
            var key = new Vector2Int(Mathf.FloorToInt((float)pos.x / Chunk.Size),
                                     Mathf.FloorToInt((float)pos.z / Chunk.Size));
            if (!pendingWrites.TryGetValue(key, out var list)) return false;
            for (int i = 0; i < list.Count; i++)
                if (list[i].pos == pos) return true;
            return false;
        }

        // gates
        const int shoreMargin = 1; // blocks above sea level required
        const int maxSlope = 2;    // avoid cliff edges
        const int aabbPad = 1;     // grow the footprint slightly to keep spacing

        HashSet<Vector2Int> touched = new HashSet<Vector2Int>();
        bool wroteInOwner = false;

        foreach (var def in structureDB.structures)
        {
            if (def == null) continue;

            int placed = 0;
            for (int attempt = 0; attempt < def.triesPerChunk && placed < def.maxPerChunk; attempt++)
            {
                if (rng.NextDouble() > def.chancePerChunk) continue;

                // pick an anchor column inside this chunk
                int lx = rng.Next(0, Chunk.Size);
                int lz = rng.Next(0, Chunk.Size);
                int wx = ch.coord.x * Chunk.Size + lx;
                int wz = ch.coord.y * Chunk.Size + lz;

                // biome filter
                if (def.allowedBiomes != null && def.allowedBiomes.Length > 0 && biomeDB != null && biomeDB.biomes != null)
                {
                    int domIdx = WG_DominantBiomeAt(wx, wz);
                    if (domIdx < 0 || domIdx >= biomeDB.biomes.Length) continue;
                    var dom = biomeDB.biomes[domIdx];
                    bool ok = System.Array.IndexOf(def.allowedBiomes, dom) >= 0;
                    if (!ok) continue;
                }

                // surface & water checks
                int surfaceY = GetSurfaceY(wx, wz);
                if (surfaceY < def.minSurfaceY || surfaceY > def.maxSurfaceY) continue;
                if (surfaceY <= seaLevel + shoreMargin) continue;

                var surf = GetBlockGlobal(new Vector3Int(wx, surfaceY, wz));
                if (surf == BlockType.Water) continue;

                var under = GetBlockGlobal(new Vector3Int(wx, surfaceY - 1, wz));
                if (!IsSolid(under)) continue;

                // slope gate
                int sN = Mathf.Abs(GetSurfaceY(wx, wz + 1) - surfaceY);
                int sS = Mathf.Abs(GetSurfaceY(wx, wz - 1) - surfaceY);
                int sE = Mathf.Abs(GetSurfaceY(wx + 1, wz) - surfaceY);
                int sW = Mathf.Abs(GetSurfaceY(wx - 1, wz) - surfaceY);
                if (Mathf.Max(Mathf.Max(sN, sS), Mathf.Max(sE, sW)) > maxSlope) continue;

                // final anchor & rotation
                Vector3Int anchor = new Vector3Int(wx, surfaceY, wz);
                if (def.anchor == StructureAnchor.OnSeafloor) anchor.y = seaLevel;
                int rot = def.allowRotate90 ? rng.Next(0, 4) : 0;

                // Build world-space positions list (and compute AABB) to preflight overlap/air checks
                var worldPositions = new List<Vector3Int>(def.blocks.Length);
                for (int i = 0; i < def.blocks.Length; i++)
                {
                    var sb = def.blocks[i];
                    Vector3Int offs = def.allowRotate90 ? Rot90Y(sb.offset, rot) : sb.offset;
                    Vector3Int pos = anchor + offs;
                    if ((uint)pos.y >= (uint)Chunk.Height) continue;
                    worldPositions.Add(pos);
                }
                if (worldPositions.Count == 0) continue;

                // AABB overlap test vs already reserved structures
                var aabb = MakeAabb(worldPositions, aabbPad);
                if (OverlapsAny(aabb, structureReservations)) continue;

                // Preflight voxel overlap: if any block would overwrite non-air (or a pending write), skip.
                bool blocked = false;
                for (int i = 0; i < def.blocks.Length && !blocked; i++)
                {
                    var sb = def.blocks[i];
                    Vector3Int offs = def.allowRotate90 ? Rot90Y(sb.offset, rot) : sb.offset;
                    Vector3Int pos = anchor + offs;
                    if ((uint)pos.y >= (uint)Chunk.Height) continue;

                    // If onlyAir, we must not touch anything occupied now or in pending.
                    if (sb.onlyPlaceIntoAir)
                    {
                        if (GetBlockGlobal(pos) != BlockType.Air || IsPendingOccupied(pos)) { blocked = true; break; }
                    }
                    else
                    {
                        // Even when allowed to overwrite terrain, avoid stamping on *other structures*:
                        // if pending has a non-air write for pos, treat as occupied.
                        if (IsPendingOccupied(pos)) { blocked = true; break; }
                    }
                }
                if (blocked) continue;

                // Group per destination chunk
                var perChunk = new Dictionary<Vector2Int, List<(Vector3Int pos, BlockType b, bool onlyAir)>>(8);
                foreach (var sb in def.blocks)
                {
                    Vector3Int offs = def.allowRotate90 ? Rot90Y(sb.offset, rot) : sb.offset;
                    Vector3Int pos = anchor + offs;
                    if ((uint)pos.y >= (uint)Chunk.Height) continue;

                    Vector2Int target = new Vector2Int(
                        Mathf.FloorToInt((float)pos.x / Chunk.Size),
                        Mathf.FloorToInt((float)pos.z / Chunk.Size)
                    );

                    if (!perChunk.TryGetValue(target, out var list))
                    {
                        list = new List<(Vector3Int, BlockType, bool)>(32);
                        perChunk[target] = list;
                    }
                    perChunk[target].Add((pos, sb.block, sb.onlyPlaceIntoAir));
                }

                // Write (or defer) blocks now
                bool wroteAnyThisTry = false;
                foreach (var kv in perChunk)
                {
                    Vector2Int target = kv.Key;
                    var list = kv.Value;

                    if (chunks.TryGetValue(target, out var targetChunk) && targetChunk != null)
                    {
                        if (!targetChunk.VoxelsReady)
                        {
                            foreach (var item in list) EnqueuePending(target, item.pos, item.b, item.onlyAir);
                            continue;
                        }

                        foreach (var item in list)
                        {
                            if (item.onlyAir && GetBlockGlobal(item.pos) != BlockType.Air) continue;
                            targetChunk.SetBlockGlobal(item.pos, item.b, rebuild: false);
                            wroteAnyThisTry = true;
                        }

                        touched.Add(target);
                        if (target == ch.coord) wroteInOwner = true;
                    }
                    else
                    {
                        foreach (var item in list) EnqueuePending(target, item.pos, item.b, item.onlyAir);
                    }
                }

                if (wroteAnyThisTry)
                {
                    placed++;
                    // Reserve the footprint so later placements (this chunk or neighbors) won’t overlap it
                    structureReservations.Add(aabb);
                }
            }
        }

        // refresh meshes where we wrote
        if (wroteInOwner) ch.RebuildMeshAsync();
        foreach (var cc in touched)
        {
            if (cc == ch.coord) continue;
            if (chunks.TryGetValue(cc, out var nb) && nb != null) nb.RebuildMeshAsync();
        }
    }

    // ====== Block queries (unchanged from your latest) ======
    public BlockType GetBlockGlobal(Vector3Int wp)
    {
        Vector2Int c = new Vector2Int(Mathf.FloorToInt((float)wp.x / Chunk.Size), Mathf.FloorToInt((float)wp.z / Chunk.Size));
        if (chunks.TryGetValue(c, out var ch)) return ch.GetBlockGlobal(wp);
        // fallback heuristic when chunk not loaded
        return BlockType.Air;
    }

    public void SetBlockGlobal(Vector3Int wp, BlockType b)
    {
        Vector2Int c = new Vector2Int(Mathf.FloorToInt((float)wp.x / Chunk.Size), Mathf.FloorToInt((float)wp.z / Chunk.Size));
        if (chunks.TryGetValue(c, out var ch)) { ch.SetBlockGlobal(wp, b, rebuild: false); ch.RebuildMeshAsync(); }
        else SpawnChunk(c).SetBlockGlobal(wp, b, rebuild: false);
        TryRebuildNeighborsAround(wp);
    }

    void TryRebuildNeighborsAround(Vector3Int wp)
    {
        Vector2Int c = new Vector2Int(Mathf.FloorToInt((float)wp.x / Chunk.Size), Mathf.FloorToInt((float)wp.z / Chunk.Size));
        int lx = wp.x - c.x * Chunk.Size, lz = wp.z - c.y * Chunk.Size;
        if (lx == 0 && chunks.TryGetValue(c + Vector2Int.left, out var L)) L.RebuildMeshAsync();
        if (lx == Chunk.Size - 1 && chunks.TryGetValue(c + Vector2Int.right, out var R)) R.RebuildMeshAsync();
        if (lz == 0 && chunks.TryGetValue(c + Vector2Int.down, out var D)) D.RebuildMeshAsync();
        if (lz == Chunk.Size - 1 && chunks.TryGetValue(c + Vector2Int.up, out var U)) U.RebuildMeshAsync();
    }

    public int GetSurfaceY(int wx, int wz)
    {
        Vector2Int c = new Vector2Int(Mathf.FloorToInt((float)wx / Chunk.Size),
                                      Mathf.FloorToInt((float)wz / Chunk.Size));
        if (chunks.TryGetValue(c, out var ch) && ch != null)
        {
            int lx = wx - c.x * Chunk.Size;
            int lz = wz - c.y * Chunk.Size;
            lx = Mathf.Clamp(lx, 0, Chunk.Size - 1);
            lz = Mathf.Clamp(lz, 0, Chunk.Size - 1);
            // scan down from top for first non-air
            for (int y = Chunk.Height - 1; y >= 0; y--)
            {
                if (ch.GetBlock(lx, y, lz) != BlockType.Air)
                    return y;
            }
        }
        // If chunk not loaded yet, return something safe.
        return seaLevel;
    }

    // ====== Streaming ======
    void Start()
    {
        if (seed == 0) seed = Random.Range(1, int.MaxValue);
        ThreadedChunkGen.EnsureInstance();
        ThreadedMesher.EnsureInstance();
        if (player == null) { var p = GameObject.Find("Player"); if (p) player = p.transform; }
        UpdateStreaming(true);
    }

    void Update()
    {
        Vector2Int playerChunk = new Vector2Int(
            Mathf.FloorToInt(player.position.x / Chunk.Size),
            Mathf.FloorToInt(player.position.z / Chunk.Size)
        );

        // spawn around player
        for (int cz = -viewRadius; cz <= viewRadius; cz++)
            for (int cx = -viewRadius; cx <= viewRadius; cx++)
            {
                Vector2Int c = new Vector2Int(playerChunk.x + cx, playerChunk.y + cz);
                if (!chunks.ContainsKey(c)) SpawnChunk(c);
            }

        // unload too-far chunks
        var toRemove = new List<Vector2Int>();
        foreach (var kv in chunks)
        {
            if (Mathf.Abs(kv.Key.x - playerChunk.x) > viewRadius + 1 ||
                Mathf.Abs(kv.Key.y - playerChunk.y) > viewRadius + 1)
            {
                toRemove.Add(kv.Key);
            }
        }
        foreach (var c in toRemove)
        {
            Destroy(chunks[c].gameObject);
            chunks.Remove(c);
        }
    }


    void UpdateStreaming(bool force = false)
    {
        Vector2Int pc = PlayerChunkCoord();
        if (!force && pc == lastPlayerChunk) return;
        lastPlayerChunk = pc;

        for (int dz = -viewRadius; dz <= viewRadius; dz++)
            for (int dx = -viewRadius; dx <= viewRadius; dx++)
            {
                Vector2Int c = new Vector2Int(pc.x + dx, pc.y + dz);
                if (!chunks.ContainsKey(c)) SpawnChunk(c);
            }

        // (optional) unload far chunks…
    }

    Vector2Int PlayerChunkCoord()
    {
        if (player == null) return Vector2Int.zero;
        Vector3 p = player.position;
        return new Vector2Int(Mathf.FloorToInt(p.x / Chunk.Size), Mathf.FloorToInt(p.z / Chunk.Size));
    }

    Chunk SpawnChunk(Vector2Int c)
    {
        if (chunks.TryGetValue(c, out var existing)) return existing;

        // Create empty chunk object first (very cheap)
        GameObject go = new GameObject($"Chunk_{c.x}_{c.y}");
        go.transform.parent = transform;
        go.transform.position = new Vector3(c.x * Chunk.Size, 0, c.y * Chunk.Size);
        go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        go.AddComponent<MeshCollider>();
        mr.sharedMaterial = blockMaterial;

        var ch = go.AddComponent<Chunk>();
        ch.Bootstrap(this, c); // no heavy gen here

        chunks[c] = ch;

        // Enqueue async voxel generation for this coord
        ThreadedChunkGen.Instance.Enqueue(new ThreadedChunkGen.GenRequest
        {
            coord = c,
            size = Chunk.Size,
            height = Chunk.Height,
            seaLevel = seaLevel,
            heightSmoothIters = heightSmoothIters,
            seed = seed,
            // biome partition
            cellMin = biomeCellSizeMin,
            cellMax = biomeCellSizeMax,
            jitter = biomeJitter,
            weightSigmaFactor = weightSigmaFactor,
            weightSearchRadius = weightSearchRadius,
            weightHardness = weightHardness,

            continentFreq = continentFreq,
            continentAmp = continentAmp,
            // biomes
            biomes = BuildBiomeSnapshot(),
            // Caves field (gentle defaults)
            cavernFreq = 0.04f,
            cavernThreshold = 0.62f,
            ravineFreq = 0.009f,
            ravineWidth = 3.0f,
            ravineDepth = 28,

            // Surface openings
            surfaceOpenChance = 0.08f,
            surfaceOpenDepth = 8,      // mouths don’t hug the grass

            // Perlin worms
            wormCellSize = 48f,
            wormSpawnChance = 1f,
            wormMinLen = 40,
            wormMaxLen = 140,
            wormRadiusStart = 2.0f,
            wormRadiusEnd = 0.9f,
            wormDirFreq = 0.035f,
            wormPitchBias = -0.15f,
            wormBranchChance = 0.04f,
            wormMaxBranches = 2,

            // Depth boosting
            depthCavernBoost = 0.25f,
            depthRavineBoost = 0.20f,
            depthBoostStart = 16,
            depthBoostFull = 48,

            // Worms
            wormRadiusMin = 3f,
            wormRadiusMax = 12f,
            wormDepthMin = 8,
            wormDepthMax = Chunk.Height - 328,
            wormStepJitter = 0.15f,

            cavernBandA = 0.28f,   // shallower pockets
            cavernBandB = 0.62f,   // deeper pockets
            cavernBandSigma = 0.12f,   // width of each band
            cavernBandMixB = 0.65f,   // weight for the deeper band
            cavernGateFreq = 0.0035f, // sparser connectivity
            cavernGateThresh = 0.60f,
    });


        return ch;
    }

    // ---------- Biome query helpers for structure placement ----------
    uint WG_HashU(int x, int y)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)x * 0x9E3779B1u; h = (h << 13) | (h >> 19);
            h ^= (uint)y * 0x85EBCA6Bu; h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return h;
        }
    }
    float WG_Hash01(int x, int y) => (WG_HashU(x, y) / 4294967295f);

    int WG_CellSizeFor(int cx, int cz)
    {
        float r = WG_Hash01(cx, cz);
        return Mathf.RoundToInt(Mathf.Lerp(biomeCellSizeMin, biomeCellSizeMax, r));
    }
    Vector2 WG_SitePos(int cx, int cz)
    {
        int size = WG_CellSizeFor(cx, cz);
        float j = biomeJitter * 0.5f * size;
        float ox = (WG_Hash01(cx, cz * 31 + 7) - 0.5f) * 2f * j;
        float oz = (WG_Hash01(cx * 17 + 5, cz) - 0.5f) * 2f * j;
        return new Vector2(cx * size + size * 0.5f + ox,
                           cz * size + size * 0.5f + oz);
    }
    int WG_BiomeIndexForSite(int cx, int cz)
    {
        if (biomeDB == null || biomeDB.biomes == null || biomeDB.biomes.Length == 0) return 0;

        // Sum positive weights
        float total = 0f;
        for (int i = 0; i < biomeDB.biomes.Length; i++)
        {
            var b = biomeDB.biomes[i];
            if (b == null) continue;
            total += Mathf.Max(0f, b.spawnWeight);
        }
        if (total <= 0f)
        {
            // fallback: old hash-mod behaviour
            return (int)(WG_HashU(cx, cz) % (uint)biomeDB.biomes.Length);
        }

        // Deterministic 0..1 per cell
        float r = WG_Hash01(cx, cz);
        float pick = r * total;

        float acc = 0f;
        for (int i = 0; i < biomeDB.biomes.Length; i++)
        {
            var b = biomeDB.biomes[i];
            if (b == null) continue;
            float w = Mathf.Max(0f, b.spawnWeight);
            acc += w;
            if (pick <= acc) return i;
        }
        // safety
        return biomeDB.biomes.Length - 1;
    }


    // Write normalized weights at (wx,wz) using Gaussian over nearby sites
    void WG_SampleBiomeWeights(int wx, int wz, float[] outW)
    {
        int N = (biomeDB != null && biomeDB.biomes != null) ? biomeDB.biomes.Length : 1;
        if (outW.Length < N) return;
        for (int i = 0; i < N; i++) outW[i] = 0f;

        int approx = (biomeCellSizeMin + biomeCellSizeMax) / 2;
        int ccx = Mathf.FloorToInt((float)wx / Mathf.Max(8, approx));
        int ccz = Mathf.FloorToInt((float)wz / Mathf.Max(8, approx));

        float sum = 0f; int R = Mathf.Max(1, weightSearchRadius);
        for (int dz = -R; dz <= R; dz++)
            for (int dx = -R; dx <= R; dx++)
            {
                int cx = ccx + dx, cz = ccz + dz;
                Vector2 s = WG_SitePos(cx, cz);
                int cell = WG_CellSizeFor(cx, cz);
                float sigma = Mathf.Max(1f, weightSigmaFactor * cell);
                float s2 = 2f * sigma * sigma;
                float d2 = Vector2.SqrMagnitude(new Vector2(wx, wz) - s);
                float w = Mathf.Exp(-d2 / s2);

                int bi = WG_BiomeIndexForSite(cx, cz);
                outW[bi] += w; sum += w;
            }
        if (sum <= 0f) { outW[0] = 1f; return; }

        float inv = 1f / sum, hard = Mathf.Max(1f, weightHardness), sumH = 0f;
        for (int i = 0; i < N; i++) { float n = outW[i] * inv; n = Mathf.Pow(n, hard); outW[i] = n; sumH += n; }
        float invH = 1f / Mathf.Max(1e-6f, sumH);
        for (int i = 0; i < N; i++) outW[i] *= invH;
    }

    int WG_DominantBiomeAt(int wx, int wz)
    {
        int N = (biomeDB != null && biomeDB.biomes != null) ? biomeDB.biomes.Length : 1;
        float[] w = new float[Mathf.Max(1, N)];
        WG_SampleBiomeWeights(wx, wz, w);
        int idx = 0; float best = w[0];
        for (int i = 1; i < N; i++) if (w[i] > best) { best = w[i]; idx = i; }
        return idx;
    }

}
