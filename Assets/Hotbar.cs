using UnityEngine;

public class Hotbar : MonoBehaviour
{
    public BlockType[] slots = new BlockType[9] {
        BlockType.Stone, BlockType.Dirt, BlockType.Grass, BlockType.Sand, BlockType.Wood,
        BlockType.Leaves, BlockType.Snow, BlockType.Water, BlockType.Stone
    };
    public int selected = 0;
    public BlockType SelectedBlock => slots[Mathf.Clamp(selected, 0, slots.Length - 1)];

    void Update()
    {
        for (int i = 0; i < 9; i++) if (Input.GetKeyDown(KeyCode.Alpha1 + i)) selected = i;
        float scroll = Input.mouseScrollDelta.y;
        if (scroll > 0) selected = (selected + slots.Length - 1) % slots.Length;
        if (scroll < 0) selected = (selected + 1) % slots.Length;
    }
}
