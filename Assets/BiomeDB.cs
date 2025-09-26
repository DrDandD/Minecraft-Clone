using UnityEngine;

[CreateAssetMenu(fileName = "BiomeDB", menuName = "Voxels/Biome Database", order = 1)]
public class BiomeDB : ScriptableObject
{
    public BiomeDef[] biomes;
}
