// Server Meshing test rig — the bot-side ship check (Phase 0 step 7): every decoded ship record is measured against the
// ship's seeded flight path, and a Bot ships line is logged every 5 s. The space game (commit 1a3a58d9) has no such
// check; the record layouts it reads are ServerMeshShipWire's.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipWatch runs on a bot started with -meshRigShips (the ship installer adds it). A pathed ship's record is
    /// measured as its distance to the path curve over path time [t − 0.25 s, t + 0.05 s] with its along-track lag; a
    /// parked ship's as its distance to its centre; an AI ship's is only counted. Main thread only; the sample lists and
    /// the seen set keep their capacity across windows.
    /// </summary>
    public sealed class ServerMeshShipWatch : MonoBehaviour
    {
        /// <summary>WatchObjectName names the watch's GameObject.</summary>
        public const string WatchObjectName = "[Mesh] ShipWatch";
        /// <summary>WindowSeconds is the Bot ships line's period.</summary>
        public const double WindowSeconds = 5.0;
        /// <summary>BehindSeconds is how far back along the path a record may lag.</summary>
        public const double BehindSeconds = 0.25;
        /// <summary>AheadSeconds is how far ahead along the path a record may lead.</summary>
        public const double AheadSeconds = 0.05;

        private readonly Dictionary<uint, ServerMeshShipFlightPath> _paths = new Dictionary<uint, ServerMeshShipFlightPath>(256);
        private readonly Dictionary<uint, int> _pathIndices = new Dictionary<uint, int>(256);
        private readonly HashSet<uint> _seen = new HashSet<uint>();
        private readonly List<float> _pathMetres = new List<float>(16384);
        private readonly List<float> _lagMs = new List<float>(16384);
        private ServerMeshShipSettings _settings;
        private double _windowStart = double.NaN;
        private long _records, _pathed, _parked, _unpathed, _unknownPath, _badLength;
        private float _parkedMax;

        /// <summary>Instance is the process's watch, or null when -meshRigShips was not given.</summary>
        public static ServerMeshShipWatch Instance { get; private set; }

        /// <summary>Setup gives the watch the settings parsed from -meshRigShips (seed, scale, wire).</summary>
        public void Setup(ServerMeshShipSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>CountBadLength counts a record the reader had too few bytes left for.</summary>
        public void CountBadLength() => _badLength++;

        /// <summary>
        /// Record measures one decoded record of ship <paramref name="objectId"/> written at server time
        /// <paramref name="timestampMs"/> (milliseconds), with the ship's spawn-data flight path index and centre.
        /// </summary>
        public void Record(uint objectId, long timestampMs, in ServerMeshShipSample sample, int flightPathIndex, Vector3 centre)
        {
            if (_settings == null)
                return;
            _records++;
            _seen.Add(objectId);
            if (flightPathIndex == -1)
            {
                _unpathed++;
                return;
            }
            if (flightPathIndex == -2)
            {
                _parked++;
                float distance = Vector3.Distance(sample.Position, centre);
                if (distance > _parkedMax)
                    _parkedMax = distance;
                return;
            }
            ServerMeshShipFlightPath path = PathOf(objectId, flightPathIndex, centre);
            if (path == null)
            {
                _unknownPath++;
                return;
            }
            _pathed++;
            float metres = path.DistanceToPath(sample.Position, timestampMs / 1000.0, BehindSeconds, AheadSeconds, out double lagSeconds);
            _pathMetres.Add(metres);
            _lagMs.Add((float)(lagSeconds * 1000.0));
        }

        private ServerMeshShipFlightPath PathOf(uint objectId, int index, Vector3 centre)
        {
            if (index < 0)
                return null;
            if (_paths.TryGetValue(objectId, out ServerMeshShipFlightPath path) && _pathIndices[objectId] == index && path.Centre == centre)
                return path;
            try
            {
                path = new ServerMeshShipFlightPath(_settings.Seed, index, centre, _settings.Scale);
            }
            catch (ArgumentException ex)
            {
                Logging.LogError("Mesh", "Bot ships error: object " + objectId.ToString(CultureInfo.InvariantCulture) + " path " + index.ToString(CultureInfo.InvariantCulture) + ": " + MultiplayerARPG.MMO.Mesh.Sync.MeshStateSender.CleanForLog(ex.Message));
                return null;
            }
            _paths[objectId] = path;
            _pathIndices[objectId] = index;
            return path;
        }

        private void Update()
        {
            double now = Time.unscaledTimeAsDouble;
            if (double.IsNaN(_windowStart))
            {
                _windowStart = now;
                return;
            }
            if (now - _windowStart < WindowSeconds)
                return;
            Logging.Log("Mesh", WindowLine(now - _windowStart, _seen.Count, _records, _pathed, _parked, _unpathed, _pathMetres, _lagMs, _parkedMax, _unknownPath, _badLength));
            _windowStart = now;
            _seen.Clear();
            _pathMetres.Clear();
            _lagMs.Clear();
            _records = _pathed = _parked = _unpathed = _unknownPath = _badLength = 0;
            _parkedMax = 0f;
        }

        /// <summary>
        /// WindowLine is the Bot ships line: invariant, no braces, zeros when empty, no NaN. It sorts
        /// <paramref name="pathMetres"/> and <paramref name="lagMs"/> in place; the lag p50 is printed in whole milliseconds
        /// and keeps its sign (negative: the record leads its path).
        /// </summary>
        public static string WindowLine(double seconds, int seen, long records, long pathed, long parked, long unpathed,
            List<float> pathMetres, List<float> lagMs, float parkedMax, long unknownPath, long badLength)
        {
            if (pathMetres == null)
                throw new ArgumentNullException(nameof(pathMetres));
            if (lagMs == null)
                throw new ArgumentNullException(nameof(lagMs));
            pathMetres.Sort();
            lagMs.Sort();
            StringBuilder b = new StringBuilder(256);
            b.Append("Bot ships ").Append(Fixed(seconds, "0.00")).Append(" s: seen ").Append(Count(seen));
            b.Append(" records ").Append(Count(records)).Append(" pathed ").Append(Count(pathed));
            b.Append(" parked ").Append(Count(parked)).Append(" unpathed ").Append(Count(unpathed));
            b.Append(" | path n ").Append(pathMetres.Count).Append(" p50 ").Append(Fixed(ServerMeshPhantomWindow.Percentile(pathMetres, 0.5), "0.000"));
            b.Append(" p95 ").Append(Fixed(ServerMeshPhantomWindow.Percentile(pathMetres, 0.95), "0.000"));
            b.Append(" p99 ").Append(Fixed(ServerMeshPhantomWindow.Percentile(pathMetres, 0.99), "0.000"));
            b.Append(" max ").Append(Fixed(pathMetres.Count > 0 ? pathMetres[pathMetres.Count - 1] : 0f, "0.000")).Append(" m");
            float lag = ServerMeshPhantomWindow.Percentile(lagMs, 0.5);
            b.Append(" lag p50 ").Append(float.IsNaN(lag) || float.IsInfinity(lag) ? "0" : ((long)Math.Round(lag)).ToString(CultureInfo.InvariantCulture)).Append(" ms");
            b.Append(" | parked max ").Append(Fixed(parkedMax, "0.0000")).Append(" m");
            b.Append(" | unknownPath ").Append(Count(unknownPath)).Append(" badLength ").Append(Count(badLength));
            return b.ToString();
        }

        private static string Fixed(double value, string format)
        {
            double v = value > 0 && !double.IsInfinity(value) ? value : 0;
            return v.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string Count(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
    }
}
