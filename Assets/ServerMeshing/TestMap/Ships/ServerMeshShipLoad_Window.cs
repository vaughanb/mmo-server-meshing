// Server Meshing test rig — the stand-in ships' load, window part (Phase 0 step 7): samples AI speeds every second and
// logs one Ships window line with every Work window, plus the setting line's formatter. Rig instrumentation; the space
// game (commit 1a3a58d9) has no counterpart.
using System;
using System.Globalization;
using System.Text;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class ServerMeshShipLoad
    {
        /// <summary>SampleIntervalSeconds is how often AI ship speeds are sampled for the window.</summary>
        public const float SampleIntervalSeconds = 1f;

        private int _lastWorkWindow = -1;
        private double _windowStart;
        private int _gcBase;
        private float _nextSample;

        private void StartWindow()
        {
            _lastWorkWindow = _mesh != null ? _mesh.WorkWindowCount : -1;
            _windowStart = Time.unscaledTimeAsDouble;
            _gcBase = GC.CollectionCount(0);
            ServerMeshShipCounters.Current.Reset();
        }

        private void SampleIfDue()
        {
            if (Time.unscaledTime < _nextSample)
                return;
            _nextSample = Time.unscaledTime + SampleIntervalSeconds;
            ServerMeshShipCounters counters = ServerMeshShipCounters.Current;
            for (int i = 0; i < _ships.Count; ++i)
            {
                ServerMeshShipEntity ship = _ships[i];
                if (ship == null || ship.Authority != ServerMeshShipAuthority.ServerAI || ship.ShipCombat == null || ship.ShipCombat.ShipDestroyed)
                    continue;
                float speed = ship.ShipMovement.CurrentVelocity.magnitude;
                counters.Speeds.Add(speed);
                if (ship.ShipPilot != null && ship.ShipPilot.CurrentTask == ServerMeshShipPilotTask.Patrol)
                    counters.PatrolSpeeds.Add(speed);
            }
        }

        private void WindowIfDue()
        {
            int work = _mesh.WorkWindowCount;
            if (work == _lastWorkWindow)
                return;
            _lastWorkWindow = work;
            ServerMeshShipCounters counters = ServerMeshShipCounters.Current;
            Census(counters);
            double now = Time.unscaledTimeAsDouble;
            Logging.Log("Mesh", WindowLine(now - _windowStart, _mesh.OwnZoneId, counters, GC.CollectionCount(0) - _gcBase));
            StartWindow();
        }

        private void Census(ServerMeshShipCounters counters)
        {
            MeshMovementHandlerSlots slots = MeshMovementHandlerSlots.For(_mesh);
            for (int i = _ships.Count - 1; i >= 0; --i)
            {
                ServerMeshShipEntity ship = _ships[i];
                if (ship == null || ship.Identity == null || ship.Identity.IsDestroyed)
                {
                    _ships.RemoveAt(i);
                    continue;
                }
                if (ship.ShipMovement == null || ship.ShipMovement.PuppetHeld)
                    continue; // the puppet feeder's ships: its own window counts them
                counters.Ships++;
                if (ship.ConnectionId >= 0)
                    counters.Phantom++;
                else if (ship.Authority == ServerMeshShipAuthority.ServerAI)
                    counters.Ai++;
                else if (ship.Authority == ServerMeshShipAuthority.Parked)
                    counters.Parked++;
                else
                    counters.Mirror++;
                if (ship.Authority == ServerMeshShipAuthority.ServerAI && ship.ShipPilot != null && ship.ShipPilot.OutsideRectangle)
                    counters.Outside++;
                if (!slots.TryGetKitHandler(ship.ObjectId, out IEntityMovementDataHandler kit) || !ReferenceEquals(kit, ship.ShipMovement))
                    counters.NotInSlot++;
                if (ship.ShipMovement.TeleportPending)
                    counters.Pending++;
            }
            CountPilots(out counters.Pilots, out counters.Seated);
        }

        /// <summary>WireName is the MESH_SHIP_WIRE spelling of <paramref name="wire"/>.</summary>
        public static string WireName(ServerMeshShipWireKind wire)
        {
            switch (wire)
            {
                case ServerMeshShipWireKind.Header56: return "header56";
                case ServerMeshShipWireKind.Compact28: return "compact28";
                default: return "legacy54";
            }
        }

        /// <summary>ScaleText is the scale factor printed invariantly with up to four decimals (1, 0.5, 0.03).</summary>
        public static string ScaleText(ServerMeshShipScale scale)
        {
            return scale == null ? "1" : scale.Factor.ToString("0.####", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// SettingLine is the load's setting line: its cell's planned AI, mirror and parked ships and pilots, the wire, scale,
        /// projectile mode, mirror velocity rule, self-acknowledgement (0 or 1) and AI prefab (enemy, or wave for
        /// <paramref name="waveAi"/>), the area (minX,minZ,maxX,maxZ or none) and the prefab hash.
        /// </summary>
        public static string SettingLine(string cell, int ai, int mirror, int parked, int pilots, ServerMeshShipWireKind wire, ServerMeshShipScale scale,
            bool pooled, bool realMirrorVelocity, bool selfAck, bool waveAi, bool hasArea, Rect area, int prefabHash)
        {
            StringBuilder b = new StringBuilder(224);
            b.Append("Ships: cell ").Append(Printable(cell)).Append(" ai ").Append(Count(ai)).Append(" mirror ").Append(Count(mirror));
            b.Append(" parked ").Append(Count(parked)).Append(" pilots ").Append(Count(pilots));
            b.Append(" | wire ").Append(WireName(wire)).Append(" scale ").Append(ScaleText(scale));
            b.Append(" projectiles ").Append(pooled ? "pooled" : "instantiate").Append(" mirrorVelocity ").Append(realMirrorVelocity ? "real" : "zero");
            b.Append(" selfAck ").Append(selfAck ? "1" : "0").Append(" aiPrefab ").Append(waveAi ? "wave" : "enemy");
            b.Append(" | area ");
            if (hasArea)
                b.Append(Metres(area.xMin)).Append(',').Append(Metres(area.yMin)).Append(',').Append(Metres(area.xMax)).Append(',').Append(Metres(area.yMax));
            else
                b.Append("none");
            b.Append(" | prefab hash ").Append(prefabHash.ToString(CultureInfo.InvariantCulture));
            return b.ToString();
        }

        /// <summary>
        /// WindowLine is the Ships window line: every field always printed, zeros when empty (the patrol and mirror age
        /// percentiles print "none" when they have no sample), rates 0 when the window has no length, no NaN or negatives,
        /// invariant and without braces. It sorts the counters' sample lists in place.
        /// </summary>
        public static string WindowLine(double seconds, string cell, ServerMeshShipCounters c, long gc0)
        {
            if (c == null)
                throw new ArgumentNullException(nameof(c));
            double s = seconds > 0 && !double.IsInfinity(seconds) ? seconds : 0;
            c.Speeds.Sort();
            c.PatrolSpeeds.Sort();
            c.MirrorAges.Sort();
            c.MirrorFrames.Sort();
            StringBuilder b = new StringBuilder(640);
            b.Append("Ships window ").Append(Fixed(s, "0.00")).Append(" s: cell ").Append(Printable(cell));
            b.Append(" ships ").Append(Count(c.Ships)).Append(" ai ").Append(Count(c.Ai)).Append(" mirror ").Append(Count(c.Mirror));
            b.Append(" parked ").Append(Count(c.Parked)).Append(" phantom ").Append(Count(c.Phantom));
            b.Append(" pilots ").Append(Count(c.Pilots)).Append(" seated ").Append(Count(c.Seated));
            b.Append(" outside ").Append(Count(c.Outside)).Append(" notInSlot ").Append(Count(c.NotInSlot)).Append(" pending ").Append(Count(c.Pending));
            b.Append(" | speed p50 ").Append(Fixed(ServerMeshPhantomWindow.Percentile(c.Speeds, 0.5), "0.00"));
            b.Append(" max ").Append(Fixed(c.Speeds.Count > 0 ? c.Speeds[c.Speeds.Count - 1] : 0f, "0.00"));
            b.Append(" patrol n ").Append(Count(c.PatrolSpeeds.Count)).Append(" p50 ");
            b.Append(c.PatrolSpeeds.Count > 0 ? Fixed(ServerMeshPhantomWindow.Percentile(c.PatrolSpeeds, 0.5), "0.00") : "none").Append(" m/s");
            b.Append(" | scans ").Append(Count(c.Scans)).Append(" casts ").Append(Count(c.Casts)).Append(" shots ").Append(Count(c.Shots));
            b.Append(" bolts ").Append(Count(c.Bolts)).Append(" hits ").Append(Count(c.Hits)).Append(" respawns ").Append(Count(c.Respawns));
            b.Append(" | shield writes ").Append(Count(c.ShieldWrites));
            b.Append(" | rpcSends ").Append(Count(c.RpcSends));
            b.Append(" | uploads ").Append(Count(c.Uploads)).Append(" stale ").Append(Count(c.Stale)).Append(" bad ").Append(Count(c.Bad));
            b.Append(" | ms/s pilot ").Append(Fixed(Rate(c.PilotMs, s), "0.00")).Append(" engine ").Append(Fixed(Rate(c.EngineMs, s), "0.00"));
            b.Append(" combat ").Append(Fixed(Rate(c.CombatMs, s), "0.00")).Append(" bolts ").Append(Fixed(Rate(c.BoltMs, s), "0.00"));
            b.Append(" mirror ").Append(Fixed(Rate(c.MirrorMs, s), "0.00"));
            b.Append(" | mirror age n ").Append(Count(c.MirrorAges.Count));
            if (c.MirrorAges.Count > 0)
            {
                b.Append(" p50 ").Append(Fixed(ServerMeshPhantomWindow.Percentile(c.MirrorAges, 0.5), "0.0"));
                b.Append(" ms frames ").Append(Count(ServerMeshPhantomWindow.Percentile(c.MirrorFrames, 0.5)));
            }
            else
            {
                b.Append(" p50 none ms frames none");
            }
            b.Append(" | gc0 ").Append(Count(gc0));

            return b.ToString();
        }

        private static string Printable(string text) => string.IsNullOrEmpty(text) ? "-" : MeshStateSender.CleanForLog(text).Replace(' ', '_');

        private static double Rate(double amount, double seconds) => seconds > 0 ? Positive(amount) / seconds : 0;

        private static double Positive(double value) => value > 0 && !double.IsInfinity(value) ? value : 0;

        private static string Fixed(double value, string format) => Positive(value).ToString(format, CultureInfo.InvariantCulture);

        private static string Metres(float value) => float.IsNaN(value) || float.IsInfinity(value) ? "0" : value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Count(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
    }
}
