// Server Meshing add-on — the drain of a split (Slice 2b, slices/slice2b_design.md decision 9; plan D5). While a layout
// operation is in progress on a Dynamic map, a cell whose owned rectangle shrank hands what stands in the rectangle it
// gave away to its new owner at a paced rate: admission control inside MeshBoundaryTick and MonsterHandoffTick, after
// their own filters (no separate queue). A drain handoff is one whose entity stands in the cell's previous owned
// rectangle and moves under 2 m/s; everything else is a movement crossing and bypasses the pacer. Characters go at
// drainRatePerSecond (the database's sustained capacity) with at most maxConcurrentHandoffsPerDestination in flight,
// nearest past the line first, +-20 % jitter, no re-drain of one character within 2 s; monsters, statics and server-owned
// characters, which do no database I/O, share drainEntityRatePerSecond. Static maps and idle Dynamic maps run none of it.
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshHandoffPacer admits drain handoffs at a rate with jitter and a cap on what is in flight, and remembers who was
    /// drained a moment ago. Pure: the caller passes the clock.
    /// </summary>
    public sealed class MeshHandoffPacer
    {
        /// <summary>DrainSpeedLimit: an entity moving this fast or faster is crossing by itself (plan D5).</summary>
        public const float DrainSpeedLimit = 2f;
        /// <summary>RedrainSeconds: a character drained this recently is not drained again (a rolled-back one waits).</summary>
        public const float RedrainSeconds = 2f;
        /// <summary>Jitter: each interval is the nominal one times 1 +- this.</summary>
        public const float Jitter = 0.2f;

        private readonly System.Random _random;
        private readonly Dictionary<string, float> _lastDrain = new Dictionary<string, float>(System.StringComparer.Ordinal);
        private readonly List<string> _prune = new List<string>();
        private float _nextAt = float.NegativeInfinity;

        public MeshHandoffPacer(int seed) { _random = new System.Random(seed); }

        /// <summary>RatePerSecond is the admission rate; 0 or less admits everything the cap allows.</summary>
        public float RatePerSecond { get; set; }
        /// <summary>MaxConcurrent caps the handoffs in flight toward one destination; 0 or less is no cap.</summary>
        public int MaxConcurrent { get; set; }

        /// <summary>IsDrain: the entity stands in the previous owned rectangle (world frame) and moves under 2 m/s.</summary>
        public static bool IsDrain(MeshRect previousOwned, float worldX, float worldZ, float speed)
        {
            return previousOwned.Contains(worldX, worldZ) && speed < DrainSpeedLimit;
        }

        /// <summary>
        /// Admit returns how many of <paramref name="wanted"/> drain handoffs may start now, with <paramref name="inFlight"/>
        /// already in flight toward the destination. Time not used after an idle spell carries over by at most one interval.
        /// </summary>
        public int Admit(float now, int inFlight, int wanted)
        {
            if (wanted <= 0)
                return 0;
            int cap = MaxConcurrent > 0 ? MaxConcurrent : int.MaxValue;
            int room = cap - Mathf.Max(0, inFlight);
            if (room <= 0)
                return 0;
            if (RatePerSecond <= 0f)
                return Mathf.Min(room, wanted);
            float interval = 1f / RatePerSecond;
            if (_nextAt < now - interval)
                _nextAt = now - interval;
            int admitted = 0;
            while (admitted < wanted && admitted < room && now >= _nextAt)
            {
                admitted++;
                float jitter = 1f + Jitter * (float)(_random.NextDouble() * 2.0 - 1.0);
                _nextAt += interval * jitter;
            }
            return admitted;
        }

        /// <summary>RecentlyDrained is true for an id drained less than RedrainSeconds ago.</summary>
        public bool RecentlyDrained(string id, float now)
        {
            return id != null && _lastDrain.TryGetValue(id, out float at) && now - at < RedrainSeconds;
        }

        public void NoteDrained(string id, float now)
        {
            if (id != null)
                _lastDrain[id] = now;
        }

        /// <summary>Prune forgets the ids drained longer than RedrainSeconds ago.</summary>
        public void Prune(float now)
        {
            if (_lastDrain.Count == 0)
                return;
            _prune.Clear();
            foreach (KeyValuePair<string, float> kv in _lastDrain)
            {
                if (now - kv.Value >= RedrainSeconds)
                    _prune.Add(kv.Key);
            }
            for (int i = 0; i < _prune.Count; ++i)
                _lastDrain.Remove(_prune[i]);
        }

        public void Reset()
        {
            _lastDrain.Clear();
            _nextAt = float.NegativeInfinity;
        }
    }

    /// <summary>MeshSplitState holds the pure rules of a cell's view of a layout operation.</summary>
    public static class MeshSplitState
    {
        /// <summary>
        /// OpInProgress is true while any live cell of the map is Pending or Draining, or simulates more than it owns
        /// (interest differs from owned): a split or merge is under way.
        /// </summary>
        public static bool OpInProgress(IReadOnlyList<MeshCell> cellsOfMap)
        {
            if (cellsOfMap == null)
                return false;
            for (int i = 0; i < cellsOfMap.Count; ++i)
            {
                MeshCell cell = cellsOfMap[i];
                if (cell == null)
                    continue;
                if (cell.state == MeshCellState.Pending || cell.state == MeshCellState.Draining || !cell.interest.Equals(cell.owned))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// TryGivenAway: when <paramref name="after"/> is <paramref name="before"/> cut along one line (it keeps three of its
        /// edges) or empty, <paramref name="given"/> is the rectangle given away and the result is true; any other change
        /// (a growth, a move, a cut that leaves an L) is false with <paramref name="given"/> empty.
        /// </summary>
        public static bool TryGivenAway(MeshRect before, MeshRect after, out MeshRect given, float eps = 1e-3f)
        {
            given = MeshRect.Empty;
            if (before.IsEmpty || before.Equals(after))
                return false;
            if (after.IsEmpty)
            {
                given = before;
                return true;
            }
            if (!before.Encloses(after, eps))
                return false;
            bool sameZ = Near(before.minZ, after.minZ, eps) && Near(before.maxZ, after.maxZ, eps);
            bool sameX = Near(before.minX, after.minX, eps) && Near(before.maxX, after.maxX, eps);
            if (sameZ && Near(before.minX, after.minX, eps))
                given = new MeshRect(after.maxX, before.minZ, before.maxX, before.maxZ);
            else if (sameZ && Near(before.maxX, after.maxX, eps))
                given = new MeshRect(before.minX, before.minZ, after.minX, before.maxZ);
            else if (sameX && Near(before.minZ, after.minZ, eps))
                given = new MeshRect(before.minX, after.maxZ, before.maxX, before.maxZ);
            else if (sameX && Near(before.maxZ, after.maxZ, eps))
                given = new MeshRect(before.minX, before.minZ, before.maxX, after.minZ);
            return !given.IsEmpty;
        }

        private static bool Near(float a, float b, float eps) => Mathf.Abs(a - b) <= eps;
    }

    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private MeshHandoffPacer _playerPacer, _entityPacer;
        private bool _splitOpInProgress;
        private bool _splitShrunk;
        private MeshRect _splitPrevOwned, _splitGivenAway;
        private string _splitDrainOwnerId;
        private MeshRect _splitOwnedBefore;
        private bool _splitHadCell;
        private readonly List<BasePlayerCharacterEntity> _drainPlayers = new List<BasePlayerCharacterEntity>();
        private readonly List<BaseMonsterCharacterEntity> _drainMonsters = new List<BaseMonsterCharacterEntity>();
        private struct PacePose { public Vector3 position; public float time; }
        private readonly Dictionary<string, PacePose> _monsterPacePoses = new Dictionary<string, PacePose>(System.StringComparer.Ordinal);

        /// <summary>IsSplitPacing: a Dynamic map's cell whose owned rectangle shrank, while the operation is in progress.</summary>
        private bool IsSplitPacing => IsDynamicMap && _splitOpInProgress && _splitShrunk;

        /// <summary>HandoffAckGuardSeconds: the registry's dynamicSettings value on a Dynamic map (the central computes the drain timeout from it), else the inspector's.</summary>
        private float HandoffAckGuardSeconds => IsDynamicMap && world != null ? world.dynamicSettings.handoffAckGuardSeconds : handoffAckGuardSeconds;

        private void PacingOnStartServer()
        {
            int seed = (CellIncarnation ?? string.Empty).GetHashCode();
            _playerPacer = new MeshHandoffPacer(seed);
            _entityPacer = new MeshHandoffPacer(seed ^ 0x5bd1e995);
            _splitOpInProgress = false;
            _splitShrunk = false;
            _splitPrevOwned = MeshRect.Empty;
            _splitGivenAway = MeshRect.Empty;
            _splitDrainOwnerId = null;
            _monsterPacePoses.Clear();
        }

        /// <summary>Called by HandleLayoutMessage right before a layout is applied: what this cell owned until now.</summary>
        private void SplitBeforeLayoutApply()
        {
            _splitHadCell = HasOwnZone;
            _splitOwnedBefore = OwnCell != null ? OwnCell.owned : MeshRect.Empty;
        }

        /// <summary>
        /// Called once a layout is applied (Dynamic maps): whether an operation is in progress, whether this cell's owned
        /// rectangle shrank and what it gave away, the pacers' settings, and an evidence message for the change.
        /// </summary>
        private void SplitOnLayoutApplied(MeshLayoutPair pair)
        {
            if (!IsDynamicMap || world == null)
                return;
            float now = Time.unscaledTime;
            NoteLayoutApplied(pair, now);
            if (!HasOwnZone)
                return;
            _splitOpInProgress = MeshSplitState.OpInProgress(world.CellsOf(OwnCell.mapId));
            MeshRect owned = OwnCell.owned;
            if (!_splitOwnedBefore.Equals(owned))
            {
                if (_splitHadCell && MeshSplitState.TryGivenAway(_splitOwnedBefore, owned, out MeshRect given))
                {
                    _splitShrunk = true;
                    _splitPrevOwned = _splitOwnedBefore;
                    _splitGivenAway = given;
                    MeshCell owner = world.CellAtWorld(new Vector3((given.minX + given.maxX) * 0.5f, 0f, (given.minZ + given.maxZ) * 0.5f));
                    _splitDrainOwnerId = owner != null ? owner.id : null;
                    Logging.Log("Mesh", System.FormattableString.Invariant($"Drain on {OwnZoneId}: gave away {Span(given)} to {_splitDrainOwnerId ?? "-"}"));
                }
                else
                {
                    _splitShrunk = false;
                    _splitPrevOwned = MeshRect.Empty;
                    _splitGivenAway = MeshRect.Empty;
                    _splitDrainOwnerId = null;
                }
            }
            if (!_splitOpInProgress)
                _monsterPacePoses.Clear();
            MeshDynamicSettings settings = world.dynamicSettings;
            _playerPacer.RatePerSecond = settings.drainRatePerSecond;
            _playerPacer.MaxConcurrent = settings.maxConcurrentHandoffsPerDestination;
            _entityPacer.RatePerSecond = settings.drainEntityRatePerSecond;
            _entityPacer.MaxConcurrent = 0;
            SendLayoutEvidence(onChange: true);
        }

        /// <summary>The handoffs toward the cell that took the given-away rectangle that have started and are not settled.</summary>
        private int CharacterHandoffsInFlight(string toward)
        {
            int count = _handoffsInProgress.Count;
            foreach (KeyValuePair<string, PendingHandoffAck> kv in _pendingHandoffAcks)
            {
                if (toward == null || string.Equals(kv.Value.zone, toward, System.StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// MeshBoundaryTick's admission control: movement crossings stay in <paramref name="candidates"/>; drain candidates
        /// stay only as far as the player pacer admits them, nearest past the line first.
        /// </summary>
        private void PaceHandoffCandidates(List<BasePlayerCharacterEntity> candidates)
        {
            if (!IsSplitPacing || candidates.Count == 0)
                return;
            float now = Time.unscaledTime;
            _drainPlayers.Clear();
            for (int i = candidates.Count - 1; i >= 0; --i)
            {
                BasePlayerCharacterEntity entity = candidates[i];
                Vector3 world = OwnCell.SceneToWorld(entity.EntityTransform.position);
                float speed = _velocities.TryGetValue(entity.Id, out VelocitySample sample) ? new Vector2(sample.vel.x, sample.vel.z).magnitude : 0f;
                if (!MeshHandoffPacer.IsDrain(_splitPrevOwned, world.x, world.z, speed))
                    continue;
                candidates.RemoveAt(i);
                if (!_playerPacer.RecentlyDrained(entity.Id, now))
                    _drainPlayers.Add(entity);
            }
            int admitted = _playerPacer.Admit(now, CharacterHandoffsInFlight(_splitDrainOwnerId), _drainPlayers.Count);
            for (int k = 0; k < admitted; ++k)
            {
                int best = NearestPastTheLine(_drainPlayers);
                BasePlayerCharacterEntity chosen = _drainPlayers[best];
                _drainPlayers[best] = _drainPlayers[_drainPlayers.Count - 1];
                _drainPlayers.RemoveAt(_drainPlayers.Count - 1);
                _playerPacer.NoteDrained(chosen.Id, now);
                candidates.Add(chosen);
            }
            _drainPlayers.Clear();
            _playerPacer.Prune(now);
        }

        /// <summary>MonsterHandoffTick's admission control, on the entity pacer (monsters do no database I/O).</summary>
        private void PaceMonsterCandidates(List<BaseMonsterCharacterEntity> candidates)
        {
            if (!IsSplitPacing || candidates.Count == 0)
                return;
            float now = Time.unscaledTime;
            _drainMonsters.Clear();
            for (int i = candidates.Count - 1; i >= 0; --i)
            {
                BaseMonsterCharacterEntity monster = candidates[i];
                Vector3 pos = monster.EntityTransform.position;
                Vector3 world = OwnCell.SceneToWorld(pos);
                MeshIdentity id = MeshIdentity.Get(monster);
                string key = id != null ? id.MeshEntityId : null;
                float speed = 0f;
                if (key != null)
                {
                    if (_monsterPacePoses.TryGetValue(key, out PacePose last) && now - last.time > 0.01f && now - last.time < 1f)
                    {
                        Vector3 delta = pos - last.position;
                        speed = new Vector2(delta.x, delta.z).magnitude / (now - last.time);
                    }
                    _monsterPacePoses[key] = new PacePose { position = pos, time = now };
                }
                if (!MeshHandoffPacer.IsDrain(_splitPrevOwned, world.x, world.z, speed))
                    continue;
                candidates.RemoveAt(i);
                _drainMonsters.Add(monster);
            }
            int admitted = _entityPacer.Admit(now, 0, _drainMonsters.Count);
            for (int k = 0; k < admitted; ++k)
            {
                int best = NearestPastTheLine(_drainMonsters);
                candidates.Add(_drainMonsters[best]);
                _drainMonsters[best] = _drainMonsters[_drainMonsters.Count - 1];
                _drainMonsters.RemoveAt(_drainMonsters.Count - 1);
            }
            _drainMonsters.Clear();
        }

        /// <summary>
        /// AdmitEntityDrain says whether an entity that is not a client's character (a static, a drop, a server-owned
        /// character) at a scene position may be handed now: a movement crossing always may, a drain one when the entity
        /// pacer admits it.
        /// </summary>
        private bool AdmitEntityDrain(Vector3 scenePos)
        {
            if (!IsSplitPacing)
                return true;
            Vector3 world = OwnCell.SceneToWorld(scenePos);
            if (!MeshHandoffPacer.IsDrain(_splitPrevOwned, world.x, world.z, 0f))
                return true;
            return _entityPacer.Admit(Time.unscaledTime, 0, 1) > 0;
        }

        /// <summary>The index of the entity nearest past this cell's edge (the smallest depth into the neighbour).</summary>
        private int NearestPastTheLine<T>(List<T> entities) where T : BaseGameEntity
        {
            int best = 0;
            float bestDepth = float.MaxValue;
            for (int i = 0; i < entities.Count; ++i)
            {
                float depth = -OwnCell.DistanceToEdge(entities[i].EntityTransform.position);
                if (depth < bestDepth)
                {
                    bestDepth = depth;
                    best = i;
                }
            }
            return best;
        }
#endif
    }
}
