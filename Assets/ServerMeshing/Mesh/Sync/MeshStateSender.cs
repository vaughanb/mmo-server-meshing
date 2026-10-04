// Server Meshing add-on — the state sender (Phase 0 step 6, MESH_SENDER=measure|on): a network-manager component that
// writes every registered state source once per build after the kit's tick and fans the records out to the source's
// subscribers as EntityState packets. Kit characters and monsters join through MeshKitWrapQueue; other sources register
// directly through IMeshStateRegistry. The manager drives it: BeforeKitTick and AfterKitTick from its OnServerUpdate.
using System;
using System.Collections.Generic;
using System.Globalization;
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;
using UnityEngine.Events;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>MeshStateSenderSetting is how the state sender runs: off, measuring after the kit without sending, or sending.</summary>
    public enum MeshStateSenderSetting : byte
    {
        /// <summary>Off adds no sender.</summary>
        Off = 0,

        /// <summary>Measure builds after the kit and counts what it would send; it installs nothing, sends nothing and consumes no flags.</summary>
        Measure = 1,

        /// <summary>On wraps kit handlers, accepts direct sources and sends their records.</summary>
        On = 2,
    }

    /// <summary>
    /// MeshStateSender is the state sender on the map network manager's GameObject. The manager adds it in Awake when
    /// MESH_SENDER is measure or on, sets Setting, and calls BeforeKitTick before and AfterKitTick after every kit tick.
    /// It configures itself on the first BeforeKitTick after a server start, builds at most once per frame, and never
    /// lets an error escape into the kit's tick. The sender stays inactive when the kit sends movement on its grid path.
    /// </summary>
    public class MeshStateSender : BaseGameNetworkManagerComponent, IMeshStateRegistry
    {
        /// <summary>EnvironmentKey turns the sender on: off, measure or on.</summary>
        public const string EnvironmentKey = "MESH_SENDER";
        /// <summary>SourcesKey chooses the sources: kit, direct or kit,direct (the default).</summary>
        public const string SourcesKey = "MESH_SENDER_SOURCES";
        /// <summary>TiersKey sets the distance policy's rates: near/mid/far[/owner] in Hz (default 20/20/20/20).</summary>
        public const string TiersKey = "MESH_SENDER_TIERS";
        /// <summary>R0Key sets the distance policy's radius in metres (default the interest manager's visible range).</summary>
        public const string R0Key = "MESH_SENDER_R0";
        /// <summary>CompactKey chooses the far tier's encoding: off or far.</summary>
        public const string CompactKey = "MESH_SENDER_COMPACT";
        /// <summary>SpreadKey spreads every second source's routine records to the next frame: 0 or 1.</summary>
        public const string SpreadKey = "MESH_SENDER_SPREAD";
        /// <summary>HorizontalKey measures distances on X/Z instead of in 3D: 0 or 1.</summary>
        public const string HorizontalKey = "MESH_SENDER_XZ";
        /// <summary>ClassifyKey sets how many builds pass between two classifications of a pair (1 to 1000, default 4).</summary>
        public const string ClassifyKey = "MESH_SENDER_CLASSIFY_BUILDS";
        /// <summary>PacketBytesKey caps a packet: 1020 (the default), 510 or 255 bytes.</summary>
        public const string PacketBytesKey = "MESH_SENDER_PACKET_BYTES";

        private const byte DefaultHz = 20;
        private const int DefaultClassifyBuilds = 4;
        private const int MaxClassifyBuilds = 1000;

        private readonly IMeshLog _log = new MeshKitLog();
        private readonly MeshStateSenderStats _stats = new MeshStateSenderStats();
        private readonly MeshStateFanoutCounters _windowCounters = new MeshStateFanoutCounters();
        private readonly MeshStateFanoutCounters _idleCounters = new MeshStateFanoutCounters();
        private readonly List<Type> _allowedTypes = new List<Type>();
        private readonly Dictionary<uint, IMeshStateSource> _direct = new Dictionary<uint, IMeshStateSource>();
        private readonly HashSet<string> _refusalsLogged = new HashSet<string>();

        private BaseGameNetworkManager _manager;
        private MeshMovementHandlerSlots _slots;
        private MeshStateFanout _fanout;
        private MeshKitWrapQueue _queue;
        private UnityAction<LiteNetLibIdentity> _onSpawn;
        private UnityAction<LiteNetLibIdentity> _onDestroy;
        private Action<uint> _onLayerRemoved;
        private Action<IMeshStateSource, string> _onDropped;
        private bool _started;
        private bool _configured;
        private bool _sendDirect = true;
        private bool _showPayload;
        private int _tickErrors;
        private int _gen0Baseline = GC.CollectionCount(0);
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private int _lastBuildFrame = -1;
        private long _preKitTicks;
#endif

        /// <summary>Setting is how the sender runs; the manager sets it right after adding the component.</summary>
        public MeshStateSenderSetting Setting { get; set; }

        /// <summary>IsSending is true while the setting is on and the sender is started and configured on a cell off the grid path.</summary>
        public bool IsSending => Setting == MeshStateSenderSetting.On && _started && _configured;

        /// <summary>Awake is empty on purpose: it runs inside AddComponent, before the manager has set Setting.</summary>
        private void Awake()
        {
        }

        /// <summary>
        /// TryParseSetting reads a MESH_SENDER value, ignoring case and surrounding whitespace: null, empty and off give
        /// Off, measure gives Measure, on gives On, each with a null <paramref name="warning"/>. Anything else returns
        /// false with Off and a warning naming the key.
        /// </summary>
        public static bool TryParseSetting(string value, out MeshStateSenderSetting setting, out string warning)
        {
            setting = MeshStateSenderSetting.Off;
            warning = null;
            string trimmed = Trimmed(value);
            if (trimmed.Length == 0 || Is(trimmed, "off"))
                return true;
            if (Is(trimmed, "measure"))
            {
                setting = MeshStateSenderSetting.Measure;
                return true;
            }
            if (Is(trimmed, "on"))
            {
                setting = MeshStateSenderSetting.On;
                return true;
            }
            warning = $"State sender: {EnvironmentKey}={CleanForLog(trimmed)} is not off, measure or on; no state sender is added";
            return false;
        }

        /// <summary>
        /// TryParseSources reads MESH_SENDER_SOURCES: kit, direct, or both comma-separated in either order (case and
        /// whitespace ignored). Null or empty gives both with no warning; anything else returns false with both and a
        /// warning naming the key.
        /// </summary>
        public static bool TryParseSources(string value, out bool kit, out bool direct, out string warning)
        {
            kit = true;
            direct = true;
            warning = null;
            string trimmed = Trimmed(value);
            if (trimmed.Length == 0)
                return true;
            bool sawKit = false, sawDirect = false, valid = true;
            foreach (string part in trimmed.Split(','))
            {
                string token = part.Trim();
                if (Is(token, "kit") && !sawKit)
                    sawKit = true;
                else if (Is(token, "direct") && !sawDirect)
                    sawDirect = true;
                else
                    valid = false;
            }
            if (valid)
            {
                kit = sawKit;
                direct = sawDirect;
                return true;
            }
            warning = $"State sender: {SourcesKey}={CleanForLog(trimmed)} is not kit, direct or kit,direct; sources stay kit,direct";
            return false;
        }

        /// <summary>
        /// TryParseTiers reads MESH_SENDER_TIERS as near/mid/far[/owner] Hz, each one of 0, 1, 2, 5, 10 or 20 (owner
        /// defaults to 20). Null or empty gives 20/20/20/20 with no warning; anything else returns false with
        /// 20/20/20/20 and a warning naming the key.
        /// </summary>
        public static bool TryParseTiers(string value, out byte near, out byte mid, out byte far, out byte owner, out string warning)
        {
            warning = null;
            string trimmed = Trimmed(value);
            if (MeshDistanceSendPolicy.TryParseTiers(trimmed, out near, out mid, out far, out owner, out string error))
                return true;
            near = mid = far = owner = DefaultHz;
            warning = $"State sender: {TiersKey}={CleanForLog(trimmed)} is not near/mid/far[/owner] in Hz ({CleanForLog(error)}); tiers stay 20/20/20/20";
            return false;
        }

        /// <summary>
        /// TryParseR0 reads MESH_SENDER_R0 as a positive, finite distance in metres (invariant culture). Null or empty
        /// gives NaN (use the interest manager's visible range) with no warning; anything else returns false with NaN and
        /// a warning naming the key.
        /// </summary>
        public static bool TryParseR0(string value, out float r0, out string warning)
        {
            r0 = float.NaN;
            warning = null;
            string trimmed = Trimmed(value);
            if (trimmed.Length == 0)
                return true;
            if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) && parsed > 0f && !float.IsInfinity(parsed))
            {
                r0 = parsed;
                return true;
            }
            warning = $"State sender: {R0Key}={CleanForLog(trimmed)} is not a positive distance in metres; r0 stays the visible range";
            return false;
        }

        /// <summary>
        /// TryParseCompact reads MESH_SENDER_COMPACT: off or far (case and whitespace ignored). Null or empty gives off with
        /// no warning; anything else returns false with off and a warning naming the key.
        /// </summary>
        public static bool TryParseCompact(string value, out bool compactFar, out string warning)
        {
            compactFar = false;
            warning = null;
            string trimmed = Trimmed(value);
            if (trimmed.Length == 0 || Is(trimmed, "off"))
                return true;
            if (Is(trimmed, "far"))
            {
                compactFar = true;
                return true;
            }
            warning = $"State sender: {CompactKey}={CleanForLog(trimmed)} is not off or far; compact stays off";
            return false;
        }

        /// <summary>
        /// TryParseSwitch reads a 0|1 sub-key such as MESH_SENDER_SPREAD or MESH_SENDER_XZ. Null or empty gives off with no
        /// warning; anything but 0 or 1 returns false with off and a warning naming <paramref name="key"/>.
        /// </summary>
        public static bool TryParseSwitch(string key, string value, out bool on, out string warning)
        {
            on = false;
            warning = null;
            string trimmed = Trimmed(value);
            if (trimmed.Length == 0 || trimmed == "0")
                return true;
            if (trimmed == "1")
            {
                on = true;
                return true;
            }
            warning = $"State sender: {key}={CleanForLog(trimmed)} is not 0 or 1; it stays 0";
            return false;
        }

        /// <summary>
        /// TryParseClassifyBuilds reads MESH_SENDER_CLASSIFY_BUILDS as a whole number from 1 to 1000. Null or empty gives 4
        /// with no warning; anything else returns false with 4 and a warning naming the key.
        /// </summary>
        public static bool TryParseClassifyBuilds(string value, out int builds, out string warning)
        {
            builds = DefaultClassifyBuilds;
            warning = null;
            string trimmed = Trimmed(value);
            if (trimmed.Length == 0)
                return true;
            if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed >= 1 && parsed <= MaxClassifyBuilds)
            {
                builds = parsed;
                return true;
            }
            warning = $"State sender: {ClassifyKey}={CleanForLog(trimmed)} is not a whole number from 1 to 1000; it stays 4";
            return false;
        }

        /// <summary>
        /// TryParsePacketBytes reads MESH_SENDER_PACKET_BYTES: 1020, 510 or 255. Null or empty gives 1020 with no warning;
        /// anything else returns false with 1020 and a warning naming the key.
        /// </summary>
        public static bool TryParsePacketBytes(string value, out int bytes, out string warning)
        {
            bytes = MeshStatePacketWriter.MaxPacketBytes;
            warning = null;
            string trimmed = Trimmed(value);
            if (trimmed.Length == 0)
                return true;
            if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && (parsed == 1020 || parsed == 510 || parsed == 255))
            {
                bytes = parsed;
                return true;
            }
            warning = $"State sender: {PacketBytesKey}={CleanForLog(trimmed)} is not 1020, 510 or 255; packets stay 1020 B";
            return false;
        }

        /// <summary>
        /// CleanForLog makes <paramref name="text"/> safe for one kit log line: braces become parentheses (the kit's
        /// logger formats every message), line breaks become spaces, and the word Exception (either case) becomes error.
        /// Null gives an empty string.
        /// </summary>
        public static string CleanForLog(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return text.Replace('{', '(').Replace('}', ')').Replace('\r', ' ').Replace('\n', ' ')
                .Replace("Exception", "error").Replace("exception", "error");
        }

        /// <summary>
        /// AllowHandlerType lets the kit adapter wrap handlers of exactly <paramref name="handlerType"/> beyond the default
        /// pair. Types allowed before the sender configures itself are kept and handed to its wrap queue then.
        /// </summary>
        public void AllowHandlerType(Type handlerType)
        {
            if (handlerType == null)
                throw new ArgumentNullException(nameof(handlerType));
            if (!_allowedTypes.Contains(handlerType))
                _allowedTypes.Add(handlerType);
            if (_queue != null)
                _queue.AllowHandlerType(handlerType);
        }

        /// <summary>
        /// Register hands a direct source to the sender. False keeps it on the kit path: the sender is not sending,
        /// MESH_SENDER_SOURCES excludes direct, the source is a kit adapter, or the fan-out refuses it (a duplicate id or
        /// no identity). Each refusal is logged once per source type and reason.
        /// </summary>
        public bool Register(IMeshStateSource source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (!IsSending)
            {
                LogRefusalOnce(source, "the state sender is not sending (off, measuring, stopped or on the grid path)");
                return false;
            }
            if (!_sendDirect)
            {
                LogRefusalOnce(source, SourcesKey + " excludes direct sources");
                return false;
            }
            if (source is MeshKitMovementStateSource)
            {
                LogRefusalOnce(source, "a kit adapter joins through the wrap queue, never directly");
                return false;
            }
            if (!_fanout.Register(source, out string error))
            {
                LogRefusalOnce(source, CleanForLog(error));
                return false;
            }
            _direct[source.ObjectId] = source;
            return true;
        }

        /// <summary>Unregister takes a direct source out of the sender; it does nothing for a source the sender does not hold.</summary>
        public void Unregister(IMeshStateSource source)
        {
            if (source == null)
                return;
            if (_direct.TryGetValue(source.ObjectId, out IMeshStateSource held) && ReferenceEquals(held, source))
                _direct.Remove(source.ObjectId);
            if (_fanout != null)
                _fanout.Unregister(source);
        }

        /// <summary>MarkRelevanceChanged re-classifies one pair on the next build.</summary>
        public void MarkRelevanceChanged(uint objectId, long observerConnectionId)
        {
            if (_fanout != null)
                _fanout.MarkRelevanceChanged(objectId, observerConnectionId);
        }

        /// <summary>MarkSourceChanged re-classifies every pair of one source on the next build.</summary>
        public void MarkSourceChanged(uint objectId)
        {
            if (_fanout != null)
                _fanout.MarkSourceChanged(objectId);
        }

        /// <summary>MarkObserverChanged re-classifies every pair of one observer on the next build.</summary>
        public void MarkObserverChanged(long observerConnectionId)
        {
            if (_fanout != null)
                _fanout.MarkObserverChanged(observerConnectionId);
        }

        /// <summary>
        /// TakeWindowLine returns the Sender window line for the last <paramref name="seconds"/> and starts the next window.
        /// Before configuration (and on an inactive sender) every count is zero. Call it once per probe window, retired
        /// cells included, so windows stay aligned.
        /// </summary>
        public string TakeWindowLine(double seconds)
        {
            if (_fanout != null)
                _fanout.Counters.TakeAndReset(_windowCounters);
            else
                _idleCounters.TakeAndReset(_windowCounters);
            int rewraps = 0, refusals = 0, kitSources = 0;
            if (_queue != null)
            {
                _queue.TakeCounts(out rewraps, out refusals);
                kitSources = _queue.Wrapped;
            }
            int gen0 = GC.CollectionCount(0);
            int gen0Delta = gen0 - _gen0Baseline;
            _gen0Baseline = gen0;
            if (_tickErrors > 1)
                _log.Error(FormattableString.Invariant($"State sender error: {_tickErrors - 1} more tick errors in the window after the first"));
            _tickErrors = 0;
            int clients = (int)Math.Min(int.MaxValue, _windowCounters.Connections);
            return _stats.ToLine(seconds, Setting, kitSources, _direct.Count, clients, rewraps, refusals, _windowCounters,
                System.Diagnostics.Stopwatch.Frequency, gen0Delta, _showPayload);
        }

        /// <summary>
        /// BeforeKitTick runs before the kit's tick. On the first kit tick of a frame only (when a build will follow) it
        /// configures the sender on the first call after a start, flushes a deferred half and drains the wrap queue. It
        /// never throws: an error is logged once per window and abandons the build.
        /// </summary>
        public void BeforeKitTick(int frameIndex)
        {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (!_started)
                return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (frameIndex != _lastBuildFrame)
                {
                    if (!_configured)
                        Configure();
                    if (_fanout.HasDeferred)
                        _fanout.FlushDeferred(frameIndex);
                    _queue.Drain();
                }
            }
            catch (Exception error)
            {
                ReportTickError("pre-kit work", error);
            }
            _preKitTicks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
