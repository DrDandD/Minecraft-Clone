using UnityEngine;

public enum StructureAnchor { OnSurface, OnSeafloor }

[System.Serializable]
public struct StructureBlock
{
    public Vector3Int offset;     // relative to anchor
    public BlockType block;
    public bool onlyPlaceIntoAir; // don't overwrite terrain if true
}

[CreateAssetMenu(fileName = "StructureDef", menuName = "Voxels/Structure Definition", order = 10)]
public class StructureDef : ScriptableObject
{
    public string structureName = "New Structure";
    public BiomeDef[] allowedBiomes;            // empty = all
    public StructureAnchor anchor = StructureAnchor.OnSurface;

    [Header("Spawning")]
    [Range(0f, 1f)] public float chancePerChunk = 0.15f;
    [Min(0)] public int maxPerChunk = 2;
    [Min(0)] public int triesPerChunk = 6;

    [Header("Placement constraints")]
    public int minSurfaceY = 0;
    public int maxSurfaceY = 9999;

    [Header("Blocks (relative to anchor)")]
    public StructureBlock[] blocks;
    public string id;
    public struct SBlock { public Vector3Int offset; public BlockType block; public bool onlyPlaceIntoAir; }


    [Header("Rotation")]
    public bool allowRotate90 = true;

}

[CreateAssetMenu(fileName = "StructureDB", menuName = "Voxels/Structure Database", order = 11)]
public class StructureDB : ScriptableObject
{
    public StructureDef[] structures;
}
