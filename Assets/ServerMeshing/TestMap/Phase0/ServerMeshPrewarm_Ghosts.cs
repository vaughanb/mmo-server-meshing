// Server Meshing test rig — pre-warm and subscription bursts, ghost part (Phase 0 step 8a): steered and kinematic bursts
// arrive as player ghosts through the mesh's own neighbour path, MeshMapNetworkManager.OnGhostSnapshot on a made-up
// connection, each snapshot a GhostSnapshotMessage serialised and read back as the puppet feeder does (and as the link
// receives it, two strings allocated per read). The ghosts join the mesh's player ghosts, so they wake monsters near them.
// A puppet counts as spawned only when the mesh's ghost count rose on its first snapshot (a failed ghost spawn only logs a
// throttled warning there); only spawned puppets are re-fed, first in the frame after their spawn, then at 10 Hz.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class ServerMeshPrewarm
    {
        /// <summary>GhostLevel is the level the ghost puppets claim.</summary>
        public const int GhostLevel = 5;
        /// <summary>GhostHp is the hit points (current and max) the ghost puppets claim.</summary>
        public const int GhostHp = 100;

        private GhostSnapshotMessage _outgoing;
        private GhostSnapshotMessage _incoming;
        private int _entityId;
        private int _dataId;

        private bool PrepareGhosts(out string error)
        {
            if (!_mesh.enableBorderGhosting)
            {
                error = "border ghosting is off on this map server, so no ghost can spawn";
                return false;
            }
            // The game's first player class and entity, as the phantom binder takes them.
            foreach (int id in GameInstance.PlayerCharacters.Keys) { _dataId = id; break; }
            foreach (int id in GameInstance.PlayerCharacterEntities.Keys) { _entityId = id; break; }
            if (_dataId == 0 || _entityId == 0)
            {
                error = "no player class or entity in the game database";
                return false;
            }
            for (int i = 0; i < _count; ++i)
            {
                _puppets.Add(new ServerMeshPrewarmPuppet
                {
                    characterId = $"prewarm-{_mesh.OwnZoneId}-{i}",
                    characterName = $"Prewarm{_mesh.OwnZoneId}{i:000}",
                    point = ServerMeshMonsterPlacer.DiscPoint(_seed, Salt, 0, i, _centre.x, _centre.z, DiscRadiusMetres),
                    yaw = YawOf(i),
                });
            }
            error = null;
            return true;
        }

        private void SpawnGhost(ServerMeshPrewarmPuppet puppet, double now)
        {
            ReadBack(puppet);
            int ghostsBefore = _mesh.GhostCount;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            _mesh.OnGhostSnapshot(PrewarmConnectionId, _incoming);
            _spawnTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            if (_mesh.GhostCount <= ghostsBefore)
                return; // no ghost spawned: not counted in n and never re-fed
            _acted++;
            puppet.spawned = true;
            puppet.due = now; // first re-fed next frame, before the mesh's Update can expire it after a long burst frame
        }

        private void FeedGhosts(double now)
        {
            const double period = 1.0 / GhostFeedHz;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                ServerMeshPrewarmPuppet puppet = _puppets[i];
                if (!puppet.spawned || now < puppet.due)
                    continue;
                puppet.due = now - puppet.due > period ? now + period : puppet.due + period;
                ReadBack(puppet);
                _mesh.OnGhostSnapshot(PrewarmConnectionId, _incoming);
            }
        }

        /// <summary>ReadBack fills the outgoing snapshot of a standing ghost, serialises it and reads it into the incoming one.</summary>
        private void ReadBack(ServerMeshPrewarmPuppet puppet)
        {
            _outgoing.kind = GhostKind.Player;
            _outgoing.characterId = puppet.characterId;
            _outgoing.characterName = puppet.characterName;
            _outgoing.entityId = _entityId;
            _outgoing.dataId = _dataId;
            _outgoing.level = GhostLevel;
            _outgoing.position = puppet.point;
            _outgoing.velocity = Vector3.zero;
            _outgoing.yaw = puppet.yaw;
            _outgoing.movementState = 0;
            _outgoing.currentHp = GhostHp;
            _outgoing.maxHp = GhostHp;
            _writer.Reset();
            _outgoing.Serialize(_writer);
            _reader.SetSource(_writer);
            _incoming.Deserialize(_reader);
        }
    }
}
