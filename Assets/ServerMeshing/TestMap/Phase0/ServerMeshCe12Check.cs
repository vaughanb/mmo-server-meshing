// Server Meshing add-on — rig CE-12 check (MESH_CE12_CHECK=1): on each serving map server, teleports one hidden,
// server-owned character and logs how its kit movement handler clears the one-shot IsTeleport flag.
using System;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshCe12Check runs once per server start of a -startMapServer process when MESH_CE12_CHECK=1. Once the cell is
    /// served and the kit has spawned its scene entities, it spawns one character (the phantom recipe) hidden before its
    /// spawn and owned by the server, so neither the kit nor the state sender writes it and no teleport confirm is awaited.
    /// It retries for up to <see cref="MaxWaitFrames"/> frames until the character's kit handler writes; then, in one call,
    /// it teleports the character, writes at T, T and T+50, consumes the handler's flags when it can, writes at T+100,
    /// logs one "CE-12 check:" line (ServerMeshCe12Shape) and destroys the character. Today's kit reports pass.
    /// </summary>
    public class ServerMeshCe12Check : MonoBehaviour
    {
        /// <summary>ENV_CE12_CHECK is the environment key; "1" installs the check.</summary>
        public const string ENV_CE12_CHECK = "MESH_CE12_CHECK";
        /// <summary>ObjectName names the check's GameObject.</summary>
        public const string ObjectName = "[Mesh] Ce12Check";
        /// <summary>MaxWaitFrames is how many frames the check waits for the handler's first successful write before it reports absent.</summary>
        public const int MaxWaitFrames = 300;
        /// <summary>TeleportFlag is the IsTeleport bit of the record's packed movement state.</summary>
        public const uint TeleportFlag = (uint)MovementState.IsTeleport;

        private readonly NetDataWriter _scratch = new NetDataWriter(true, 64);
        private readonly NetDataReader _reader = new NetDataReader();
        private MeshMapNetworkManager _mesh;
        private BasePlayerCharacterEntity _entity;
        private bool _kitSpawned;
        private bool _done;
        private int _waitFrames;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string value = Environment.GetEnvironmentVariable(ENV_CE12_CHECK);
            if (string.IsNullOrEmpty(value))
                return;
            if (value.Trim() != "1")
            {
                Logging.LogWarning("Mesh", $"CE-12 check: {ENV_CE12_CHECK}={MeshStateSender.CleanForLog(value.Trim())} is not 1; nothing installed");
                return;
            }
            if (!ConfigReader.IsArgsProvided(Environment.GetCommandLineArgs(), ProcessArguments.ARG_START_MAP_SERVER))
                return;
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager))
                return;
            if (FindFirstObjectByType<ServerMeshCe12Check>() != null)
                return;
            GameObject go = new GameObject(ObjectName);
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshCe12Check>();
        }

        private void Update()
        {
            MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
            if (mesh == null || !mesh.IsServer)
            {
                if (_mesh != null)
                    ResetForNextStart();
                return;
            }
            if (_mesh == null)
            {
                _mesh = mesh;
                mesh.onSpawnEntitiesFinish.AddListener(OnKitSpawnedEntities);
            }
            if (_done || mesh.OwnCell == null || !_kitSpawned || !mesh.IsServerReadyToInstantiateObjects())
                return;
            try
            {
                if (_entity == null)
                {
                    _entity = Spawn(mesh);
                    _waitFrames = 0;
                    if (_entity == null)
                    {
                        _done = true;
                        return;
                    }
                }
                Probe(mesh);
            }
            catch (Exception error)
            {
                _done = true;
                Logging.LogWarning("Mesh", $"CE-12 check: cell {mesh.OwnZoneId} stopped: {MeshStateSender.CleanForLog(error.Message)}");
                DestroyCharacter();
            }
        }

        private void Probe(MeshMapNetworkManager mesh)
        {
            uint objectId = _entity.ObjectId;
            bool hasHandler = MeshMovementHandlerSlots.For(mesh).TryGetKitHandler(objectId, out IEntityMovementDataHandler kit) && kit != null;
            if (!hasHandler || !Write(kit, mesh.ServerTimestamp, out _, out _))
            {
                if (++_waitFrames < MaxWaitFrames)
                    return;
                Report(mesh, hasHandler ? kit : null, false, false, false, false, hasHandler && kit is IEntityMovementServerStateFlags, false, false, false, false, false);
                return;
            }
            Vector3 position = _entity.EntityTransform.position;
            _entity.Teleport(position + new Vector3(0f, 0f, 1f), _entity.EntityTransform.rotation, false);
            long t = mesh.ServerTimestamp;
            bool firstWrote = Write(kit, t, out bool first, out bool reliableFirst);
            Write(kit, t, out bool second, out bool reliableSecond);
            Write(kit, t + 50, out bool next, out bool reliableNext);
            bool consumeAvailable = kit is IEntityMovementServerStateFlags;
            if (consumeAvailable)
                ((IEntityMovementServerStateFlags)kit).ConsumeServerStateFlags();
            Write(kit, t + 100, out bool after, out bool reliableAfter);
            Report(mesh, kit, firstWrote, first, second, next, consumeAvailable, after, reliableFirst, reliableSecond, reliableNext, reliableAfter);
        }

        private void Report(MeshMapNetworkManager mesh, IEntityMovementDataHandler kit, bool firstWrote, bool first, bool second, bool next,
            bool consumeAvailable, bool after, bool reliableFirst, bool reliableSecond, bool reliableNext, bool reliableAfter)
        {
            string handler = kit != null ? kit.GetType().Name : null;
            Logging.Log("Mesh", ServerMeshCe12Shape.Line(mesh.OwnZoneId, firstWrote, first, second, next, consumeAvailable, after,
                reliableFirst, reliableSecond, reliableNext, reliableAfter, handler));
            _done = true;
            DestroyCharacter();
        }

        private bool Write(IEntityMovementDataHandler kit, long timestamp, out bool teleport, out bool reliable)
        {
            _scratch.Reset();
            bool wrote = kit.WriteServerState(timestamp, _scratch, out reliable);
            teleport = false;
            if (wrote && _scratch.Length > 0)
            {
                _reader.SetSource(_scratch.Data, 0, _scratch.Length);
                teleport = (_reader.GetPackedUInt() & TeleportFlag) != 0;
            }
            if (!wrote)
                reliable = false;
            return wrote;
        }

        private BasePlayerCharacterEntity Spawn(MeshMapNetworkManager mesh)
        {
            int classId = 0, entityId = 0;
            foreach (int id in GameInstance.PlayerCharacters.Keys) { classId = id; break; }
            foreach (int id in GameInstance.PlayerCharacterEntities.Keys) { entityId = id; break; }
            string cell = mesh.OwnZoneId;
            if (classId == 0 || entityId == 0)
            {
                Logging.LogWarning("Mesh", $"CE-12 check: cell {cell}: no player class or entity in the game database; nothing checked");
                return null;
            }
            MeshCell own = mesh.OwnCell;
            Vector3 position = new Vector3((own.owned.minX + own.owned.maxX) * 0.5f - own.frameOrigin.x, 0f, (own.owned.minZ + own.owned.maxZ) * 0.5f - own.frameOrigin.z);
            string characterId = "ce12check-" + cell;
            PlayerCharacterData data = new PlayerCharacterData();
            data.SetNewPlayerCharacterData("Ce12Check" + cell, classId, entityId, 0);
            data.Id = characterId;
            data.Level = 1;
            data.CurrentMapName = mesh.OwnMapId;
            data.CurrentPosition = position;
            LiteNetLibIdentity spawnObj = mesh.Assets.GetObjectInstance(GameInstance.GetPlayerCharacterEntityHashAssetId(data.EntityId, out int metaDataId), position, Quaternion.identity);
            if (spawnObj == null)
            {
                Logging.LogWarning("Mesh", $"CE-12 check: cell {cell}: no instance of the player entity for {characterId}; nothing checked");
                return null;
            }
            BasePlayerCharacterEntity entity = spawnObj.GetComponent<BasePlayerCharacterEntity>();
            if (entity == null)
            {
                Destroy(spawnObj.gameObject);
                Logging.LogWarning("Mesh", $"CE-12 check: cell {cell}: the player entity instance for {characterId} has no player character; nothing checked");
                return null;
            }
            GameInstance.SetupByMetaData(entity, metaDataId);
            data.CloneTo(entity);
            spawnObj.SetIsHide(this, true);
            mesh.Assets.NetworkSpawn(spawnObj, 0, -1);
            return entity;
        }

        private void DestroyCharacter()
        {
            BasePlayerCharacterEntity entity = _entity;
            _entity = null;
            if (entity != null && entity.Identity != null && !entity.Identity.IsDestroyed)
                entity.Identity.NetworkDestroy();
        }

        private void OnKitSpawnedEntities(string sceneName, bool isOnline, bool isAdditive, float progress)
        {
            _kitSpawned = true;
        }

        private void ResetForNextStart()
        {
            _mesh.onSpawnEntitiesFinish.RemoveListener(OnKitSpawnedEntities);
            _mesh = null;
            _entity = null;
            _kitSpawned = false;
            _done = false;
            _waitFrames = 0;
        }
    }
}
