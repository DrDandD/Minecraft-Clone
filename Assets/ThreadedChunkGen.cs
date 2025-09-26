using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// Threaded voxel generator with biomes, height, Perlin-worm tunnels,
/// caverns & ravines (depth-boosted), and controlled surface openings.
/// Deterministic per seed. No UnityEngine API calls off the main thread.
public class ThreadedChunkGen : MonoBehaviour
{
    public static ThreadedChunkGen Instance { get; private set; }
    [Range(1, 8)] public int maxWorkers = 2;

    // ======== Snapshots (data-only structs) ========
    [Serializable] public struct LayerSnap { public byte block; public int thickness; }
    [Serializable]
    public struct TerrainSnap
    {
        public float baseHeight, amplitude, frequency;
        public int octaves; public float ridged;
        public float warpStrength, warpFrequency;
    }
    public enum SurfaceMode : byte { Default = 0, Ocean = 1, Mountain = 2 }
    [Serializable]
    public struct BiomeSnap
    {
        public SurfaceMode surfaceMode;
        public int snowLineY, beachBand;
        public float heightBias;
        public byte surfaceBlock;      // cast BlockType to byte
        public TerrainSnap terrain;
        public LayerSnap[] subsurface; // top-down order
    }

    public struct GenRequest
    {
        public Vector2Int coord;
        public int size, height, seaLevel;
        public int heightSmoothIters;
        public int seed;

        // Biome partition & blending
        public int cellMin, cellMax;
        public float jitter, weightSigmaFactor; public int weightSearchRadius; public float weightHardness;
        public BiomeSnap[] biomes;

        // --- Caves: field components (kept moderate)
        public float cavernFreq, cavernThreshold;
        public float ravineFreq, ravineWidth; public int ravineDepth;

        // Surface openings (gate)
        public float surfaceOpenChance; public int surfaceOpenDepth;

        // --- Perlin Worms (tunnels)
        public float wormCellSize;
        public float wormSpawnChance;
        public int wormMinLen, wormMaxLen;
        public float wormRadiusStart, wormRadiusEnd;
        public float wormDirFreq;
        public float wormPitchBias;       // -1..1 down/up bias
        public float wormBranchChance;
        public int wormMaxBranches;

        // --- Depth boosting (more caverns/ravines deeper down)
        public float depthCavernBoost;    // 0..1 lower threshold at depth
        public float depthRavineBoost;    // 0..1 loosen ravine gate at depth
        public int depthBoostStart;     // start depth (blocks below surface)
        public int depthBoostFull;      // full effect depth

        // --- Water bodies (height shaping) ---
        public float lakeNoiseFreq;      // e.g. 0.01
        public float lakeThreshold;      // 0..1  (e.g. 0.72)
        public int lakeMaxDepth;       // blocks to dip below local ground (e.g. 3)

        public float riverNoiseFreq;     // e.g. 0.002
        public float riverWidth;         // ~0.06..0.14 (controls lateral width)
        public int riverDepth;         // max blocks carved down (e.g. 7)
        public float continentFreq;     // e.g. 0.0015f
        public float continentAmp;      // e.g. 12f  (how much to pull down)

        // ------- Worm tunnels (thin, across all depths) -------
        public float wormRadiusMin;      // e.g., 0.6f
        public float wormRadiusMax;      // e.g., 1.4f
        public int wormDepthMin;       // absolute Y spawn min (e.g., 8)
        public int wormDepthMax;       // absolute Y spawn max (e.g., 72)
        public float wormStepJitter;     // 0..0.5 step length jitter (e.g., 0.15f)

        // ------- Cavern bands (scattered pockets, not one layer) -------
        public float cavernBandA;        // normalized depth from surface (0..1), e.g., 0.35
        public float cavernBandB;        // e.g., 0.70
        public float cavernBandSigma;    // Gaussian width (0.05..0.25), e.g., 0.14
        public float cavernBandMixB;     // weight for band B (0..1), e.g., 0.6
        public float cavernGateFreq;     // very low freq gate to break networks (e.g., 0.004)
        public float cavernGateThresh;   // 0..1 gate threshold (e.g., 0.58)
    }

    public struct GenResult { public Vector2Int coord; public byte[] voxels; }

