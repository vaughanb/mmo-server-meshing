// Server Meshing test rig — scene monster awake probe (Phase 0 step 9 c2, MESH_MONSTER_AWAKE=1, step9_design.md
// decision 9): every 5 s on its own clock, how many live monsters this cell owns and how many of them are awake, in the
// Monsters window format the placer uses (ServerMeshMonsterPlacer.WindowLine, placed 0), so the stage 0b readers give the
// row its awake_monsters. Awake is the kit's rule as the mesh extends it (MeshMonsterActivityComponent.IsWatched, which
// is protected): a network subscriber, or a player ghost within the monster's visual range plus the component's ghost
// watch margin. Not with MESH_MONSTER_PLACE (the placer logs its own Monsters windows).
using System;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshMonsterAwake logs a Monsters window per 5 s on a map server: the cell's live non-ghost monsters (alive)
    /// and those awake (a subscriber, or a player ghost near, as MeshMonsterActivityComponent judges it).
    /// </summary>
    public sealed class ServerMeshMonsterAwake : MonoBehaviour
    {
        /// <summary>EnvironmentKey switches the probe on with 1.</summary>
        public const string EnvironmentKey = "MESH_MONSTER_AWAKE";
        /// <summary>PlaceKey is the placer's key; with it set the probe stays off.</summary>
        public const string PlaceKey = "MESH_MONSTER_PLACE";
        /// <summary>WindowSeconds is the length of a window on the probe's own clock.</summary>
        public const double WindowSeconds = 5;
        /// <summary>DefaultGhostMargin is the ghost watch margin used for a monster without a MeshMonsterActivityComponent.</summary>
        public const float DefaultGhostMargin = 10f;

        private MeshMapNetworkManager _mesh;
        private double _windowStart;
        private int _gcBase;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string value = Environment.GetEnvironmentVariable(EnvironmentKey);
            if (string.IsNullOrEmpty(value))
                return;
            if (!ConfigReader.IsArgsProvided(Environment.GetCommandLineArgs(), ProcessArguments.ARG_START_MAP_SERVER))
                return;
            if (!ServerMeshNetStats.TryParseSwitch(value, out bool on, out string error))
            {
                Logging.LogError("Mesh", "Monster awake error: " + error.Replace(ServerMeshNetStats.EnvironmentKey, EnvironmentKey) + "; no probe");
                return;
            }
            if (!on || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PlaceKey)) || FindFirstObjectByType<ServerMeshMonsterAwake>() != null)
                return;
            GameObject go = new GameObject("[Mesh] MonsterAwake");
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshMonsterAwake>();
        }

        private void Update()
        {
            if (_mesh == null)
            {
                _mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
                if (_mesh == null || !_mesh.IsServer)
                    return;
                StartWindow();
                Logging.Log("Mesh", "Monster awake probe: a Monsters window every 5 s of this cell's scene monsters (placed 0)");
                return;
            }
            if (Time.unscaledTimeAsDouble - _windowStart < WindowSeconds)
                return;
            int alive = 0, awake = 0;
            BaseMonsterCharacterEntity[] monsters = FindObjectsByType<BaseMonsterCharacterEntity>(FindObjectsSortMode.None);
            for (int i = 0; i < monsters.Length; ++i)
            {
                BaseMonsterCharacterEntity monster = monsters[i];
                if (monster == null || monster.Identity == null || monster.Identity.IsDestroyed || monster.IsDead() || MeshIdentity.IsGhostEntity(monster))
                    continue;
                alive++;
                if (IsAwake(monster))
                    awake++;
            }
            Logging.Log("Mesh", ServerMeshMonsterPlacer.WindowLine(Time.unscaledTimeAsDouble - _windowStart, _mesh.OwnZoneId, 0, alive, awake, 0, 0,
                GC.CollectionCount(0) - _gcBase));
            StartWindow();
        }

        private bool IsAwake(BaseMonsterCharacterEntity monster)
        {
            if (monster.Identity.CountSubscribers() > 0)
                return true;
            MeshMonsterActivityComponent activity = monster.GetComponent<MeshMonsterActivityComponent>();
            float margin = activity != null ? activity.ghostWatchMargin : DefaultGhostMargin;
            float range = (monster.CharacterDatabase != null ? monster.CharacterDatabase.VisualRange : 0f) + margin;
            return _mesh.IsPlayerGhostNear(monster.EntityTransform.position, range);
        }

        private void StartWindow()
        {
            _windowStart = Time.unscaledTimeAsDouble;
            _gcBase = GC.CollectionCount(0);
        }
    }
}
