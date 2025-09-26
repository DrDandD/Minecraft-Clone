// ================================
// Unity Mini-Minecraft MVP (Streaming + Biomes + No self-placement)
// ================================

// ----- FILE: VoxelDefs.cs -----
using System.Collections.Generic;
using System.Drawing;
using UnityEngine;
using UnityEngine.LightTransport;

public enum BlockType : byte
{
    Air = 0,
    Bedrock = 1,
    Stone = 2,
    Dirt = 3,
    Grass = 4,
    Wood = 5,
    Water = 6,
    Sand = 7,
    Snow = 8,
    Leaves = 9,
    Cactus = 10,
}

[System.Serializable]
public struct BlockInfo
{
    public bool solid;
    public bool transparent;
    public byte topTex;
    public byte sideTex;
    public byte bottomTex;
}

public static class VoxelDefs
{
    // NOTE: Make sure your atlas includes tiles for Sand (index 7) and Snow (index 8).
    public const int AtlasTilesPerRow = 4;   // 4x4 = 16 tiles (indices 0..15)
    public const float TileUV = 1f / AtlasTilesPerRow;

    public const byte WATER_NONE = 255;   // no water
    public const byte WATER_MAX = 7;     // 0 = source, 1..7 = flowing

    public static readonly BlockInfo[] Blocks = new BlockInfo[] {
        // 0 Air
        new BlockInfo { solid=false, transparent=true, topTex=0, sideTex=0, bottomTex=0 },
        // 1 Bedrock
        new BlockInfo { solid=true,  transparent=false, topTex=1, sideTex=1, bottomTex=1 },
        // 2 Stone
        new BlockInfo { solid=true,  transparent=false, topTex=2, sideTex=2, bottomTex=2 },
        // 3 Dirt
        new BlockInfo { solid=true,  transparent=false, topTex=3, sideTex=3, bottomTex=3 },
        // 4 Grass (top/side/bottom)
        new BlockInfo { solid=true,  transparent=false, topTex=4, sideTex=5, bottomTex=3 },
        // 5 Wood (placeholder)
        new BlockInfo { solid=true,  transparent=false, topTex=6, sideTex=6, bottomTex=6 },
        // 6 Water (render faces next to opaque; non-solid here)
        new BlockInfo { solid=false, transparent=true,  topTex=7, sideTex=7, bottomTex=7 },
        // 7 Sand
        new BlockInfo { solid=true,  transparent=false, topTex=8, sideTex=8, bottomTex=8 },
        // 8 Snow
        new BlockInfo { solid=true,  transparent=false, topTex=9, sideTex=9, bottomTex=9 },
        // 9 Leaves
        new BlockInfo { solid=true,  transparent=false, topTex=13, sideTex=13, bottomTex=13 },
        // 10 Cactus
        new BlockInfo { solid=true,  transparent=false, topTex=12, sideTex=10, bottomTex=12 },
    };

    public static Vector2[] TileUVs(byte tileIndex)
    {
        int x = tileIndex % AtlasTilesPerRow;
        int y = tileIndex / AtlasTilesPerRow;
        // Start indexing at TOP-LEFT
        int flippedY = (AtlasTilesPerRow - 1) - y;
        float u = x * TileUV;
        float v = flippedY * TileUV;
        float o = 0.001f; // bleed guard
        return new Vector2[] {
            new Vector2(u + o, v + o),
            new Vector2(u + TileUV - o, v + o),
            new Vector2(u + TileUV - o, v + TileUV - o),
            new Vector2(u + o, v + TileUV - o)
        };
    }
}
