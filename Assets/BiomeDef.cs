using UnityEngine;

[System.Serializable]
public struct LayerDef
{
    public BlockType block;
    [Min(0)] public int thickness; // blocks under surface
}

public enum BiomeSurfaceMode { Default, Ocean, Mountain }

[System.Serializable]
public struct TerrainSettings
{
    [Header("Base shape")]
    public float baseHeight;     // absolute offset for this biome
    public float amplitude;      // vertical scale for this biome
    public float frequency;      // base frequency (world units^-1)
    [Range(1, 8)] public int octaves; // FBM octaves (1..8)

    [Header("Ridged/warp")]
    [Range(0, 1)] public float ridged;     // 0 = FBM, 1 = fully ridged
    public float warpStrength;            // domain warp magnitude
    public float warpFrequency;           // domain warp scale
}

[CreateAssetMenu(fileName = "BiomeDef", menuName = "Voxels/Biome Definition", order = 0)]
public class BiomeDef : ScriptableObject
{
    [Header("Identity")]
    public string biomeName = "New Biome";

    [Header("Spawn weighting")]
    [Min(0f)] public float spawnWeight = 1f; // 0 = never, 1 = normal, >1 = more frequent

    [Header("Climate center (0..1)")]
    [Range(0, 1)] public float tempCenter = 0.5f;
    [Range(0, 1)] public float moistCenter = 0.5f;

    [Header("Spread & height bias")]
    [Range(0.05f, 0.6f)] public float falloff = 0.30f; // bigger = wider biome
    public float heightBias = 0f;                      // small +/- bias AFTER terrain eval

    [Header("Terrain profile (per-biome)")]
    public TerrainSettings terrain;

    [Header("Surface rules")]
    public BiomeSurfaceMode surfaceMode = BiomeSurfaceMode.Default;
    public int snowLineY = 60;                   // used when surfaceMode = Mountain
    public int beachBand = 1;                    // extra sand band near sea level (Default)

    [Header("Blocks")]
    public BlockType surface = BlockType.Grass;  // default surface (or seafloor for Ocean)
    public LayerDef[] subsurface = new LayerDef[] {
        new LayerDef { block = BlockType.Dirt, thickness = 3 }
    };
}
