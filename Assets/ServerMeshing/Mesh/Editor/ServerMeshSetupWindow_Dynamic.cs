// Server Meshing add-on — Setup window, the Validate panel's lines for Dynamic maps (Slice 6, slices/slice6_design.md
// decision 8). The judging is MeshDynamicSetupChecks (pure, tested); this file gathers what it needs from the prefabs
// and turns its findings into lines and Fixes.
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    public partial class ServerMeshSetupWindow
    {
        /// <summary>Every Dynamic map's cut and settings, and the spawners' served cells: a line per finding, one OK line per clean map.</summary>
        private void AddDynamicChecks()
        {
            if (_registry == null || !_registry.HasDynamicMap)
                return;
            List<MeshSetupSpawner> spawners = new List<MeshSetupSpawner>();
            bool linkAllZones = false;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/") || path.StartsWith(KitFolder)) continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                foreach (MeshMapSpawnNetworkManager spawner in prefab.GetComponentsInChildren<MeshMapSpawnNetworkManager>(true))
                    spawners.Add(new MeshSetupSpawner { owner = ShortPath(path), servedCells = spawner.servedCells });
                foreach (MeshMapNetworkManager map in prefab.GetComponentsInChildren<MeshMapNetworkManager>(true))
                    linkAllZones |= map.linkAllZones;
            }
            List<MeshSetupFinding> findings = MeshDynamicSetupChecks.Check(_registry, linkAllZones);
            findings.AddRange(MeshDynamicSetupChecks.ServedCellsFindings(_registry, spawners));
            foreach (MeshSetupFinding finding in findings)
                _checks.Add(ToCheck(finding));
            Dictionary<string, int> counts = MeshDynamicSetupChecks.WorstCaseBandCounts(_registry);
            List<MeshLayoutBaseCell> bases = _registry.ValidationWorld().baseCells;
            foreach (string mapId in MeshDynamicSetupChecks.DynamicMaps(_registry))
            {
                if (findings.Exists(f => f.mapId == mapId))
                    continue;
                string busiest = null;
                int most = 0;
                foreach (MeshLayoutBaseCell cell in bases)
                {
                    if (cell.mapId == mapId && counts.TryGetValue(cell.id, out int count) && (busiest == null || count > most))
                    {
                        busiest = cell.id;
                        most = count;
                    }
                }
                float minCell = MeshDynamicSetupChecks.EffectiveMinCellSize(_registry.GetLayout(mapId), _registry.dynamicSettings, MeshDynamicSetupChecks.AssetBand(_registry.dynamicSettings));
                _checks.Add(new Check
                {
                    ok = true,
                    text = $"Dynamic map {mapId}: every base can split, minimum cell {minCell.ToString("0.##", CultureInfo.InvariantCulture)} m, at most {most} band neighbours ({busiest}).",
                });
            }
        }

        private Check ToCheck(MeshSetupFinding finding)
        {
            Check check = new Check { ok = false, warning = finding.severity == MeshSetupSeverity.Warning, text = finding.text + "." };
            if (finding.fix == MeshSetupFix.RaiseMinCellSizeToBand)
            {
                check.fixLabel = "Raise to band";
                check.fix = () =>
                {
                    Undo.RecordObject(_registry, "Raise minCellSize to the band");
                    if (MeshDynamicSetupChecks.ApplyFix(_registry, finding))
                    {
                        EditorUtility.SetDirty(_registry);
                        AssetDatabase.SaveAssetIfDirty(_registry);
                    }
                };
            }
            return check;
        }
    }
}
