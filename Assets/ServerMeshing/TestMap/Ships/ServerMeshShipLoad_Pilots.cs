// Server Meshing test rig — the stand-in ships' load, pilot part (Phase 0 step 7, MESH_LOAD_SHIP_PILOTS=1): one seated
// server pilot per load ship, a player character spawned with a copy of the load generator's recipe (never tracked for
// ghosting, its movement disabled, never exiting). The space game (commit 1a3a58d9) puts its AI pilot on the ship as a
// component instead; this models a seated driver's own kit movement record.
using System.Collections.Generic;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class ServerMeshShipLoad
    {
        /// <summary>SeatFrames is how many frames a pilot may take to appear in its ship's driver seat before it is reported.</summary>
        public const int SeatFrames = 60;

        private readonly List<BasePlayerCharacterEntity> _pilots = new List<BasePlayerCharacterEntity>();
        private readonly List<ServerMeshShipEntity> _pilotShips = new List<ServerMeshShipEntity>();
        private readonly List<int> _seatDeadlines = new List<int>();

        /// <summary>
        /// SeatPilots spawns one pilot per load ship of the cell and seats it in seat 0 (the ship stays server-owned: the
        /// pilot has no connection). Phantom ships never get one: a server driver would make them server-owned and the kit
        /// would drop their phantom's uploads.
        /// </summary>
        private void SeatPilots(string cellId)
        {
            Logging.Log("Mesh", "Ships: pilots seat load ships only; phantom ships get none");
            for (int k = 0; k < _loadShips.Count; ++k)
            {
                ServerMeshShipEntity ship = _loadShips[k];
                if (ship == null || ship.Identity == null || ship.Identity.IsDestroyed)
                    continue;
                BasePlayerCharacterEntity pilot = SpawnServerPlayer(_mesh, $"shippilot-{cellId}-{k}", $"ShipPilot{cellId}{k:000}", ship.transform.position, out string error);
                if (pilot == null)
                {
                    Logging.LogError("Mesh", "Ships error: " + MeshStateSender.CleanForLog(error) + "; no more pilots spawned");
                    return;
                }
                pilot.MovementDisableState.Add(this);
                IEntityMovementComponent movement = pilot.Movement;
                if (!movement.IsNull())
                {
                    movement.StopMove();
                    movement.enabled = false;
                }
                pilot.EnterVehicleAndForget(ship, 0);
                _pilots.Add(pilot);
                _pilotShips.Add(ship);
                _seatDeadlines.Add(Time.frameCount + SeatFrames);
            }
            Logging.Log("Mesh", $"Ships: {_pilots.Count} pilots spawned on cell {cellId}");
        }

        private void SeatingUpdate()
        {
            for (int i = 0; i < _pilots.Count; ++i)
            {
                if (_seatDeadlines[i] <= 0)
                    continue;
                BasePlayerCharacterEntity pilot = _pilots[i];
                ServerMeshShipEntity ship = _pilotShips[i];
                if (pilot != null && ship != null && ship.DriverObjectId == pilot.ObjectId)
                {
                    _seatDeadlines[i] = 0;
                    continue;
                }
                if (Time.frameCount <= _seatDeadlines[i])
                    continue;
                _seatDeadlines[i] = 0;
                Logging.LogError("Mesh", $"Ships error: pilot {(pilot == null ? 0 : pilot.ObjectId)} was not seated on ship {(ship == null ? 0 : ship.ObjectId)} within {SeatFrames} frames");
            }
        }

        private void CountPilots(out int pilots, out int seated)
        {
            pilots = seated = 0;
            for (int i = 0; i < _pilots.Count; ++i)
            {
                BasePlayerCharacterEntity pilot = _pilots[i];
                if (pilot == null || pilot.Identity == null || pilot.Identity.IsDestroyed)
                    continue;
                pilots++;
                ServerMeshShipEntity ship = _pilotShips[i];
                if (ship != null && ship.Identity != null && !ship.Identity.IsDestroyed && ship.DriverObjectId == pilot.ObjectId)
                    seated++;
            }
        }

        private void DestroyPilots()
        {
            for (int i = 0; i < _pilots.Count; ++i)
            {
                BasePlayerCharacterEntity pilot = _pilots[i];
                if (pilot != null && pilot.Identity != null && !pilot.Identity.IsDestroyed)
                    pilot.Identity.NetworkDestroy();
            }
            _pilots.Clear();
            _pilotShips.Clear();
            _seatDeadlines.Clear();
        }

        /// <summary>
        /// SpawnServerPlayer spawns a server-owned player character with new-character data (the load generator's recipe,
        /// restated: that file's spawn is private): the game's first class and entity, level 5, full hp and mp. It is never
        /// tracked for ghosting. Null with the reason when the game has no player class or entity, or no instance comes back.
        /// </summary>
        public static BasePlayerCharacterEntity SpawnServerPlayer(MeshMapNetworkManager mesh, string id, string name, Vector3 position, out string error)
        {
            int classId = 0, entityId = 0;
            foreach (int key in GameInstance.PlayerCharacters.Keys) { classId = key; break; }
            foreach (int key in GameInstance.PlayerCharacterEntities.Keys) { entityId = key; break; }
            if (classId == 0 || entityId == 0)
            {
                error = "no player class or entity in the game database";
                return null;
            }
            PlayerCharacterData data = new PlayerCharacterData();
            data.SetNewPlayerCharacterData(name, classId, entityId, 0);
            data.Id = id;
            data.Level = 5;
            data.CurrentMapName = mesh.OwnMapId;
            data.CurrentPosition = position;
            LiteNetLibIdentity spawnObj = mesh.Assets.GetObjectInstance(GameInstance.GetPlayerCharacterEntityHashAssetId(data.EntityId, out int metaDataId), position, Quaternion.identity);
            if (spawnObj == null)
            {
                error = $"no player instance for {id}";
                return null;
            }
            BasePlayerCharacterEntity entity = spawnObj.GetComponent<BasePlayerCharacterEntity>();
            if (entity == null)
            {
                Object.Destroy(spawnObj.gameObject);
                error = $"the player instance for {id} has no {nameof(BasePlayerCharacterEntity)}";
                return null;
            }
            GameInstance.SetupByMetaData(entity, metaDataId);
            data.CloneTo(entity);
            mesh.Assets.NetworkSpawn(spawnObj);
            entity.ForceMakeCaches();
            entity.FillEmptySlots();
            entity.CurrentHp = entity.MaxHp;
            entity.CurrentMp = entity.MaxMp;
            error = null;
            return entity;
        }
    }
}
