// Server Meshing test rig — the editor as a client only (Slice 5, slices/slice5_design.md decision 14). The rig's
// split-process topology runs the central, login, database manager and spawner as build processes; the editor's Play then
// hosts no server. The kit's MMOServerInstance starts, in the editor, the servers the scene's four start-on-awake flags
// name, and reads them in its Awake; this component sits on the same object with an earlier execution order and clears
// them when the editor runs with MESH_EDITOR_SERVERS=0 (the play runner sets it from the Play request's environment).
// In a build, or without the variable, it does nothing.
using System;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshEditorRole clears the kit's start-on-awake flags for a client-only editor Play.</summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class ServerMeshEditorRole : MonoBehaviour
    {
        /// <summary>EnvServers is the variable: "0" makes the editor a client only.</summary>
        public const string EnvServers = "MESH_EDITOR_SERVERS";

        /// <summary>ClientOnly: the variable's value asks for no servers in the editor ("0", trimmed).</summary>
        public static bool ClientOnly(string value) => value != null && value.Trim() == "0";

        /// <summary>Line is what is logged once the flags are cleared.</summary>
        public static string Line => $"Editor role: client only ({EnvServers}=0)";

        private void Awake()
        {
            if (!Application.isEditor || !ClientOnly(Environment.GetEnvironmentVariable(EnvServers)))
                return;
            MMOServerInstance instance = GetComponent<MMOServerInstance>();
            if (instance == null)
            {
                Logging.LogWarning("Mesh", $"{EnvServers}=0 but no MMOServerInstance sits on {name}; the editor starts its servers as usual");
                return;
            }
            instance.startCentralOnAwake = false;
            instance.startLoginOnAwake = false;
            instance.startDatabaseOnAwake = false;
            instance.startMapSpawnOnAwake = false;
            Logging.Log("Mesh", Line);
        }
    }
}
