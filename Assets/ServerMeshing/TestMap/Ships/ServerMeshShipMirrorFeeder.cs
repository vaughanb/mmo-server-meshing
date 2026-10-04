// Server Meshing test rig — the in-process owner of the load's mirror ships (Phase 0 step 7, MESH_LOAD_SHIPS mirror):
// 20 Hz upload bytes from each ship's flight path go through the ship's own ReadClientStateAtServer, the real decode,
// acknowledgement check and transform set of the space game's ShipMovementComponent (commit 1a3a58d9, read there, not
// copied), minus the transport and the kit's message dispatch. Phantom ships carry the real upload path instead.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipMirrorFeeder feeds every load mirror ship once per 1/20 s of unscaled time: its path at server time
    /// becomes an OwnerMirror sample acknowledging the ship's current teleport sequence, written as an upload of the
    /// configured wire (header56 under compact28: uploads are never compact) into one reused writer and read back through
    /// one reused reader. No allocation per feed.
    /// </summary>
    public sealed class ServerMeshShipMirrorFeeder : MonoBehaviour
    {
        /// <summary>FeedHz is the owner upload rate the feeder stands in for.</summary>
        public const float FeedHz = 20f;

        private readonly List<ServerMeshShipEntity> _ships = new List<ServerMeshShipEntity>();
        private readonly List<ServerMeshShipFlightPath> _paths = new List<ServerMeshShipFlightPath>();
        private readonly NetDataWriter _writer = new NetDataWriter(true, 64);
        private readonly NetDataReader _reader = new NetDataReader();
        private MeshMapNetworkManager _mesh;
        private ServerMeshShipWireKind _wire;
        private double _nextFeed;

        /// <summary>Setup gives the feeder its ships and their paths (same order) and the configured wire.</summary>
        public void Setup(MeshMapNetworkManager mesh, List<ServerMeshShipEntity> ships, List<ServerMeshShipFlightPath> paths, ServerMeshShipWireKind wire)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (ships == null)
                throw new ArgumentNullException(nameof(ships));
            if (paths == null)
                throw new ArgumentNullException(nameof(paths));
            if (ships.Count != paths.Count)
                throw new ArgumentException($"{ships.Count} mirror ships but {paths.Count} paths", nameof(paths));
            _mesh = mesh;
            _ships.AddRange(ships);
            _paths.AddRange(paths);
            _wire = wire;
            _nextFeed = Time.unscaledTimeAsDouble;
        }

        private void Update()
        {
            if (_mesh == null || !_mesh.IsServer)
                return;
            double now = Time.unscaledTimeAsDouble;
            if (now < _nextFeed)
                return;
            _nextFeed = now - _nextFeed > 1.0 / FeedHz ? now + 1.0 / FeedHz : _nextFeed + 1.0 / FeedHz;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            long timestamp = _mesh.ServerTimestamp;
            double pathTime = timestamp / 1000.0;
            for (int i = 0; i < _ships.Count; ++i)
            {
                ServerMeshShipEntity ship = _ships[i];
                if (ship == null || ship.Identity == null || ship.Identity.IsDestroyed)
                    continue;
                ServerMeshShipMovement movement = ship.ShipMovement;
                _paths[i].Evaluate(pathTime, out Vector3 position, out Quaternion rotation, out Vector3 velocity, out Vector3 angular);
                ServerMeshShipSample sample = new ServerMeshShipSample
                {
                    Position = position,
                    Rotation = rotation,
                    Velocity = velocity,
                    AngularVelocity = angular,
                    Forward = true,
                    TeleportByte = movement.TeleportSequence,
                    Authority = ServerMeshShipAuthority.OwnerMirror,
                    FlightState = 0,
                    Epoch = movement.Epoch,
                    Compact = false,
                };
                _writer.Reset();
                if (_wire == ServerMeshShipWireKind.Legacy54)
                    ServerMeshShipWire.WriteLegacy54(_writer, in sample);
                else
                    ServerMeshShipWire.WriteHeader56(_writer, in sample);
                _reader.SetSource(_writer);
                movement.ReadClientStateAtServer(timestamp, _reader);
            }
            ServerMeshShipCounters.Current.MirrorMs += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }
}
