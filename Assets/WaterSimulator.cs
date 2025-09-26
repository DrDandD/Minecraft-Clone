using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lightweight Minecraft-style water solver (levels 0..7).
/// Runs a small number of cell updates per frame to avoid spikes.
/// </summary>
public class WaterSimulator : MonoBehaviour
{
    public static WaterSimulator Instance;

    [Tooltip("How many water cells to process per frame.")]
    public int budgetPerFrame = 512;

    [Tooltip("Enable 'infinite source' rule (2+ adjacent sources recreate a missing source).")]
    public bool infiniteSources = true;

    WorldGenerator world;
    readonly Queue<Vector3Int> q = new Queue<Vector3Int>(8192);
    readonly HashSet<Vector3Int> inQueue = new HashSet<Vector3Int>();

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        world = FindObjectOfType<WorldGenerator>();
    }

    void LateUpdate()
    {
        if (world == null) world = FindObjectOfType<WorldGenerator>();
        int budget = budgetPerFrame;
        while (budget-- > 0 && q.Count > 0)
        {
            var p = q.Dequeue();
            inQueue.Remove(p);
            Step(p);
        }
    }

    // --- Public API ----------------------------------------------------------

    /// <summary>Schedule a cell to be (re)solved.</summary>
    public void Enqueue(Vector3Int p)
    {
        if (inQueue.Add(p)) q.Enqueue(p);
    }

    /// <summary>Convenience: signal p and its 6 neighbors.</summary>
    public void NudgeWithNeighbors(Vector3Int p)
    {
        Enqueue(p);
        Enqueue(p + Vector3Int.up);
        Enqueue(p + Vector3Int.down);
        Enqueue(p + Vector3Int.left);
        Enqueue(p + Vector3Int.right);
        Enqueue(p + Vector3Int.forward);
        Enqueue(p + Vector3Int.back);
    }

    // --- Core solver ---------------------------------------------------------

    static readonly Vector3Int[] Horiz = new[]
    {
        Vector3Int.left, Vector3Int.right, Vector3Int.forward, Vector3Int.back
    };

    void Step(Vector3Int p)
    {
        if (!world.IsCoordLoaded(p)) return;

        var b = world.GetBlockGlobal(p);
        byte wl = world.GetWaterLevelGlobal(p);         // 255 = none, 0 = source, 1..7 = flowing

        // If solid block occupies the cell, clear water if any.
        if (b != BlockType.Air && b != BlockType.Water)
        {
            if (wl != VoxelDefs.WATER_NONE)
            {
                world.SetWaterLevelGlobal(p, VoxelDefs.WATER_NONE, rebuildMesh: false);
                Touch(p);
            }
            return;
        }

        // Recompute desired level from neighborhood (Minecraft-like rule).
        byte desired = ComputeDesiredLevel(p);

        // Infinite source rule: if disabled, skip this.
        if (infiniteSources && desired == VoxelDefs.WATER_NONE)
        {
            // If two or more adjacent (horizontal) cells are sources and top is blocked -> become source.
            int src = 0;
            for (int i = 0; i < 4; i++)
            {
                var n = p + Horiz[i];
                if (world.GetWaterLevelGlobal(n) == 0) src++;
            }
            var above = p + Vector3Int.up;
            bool topOpen = world.GetBlockGlobal(above) == BlockType.Air && world.GetWaterLevelGlobal(above) == VoxelDefs.WATER_NONE;
            if (src >= 2 && !topOpen) desired = 0;
        }

        // Apply change
        if (desired != wl)
        {
            if (desired == VoxelDefs.WATER_NONE)
            {
                if (b == BlockType.Water) world.SetBlockGlobal(p, BlockType.Air);
                world.SetWaterLevelGlobal(p, VoxelDefs.WATER_NONE, rebuildMesh: false);
            }
            else
            {
                if (b == BlockType.Air) world.SetBlockGlobal(p, BlockType.Water);
                world.SetWaterLevelGlobal(p, desired, rebuildMesh: false);
            }
            Touch(p);
        }
    }

    // MC-like recomputation of water level for one cell
    byte ComputeDesiredLevel(Vector3Int p)
    {
        var below = p + Vector3Int.down;

        // 1) If we can flow down, we become flowing and below becomes source-like (level 0)
        if (CanFlowInto(below))
            return 1; // falling/flowing here; below will be set to 0 by its own evaluation

        // 2) Otherwise, level is min(neighbor levels + 1)
        int best = 8; // 8 = none; we clamp to <=7 later

        // look at four horizontal neighbors
        for (int i = 0; i < 4; i++)
        {
            var n = p + Horiz[i];
            byte nl = world.GetWaterLevelGlobal(n);
            if (nl == VoxelDefs.WATER_NONE) continue;
            // treat solid below neighbor as higher cost, like MC
            if (!CanFlowInto(n + Vector3Int.down)) best = Mathf.Min(best, nl + 1);
            else best = Mathf.Min(best, 1); // neighbor can fall; this spot should also be low
        }

        // 3) If any water above, we become level 1 (falls down from above)
        var up = p + Vector3Int.up;
        if (world.GetWaterLevelGlobal(up) != VoxelDefs.WATER_NONE) best = Mathf.Min(best, 1);

        if (best <= 7) return (byte)best;

        // 4) No neighbors feeding us — no water desired here.
        return VoxelDefs.WATER_NONE;
    }

    bool CanFlowInto(Vector3Int p)
    {
        if (!world.IsCoordLoaded(p)) return false;
        var b = world.GetBlockGlobal(p);
        if (b == BlockType.Air) return true;
        if (b == BlockType.Water)
        {
            // allow if existing water is weaker (higher level number)
            byte wl = world.GetWaterLevelGlobal(p);
            return wl != 0; // we don't overwrite a source directly
        }
        // could add “replaceable plant” here later
        return false;
    }

    void Touch(Vector3Int p)
    {
        // signal neighbors to recompute
        Enqueue(p);
        Enqueue(p + Vector3Int.up);
        Enqueue(p + Vector3Int.down);
        for (int i = 0; i < 4; i++) Enqueue(p + Horiz[i]);
    }
}
