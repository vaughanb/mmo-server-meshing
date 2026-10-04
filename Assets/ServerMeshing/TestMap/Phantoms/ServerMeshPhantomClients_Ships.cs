// Server Meshing add-on — rig phantom binder, ship part (Phase 0 step 7): seeded flight paths, OwnerMirror ship spawns, bind replies and teardown.
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshPhantomClients
    {
        private Vector3[] _shipCentres;
        private ServerMeshShipScale _shipScale;
        private ServerMeshShipSettings _shipSettings;

        /// <summary>
        /// ShipCentres draws the path centre of every ship phantom: phantom i takes path index
        /// ServerMeshShipFlightPath.PhantomIndexBase + i inside area (the whole cell, decision 26) from the one path seed.
        /// False, centres null and an error naming the phantom when the area cannot hold a path.
        /// </summary>
        public static bool ShipCentres(int seed, int phantoms, Rect area, float groundY, ServerMeshShipScale scale, out Vector3[] centres, out string error)
        {
            if (scale == null)
                throw new System.ArgumentNullException(nameof(scale));
            if (phantoms < 1)
                throw new System.ArgumentOutOfRangeException(nameof(phantoms), phantoms, "at least one ship phantom is needed");
            Vector3[] drawn = new Vector3[phantoms];
            for (int i = 0; i < phantoms; ++i)
            {
                if (!ServerMeshShipFlightPath.TryChooseCentre(seed, ServerMeshShipFlightPath.PhantomIndexBase + i, area, groundY, scale, out drawn[i], out string reason))
                {
                    centres = null;
                    error = System.FormattableString.Invariant($"phantom ship {i} has no path: {reason}");
                    return false;
                }
            }
            centres = drawn;
            error = null;
            return true;
        }

        /// <summary>
        /// ShipReply is a ship phantom's bind reply: kind 2, payload the upload wire (0 legacy54, 1 header56), speed the
        /// path's top speed, the spawn point and the centre at the path centre, and the seed, path index and scale a sink
        /// rebuilds the path from.
        /// </summary>
        public static ServerMeshPhantomBindReply ShipReply(uint objectId, int hz, ServerMeshShipWireKind wire, int seed, int flightPath, float scale, Vector3 centre, float maxSpeed)
        {
            return new ServerMeshPhantomBindReply
            {
                objectId = objectId,
                x = centre.x,
                z = centre.z,
                kind = ServerMeshPhantomBindReply.ShipKind,
                payload = (byte)(ServerMeshPhantomSettings.UploadWireOf(wire) == ServerMeshShipWireKind.Legacy54 ? 0 : 1),
                hz = (byte)hz,
                stand = 0,
                speed = maxSpeed,
                centreX = centre.x,
                centreZ = centre.z,
                halfSide = 0f,
                centreY = centre.y,
                seed = seed,
                flightPath = flightPath,
                scale = scale,
            };
        }

        private bool PlanShips(Rect scene, Vector2[] positions, Rect[] regions)
        {
            _shipSettings = ServerMeshShipSettings.TryResolve(System.Environment.GetEnvironmentVariable, out string error);
            if (_shipSettings == null)
            {
                _log.Error(ServerMeshPhantomSettings.Printable($"Phantoms error: cell {_cell}: the ship keys did not resolve: {error}; none bound"));
                return false;
            }
            _shipScale = new ServerMeshShipScale(_settings.ShipScale);
            if (!ShipCentres(_settings.Seed, _settings.Phantoms, scene, ServerMeshShipLoad.GroundY, _shipScale, out _shipCentres, out error))
            {
                _log.Error(ServerMeshPhantomSettings.Printable($"Phantoms error: cell {_cell}: {error}; none bound"));
                return false;
            }
            for (int k = 0; k < _shipCentres.Length; ++k)
            {
                positions[k] = new Vector2(_shipCentres[k].x, _shipCentres[k].z);
                regions[k] = new Rect(positions[k], Vector2.zero);
            }
            _groups = new ServerMeshPhantomGroup[0];
            return true;
        }

        private bool SpawnShip(MeshMapNetworkManager mesh, ServerMeshPhantomSlot slot, double now)
        {
            if (!ServerMeshShipEntity.EnsureRegistered(mesh, out string error))
            {
                _log.Error(ServerMeshPhantomSettings.Printable($"Phantoms error: cell {_cell}: phantom ship {slot.index}: the ship prefab is not registered: {error}"));
                return false;
            }
            ServerMeshShipEntity prefab = ServerMeshShipEntity.LoadPrefab(out error);
            LiteNetLibIdentity prefabIdentity = prefab == null ? null : prefab.GetComponent<LiteNetLibIdentity>();
            if (prefabIdentity == null)
            {
                _log.Error(ServerMeshPhantomSettings.Printable($"Phantoms error: cell {_cell}: phantom ship {slot.index}: no ship prefab: {error}"));
                return false;
            }
            int pathIndex = ServerMeshShipFlightPath.PhantomIndexBase + slot.index;
            Vector3 centre = _shipCentres[slot.index];
            ServerMeshShipFlightPath path = new ServerMeshShipFlightPath(_settings.Seed, pathIndex, centre, _shipScale);
            path.Evaluate(mesh.ServerTimestamp / 1000.0, out Vector3 position, out Quaternion rotation, out _, out _);
            LiteNetLibIdentity identity = mesh.Assets.GetObjectInstance(prefabIdentity.HashAssetId, position, rotation);
            if (identity == null)
            {
                _log.Error(System.FormattableString.Invariant($"Phantoms error: cell {_cell}: phantom ship {slot.index}: no instance of the ship prefab (hash {prefabIdentity.HashAssetId})"));
                return false;
            }
            ServerMeshShipEntity ship = identity.GetComponent<ServerMeshShipEntity>();
            if (ship == null)
            {
                Destroy(identity.gameObject);
                _log.Error($"Phantoms error: cell {_cell}: phantom ship {slot.index}: the ship prefab instance has no ship entity");
                return false;
            }
            ship.Configure(ServerMeshShipAuthority.OwnerMirror, slot.index % 2, pathIndex, centre, _shipScale, _shipSettings, ServerMeshShipClass.Base);
            mesh.Assets.NetworkSpawn(identity, 0, slot.connectionId);
            slot.ship = ship;
            slot.identity = identity;
            slot.flightPath = pathIndex;
            slot.shipPath = path;
            slot.shipSequence = ship.ShipMovement.TeleportSequence;
            slot.nextDue = ServerMeshPhantomUploads.FirstDue(now, 1.0 / _settings.UploadHz, ServerMeshPhantomUploads.Unit(_settings.Seed, slot.index, PhaseSalt));
            return true;
        }

        // A ship's identity outlives its owner's disconnect (VehicleEntity sets DoNotDestroyWhenDisconnect), so the binder
        // destroys it itself while the server still runs; once it stopped, the kit already destroyed every spawned object.
        private static void ReleaseShip(ServerMeshPhantomSlot slot)
        {
            ServerMeshShipEntity ship = slot.ship;
            if (ship == null)
                return;
            LiteNetLibIdentity identity = ship.Identity;
            if (identity != null && !identity.IsDestroyed && identity.IsServer)
                identity.NetworkDestroy();
        }

        private void ReleaseShips()
        {
            for (int k = 0; k < _slots.Count; ++k)
                ReleaseShip(_slots[k]);
            _shipCentres = null;
            _shipScale = null;
            _shipSettings = null;
        }
    }
}