    readonly ConcurrentQueue<GenRequest> q = new();
    readonly ConcurrentQueue<GenResult> done = new();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        StartWorkers(Mathf.Max(1, maxWorkers));
    }
    public static void EnsureInstance()
    {
        if (Instance == null) new GameObject("ThreadedChunkGen").AddComponent<ThreadedChunkGen>();
    }
    void StartWorkers(int n) { for (int i = 0; i < n; i++) Task.Run(WorkerLoop); }
    public void Enqueue(GenRequest r) => q.Enqueue(r);

    void Update()
    {
        int applied = 0;
        while (applied < 4 && done.TryDequeue(out var r))
        {
            if (WorldGenerator.TryApplyGeneratedVoxels(r)) applied++;
        }
    }

    // ================= worker-side math utils =================
    static uint RotL(uint x, int r) => (x << r) | (x >> (32 - r));
    static uint HashU(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)x * 0x9E3779B1u; h = RotL(h, 13);
            h ^= (uint)y * 0x85EBCA6Bu; h *= 0xC2B2AE35u;
            h ^= h >> 16; return h;
        }
    }
    static float Hash01(int x, int y, int seed) => HashU(x, y, seed) / 4294967295f;

    static int CellSizeFor(int cx, int cz, int seed, int min, int max)
    {
        float r = Hash01(cx, cz, seed);
        return Mathf.RoundToInt(Mathf.Lerp(min, max, r));
    }
    static Vector2 SitePos(int cx, int cz, int seed, int min, int max, float jitter)
    {
        int size = CellSizeFor(cx, cz, seed, min, max);
        float j = jitter * 0.5f * size;
        float ox = (Hash01(cx, cz * 31 + 7, seed) - 0.5f) * 2f * j;
        float oz = (Hash01(cx * 17 + 5, cz, seed) - 0.5f) * 2f * j;
        return new Vector2(cx * size + size * 0.5f + ox, cz * size + size * 0.5f + oz);
    }

    static float FBM2(float x, float z, float f, int oct)
    {
        float a = 0.5f, sum = 0f;
        for (int i = 0; i < Mathf.Max(1, oct); i++) { sum += a * Mathf.PerlinNoise(x * f, z * f); f *= 2f; a *= 0.5f; }
        return sum;
    }
    static float Ridged2(float x, float z, float f, int oct)
    {
        float a = 0.5f, sum = 0f;
        for (int i = 0; i < Mathf.Max(1, oct); i++)
        {
            float n = 1f - Mathf.Abs(2f * Mathf.PerlinNoise(x * f, z * f) - 1f);
            sum += a * n; f *= 2f; a *= 0.5f;
        }
        return sum;
    }
    static Vector2 Warp2(float x, float z, float s, float f)
    {
        if (s <= 0f || f <= 0f) return Vector2.zero;
        float nx = Mathf.PerlinNoise((x + 123f) * f, (z + 321f) * f);
        float nz = Mathf.PerlinNoise((x + 987f) * f, (z + 654f) * f);
        return new Vector2((nx - 0.5f) * 2f * s, (nz - 0.5f) * 2f * s);
    }

    static float Noise3(float x, float y, float z, float f, int off = 0)
    {
        float a = Mathf.PerlinNoise((x + off) * f, (z - off) * f);
        float b = Mathf.PerlinNoise((x - off * 2) * f, (y + off) * f);
        float c = Mathf.PerlinNoise((y + off * 3) * f, (z - off * 4) * f);
        return (a + b + c) / 3f;
    }
    static float FBM3(float x, float y, float z, float f, int oct, int off = 0)
    {
        float a = 0.5f, sum = 0f;
        for (int i = 0; i < Mathf.Max(1, oct); i++) { sum += a * Noise3(x, y, z, f, off + i * 97); f *= 2f; a *= 0.5f; }
        return sum;
    }
    static float Ridge2(float x, float z, float f) { float n = Mathf.PerlinNoise(x * f, z * f); return 1f - Mathf.Abs(2f * n - 1f); }
    static float Hash3_01(int x, int y, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)(x * 747796405) + 2891336453u; h = (h << 13) | (h >> 19);
            h ^= (uint)(y * 277803737) + 2891336453u; h *= 0xC2B2AE35u;
            h ^= (uint)(z * 19999999) + 2891336453u; h ^= h >> 16;
            return h / 4294967295f;
        }
    }

    static float RiverBand01(int wx, int wz, int seed, float freq)
    {
        // Base perlin
        float n = Mathf.PerlinNoise(wx * freq + seed * 0.37f, wz * freq - seed * 0.29f); // 0..1
        float band = 1f - Mathf.Abs(n - 0.5f) * 2f;                                      // ~0 at banks, 1 at center
                                                                                         // Soften edges (ease curve)
        band = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(band));
        band = band * band; // a bit sharper core, softer banks
        return band;
    }

    static float LakeNoise01(int wx, int wz, int seed, float freq)
    {
        return Mathf.PerlinNoise((wx + seed * 0.11f) * freq, (wz - seed * 0.17f) * freq);
    }

    // --------- Biome weights (Gaussian over Voronoi sites) ---------
    static void SampleWeights(int wx, int wz, float[] outW, GenRequest r)
    {
        int N = r.biomes.Length; for (int i = 0; i < N; i++) outW[i] = 0f;

        int approx = (r.cellMin + r.cellMax) / 2;
        int ccx = Mathf.FloorToInt((float)wx / Mathf.Max(8, approx));
        int ccz = Mathf.FloorToInt((float)wz / Mathf.Max(8, approx));

        float sum = 0f; int R = Mathf.Max(1, r.weightSearchRadius);
        for (int dz = -R; dz <= R; dz++)
            for (int dx = -R; dx <= R; dx++)
            {
                int cx = ccx + dx, cz = ccz + dz;
                Vector2 s = SitePos(cx, cz, r.seed, r.cellMin, r.cellMax, r.jitter);
                int cell = CellSizeFor(cx, cz, r.seed, r.cellMin, r.cellMax);
                float sigma = Mathf.Max(1f, r.weightSigmaFactor * cell);
                float s2 = 2f * sigma * sigma;
                float d2 = Vector2.SqrMagnitude(new Vector2(wx, wz) - s);
                float w = Mathf.Exp(-d2 / s2);
                int bi = (int)(HashU(cx, cz, r.seed) % (uint)N);
                outW[bi] += w; sum += w;
            }

        if (sum <= 0f) { outW[0] = 1f; return; }
        float inv = 1f / sum, hard = Mathf.Max(1f, r.weightHardness), sumH = 0f;
        for (int i = 0; i < N; i++) { float n = outW[i] * inv; n = Mathf.Pow(n, hard); outW[i] = n; sumH += n; }
        float invH = 1f / Mathf.Max(1e-6f, sumH);
        for (int i = 0; i < N; i++) outW[i] *= invH;
    }
    static int HeightAt(int wx, int wz, float[] w, GenRequest r)
    {
        // --- biome-driven base height ---
        float hBase = 0f;
        for (int i = 0; i < r.biomes.Length; i++)
        {
            var b = r.biomes[i]; var t = b.terrain;
            Vector2 warp = Warp2(wx, wz, t.warpStrength, t.warpFrequency);
            float nx = wx + warp.x, nz = wz + warp.y;
            float nf = FBM2(nx, nz, t.frequency, t.octaves);
            float nr = Ridged2(nx, nz, t.frequency, t.octaves);
            float n = Mathf.Lerp(nf, nr, t.ridged);
            float bi = t.baseHeight + n * t.amplitude + b.heightBias;
            hBase += w[i] * bi;
        }

        float h = hBase;

        // --- Rivers (smooth valley; ensure center goes below seaLevel) ---
        if (r.riverNoiseFreq > 0f && r.riverWidth > 0f && r.riverDepth > 0)
        {
            float band = RiverBand01(wx, wz, r.seed, r.riverNoiseFreq);   // 0..1 (1 = center)
                                                                          // Width control: keep only the inner portion; fade banks
            float wGate = Mathf.Clamp01(r.riverWidth);                    // ~0.06..0.14 typical
            float s = Mathf.SmoothStep(0f, 1f, (band - (1f - wGate)) / Mathf.Max(1e-5f, wGate)); // 0..1 strength
            if (s > 0f)
            {
                // Desired river bed (at least 1–3 blocks below sea at the core)
                int bedTarget = r.seaLevel - (1 + Mathf.RoundToInt(2f * s));
                // Carve depth relative to local relief; shallower if far above sea
                float relief = Mathf.Clamp01(1f - (hBase - r.seaLevel) * 0.02f);
                float maxCut = r.riverDepth * s * relief;
                float hCarved = Mathf.Min(hBase - maxCut, bedTarget);
                // Blend (avoid hard cliffs): stronger blend near centerline
                float blend = s * 0.85f;
                h = Mathf.Lerp(h, hCarved, blend);
            }
        }

        // --- Lakes (depressions that end up under seaLevel) ---
        if (r.lakeNoiseFreq > 0f && r.lakeMaxDepth > 0)
        {
            float ln = LakeNoise01(wx, wz, r.seed, r.lakeNoiseFreq);      // 0..1
            float th = Mathf.Clamp01(r.lakeThreshold);                    // e.g. 0.70–0.78
            if (ln > th)
            {
                float t = (ln - th) / Mathf.Max(1e-5f, 1f - th);          // 0..1 strength
                int lakeTarget = r.seaLevel - (1 + Mathf.RoundToInt(t * r.lakeMaxDepth));
                float blend = t * t;                                      // softer rim, deeper center
                h = Mathf.Lerp(h, lakeTarget, blend);
            }
        }

        return Mathf.FloorToInt(h);
    }
    static float HeightBaseBiomes(int wx, int wz, float[] weights, GenRequest r)
    {
        float h = 0f;
        for (int i = 0; i < r.biomes.Length; i++)
        {
            float w = weights[i]; if (w <= 0f) continue;
            var b = r.biomes[i]; var t = b.terrain;
            Vector2 warp = Warp2(wx, wz, t.warpStrength, t.warpFrequency);
            float nx = wx + warp.x, nz = wz + warp.y;
            float nf = FBM2(nx, nz, t.frequency, t.octaves);
            float nr = Ridged2(nx, nz, t.frequency, t.octaves);
            float n = Mathf.Lerp(nf, nr, t.ridged);
            h += w * (t.baseHeight + n * t.amplitude + b.heightBias);
        }
        return h;
    }

    // Recompute biome weights per sample → no seams at chunk borders
    static int HeightSmooth(int wx, int wz, GenRequest r)
    {
        // center
        float[] wC = new float[r.biomes.Length];
        SampleWeights(wx, wz, wC, r);
        float hC = HeightBaseBiomes(wx, wz, wC, r);

        // 4-neighbors
        float[] wX1 = new float[r.biomes.Length]; SampleWeights(wx + 1, wz, wX1, r);
        float[] wX0 = new float[r.biomes.Length]; SampleWeights(wx - 1, wz, wX0, r);
        float[] wZ1 = new float[r.biomes.Length]; SampleWeights(wx, wz + 1, wZ1, r);
        float[] wZ0 = new float[r.biomes.Length]; SampleWeights(wx, wz - 1, wZ0, r);

        float h = (hC
                  + HeightBaseBiomes(wx + 1, wz, wX1, r)
                  + HeightBaseBiomes(wx - 1, wz, wX0, r)
                  + HeightBaseBiomes(wx, wz + 1, wZ1, r)
                  + HeightBaseBiomes(wx, wz - 1, wZ0, r)) / 5f;

        // --- add CONTINENT sink (huge low-freq basins → oceans/coasts) ---
        if (r.continentFreq > 0f && r.continentAmp > 0f)
        {
            float c = Mathf.PerlinNoise((wx + r.seed * 0.13f) * r.continentFreq,
                                        (wz - r.seed * 0.19f) * r.continentFreq); // 0..1
                                                                                  // Turn into a broad sink: high c ⇒ strong sink; smooth edges
            float sink = Mathf.SmoothStep(0f, 1f, c) * r.continentAmp;
            h -= sink;
        }

        // --- Rivers & lakes shaping (your latest version) ---
        // Reuse the *exact* river/lake code you currently have (after computing h),
        // operating on world coords (wx,wz) and r.seaLevel. If you used HeightAt
        // before for that part, move that river/lake logic here so it runs on h.

        return Mathf.FloorToInt(h);
    }
    // Caverns & ravines with (1) mixed depth metrics and (2) drifting band centers,
    // so preferred cavern heights vary across the world. Sparse gate avoids mega-nets.
    static bool FieldCarve(int wx, int wy, int wz, int surfaceY, GenRequest r)
    {
        // Relative depth (from local surface) and absolute depth (from world top)
        float depthFromTop = Mathf.Max(0, surfaceY - wy);
        float relDepth01 = (surfaceY > 0) ? depthFromTop / Mathf.Max(1f, surfaceY) : 1f; // 0 (surface) .. 1 (toward bedrock)
        float absDepth01 = 1f - (wy / Mathf.Max(1f, r.height - 1));                      // 0 (sky) .. 1 (bedrock)

        // Blend them so caverns aren’t locked to a single world Y
        const float depthMix = 0.55f; // 0=only relative, 1=only absolute
        float mixedDepth01 = Mathf.Lerp(relDepth01, absDepth01, depthMix);

        // Surface/bedrock falloff (scaled to height)
        float surfFalloff = Mathf.SmoothStep(0f, 1f, depthFromTop / Mathf.Max(1, r.surfaceOpenDepth + 8));
        const int bedrockCutoff = 3;
        int bedrockBand = Mathf.Max(16, Mathf.RoundToInt(r.height * 0.08f));
        float bedrockFalloff = Mathf.SmoothStep(0f, 1f, (wy - bedrockCutoff) / Mathf.Max(1f, (bedrockBand - bedrockCutoff)));
        float falloff = surfFalloff * bedrockFalloff;

        // Depth boost (your usual controls)
        float t = 0f;
        int start = Mathf.Max(0, r.depthBoostStart);
        int full = Mathf.Max(start + 1, r.depthBoostFull);
        if (depthFromTop >= start) t = Mathf.Clamp01((depthFromTop - start) / (full - start));

        // ---- Drifting cavern bands ----
        // Base band centers from settings (0..1), plus a very low-freq XY jitter so
        // the favorite cavern depths wander across the world instead of one constant layer.
        float bandJitter = 0.18f;         // how far bands can drift (in normalized depth)
        float bandFreq = 0.0018f;       // how slowly they drift across (wx,wz)

        float jitterA = (Mathf.PerlinNoise(wx * bandFreq + r.seed * 0.13f,
                                           wz * bandFreq - r.seed * 0.21f) - 0.5f) * 2f * bandJitter;
        float jitterB = (Mathf.PerlinNoise(wx * bandFreq + r.seed * 0.43f,
                                           wz * bandFreq + r.seed * 0.07f) - 0.5f) * 2f * bandJitter;

        float a = Mathf.Clamp01(r.cavernBandA + jitterA);
        float b = Mathf.Clamp01(r.cavernBandB + jitterB);
        float sig = Mathf.Max(0.05f, r.cavernBandSigma); // width of each band

        float wA = Mathf.Exp(-0.5f * (mixedDepth01 - a) * (mixedDepth01 - a) / (sig * sig));
        float wB = Mathf.Exp(-0.5f * (mixedDepth01 - b) * (mixedDepth01 - b) / (sig * sig)) * Mathf.Clamp01(r.cavernBandMixB);
        float bandWeight = Mathf.Clamp01(wA + wB);

        // Base 3D cavern field + a low-freq density mod to vary “how caverns” a region is
        float cav = FBM3(wx, wy, wz, r.cavernFreq, 4, r.seed + 211);
        float density2D = Mathf.PerlinNoise(wx * 0.0022f + r.seed * 0.31f, wz * 0.0022f - r.seed * 0.19f); // 0..1
        float densityMul = Mathf.Lerp(0.85f, 1.35f, density2D); // some regions richer/poorer in caverns
        cav *= densityMul;

        // Sparse 2D gate (breaks huge connectivity)
        float gate2D = Mathf.PerlinNoise(wx * r.cavernGateFreq + r.seed * 0.27f,
                                         wz * r.cavernGateFreq - r.seed * 0.11f);
        bool gateOpen = gate2D > Mathf.Clamp01(r.cavernGateThresh);

        // Threshold relaxed with depth
        float cavThresh = Mathf.Clamp01(r.cavernThreshold - Mathf.Clamp01(r.depthCavernBoost) * t);

        bool cavern = gateOpen && (cav * bandWeight * falloff) > cavThresh;

        // ---- Ravines (scaled by bands so they also vary in favored depth) ----
        float ridge = Ridge2(wx, wz, r.ravineFreq);
        float baseGate = Mathf.Clamp01(1f - r.ravineWidth * 0.1f);
        float ravGate = Mathf.Clamp01(baseGate - Mathf.Clamp01(r.depthRavineBoost) * 0.3f * t);
        int maxDepth = r.ravineDepth + Mathf.RoundToInt(12f * t);
        bool withinDepth = (depthFromTop <= Mathf.Max(6, maxDepth)) && wy < surfaceY && wy > bedrockCutoff + 1;

        bool ravine = (ridge * bandWeight * bedrockFalloff) > ravGate && withinDepth && surfFalloff > 0.4f;

        return cavern || ravine;
    }

    static void CarveWorms(byte[] mask, GenRequest r, Vector3Int chunkOrigin, int size, int height, int seaLevel)
    {
        // Spawn grid extended around the owner chunk (for seamless borders)
        float cell = Mathf.Max(12f, r.wormCellSize);

        int margin = Mathf.CeilToInt(Mathf.Max(r.wormRadiusMax, 1f)) + 2;
        int minX = chunkOrigin.x - margin, maxX = chunkOrigin.x + size + margin - 1;
        int minZ = chunkOrigin.z - margin, maxZ = chunkOrigin.z + size + margin - 1;

        int cellMinX = Mathf.FloorToInt(minX / cell) - 1;
        int cellMaxX = Mathf.FloorToInt(maxX / cell) + 1;
        int cellMinZ = Mathf.FloorToInt(minZ / cell) - 1;
        int cellMaxZ = Mathf.FloorToInt(maxZ / cell) + 1;

        // Auto depth range if 0/0 was supplied (scales to world height)
        int ySpawnMin = Mathf.Clamp((r.wormDepthMin > 0 ? r.wormDepthMin : Mathf.RoundToInt(height * 0.05f)), 1, height - 2);
        int ySpawnMax = Mathf.Clamp((r.wormDepthMax > 0 ? r.wormDepthMax : Mathf.RoundToInt(height * 0.85f)), ySpawnMin + 1, height - 1);

        // Tunables (local so you don’t need to add fields)
        const float depthBiasK = 1.3f;      // >1 favors deeper spawns a bit
        const int maxSpawnsPerCell = 3;   // spawn 1–3 worms per eligible cell
        const float extraSpawnChance = 0.55f; // chance to attempt 2nd/3rd worm

        for (int cz = cellMinZ; cz <= cellMaxZ; cz++)
            for (int cx = cellMinX; cx <= cellMaxX; cx++)
            {
                uint hseed = HashU(cx, cz, r.seed);
                System.Random rng = new System.Random((int)hseed);

                if (rng.NextDouble() > Mathf.Clamp01(r.wormSpawnChance)) continue;

                // 1–3 worms per cell, depending on a quick coin-flip
                int spawns = 1;
                if (rng.NextDouble() < extraSpawnChance) spawns++;
                if (rng.NextDouble() < extraSpawnChance * 0.5f) spawns = Mathf.Min(maxSpawnsPerCell, spawns + 1);

                for (int s = 0; s < spawns; s++)
                {
                    // Start anywhere in the cell
                    float sx = (float)(cx + rng.NextDouble()) * cell;
                    float sz = (float)(cz + rng.NextDouble()) * cell;

                    // Uniform depth with a slight bias toward deeper Y
                    float u = (float)rng.NextDouble();
                    u = Mathf.Pow(u, depthBiasK); // bias
                    float sy = Mathf.Lerp(ySpawnMin, ySpawnMax, u);

                    int steps = Mathf.Clamp(rng.Next(r.wormMinLen, r.wormMaxLen + 1), 12, 260);
                    int branchesLeft = Mathf.Clamp(r.wormMaxBranches, 0, 6);

                    float dirF = Mathf.Clamp(r.wormDirFreq, 0.015f, 0.07f);
                    float rMin = Mathf.Clamp(r.wormRadiusMin, 0.4f, 2.0f);
                    float rMax = Mathf.Clamp(Mathf.Max(r.wormRadiusMax, rMin), rMin, 3.0f);
                    float stepJit = Mathf.Clamp01(r.wormStepJitter); // 0..1

                    void Trace(Vector3 start, int maxSteps, float radiusStart, float radiusEnd, int seedOff)
                    {
                        Vector3 wp = start;

                        for (int i = 0; i < maxSteps; i++)
                        {
                            // Smooth 3D direction with slight pitch bias
                            float nx = Noise3(wp.x, wp.y, wp.z, dirF, r.seed + seedOff);
                            float ny = Noise3(wp.x + 777, wp.y + 321, wp.z + 123, dirF, r.seed + seedOff * 3);
                            float nz = Noise3(wp.x + 999, wp.y + 555, wp.z + 222, dirF, r.seed + seedOff * 5);
                            Vector3 dir = new Vector3(nx - 0.5f, ny - 0.5f + r.wormPitchBias * 0.2f, nz - 0.5f);
                            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
                            dir.Normalize();

                            // Small jittered step → avoids squarey artifacts
                            float j = 1f + ((float)rng.NextDouble() * 2f - 1f) * stepJit;
                            wp += dir * j;

                            // Thin, slightly tapering radius
                            float t = (float)i / Mathf.Max(1, maxSteps - 1);
                            float rad = Mathf.Lerp(radiusStart, radiusEnd, t);
                            rad = Mathf.Clamp(rad, rMin, rMax);

                            int cx0 = Mathf.FloorToInt(wp.x - rad) - 1;
                            int cy0 = Mathf.FloorToInt(wp.y - rad) - 1;
                            int cz0 = Mathf.FloorToInt(wp.z - rad) - 1;
                            int cx1 = Mathf.FloorToInt(wp.x + rad) + 1;
                            int cy1 = Mathf.FloorToInt(wp.y + rad) + 1;
                            int cz1 = Mathf.FloorToInt(wp.z + rad) + 1;

                            for (int yy = cy0; yy <= cy1; yy++)
                            {
                                if (yy < 1 || yy >= height) continue;
                                for (int zz = cz0; zz <= cz1; zz++)
                                {
                                    if (zz < minZ || zz > maxZ) continue;
                                    for (int xx = cx0; xx <= cx1; xx++)
                                    {
                                        if (xx < minX || xx > maxX) continue;
                                        float d2 = (xx - wp.x) * (xx - wp.x) + (yy - wp.y) * (yy - wp.y) + (zz - wp.z) * (zz - wp.z);
                                        if (d2 <= rad * rad)
                                        {
                                            int lx = xx - chunkOrigin.x;
                                            int lz = zz - chunkOrigin.z;
                                            if ((uint)lx < (uint)size && (uint)lz < (uint)size)
                                            {
                                                int idx = lx + lz * size + yy * size * size;
                                                mask[idx] = 1;
                                            }
                                        }
                                    }
                                }
                            }

                            // Rare deterministic branching to keep networks light
                            if (branchesLeft > 0 && i > 12)
                            {
                                float br = Hash3_01(Mathf.FloorToInt(wp.x) + i * 3,
                                                    Mathf.FloorToInt(wp.y) + i * 7,
                                                    Mathf.FloorToInt(wp.z) + i * 11,
                                                    r.seed + 7777);
                                if (br < Mathf.Clamp01(r.wormBranchChance))
                                {
                                    branchesLeft--;
                                    Trace(wp, Mathf.Max(8, maxSteps - i - 4), radiusStart * 0.9f, radiusEnd * 0.85f, seedOff + 17);
                                }
                            }
                        }
                    }

                    float rStart = Mathf.Lerp(rMin, rMax, (float)rng.NextDouble() * 0.5f);
                    float rEnd = Mathf.Lerp(rMin * 0.8f, rStart, 0.5f);
                    Trace(new Vector3(sx, sy, sz), steps, rStart, rEnd, 13 + s * 31);
                }
            }
    }

    static int FlatIdx(int x, int y, int z, int size, int height) => x + z * size + y * size * size;

    // ================= worker loop =================
    void WorkerLoop()
    {
        while (true)
        {
            if (!q.TryDequeue(out var req)) { System.Threading.Thread.Sleep(1); continue; }

            int size = req.size, height = req.height;
            var vox = new byte[size * size * height];
            var carveMask = new byte[size * size * height]; // 1 = carved by worms
            var tmpLayers = new List<LayerSnap>(4);
            float[] weights = new float[Mathf.Max(1, req.biomes.Length)];

            Vector3Int origin = new Vector3Int(req.coord.x * size, 0, req.coord.y * size);

            // Precompute worm mask for chunk (with margin inside function)
            CarveWorms(carveMask, req, origin, size, height, req.seaLevel);

            for (int x = 0; x < size; x++)
                for (int z = 0; z < size; z++)
                {
                    int wx = origin.x + x;
                    int wz = origin.z + z;

                    SampleWeights(wx, wz, weights, req);
                    int h = HeightSmooth(wx, wz, req);

                    // Safety: keep surface within array bounds
                    if (h < 1) h = 1;
                    if (h > height - 2) h = height - 2;

                    // Bedrock
                    vox[FlatIdx(x, 0, z, size, height)] = (byte)BlockType.Bedrock;

                    // Dominant biome
                    int dom = 0; float best = weights[0];
                    for (int i = 1; i < weights.Length; i++) if (weights[i] > best) { best = weights[i]; dom = i; }
                    var biome = req.biomes[dom];

                    // Surface block
                    BlockType surface;
                    if (biome.surfaceMode == SurfaceMode.Ocean) surface = BlockType.Sand;
                    else if (biome.surfaceMode == SurfaceMode.Mountain) surface = (h >= biome.snowLineY) ? BlockType.Snow : BlockType.Stone;
                    else surface = (h <= req.seaLevel + Mathf.Max(0, biome.beachBand)) ? BlockType.Sand : (BlockType)biome.surfaceBlock;

                    vox[FlatIdx(x, h, z, size, height)] = (byte)surface;

                    // Layers then stone with carving
                    tmpLayers.Clear(); if (biome.subsurface != null) tmpLayers.AddRange(biome.subsurface);

                    int y = h - 1;
                    for (int li = 0; li < tmpLayers.Count && y > 0; li++)
                    {
                        var L = tmpLayers[li]; int t = Mathf.Max(0, L.thickness);
                        for (int k = 0; k < t && y > 0; k++, y--)
                        {
                            bool carved = FieldCarve(wx, y, wz, h, req) || carveMask[FlatIdx(x, y, z, size, height)] == 1;
                            vox[FlatIdx(x, y, z, size, height)] = (byte)(carved ? BlockType.Air : (BlockType)L.block);
                        }
                    }
                    for (; y > 0; y--)
                    {
                        bool carved = FieldCarve(wx, y, wz, h, req) || carveMask[FlatIdx(x, y, z, size, height)] == 1;
                        vox[FlatIdx(x, y, z, size, height)] = (byte)(carved ? BlockType.Air : BlockType.Stone);
                    }

                    // Fill to sea level (lakes/ocean)
                    int waterStart = Mathf.Clamp(h + 1, 1, height - 1);
                    for (int yy = waterStart; yy <= req.seaLevel && yy < height; yy++)
                        vox[FlatIdx(x, yy, z, size, height)] = (byte)BlockType.Water;

                    // Surface opening gate (occasional mouths only)
                    if (h >= 1 && h < height)
                    {
                        bool wormHitSurface = carveMask[FlatIdx(x, h, z, size, height)] == 1;
                        bool fieldHitSurface = FieldCarve(wx, h, wz, h, req);
                        bool allowOpen = false;

                        if (wormHitSurface)
                        {
                            float openRnd = Hash3_01(wx, h, wz, req.seed + 909);
                            allowOpen = openRnd < Mathf.Clamp01(req.surfaceOpenChance);
                        }
                        if (fieldHitSurface)
                        {
                            float openRnd2 = Hash3_01(wx, h, wz, req.seed + 1234);
                            allowOpen |= openRnd2 < Mathf.Clamp01(req.surfaceOpenChance * 0.35f);
                        }

                        if (allowOpen)
                        {
                            vox[FlatIdx(x, h, z, size, height)] = (byte)BlockType.Air;
                            if (h + 1 < height) vox[FlatIdx(x, h + 1, z, size, height)] = (byte)BlockType.Air;
                        }
                        else
                        {
                            // ensure surface stays solid
                            vox[FlatIdx(x, h, z, size, height)] = (byte)surface;
                        }
                    }

                    // Air above
                    int airStart = Mathf.Clamp(Mathf.Max(h, req.seaLevel) + 1, 1, height);
                    for (int yy = airStart; yy < height; yy++)
                        vox[FlatIdx(x, yy, z, size, height)] = (byte)BlockType.Air;
                }

            done.Enqueue(new GenResult { coord = req.coord, voxels = vox });
        }
    }
}
