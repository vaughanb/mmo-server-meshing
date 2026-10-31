using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using Insthync.DevExtension;

namespace MultiplayerARPG.MMO
{
    public sealed class DatabaseCharacterSaveScheduler
    {
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        private sealed class PendingCharacterSave
        {
            public string CharacterId;
            public UpdateCharacterReq Request;
            public int LaneIndex;
            public int Version;
            public bool IsQueued;
            public bool IsSaving;
            public double LastSavedAt;
            public double NextEligibleSaveAt;
        }

        private readonly object _lock = new object();
        private readonly DatabaseNetworkManager _owner;
        private readonly Queue<string>[] _lanes;
        private readonly Dictionary<string, PendingCharacterSave> _pending = new Dictionary<string, PendingCharacterSave>();

        private readonly int _tickMilliseconds;
        private readonly int _maxSavesPerTick;
        private readonly float _minSaveIntervalSeconds;
        private readonly float _retryDelaySeconds;

        private bool _running;
        private int _nextLaneIndex;

        public DatabaseCharacterSaveScheduler(
            DatabaseNetworkManager owner,
            int laneCount,
            int tickMilliseconds,
            int maxSavesPerTick,
            float minSaveIntervalSeconds,
            float retryDelaySeconds)
        {
            _owner = owner;
            _tickMilliseconds = Math.Max(50, tickMilliseconds);
            _maxSavesPerTick = Math.Max(1, maxSavesPerTick);
            _minSaveIntervalSeconds = Math.Max(1f, minSaveIntervalSeconds);
            _retryDelaySeconds = Math.Max(1f, retryDelaySeconds);

            laneCount = Math.Max(1, laneCount);
            _lanes = new Queue<string>[laneCount];
            for (int i = 0; i < laneCount; ++i)
                _lanes[i] = new Queue<string>();
        }

        public int PendingCount
        {
            get
            {
                lock (_lock)
                {
                    // Entries kept only for their last save time are not waiting
                    int count = 0;
                    foreach (PendingCharacterSave pending in _pending.Values)
                    {
                        if (pending.IsSaving || pending.Request.CharacterData != null)
                            count++;
                    }
                    return count;
                }
            }
        }

        public void Start()
        {
            if (_running)
                return;

            _running = true;
            RunLoop().Forget();
        }

        public void Stop()
        {
            _running = false;
        }

        public void Enqueue(UpdateCharacterReq request, bool forceImmediate = false)
        {
            string characterId = request.CharacterData.Id;
            if (string.IsNullOrEmpty(characterId))
                return;

            double now = GetUtcSeconds();

            lock (_lock)
            {
                if (!_pending.TryGetValue(characterId, out PendingCharacterSave pending))
                {
                    pending = new PendingCharacterSave()
                    {
                        CharacterId = characterId,
                        LaneIndex = GetLaneIndex(characterId),
                        LastSavedAt = 0d,
                        NextEligibleSaveAt = now,
                        Version = 0,
                        IsQueued = false,
                        IsSaving = false,
                    };
                    _pending.Add(characterId, pending);
                }

                // A waiting save must keep its flags, or tables only it marked are never written
                if (pending.Request.CharacterData != null)
                    request = MergeRequests(pending.Request, request);
                pending.Request = request;
                pending.Version++;

                if (forceImmediate)
                {
                    pending.NextEligibleSaveAt = now;
                }
                else
                {
                    double nextAllowed = pending.LastSavedAt + _minSaveIntervalSeconds;
                    pending.NextEligibleSaveAt = nextAllowed > now ? nextAllowed : now;
                }

                if (!pending.IsQueued && !pending.IsSaving)
                {
                    pending.IsQueued = true;
                    _lanes[pending.LaneIndex].Enqueue(characterId);
                }
            }
        }

        public async UniTask CancelAsync(string characterId)
        {
            while (true)
            {
                lock (_lock)
                {
                    if (!_pending.TryGetValue(characterId, out PendingCharacterSave pending))
                        return;

                    if (!pending.IsSaving)
                    {
                        _pending.Remove(characterId);
                        return;
                    }
                }
                // Let a save already being written finish first, so it cannot land after the caller's delete
                await UniTask.Yield();
            }
        }

        public async UniTask FlushAllAsync()
        {
            while (true)
            {
                string[] ids;

                lock (_lock)
                {
                    if (_pending.Count == 0)
                        break;

                    ids = new string[_pending.Count];
                    int index = 0;
                    foreach (string id in _pending.Keys)
                        ids[index++] = id;
                }

                bool savedAny = false;
                for (int i = 0; i < ids.Length; ++i)
                {
                    if (await SaveCharacterNow(ids[i], true))
                        savedAny = true;
                }

                if (!savedAny)
                    break;
            }
        }

        private async UniTaskVoid RunLoop()
        {
            while (_running)
            {
                try
                {
                    await UniTask.Delay(_tickMilliseconds);
                    await ProcessNextLane();
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[{nameof(DatabaseCharacterSaveScheduler)}] {ex}");
                }
            }
        }

