// Server Meshing test rig — database burst (Phase 0 step 9 c1, MESH_DB_BURST=<N>@<t> or <R>/s@<t>+<d>,
// step9_design.md decision 2): N arrivals at once, or R a second for d s, each one a real handoff's database requests
// against a distinct burst character, through the cell's own DatabaseClient (the path a player's save takes, so the
// database manager's cache stays coherent). Grammar, names and lines in _Lines.cs.
using LiteNetLibManager;
using UnityEngine;
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Cysharp.Threading.Tasks;
#endif

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshDbBurst drives database arrivals from a map server. Warm-up (from when the server can instantiate
    /// objects): for each burst character meshburst&lt;k&gt;Char it looks up the id and the account, reads the full
    /// character (GetCharacterAsync) and sets a fresh access token (UpdateAccessTokenAsync), WarmParallel at a time. One
    /// arrival is then the 7 requests of a real handoff: the origin's save (UpdateCharacterAsync, State All, with the full
    /// data read in the warm-up, not the kit's SaveCharacter, which refuses while savingCharacters holds the id), and the
    /// destination's ValidateAccessTokenAsync, GetCharacterAsync (a cache hit after the save) and the background reads
    /// GetGoldAsync, GetCashAsync, GetUserLevelAsync, GetSummonBuffsAsync, in the kit's order (MapNetworkManager.cs
    /// :688, :769-805). Every map server process that has the key runs it, so a burst spec runs one cell.
    /// </summary>
    public sealed partial class ServerMeshDbBurst : MonoBehaviour
    {
        /// <summary>WarmParallel is how many burst characters the warm-up reads at a time.</summary>
        public const int WarmParallel = 16;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private sealed class BurstCharacter
        {
            public string name;
            public string userId;
            public string characterId;
            public string accessToken;
            public PlayerCharacterData data;
        }

        private static readonly List<CharacterBuff> s_noSummonBuffs = new List<CharacterBuff>();

        private Plan _plan;
        private MeshMapNetworkManager _mesh;
        private IDatabaseClient _db;
        private BurstCharacter[] _characters;
        private int[] _readySamples;
        private int _sampleCount;
        private bool _started, _warmFailed, _firing, _doneLogged;
        private float _nextTry;
        private double _readyAt, _fireAt, _firstArrivalAt, _windowStart;
        private long _warmStartTicks;
        private int _warmNext, _warmDone, _fired, _ready, _failed;
        private long _windowStarted, _windowReady, _windowFailed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string value = Environment.GetEnvironmentVariable(EnvironmentKey);
            if (string.IsNullOrEmpty(value))
                return;
            if (!ConfigReader.IsArgsProvided(Environment.GetCommandLineArgs(), ProcessArguments.ARG_START_MAP_SERVER))
                return;
            if (!TryParseSetting(value, out Plan plan, out string error))
            {
                Logging.LogError("Mesh", "DB burst error: " + error + "; no burst");
                return;
            }
            if (FindFirstObjectByType<ServerMeshDbBurst>() != null)
                return;
            GameObject go = new GameObject(BurstObjectName);
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshDbBurst>()._plan = plan;
        }

        private void Update()
        {
            if (!_started)
            {
                if (Time.unscaledTime < _nextTry)
                    return;
                _nextTry = Time.unscaledTime + 1f;
                TryStart();
                return;
            }
            double now = Time.unscaledTimeAsDouble;
            if (_firing)
                FireDue(now);
            if (now - _windowStart >= WindowSeconds)
                EndWindow(now);
        }

        private void TryStart()
        {
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || !mesh.IsServerReadyToInstantiateObjects())
                return;
            _started = true;
            _mesh = mesh;
            _readyAt = Time.unscaledTimeAsDouble;
            _windowStart = _readyAt;
            _db = mesh.DatabaseClient;
            if (_db == null)
            {
                Logging.LogError("Mesh", $"DB burst error: map server {Printable(mesh.OwnZoneId)} has no database client; no burst");
                enabled = false;
                return;
            }
            _characters = new BurstCharacter[_plan.Total];
            _readySamples = new int[_plan.Total];
            _warmStartTicks = Stopwatch.GetTimestamp();
            int workers = Math.Min(WarmParallel, _plan.Total);
            for (int i = 0; i < workers; ++i)
                WarmWorker().Forget();
        }

        private async UniTaskVoid WarmWorker()
        {
            while (!_warmFailed && _warmNext < _plan.Total)
            {
                int k = _warmNext++;
                BurstCharacter character = new BurstCharacter { name = CharacterName(k + 1) };
                string failedStep = await WarmOne(character);
                if (_warmFailed)
                    return;
                if (failedStep != null)
                {
                    _warmFailed = true;
                    Logging.LogError("Mesh", $"DB burst error: warm-up of {Printable(character.name)} failed at {failedStep} (run provision.py --burst {_plan.Total} out of Play); no burst");
                    return;
                }
                _characters[k] = character;
                if (++_warmDone == _plan.Total)
                    EndWarmUp();
            }
        }

        private async UniTask<string> WarmOne(BurstCharacter c)
        {
            string step = "id";
            try
            {
                DatabaseApiResult<GetIdByCharacterNameResp> id = await _db.GetIdByCharacterNameAsync(new GetIdByCharacterNameReq { CharacterName = c.name });
                if (!id.IsSuccess || string.IsNullOrEmpty(id.Response.Id))
                    return step;
                c.characterId = id.Response.Id;
                step = "user";
                DatabaseApiResult<GetUserIdByCharacterNameResp> user = await _db.GetUserIdByCharacterNameAsync(new GetUserIdByCharacterNameReq { CharacterName = c.name });
                if (!user.IsSuccess || string.IsNullOrEmpty(user.Response.UserId))
                    return step;
                c.userId = user.Response.UserId;
                step = "read";
                DatabaseApiResult<CharacterResp> read = await _db.GetCharacterAsync(new GetCharacterReq { UserId = c.userId, CharacterId = c.characterId });
                if (!read.IsSuccess || read.Response.CharacterData == null)
                    return step;
                c.data = read.Response.CharacterData;
                step = "token";
                c.accessToken = "meshburst-" + Guid.NewGuid().ToString("N");
                DatabaseApiResult token = await _db.UpdateAccessTokenAsync(new UpdateAccessTokenReq { UserId = c.userId, AccessToken = c.accessToken });
                return token.IsSuccess ? null : step;
            }
            catch (Exception)
            {
                return step;
            }
        }

        private void EndWarmUp()
        {
            double now = Time.unscaledTimeAsDouble;
            long warmMs = Milliseconds(Stopwatch.GetTimestamp() - _warmStartTicks, Stopwatch.Frequency);
            Logging.Log("Mesh", $"DB burst warm-up: {_plan.Total} characters read and tokens set in {warmMs} ms on {Printable(_mesh.OwnZoneId)}");
            _fireAt = _readyAt + _plan.StartSeconds;
            if (now > _fireAt)
            {
                Logging.LogWarning("Mesh", System.FormattableString.Invariant($"DB burst warm-up: ended {now - _readyAt:0.##} s after the server was ready, past the start at {_plan.StartSeconds:0.##} s; the arrivals start now"));
                _fireAt = now;
            }
            _firing = true;
        }

        private void FireDue(double now)
        {
            if (now < _fireAt)
                return;
            if (_fired == 0)
            {
                _firstArrivalAt = now;
                Logging.Log("Mesh", StartLine(_plan, now - _readyAt));
            }
            int due = DueCount(_plan, now - _firstArrivalAt);
            while (_fired < due)
            {
                int k = _fired++;
                Arrive(k).Forget();
            }
            if (_fired >= _plan.Total)
                _firing = false;
        }

        private async UniTaskVoid Arrive(int k)
        {
            _windowStarted++;
            BurstCharacter c = _characters[k];
            long start = Stopwatch.GetTimestamp();
            long mark = start;
            long save = 0, token = 0, get = 0;
            string step = "save";
            try
            {
                DatabaseApiResult<CharacterResp> saved = await _db.UpdateCharacterAsync(new UpdateCharacterReq
                {
                    State = TransactionUpdateCharacterState.All,
                    CharacterData = c.data,
                    SummonBuffs = s_noSummonBuffs,
                    DeleteStorageReservation = false,
                });
                if (!saved.IsSuccess)
                {
                    Fail(k, step, start);
                    return;
                }
                save = Lap(ref mark);
                step = "token";
                DatabaseApiResult<ValidateAccessTokenResp> valid = await _db.ValidateAccessTokenAsync(new ValidateAccessTokenReq { UserId = c.userId, AccessToken = c.accessToken });
                if (!valid.IsSuccess || !valid.Response.IsPass)
                {
                    Fail(k, step, start);
                    return;
                }
                token = Lap(ref mark);
                step = "get";
                DatabaseApiResult<CharacterResp> read = await _db.GetCharacterAsync(new GetCharacterReq { UserId = c.userId, CharacterId = c.characterId });
                if (!read.IsSuccess || read.Response.CharacterData == null)
                {
                    Fail(k, step, start);
                    return;
                }
                get = Lap(ref mark);
                step = "bg";
                if (!await BackgroundReads(c))
                {
                    Fail(k, step, start);
                    return;
                }
            }
            catch (Exception)
            {
                Fail(k, step, start);
                return;
            }
            long bg = Lap(ref mark);
            long ready = Milliseconds(Stopwatch.GetTimestamp() - start, Stopwatch.Frequency);
            Logging.Log("Mesh", ReadyLine(k + 1, save, token, get, bg, ready));
            _ready++;
            _windowReady++;
            if (_sampleCount < _readySamples.Length)
                _readySamples[_sampleCount++] = (int)Math.Min(ready, int.MaxValue);
            Settled();
        }

        private async UniTask<bool> BackgroundReads(BurstCharacter c)
        {
            DatabaseApiResult<GoldResp> gold = await _db.GetGoldAsync(new GetGoldReq { UserId = c.userId });
            if (!gold.IsSuccess)
                return false;
            DatabaseApiResult<CashResp> cash = await _db.GetCashAsync(new GetCashReq { UserId = c.userId });
            if (!cash.IsSuccess)
                return false;
            DatabaseApiResult<GetUserLevelResp> level = await _db.GetUserLevelAsync(new GetUserLevelReq { UserId = c.userId });
            if (!level.IsSuccess)
                return false;
            DatabaseApiResult<GetSummonBuffsResp> buffs = await _db.GetSummonBuffsAsync(new GetSummonBuffsReq { CharacterId = c.characterId });
            return buffs.IsSuccess;
        }

        private static long Lap(ref long mark)
        {
            long now = Stopwatch.GetTimestamp();
            long ms = Milliseconds(now - mark, Stopwatch.Frequency);
            mark = now;
            return ms;
        }

        private void Fail(int k, string step, long start)
        {
            Logging.Log("Mesh", FailedLine(k + 1, step, Milliseconds(Stopwatch.GetTimestamp() - start, Stopwatch.Frequency)));
            _failed++;
            _windowFailed++;
            Settled();
        }

        private void Settled()
        {
            if (_doneLogged || _ready + _failed < _plan.Total)
                return;
            _doneLogged = true;
            Logging.Log("Mesh", DoneLine(_ready, _failed));
        }

        private void EndWindow(double now)
        {
            int count = _sampleCount;
            if (count > 1)
                Array.Sort(_readySamples, 0, count);
            int p50 = Percentile(_readySamples, count, 0.50);
            int p95 = Percentile(_readySamples, count, 0.95);
            int max = count > 0 ? _readySamples[count - 1] : 0;
            Logging.Log("Mesh", WindowLine(now - _windowStart, _windowStarted, _windowReady, _windowFailed, p50, p95, max));
            _windowStart = now;
            _windowStarted = _windowReady = _windowFailed = 0;
            _sampleCount = 0;
        }
#endif
    }
}