#endif
        }

        /// <summary>
        /// AfterKitTick runs after the kit's tick returned: it drops wrappers the kit evicted (re-queued for the next
        /// pre-kit work), builds once per frame, and adds the pre-kit and build time to the tick's send sample. It never
        /// throws: an error is logged once per window and abandons the build, so no flag is consumed.
        /// </summary>
        public void AfterKitTick(int frameIndex)
        {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (!_started)
                return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            bool builds = frameIndex != _lastBuildFrame;
            if (_configured)
            {
                _lastBuildFrame = frameIndex;
                try
                {
                    _queue.CheckHeld();
                    _fanout.Build(_manager.ServerTimestamp, frameIndex, _manager.ClientConnectionId);
                }
                catch (Exception error)
                {
                    ReportTickError("build", error);
                }
            }
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start + _preKitTicks;
            _preKitTicks = 0;
            double ms = ticks * 1000d / System.Diagnostics.Stopwatch.Frequency;
            if (builds && _configured)
                _stats.AddBuild(ms);
            MeshWorkProbe.Meter.AddTickWork(ms);
#endif
        }

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>
        /// OnStartServer starts the sender unless the kit sends movement on its grid path (one warning, inactive): it takes
        /// the manager's handler slots and listens for spawns, destroys and removed layers. Configuration waits for the
        /// first pre-kit tick, after the manager has applied its band override.
        /// </summary>
        public override void OnStartServer(BaseGameNetworkManager networkManager)
        {
            StopListening();
            _manager = networkManager;
            _started = false;
            _configured = false;
            _lastBuildFrame = -1;
            _preKitTicks = 0;
            _refusalsLogged.Clear();
            if (networkManager == null)
                return;
            if (!IsGridDisabled)
            {
                _log.Warning("State sender: this cell sends movement on the kit's grid path, which the state sender does not feed; it stays inactive");
                return;
            }
            _slots = MeshMovementHandlerSlots.For(networkManager);
            if (_onSpawn == null)
            {
                _onSpawn = OnObjectSpawn;
                _onDestroy = OnObjectDestroy;
                _onLayerRemoved = OnLayerRemoved;
                _onDropped = OnSourceDropped;
            }
            networkManager.Assets.onObjectSpawn.AddListener(_onSpawn);
            networkManager.Assets.onObjectDestroy.AddListener(_onDestroy);
            _slots.LayerRemoved += _onLayerRemoved;
            _started = true;
        }

        /// <summary>OnStopServer stops listening and drops the fan-out, the wrap queue and every connection; the next start configures afresh.</summary>
        public override void OnStopServer(BaseGameNetworkManager networkManager)
        {
            StopListening();
            if (_queue != null)
                _queue.Clear();
            foreach (IMeshStateSource source in _direct.Values)
                if (source is IMeshSenderOwnedSource owned)
                    owned.OnSenderReleased();
            _direct.Clear();
            _queue = null;
            _fanout = null;
            _started = false;
            _configured = false;
            _lastBuildFrame = -1;
        }

        /// <summary>OnPeerDisconnected frees the connection's packet state and pairs.</summary>
        public override void OnPeerDisconnected(BaseGameNetworkManager networkManager, long connectionId, DisconnectReason reason, System.Net.Sockets.SocketError socketError)
        {
            if (_fanout != null)
                _fanout.ForgetConnection(connectionId);
        }

        private void Update()
        {
            if (!_started || _fanout == null || !_fanout.HasDeferred)
                return;
            int frame = Time.frameCount;
            if (frame == _lastBuildFrame)
                return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _fanout.FlushDeferred(frame);
            }
            catch (Exception error)
            {
                ReportTickError("deferred flush", error);
            }
            MeshWorkProbe.Meter.AddTickWork((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000d / System.Diagnostics.Stopwatch.Frequency);
        }

        private static bool IsGridDisabled => DefaultGridManagerComponent.Instance == null || DefaultGridManagerComponent.Instance.IsDisabled;

        private void Configure()
        {
            bool measure = Setting == MeshStateSenderSetting.Measure;
            WarnIfBad(TryParseSources(Env(SourcesKey), out bool wrapKit, out bool direct, out string warning), warning);
            WarnIfBad(TryParseTiers(Env(TiersKey), out byte near, out byte mid, out byte far, out byte owner, out warning), warning);
            WarnIfBad(TryParseR0(Env(R0Key), out float r0, out warning), warning);
            WarnIfBad(TryParseCompact(Env(CompactKey), out bool compactFar, out warning), warning);
            WarnIfBad(TryParseSwitch(SpreadKey, Env(SpreadKey), out bool spread, out warning), warning);
            WarnIfBad(TryParseSwitch(HorizontalKey, Env(HorizontalKey), out bool horizontal, out warning), warning);
            WarnIfBad(TryParseClassifyBuilds(Env(ClassifyKey), out int classify, out warning), warning);
            WarnIfBad(TryParsePacketBytes(Env(PacketBytesKey), out int packetBytes, out warning), warning);
            if (float.IsNaN(r0))
                r0 = _manager.InterestManager != null && _manager.InterestManager.defaultVisibleRange > 0f ? _manager.InterestManager.defaultVisibleRange : 80f;
            int buildHz = BuildHz();
            MeshStateFanoutSettings settings = new MeshStateFanoutSettings(buildHz, classify, packetBytes, spread, measure, horizontal);
            IMeshSendPolicy policy = GetComponent<IMeshSendPolicy>();
            bool customPolicy = !(policy is UnityEngine.Object policyObject ? policyObject == null : policy == null);
            if (!customPolicy)
                policy = new MeshDistanceSendPolicy(r0, near, mid, far, owner, compactFar ? MeshStateVariant.Compact : MeshStateVariant.Full);
            MeshStateFanout fanout = new MeshStateFanout(new MeshStateSenderTransport(_manager), new MeshStateSenderPositions(_manager), policy, settings, _log, _onDropped);
            MeshKitWrapQueue queue = new MeshKitWrapQueue(_slots, fanout, _log, !measure, IsGridDisabled, wrapKit);
            for (int i = 0; i < _allowedTypes.Count; ++i)
                queue.AllowHandlerType(_allowedTypes[i]);
            _fanout = fanout;
            _queue = queue;
            _sendDirect = direct;
            _showPayload = MeshRecordSizeCounts.TryParseSetting(Env(MeshRecordSizeCounts.EnvironmentKey), out bool showPayload, out _) && showPayload;
            _configured = true;
            string sources = wrapKit && direct ? "kit,direct" : wrapKit ? "kit" : direct ? "direct" : "none";
            string rates = customPolicy
                ? "policy " + policy.GetType().Name
                : FormattableString.Invariant($"tiers {near}/{mid}/{far}/{owner} r0 {r0:0.0} m");
            _log.Info(FormattableString.Invariant($"State sender: {(measure ? "measure" : "on")} | sources {sources} | {rates} {(horizontal ? "xz" : "3d")} | compact {(compactFar ? "far" : "off")} spread {(spread ? 1 : 0)} classify {classify} packet {packetBytes} B build {buildHz} Hz"));
            Dictionary<uint, LiteNetLibIdentity>.Enumerator spawned = _manager.Assets.GetSpawnedObjects();
            while (spawned.MoveNext())
                queue.Enqueue(spawned.Current.Value);
        }

        private int BuildHz()
        {
            LogicUpdater updater = _manager.LogicUpdater;
            if (updater != null && updater.DeltaTime > 0d)
                return (int)Math.Round(1d / updater.DeltaTime);
            return _manager.updateFps;
        }

        private void WarnIfBad(bool valid, string warning)
        {
            if (!valid)
                _log.Warning(warning);
        }

        private static string Env(string key) => Environment.GetEnvironmentVariable(key);

        private void StopListening()
        {
            if (_manager != null && _onSpawn != null && _manager.Assets != null)
            {
                _manager.Assets.onObjectSpawn.RemoveListener(_onSpawn);
                _manager.Assets.onObjectDestroy.RemoveListener(_onDestroy);
            }
            if (_slots != null && _onLayerRemoved != null)
                _slots.LayerRemoved -= _onLayerRemoved;
            _slots = null;
        }

        private void OnObjectSpawn(LiteNetLibIdentity identity)
        {
            if (_queue != null)
                _queue.Enqueue(identity);
        }

        private void OnObjectDestroy(LiteNetLibIdentity identity)
        {
            if (identity == null)
                return;
            uint objectId = identity.ObjectId;
            if (_direct.TryGetValue(objectId, out IMeshStateSource source))
            {
                _direct.Remove(objectId);
                if (_fanout != null)
                    _fanout.Unregister(source);
                if (source is IMeshSenderOwnedSource owned)
                    owned.OnSenderReleased();
            }
            if (_queue != null)
                _queue.Forget(objectId);
        }

        private void OnLayerRemoved(uint objectId)
        {
            if (_queue != null && _manager != null && _manager.Assets.TryGetSpawnedObject(objectId, out LiteNetLibIdentity identity))
                _queue.OnLayerRemoved(identity);
        }

        private void OnSourceDropped(IMeshStateSource source, string reason)
        {
            if (source == null)
                return;
            if (_direct.TryGetValue(source.ObjectId, out IMeshStateSource held) && ReferenceEquals(held, source))
                _direct.Remove(source.ObjectId);
            if (_queue != null)
                _queue.OnDropped(source, reason);
            else if (source is IMeshSenderOwnedSource owned)
                owned.OnSenderReleased();
        }

        private void ReportTickError(string stage, Exception error)
        {
            _tickErrors++;
            if (_tickErrors == 1)
                _log.Error($"State sender error: {stage} failed: {CleanForLog(error.Message)}");
        }
#endif

        private void LogRefusalOnce(IMeshStateSource source, string reason)
        {
            string type = source.GetType().Name;
            if (_refusalsLogged.Add(type + "|" + reason))
                _log.Warning(FormattableString.Invariant($"State sender: object {source.ObjectId} ({type}) stays on the kit path: {reason}"));
        }

        private static string Trimmed(string value) => value == null ? string.Empty : value.Trim();

        private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }
}