        private async UniTask ProcessNextLane()
        {
            Queue<string> lane;

            lock (_lock)
            {
                lane = _lanes[_nextLaneIndex];
                _nextLaneIndex = (_nextLaneIndex + 1) % _lanes.Length;
                if (_nextLaneIndex == 0)
                    RemoveIdleEntries(GetUtcSeconds());
            }

            // Each id queued now is looked at once at most, and only saves that are due count against the budget,
            // so ids waiting out their interval neither spin round the lane nor hold up the saves behind them
            int toInspect;
            lock (_lock)
            {
                toInspect = lane.Count;
            }
            int processed = 0;
            while (processed < _maxSavesPerTick && toInspect > 0)
            {
                string characterId;

                lock (_lock)
                {
                    if (lane.Count == 0)
                        break;

                    characterId = lane.Dequeue();
                    toInspect--;
                    if (_pending.TryGetValue(characterId, out PendingCharacterSave dequeued))
                    {
                        if (!dequeued.IsSaving && dequeued.Request.CharacterData != null && GetUtcSeconds() < dequeued.NextEligibleSaveAt)
                        {
                            // Not due yet: back to the end of the lane, still queued
                            lane.Enqueue(characterId);
                            continue;
                        }
                        // Off the lane now, so a save that is not due yet is queued again
                        dequeued.IsQueued = false;
                    }
                }

                await SaveCharacterNow(characterId, false);
                processed++;
            }
        }

        private async UniTask<bool> SaveCharacterNow(string characterId, bool ignoreInterval)
        {
            PendingCharacterSave pending;
            UpdateCharacterReq request;
            int version;
            double now = GetUtcSeconds();

            lock (_lock)
            {
                if (!_pending.TryGetValue(characterId, out pending))
                    return false;

                if (pending.IsSaving || pending.Request.CharacterData == null)
                    return false;

                if (!ignoreInterval && now < pending.NextEligibleSaveAt)
                {
                    if (!pending.IsQueued)
                    {
                        pending.IsQueued = true;
                        _lanes[pending.LaneIndex].Enqueue(characterId);
                    }
                    return false;
                }

                pending.IsSaving = true;
                pending.IsQueued = false;
                request = pending.Request;
                version = pending.Version;
                // Requests arriving during the save collect only the flags changed after it
                pending.Request = default;
            }

            bool success = await _owner.InternalPersistCharacterUpdate(request);

            lock (_lock)
            {
                if (!_pending.TryGetValue(characterId, out pending))
                    return success;

                pending.IsSaving = false;

                if (!success)
                {
                    // Put the unsaved flags back so the retry still writes them
                    pending.Request = pending.Request.CharacterData != null
                        ? MergeRequests(request, pending.Request)
                        : request;
                    pending.NextEligibleSaveAt = GetUtcSeconds() + _retryDelaySeconds;
                    if (!pending.IsQueued)
                    {
                        pending.IsQueued = true;
                        _lanes[pending.LaneIndex].Enqueue(characterId);
                    }
                    return false;
                }

                pending.LastSavedAt = GetUtcSeconds();

                if (pending.Version != version)
                {
                    pending.NextEligibleSaveAt = ignoreInterval
                        ? pending.LastSavedAt
                        : pending.LastSavedAt + _minSaveIntervalSeconds;

                    if (!pending.IsQueued)
                    {
                        pending.IsQueued = true;
                        _lanes[pending.LaneIndex].Enqueue(characterId);
                    }
                }
                // Otherwise the entry stays idle, keeping LastSavedAt for the minimum interval, until RemoveIdleEntries
            }

            return true;
        }

        private void RemoveIdleEntries(double now)
        {
            List<string> idleIds = null;
            foreach (PendingCharacterSave pending in _pending.Values)
            {
                if (!pending.IsSaving && !pending.IsQueued && pending.Request.CharacterData == null &&
                    now >= pending.LastSavedAt + _minSaveIntervalSeconds)
                {
                    if (idleIds == null)
                        idleIds = new List<string>();
                    idleIds.Add(pending.CharacterId);
                }
            }
            if (idleIds == null)
                return;
            for (int i = 0; i < idleIds.Count; ++i)
                _pending.Remove(idleIds[i]);
        }

        private static UpdateCharacterReq MergeRequests(UpdateCharacterReq older, UpdateCharacterReq newer)
        {
            // The newer snapshot has the latest data for every table, it only needs the older flags
            if (older.State.Has(TransactionUpdateCharacterState.Summons) &&
                !newer.State.Has(TransactionUpdateCharacterState.Summons))
                newer.SummonBuffs = older.SummonBuffs;
            newer.State |= older.State;
            newer.DeleteStorageReservation |= older.DeleteStorageReservation;
            return newer;
        }

        private int GetLaneIndex(string characterId)
        {
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < characterId.Length; ++i)
                    hash = (hash * 31) + characterId[i];

                // Clear the sign bit; negating int.MinValue would leave it negative
                hash &= int.MaxValue;

                return hash % _lanes.Length;
            }
        }

        private static double GetUtcSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        }
#endif
    }
}
