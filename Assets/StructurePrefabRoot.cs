using UnityEngine;

#if UNITY_EDITOR
[ExecuteAlways]
#endif
public class StructurePrefabRoot : MonoBehaviour
{
    [Header("What biomes can spawn this (leave empty = any)")]
    public BiomeDef[] allowedBiomes;

    public StructureAnchor anchor = StructureAnchor.OnSurface;

    [Header("Spawn settings (baked into StructureDef)")]
    [Range(0f, 1f)] public float chancePerChunk = 0.15f;
    [Min(0)] public int maxPerChunk = 2;
    [Min(0)] public int triesPerChunk = 6;
    public int minSurfaceY = 0;
    public int maxSurfaceY = 9999;

    [Header("Grid")]
    public float gridSize = 1f;           // your voxel size (1 if your blocks are 1x1x1)
    public bool snapChildrenToGrid = true;

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!snapChildrenToGrid || gridSize <= 0f) return;
        var children = GetComponentsInChildren<Transform>(includeInactive: true);
        foreach (var t in children)
        {
            if (t == transform) continue;
            Vector3 p = t.position;
            p.x = Mathf.Round(p.x / gridSize) * gridSize;
            p.y = Mathf.Round(p.y / gridSize) * gridSize;
            p.z = Mathf.Round(p.z / gridSize) * gridSize;
            t.position = p;
        }
    }
#endif
}
