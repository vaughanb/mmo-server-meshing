// Server Meshing add-on — a cell's load report (DESIGN.md 0.71): the work probe's window, what the cell owns and holds
// as puppets, and where, sent to the central every loadReportSeconds. Every window is also logged as a "Work window"
// line, and the latest one is shown in the Mesh stats line, after the cost figures and before the per-link breakdown.
// Each Work window line is followed by a "Work ticks" line (MeshTickWindow): the kit's ticks and the frames they fell on.
// With MESH_METER_SENDS set, a "Send window" line (MeshSendWindow) follows them: the window's server sends by type.
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Header("Server Meshing — load reports")]
        [Tooltip("Seconds between load reports to the central (measured work, counts and an occupancy grid), and the length of a work-probe window. 0 = no reports; the probe still takes a window every 5 s for the log.")]
        public float loadReportSeconds = 5f;
        [Tooltip("Bucket edge of the occupancy grid in metres; 0 = a quarter of the ghost band, which lets the central place a cut to within a bucket of the band whatever the map's scale.")]
        public float occupancyBucketSize = 0f;

        /// <summary>The occupancy grid's bucket edge: the configured size, or a quarter of the ghost band.</summary>
        public float OccupancyBucket => occupancyBucketSize > 0f ? occupancyBucketSize : Mathf.Max(1f, ghostBandWidth / 4f);

        /// <summary>Rig toggle: "&lt;metres&gt;" sets both the ghost band and the kit's visible range, so a capacity run can
        /// model another game's ratio of visible range to cell size (a space game's is far smaller than the demo's).</summary>
        public const string ENV_BAND = "MESH_BAND";

        /// <summary>Rig toggle: busy-wait this many milliseconds every server frame, to prove the work probe measures work.
        /// "8" burns on every cell, "C:8" on cell C only (so one cell's figure is not inflated by ten others spinning).</summary>
        public const string ENV_PROBE_BURN_MS = "MESH_PROBE_BURN_MS";

        /// <summary>ENV_WORK_BINS is a rig toggle: "1" follows every Work ticks line with a Work bins line listing the non-empty 0.05 ms bins
        /// of its histograms as index:count, so a run's distributions can be rebuilt offline. Unset = no bins line.</summary>
        public const string ENV_WORK_BINS = "MESH_WORK_BINS";

        private MeshWorkWindow _lastWorkWindow;
        private bool _hasWorkWindow;

        /// <summary>WorkWindowCount is how many probe windows this process has taken (one per Work window, retired cells
        /// included), so rig code can pair its own windows with a Work window exactly instead of by log stamp.</summary>
        public int WorkWindowCount { get; private set; }

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private float _nextLoadReport;
        private float _lastLoadReportTime;
        private float _probeBurnMs;
        private string _probeBurnCell;
        private long _loadClientBytesOut, _loadLinkBytesOut;
        private System.TimeSpan _loadCpuTime;
        private bool _loadCountersPrimed;
        private int _loadGen0Collections;
        private long _sendWireBytes, _sendWirePackets;

        /// <summary>
        /// Stamps every kit tick for the work meter: the state sync, the interest manager and the movement send all run
        /// inside it. The end stamp is taken the moment the kit returns, so work added after the tick is not in its parts.
        /// </summary>
        protected override void OnServerUpdate(LogicUpdater updater)
        {
            PoseKinematicPuppets(); // right before the kit sends movement, so watched kinematic puppets go out current
            MeshWorkMeter meter = MeshWorkProbe.Meter;
            meter.BeginTick(System.Diagnostics.Stopwatch.GetTimestamp());
            try
            {
                base.OnServerUpdate(updater);
            }
            finally
            {
                meter.EndTick(System.Diagnostics.Stopwatch.GetTimestamp());
            }
        }

        /// <summary>The kit's tick rate as its updater runs it, or updateFps before the updater exists.</summary>
        private int TickRate
        {
            get
            {
                LogicUpdater updater = LogicUpdater;
                if (updater != null && updater.DeltaTime > 0d)
                    return (int)System.Math.Round(1d / updater.DeltaTime);
                return updateFps;
            }
        }

        /// <summary>Applies MESH_BAND before the band sizing check runs: the band and the visible range move together (Q1).</summary>
        private void ApplyBandOverride()
        {
            string value = System.Environment.GetEnvironmentVariable(ENV_BAND);
            if (string.IsNullOrEmpty(value))
                return;
            if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float band) || band <= 0f)
            {
                Logging.LogWarning("Mesh", $"{ENV_BAND}={value} is not a positive distance; ignored");
                return;
            }
            ghostBandWidth = band;
            if (InterestManager != null)
                InterestManager.defaultVisibleRange = band;
            Logging.Log("Mesh", $"{ENV_BAND}={value}: ghost band and visible range set to {band:0.#} m");
        }

        private void LoadOnStartServer()
        {
            _ = MeshWorkProbe.Meter; // hooks the player loop now, so the first report has a full window
            _nextLoadReport = Time.unscaledTime + Mathf.Max(1f, loadReportSeconds);
            _lastLoadReportTime = Time.unscaledTime;
            _loadCountersPrimed = false;
            _loadGen0Collections = System.GC.CollectionCount(0);
            _hasWorkWindow = false;
            _probeOverdueLogged = false;
            _probeBurnMs = 0f;
            _probeBurnCell = null;
            string burn = System.Environment.GetEnvironmentVariable(ENV_PROBE_BURN_MS);
            if (!string.IsNullOrEmpty(burn))
            {
                int colon = burn.IndexOf(':');
                string msText = colon >= 0 ? burn.Substring(colon + 1) : burn;
                if (float.TryParse(msText.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ms) && ms > 0f)
                {
                    _probeBurnMs = ms;
                    _probeBurnCell = colon >= 0 ? burn.Substring(0, colon).Trim() : null;
                    Logging.Log("Mesh", $"{ENV_PROBE_BURN_MS}={burn}: burning {ms:0.##} ms of main-thread work every frame on {(_probeBurnCell ?? "every cell")}");
                }
                else
                    Logging.LogWarning("Mesh", $"{ENV_PROBE_BURN_MS}={burn} is not [cell:]milliseconds; ignored");
            }
            bool logBins = System.Environment.GetEnvironmentVariable(ENV_WORK_BINS) == "1";
            MeshWorkProbe.Meter.LogBins = logBins;
            if (logBins)
                Logging.Log("Mesh", $"{ENV_WORK_BINS}=1: every Work ticks line is followed by a Work bins line (non-empty 0.05 ms bins as index:count)");
            if (_sendMeter != null)
                ReadSendWire(out _sendWireBytes, out _sendWirePackets); // the kit built a new NetManager, so its counters restarted
        }

        private float ProbeWindowSeconds => loadReportSeconds > 0f ? loadReportSeconds : 5f;

        private void LoadTick()
        {
            if (_probeBurnMs > 0f && (_probeBurnCell == null || string.Equals(_probeBurnCell, OwnZoneId, System.StringComparison.OrdinalIgnoreCase)))
            {
                long until = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(_probeBurnMs / 1000f * System.Diagnostics.Stopwatch.Frequency);
                while (System.Diagnostics.Stopwatch.GetTimestamp() < until) { }
            }
            if (Time.unscaledTime < _nextLoadReport)
                return;
            float now = Time.unscaledTime;
            float window = Mathf.Max(0.001f, now - _lastLoadReportTime);
            _nextLoadReport = now + ProbeWindowSeconds;
            _lastLoadReportTime = now;
            MeshProbeState before = _hasWorkWindow ? _lastWorkWindow.state : MeshProbeState.Unknown;
            _lastWorkWindow = MeshWorkProbe.Meter.TakeWindow(MeshWorkProbe.BudgetMs, window);
            MeshTickWindow ticks = MeshWorkProbe.Meter.TakeTickWindow(window, TickRate, MeshWorkProbe.EffectiveBudgetMs);
            int gen0 = System.GC.CollectionCount(0);
            ticks.gen0Collections = gen0 - _loadGen0Collections;
            _loadGen0Collections = gen0;
            _hasWorkWindow = true;
            WorkWindowCount++;
            MeshSendWindow sends = TakeSendWindow(window, out long wireBytes, out long wirePackets);
            ReportProbeTransitions(before, _lastWorkWindow);
            if (!HasOwnZone || (OwnCell.owned.IsEmpty && OwnCell.interest.IsEmpty))
                return; // a retired cell measures nothing
            MeshLoadReportMessage report = BuildLoadReport(window);
            Logging.Log("Mesh", WorkWindowLine(report));
            Logging.Log("Mesh", ticks.ToLine());
            if (ticks.bins != null)
                Logging.Log("Mesh", ticks.bins);
            if (sends != null)
                Logging.Log("Mesh", sends.ToLine(wireBytes, wirePackets));
            if (loadReportSeconds > 0f && ClusterClient != null && ClusterClient.IsAppRegistered)
                ClusterClient.SendPacket(0, DeliveryMethod.ReliableOrdered, MeshClusterMsg.LoadReport, writer => report.Serialize(writer));
        }

        /// <summary>
        /// TakeSendWindow takes the send meter's window and the LiteNetLib server's sent-bytes and sent-datagram deltas
        /// over it, advancing both baselines together so the next window starts where this one ended. Null when the
        /// meter is off, which leaves every counter untouched.
        /// </summary>
        private MeshSendWindow TakeSendWindow(float window, out long wireBytes, out long wirePackets)
        {
            wireBytes = 0;
            wirePackets = 0;
            if (_sendMeter == null)
                return null;
            ReadSendWire(out long bytes, out long packets);
            wireBytes = bytes - _sendWireBytes;
            wirePackets = packets - _sendWirePackets;
            _sendWireBytes = bytes;
            _sendWirePackets = packets;
            return _sendMeter.TakeWindow(window);
        }

        private void ReadSendWire(out long bytes, out long packets)
        {
            LiteNetLibTransport transport = MeshTransportUnwrap.Innermost<LiteNetLibTransport>(ServerTransport);
            if (transport != null && transport.Server != null && transport.Server.Statistics != null)
            {
                bytes = transport.Server.Statistics.BytesSent;
                packets = transport.Server.Statistics.PacketsSent;
                return;
            }
            bytes = 0;
            packets = 0;
        }

        /// <summary>
        /// WorkWindowLine is every probe window in the log, with what the cell held during it: the Mesh stats line only
        /// shows the latest window when it happens to be written, which is every other one. Invariant culture, because
        /// the rig's tools parse it.
        /// </summary>
        private static string WorkWindowLine(MeshLoadReportMessage report)
        {
            MeshWorkWindow w = report.work;
            return System.FormattableString.Invariant(
                $"Work window {report.windowSeconds:0.00} s: frames {w.frames} work mean {w.workMeanMs:0.000} ms p95 {w.workP95Ms:0.00} ms max {w.workMaxMs:0.0} ms wait {w.waitMeanMs:0.000} ms period {w.periodMeanMs:0.000} ms budget {w.budgetMs:0.00} ms sat {w.saturatedShare * 100f:0.0}% probe={w.state} | owned players {report.ownedPlayers} monsters {report.ownedMonsters} | puppets players {report.puppetPlayers} monsters {report.puppetMonsters} | clients {report.clients} cpu {report.cpuPercent:0}%");
        }

        private bool _probeOverdueLogged;

        /// <summary>A probe that turns Invalid, or cannot prove itself, is an error the operator must see, not a token in a stats line.</summary>
        private void ReportProbeTransitions(MeshProbeState before, MeshWorkWindow window)
        {
            if (window.state == MeshProbeState.Invalid && before != MeshProbeState.Invalid)
                Logging.LogError("Mesh", $"Work probe INVALID: its stamps do not fire every frame (window: {window.frames} frames, work {window.workMeanMs:0.00} ms, wait {window.waitMeanMs:0.00} ms, period {window.periodMeanMs:0.00} ms); this cell's load figures cannot be trusted and it takes no part in automatic splits");
            else if (window.state == MeshProbeState.Valid && before != MeshProbeState.Valid)
                Logging.Log("Mesh", $"Work probe valid: frames wait inside the bracket (work {window.workMeanMs:0.00} ms, wait {window.waitMeanMs:0.00} ms of a {window.budgetMs:0.00} ms budget)");
            if (MeshWorkProbe.Meter.ProofOverdue && !_probeOverdueLogged && window.state != MeshProbeState.Valid)
            {
                _probeOverdueLogged = true;
                Logging.LogWarning("Mesh", $"Work probe unproven after {MeshWorkMeter.ProveWaitWithinSeconds:0} s: no window in which most frames waited (saturated share {window.saturatedShare * 100f:0}%). Either this cell has been saturated since it started or the frame limiter waits outside the probe; its load figures are not used until a window proves it");
            }
        }

        private MeshLoadReportMessage BuildLoadReport(float window)
        {
            MeshCell cell = OwnCell;
            MeshRect area = cell.owned.IsEmpty ? cell.interest : cell.owned; // a draining cell owns nothing but still holds R
            MeshLayoutPair applied = world != null ? world.AppliedPair : MeshLayoutPair.None;
            MeshLoadReportMessage report = new MeshLoadReportMessage
            {
                cellId = OwnZoneId,
                mapId = cell.mapId,
                incarnation = CellIncarnation,
                layoutIncarnation = applied.coordinatorIncarnation,
                layoutVersion = applied.version,
                windowSeconds = window,
                work = _lastWorkWindow,
                grid = MeshOccupancyGrid.Covering(area.minX - ghostBandWidth, area.minZ - ghostBandWidth,
                    area.maxX + ghostBandWidth, area.maxZ + ghostBandWidth, OccupancyBucket),
                ownedDrops = _ownedDrops.Count,
                ownedStatics = _ownedStatics.Count,
                pendingAcks = _pendingHandoffAcks.Count,
                reserved = _reservedArrivals.Count,
            };
            foreach (IPlayerCharacterData _ in ServerUserHandlers.GetPlayerCharacters())
                report.registeredPlayers++;
            List<OwnedEntry<BaseCharacterEntity>> owned = _ownedCharacters.Entries;
            for (int i = 0; i < owned.Count; ++i)
            {
                BaseCharacterEntity entity = owned[i].entity;
                if (entity == null)
                    continue;
                Vector3 world = cell.SceneToWorld(entity.EntityTransform.position);
                if (entity is BasePlayerCharacterEntity)
                {
                    report.ownedPlayers++;
                    report.grid.Add(MeshOccupancyLayer.OwnedPlayers, world.x, world.z);
                }
                else
                {
                    report.ownedMonsters++;
                    report.grid.Add(MeshOccupancyLayer.OwnedMonsters, world.x, world.z);
                }
                if (-cell.DistanceToEdge(entity.EntityTransform.position) >= handoffHysteresis)
                    report.ownedOutside++;
            }
            foreach (MeshGhost ghost in _ghosts.Values)
            {
                if (ghost.entity == null)
                    continue;
                if (ghost.kind == GhostKind.Player) report.puppetPlayers++; else report.puppetMonsters++;
                Vector3 world = cell.SceneToWorld(ghost.entity.EntityTransform.position);
                report.grid.Add(MeshOccupancyLayer.Puppets, world.x, world.z);
            }
            foreach (GhostStatic ghost in _ghostStatics.Values)
            {
                if (ghost.kind == GhostKind.ItemDrop) report.puppetDrops++; else report.puppetStatics++;
            }
            foreach (IPlayerCharacterData data in ServerUserHandlers.GetPlayerCharacters())
            {
                if (data is BasePlayerCharacterEntity player && player != null)
                {
                    Vector3 world = cell.SceneToWorld(player.EntityTransform.position);
                    report.grid.Add(MeshOccupancyLayer.Clients, world.x, world.z);
                }
            }
            FillLoadRates(report, window);
            return report;
        }

        private void FillLoadRates(MeshLoadReportMessage report, float window)
        {
            long clientBytesOut = 0;
            LiteNetLibTransport transport = MeshTransportUnwrap.Innermost<LiteNetLibTransport>(ServerTransport);
            if (transport != null && transport.Server != null && transport.Server.Statistics != null)
            {
                clientBytesOut = transport.Server.Statistics.BytesSent;
                report.clients = transport.Server.ConnectedPeersCount;
            }
            long linkBytesOut = 0;
            foreach (MeshLinkClient link in _linkClients.Values)
            {
                NetStatistics st = link.Statistics;
                if (st != null)
                    linkBytesOut += st.BytesSent;
            }
            System.TimeSpan cpu;
            using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
                cpu = process.TotalProcessorTime;
            if (_loadCountersPrimed)
            {
                report.clientOutKBps = (clientBytesOut - _loadClientBytesOut) / 1024f / window;
                report.linkOutKBps = (linkBytesOut - _loadLinkBytesOut) / 1024f / window;
                report.cpuPercent = (float)(cpu - _loadCpuTime).TotalSeconds / window * 100f;
            }
            _loadClientBytesOut = clientBytesOut;
            _loadLinkBytesOut = linkBytesOut;
            _loadCpuTime = cpu;
            _loadCountersPrimed = true;
        }
#else
        private void ApplyBandOverride() { }
        private void LoadOnStartServer() { }
        private void LoadTick() { }
#endif

        /// <summary>The work probe's last window, appended to the Mesh stats line (after everything the load tools parse).</summary>
        private string LoadStatsSuffix()
        {
            if (!_hasWorkWindow)
                return " | work none";
            MeshWorkWindow w = _lastWorkWindow;
            return $" | work mean {w.workMeanMs:0.00} ms p95 {w.workP95Ms:0.00} ms max {w.workMaxMs:0.0} ms wait {w.waitMeanMs:0.00} ms util {w.Utilisation * 100f:0}% sat {w.saturatedShare * 100f:0}% probe={w.state}";
        }
    }
}
