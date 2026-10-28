using System.IO;
using UnityEditor;
using UnityEngine;
using MultiplayerARPG.MMO;

namespace MultiplayerARPG.Demo.EditorTools
{
    [CustomEditor(typeof(MapSpawnNetworkManager), true)]
    [CanEditMultipleObjects]
    public class MapSpawnNetworkManagerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var manager = target as MapSpawnNetworkManager;
            if (manager != null)
            {
                string targetExe = manager.isOverrideExePath ? manager.overrideExePath : manager.spawnExePath;
                if (!string.IsNullOrEmpty(targetExe))
                {
                    string fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), targetExe);
                    bool exists = File.Exists(fullPath);

                    if (!exists)
                    {
                        EditorGUILayout.HelpBox(
                            $"Map Server executable not found at '{targetExe}'.\n" +
                            "The game will be unable to launch map instances when playing in the editor until the executable is built.",
                            MessageType.Warning);

                        if (GUILayout.Button("Build Demo Map Server", GUILayout.Height(28)))
                        {
                            DemoBuildTools.BuildDemoMapServer();
                        }
                        EditorGUILayout.Space(6);
                    }
                    else if (targetExe.Contains("OpenMMORPG.exe"))
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.HelpBox($"Demo Map Server found at '{targetExe}'.", MessageType.None);
                        if (GUILayout.Button("Rebuild", GUILayout.Width(70), GUILayout.Height(28)))
                        {
                            DemoBuildTools.BuildDemoMapServer();
                        }
                        EditorGUILayout.EndHorizontal();
                        EditorGUILayout.Space(4);
                    }
                }
            }

            DrawDefaultInspector();
        }
    }
}
