// Server Meshing add-on — automated test client: a trade, a duel and a party invitation with a player across a line
// (DESIGN.md 1.44).
//
// social (the driver): walk to (walkTargetX, walkTargetZ), find the character named socialTargetName (a puppet of a
// player on the other cell, or anyone), then in order: trade one bag item and socialGold for whatever it offers, duel it
// until the duel ends, make a party and invite it. The RESULT reads `RESULT social: trade=<ok|how far> duel=<ok|how
// far> party=<ok|how far>`, PASS when all three went through.
// socialAutoAccept (the other side, any mode): accept every trade request (offering its own first bag item and
// socialGold, lock, confirm after the other confirms), every duel request and every party or guild invitation.
using System.Collections;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshAutoClient
    {
        [Header("Social (trade, duel, invite)")]
        [Tooltip("social: the character to trade with, duel and invite (a player's name).")]
        public string socialTargetName = "";
        [Tooltip("Accept every trade request (offer the first bag item and socialGold, lock, confirm), duel request, party and guild invitation that comes in, in any mode.")]
        public bool socialAutoAccept = false;
        [Tooltip("Gold offered in a trade, by the driver and by an auto-accepting client.")]
        public int socialGold = 5;

        public const string ARG_SOCIAL_ACCEPT = "-meshSocialAccept";
        public const string ARG_SOCIAL_TARGET = "-meshSocialTarget";

        private BasePlayerCharacterEntity _socialHooked;
        private DealingState _myDealState = DealingState.None, _otherDealState = DealingState.None;
        private int _dealAccepted, _duelAccepted, _invitesAccepted;
        private bool _dealStarted, _dealEnded, _duelStarted, _duelEnded;
        private string _duelLoser = "";

        private void ReadSocialArgs(string[] args)
        {
            socialAutoAccept = socialAutoAccept || System.Array.IndexOf(args, ARG_SOCIAL_ACCEPT) >= 0;
            string target = ReadArg(args, ARG_SOCIAL_TARGET, null);
            if (!string.IsNullOrEmpty(target))
                socialTargetName = target;
        }

        /// <summary>Hooks the own character's trade and duel events (again after a hop: the entity changes).</summary>
        private void HookSocial(bool autoAccept)
        {
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (self == null || ReferenceEquals(self, _socialHooked) || self.DealingComponent == null || self.DuelingComponent == null)
                return;
            _socialHooked = self;
            PlayerCharacterDealingComponent dealing = self.DealingComponent;
            PlayerCharacterDuelingComponent dueling = self.DuelingComponent;
            Log($"social: hooked {self.CharacterName} (object {self.ObjectId}, {dealing.GetType().Name}, {dueling.GetType().Name}, auto-accept {autoAccept})");
            StartCoroutine(LogPlayersInSight());
            dealing.onRequestDealing += other =>
            {
                Log($"social: trade request from {other.CharacterName}{(autoAccept ? ", accepting" : "")}");
                if (autoAccept) { _dealAccepted++; dealing.CallCmdAcceptDealingRequest(); }
            };
            dealing.onStartDealing += other =>
            {
                _dealStarted = true;
                _myDealState = DealingState.Dealing;
                Log($"social: trade with {other.CharacterName} open");
                OfferAndLock(self);
            };
            dealing.onUpdateDealingState += state =>
            {
                Log($"social: my trade state {state}");
                if (_myDealState != DealingState.None && state == DealingState.None)
                    _dealEnded = true;
                _myDealState = state;
            };
            dealing.onUpdateAnotherDealingState += state =>
            {
                Log($"social: the other's trade state {state}");
                _otherDealState = state;
                // Confirm once the other is locked or confirmed and this side is locked: the second confirm closes the trade.
                if ((state == DealingState.LockDealing || state == DealingState.ConfirmDealing) && _myDealState == DealingState.LockDealing)
                {
                    Log("social: confirming the trade");
                    dealing.CallCmdConfirmDealing();
                }
            };
            dealing.onUpdateAnotherDealingItems += items => Log($"social: the other offers {items.Count} item(s)");
            dealing.onUpdateAnotherDealingGold += gold => Log($"social: the other offers {gold} gold");
            dueling.onRequestDueling += other =>
            {
                Log($"social: duel request from {other.CharacterName}{(autoAccept ? ", accepting" : "")}");
                if (autoAccept) { _duelAccepted++; dueling.CallCmdAcceptDuelingRequest(); }
            };
            dueling.onStartDueling += (other, countDown, duration) =>
            {
                _duelStarted = true;
                Log($"social: duel with {other.CharacterName} starts ({countDown:0}+{duration:0} s)");
            };
            dueling.onEndDueling += loser =>
            {
                _duelEnded = true;
                _duelLoser = loser != null ? loser.CharacterName : "nobody";
                Log($"social: duel ended, loser {_duelLoser}");
            };
            if (autoAccept)
            {
                ClientPartyActions.onNotifyPartyInvitation += invitation =>
                {
                    Log($"social: party invitation from {invitation.InviterName} (party {invitation.PartyId}), accepting");
                    GameInstance.ClientPartyHandlers.RequestAcceptPartyInvitation(new RequestAcceptPartyInvitationMessage { partyId = invitation.PartyId, inviterId = invitation.InviterId },
                        (handler, code, response) => { _invitesAccepted++; Log($"social: party invitation accept -> {code} {response.message}"); });
                };
                ClientGuildActions.onNotifyGuildInvitation += invitation =>
                {
                    Log($"social: guild invitation from {invitation.InviterName} (guild {invitation.GuildId}), accepting");
                    GameInstance.ClientGuildHandlers.RequestAcceptGuildInvitation(new RequestAcceptGuildInvitationMessage { guildId = invitation.GuildId, inviterId = invitation.InviterId },
                        (handler, code, response) => { _invitesAccepted++; Log($"social: guild invitation accept -> {code} {response.message}"); });
                };
            }
        }

        /// <summary>Which other players this client has, now and a little later: whether a player already standing across the line is shown to a client that logged in after it (DESIGN 1.44).</summary>
        private IEnumerator LogPlayersInSight()
        {
            for (int round = 0; round < 3; ++round)
            {
                System.Text.StringBuilder names = new System.Text.StringBuilder();
                foreach (BasePlayerCharacterEntity entity in FindObjectsByType<BasePlayerCharacterEntity>(FindObjectsSortMode.None))
                    if (entity != null && !entity.IsOwnerClient)
                        names.Append(entity.CharacterName).Append('@').Append(entity.EntityTransform.position.x.ToString("0.#")).Append(' ');
                Log($"social: other players in sight after {round * 10} s: {(names.Length > 0 ? names.ToString() : "none")}");
                yield return new WaitForSecondsRealtime(10f);
            }
        }

        /// <summary>Offers the first filled bag item (one of it) and socialGold, then locks.</summary>
        private void OfferAndLock(BasePlayerCharacterEntity self)
        {
            StartCoroutine(OfferAndLockRoutine(self));
        }

        private IEnumerator OfferAndLockRoutine(BasePlayerCharacterEntity self)
        {
            yield return new WaitForSecondsRealtime(0.5f);
            if (self == null || self.DealingComponent == null || _myDealState != DealingState.Dealing)
                yield break;
            for (int i = 0; i < self.NonEquipItems.Count; ++i)
            {
                CharacterItem item = self.NonEquipItems[i];
                if (item.IsEmptySlot() || item.GetItem() == null || item.GetItem().RestrictDealing)
                    continue;
                Log($"social: offering {item.GetItem().Title} x1 (of {item.amount}) and {Mathf.Min(socialGold, self.Gold)} gold");
                self.DealingComponent.CallCmdSetDealingItem(item.id, 1);
                break;
            }
            self.DealingComponent.CallCmdSetDealingGold(Mathf.Min(socialGold, self.Gold));
            yield return new WaitForSecondsRealtime(0.5f);
            if (self != null && self.DealingComponent != null && _myDealState == DealingState.Dealing)
                self.DealingComponent.CallCmdLockDealing();
        }

        /// <summary>Keeps the hooks on the current character while the client runs (any mode).</summary>
        private IEnumerator KeepSocialHooked()
        {
            while (true)
            {
                HookSocial(socialAutoAccept);
                yield return null;
            }
        }

        private static BasePlayerCharacterEntity FindPlayerByName(string name)
        {
            foreach (BasePlayerCharacterEntity entity in FindObjectsByType<BasePlayerCharacterEntity>(FindObjectsSortMode.None))
                if (entity != null && !entity.IsOwnerClient && string.Equals(entity.CharacterName, name))
                    return entity;
            return null;
        }

        private IEnumerator RunSocial(float deadline)
        {
            Phase = "social-walk";
            _walkZOverride = walkTargetZ;
            bool reached = false;
            yield return WalkTo(walkTargetX, deadline, ClientMigrationController.CompletedCount, r => reached = r);
            _walkZOverride = null;
            if (!reached) { Fail("never reached the social position"); yield break; }
            Phase = "social-find";
            BasePlayerCharacterEntity target = null;
            float until = Time.realtimeSinceStartup + 30f;
            while (target == null && Time.realtimeSinceStartup < until)
            {
                target = FindPlayerByName(socialTargetName);
                yield return null;
            }
            if (target == null) { Fail($"no player named '{socialTargetName}' in sight"); yield break; }
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            HookSocial(false);
            // The line the test is about is x = 0 (the lane's cell edge): the target across it or on this side.
            bool across = (target.EntityTransform.position.x < 0f) != (self.EntityTransform.position.x < 0f);
            Log($"social: target '{target.CharacterName}' id {target.Id} object {target.ObjectId} at {target.EntityTransform.position}, {Vector3.Distance(self.EntityTransform.position, target.EntityTransform.position):0.#} m away, {(across ? "across the line" : "on this side")}; my gold {self.Gold}, bag {BagSummaryShort(self)}");

            // 1. Trade.
            Phase = "social-trade";
            int goldBefore = self.Gold;
            string bagBefore = BagSummaryShort(self);
            string trade = "not started";
            _dealStarted = _dealEnded = false;
            self.DealingComponent.CallCmdSendDealingRequest(target.ObjectId);
            until = Time.realtimeSinceStartup + 40f;
            while (!_dealEnded && Time.realtimeSinceStartup < until)
                yield return null;
            self = GameInstance.PlayingCharacterEntity;
            if (!_dealStarted) trade = "never opened";
            else if (!_dealEnded) trade = $"stuck at mine={_myDealState} other={_otherDealState}";
            else trade = _otherDealState == DealingState.ConfirmDealing || _myDealState == DealingState.None ? "ok" : "ended early";
            Log($"social: trade {trade}; gold {goldBefore} -> {self.Gold}; bag {bagBefore} -> {BagSummaryShort(self)}");
            bool tradeOk = trade == "ok" && self.Gold != goldBefore;
            yield return new WaitForSecondsRealtime(2f);

            // 2. Party: make one, invite the target, wait for it to join.
            Phase = "social-party";
            self = GameInstance.PlayingCharacterEntity;
            target = FindPlayerByName(socialTargetName);
            string party = "not started";
            if (target == null)
                party = "the target left";
            else
            {
                bool created = GameInstance.JoinedParty != null, answered = GameInstance.JoinedParty != null;
                if (!created)
                    GameInstance.ClientPartyHandlers.RequestCreateParty(new RequestCreatePartyMessage { shareExp = true, shareItem = false }, (handler, code, response) => { answered = true; created = code == LiteNetLibManager.AckResponseCode.Success; Log($"social: create party -> {code} {response.message}"); });
                until = Time.realtimeSinceStartup + 15f;
                while (!answered && Time.realtimeSinceStartup < until) yield return null;
                if (!created) party = "no party";
                else
                {
                    bool sent = false; answered = false;
                    string inviteeId = target.Id;
                    GameInstance.ClientPartyHandlers.RequestSendPartyInvitation(new RequestSendPartyInvitationMessage { inviteeId = inviteeId }, (handler, code, response) => { answered = true; sent = code == LiteNetLibManager.AckResponseCode.Success; Log($"social: party invitation -> {code} {response.message}"); });
                    until = Time.realtimeSinceStartup + 15f;
                    while (!answered && Time.realtimeSinceStartup < until) yield return null;
                    if (!sent) party = "invitation refused";
                    else
                    {
                        until = Time.realtimeSinceStartup + 30f;
                        bool joined = false;
                        while (!joined && Time.realtimeSinceStartup < until)
                        {
                            joined = GameInstance.JoinedParty != null && GameInstance.JoinedParty.IsMember(inviteeId);
                            yield return null;
                        }
                        party = joined ? $"ok ({GameInstance.JoinedParty.CountMember()} members)" : "invited, never joined";
                    }
                }
            }
            Log($"social: party {party}");
            bool partyOk = party.StartsWith("ok");
            // 3. Duel, last: it may end with the target dead.
            Phase = "social-duel";
            _duelStarted = _duelEnded = false;
            target = FindPlayerByName(socialTargetName);
            if (target == null) { Fail("the target left before the duel"); yield break; }
            self.DuelingComponent.CallCmdSendDuelingRequest(target.ObjectId);
            until = Time.realtimeSinceStartup + 30f;
            while (!_duelStarted && Time.realtimeSinceStartup < until)
                yield return null;
            string duel;
            int swings = 0, targetHpStart = target.CurrentHp, targetHpMin = target.CurrentHp;
            if (!_duelStarted)
                duel = "never started";
            else
            {
                until = Time.realtimeSinceStartup + 120f;
                float nextSwing = 0f;
                while (!_duelEnded && Time.realtimeSinceStartup < until)
                {
                    self = GameInstance.PlayingCharacterEntity;
                    target = FindPlayerByName(socialTargetName);
                    if (self == null || self.IsDead() || target == null)
                        break;
                    targetHpMin = Mathf.Min(targetHpMin, target.CurrentHp);
                    if (self.IsWeaponsSheathed && self.IsOwnerClient)
                        self.IsWeaponsSheathed = false;
                    float now = Time.realtimeSinceStartup;
                    float range = Mathf.Max(1f, self.GetAttackDistance(false) - 0.3f);
                    float distance = Vector3.Distance(self.EntityTransform.position, target.EntityTransform.position);
                    if (distance > range)
                    {
                        self.PointClickMovement(target.EntityTransform.position);
                    }
                    else if (now >= nextSwing)
                    {
                        nextSwing = now + 0.6f;
                        self.StopMove();
                        Vector3 toTarget = target.EntityTransform.position - self.EntityTransform.position; toTarget.y = 0f;
                        if (toTarget.sqrMagnitude > 0.001f) self.SetLookRotation(Quaternion.LookRotation(toTarget.normalized), true);
                        self.SetTargetEntity(target);
                        bool isLeftHand = false;
                        self.AimPosition = self.GetAttackAimPosition(ref isLeftHand, target.EntityTransform.position);
                        WeaponHandlingState whs = WeaponHandlingState.None;
                        if (self.Attack(ref whs)) swings++;
                    }
                    yield return null;
                }
                duel = _duelEnded ? $"ok (loser {_duelLoser}, {swings} swings, target hp {targetHpStart}->{targetHpMin})" : $"never ended ({swings} swings, target hp {targetHpStart}->{targetHpMin})";
            }
            Log($"social: duel {duel}");
            bool duelOk = _duelEnded && targetHpMin < targetHpStart;
            yield return new WaitForSecondsRealtime(2f);

            Finished = true;
            Passed = tradeOk && duelOk && partyOk;
            Log($"RESULT social: across={across} trade={trade} duel={duel} party={party} -> {(Passed ? "PASS" : "FAIL")}");
        }

        private static string BagSummaryShort(BasePlayerCharacterEntity self)
        {
            int filled = 0, amount = 0;
            for (int i = 0; i < self.NonEquipItems.Count; ++i)
            {
                if (self.NonEquipItems[i].IsEmptySlot()) continue;
                filled++;
                amount += self.NonEquipItems[i].amount;
            }
            return $"{filled} filled, amount {amount}";
        }
    }
}
