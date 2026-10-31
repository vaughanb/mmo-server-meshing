// Server Meshing add-on — a handoff arrival's relates data survives a database outage (DESIGN.md 0.89).
//
// A player handed off to this cell spawns at once and its relates data (gold, cash, user level, party, guild, storage)
// loads in the background (LoadPlayerCharacterEntityRelatesData, MeshMapNetworkManager_Ghosting). The kit's load kicks
// the player on the first failed gold, cash or user-level query. When the database connection drops under an arrival --
// the central process that hosts the database manager restarting while a merge's drain hands players over
// (splitcentral_merge) -- those queries can never be answered: the reconnect answers them with a timeout
// (MeshMapNetworkManager_Database) and the kit kicked a player whose character was fine. Here the load is the kit's,
// step for step, except that a query that failed because the connection that carried it went down is asked again
// once the connection is back, for up to arrivalLoadRetrySeconds. A query that failed over a connection that stayed up
// is a real database failure and ends as the kit's does (the entity destroyed, the client kicked), as does running out
// of time. The retry only reads: it never spawns, promotes or registers anything, and it stops without a word once the
// connection no longer holds this entity (the player left or was handed on), so it cannot duplicate a character.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshArrivalLoadRules is the pure part of an arrival's relates-data retry (DESIGN.md 0.89).</summary>
    public static class MeshArrivalLoadRules
    {
        /// <summary>
        /// Transient: a failed query is worth asking again when the connection that carried it went down -- it was
        /// down when the query was sent, or the drop count moved while it waited. A server that does not watch its
        /// database client (it hosts the database manager) never retries: every failure there is the kit's.
        /// </summary>
        public static bool Transient(bool watched, bool upWhenSent, int dropsWhenSent, int dropsNow)
        {
            return watched && (!upWhenSent || dropsNow != dropsWhenSent);
        }

        /// <summary>RetryLine is the warning an arrival logs before it asks a query again.</summary>
        public static string RetryLine(string name, long connectionId, string what, int attempt)
        {
            return $"Arrival {name} conn={connectionId}: {what} failed with the database connection lost; asking again once it is back (retry {attempt})";
        }

        /// <summary>GiveUpLine is the warning an arrival logs before the kit's kick.</summary>
        public static string GiveUpLine(string name, long connectionId, string what, bool transient, float retrySeconds)
        {
            string why = transient ? $"and the database connection was not back within {retrySeconds:0}s" : "over a live database connection";
            return $"Arrival {name} conn={connectionId}: {what} failed {why}; kicked as the kit does";
        }
    }

    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        [Tooltip("How long a handoff arrival's relates data (gold, cash, user level) keeps being asked again when the " +
                 "database connection drops under it, before the kit's kick. A central restart (which restarts the " +
                 "database manager on the split topology) takes 10-40 s. 0 restores the kit's kick on the first failure.")]
        public float arrivalLoadRetrySeconds = 90f;

        /// <summary>
        /// The kit's MapNetworkManager.LoadPlayerCharacterEntityRelatesData for a handoff arrival, with the retry
        /// described at the top of this file. Kept in step with the kit (OpenMMORPG develop d077c5f).
        /// </summary>
        private async UniTask LoadArrivalRelatesData(long connectionId, BasePlayerCharacterEntity playerCharacterEntity, string userId, string accessToken)
        {
            ArrivalLoad load = new ArrivalLoad(this, connectionId, playerCharacterEntity);
            // Gold
            DatabaseApiResult<GoldResp> getGoldResp = await load.Ask("gold", () => DatabaseClient.GetGoldAsync(new GetGoldReq() { UserId = userId }), r => r.IsSuccess);
            if (!load.Proceed(getGoldResp.IsSuccess))
                return;
            playerCharacterEntity.UserGold = getGoldResp.Response.Gold;
            // Cash
            DatabaseApiResult<CashResp> getCashResp = await load.Ask("cash", () => DatabaseClient.GetCashAsync(new GetCashReq() { UserId = userId }), r => r.IsSuccess);
            if (!load.Proceed(getCashResp.IsSuccess))
                return;
            playerCharacterEntity.UserCash = getCashResp.Response.Cash;

            // Set user Id
            playerCharacterEntity.UserId = userId;

            // Load user level
            DatabaseApiResult<GetUserLevelResp> getUserLevelResp = await load.Ask("user level", () => DatabaseClient.GetUserLevelAsync(new GetUserLevelReq() { UserId = userId }), r => r.IsSuccess);
            if (!load.Proceed(getUserLevelResp.IsSuccess))
                return;
            playerCharacterEntity.UserLevel = getUserLevelResp.Response.UserLevel;
            load.Done();

            // Load party data, if this map-server does not have party data (the kit's routines retry until loaded)
            if (playerCharacterEntity.PartyId > 0)
            {
                if (!ServerPartyHandlers.ContainsParty(playerCharacterEntity.PartyId))
                    await LoadPartyRoutine(playerCharacterEntity.PartyId);
                if (ServerPartyHandlers.TryGetParty(playerCharacterEntity.PartyId, out PartyData party))
                {
                    ServerGameMessageHandlers.SendSetFullPartyData(connectionId, party);
                }
                else
                {
                    playerCharacterEntity.ClearParty();
                }
            }

            // Load guild data, if this map-server does not have guild data
            if (playerCharacterEntity.GuildId > 0)
            {
                if (!ServerGuildHandlers.ContainsGuild(playerCharacterEntity.GuildId))
                    await LoadGuildRoutine(playerCharacterEntity.GuildId);
                if (ServerGuildHandlers.TryGetGuild(playerCharacterEntity.GuildId, out GuildData guild))
                {
                    playerCharacterEntity.GuildRole = guild.GetMemberRole(playerCharacterEntity.Id);
                    foreach (KeyValuePair<int, int> kv in guild.skillLevels)
                    {
                        playerCharacterEntity.GuildSkills.Add(CharacterSkill.Create(kv.Key, kv.Value));
                    }
                    ServerGameMessageHandlers.SendSetFullGuildData(connectionId, guild);
                }
                else
                {
                    playerCharacterEntity.ClearGuild();
                }
            }

            // Load storage
            StorageId storageId = new StorageId(StorageType.Player, userId);
            await LoadStorageRoutine(storageId);
            MarkArrivalLoaded(playerCharacterEntity); // DESIGN 1.37: a storage it had open on the origin reopens now
        }

        /// <summary>Whether this connection still holds this entity: false once the player left or was handed on.</summary>
        private bool ArrivalStillHere(long connectionId, BasePlayerCharacterEntity entity)
        {
            return entity != null && ServerUserHandlers.TryGetPlayerCharacter(connectionId, out IPlayerCharacterData held) && ReferenceEquals(held, entity);
        }

        /// <summary>One arrival's relates-data load: asks a query until it succeeds or the failure is the kit's to handle.</summary>
        private sealed class ArrivalLoad
        {
            private readonly MeshMapNetworkManager _manager;
            private readonly long _connectionId;
            private readonly BasePlayerCharacterEntity _entity;
            private readonly string _name;
            private readonly float _deadline;
            private int _retries;
            private bool _left;

            public ArrivalLoad(MeshMapNetworkManager manager, long connectionId, BasePlayerCharacterEntity entity)
            {
                _manager = manager;
                _connectionId = connectionId;
                _entity = entity;
                _name = entity != null ? entity.CharacterName : "?";
                _deadline = Time.unscaledTime + Mathf.Max(0f, manager.arrivalLoadRetrySeconds);
            }

            /// <summary>Asks the query; again, once the connection is back, while each failure is transient and time remains.</summary>
            public async UniTask<T> Ask<T>(string what, System.Func<UniTask<T>> query, System.Func<T, bool> succeeded)
            {
                while (true)
                {
                    bool watched = _manager.DatabaseClientWatched;
                    bool up = _manager.IsDatabaseConnected;
                    int drops = _manager.DatabaseDrops;
                    T result = await query();
                    if (succeeded(result))
                        return result;
                    if (!_manager.ArrivalStillHere(_connectionId, _entity))
                    {
                        _left = true;
                        return result;
                    }
                    bool transient = MeshArrivalLoadRules.Transient(watched, up, drops, _manager.DatabaseDrops);
                    if (!transient || Time.unscaledTime >= _deadline)
                    {
                        Logging.LogWarning("Mesh", MeshArrivalLoadRules.GiveUpLine(_name, _connectionId, what, transient, _manager.arrivalLoadRetrySeconds));
                        return result;
                    }
                    _retries++;
                    Logging.LogWarning("Mesh", MeshArrivalLoadRules.RetryLine(_name, _connectionId, what, _retries));
                    await UniTask.Delay(500, true);
                    while (!_manager.IsDatabaseConnected && Time.unscaledTime < _deadline && _manager.ArrivalStillHere(_connectionId, _entity))
                        await UniTask.Yield();
                    if (!_manager.ArrivalStillHere(_connectionId, _entity))
                    {
                        _left = true;
                        return result;
                    }
                }
            }

            /// <summary>
            /// After a query: true to go on (a success is applied as the kit applies it). On a failure the kit's ending
            /// (entity destroyed, client kicked), unless the player already left this connection: then the load ends
            /// without a word, and a connection id that may now be someone else's is not kicked.
            /// </summary>
            public bool Proceed(bool succeeded)
            {
                if (succeeded)
                    return true;
                if (_left || !_manager.ArrivalStillHere(_connectionId, _entity))
                {
                    Logging.Log("Mesh", $"Arrival {_name} conn={_connectionId}: left this cell during its relates-data load; stopped");
                    return false;
                }
                _entity.NetworkDestroy();
                _manager.KickClient(_connectionId, UITextKeys.UI_ERROR_KICKED_FROM_SERVER);
                return false;
            }

            /// <summary>The kickable queries are in: says so when they needed a retry.</summary>
            public void Done()
            {
                if (_retries > 0)
                    Logging.Log("Mesh", $"Arrival {_name} conn={_connectionId}: relates data loaded after {_retries} retr{(_retries == 1 ? "y" : "ies")}");
            }
        }
#endif
    }
}
