// Server Meshing add-on — rig phantom binder, spawn part: binds sink hellos and spawns phantom characters, eyes, ships and filler.
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshPhantomClients
    {
        private bool BindHello(MeshMapNetworkManager mesh, double now)
        {
            while (_transport.TryDequeueHello(out long helloConnection, out int helloIndex))
            {
                _helloConnections.Enqueue(helloConnection);
                _helloIndices.Enqueue(helloIndex);
            }
            while (_helloConnections.Count > 0)
            {
                long connection = _helloConnections.Dequeue();
                int index = _helloIndices.Dequeue();
                if (index < 0 || index >= _slots.Count)
                {
                    if (!_warnedHelloRange)
                    {
                        _warnedHelloRange = true;
                        _log.Warning(System.FormattableString.Invariant($"Phantoms: cell {_cell}: hello index {index} from connection {connection} is outside 0 to {_slots.Count - 1}; ignored"));
                    }
                    continue;
                }
                ServerMeshPhantomSlot slot = _slots[index];
                if (_sinkConnections.Contains(connection) || (slot.connectionId >= 0 && IsAlive(mesh, slot)))
                {
                    if (!_warnedHelloLive)
                    {
                        _warnedHelloLive = true;
                        _log.Warning(System.FormattableString.Invariant($"Phantoms: cell {_cell}: hello index {index} from connection {connection} is ignored: the index is bound to live connection {slot.connectionId} or the connection already holds an index"));
                    }
                    continue;
                }
                if (slot.connectionId >= 0)
                    Release(slot);
                if (!mesh.TryGetPlayer(connection, out LiteNetLibPlayer player))
                    continue;
                slot.connectionId = connection;
                slot.sink = true;
                if (!TrySpawn(mesh, slot, player, now))
                {
                    Release(slot);
                    if (_halted)
                        continue;
                    _helloConnections.Enqueue(connection);
                    _helloIndices.Enqueue(index);
                    return false;
                }
                _sinkConnections.Add(connection);
                Reply(mesh, slot);
                return true;
            }
            return false;
        }

        private bool TrySpawn(MeshMapNetworkManager mesh, ServerMeshPhantomSlot slot, LiteNetLibPlayer player, double now)
        {
            Vector3 position = new Vector3(slot.spawn.x, 0f, slot.spawn.y);
            Transform host = null;
            bool eyes = _settings.Kind == ServerMeshPhantomKind.Eye;
            if (eyes)
            {
                host = PickHost(mesh, slot, now);
                if (host == null)
                    return false;
                position = host.position;
            }
            player.IsReady = true;
            if (eyes)
            {
                ServerMeshPhantomEye eye = ServerMeshPhantomEye.Create(System.FormattableString.Invariant($"phantom-eye-{_cell}-{slot.connectionId}"), position, HideFlags.None);
                mesh.Assets.NetworkSpawn(eye.Identity, 0, slot.connectionId);
                eye.Follow(host);
                slot.identity = eye.Identity;
                slot.eye = eye;
            }
            else if (_settings.Kind == ServerMeshPhantomKind.Ship)
            {
                if (!SpawnShip(mesh, slot, now))
                {
                    Halt();
                    return false;
                }
            }
            else
            {
                string id = System.FormattableString.Invariant($"phantom-{_cell}-{slot.connectionId}");
                string name = System.FormattableString.Invariant($"Phantom{_cell}{slot.connectionId}");
                BasePlayerCharacterEntity entity = SpawnCharacter(mesh, id, name, position, slot.connectionId, slot.input);
                if (entity == null)
                {
                    Halt();
                    return false;
                }
                slot.entity = entity;
                slot.identity = entity.Identity;
                slot.walkPosition = position;
                slot.random = new System.Random(unchecked(_settings.Seed * 486187739 ^ (slot.index * 16777619 + 97)));
                slot.nextTurn = now;
                slot.nextDue = ServerMeshPhantomUploads.FirstDue(now, 1.0 / _settings.UploadHz, ServerMeshPhantomUploads.Unit(_settings.Seed, slot.index, PhaseSalt));
                if (slot.fighting)
                    _fighter.Add(entity, unchecked(_settings.Seed + slot.index));
            }
            slot.objectId = slot.identity.ObjectId;
            slot.bound = true;
            slot.lastDownBytes = 0;
            if (_viewSampler != null)
                _viewSampler.Track(slot.connectionId);
            return true;
        }

        private BasePlayerCharacterEntity SpawnCharacter(MeshMapNetworkManager mesh, string id, string name, Vector3 position, long owner, bool input)
        {
            PlayerCharacterData data = new PlayerCharacterData();
            data.SetNewPlayerCharacterData(name, _classId, _entityId, 0);
            data.Id = id;
            data.Level = 5;
            data.CurrentMapName = mesh.OwnMapId;
            data.CurrentPosition = position;
            LiteNetLibIdentity spawnObj = mesh.Assets.GetObjectInstance(GameInstance.GetPlayerCharacterEntityHashAssetId(data.EntityId, out int metaDataId), position, Quaternion.identity);
            if (spawnObj == null)
            {
                _log.Error($"Phantoms error: cell {_cell}: no instance of the player entity for {id}");
                return null;
            }
            BasePlayerCharacterEntity entity = spawnObj.GetComponent<BasePlayerCharacterEntity>();
            if (entity == null)
            {
                Destroy(spawnObj.gameObject);
                _log.Error($"Phantoms error: cell {_cell}: the player entity instance for {id} has no player character");
                return null;
            }
            GameInstance.SetupByMetaData(entity, metaDataId);
            data.CloneTo(entity);
            if (input && !SetSecure(spawnObj, MovementSecure.ServerAuthoritative))
            {
                Destroy(spawnObj.gameObject);
                _log.Error($"Phantoms error: cell {_cell}: {ServerMeshPhantomSettings.ENV_PAYLOAD}=input needs a CharacterControllerEntityMovement on the player entity; {id} not spawned");
                return null;
            }
            mesh.Assets.NetworkSpawn(spawnObj, 0, owner);
            entity.ForceMakeCaches();
            entity.FillEmptySlots();
            entity.CurrentHp = entity.MaxHp;
            entity.CurrentMp = entity.MaxMp;
            mesh.TrackSyntheticEntity(entity);
            return entity;
        }

        private static bool SetSecure(LiteNetLibIdentity identity, MovementSecure secure)
        {
            if (identity == null)
                return false;
            CharacterControllerEntityMovement movement = identity.GetComponent<CharacterControllerEntityMovement>();
            if (movement == null || movement.Functions == null)
                return false;
            movement.movementSecure = secure;
            movement.Functions.movementSecure = secure;
            return true;
        }

        private void SpawnFiller(MeshMapNetworkManager mesh)
        {
            Vector2 at = _fillerPending[0];
            int group = _fillerPendingGroups[0];
            _fillerPending.RemoveAt(0);
            _fillerPendingGroups.RemoveAt(0);
            int j = _fillerNext++;
            string id = System.FormattableString.Invariant($"phantomfill-{_cell}-{j}");
            string name = System.FormattableString.Invariant($"PhantomFill{_cell}{j}");
            BasePlayerCharacterEntity entity = SpawnCharacter(mesh, id, name, new Vector3(at.x, 0f, at.y), -1, false);
            if (entity == null)
            {
                Halt();
                return;
            }
            int seed = unchecked(_settings.Seed * 31 + j);
            switch (_fillerMotion)
            {
                case ServerMeshLoadMotionKind.Kinematic:
                    _walks[group].AddWalking(entity, seed);
                    break;
                case ServerMeshLoadMotionKind.Inert:
                    _walks[group].AddStanding(entity, seed);
                    break;
                default:
                    entity.gameObject.AddComponent<SyntheticPlayerBrain>().Setup(entity, _groups[group].Region, seed, _fillerFight);
                    break;
            }
            _filler.Add(entity);
            _fillerGroups.Add(group);
        }

        private Transform PickHost(MeshMapNetworkManager mesh, ServerMeshPhantomSlot slot, double now)
        {
            int offset = (int)(ServerMeshPhantomUploads.Unit(_settings.Seed, 0, HostSalt) * 65536.0);
            if (_settings.View > 0)
            {
                if (_filler.Count == 0)
                    return null;
                for (int n = 0; n < _filler.Count; ++n)
                {
                    BasePlayerCharacterEntity candidate = _filler[(offset + slot.index + n) % _filler.Count];
                    if (candidate != null && !candidate.IsDestroyed)
                        return candidate.EntityTransform;
                }
                return null;
            }
            if (now >= _nextHostRefresh)
                RefreshHosts(mesh, now);
            for (int n = 0; n < _hosts.Count; ++n)
            {
                LiteNetLibIdentity candidate = _hosts[(offset + slot.index + n) % _hosts.Count];
                if (candidate != null && !candidate.IsDestroyed)
                    return candidate.transform;
            }
            if (!_warnedNoHost)
            {
                _warnedNoHost = true;
                _log.Warning($"Phantoms: cell {_cell}: {ServerMeshPhantomSettings.ENV_VIEW}=0 (host mode) found no mover to follow yet; eyes wait for one");
            }
            return null;
        }

        private void RefreshHosts(MeshMapNetworkManager mesh, double now)
        {
            _nextHostRefresh = now + HostRefreshSeconds;
            _hosts.Clear();
            foreach (KeyValuePair<uint, IEntityMovementDataHandler> handler in mesh.EntityMovementDataHandlers)
            {
                if (!mesh.Assets.TryGetSpawnedObject(handler.Key, out LiteNetLibIdentity identity) || identity == null)
                    continue;
                long owner = identity.ConnectionId;
                if (owner >= ServerMeshPhantomTransport.IdBase || _sinkConnections.Contains(owner))
                    continue;
                _hosts.Add(identity);
            }
            _hosts.Sort((a, b) => a.ObjectId.CompareTo(b.ObjectId));
        }

        private void Reply(MeshMapNetworkManager mesh, ServerMeshPhantomSlot slot)
        {
            ServerMeshPhantomBindReply reply = new ServerMeshPhantomBindReply
            {
                objectId = slot.objectId,
                x = slot.spawn.x,
                z = slot.spawn.y,
                kind = (byte)_settings.Kind,
                payload = (byte)_settings.Payload,
                hz = (byte)_settings.UploadHz,
                stand = (byte)(slot.standing ? 1 : 0),
                speed = slot.entity != null ? slot.entity.GetMoveSpeed() : 0f,
                centreX = slot.region.center.x,
                centreZ = slot.region.center.y,
                halfSide = slot.region.width * 0.5f,
            };
            if (slot.ship != null)
                reply = ShipReply(slot.objectId, _settings.UploadHz, _settings.Wire, _settings.Seed, slot.flightPath, _settings.ShipScale, slot.shipPath.Centre, slot.shipPath.MaxSpeed);
            reply.Write(_replyWriter);
            mesh.ServerSendMessage(slot.connectionId, 0, DeliveryMethod.ReliableOrdered, _replyWriter);
        }
    }
}
