// Server Meshing test rig — the SubChannel seam of the isolated-spaces rig (Phase 0 step 9 decision 10, MESH_SPACES).
// The load generator and the phantom binder pass every character they spawn through Tag just before NetworkSpawn; with
// MESH_SPACES set it gets its space's SubChannelId (space:k), so the kit's AOI hides it from every other space
// (LiteNetLibIdentity.IsHideFrom). The kit never resets SubChannelId, and a pooled instance comes back with the last
// one, so the seam clears it when the kit destroys a tagged object (Assets.onObjectDestroy runs before the instance goes
// back to the pool). Unset: Tag returns its argument untouched and nothing is registered.
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;
using UnityEngine.Events;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshSpaceTags holds the process's space layout (built once the cell is known) and tags spawns with their space.
    /// Observers (phantom characters with a connection) are kept in a list the spaces window reads; load entities (synthetic
    /// players, phantom filler) and content are tagged only. A layout that cannot be built is logged once and nothing is
    /// tagged.
    /// </summary>
    public static class ServerMeshSpaceTags
    {
        private static bool s_read;
        private static bool s_on;
        private static int s_spaces;
        private static ServerMeshSpaceArrangement s_layout;
        private static MeshMapNetworkManager s_mesh;
        private static LiteNetLibAssets s_assets;
        private static bool s_ready;
        private static bool s_failed;
        private static Vector2[] s_centres;
        private static string[] s_ids;
        private static int s_observerOrdinal;
        private static int s_loadOrdinal;
        private static readonly List<LiteNetLibIdentity> s_observers = new List<LiteNetLibIdentity>();
        private static UnityAction<LiteNetLibIdentity> s_onDestroy;

        /// <summary>Enabled is true when MESH_SPACES is set and parses in this process (read on first use; a bad value is logged once).</summary>
        public static bool Enabled
        {
            get
            {
                if (!s_read)
                    ReadSetting();
                return s_on;
            }
        }

        /// <summary>Spaces is S, the number of spaces (0 when unset).</summary>
        public static int Spaces => Enabled ? s_spaces : 0;

        /// <summary>Layout is the spaces' arrangement (stack when unset).</summary>
        public static ServerMeshSpaceArrangement Layout => s_layout;

        /// <summary>Centres are the spaces' centres in the scene frame once the layout is built, else null.</summary>
        public static Vector2[] Centres => s_ready ? s_centres : null;

        /// <summary>Observers are the tagged observers spawned since the layout was built; destroyed ones leave the list.</summary>
        internal static List<LiteNetLibIdentity> Observers => s_observers;

        /// <summary>
        /// Tag gives <paramref name="identity"/> its space's SubChannelId before it is network-spawned and returns it, so a
        /// spawn call can wrap it. An observer joins the observers list. Unset, or without a layout, it does nothing.
        /// </summary>
        public static LiteNetLibIdentity Tag(LiteNetLibIdentity identity, bool observer)
        {
            if (identity == null || !Enabled)
                return identity;
            if (!TryEnsure(BaseGameNetworkManager.Singleton as MeshMapNetworkManager))
                return identity;
            Vector3 p = identity.transform.position;
            int ordinal = observer ? s_observerOrdinal++ : s_loadOrdinal++;
            int k = ServerMeshSpaceLayout.SpaceOf(s_layout, s_centres, ordinal, new Vector2(p.x, p.z));
            identity.SubChannelId = s_ids[k];
            if (observer)
                s_observers.Add(identity);
            return identity;
        }

        /// <summary>TagSpace gives <paramref name="identity"/> space k's SubChannelId (content); false when there is no layout or k is out of range.</summary>
        internal static bool TagSpace(LiteNetLibIdentity identity, int k)
        {
            if (identity == null || !s_ready || k < 0 || k >= s_ids.Length)
                return false;
            identity.SubChannelId = s_ids[k];
            return true;
        }

        /// <summary>
        /// TryEnsure builds the layout once for <paramref name="mesh"/> (its map's ground, the union of the map's asset cells)
        /// and hooks the destroy event; a new server builds it again. False while the cell is unknown or when the layout
        /// cannot be built (logged once per server).
        /// </summary>
        internal static bool TryEnsure(MeshMapNetworkManager mesh)
        {
            if (!Enabled || mesh == null || !mesh.IsServer || mesh.OwnCell == null || mesh.world == null)
                return false;
            if (!ReferenceEquals(mesh, s_mesh) || !ReferenceEquals(mesh.Assets, s_assets))
                Rebuild(mesh);
            return s_ready;
        }

        private static void Rebuild(MeshMapNetworkManager mesh)
        {
            Unhook();
            s_mesh = mesh;
            s_assets = mesh.Assets;
            s_ready = s_failed = false;
            s_observers.Clear();
            s_observerOrdinal = s_loadOrdinal = 0;
            if (!ServerMeshSpaceLayout.TryCentres(s_spaces, s_layout, Ground(mesh), out s_centres, out string error))
            {
                s_failed = true;
                Logging.LogError("Mesh", "Spaces error: " + ServerMeshSpaceLayout.Printable(error) + "; nothing tagged");
                return;
            }
            s_ids = new string[s_spaces];
            for (int k = 0; k < s_spaces; ++k)
                s_ids[k] = ServerMeshSpaceLayout.SpaceId(k);
            if (s_assets != null)
            {
                s_onDestroy = OnObjectDestroy;
                s_assets.onObjectDestroy.AddListener(s_onDestroy);
            }
            s_ready = true;
        }

        /// <summary>Failed is true when this server's layout could not be built (the error was logged).</summary>
        internal static bool Failed => s_failed;

        /// <summary>Ground is the union of the map's asset cells in the cell's scene frame (every cut draws over it).</summary>
        internal static Rect Ground(MeshMapNetworkManager mesh)
        {
            MeshCell own = mesh.OwnCell;
            Rect ground = SceneRect(own.baseRect, own.frameOrigin);
            IReadOnlyList<MeshCell> cells = mesh.world.CellsOf(mesh.OwnMapId);
            for (int i = 0; i < cells.Count; ++i)
            {
                Rect r = SceneRect(cells[i].baseRect, cells[i].frameOrigin);
                ground = Rect.MinMaxRect(Mathf.Min(ground.xMin, r.xMin), Mathf.Min(ground.yMin, r.yMin), Mathf.Max(ground.xMax, r.xMax), Mathf.Max(ground.yMax, r.yMax));
            }
            return ground;
        }

        private static Rect SceneRect(MeshRect rect, Vector3 frameOrigin)
        {
            return Rect.MinMaxRect(rect.minX - frameOrigin.x, rect.minZ - frameOrigin.z, rect.maxX - frameOrigin.x, rect.maxZ - frameOrigin.z);
        }

        /// <summary>The kit is destroying an object: a tagged one loses its tag before it can go back to the pool.</summary>
        private static void OnObjectDestroy(LiteNetLibIdentity identity)
        {
            if (identity == null || !ServerMeshSpaceLayout.IsSpaceId(identity.SubChannelId))
                return;
            identity.SubChannelId = string.Empty;
            s_observers.Remove(identity);
        }

        private static void Unhook()
        {
            if (s_assets != null && s_onDestroy != null)
                s_assets.onObjectDestroy.RemoveListener(s_onDestroy);
            s_onDestroy = null;
        }

        private static void ReadSetting()
        {
            s_read = true;
            string value = System.Environment.GetEnvironmentVariable(ServerMeshSpaceLayout.EnvironmentKey);
            if (string.IsNullOrEmpty(value))
                return;
            if (!ServerMeshSpaceLayout.TryParseSetting(value, out s_spaces, out s_layout, out string error))
            {
                Logging.LogError("Mesh", "Spaces error: " + error + "; nothing tagged");
                return;
            }
            s_on = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay()
        {
            s_read = s_on = s_ready = s_failed = false;
            s_spaces = 0;
            s_layout = ServerMeshSpaceArrangement.Stack;
            s_mesh = null;
            s_assets = null;
            s_onDestroy = null;
            s_centres = null;
            s_ids = null;
            s_observerOrdinal = s_loadOrdinal = 0;
            s_observers.Clear();
        }
    }
}
