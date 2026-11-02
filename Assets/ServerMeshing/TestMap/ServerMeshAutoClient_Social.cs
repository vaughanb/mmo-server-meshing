// Server Meshing add-on — automated test client: trades, invitations, friends, a shop, a duel and a craft queue with a
// player across a line (DESIGN.md 1.44, 1.45, 1.46).
//
// social (the driver): walk to (walkTargetX, walkTargetZ), find the character named socialTargetName (or, without one,
// the nearest other player), then in order: trade one bag item and socialGold for whatever it offers, make a party and
// invite it, found a guild and invite it (when socialGuild), send it a friend request (when socialFriend), buy the one
// entry of the shop it opens (when socialBuy), duel it until the duel ends (it may die or leave), and last queue
// socialCraftAmount crafts of socialCraftFormula and walk across the line while they run (when socialCraftAmount > 0).
// The line the test is about is lineX when given (the harness's cut), else x = 0. The RESULT reads `RESULT social:
// across=<bool> trade=... party=... guild=... friend=... vend=... duel=... craft=...`, PASS when every step went through.
// socialAutoAccept (the other side, any mode): accept every trade request (offering its own first bag item and
// socialGold, lock, confirm after the other confirms), every duel request, every party or guild invitation and every
// friend request; with socialVend, open a shop of one bag item once it has joined the party and the guild.
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
        [Tooltip("social: found a guild and invite the target to it too (the kit asks the founder for gold, 1,000 in the demo's settings).")]
        public bool socialGuild = true;
        [Tooltip("With socialAutoAccept: quit this many seconds after a duel starts (0: stay), so the duel ends with this side gone, the way a disconnect ends it.")]
        public float socialLeaveDuelSeconds = 0f;
        public const string ARG_SOCIAL_LEAVE_DUEL = "-meshSocialLeaveDuel";
        [Tooltip("social: send the target a friend request and wait until it is a friend (the other side accepts with socialAutoAccept).")]
        public bool socialFriend = true;
        [Tooltip("social: wait for the target's shop and buy its first entry (the other side opens one with socialVend).")]
        public bool socialBuy = true;
        [Tooltip("With socialAutoAccept: open a shop of one bag item at socialVendPrice once the party and guild invitations are accepted.")]
        public bool socialVend = false;
        public const string ARG_SOCIAL_VEND = "-meshSocialVend";
        [Tooltip("The price of the shop's one entry.")]
        public int socialVendPrice = 7;
        [Tooltip("social: after the duel, queue this many crafts of socialCraftFormula and walk across the line while they run (0: no craft step).")]
        public int socialCraftAmount = 8;
        [Tooltip("social: the title of the craft formula to queue (the rig's database has 'Mesh Test Craft', one second and one gold a craft).")]
        public string socialCraftFormula = "Mesh Test Craft";
        private bool _friendPoller, _shopOpened;

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
            socialVend = socialVend || System.Array.IndexOf(args, ARG_SOCIAL_VEND) >= 0;
            string leave = ReadArg(args, ARG_SOCIAL_LEAVE_DUEL, null);
            if (!string.IsNullOrEmpty(leave) && float.TryParse(leave, NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds))
                socialLeaveDuelSeconds = seconds;
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
                if (autoAccept && socialLeaveDuelSeconds > 0f)
                    StartCoroutine(LeaveDuel(countDown + socialLeaveDuelSeconds));
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
                        (handler, code, response) => { _invitesAccepted++; Log($"social: party invitation accept -> {code} {response.message}"); MaybeOpenShop(); });
                };
                ClientGuildActions.onNotifyGuildInvitation += invitation =>
                {
                    Log($"social: guild invitation from {invitation.InviterName} (guild {invitation.GuildId}), accepting");
                    GameInstance.ClientGuildHandlers.RequestAcceptGuildInvitation(new RequestAcceptGuildInvitationMessage { guildId = invitation.GuildId, inviterId = invitation.InviterId },
                        (handler, code, response) => { _invitesAccepted++; Log($"social: guild invitation accept -> {code} {response.message}"); MaybeOpenShop(); });
                };
                if (!_friendPoller)
                {
                    _friendPoller = true;
                    StartCoroutine(AcceptFriendRequests());
                }
            }
        }

        /// <summary>With socialVend: once both invitations are accepted, a shop of one bag item (the driver buys it across the line).</summary>
        private void MaybeOpenShop()
        {
            if (!socialVend || _shopOpened || _invitesAccepted < 2)
                return;
            _shopOpened = true;
            StartCoroutine(OpenShop());
        }

        private IEnumerator OpenShop()
        {
            yield return new WaitForSecondsRealtime(2f);
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (self == null || self.VendingComponent == null)
                yield break;
            for (int i = 0; i < self.NonEquipItems.Count; ++i)
            {
                CharacterItem item = self.NonEquipItems[i];
                if (item.IsEmptySlot() || item.GetItem() == null || item.GetItem().RestrictDealing)
                    continue;
                StartVendingItems items = new StartVendingItems();
                items.Add(new StartVendingItem { id = item.id, amount = 1, price = socialVendPrice });
                Log($"social: opening a shop: {item.GetItem().Title} x1 for {socialVendPrice} gold");
                self.VendingComponent.CallCmdStartVending("MeshShop", items);
                yield break;
            }
            Log("social: no bag item to sell, no shop");
        }

        /// <summary>The auto-accepting side: every friend request that comes in is accepted (the kit's client asks for them; nothing is pushed).</summary>
        private IEnumerator AcceptFriendRequests()
        {
            HashSet<string> accepted = new HashSet<string>();
            while (true)
            {
                yield return new WaitForSecondsRealtime(3f);
                if (GameInstance.ClientFriendHandlers == null || GameInstance.PlayingCharacterEntity == null)
                    continue;
                GameInstance.ClientFriendHandlers.RequestGetFriendRequests(new RequestGetFriendRequestsMessage { skip = 0, limit = 20 }, (handler, code, response) =>
                {
                    if (response.friendRequests == null)
                        return;
                    foreach (SocialCharacterData requester in response.friendRequests)
                    {
                        if (string.IsNullOrEmpty(requester.id) || !accepted.Add(requester.id))
                            continue;
                        Log($"social: friend request from {requester.characterName}, accepting");
                        GameInstance.ClientFriendHandlers.RequestAcceptFriendRequest(new RequestAcceptFriendRequestMessage { requesterId = requester.id },
                            (h, c, r) => Log($"social: friend request accept -> {c} {r.message}"));
                    }
                });
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

        /// <summary>Quits the client mid-duel: the server ends the duel with this side as the loser, as on a disconnect.</summary>
        private IEnumerator LeaveDuel(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            Log($"social: leaving {seconds:0} s into the duel (quitting)");
            Application.Quit();
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

        /// <summary>The other player named <paramref name="name"/>, or with no name the nearest other player.</summary>
        private static BasePlayerCharacterEntity FindPlayerByName(string name)
        {
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity, nearest = null;
            float best = float.MaxValue;
            foreach (BasePlayerCharacterEntity entity in FindObjectsByType<BasePlayerCharacterEntity>(FindObjectsSortMode.None))
            {
                if (entity == null || entity.IsOwnerClient || string.IsNullOrEmpty(entity.CharacterName))
                    continue;
                if (!string.IsNullOrEmpty(name))
                {
                    if (string.Equals(entity.CharacterName, name))
                        return entity;
                    continue;
                }
                float distance = self != null ? Vector3.Distance(self.EntityTransform.position, entity.EntityTransform.position) : 0f;
                if (distance < best) { best = distance; nearest = entity; }
            }
            return nearest;
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
            float until = Time.realtimeSinceStartup + 90f; // the other client may log in after this one
            while (target == null && Time.realtimeSinceStartup < until)
            {
                target = FindPlayerByName(socialTargetName);
                yield return null;
            }
            if (target == null) { Fail(string.IsNullOrEmpty(socialTargetName) ? "no other player in sight" : $"no player named '{socialTargetName}' in sight"); yield break; }
            string targetName = target.CharacterName; // found by name from here on, whatever the field said
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            HookSocial(false);
            // The line the test is about: the harness's cut when it gave one, else x = 0 (the 8 km demo's start line).
            float line = hasLineX ? lineX : 0f;
            bool across = (target.EntityTransform.position.x < line) != (self.EntityTransform.position.x < line);
            Log($"social: target '{target.CharacterName}' id {target.Id} object {target.ObjectId} at {target.EntityTransform.position}, {Vector3.Distance(self.EntityTransform.position, target.EntityTransform.position):0.#} m away, {(across ? "across the line" : "on this side")}; my gold {self.Gold}, bag {BagSummaryShort(self)}");

            // 1. Trade.
            Phase = "social-trade";
            int goldBefore = self.Gold;
            string bagBefore = BagSummaryShort(self);
            string trade = "not started";
            _dealStarted = _dealEnded = false;
            // A request the other side never answered lapses after the kit's dealingRequestDuration (5 s in the demo): the
            // other client may still be finishing its login when this one first sees it. Ask again until it opens.
            until = Time.realtimeSinceStartup + 60f;
            float nextRequest = 0f;
            int requests = 0;
            while (!_dealEnded && Time.realtimeSinceStartup < until)
            {
                if (!_dealStarted && Time.realtimeSinceStartup >= nextRequest)
                {
                    target = FindPlayerByName(targetName);
                    if (target != null)
                    {
                        self.DealingComponent.CallCmdSendDealingRequest(target.ObjectId);
                        requests++;
                        Log($"social: trade request {requests} sent");
                    }
                    nextRequest = Time.realtimeSinceStartup + 8f;
                }
                yield return null;
            }
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
            target = FindPlayerByName(targetName);
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
            // 3. Guild: found one (a fresh name every run), invite the target, wait for it to join.
            string guild = "not asked";
            if (socialGuild)
            {
                Phase = "social-guild";
                self = GameInstance.PlayingCharacterEntity;
                target = FindPlayerByName(targetName);
                if (target == null)
                    guild = "the target left";
                else
                {
                    bool founded = GameInstance.JoinedGuild != null, answered = founded;
                    string guildName = "MeshG" + Random.Range(1000, 9999).ToString(CultureInfo.InvariantCulture);
                    if (!founded)
                        GameInstance.ClientGuildHandlers.RequestCreateGuild(new RequestCreateGuildMessage { guildName = guildName }, (handler, code, response) => { answered = true; founded = code == LiteNetLibManager.AckResponseCode.Success; Log($"social: create guild {guildName} -> {code} {response.message}"); });
                    until = Time.realtimeSinceStartup + 15f;
                    while (!answered && Time.realtimeSinceStartup < until) yield return null;
                    until = Time.realtimeSinceStartup + 10f;
                    while (founded && GameInstance.JoinedGuild == null && Time.realtimeSinceStartup < until) yield return null;
                    if (!founded || GameInstance.JoinedGuild == null)
                        guild = "no guild";
                    else
                    {
                        bool sent = false; answered = false;
                        string inviteeId = target.Id;
                        GameInstance.ClientGuildHandlers.RequestSendGuildInvitation(new RequestSendGuildInvitationMessage { inviteeId = inviteeId }, (handler, code, response) => { answered = true; sent = code == LiteNetLibManager.AckResponseCode.Success; Log($"social: guild invitation -> {code} {response.message}"); });
                        until = Time.realtimeSinceStartup + 15f;
                        while (!answered && Time.realtimeSinceStartup < until) yield return null;
                        if (!sent) guild = "invitation refused";
                        else
                        {
                            until = Time.realtimeSinceStartup + 30f;
                            bool joined = false;
                            while (!joined && Time.realtimeSinceStartup < until)
                            {
                                joined = GameInstance.JoinedGuild != null && GameInstance.JoinedGuild.IsMember(inviteeId);
                                yield return null;
                            }
                            guild = joined ? $"ok ({GameInstance.JoinedGuild.CountMember()} members)" : "invited, never joined";
                        }
                    }
                }
                Log($"social: guild {guild}");
                yield return new WaitForSecondsRealtime(2f);
            }
            bool guildOk = !socialGuild || guild.StartsWith("ok");

            // 4. Friend request: the kit writes it to the database by id; the target's client asks for it and accepts.
            string friend = "not asked";
            if (socialFriend)
            {
                Phase = "social-friend";
                yield return SendFriendRequest(targetName, r => friend = r);
                Log($"social: friend {friend}");
            }
            bool friendOk = !socialFriend || friend.StartsWith("ok");

            // 5. The target's shop: its sign, its items and a purchase, across the line.
            string vend = "not asked";
            if (socialBuy)
            {
                Phase = "social-vend";
                yield return BuyFromShop(targetName, r => vend = r);
                Log($"social: vend {vend}");
                yield return new WaitForSecondsRealtime(2f);
            }
            bool vendOk = !socialBuy || vend.StartsWith("ok");
            self = GameInstance.PlayingCharacterEntity;

            // 6. Duel: it may end with the target dead or gone.
            Phase = "social-duel";
            _duelStarted = _duelEnded = false;
            target = FindPlayerByName(targetName);
            if (target == null) { Fail($"{targetName} left before the duel"); yield break; }
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
                    target = FindPlayerByName(targetName);
                    if (self == null || self.IsDead())
                        break;
                    if (target == null)
                    {
                        yield return null; // gone (left or out of sight): the end may still be on its way
                        continue;
                    }
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

            // 7. A craft queue that runs across a hop: queue, let a few complete, walk over the line, let the rest complete.
            string craft = "not asked";
            if (socialCraftAmount > 0)
            {
                Phase = "social-craft";
                yield return CraftAcrossTheLine(line, r => craft = r);
                Log($"social: craft {craft}");
            }
            bool craftOk = socialCraftAmount <= 0 || craft.StartsWith("ok");

            Finished = true;
            Passed = tradeOk && duelOk && partyOk && guildOk && friendOk && vendOk && craftOk;
            Log($"RESULT social: across={across} trade={trade} party={party} guild={guild} friend={friend} vend={vend} duel={duel} craft={craft} -> {(Passed ? "PASS" : "FAIL")}");
        }

        private IEnumerator SendFriendRequest(string targetName, System.Action<string> done)
        {
            BasePlayerCharacterEntity target = FindPlayerByName(targetName);
            if (target == null) { done("the target left"); yield break; }
            string friendId = target.Id;
            bool answered = false, sent = false;
            GameInstance.ClientFriendHandlers.RequestSendFriendRequest(new RequestSendFriendRequestMessage { requesteeId = friendId },
                (handler, code, response) => { answered = true; sent = code == LiteNetLibManager.AckResponseCode.Success; Log($"social: friend request -> {code} {response.message}"); });
            float until = Time.realtimeSinceStartup + 15f;
            while (!answered && Time.realtimeSinceStartup < until) yield return null;
            if (!sent) { done("request refused"); yield break; }
            bool isFriend = false;
            float nextAsk = 0f;
            until = Time.realtimeSinceStartup + 40f;
            while (!isFriend && Time.realtimeSinceStartup < until)
            {
                if (Time.realtimeSinceStartup >= nextAsk)
                {
                    nextAsk = Time.realtimeSinceStartup + 3f;
                    GameInstance.ClientFriendHandlers.RequestGetFriends(new RequestGetFriendsMessage { skip = 0, limit = 50 }, (handler, code, response) =>
                    {
                        if (response.friends == null) return;
                        foreach (SocialCharacterData f in response.friends)
                            if (f.id == friendId) isFriend = true;
                    });
                }
                yield return null;
            }
            done(isFriend ? "ok" : "requested, never accepted");
        }

        private IEnumerator BuyFromShop(string targetName, System.Action<string> done)
        {
            BasePlayerCharacterEntity target = FindPlayerByName(targetName);
            float until = Time.realtimeSinceStartup + 30f;
            while (target != null && !(target.VendingComponent != null && target.VendingComponent.Data.isStarted) && Time.realtimeSinceStartup < until)
            {
                yield return null;
                target = FindPlayerByName(targetName);
            }
            if (target == null) { done("the target left"); yield break; }
            PlayerCharacterVendingComponent shop = target.VendingComponent;
            if (shop == null || !shop.Data.isStarted) { done("no shop opened"); yield break; }
            Log($"social: {target.CharacterName}'s shop '{shop.Data.title}' is open");
            VendingItems seen = null;
            System.Action<VendingItems> onItems = items => { seen = items; Log($"social: the shop lists {items.Count} item(s)"); };
            shop.onUpdateItems += onItems;
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            self.VendingComponent.CallCmdSubscribe(target.ObjectId);
            until = Time.realtimeSinceStartup + 15f;
            while ((seen == null || seen.Count == 0) && Time.realtimeSinceStartup < until) yield return null;
            string result;
            if (seen == null || seen.Count == 0)
                result = "no items reached the client";
            else
            {
                int price = seen[0].price, goldBefore = self.Gold, bagBefore = BagAmount(self);
                self.VendingComponent.CallCmdBuyItem(0);
                until = Time.realtimeSinceStartup + 15f;
                while (self != null && self.Gold > goldBefore - price && Time.realtimeSinceStartup < until)
                {
                    yield return null;
                    self = GameInstance.PlayingCharacterEntity;
                }
                yield return new WaitForSecondsRealtime(1.5f);
                self = GameInstance.PlayingCharacterEntity;
                until = Time.realtimeSinceStartup + 5f;
                while (shop != null && shop.Data.isStarted && Time.realtimeSinceStartup < until) yield return null;
                bool closed = shop == null || !shop.Data.isStarted;
                int bagAfter = BagAmount(self);
                string detail = $"{price} gold, gold {goldBefore}->{self.Gold}, bag amount {bagBefore}->{bagAfter}, {(closed ? "sold out and closed" : "still open")}";
                result = self.Gold == goldBefore - price && bagAfter > bagBefore && closed ? $"ok ({detail})" : detail;
            }
            if (shop != null) shop.onUpdateItems -= onItems;
            self = GameInstance.PlayingCharacterEntity;
            if (self != null && self.VendingComponent != null) self.VendingComponent.CallCmdUnsubscribe();
            done(result);
        }

        private IEnumerator CraftAcrossTheLine(float line, System.Action<string> done)
        {
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            ItemCraftFormula formula = null;
            foreach (ItemCraftFormula f in GameInstance.ItemCraftFormulas.Values)
                if (f != null && f.Title == socialCraftFormula) { formula = f; break; }
            if (self == null || formula == null || formula.ItemCraft.CraftingItem == null) { done($"no formula '{socialCraftFormula}'"); yield break; }
            int itemId = formula.ItemCraft.CraftingItem.DataId, per = formula.ItemCraft.Amount, goldPer = formula.ItemCraft.RequireGold;
            int before = CountItem(self, itemId), goldBefore = self.Gold, hopsBefore = ClientMigrationController.CompletedCount;
            self.CallCmdAppendCraftingQueueItem(self.ObjectId, formula.DataId, socialCraftAmount);
            Log($"social: queued {socialCraftAmount} x '{formula.Title}' ({per} x item {itemId} and {goldPer} gold each); have {before}, gold {goldBefore}");
            float until = Time.realtimeSinceStartup + 15f;
            while (CountItem(GameInstance.PlayingCharacterEntity, itemId) < before + 2 * per && Time.realtimeSinceStartup < until) yield return null;
            self = GameInstance.PlayingCharacterEntity;
            int doneBeforeHop = (CountItem(self, itemId) - before) / per;
            int leftAtHop = self.CraftingComponent != null && self.CraftingComponent.QueueItems.Count > 0 ? self.CraftingComponent.QueueItems[0].amount : 0;
            float targetX = self.EntityTransform.position.x < line ? line + 3f : line - 3f;
            Log($"social: {doneBeforeHop} crafted here, {leftAtHop} queued; walking over the line to x={targetX:0.#}");
            bool reached = false;
            _walkZOverride = self.EntityTransform.position.z;
            yield return WalkTo(targetX, Time.realtimeSinceStartup + 30f, hopsBefore, r => reached = r);
            _walkZOverride = null;
            // The client counts a hop once its new character is ready, which can come after the walk reached its mark.
            until = Time.realtimeSinceStartup + 10f;
            while (ClientMigrationController.CompletedCount <= hopsBefore && Time.realtimeSinceStartup < until) yield return null;
            bool hopped = ClientMigrationController.CompletedCount > hopsBefore;
            until = Time.realtimeSinceStartup + socialCraftAmount * 2f + 20f;
            while (Time.realtimeSinceStartup < until)
            {
                self = GameInstance.PlayingCharacterEntity;
                if (self != null && CountItem(self, itemId) >= before + socialCraftAmount * per)
                    break;
                yield return null;
            }
            yield return new WaitForSecondsRealtime(3f); // and nothing more after the queue ran out
            self = GameInstance.PlayingCharacterEntity;
            int crafted = (CountItem(self, itemId) - before) / per;
            int queueLeft = self.CraftingComponent != null ? self.CraftingComponent.QueueItems.Count : -1;
            string detail = $"{crafted} of {socialCraftAmount} crafted ({doneBeforeHop} before the hop, {leftAtHop} queued at it), gold {goldBefore}->{self.Gold}, hopped {hopped}, {queueLeft} left queued";
            bool ok = crafted == socialCraftAmount && hopped && leftAtHop > 0 && goldBefore - self.Gold == socialCraftAmount * goldPer && queueLeft == 0;
            done(ok ? $"ok ({detail})" : detail);
        }

        private static int CountItem(BasePlayerCharacterEntity self, int dataId)
        {
            if (self == null) return 0;
            int amount = 0;
            for (int i = 0; i < self.NonEquipItems.Count; ++i)
                if (!self.NonEquipItems[i].IsEmptySlot() && self.NonEquipItems[i].dataId == dataId)
                    amount += self.NonEquipItems[i].amount;
            return amount;
        }

        private static int BagAmount(BasePlayerCharacterEntity self)
        {
            if (self == null) return 0;
            int amount = 0;
            for (int i = 0; i < self.NonEquipItems.Count; ++i)
                if (!self.NonEquipItems[i].IsEmptySlot())
                    amount += self.NonEquipItems[i].amount;
            return amount;
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
