// Server Meshing add-on — invitations across a line and the trade's two halves (DESIGN.md 1.44).
//
// A party or guild invitation names the invitee by character id, and the kit's handler looks it up among the players
// registered on this server: one standing across the line is a puppet here, not registered, and the kit answered
// CHARACTER_NOT_FOUND. The wrappers below (set over the kit's handlers in MeshMapNetworkManager.SetServerHandlersRef,
// as the chat and storage ones are) find the puppet, validate the inviter's side here (the puppet carries the invitee's
// party and guild ids) and forward the invitation to the puppet's owner, where it is kept and delivered: the accept
// and the decline come in on the invitee's own cell, which is where the kit keeps invitations. Friend requests need
// no wrapper: the kit writes them to the database by id.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>The kit's party message handlers, with the invitation of a player across a line forwarded to its owner.</summary>
    public sealed class MeshServerPartyMessageHandlers : IServerPartyMessageHandlers
    {
        private readonly IServerPartyMessageHandlers _kit;
        private readonly MeshMapNetworkManager _manager;

        public MeshServerPartyMessageHandlers(IServerPartyMessageHandlers kit, MeshMapNetworkManager manager)
        {
            _kit = kit;
            _manager = manager;
        }

        public UniTaskVoid HandleRequestSendPartyInvitation(RequestHandlerData requestHandler, RequestSendPartyInvitationMessage request, RequestProceedResultDelegate<ResponseSendPartyInvitationMessage> result)
        {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (!GameInstance.ServerUserHandlers.TryGetPlayerCharacter(requestHandler.ConnectionId, out BasePlayerCharacterEntity inviter)
                || GameInstance.ServerUserHandlers.TryGetPlayerCharacterById(request.inviteeId, out _))
                return _kit.HandleRequestSendPartyInvitation(requestHandler, request, result); // not logged in, or the invitee is on this cell: the kit's path
            BasePlayerCharacterEntity invitee = _manager.FindPlayerGhost(request.inviteeId);
            if (invitee == null)
                return _kit.HandleRequestSendPartyInvitation(requestHandler, request, result); // not here at all: the kit answers not found
            ValidatePartyRequestResult validate = GameInstance.ServerPartyHandlers.CanSendPartyInvitation(inviter, invitee);
            if (!validate.IsSuccess)
            {
                result.InvokeError(new ResponseSendPartyInvitationMessage { message = validate.GameMessage });
                return default;
            }
            PartyData party = validate.Party;
            string inviterId = inviter.Id, inviterName = inviter.CharacterName;
            int inviterLevel = inviter.Level, partyId = validate.PartyId;
            _manager.ForwardGhostAction(invitee, GhostActionKind.PartyInvite, inviter.GetInfo(), writer =>
            {
                writer.Put(inviterId); writer.Put(inviterName); writer.Put(inviterLevel);
                writer.Put(partyId); writer.Put(party.shareExp); writer.Put(party.shareItem);
            });
            Logging.Log("Mesh", $"Party invitation {inviter.CharacterName} -> {invitee.CharacterName} across a line, sent to its owner");
            result.InvokeSuccess(new ResponseSendPartyInvitationMessage());
            return default;
#else
            return _kit.HandleRequestSendPartyInvitation(requestHandler, request, result);
#endif
        }

        public UniTaskVoid HandleRequestCreateParty(RequestHandlerData requestHandler, RequestCreatePartyMessage request, RequestProceedResultDelegate<ResponseCreatePartyMessage> result) => _kit.HandleRequestCreateParty(requestHandler, request, result);
        public UniTaskVoid HandleRequestChangePartyLeader(RequestHandlerData requestHandler, RequestChangePartyLeaderMessage request, RequestProceedResultDelegate<ResponseChangePartyLeaderMessage> result) => _kit.HandleRequestChangePartyLeader(requestHandler, request, result);
        public UniTaskVoid HandleRequestChangePartySetting(RequestHandlerData requestHandler, RequestChangePartySettingMessage request, RequestProceedResultDelegate<ResponseChangePartySettingMessage> result) => _kit.HandleRequestChangePartySetting(requestHandler, request, result);
        public UniTaskVoid HandleRequestAcceptPartyInvitation(RequestHandlerData requestHandler, RequestAcceptPartyInvitationMessage request, RequestProceedResultDelegate<ResponseAcceptPartyInvitationMessage> result) => _kit.HandleRequestAcceptPartyInvitation(requestHandler, request, result);
        public UniTaskVoid HandleRequestDeclinePartyInvitation(RequestHandlerData requestHandler, RequestDeclinePartyInvitationMessage request, RequestProceedResultDelegate<ResponseDeclinePartyInvitationMessage> result) => _kit.HandleRequestDeclinePartyInvitation(requestHandler, request, result);
        public UniTaskVoid HandleRequestKickMemberFromParty(RequestHandlerData requestHandler, RequestKickMemberFromPartyMessage request, RequestProceedResultDelegate<ResponseKickMemberFromPartyMessage> result) => _kit.HandleRequestKickMemberFromParty(requestHandler, request, result);
        public UniTaskVoid HandleRequestLeaveParty(RequestHandlerData requestHandler, EmptyMessage request, RequestProceedResultDelegate<ResponseLeavePartyMessage> result) => _kit.HandleRequestLeaveParty(requestHandler, request, result);
    }

    /// <summary>The kit's guild message handlers, with the invitation of a player across a line forwarded to its owner.</summary>
    public sealed class MeshServerGuildMessageHandlers : IServerGuildMessageHandlers
    {
        private readonly IServerGuildMessageHandlers _kit;
        private readonly MeshMapNetworkManager _manager;

        public MeshServerGuildMessageHandlers(IServerGuildMessageHandlers kit, MeshMapNetworkManager manager)
        {
            _kit = kit;
            _manager = manager;
        }

        public UniTaskVoid HandleRequestSendGuildInvitation(RequestHandlerData requestHandler, RequestSendGuildInvitationMessage request, RequestProceedResultDelegate<ResponseSendGuildInvitationMessage> result)
        {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (!GameInstance.ServerUserHandlers.TryGetPlayerCharacter(requestHandler.ConnectionId, out BasePlayerCharacterEntity inviter)
                || GameInstance.ServerUserHandlers.TryGetPlayerCharacterById(request.inviteeId, out _))
                return _kit.HandleRequestSendGuildInvitation(requestHandler, request, result);
            BasePlayerCharacterEntity invitee = _manager.FindPlayerGhost(request.inviteeId);
            if (invitee == null)
                return _kit.HandleRequestSendGuildInvitation(requestHandler, request, result);
            ValidateGuildRequestResult validate = GameInstance.ServerGuildHandlers.CanSendGuildInvitation(inviter, invitee);
            if (!validate.IsSuccess)
            {
                result.InvokeError(new ResponseSendGuildInvitationMessage { message = validate.GameMessage });
                return default;
            }
            GuildData guild = validate.Guild;
            string inviterId = inviter.Id, inviterName = inviter.CharacterName;
            int inviterLevel = inviter.Level, guildId = validate.GuildId;
            _manager.ForwardGhostAction(invitee, GhostActionKind.GuildInvite, inviter.GetInfo(), writer =>
            {
                writer.Put(inviterId); writer.Put(inviterName); writer.Put(inviterLevel);
                writer.Put(guildId); writer.Put(guild.guildName ?? string.Empty); writer.Put(guild.level);
            });
            Logging.Log("Mesh", $"Guild invitation {inviter.CharacterName} -> {invitee.CharacterName} across a line, sent to its owner");
            result.InvokeSuccess(new ResponseSendGuildInvitationMessage());
            return default;
#else
            return _kit.HandleRequestSendGuildInvitation(requestHandler, request, result);
#endif
        }

        public UniTaskVoid HandleRequestCreateGuild(RequestHandlerData requestHandler, RequestCreateGuildMessage request, RequestProceedResultDelegate<ResponseCreateGuildMessage> result) => _kit.HandleRequestCreateGuild(requestHandler, request, result);
        public UniTaskVoid HandleRequestChangeGuildLeader(RequestHandlerData requestHandler, RequestChangeGuildLeaderMessage request, RequestProceedResultDelegate<ResponseChangeGuildLeaderMessage> result) => _kit.HandleRequestChangeGuildLeader(requestHandler, request, result);
        public UniTaskVoid HandleRequestChangeGuildMessage(RequestHandlerData requestHandler, RequestChangeGuildMessageMessage request, RequestProceedResultDelegate<ResponseChangeGuildMessageMessage> result) => _kit.HandleRequestChangeGuildMessage(requestHandler, request, result);
        public UniTaskVoid HandleRequestChangeGuildMessage2(RequestHandlerData requestHandler, RequestChangeGuildMessageMessage request, RequestProceedResultDelegate<ResponseChangeGuildMessageMessage> result) => _kit.HandleRequestChangeGuildMessage2(requestHandler, request, result);
        public UniTaskVoid HandleRequestChangeGuildOptions(RequestHandlerData requestHandler, RequestChangeGuildOptionsMessage request, RequestProceedResultDelegate<ResponseChangeGuildOptionsMessage> result) => _kit.HandleRequestChangeGuildOptions(requestHandler, request, result);
        public UniTaskVoid HandleRequestChangeGuildAutoAcceptRequests(RequestHandlerData requestHandler, RequestChangeGuildAutoAcceptRequestsMessage request, RequestProceedResultDelegate<ResponseChangeGuildAutoAcceptRequestsMessage> result) => _kit.HandleRequestChangeGuildAutoAcceptRequests(requestHandler, request, result);
        public UniTaskVoid HandleRequestChangeGuildRole(RequestHandlerData requestHandler, RequestChangeGuildRoleMessage request, RequestProceedResultDelegate<ResponseChangeGuildRoleMessage> result) => _kit.HandleRequestChangeGuildRole(requestHandler, request, result);
        public UniTaskVoid HandleRequestChangeMemberGuildRole(RequestHandlerData requestHandler, RequestChangeMemberGuildRoleMessage request, RequestProceedResultDelegate<ResponseChangeMemberGuildRoleMessage> result) => _kit.HandleRequestChangeMemberGuildRole(requestHandler, request, result);
        public UniTaskVoid HandleRequestAcceptGuildInvitation(RequestHandlerData requestHandler, RequestAcceptGuildInvitationMessage request, RequestProceedResultDelegate<ResponseAcceptGuildInvitationMessage> result) => _kit.HandleRequestAcceptGuildInvitation(requestHandler, request, result);
        public UniTaskVoid HandleRequestDeclineGuildInvitation(RequestHandlerData requestHandler, RequestDeclineGuildInvitationMessage request, RequestProceedResultDelegate<ResponseDeclineGuildInvitationMessage> result) => _kit.HandleRequestDeclineGuildInvitation(requestHandler, request, result);
        public UniTaskVoid HandleRequestKickMemberFromGuild(RequestHandlerData requestHandler, RequestKickMemberFromGuildMessage request, RequestProceedResultDelegate<ResponseKickMemberFromGuildMessage> result) => _kit.HandleRequestKickMemberFromGuild(requestHandler, request, result);
        public UniTaskVoid HandleRequestLeaveGuild(RequestHandlerData requestHandler, EmptyMessage request, RequestProceedResultDelegate<ResponseLeaveGuildMessage> result) => _kit.HandleRequestLeaveGuild(requestHandler, request, result);
        public UniTaskVoid HandleRequestIncreaseGuildSkillLevel(RequestHandlerData requestHandler, RequestIncreaseGuildSkillLevelMessage request, RequestProceedResultDelegate<ResponseIncreaseGuildSkillLevelMessage> result) => _kit.HandleRequestIncreaseGuildSkillLevel(requestHandler, request, result);
        public UniTaskVoid HandleRequestSendGuildRequest(RequestHandlerData requestHandler, RequestSendGuildRequestMessage request, RequestProceedResultDelegate<ResponseSendGuildRequestMessage> result) => _kit.HandleRequestSendGuildRequest(requestHandler, request, result);
        public UniTaskVoid HandleRequestAcceptGuildRequest(RequestHandlerData requestHandler, RequestAcceptGuildRequestMessage request, RequestProceedResultDelegate<ResponseAcceptGuildRequestMessage> result) => _kit.HandleRequestAcceptGuildRequest(requestHandler, request, result);
        public UniTaskVoid HandleRequestDeclineGuildRequest(RequestHandlerData requestHandler, RequestDeclineGuildRequestMessage request, RequestProceedResultDelegate<ResponseDeclineGuildRequestMessage> result) => _kit.HandleRequestDeclineGuildRequest(requestHandler, request, result);
        public UniTaskVoid HandleRequestGetGuildRequests(RequestHandlerData requestHandler, RequestGetGuildRequestsMessage request, RequestProceedResultDelegate<ResponseGetGuildRequestsMessage> result) => _kit.HandleRequestGetGuildRequests(requestHandler, request, result);
        public UniTaskVoid HandleRequestFindGuilds(RequestHandlerData requestHandler, RequestFindGuildsMessage request, RequestProceedResultDelegate<ResponseFindGuildsMessage> result) => _kit.HandleRequestFindGuilds(requestHandler, request, result);
        public UniTaskVoid HandleRequestGetGuildInfo(RequestHandlerData requestHandler, RequestGetGuildInfoMessage request, RequestProceedResultDelegate<ResponseGetGuildInfoMessage> result) => _kit.HandleRequestGetGuildInfo(requestHandler, request, result);
        public UniTaskVoid HandleRequestGuildRequestNotification(RequestHandlerData requestHandler, EmptyMessage request, RequestProceedResultDelegate<ResponseGuildRequestNotificationMessage> result) => _kit.HandleRequestGuildRequestNotification(requestHandler, request, result);
    }

    /// <summary>
    /// The two halves of the kit's exchange (PlayerCharacterDealingComponent.ExchangeDealingItemsAndGold), separable so
    /// each trader's cell runs its own: take an offer out of an inventory, give one into another. Pure over character
    /// data, so a test can run them without a server.
    /// </summary>
    public static class MeshDealing
    {
        /// <summary>
        /// Takes <paramref name="offered"/> and <paramref name="gold"/> out of <paramref name="from"/>, what was taken
        /// appended to <paramref name="taken"/> (each a copy of the inventory item at the offered amount). False and
        /// nothing taken when an offered item is not in the inventory in that amount, or the gold is short.
        /// </summary>
        public static bool TakeOffered(IPlayerCharacterData from, IList<CharacterItem> offered, int gold, List<CharacterItem> taken)
        {
            if (from == null || from.NonEquipItems == null)
                return false;
            if (from.Gold < gold)
                return false;
            for (int j = 0; j < (offered != null ? offered.Count : 0); ++j)
            {
                if (offered[j].IsEmptySlot())
                    continue;
                int index = from.IndexOfNonEquipItem(offered[j].id);
                if (index < 0 || from.NonEquipItems[index].amount < offered[j].amount)
                    return false;
            }
            for (int j = 0; j < (offered != null ? offered.Count : 0); ++j)
            {
                if (offered[j].IsEmptySlot())
                    continue;
                int index = from.IndexOfNonEquipItem(offered[j].id);
                CharacterItem item = from.NonEquipItems[index];
                CharacterItem copy = item.Clone();
                copy.amount = offered[j].amount;
                taken.Add(copy);
                item.amount -= offered[j].amount;
                if (item.amount <= 0)
                    from.NonEquipItems.RemoveOrPlaceEmptySlot(index);
                else
                    from.NonEquipItems[index] = item;
            }
            from.Gold -= gold;
            from.FillEmptySlots();
            return true;
        }

        /// <summary>Gives <paramref name="items"/> and <paramref name="gold"/> to <paramref name="to"/>; <paramref name="onItem"/> is the kit's reward hook per item given.</summary>
        public static void Give(IPlayerCharacterData to, IList<CharacterItem> items, int gold, System.Action<CharacterItem> onItem)
        {
            if (to == null)
                return;
            for (int i = 0; i < (items != null ? items.Count : 0); ++i)
            {
                if (items[i].IsEmptySlot())
                    continue;
                to.IncreaseItems(items[i], onItem);
            }
            if (gold != 0)
                to.Gold = to.Gold.Increase(gold);
            to.FillEmptySlots();
        }

        /// <summary>Whether receiving <paramref name="items"/> would overfill <paramref name="to"/> (the kit's slot and weight rule).</summary>
        public static bool WillOverwhelm(ICharacterData to, IList<CharacterItem> items)
        {
            if (to == null)
                return true;
            List<CharacterItem> filled = new List<CharacterItem>();
            for (int i = 0; i < (items != null ? items.Count : 0); ++i)
                if (!items[i].IsEmptySlot())
                    filled.Add(items[i]);
            return filled.Count > 0 && to.IncreasingItemsWillOverwhelming(filled);
        }
    }
}
