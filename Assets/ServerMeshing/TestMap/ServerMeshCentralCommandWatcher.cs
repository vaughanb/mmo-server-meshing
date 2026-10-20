// Server Meshing test rig — the layout command files served by a build-hosted central (Slice 5, slices/slice5_design.md
// decision 15). The editor's play runner hands Temp/ServerMesh.LayoutCommand.txt to the central in its own Play; in the
// split-process topology the central is a build process, so this watcher (created when MESH_RIG_COMMAND_DIR names the
// directory) polls <dir>/ServerMesh.LayoutCommand.txt once a second, hands the line to MeshCentralNetworkManager
// .SubmitLayoutCommand, and mirrors every layout operation line (LayoutOpLine) into <dir>/ServerMesh.LayoutCommandStatus.txt
// under the latest command's seq, or `auto` before any (the runner's format, so the graders read it unchanged). The
// central starts after the game data loaded, so the watcher polls for it. An append the editor is making at the same
// moment fails with an IOException: the line is kept and written next tick.
using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshCentralCommandWatcher serves the rig's layout command and status files from a build-hosted central.</summary>
    public sealed class ServerMeshCentralCommandWatcher : MonoBehaviour
    {
        /// <summary>EnvDir names the directory of the two files (the harness passes the project's Temp, absolute).</summary>
        public const string EnvDir = "MESH_RIG_COMMAND_DIR";

        private string _dir;
        private string _commandPath;
        private string _statusPath;
        private MeshCentralNetworkManager _central;
        private float _nextTick;
        private string _seq;
        private bool _refusalMirrored;
        private readonly Queue<string> _pending = new Queue<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            string dir = Environment.GetEnvironmentVariable(EnvDir);
            if (string.IsNullOrWhiteSpace(dir) || Application.isEditor)
                return;
            GameObject go = new GameObject("[ServerMesh] CentralCommandWatcher");
            DontDestroyOnLoad(go);
            ServerMeshCentralCommandWatcher watcher = go.AddComponent<ServerMeshCentralCommandWatcher>();
            watcher._dir = dir.Trim();
            watcher._commandPath = Path.Combine(watcher._dir, ServerMeshLayoutCommandFile.CommandFileName);
            watcher._statusPath = Path.Combine(watcher._dir, ServerMeshLayoutCommandFile.StatusFileName);
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextTick)
                return;
            _nextTick = Time.unscaledTime + 1f;
            Flush();
            if (_central == null)
            {
                Attach();
                return;
            }
            if (!_central.IsServer)
                return;
            Poll();
        }

        /// <summary>Attach finds the central once it runs a server with a coordinator (nothing has started when the scene loads).</summary>
        private void Attach()
        {
            MeshCentralNetworkManager central = FindFirstObjectByType<MeshCentralNetworkManager>();
            if (central == null || !central.IsServer || central.Coordinator == null)
                return;
            _central = central;
            _central.LayoutOpLine += Mirror;
            Logging.Log("Mesh", $"Rig command watcher on {_dir}");
        }

        private void OnDestroy()
        {
            if (_central != null)
                _central.LayoutOpLine -= Mirror;
        }

        private void Poll()
        {
            string text;
            try
            {
                if (!File.Exists(_commandPath))
                    return;
                text = File.ReadAllText(_commandPath);
            }
            catch (IOException)
            {
                return;   // the harness is still writing it
            }
            if (ServerMeshLayoutCommandFile.IsBlank(text))
                return;
            try
            {
                File.Delete(_commandPath);
            }
            catch (IOException ex)
            {
                Logging.LogWarning("Mesh", $"Rig command watcher: the command file could not be deleted ({ex.Message}); the command is not run");
                return;
            }
            if (!ServerMeshLayoutCommandFile.TryParse(text, out string seq, out string line, out string error))
            {
                Append(ServerMeshLayoutCommandFile.RefusalLine(seq, error));
                return;
            }
            _seq = seq;
            _refusalMirrored = false;
            Append(ServerMeshLayoutCommandFile.SubmittedLine(seq, line));
            string refusal;
            try
            {
                _central.SubmitLayoutCommand(line, out refusal);
            }
            catch (Exception ex)
            {
                refusal = "submit failed: " + ex.Message;
            }
            if (!string.IsNullOrEmpty(refusal) && !_refusalMirrored)
                Append(ServerMeshLayoutCommandFile.RefusalLine(seq, refusal));
            Logging.Log("Mesh", "Rig command watcher: layout command " + seq + " handed to the central" + (string.IsNullOrEmpty(refusal) ? "" : "; refused"));
        }

        /// <summary>Mirror writes a layout operation line under the latest command's seq (or `auto`): the event's text is already the payload.</summary>
        private void Mirror(string line)
        {
            string payload = ServerMeshLayoutCommandFile.MirroredPayload(line) ?? ServerMeshLayoutCommandFile.OneLine(line);
            if (ServerMeshLayoutCommandFile.IsRefusal(payload))
                _refusalMirrored = true;
            Append(ServerMeshLayoutCommandFile.StatusLine(_seq ?? ServerMeshLayoutCommandFile.AutoSeq, payload));
        }

        private void Append(string statusLine)
        {
            _pending.Enqueue(statusLine);
            Flush();
        }

        /// <summary>Flush appends every pending line; a file another process holds open is tried again next tick.</summary>
        private void Flush()
        {
            while (_pending.Count > 0)
            {
                string line = _pending.Peek();
                try
                {
                    Directory.CreateDirectory(_dir);
                    File.AppendAllText(_statusPath, line + "\n");
                }
                catch (IOException)
                {
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
                _pending.Dequeue();
            }
        }
    }
}
