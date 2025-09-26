using UnityEngine;

public class Bootstrapper : MonoBehaviour
{
    public Material atlasMaterial;
    public WorldGenerator worldPrefab; // leave null to spawn a basic one

    void Start()
    {
        // World root
        WorldGenerator world = worldPrefab ? Instantiate(worldPrefab) : new GameObject("World").AddComponent<WorldGenerator>();
        world.blockMaterial = atlasMaterial;
        world.viewRadius = 2;   // 5x5 chunks
        world.seaLevel = 18;

        // Player
        var player = new GameObject("Player");
        player.transform.position = new Vector3(8, 28, 8);
        var cc = player.AddComponent<CharacterController>();
        cc.height = 1.8f; cc.radius = 0.35f; cc.center = new Vector3(0, 0.9f, 0);
        var pc = player.AddComponent<PlayerController>();
        var camGO = new GameObject("Camera");
        camGO.transform.SetParent(player.transform);
        camGO.transform.localPosition = new Vector3(0, 1.5f, 0);
        var cam = camGO.AddComponent<Camera>();
        pc.cam = cam.transform;

        // Interact + hotbar
        var hotbarGO = new GameObject("Hotbar");
        var hotbar = hotbarGO.AddComponent<Hotbar>();
        var inter = player.AddComponent<BlockInteractor>();
        inter.cam = cam; inter.world = world; inter.hotbar = hotbar;

        // assign player to world
        world.player = player.transform;

        // Light
        var light = new GameObject("Sun").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(50, -30, 0);
    }
}
