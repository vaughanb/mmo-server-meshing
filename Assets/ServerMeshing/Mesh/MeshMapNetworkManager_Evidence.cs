// Server Meshing add-on — a cell's evidence for the split gates (Slice 2b, slices/slice2b_design.md decisions 6 and 8),
// sent to the central as MeshLayoutEvidenceMessage (MeshClusterMsg.LayoutEvidence) at the end of every load window
// while the cell's map has an operation in progress or the cell is Pending, and once when a layout is applied during
// one. The e1 gate reads a new child's pre-warm (its band neighbours' hellos and datagrams since, the spawn queue, the
// window's saturation); the drain gate reads what a shrunk parent still owns in the rectangle it gave away, by rectangle
// with the handoff hysteresis, as the most of each count sampled once a second over the window, plus the handoffs
// toward the new owner not yet settled. Window pair semantics: a per-window message carries the pair applied when the
// window started and how long after that apply it started (never negative); the on-apply message carries the new pair
// with a negative offset (the current window started before it), so it never qualifies a gate by itself.
using System.Collections.Generic;
using LiteNetLib;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshDrainWindow keeps the most of each drain count sampled over one window.</summary>
    public sealed class MeshDrainWindow
    {
        private MeshDrainCounts _max;

        public bool HasSample { get; private set; }
        public MeshDrainCounts Max => _max;

        public void Reset()
        {
            _max = default;
            HasSample = false;
        }

        public void Sample(MeshDrainCounts counts)
        {
            _max.characters = Mathf.Max(_max.characters, counts.characters);
            _max.petsAndMounts = Mathf.Max(_max.petsAndMounts, counts.petsAndMounts);
            _max.monsters = Mathf.Max(_max.monsters, counts.monsters);
            _max.staticsAndDrops = Mathf.Max(_max.staticsAndDrops, counts.staticsAndDrops);
            _max.synthetics = Mathf.Max(_max.synthetics, counts.synthetics);
            _max.pendingHandoffs = Mathf.Max(_max.pendingHandoffs, counts.pendingHandoffs);
            HasSample = true;
        }
    }

    /// <summary>MeshSplitEvidence holds the pure rules of the evidence message.</summary>
    public static class MeshSplitEvidence
    {
        /// <summary>
        /// InGivenAway: a position (world frame) still counts as owned inside the rectangle given away when it lies in it
        /// at least <paramref name="hysteresis"/> past the cell's own edge, where the boundary tick would hand it off.
        /// </summary>
        public static bool InGivenAway(MeshRect givenAway, MeshRect owned, float worldX, float worldZ, float hysteresis)
        {
            if (!givenAway.Contains(worldX, worldZ))
                return false;
            return -owned.DistanceToEdge(worldX, worldZ) >= hysteresis;
        }

        /// <summary>
        /// Neighbour describes one band neighbour: hello age (negative without a live hello) and the datagrams received
        /// on its connection since (0 when the count is unknown).
        /// </summary>
        public static MeshNeighbourEvidence Neighbour(string zoneId, bool helloKnown, float helloTime, float now, long packetsAtHello, long packetsNow)
        {
            if (!helloKnown)
                return new MeshNeighbourEvidence { zoneId = zoneId, helloAgeSeconds = -1f, datagramsSinceHello = 0 };
            long since = packetsAtHello >= 0 && packetsNow >= packetsAtHello ? packetsNow - packetsAtHello : 0;
            return new MeshNeighbourEvidence
            {
                zoneId = zoneId,
                helloAgeSeconds = Mathf.Max(0f, now - helloTime),
                datagramsSinceHello = since > int.MaxValue ? int.MaxValue : (int)since,
            };
        }

        /// <summary>ShouldSend: a Dynamic map's cell that knows its cell sends evidence while an operation is in progress or it is Pending.</summary>
        public static bool ShouldSend(bool dynamicMap, bool hasCell, bool opInProgress, MeshCellState ownState)
        {
            return dynamicMap && hasCell && (opInProgress || ownState == MeshCellState.Pending);
        }

        /// <summary>Assemble builds one message; the drain part only when the cell shrank.</summary>
        public static MeshLayoutEvidenceMessage Assemble(string cellId, string incarnation, MeshLayoutPair pair, float windowStart, float pairAppliedAt,
            List<MeshNeighbourEvidence> neighbours, int prewarmQueue, float saturatedShare, float p95Ms, float budgetMs,
            bool shrunk, MeshRect givenAway, MeshDrainCounts drain)
        {
            return new MeshLayoutEvidenceMessage
            {
                cellId = cellId,
                incarnation = incarnation,
                pair = pair,
                windowStartedAfterApplySeconds = windowStart - pairAppliedAt,
                neighbours = neighbours ?? new List<MeshNeighbourEvidence>(),
                prewarmQueue = prewarmQueue,
                saturatedShare = saturatedShare,
                p95Ms = p95Ms,
                budgetMs = budgetMs,
                shrunk = shrunk,
                givenAway = shrunk ? givenAway : MeshRect.Empty,
                drain = shrunk ? drain : default,
            };
        }
    }

    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private readonly MeshDrainWindow _drainWindow = new MeshDrainWindow();
        private readonly Dictionary<Component, float> _ownedSince = new Dictionary<Component, float>();
        private float _evidenceWindowStart;
        private MeshLayoutPair _evidenceWindowPair;
        private float _evidenceWindowPairAppliedAt;
        private MeshLayoutPair _lastApplyPair;
        private float _lastApplyTime;
        private float _nextDrainSample;

        private void EvidenceOnStartServer()
        {
            _drainWindow.Reset();
            _ownedSince.Clear();
            _evidenceWindowStart = Time.unscaledTime;
            _evidenceWindowPair = MeshLayoutPair.None;
            _evidenceWindowPairAppliedAt = Time.unscaledTime;
            _lastApplyPair = MeshLayoutPair.None;
            _lastApplyTime = Time.unscaledTime;
            _nextDrainSample = 0f;
        }

        private bool ShouldSendEvidence => MeshSplitEvidence.ShouldSend(IsDynamicMap, HasOwnZone, _splitOpInProgress, OwnCell != null ? OwnCell.state : MeshCellState.Static);

        private void NoteLayoutApplied(MeshLayoutPair pair, float now)
        {
            _lastApplyPair = pair;
            _lastApplyTime = now;
        }

        /// <summary>TrackOwned's hook (Dynamic maps): when this cell began to own an entity, for the drain gate's monster age clause and a drop's appear time.</summary>
        private void NoteOwnedSince(Component entity)
        {
            if (IsDynamicMap && entity != null && !_ownedSince.ContainsKey(entity))
                _ownedSince[entity] = Time.unscaledTime;
        }

        private float OwnedAge(Component entity, float now) => entity != null && _ownedSince.TryGetValue(entity, out float since) ? now - since : float.MaxValue;

        /// <summary>UntrackOwned's hook: the entity is no longer this cell's (destroyed, demoted, handed on).</summary>
        private void OnOwnedUntracked(Component entity)
        {
            if (entity == null || !IsDynamicMap)
                return;
            _ownedSince.Remove(entity);
            OnStaticUntracked(entity);
        }

        /// <summary>Once a second while the cell drains: the counts, folded into the window's maxima.</summary>
        private void SplitEvidenceTick(float now)
        {
            if (now < _nextDrainSample)
                return;
            _nextDrainSample = now + 1f;
            if (_splitShrunk && ShouldSendEvidence)
                _drainWindow.Sample(CountDrain(now));
        }

        /// <summary>What this cell still owns in the rectangle it gave away, and the handoffs toward its new owner not yet settled.</summary>
        private MeshDrainCounts CountDrain(float now)
        {
            MeshDrainCounts counts = default;
            if (!_splitShrunk || OwnCell == null)
                return counts;
            MeshRect owned = OwnCell.owned;
            float monsterAge = world != null ? world.dynamicSettings.drainMonsterAgeSeconds : 5f;
            List<OwnedEntry<BaseCharacterEntity>> characters = _ownedCharacters.Entries;
            for (int i = 0; i < characters.Count; ++i)
            {
                BaseCharacterEntity entity = characters[i].entity;
                if (entity == null || MeshIdentity.IsGhostEntity(entity) || !InGivenAway(entity.EntityTransform.position, owned))
                    continue;
                if (entity is BasePlayerCharacterEntity player)
                {
                    if (ServerUserHandlers.TryGetPlayerCharacterById(player.Id, out _))
                    {
                        counts.characters++;
                        if (player.PassengingVehicleEntity != null)
                            counts.petsAndMounts++;
                    }
                    else
                        counts.synthetics++;
                }
                else if (entity is BaseMonsterCharacterEntity monster && !monster.IsDead())
                {
                    if (monster.IsSummoned)
                        counts.petsAndMounts++;
                    else if (OwnedAge(monster, now) >= monsterAge)
                        counts.monsters++; // a younger one is a fresh spawn in the given-away rectangle: handed off, not counted
                }
            }
            List<OwnedEntry<DamageableEntity>> statics = _ownedStatics.Entries;
            for (int i = 0; i < statics.Count; ++i)
            {
                if (statics[i].entity is HarvestableEntity h && h != null && InGivenAway(h.EntityTransform.position, owned))
                    counts.staticsAndDrops++;
            }
            List<OwnedEntry<ItemDropEntity>> drops = _ownedDrops.Entries;
            for (int i = 0; i < drops.Count; ++i)
            {
                ItemDropEntity drop = drops[i].entity;
                if (drop != null && InGivenAway(drop.EntityTransform.position, owned))
                    counts.staticsAndDrops++;
            }
            counts.pendingHandoffs = CharacterHandoffsInFlight(_splitDrainOwnerId) + _rolledBackHandoffs.Count + StaticLoansToward(_splitDrainOwnerId);
            return counts;
        }

        private bool InGivenAway(Vector3 scenePos, MeshRect owned)
        {
            Vector3 world = OwnCell.SceneToWorld(scenePos);
            return MeshSplitEvidence.InGivenAway(_splitGivenAway, owned, world.x, world.z, handoffHysteresis);
        }

        /// <summary>LoadTick's hook after each work window: the Prewarm window line and the evidence of the window, then a new evidence window.</summary>
        private void SplitOnWorkWindow(float window)
        {
            if (!IsDynamicMap)
                return;
            bool send = ShouldSendEvidence;
            LogPrewarmWindow(window, send);
            if (send)
            {
                if (_splitShrunk)
                    _drainWindow.Sample(CountDrain(Time.unscaledTime));
                SendLayoutEvidence(onChange: false);
            }
            _drainWindow.Reset();
            _evidenceWindowStart = Time.unscaledTime;
            _evidenceWindowPair = world != null ? world.AppliedPair : MeshLayoutPair.None;
            _evidenceWindowPairAppliedAt = _evidenceWindowPair == _lastApplyPair ? _lastApplyTime : _evidenceWindowStart;
        }

        /// <summary>
        /// Sends the evidence to the central. Per window: the pair applied when the window started. On a change: the pair
        /// just applied, with the (negative) offset of the window under way.
        /// </summary>
        private void SendLayoutEvidence(bool onChange)
        {
            if (!ShouldSendEvidence || ClusterClient == null || !ClusterClient.IsAppRegistered)
                return;
            float now = Time.unscaledTime;
            MeshLayoutPair pair = onChange ? _lastApplyPair : _evidenceWindowPair;
            float appliedAt = onChange ? _lastApplyTime : _evidenceWindowPairAppliedAt;
            MeshDrainCounts drain = _splitShrunk ? (onChange || !_drainWindow.HasSample ? CountDrain(now) : _drainWindow.Max) : default;
            MeshWorkWindow work = _hasWorkWindow ? _lastWorkWindow : default;
            MeshLayoutEvidenceMessage evidence = MeshSplitEvidence.Assemble(OwnZoneId, CellIncarnation, pair, _evidenceWindowStart, appliedAt,
                NeighbourEvidence(now), _puppetQueue.Count, work.saturatedShare, work.workP95Ms, work.budgetMs, _splitShrunk, _splitGivenAway, drain);
            ClusterClient.SendPacket(0, DeliveryMethod.ReliableOrdered, MeshClusterMsg.LayoutEvidence, writer => evidence.Serialize(writer));
        }
#endif
    }
}
