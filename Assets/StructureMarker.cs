using UnityEngine;

#if UNITY_EDITOR
[ExecuteAlways]
#endif
public class StructureMarker : MonoBehaviour
{
    public BlockType block = BlockType.Wood;
    public bool onlyPlaceIntoAir = true;

#if UNITY_EDITOR
    // simple gizmo so you can see the voxel
    void OnDrawGizmos()
    {
        Gizmos.matrix = Matrix4x4.TRS(transform.position + new Vector3(0.5f, 0.5f, 0.5f), Quaternion.identity, Vector3.one);
        Gizmos.color = Color.white;
        Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
    }
#endif
}
