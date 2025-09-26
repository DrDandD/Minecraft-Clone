using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(StructurePrefabRoot))]
public class StructureExporterEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var root = (StructurePrefabRoot)target;
        GUILayout.Space(8);
        EditorGUILayout.HelpBox("Build your structure under this GameObject using child cubes with StructureMarker.\nClick Export to create a StructureDef asset.", MessageType.Info);

        using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
        {
            if (GUILayout.Button("Export → StructureDef Asset", GUILayout.Height(32)))
            {
                Export(root);
            }
        }
    }

    [MenuItem("Tools/Voxels/Export Selected Root to StructureDef", priority = 10)]
    static void ExportSelectedMenu()
    {
        var root = Selection.activeGameObject ? Selection.activeGameObject.GetComponent<StructurePrefabRoot>() : null;
        if (root) new StructureExporterEditor().Export(root);
        else EditorUtility.DisplayDialog("Export Structure", "Select a GameObject with StructurePrefabRoot.", "OK");
    }

    void Export(StructurePrefabRoot root)
    {
        if (root == null) return;

        var markers = root.GetComponentsInChildren<StructureMarker>(includeInactive: true);
        if (markers.Length == 0)
        {
            EditorUtility.DisplayDialog("Export Structure", "No StructureMarker components found under this root.", "OK");
            return;
        }

        float g = Mathf.Max(0.0001f, root.gridSize);
        Vector3 origin = root.transform.position;

        var blocks = new List<StructureBlock>();
        var seen = new HashSet<Vector3Int>();

        foreach (var m in markers)
        {
            Vector3 local = m.transform.position - origin;
            var off = new Vector3Int(
                Mathf.RoundToInt(local.x / g),
                Mathf.RoundToInt(local.y / g),
                Mathf.RoundToInt(local.z / g)
            );

            if (seen.Contains(off)) continue; // avoid duplicates
            seen.Add(off);

            StructureBlock sb = new StructureBlock
            {
                offset = off,
                block = m.block,
                onlyPlaceIntoAir = m.onlyPlaceIntoAir
            };
            blocks.Add(sb);
        }

        // Stable ordering (deterministic)
        blocks = blocks.OrderBy(b => b.offset.y).ThenBy(b => b.offset.x).ThenBy(b => b.offset.z).ToList();

        // Create asset
        string suggested = $"{root.name}_StructureDef.asset";
        string path = EditorUtility.SaveFilePanelInProject("Save StructureDef", suggested, "asset", "Choose save location");
        if (string.IsNullOrEmpty(path)) return;

        var def = ScriptableObject.CreateInstance<StructureDef>();
        def.structureName = root.name;
        def.allowedBiomes = root.allowedBiomes;
        def.anchor = root.anchor;
        def.chancePerChunk = root.chancePerChunk;
        def.maxPerChunk = root.maxPerChunk;
        def.triesPerChunk = root.triesPerChunk;
        def.minSurfaceY = root.minSurfaceY;
        def.maxSurfaceY = root.maxSurfaceY;
        def.blocks = blocks.ToArray();

        AssetDatabase.CreateAsset(def, path);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(def);
        Debug.Log($"Exported StructureDef with {blocks.Count} blocks → {path}");
    }
}
