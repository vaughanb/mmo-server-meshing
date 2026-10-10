// Server Meshing test rig — isolated spaces in one process (Phase 0 step 9 decision 10, MESH_SPACES=S[:stack|grid]):
// S spaces share one process and one physics scene, each with its own SubChannelId (space:k). The load generator's and
// the phantom binder's characters are tagged at spawn by ServerMeshSpaceTags (observers: phantom characters with a
// connection, whose per-connection AOI runs the kit's IsHideFrom; load: synthetic players and phantom filler); this
// component places each space's content (5 stationary, not always visible stand-ins for a station, built from the
// map's harvestable prefab with the placer's runtime recipe) and logs the once line and a Spaces window line with every
// Work window: per observer, the entities of its own space it subscribes to and those of other spaces (must be 0).
// Pure parts in ServerMeshSpaceLayout.cs. Default off: unset, no component and no line.
using System;
using System.Collections.Generic;
using System.Text;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshSpaces waits until the map server can instantiate objects and knows its cell, builds the space layout,
    /// logs the setting line, places the content of every space whose centre this cell owns, and then counts what each
    /// tagged observer sees once per Work window. Content is placed once and never kept up: nothing attacks it (the spaces
    /// runs set MESH_NO_MONSTERS=1 and no fighting).
    /// </summary>
    public sealed class ServerMeshSpaces : MonoBehaviour
    {
        /// <summary>SpacesObjectName names the component's GameObject.</summary>
        public const string SpacesObjectName = "[Mesh] Spaces";
        /// <summary>ContentLevel is the level the content stand-ins spawn at.</summary>
        public const int ContentLevel = 1;

        private MeshMapNetworkManager _mesh;
        private bool _started;
        private bool _idle;
        private float _nextTry;
        private int _lastWorkWindow = -1;
        private double _windowStart;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ServerMeshSpaceLayout.EnvironmentKey)))
                return;
            if (!ConfigReader.IsArgsProvided(Environment.GetCommandLineArgs(), ProcessArguments.ARG_START_MAP_SERVER))
                return;
            if (!ServerMeshSpaceTags.Enabled)
                return; // the parse error is logged by the seam
            if (FindFirstObjectByType<ServerMeshSpaces>() != null)
                return;
            GameObject go = new GameObject(SpacesObjectName);
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshSpaces>();
        }

        private void Update()
        {
            if (!_started)
            {
                if (Time.unscaledTime < _nextTry)
                    return;
                _nextTry = Time.unscaledTime + 1f;
                TryStart();
                return;
            }
            if (_idle || _mesh == null || !_mesh.IsServer)
                return;
            WindowIfDue();
        }

        private void TryStart()
        {
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || mesh.OwnCell == null || !mesh.IsServerReadyToInstantiateObjects())
                return;
            _started = true;
            _mesh = mesh;
            if (!ServerMeshSpaceTags.TryEnsure(mesh))
            {
                _idle = true; // the layout error was logged by the seam
                return;
            }
            int spaces = ServerMeshSpaceTags.Spaces;
            Logging.Log("Mesh", ServerMeshSpaceLayout.SettingLine(spaces, ServerMeshSpaceTags.Layout,
                ServerMeshSpaceLayout.PerSpace(ObserversOnThisCell(mesh.OwnZoneId), spaces), ServerMeshSpaceLayout.ContentPerSpace));
            PlaceContent(mesh);
            StartWindow();
        }

        /// <summary>The phantom characters MESH_PHANTOM binds on this cell (0 in the editor, which binds none, or for other kinds).</summary>
        private static int ObserversOnThisCell(string cellId)
        {
            if (Application.isEditor || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ServerMeshPhantomSettings.ENV_PHANTOM)))
                return 0;
            if (!ServerMeshPhantomSettings.TryResolve(Environment.GetEnvironmentVariable, out ServerMeshPhantomSettings settings, out _) || settings == null)
                return 0;
            return settings.Kind == ServerMeshPhantomKind.Character && settings.ServesCell(cellId) ? settings.Phantoms : 0;
        }

        private void PlaceContent(MeshMapNetworkManager mesh)
        {
            HarvestableEntity prefab = FindContentPrefab(out int areaCount);
            if (prefab == null)
            {
                Logging.LogError("Mesh", $"Spaces error: cell {ServerMeshSpaceLayout.Printable(mesh.OwnZoneId)}: no harvestable prefab in the map's {areaCount} harvestable spawn area(s), inactive included; no content placed");
                return;
            }
            GameInstance.AddHarvestableEntities(prefab);
            mesh.Assets.RegisterPrefab(prefab.Identity);
            MeshCell cell = mesh.OwnCell;
            Vector2[] centres = ServerMeshSpaceTags.Centres;
            for (int k = 0; k < centres.Length; ++k)
            {
                Vector3 world = cell.SceneToWorld(new Vector3(centres[k].x, 0f, centres[k].y));
                if (!cell.ContainsWorld(world.x, world.z))
                    continue; // another cell owns this space's centre and places its content
                for (int j = 0; j < ServerMeshSpaceLayout.ContentPerSpace; ++j)
                {
                    if (!Place(mesh, prefab, k, ServerMeshSpaceLayout.ContentPoint(centres[k], j)))
                        return;
                }
            }
        }

        private bool Place(MeshMapNetworkManager mesh, HarvestableEntity prefab, int space, Vector2 point)
        {
            Vector3 position = new Vector3(point.x, 0f, point.y);
            LiteNetLibIdentity identity = mesh.Assets.GetObjectInstance(prefab.Identity.HashAssetId, position, Quaternion.identity);
            HarvestableEntity entity = identity == null ? null : identity.GetComponent<HarvestableEntity>();
            if (entity == null)
            {
                if (identity != null)
                    Destroy(identity.gameObject);
                Logging.LogError("Mesh", $"Spaces error: cell {ServerMeshSpaceLayout.Printable(mesh.OwnZoneId)}: no harvestable instance for prefab hash {prefab.Identity.HashAssetId}; no more content placed");
                return false;
            }
            identity.AlwaysVisible = false; // always-visible objects bypass IsHideFrom, which is what the spaces measure
            ServerMeshSpaceTags.TagSpace(identity, space);
            entity.SetSpawnArea(null, prefab, ContentLevel, position);
            entity.InitStats();
            mesh.Assets.NetworkSpawn(identity);
            return true;
        }

        /// <summary>The first harvestable prefab of the map's harvestable spawn areas, inactive included, in hierarchy order.</summary>
        private static HarvestableEntity FindContentPrefab(out int areaCount)
        {
            HarvestableSpawnArea[] areas = FindObjectsByType<HarvestableSpawnArea>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            areaCount = areas.Length;
            string[] keys = new string[areas.Length];
            for (int i = 0; i < areas.Length; ++i)
                keys[i] = HierarchyKey(areas[i].transform);
            Array.Sort(keys, areas, StringComparer.Ordinal);
            for (int i = 0; i < areas.Length; ++i)
            {
                HarvestableSpawnArea area = areas[i];
                if (area.prefab != null && area.prefab.Identity != null)
                    return area.prefab;
                if (area.spawningPrefabs == null)
                    continue;
                for (int j = 0; j < area.spawningPrefabs.Count; ++j)
                {
                    HarvestableEntity candidate = area.spawningPrefabs[j] != null ? area.spawningPrefabs[j].prefab : null;
                    if (candidate != null && candidate.Identity != null)
                        return candidate;
                }
            }
            return null;
        }

        /// <summary>A transform's sibling-index path from the root, zero-padded, so an ordinal sort is the hierarchy's order.</summary>
        private static string HierarchyKey(Transform transform)
        {
            List<int> path = new List<int>(8);
            for (Transform t = transform; t != null; t = t.parent)
                path.Add(t.GetSiblingIndex());
            StringBuilder b = new StringBuilder(64);
            for (int i = path.Count - 1; i >= 0; --i)
                b.Append(path[i].ToString("D6", System.Globalization.CultureInfo.InvariantCulture)).Append('/');
            return b.ToString();
        }

        private void StartWindow()
        {
            _lastWorkWindow = _mesh.WorkWindowCount;
            _windowStart = Time.unscaledTimeAsDouble;
        }

        private void WindowIfDue()
        {
            if (_mesh.WorkWindowCount == _lastWorkWindow)
                return;
            List<LiteNetLibIdentity> observers = ServerMeshSpaceTags.Observers;
            int alive = 0, otherMax = 0;
            long ownSum = 0;
            for (int i = observers.Count - 1; i >= 0; --i)
            {
                LiteNetLibIdentity observer = observers[i];
                if (observer == null || observer.IsDestroyed)
                {
                    observers.RemoveAt(i);
                    continue;
                }
                alive++;
                CountSeen(observer, out int own, out int other);
                ownSum += own;
                if (other > otherMax)
                    otherMax = other;
            }
            Logging.Log("Mesh", ServerMeshSpaceLayout.WindowLine(Time.unscaledTimeAsDouble - _windowStart, alive, alive > 0 ? ownSum / (double)alive : 0.0, otherMax));
            StartWindow();
        }

        /// <summary>
        /// CountSeen walks what <paramref name="observer"/> subscribes to (the kit's AOI result for its connection), itself
        /// excluded: own counts the entities of its space, other those of any other space; untagged entities count in neither.
        /// </summary>
        private void CountSeen(LiteNetLibIdentity observer, out int own, out int other)
        {
            own = other = 0;
            string space = observer.SubChannelId;
            HashSet<uint>.Enumerator subscribed = observer.GetSubscribings();
            while (subscribed.MoveNext())
            {
                uint id = subscribed.Current;
                if (id == observer.ObjectId || !_mesh.Assets.TryGetSpawnedObject(id, out LiteNetLibIdentity seen) || seen == null)
                    continue;
                string tag = seen.SubChannelId;
                if (string.Equals(tag, space, StringComparison.Ordinal))
                    own++;
                else if (ServerMeshSpaceLayout.IsSpaceId(tag))
                    other++;
            }
        }
    }
}
