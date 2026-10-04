// Server Meshing test rig — monsters placed at seeded points in discs (Phase 0 step 8a,
// MESH_MONSTER_PLACE=<n>@<x>,<z>,<r>[:passive][;...]): the cell that owns a disc's centre spawns its monsters with the
// kit's GM recipe, so the mesh tracks and ghosts them as it does a spawn area's monsters; it keeps their count constant
// (heals below half, re-places a destroyed one after 10 s) and logs a Monsters window line with every Work window. Every
// cell registers the prefab, since MESH_NO_MONSTERS=1 hides the areas from the kit and a neighbour must know the prefab
// to ghost a placed monster. Window lines in _Window.cs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshMonsterPlacer registers the test map's first spawn-area monster prefab once the map server can instantiate
    /// objects, then places the monsters of every disc whose centre this cell owns: GetObjectInstance, SetSpawnArea with no
    /// area, NetworkSpawn. A passive disc's monsters take the kit's NoHarm characteristic override, so they never attack;
    /// the kit's Clean does not reset that override, so when the placer drops a monster (dead, destroyed, or this
    /// component going) it puts the prefab's two values back before the instance returns to the pool.
    /// A placed monster has no spawn area, so every spec that places monsters must set MESH_NO_MONSTER_HANDOFF=1.
    /// </summary>
    public sealed partial class ServerMeshMonsterPlacer : MonoBehaviour
    {
        /// <summary>EnvironmentKey is the placer's setting.</summary>
        public const string EnvironmentKey = "MESH_MONSTER_PLACE";
        /// <summary>PlacerObjectName names the placer's GameObject.</summary>
        public const string PlacerObjectName = "[Mesh] MonsterPlacer";
        /// <summary>SeedKey is the load generator's seed, which keys the placement points (default 12345).</summary>
        public const string SeedKey = "MESH_LOAD_SEED";
        /// <summary>MaxTotal is the most monsters one setting places over all its discs.</summary>
        public const int MaxTotal = 2000;
        /// <summary>Salt keys the placement points off MESH_LOAD_SEED.</summary>
        public const int Salt = 0x534D;
        /// <summary>ReplaceSeconds is how long after a placed monster is found dead or destroyed it is placed again.</summary>
        public const float ReplaceSeconds = 10f;
        /// <summary>PassiveSuffix is the disc suffix (after a colon) that makes its monsters passive.</summary>
        public const string PassiveSuffix = "passive";

        /// <summary>ServerMeshMonsterDisc is one part of the setting: Count monsters in the disc (X, Z, Radius) of the scene frame.</summary>
        public sealed class ServerMeshMonsterDisc
        {
            /// <summary>Count is how many monsters the disc holds (1 or more).</summary>
            public int Count;
            /// <summary>X is the disc centre's scene x.</summary>
            public float X;
            /// <summary>Z is the disc centre's scene z.</summary>
            public float Z;
            /// <summary>Radius is the disc radius in metres (above 0).</summary>
            public float Radius;
            /// <summary>Passive is true for a :passive disc: its monsters never attack.</summary>
            public bool Passive;
            /// <summary>Text is the part as given in the setting, without whitespace (for the setting line).</summary>
            public string Text;
        }

        private sealed class ServerMeshPlacedMonster
        {
            public Vector3 point;
            public bool passive;
            public BaseMonsterCharacterEntity entity;
            public float deadSince = -1f;
        }

        private readonly List<ServerMeshPlacedMonster> _placed = new List<ServerMeshPlacedMonster>();
        private List<ServerMeshMonsterDisc> _discs;
        private MeshMapNetworkManager _mesh;
        private BaseMonsterCharacterEntity _prefab;
        private int _level = 1;
        private int _placedCount;
        private UnityEngine.Events.UnityAction<LiteNetLibIdentity> _onDestroy;
        private bool _started;
        private bool _idle;
        private bool _halted;
        private bool _quitting;
        private float _nextTry;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string value = Environment.GetEnvironmentVariable(EnvironmentKey);
            if (string.IsNullOrEmpty(value))
                return;
            if (!ConfigReader.IsArgsProvided(Environment.GetCommandLineArgs(), ProcessArguments.ARG_START_MAP_SERVER))
                return;
            if (!TryParseSetting(value, out List<ServerMeshMonsterDisc> discs, out string error))
            {
                Logging.LogError("Mesh", "Monsters error: " + error + "; nothing placed");
                return;
            }
            if (FindFirstObjectByType<ServerMeshMonsterPlacer>() != null)
                return;
            GameObject go = new GameObject(PlacerObjectName);
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshMonsterPlacer>()._discs = discs;
        }

        /// <summary>
        /// TryParseSetting reads <c>&lt;n&gt;@&lt;x&gt;,&lt;z&gt;,&lt;r&gt;[:passive][;...]</c> (parts trimmed, the suffix
        /// ignoring case, numbers invariant): n a whole number of 1 or more, x and z finite, r finite and above 0, the total
        /// at most 2,000. False with no discs and an error naming the key and the bad part otherwise.
        /// </summary>
        public static bool TryParseSetting(string text, out List<ServerMeshMonsterDisc> discs, out string error)
        {
            discs = null;
            string value = text == null ? string.Empty : text.Trim();
            if (value.Length == 0)
            {
                error = $"{EnvironmentKey} is empty; it needs <n>@<x>,<z>,<r>[:passive][;...]";
                return false;
            }
            string[] parts = value.Split(';');
            List<ServerMeshMonsterDisc> parsed = new List<ServerMeshMonsterDisc>(parts.Length);
            long total = 0;
            for (int i = 0; i < parts.Length; ++i)
            {
                string part = parts[i].Trim();
                if (!TryParseDisc(part, out ServerMeshMonsterDisc disc, out string problem))
                {
                    error = $"{EnvironmentKey}={Printable(value)}: the part {Printable(part)} {problem}";
                    return false;
                }
                total += disc.Count;
                if (total > MaxTotal)
                {
                    error = $"{EnvironmentKey}={Printable(value)}: the part {Printable(part)} brings the total to {total.ToString(CultureInfo.InvariantCulture)}, over {MaxTotal}";
                    return false;
                }
                parsed.Add(disc);
            }
            discs = parsed;
            error = null;
            return true;
        }

        private static bool TryParseDisc(string part, out ServerMeshMonsterDisc disc, out string problem)
        {
            disc = null;
            string body = part;
            bool passive = false;
            int colon = part.IndexOf(':');
            if (colon >= 0)
            {
                if (!string.Equals(part.Substring(colon + 1).Trim(), PassiveSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    problem = "has a suffix that is not :passive";
                    return false;
                }
                passive = true;
                body = part.Substring(0, colon).Trim();
            }
            int at = body.IndexOf('@');
            if (at <= 0 || at == body.Length - 1 || body.IndexOf('@', at + 1) >= 0)
            {
                problem = "is not <n>@<x>,<z>,<r>[:passive]";
                return false;
            }
            if (!int.TryParse(body.Substring(0, at).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1)
            {
                problem = "has a count that is not a whole number of at least 1";
                return false;
            }
            string[] numbers = body.Substring(at + 1).Split(',');
            if (numbers.Length != 3 || !TryFinite(numbers[0], out float x) || !TryFinite(numbers[1], out float z) || !TryFinite(numbers[2], out float r))
            {
                problem = "does not have a centre and radius x,z,r of finite numbers";
                return false;
            }
            if (r <= 0f)
            {
                problem = "has a radius that is not above 0";
                return false;
            }
            disc = new ServerMeshMonsterDisc { Count = count, X = x, Z = z, Radius = r, Passive = passive, Text = WithoutWhitespace(part) };
            problem = null;
            return true;
        }

        /// <summary>
        /// TryReadSeed is <see cref="TryParseSeed"/> with the error a caller logs when it is false: the key and the text as
        /// given (null when true).
        /// </summary>
        public static bool TryReadSeed(string text, out int seed, out string error)
        {
            if (TryParseSeed(text, out seed))
            {
                error = null;
                return true;
            }
            error = $"{SeedKey}={Printable(text == null ? null : text.Trim())} is not a whole number";
            return false;
        }

        /// <summary>
        /// ErrorLine is the placer's error line for a cell that places nothing: <c>Monsters error: cell C: reason; nothing
        /// placed</c>, the reason cleaned to one invariant line.
        /// </summary>
        public static string ErrorLine(string cell, string reason)
        {
            return "Monsters error: cell " + Printable(cell) + ": " + MeshStateSender.CleanForLog(reason ?? "-") + "; nothing placed";
        }

        /// <summary>
        /// TryParseSeed reads MESH_LOAD_SEED: null or blank gives 12345 (true), a whole number gives itself (true), anything
        /// else gives 12345 and false.
        /// </summary>
        public static bool TryParseSeed(string text, out int seed)
        {
            seed = ServerMeshLoadGenerator.DefaultSeed;
            string trimmed = text == null ? string.Empty : text.Trim();
            if (trimmed.Length == 0)
                return true;
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                seed = parsed;
                return true;
            }
            return false;
        }

        /// <summary>
        /// DiscPoint is point <paramref name="k"/> of disc <paramref name="disc"/>: uniform over the disc of
        /// <paramref name="radius"/> about (<paramref name="x"/>, <paramref name="z"/>) at the ground height, drawn from a
        /// random stream keyed by the seed, <paramref name="salt"/>, the disc and k (the same inputs give the same point).
        /// </summary>
        public static Vector3 DiscPoint(int seed, int salt, int disc, int k, float x, float z, float radius)
        {
            System.Random random = new System.Random(ServerMeshShipFlightPath.MixSeed(ServerMeshShipFlightPath.MixSeed(seed ^ salt, disc), k));
            double angle = random.NextDouble() * 2.0 * Math.PI;
            double distance = Math.Max(0f, radius) * Math.Sqrt(random.NextDouble());
            return new Vector3(x + (float)(Math.Cos(angle) * distance), ServerMeshShipLoad.GroundY, z + (float)(Math.Sin(angle) * distance));
        }

        /// <summary>
        /// DiscInside is true when the disc lies inside <paramref name="cell"/> (scene x and z; touching an edge counts as
        /// inside); a disc that crosses the edge of the cell owning its centre is refused. A null disc throws naming it.
        /// </summary>
        public static bool DiscInside(Rect cell, ServerMeshMonsterDisc disc)
        {
            if (disc == null)
                throw new ArgumentNullException(nameof(disc));
            return disc.X - disc.Radius >= cell.xMin && disc.X + disc.Radius <= cell.xMax && disc.Z - disc.Radius >= cell.yMin && disc.Z + disc.Radius <= cell.yMax;
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
            Keep(Time.unscaledTime);
            WindowIfDue();
        }

        private void TryStart()
        {
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || mesh.OwnCell == null || !mesh.IsServerReadyToInstantiateObjects())
                return;
            _started = true;
            _mesh = mesh;
            string cellId = mesh.OwnZoneId;
            if (!TryRegisterPrefab(mesh, out string error) || !TryReadSeed(Environment.GetEnvironmentVariable(SeedKey), out int seed, out error))
            {
                Logging.LogError("Mesh", ErrorLine(cellId, error)); // a seed error leaves the prefab registered, for ghosting
                _idle = true;
                return;
            }
            MeshCell cell = mesh.OwnCell;
            Rect cellRect = Rect.MinMaxRect(cell.owned.minX - cell.frameOrigin.x, cell.owned.minZ - cell.frameOrigin.z, cell.owned.maxX - cell.frameOrigin.x, cell.owned.maxZ - cell.frameOrigin.z);
            StringBuilder owned = new StringBuilder(64);
            for (int d = 0; d < _discs.Count; ++d)
            {
                ServerMeshMonsterDisc disc = _discs[d];
                Vector3 world = cell.SceneToWorld(new Vector3(disc.X, 0f, disc.Z));
                if (!cell.ContainsWorld(world.x, world.z))
                    continue; // another cell owns the centre and places this disc
                if (!DiscInside(cellRect, disc))
                {
                    Logging.LogError("Mesh", $"Monsters error: cell {Printable(cellId)}: the part {Printable(disc.Text)} crosses the cell's edge (x {Metres(cellRect.xMin)} to {Metres(cellRect.xMax)}, z {Metres(cellRect.yMin)} to {Metres(cellRect.yMax)}); that disc is not placed");
                    continue;
                }
                if (owned.Length > 0)
                    owned.Append(';');
                owned.Append(disc.Text);
                for (int k = 0; k < disc.Count; ++k)
                    _placed.Add(new ServerMeshPlacedMonster { point = DiscPoint(seed, Salt, d, k, disc.X, disc.Z, disc.Radius), passive = disc.Passive });
            }
            if (_placed.Count == 0)
            {
                _idle = true; // no disc of this cell: the prefab is registered for ghosting and nothing is logged
                return;
            }
            _onDestroy = OnObjectDestroy;
            mesh.Assets.onObjectDestroy.AddListener(_onDestroy);
            _placedCount = 0;
            for (int i = 0; i < _placed.Count && !_halted; ++i)
            {
                if (Place(_placed[i]))
                    _placedCount++;
            }
            Logging.Log("Mesh", SettingLine(_placedCount, owned.ToString(), cellId));
            StartWindow();
        }

        private bool TryRegisterPrefab(MeshMapNetworkManager mesh, out string error)
        {
            MonsterSpawnArea[] areas = FindObjectsByType<MonsterSpawnArea>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            string[] keys = new string[areas.Length];
            for (int i = 0; i < areas.Length; ++i)
                keys[i] = HierarchyKey(areas[i].transform);
            Array.Sort(keys, areas, StringComparer.Ordinal);
            for (int i = 0; i < areas.Length && _prefab == null; ++i)
            {
                MonsterSpawnArea area = areas[i];
                if (area.prefab != null)
                {
                    _prefab = area.prefab;
                    _level = Math.Max(1, area.minLevel);
                    break;
                }
                if (area.spawningPrefabs == null)
                    continue;
                for (int j = 0; j < area.spawningPrefabs.Count; ++j)
                {
                    if (area.spawningPrefabs[j] == null || area.spawningPrefabs[j].prefab == null)
                        continue;
                    _prefab = area.spawningPrefabs[j].prefab;
                    _level = Math.Max(1, area.spawningPrefabs[j].minLevel);
                    break;
                }
            }
            if (_prefab == null || _prefab.Identity == null)
            {
                _prefab = null;
                error = $"no monster prefab in the map's {areas.Length} monster spawn area(s), inactive included";
                return false;
            }
            GameInstance.AddMonsterCharacterEntities(_prefab);
            mesh.Assets.RegisterPrefab(_prefab.Identity);
            error = null;
            return true;
        }

        private bool Place(ServerMeshPlacedMonster slot)
        {
            LiteNetLibIdentity identity = _mesh.Assets.GetObjectInstance(_prefab.Identity.HashAssetId, slot.point, Quaternion.identity);
            BaseMonsterCharacterEntity monster = identity == null ? null : identity.GetComponent<BaseMonsterCharacterEntity>();
            if (monster == null)
            {
                if (identity != null)
                    Destroy(identity.gameObject);
                Logging.LogError("Mesh", $"Monsters error: cell {Printable(_mesh.OwnZoneId)}: no monster instance for prefab hash {_prefab.Identity.HashAssetId}; no more monsters placed");
                _halted = true;
                return false;
            }
            // A pooled instance keeps the last placement's override, so it is set from the prefab every time.
            monster.IsOverrideCharacteristic = slot.passive || _prefab.IsOverrideCharacteristic;
            monster.OverrideCharacteristic = slot.passive ? MonsterCharacteristic.NoHarm : _prefab.OverrideCharacteristic;
            monster.Level = _level;
            monster.SetSpawnArea(null, _prefab, _level, slot.point);
            _mesh.Assets.NetworkSpawn(identity);
            slot.entity = monster;
            slot.deadSince = -1f;
            return true;
        }

        private void Keep(float now)
        {
            for (int i = 0; i < _placed.Count; ++i)
            {
                ServerMeshPlacedMonster slot = _placed[i];
                BaseMonsterCharacterEntity monster = slot.entity;
                if (!ReferenceEquals(monster, null))
                {
                    if (monster == null || monster.Identity == null || monster.Identity.IsDestroyed || monster.IsDead())
                    {
                        // Dropped at once: a pooled instance may come back as another slot's monster. A destroyed one was
                        // restored by OnObjectDestroy before it was pooled; a dead one is restored here, before its destroy.
                        if (monster != null && monster.Identity != null && !monster.Identity.IsDestroyed)
                            RestorePrefabCharacteristic(monster);
                        slot.entity = null;
                        slot.deadSince = now;
                        continue;
                    }
                    if (!MeshIdentity.IsGhostEntity(monster) && (long)monster.CurrentHp * 2 < monster.MaxHp)
                    {
                        monster.CurrentHp = monster.MaxHp;
                        _healed++;
                    }
                    continue;
                }
                if (slot.deadSince >= 0f && !_halted && now - slot.deadSince >= ReplaceSeconds && Place(slot))
                    _respawned++;
            }
        }

        /// <summary>OnObjectDestroy drops a placed monster's slot as the kit destroys it, restoring the prefab's override first.</summary>
        private void OnObjectDestroy(LiteNetLibIdentity identity)
        {
            if (identity == null || _prefab == null || identity.HashAssetId != _prefab.Identity.HashAssetId)
                return;
            for (int i = 0; i < _placed.Count; ++i)
            {
                ServerMeshPlacedMonster slot = _placed[i];
                if (slot.entity == null || slot.entity.Identity != identity)
                    continue;
                RestorePrefabCharacteristic(slot.entity);
                slot.entity = null;
                slot.deadSince = Time.unscaledTime;
                return;
            }
        }

        /// <summary>RestorePrefabCharacteristic puts the prefab's characteristic override back on a placed instance.</summary>
        private void RestorePrefabCharacteristic(BaseMonsterCharacterEntity monster)
        {
            monster.IsOverrideCharacteristic = _prefab.IsOverrideCharacteristic;
            monster.OverrideCharacteristic = _prefab.OverrideCharacteristic;
        }

        private static string HierarchyKey(Transform transform)
        {
            List<int> path = new List<int>(8);
            for (Transform t = transform; t != null; t = t.parent)
                path.Add(t.GetSiblingIndex());
            StringBuilder b = new StringBuilder(64);
            b.Append(transform.gameObject.scene.buildIndex.ToString("D4", CultureInfo.InvariantCulture));
            for (int i = path.Count - 1; i >= 0; --i)
                b.Append('/').Append(path[i].ToString("D6", CultureInfo.InvariantCulture));
            return b.ToString();
        }

        private static bool TryFinite(string text, out float value)
        {
            return float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static string WithoutWhitespace(string text)
        {
            StringBuilder b = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; ++i)
            {
                if (!char.IsWhiteSpace(text[i]))
                    b.Append(text[i]);
            }
            return b.ToString();
        }

        private void OnApplicationQuit() => _quitting = true;

        private void OnDestroy()
        {
            if (_mesh != null && _mesh.Assets != null && _onDestroy != null)
                _mesh.Assets.onObjectDestroy.RemoveListener(_onDestroy);
            for (int i = 0; i < _placed.Count && _prefab != null; ++i)
            {
                BaseMonsterCharacterEntity monster = _placed[i].entity;
                if (monster != null && monster.Identity != null && !monster.Identity.IsDestroyed)
                    RestorePrefabCharacteristic(monster); // before any pooling, the kit's own pass at quit included
            }
            if (_quitting || _mesh == null || !_mesh.IsServer)
                return; // at quit or Play stop the kit destroys every object in its own pass
            for (int i = 0; i < _placed.Count; ++i)
            {
                BaseMonsterCharacterEntity monster = _placed[i].entity;
                if (monster != null && monster.Identity != null && !monster.Identity.IsDestroyed)
                    monster.Identity.NetworkDestroy();
            }
            _placed.Clear();
        }
    }
}
