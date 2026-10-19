// Server Meshing add-on — unattended Play-mode runner for the editor test rig.
//
// Lets a script outside the editor start/stop the auto-client run without the MCP bridge:
//   Temp/ServerMesh.PlayRequest.txt   key=value lines (mode, channel, user, walkX, walkZ, mount, pet, enabled, scene,
//                                     seq, env.NAME=value sets an environment variable in the editor process before
//                                     Play — zone servers inherit it; an empty value clears it; every variable a
//                                     request sets is put back as it was when Play ends)
//                                     -> sets the "Server Meshing/Auto Client" EditorPrefs, opens the scene
//                                        (default BaseInit-CE) and enters Play. Deleted once consumed. The status
//                                        echoes seq as playSeq= while that Play runs, so the harness can tell its Play
//                                        from one an earlier, abandoned request started late.
//   Temp/ServerMesh.StopRequest.txt   -> exits Play; one that arrives while Play is starting waits for it to start.
//                                     Deleted once consumed.
//   Temp/ServerMesh.RefreshRequest.txt -> AssetDatabase.Refresh(), so script edits compile while the editor is in the
//                                     background (it only auto-refreshes on focus). Outside Play only.
//   Temp/ServerMesh.BuildRequest.txt  -> builds the rig player (ServerMeshRigBuild); the outcome is written to
//                                     Temp/ServerMesh.BuildStatus.txt as seq= and result=. Outside Play only.
//   Temp/ServerMesh.TestRequest.txt   -> runs the add-on's EditMode tests (ServerMeshTestRunner). Outside Play only.
//   Temp/ServerMesh.PackageRequest.txt -> builds both .unitypackages (ServerMeshPackageBuilder); the outcome is written
//                                     to Temp/ServerMesh.PackageStatus.txt as seq= and result=. Outside Play only.
//   Temp/ServerMesh.PlayStatus.txt    written every second: editor state + auto-client phase/finished/passed.
//   Temp/ServerMesh.LayoutCommand.txt -> during Play: one line `<seq> split <baseCell> <x|z> <at> <+|-> <childId>` or
//                                     `<seq> merge <a> <b>` (Slice 2c), handed to the central's SubmitLayoutCommand
//                                     whatever its verb, and deleted. Every console line starting `Split `, `Merge ` or
//                                     `Layout op ` is then appended to Temp/ServerMesh.LayoutCommandStatus.txt prefixed
//                                     with the latest command's seq (ServerMeshLayoutCommandFile), as are refusals the runner
//                                     makes itself. Play requests may carry observeSeconds, lineX and
//                                     harvestAfterMigration for the auto client (absent: cleared).
// The first line of a refresh, build, test or package request may hold a sequence number, echoed back so the harness knows the
// status it reads belongs to its own request.
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
        private static readonly string RefreshPath = Path.Combine(Root, "Temp", "ServerMesh.RefreshRequest.txt");
        private static readonly string BuildPath = Path.Combine(Root, "Temp", "ServerMesh.BuildRequest.txt");
        private static readonly string BuildStatusPath = Path.Combine(Root, "Temp", "ServerMesh.BuildStatus.txt");
        private static readonly string TestPath = Path.Combine(Root, "Temp", "ServerMesh.TestRequest.txt");
        private static readonly string PackagePath = Path.Combine(Root, "Temp", "ServerMesh.PackageRequest.txt");
        private static readonly string PackageStatusPath = Path.Combine(Root, "Temp", "ServerMesh.PackageStatus.txt");
        private static readonly string LayoutCommandPath = Path.Combine(Root, "Temp", ServerMeshLayoutCommandFile.CommandFileName);
        private static readonly string LayoutStatusPath = Path.Combine(Root, "Temp", ServerMeshLayoutCommandFile.StatusFileName);
        private static readonly object s_layoutLock = new object();
        // The seq of the layout command this Play last handed to the central (null: none yet); console lines are mirrored
        // under it. A static: Play does not reload the domain while it runs, and leaving Play clears it.
        private static volatile string s_layoutSeq;
        private static volatile bool s_layoutRefusalMirrored;
        private static string _lastRefreshSeq = "";
        private static double _nextTick;
        private static string _lastError = "";
        // SessionState, not statics: entering Play reloads the domain, which would wipe both before they are needed.
        private const string PlaySeqKey = "ServerMesh.PlaySeq";
        private const string EnvRestoreKey = "ServerMesh.EnvRestore";
        // When the last request asked for Play; cleared once Play mode starts changing. A request whose Play never
        // starts (Unity can refuse it) would otherwise leave its environment set for the next manual Play.
        private const string PlayPendingKey = "ServerMesh.PlayPendingSince";
        private const float PlayStartGraceSeconds = 10f;
        // Makes every process quit after its self-test (ServerMeshProbeSelfTest): never meant for a rig Play.
        private const string ProbeSelfTestEnv = "MESH_PROBE_SELFTEST";

        static ServerMeshPlayRunner()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            Application.logMessageReceivedThreaded += MirrorLayoutLine;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            SessionState.EraseFloat(PlayPendingKey);
            if (change == PlayModeStateChange.EnteredPlayMode)
                s_playing = true;
            if (change != PlayModeStateChange.EnteredEditMode)
                return;
            s_playing = false;
            SessionState.EraseString(PlaySeqKey);
            s_layoutSeq = null;
            RestoreEnvironment();
        }

        // Whether Play runs (set on EnteredPlayMode, cleared on EnteredEditMode): lines of automatic operations are
        // mirrored under `auto` only then. A static, read from the log thread.
        private static volatile bool s_playing;

        /// <summary>A request whose Play did not start within the grace period gets its environment put back.</summary>
        private static void AbandonUnstartedPlay()
        {
            float since = SessionState.GetFloat(PlayPendingKey, -1f);
            if (since < 0f || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.timeSinceStartup - since < PlayStartGraceSeconds)
                return;
            SessionState.EraseFloat(PlayPendingKey);
            SessionState.EraseString(PlaySeqKey);
            RestoreEnvironment();
            _lastError = "Play did not start; the request's environment was put back";
            Debug.LogWarning("[MeshRunner] " + _lastError);
        }

        /// <summary>Sets an environment variable for Play, remembering its value from before the first request that set it.</summary>
        private static void SetForPlay(string name, string value)
        {
            string saved = SessionState.GetString(EnvRestoreKey, "");
            if (!EnvRestoreEntries(saved).ContainsKey(name))
            {
                string before = Environment.GetEnvironmentVariable(name);
                saved += name + "\t" + (before == null ? "0" : "1" + before) + "\n";
                SessionState.SetString(EnvRestoreKey, saved);
            }
            Environment.SetEnvironmentVariable(name, value);
        }

        /// <summary>Puts back every variable a request set, so a manual Play afterwards does not inherit a rig run's load or toggles.</summary>
        private static void RestoreEnvironment()
        {
            foreach (KeyValuePair<string, string> kv in EnvRestoreEntries(SessionState.GetString(EnvRestoreKey, "")))
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            SessionState.EraseString(EnvRestoreKey);
        }

        /// <summary>Name -> value before (null: it was not set), from lines "NAME\t0" or "NAME\t1value".</summary>
        private static Dictionary<string, string> EnvRestoreEntries(string saved)
        {
            Dictionary<string, string> entries = new Dictionary<string, string>();
            foreach (string line in saved.Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0 || tab + 1 >= line.Length)
                    continue;
                entries[line.Substring(0, tab)] = line[tab + 1] == '1' ? line.Substring(tab + 2) : null;
            }
            return entries;
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
                AbandonUnstartedPlay();
                if (File.Exists(StopPath))
                {
                    // A Play that is still starting cannot be exited yet; keep the request until it has started, or an
                    // abandoned Play would run on after the harness moved on.
                    if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying)
                        return;
                    File.Delete(StopPath);
                    if (EditorApplication.isPlaying)
                        EditorApplication.ExitPlaymode();
                    return;
                }
                if (EditorApplication.isPlaying && File.Exists(LayoutCommandPath))
                    HandleLayoutCommand();
                if (EditorApplication.isPlayingOrWillChangePlaymode || ServerMeshTestRunner.IsRunning)
                    return; // a refresh, build or Play in the middle of an asynchronous test run would pull it down
                if (File.Exists(RefreshPath))
                {
                    _lastRefreshSeq = ConsumeSeq(RefreshPath);
                    AssetDatabase.Refresh();
                    return;
                }
                if (File.Exists(BuildPath))
                {
                    HandleBuild();
                    return;
                }
                if (File.Exists(TestPath))
                {
                    ServerMeshTestRunner.Run(ConsumeSeq(TestPath));
                    return;
                }
                if (File.Exists(PackagePath))
                {
                    HandlePackage();
                    return;
                }
                if (File.Exists(RequestPath))
                    HandleRequest();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Debug.LogError($"[MeshRunner] {ex}");
            }
        }

        private static string ConsumeSeq(string path)
        {
            string[] lines = File.ReadAllLines(path);
            File.Delete(path);
            return lines.Length > 0 ? lines[0].Trim() : "";
        }

        private static void HandleBuild()
        {
            string seq = ConsumeSeq(BuildPath);
            File.WriteAllText(BuildStatusPath, $"seq={seq}\nresult=running\n");
            string result;
            try
            {
                result = ServerMeshRigBuild.Build();
            }
            catch (Exception ex)
            {
                result = "error: " + ex.Message;
                Debug.LogError($"[MeshRunner] Rig build failed: {ex}");
            }
            File.WriteAllText(BuildStatusPath, $"seq={seq}\nresult={result.Replace('\n', ' ')}\n");
            Debug.Log($"[MeshRunner] Rig build {result}");
        }

        private static void HandlePackage()
        {
            string seq = ConsumeSeq(PackagePath);
            File.WriteAllText(PackageStatusPath, $"seq={seq}\nresult=running\n");
            string result;
            try
            {
                result = "ok " + string.Join(" ", ServerMeshPackageBuilder.Build());
            }
            catch (Exception ex)
            {
                result = "error: " + ex.Message;
                Debug.LogError($"[MeshRunner] Package build failed: {ex}");
            }
            File.WriteAllText(PackageStatusPath, $"seq={seq}\nresult={result.Replace('\n', ' ')}\n");
            Debug.Log($"[MeshRunner] Package build {result}");
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
            if (EditorUtility.scriptCompilationFailed)
            {
                // Unity refuses to enter Play with compile errors, so nothing of the request is applied.
                _lastError = "script compile errors; not entering Play";
                Debug.LogWarning("[MeshRunner] " + _lastError);
                return;
            }

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
            ForwardOrClear(req, "observeSeconds", ServerMeshAutoClient.PREF_OBSERVE_SECONDS);
            ForwardOrClear(req, "lineX", ServerMeshAutoClient.PREF_LINE_X);
            ForwardOrClear(req, "harvestAfterMigration", ServerMeshAutoClient.PREF_HARVEST_AFTER_MIGRATION);
            foreach (KeyValuePair<string, string> kv in req)
            {
                if (!kv.Key.StartsWith("env.", StringComparison.OrdinalIgnoreCase) || kv.Key.Length <= 4)
                    continue;
                SetForPlay(kv.Key.Substring(4), kv.Value.Length > 0 ? kv.Value : null);
            }
            if (Environment.GetEnvironmentVariable(ProbeSelfTestEnv) != null)
                SetForPlay(ProbeSelfTestEnv, null);
            SessionState.SetString(PlaySeqKey, req.TryGetValue("seq", out string seq) ? seq : "");
            SessionState.SetFloat(PlayPendingKey, (float)EditorApplication.timeSinceStartup);

            Debug.Log($"[MeshRunner] Entering Play in {scene} (auto client: {EditorPrefs.GetString(ServerMeshAutoClient.PREF_MODE, "walk")}, seq {seq})");
            EditorApplication.EnterPlaymode();
        }

        /// <summary>A Slice 2b bot setting: the request's value, or none (the auto client's default) when the request has none.</summary>
        private static void ForwardOrClear(Dictionary<string, string> req, string key, string pref)
        {
            if (req.TryGetValue(key, out string value) && value.Length > 0)
                EditorPrefs.SetString(pref, value);
            else
                EditorPrefs.DeleteKey(pref);
        }

        /// <summary>
        /// Hands the layout command file's line to the central (the leg runs while this Play runs, so a command belongs to
        /// the playSeq the status shows) and deletes the file; a blank file is left for the next tick (still being written).
        /// A refusal the coordinator did not log itself is written to the status file here.
        /// </summary>
        private static void HandleLayoutCommand()
        {
            string text;
            try
            {
                text = File.ReadAllText(LayoutCommandPath);
            }
            catch (IOException)
            {
                return; // the harness is still writing it
            }
            if (ServerMeshLayoutCommandFile.IsBlank(text))
                return;
            File.Delete(LayoutCommandPath);
            if (!ServerMeshLayoutCommandFile.TryParse(text, out string seq, out string line, out string error))
            {
                AppendLayoutStatus(ServerMeshLayoutCommandFile.RefusalLine(seq, error));
                return;
            }
            s_layoutSeq = seq;
            s_layoutRefusalMirrored = false;
            AppendLayoutStatus(ServerMeshLayoutCommandFile.SubmittedLine(seq, line));
            MeshCentralNetworkManager central = UnityEngine.Object.FindFirstObjectByType<MeshCentralNetworkManager>();
            if (central == null)
            {
                AppendLayoutStatus(ServerMeshLayoutCommandFile.RefusalLine(seq, "no central server in this Play"));
                return;
            }
            string refusal;
            try
            {
                central.SubmitLayoutCommand(line, out refusal);
            }
            catch (Exception ex)
            {
                refusal = "submit failed: " + ex.Message;
            }
            if (!string.IsNullOrEmpty(refusal) && !s_layoutRefusalMirrored)
                AppendLayoutStatus(ServerMeshLayoutCommandFile.RefusalLine(seq, refusal));
            Debug.Log("[MeshRunner] Layout command " + seq + " handed to the central" + (string.IsNullOrEmpty(refusal) ? "" : "; refused"));
        }

        /// <summary>
        /// Mirrors a console line starting `Split `, `Merge `, `Layout op `, `Supervisor:` or `Policy: ` under the current
        /// command's seq, or under `auto` while this Play has no live command (Slice 3: automatic operations); any thread.
        /// </summary>
        private static void MirrorLayoutLine(string condition, string stackTrace, LogType type)
        {
            string seq = s_layoutSeq;
            if (seq == null)
            {
                if (!s_playing)
                    return;
                seq = ServerMeshLayoutCommandFile.AutoSeq;
            }
            string payload = ServerMeshLayoutCommandFile.MirroredPayload(condition);
            if (payload == null)
                return;
            if (ServerMeshLayoutCommandFile.IsRefusal(payload))
                s_layoutRefusalMirrored = true;
            AppendLayoutStatus(ServerMeshLayoutCommandFile.StatusLine(seq, payload));
        }

        private static void AppendLayoutStatus(string line)
        {
            lock (s_layoutLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LayoutStatusPath));
                    File.AppendAllText(LayoutStatusPath, line + "\n");
                }
                catch (IOException ex)
                {
                    _lastError = "layout status not written: " + ex.Message; // never logged: this runs inside the log callback
                }
                catch (UnauthorizedAccessException ex)
                {
                    _lastError = "layout status not written: " + ex.Message;
                }
            }
        }

        private static void WriteStatus()
        {
            ServerMeshAutoClient c = EditorApplication.isPlaying ? ServerMeshAutoClient.Instance : null;
            string text =
                $"time={DateTime.Now:HH:mm:ss}\n" +
                $"isPlaying={EditorApplication.isPlaying}\n" +
                $"playSeq={(EditorApplication.isPlaying ? SessionState.GetString(PlaySeqKey, "") : "")}\n" +
                $"isCompiling={EditorApplication.isCompiling}\n" +
                $"isUpdating={EditorApplication.isUpdating}\n" +
                $"compileFailed={EditorUtility.scriptCompilationFailed}\n" +
                $"refreshSeq={_lastRefreshSeq}\n" +
                $"testsRunning={ServerMeshTestRunner.IsRunning}\n" +
                $"scene={SceneManager.GetActiveScene().path}\n" +
                $"autoClient={(c != null ? "yes" : "no")}\n" +
                $"phase={(c != null ? c.Phase : "")}\n" +
                $"finished={(c != null && c.Finished)}\n" +
                $"passed={(c != null && c.Passed)}\n" +
                $"result={(c != null ? c.LastResult : "")}\n" +
                $"layoutSeq={(EditorApplication.isPlaying ? s_layoutSeq ?? "" : "")}\n" +
                $"error={_lastError}\n";
            Directory.CreateDirectory(Path.GetDirectoryName(StatusPath));
            File.WriteAllText(StatusPath, text);
        }
    }
}
