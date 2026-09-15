// Server Meshing add-on — unattended Play-mode runner for the editor test rig.
//
// Lets a script outside the editor start/stop the auto-client run without the MCP bridge:
//   Temp/ServerMesh.PlayRequest.txt   key=value lines (mode, channel, user, walkX, walkZ, mount, pet, scene,
//                                     env.NAME=value sets an environment variable in the editor process before Play —
//                                     zone servers inherit it; an empty value clears it)
//                                     -> sets the "Server Meshing/Auto Client" EditorPrefs, opens the scene
//                                        (default BaseInit-CE) and enters Play. Deleted once consumed.
//   Temp/ServerMesh.StopRequest.txt   -> exits Play. Deleted once consumed.
//   Temp/ServerMesh.PlayStatus.txt    written every second: editor state + auto-client phase/finished/passed.
// Never enters Play while the editor is compiling or importing (a domain reload mid-Play wipes statics).

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    [InitializeOnLoad]
    public static class ServerMeshPlayRunner
    {
        private const string DefaultScene = "Assets/BaseDemo/Scenes/BaseInit-CE.unity";
        private static readonly string Root = Path.GetDirectoryName(Application.dataPath) ?? "";
        private static readonly string RequestPath = Path.Combine(Root, "Temp", "ServerMesh.PlayRequest.txt");
        private static readonly string StopPath = Path.Combine(Root, "Temp", "ServerMesh.StopRequest.txt");
        private static readonly string StatusPath = Path.Combine(Root, "Temp", "ServerMesh.PlayStatus.txt");
        private static double _nextTick;
        private static string _lastError = "";

        static ServerMeshPlayRunner()
        {
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _nextTick)
                return;
            _nextTick = EditorApplication.timeSinceStartup + 1.0;
            try
            {
                WriteStatus();
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    return;
                if (File.Exists(StopPath))
                {
                    File.Delete(StopPath);
                    if (EditorApplication.isPlaying)
                        EditorApplication.ExitPlaymode();
                    return;
                }
                if (File.Exists(RequestPath) && !EditorApplication.isPlayingOrWillChangePlaymode)
                    HandleRequest();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Debug.LogWarning($"[MeshRunner] {ex}");
            }
        }

        private static void HandleRequest()
        {
            Dictionary<string, string> req = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(RequestPath))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line.StartsWith("#") || eq <= 0)
                    continue;
                req[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            File.Delete(RequestPath);
            _lastError = "";

            string scene = req.TryGetValue("scene", out string s) && s.Length > 0 ? s : DefaultScene;
            Scene active = SceneManager.GetActiveScene();
            if (active.path != scene)
            {
                if (active.isDirty)
                {
                    _lastError = $"active scene {active.path} has unsaved changes; not switching to {scene}";
                    Debug.LogWarning("[MeshRunner] " + _lastError);
                    return;
                }
                EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            }

            EditorPrefs.SetBool(ServerMeshAutoClient.PREF_ENABLE, !req.TryGetValue("enabled", out string en) || en != "0");
            if (req.TryGetValue("mode", out string v)) EditorPrefs.SetString(ServerMeshAutoClient.PREF_MODE, v);
            if (req.TryGetValue("channel", out v)) EditorPrefs.SetString(ServerMeshAutoClient.PREF_CHANNEL, v);
            if (req.TryGetValue("user", out v)) EditorPrefs.SetString(ServerMeshAutoClient.PREF_USER, v);
            if (req.TryGetValue("walkX", out v)) EditorPrefs.SetString(ServerMeshAutoClient.PREF_WALK_X, v);
            if (req.TryGetValue("walkZ", out v)) EditorPrefs.SetString(ServerMeshAutoClient.PREF_WALK_Z, v);
            if (req.TryGetValue("mount", out v)) EditorPrefs.SetBool(ServerMeshAutoClient.PREF_MOUNT, v == "1");
            if (req.TryGetValue("pet", out v)) EditorPrefs.SetBool(ServerMeshAutoClient.PREF_PET, v == "1");
            foreach (KeyValuePair<string, string> kv in req)
            {
                if (!kv.Key.StartsWith("env.", StringComparison.OrdinalIgnoreCase) || kv.Key.Length <= 4)
                    continue;
                Environment.SetEnvironmentVariable(kv.Key.Substring(4), kv.Value.Length > 0 ? kv.Value : null);
            }

            Debug.Log($"[MeshRunner] Entering Play in {scene} (auto client: {EditorPrefs.GetString(ServerMeshAutoClient.PREF_MODE, "walk")})");
            EditorApplication.EnterPlaymode();
        }

        private static void WriteStatus()
        {
            ServerMeshAutoClient c = EditorApplication.isPlaying ? ServerMeshAutoClient.Instance : null;
            string text =
                $"time={DateTime.Now:HH:mm:ss}\n" +
                $"isPlaying={EditorApplication.isPlaying}\n" +
                $"isCompiling={EditorApplication.isCompiling}\n" +
                $"isUpdating={EditorApplication.isUpdating}\n" +
                $"scene={SceneManager.GetActiveScene().path}\n" +
                $"autoClient={(c != null ? "yes" : "no")}\n" +
                $"phase={(c != null ? c.Phase : "")}\n" +
                $"finished={(c != null && c.Finished)}\n" +
                $"passed={(c != null && c.Passed)}\n" +
                $"result={(c != null ? c.LastResult : "")}\n" +
                $"error={_lastError}\n";
            Directory.CreateDirectory(Path.GetDirectoryName(StatusPath));
            File.WriteAllText(StatusPath, text);
        }
    }
}
