using UnityEngine;

public class BlockInteractor : MonoBehaviour
{
    public Camera cam;
    public WorldGenerator world;
    public float maxReach = 6f;
    public Hotbar hotbar;


    private void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }
    void Update()
    {
        if (Input.GetMouseButtonDown(0)) TryBreak();
        if (Input.GetMouseButtonDown(1)) TryPlace();
    }

    void TryBreak()
    {
        if (RaycastBlock(out Vector3Int hit, out Vector3Int normal))
        {
            var b = world.GetBlockGlobal(hit);
            if (b != BlockType.Bedrock) world.SetBlockGlobal(hit, BlockType.Air);
        }
    }

    void TryPlace()
    {
        if (RaycastBlock(out Vector3Int hit, out Vector3Int normal))
        {
            Vector3Int placePos = hit + normal;
            BlockType sel = hotbar ? hotbar.SelectedBlock : BlockType.Stone;
            if (sel != BlockType.Air) world.SetBlockGlobal(placePos, sel);
        }
    }

    bool RaycastBlock(out Vector3Int blockPos, out Vector3Int normalOut)
    {
        blockPos = default; normalOut = default;
        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, maxReach))
        {
            Vector3 p = hit.point - hit.normal * 0.5f;
            blockPos = Vector3Int.FloorToInt(p);
            normalOut = Vector3Int.RoundToInt(hit.normal);
            return true;
        }
        return false;
    }
}
