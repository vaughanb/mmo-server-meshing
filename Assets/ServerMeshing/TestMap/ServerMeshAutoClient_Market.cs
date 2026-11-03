// Server Meshing add-on — automated test client: a shop and a workbench across a line, past what the social driver's
// own steps reach (DESIGN.md 1.47).
//
// social with socialSteps (-meshSocialSteps, comma-separated) walks to (walkTargetX, walkTargetZ), finds the target as
// the social driver does, and runs only the steps named, in this order:
//   vend     buy the first entry of the target's shop (the social driver's step; the shop may stay open after it)
//   vendhop  twice: buy the first entry and walk across the line at once, then back (the harness delays the vendor's
//            cell's answers, so the hop is held while the purchase is on its way, and the second answer comes late)
//   bench    place a TestBench-CE (a public crafting queue, 5 m reach) on this side of the line, queue socialCraftAmount
//            crafts of socialCraftFormula on it, and walk across within its reach while they run; then walk back and
//            destroy the bench (a building is saved: one left standing blocked the next run's walks there)
//   buyrefund  buy the first entry from this side and stay: no item comes (the harness drops the vendor's cell's
//            answer) and the gold goes back (DESIGN 1.48)
//   buy      buy the first entry from this side and stay: the item comes (the harness may drop this cell's word to the
//            vendor's cell, which then asks)
//   buyquit  last: log the RESULT, buy the first entry and quit at once, while the answer is on its way
//   trade, tradefail  in the order named, each a trade with the target (one of the first dealable bag stack and
//            socialGold each way; the target accepts with socialAutoAccept): `trade` expects the exchange, `tradefail`
//            none (the harness drops the commit, DESIGN 1.49). With socialQuitOnTrade N the accepting side quits once
//            both have confirmed its N-th trade, while the exchange is on its way.
//   quit     last: log the RESULT and quit (the kit saves the character as it leaves)
// The RESULT reads `RESULT social: across=<bool> <step>=<result> ... -> PASS`, PASS when every step named went through.
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshAutoClient
    {
        [Tooltip("social: run only these steps (comma-separated: vend, vendhop, bench); empty runs the social driver's own.")]
        public string socialSteps = "";
        public const string ARG_SOCIAL_STEPS = "-meshSocialSteps";
        [Tooltip("With socialVend: open the shop as soon as this client stands (stand mode) or is in game, not after the invitations.")]
        public bool socialVendNow = false;
        public const string ARG_SOCIAL_VEND_NOW = "-meshSocialVendNow";
        [Tooltip("With socialVend: the shop's entries, one of each of the first bag stacks that may be dealt.")]
        public int socialVendCount = 1;
        public const string ARG_SOCIAL_VEND_COUNT = "-meshSocialVendCount";
        [Tooltip("bench: where the bench goes, metres from the line on this side, and along z from where this client stands.")]
        public Vector2 socialBenchOffset = new Vector2(1f, 1.5f); // 1 m: a walk aimed 3 m past the line can stop at 3 m, 4 m from it
        private bool _standing;
        [Tooltip("With socialAutoAccept: quit 1.5 s after both sides confirmed this trade (its number, 1 first; 0: never).")]
        public int socialQuitOnTrade = 0;
        public const string ARG_SOCIAL_QUIT_ON_TRADE = "-meshSocialQuitOnTrade";
        private int _myConfirms; // trades this side confirmed (a trade can send two confirms: count it once)
        private bool _quittingOnTrade, _confirmedThisTrade;

        /// <summary>socialQuitOnTrade: called on this side's confirm and on the other's; quits once both confirmed the N-th trade.</summary>
        private void MaybeQuitOnTrade()
        {
            if (socialQuitOnTrade <= 0 || _quittingOnTrade || _myConfirms != socialQuitOnTrade || _otherDealState != DealingState.ConfirmDealing)
                return;
            _quittingOnTrade = true;
            StartCoroutine(QuitOnTrade());
        }

        private IEnumerator QuitOnTrade()
        {
            yield return new WaitForSecondsRealtime(1.5f);
            Log($"social: both confirmed trade {_myConfirms}; quitting with the exchange on its way");
            Application.Quit();
        }

        private bool HasSocialStep(string step)
        {
            foreach (string s in socialSteps.Split(','))
                if (string.Equals(s.Trim(), step, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private IEnumerator RunMarket(float deadline)
        {
            Phase = "market-walk";
            _walkZOverride = walkTargetZ;
            bool reached = false;
            yield return WalkTo(walkTargetX, deadline, ClientMigrationController.CompletedCount, r => reached = r);
            _walkZOverride = null;
            if (!reached) { Fail("never reached the social position"); yield break; }
            Phase = "market-find";
            BasePlayerCharacterEntity target = null;
            float until = Time.realtimeSinceStartup + 90f;
            while (target == null && Time.realtimeSinceStartup < until)
            {
                target = FindPlayerByName(socialTargetName);
                yield return null;
            }
            if (target == null) { Fail(string.IsNullOrEmpty(socialTargetName) ? "no other player in sight" : $"no player named '{socialTargetName}' in sight"); yield break; }
            string targetName = target.CharacterName;
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            HookSocial(false);
            float line = hasLineX ? lineX : 0f;
            bool across = (target.EntityTransform.position.x < line) != (self.EntityTransform.position.x < line);
            Vector3 home = self.EntityTransform.position;
            Log($"market: target '{targetName}' at {target.EntityTransform.position}, {(across ? "across the line" : "on this side")}; my gold {self.Gold}, bag {BagSummaryShort(self)}; steps {socialSteps}");

            List<string> results = new List<string>();
            bool ok = true;
            if (HasSocialStep("vend"))
            {
                Phase = "market-vend";
                string vend = "";
                yield return BuyFromShop(targetName, r => vend = r);
                Log($"market: vend {vend}");
                results.Add($"vend={vend}");
                ok &= vend.StartsWith("ok");
            }
            if (HasSocialStep("vendhop"))
            {
                for (int round = 1; round <= 2; ++round)
                {
                    Phase = $"market-vendhop{round}";
                    string hop = "";
                    yield return BuyAndCross(targetName, line, home, r => hop = r);
                    Log($"market: vendhop {round} {hop}");
                    results.Add($"vendhop{round}={hop}");
                    ok &= hop.StartsWith("ok");
                }
            }
            if (HasSocialStep("bench"))
            {
                Phase = "market-bench";
                string bench = "";
                yield return CraftAtBench(line, home, r => bench = r);
                Log($"market: bench {bench}");
                results.Add($"bench={bench}");
                ok &= bench.StartsWith("ok");
            }
            foreach (string step in new[] { "buyrefund", "buy" })
            {
                if (!HasSocialStep(step))
                    continue;
                Phase = $"market-{step}";
                string r = "";
                yield return BuyAndStay(targetName, line, home, step == "buy", s => r = s);
                Log($"market: {step} {r}");
                results.Add($"{step}={r}");
                ok &= r.StartsWith("ok");
            }
            int trades = 0;
            foreach (string part in socialSteps.Split(','))
            {
                string step = part.Trim().ToLowerInvariant();
                if (step != "trade" && step != "tradefail")
                    continue;
                trades++;
                Phase = $"market-{step}{trades}";
                string r = "";
                yield return TradeOnce(targetName, step == "trade", s => r = s);
                Log($"market: {step} {trades} {r}");
                results.Add($"{step}{trades}={r}");
                ok &= r.StartsWith("ok");
            }
            bool quit = HasSocialStep("buyquit");
            if (quit)
                results.Add("buyquit=leaving");
            Finished = true;
            Passed = ok && results.Count > 0;
            Log($"RESULT social: across={across} {string.Join(" ", results)} -> {(Passed ? "PASS" : "FAIL")}");
            if (quit)
            {
                Phase = "market-buyquit";
                yield return BuyAndQuit(targetName, line, home);
            }
            else if (HasSocialStep("quit"))
            {
                yield return new WaitForSecondsRealtime(1f);
                Log("market: quitting");
                Application.Quit();
            }
        }

        /// <summary>Back home, the target's shop open and its items listed: the shop, or null with `why` set.</summary>
        private IEnumerator OpenTargetShop(string targetName, float line, Vector3 home, System.Action<PlayerCharacterVendingComponent, VendingItems, string> done)
        {
            bool atHome = false;
            yield return WalkHome(home, line, r => atHome = r);
            if (!atHome) { done(null, null, "never got back to its side of the line"); yield break; }
            BasePlayerCharacterEntity target = null;
            float until = Time.realtimeSinceStartup + 20f;
            while ((target == null || target.VendingComponent == null || !target.VendingComponent.Data.isStarted) && Time.realtimeSinceStartup < until)
            {
                yield return null;
                target = FindPlayerByName(targetName);
            }
            if (target == null || !target.VendingComponent.Data.isStarted) { done(null, null, target == null ? "the target is not in sight" : "no shop open"); yield break; }
            PlayerCharacterVendingComponent shop = target.VendingComponent;
            VendingItems seen = null;
            System.Action<VendingItems> onItems = items => seen = items;
            shop.onUpdateItems += onItems;
            GameInstance.PlayingCharacterEntity.VendingComponent.CallCmdSubscribe(target.ObjectId);
            until = Time.realtimeSinceStartup + 15f;
            while ((seen == null || seen.Count == 0) && Time.realtimeSinceStartup < until) yield return null;
            shop.onUpdateItems -= onItems;
            done(shop, seen, seen == null || seen.Count == 0 ? "no items reached the client" : null);
        }

        /// <summary>
        /// Buys the first entry from this side of the line and stays: with `expectItem` the item comes and the price is
        /// paid; without, no item comes and the gold goes back (the escrow's 10 s and a few more).
        /// </summary>
        private IEnumerator BuyAndStay(string targetName, float line, Vector3 home, bool expectItem, System.Action<string> done)
        {
            PlayerCharacterVendingComponent shop = null;
            VendingItems seen = null;
            string why = null;
            yield return OpenTargetShop(targetName, line, home, (s, i, w) => { shop = s; seen = i; why = w; });
            if (why != null) { done(why); yield break; }
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            int price = seen[0].price, goldBefore = self.Gold, bagBefore = BagAmount(self), lowest = goldBefore;
            self.VendingComponent.CallCmdBuyItem(0);
            Log($"market: bought entry 0 ({price} gold, {seen.Count} listed); staying");
            float until = Time.realtimeSinceStartup + (expectItem ? 15f : 14f);
            while (Time.realtimeSinceStartup < until)
            {
                self = GameInstance.PlayingCharacterEntity;
                if (self != null) lowest = Mathf.Min(lowest, self.Gold);
                if (expectItem && BagAmount(self) > bagBefore)
                    break;
                yield return null;
            }
            yield return new WaitForSecondsRealtime(2f);
            self = GameInstance.PlayingCharacterEntity;
            int bagAfter = BagAmount(self);
            if (self != null && self.VendingComponent != null) self.VendingComponent.CallCmdUnsubscribe();
            string detail = $"{price} gold, gold {goldBefore}->{(self != null ? self.Gold : -1)} (lowest {lowest}), bag amount {bagBefore}->{bagAfter}";
            bool ok = self != null && (expectItem
                ? self.Gold == goldBefore - price && bagAfter == bagBefore + 1
                : self.Gold == goldBefore && bagAfter == bagBefore && lowest == goldBefore - price);
            done(ok ? $"ok ({detail})" : detail);
        }

        private static Dictionary<int, int> BagCounts(BasePlayerCharacterEntity self)
        {
            Dictionary<int, int> counts = new Dictionary<int, int>();
            if (self == null)
                return counts;
            foreach (CharacterItem item in self.NonEquipItems)
            {
                if (item.IsEmptySlot())
                    continue;
                counts.TryGetValue(item.dataId, out int n);
                counts[item.dataId] = n + item.amount;
            }
            return counts;
        }

        private static bool SameCounts(Dictionary<int, int> a, Dictionary<int, int> b)
        {
            if (a.Count != b.Count)
                return false;
            foreach (KeyValuePair<int, int> kv in a)
                if (!b.TryGetValue(kv.Key, out int n) || n != kv.Value)
                    return false;
            return true;
        }

        /// <summary>
        /// One trade with the target (it accepts with socialAutoAccept; HookSocial's handlers offer, lock and confirm):
        /// `expectExchange` wants the bag changed, else unchanged (an exchange that failed gives everything back).
        /// </summary>
        private IEnumerator TradeOnce(string targetName, bool expectExchange, System.Action<string> done)
        {
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            Dictionary<int, int> before = BagCounts(self);
            int goldBefore = self != null ? self.Gold : 0;
            _dealStarted = _dealEnded = false;
            _myDealState = _otherDealState = DealingState.None;
            float until = Time.realtimeSinceStartup + 60f, nextRequest = 0f;
            while (!_dealEnded && Time.realtimeSinceStartup < until)
            {
                if (!_dealStarted && Time.realtimeSinceStartup >= nextRequest)
                {
                    BasePlayerCharacterEntity target = FindPlayerByName(targetName);
                    if (target != null)
                        GameInstance.PlayingCharacterEntity.DealingComponent.CallCmdSendDealingRequest(target.ObjectId);
                    nextRequest = Time.realtimeSinceStartup + 8f;
                }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(2f);
            self = GameInstance.PlayingCharacterEntity;
            Dictionary<int, int> after = BagCounts(self);
            bool changed = !SameCounts(before, after);
            string detail = $"{(_dealStarted ? (_dealEnded ? "ended" : $"stuck at mine={_myDealState} other={_otherDealState}") : "never opened")}, bag {(changed ? "changed" : "unchanged")}, gold {goldBefore}->{(self != null ? self.Gold : -1)}";
            bool ok = _dealStarted && _dealEnded && changed == expectExchange && (expectExchange || self.Gold == goldBefore);
            done(ok ? $"ok ({detail})" : detail);
        }

        /// <summary>Buys the first entry and quits once the price is in escrow, while the answer is on its way.</summary>
        private IEnumerator BuyAndQuit(string targetName, float line, Vector3 home)
        {
            PlayerCharacterVendingComponent shop = null;
            VendingItems seen = null;
            string why = null;
            yield return OpenTargetShop(targetName, line, home, (s, i, w) => { shop = s; seen = i; why = w; });
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (why == null && self != null)
            {
                int goldBefore = self.Gold;
                self.VendingComponent.CallCmdBuyItem(0);
                float until = Time.realtimeSinceStartup + 5f;
                while (GameInstance.PlayingCharacterEntity != null && GameInstance.PlayingCharacterEntity.Gold >= goldBefore && Time.realtimeSinceStartup < until)
                    yield return null;
                Log($"market: bought entry 0 ({seen[0].price} gold, gold {goldBefore}->{(GameInstance.PlayingCharacterEntity != null ? GameInstance.PlayingCharacterEntity.Gold : -1)}); quitting at once");
            }
            else
                Log($"market: buyquit found no shop ({why}); quitting");
            Application.Quit();
        }

        /// <summary>
        /// Back to `home`'s side of the line when the last step left this client across it, aimed at least 3.5 m past the
        /// line: WalkTo stops within 1.5 m of its mark, and a hop needs 1 m past the line (the hysteresis); true when it
        /// is there.
        /// </summary>
        private IEnumerator WalkHome(Vector3 home, float line, System.Action<bool> done)
        {
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (self == null) { done(false); yield break; }
            if ((self.EntityTransform.position.x < line) == (home.x < line)) { done(true); yield break; }
            float homeX = line + Mathf.Sign(home.x - line) * Mathf.Max(Mathf.Abs(home.x - line), 3.5f);
            int hopsBefore = ClientMigrationController.CompletedCount;
            bool reached = false;
            _walkZOverride = home.z;
            yield return WalkTo(homeX, Time.realtimeSinceStartup + 40f, hopsBefore, r => reached = r);
            _walkZOverride = null;
            float until = Time.realtimeSinceStartup + 15f;
            while (GameInstance.PlayingCharacterEntity != null && (GameInstance.PlayingCharacterEntity.EntityTransform.position.x < line) != (home.x < line) && Time.realtimeSinceStartup < until)
                yield return null;
            yield return new WaitForSecondsRealtime(3f); // the new character's ghosts (the target among them) arrive
            done(reached);
        }

        /// <summary>
        /// Buys the target's first entry and walks across the line at once; waits for the hop and for the item, then for
        /// the gold to settle (a late answer takes the price again after the escrow went back).
        /// </summary>
        private IEnumerator BuyAndCross(string targetName, float line, Vector3 home, System.Action<string> done)
        {
            bool atHome = false;
            yield return WalkHome(home, line, r => atHome = r);
            if (!atHome) { done("never got back to its side of the line"); yield break; }
            BasePlayerCharacterEntity target = null;
            float until = Time.realtimeSinceStartup + 20f;
            while ((target == null || target.VendingComponent == null || !target.VendingComponent.Data.isStarted) && Time.realtimeSinceStartup < until)
            {
                yield return null;
                target = FindPlayerByName(targetName);
            }
            if (target == null || !target.VendingComponent.Data.isStarted) { done(target == null ? "the target is not in sight" : "no shop open"); yield break; }
            PlayerCharacterVendingComponent shop = target.VendingComponent;
            VendingItems seen = null;
            System.Action<VendingItems> onItems = items => seen = items;
            shop.onUpdateItems += onItems;
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            self.VendingComponent.CallCmdSubscribe(target.ObjectId);
            until = Time.realtimeSinceStartup + 15f;
            while ((seen == null || seen.Count == 0) && Time.realtimeSinceStartup < until) yield return null;
            shop.onUpdateItems -= onItems;
            if (seen == null || seen.Count == 0) { done("no items reached the client"); yield break; }
            int price = seen[0].price, goldBefore = self.Gold, bagBefore = BagAmount(self), hopsBefore = ClientMigrationController.CompletedCount;
            float boughtAt = Time.realtimeSinceStartup;
            self.VendingComponent.CallCmdBuyItem(0);
            float targetX = self.EntityTransform.position.x < line ? line + 3f : line - 3f;
            Log($"market: bought entry 0 ({price} gold, {seen.Count} listed); walking over the line to x={targetX:0.#} at once");
            bool reached = false;
            _walkZOverride = self.EntityTransform.position.z;
            yield return WalkTo(targetX, Time.realtimeSinceStartup + 40f, hopsBefore, r => reached = r);
            _walkZOverride = null;
            until = Time.realtimeSinceStartup + 25f;
            while (ClientMigrationController.CompletedCount <= hopsBefore && Time.realtimeSinceStartup < until) yield return null;
            bool hopped = ClientMigrationController.CompletedCount > hopsBefore;
            float hopAfter = Time.realtimeSinceStartup - boughtAt;
            until = Time.realtimeSinceStartup + 25f;
            while (BagAmount(GameInstance.PlayingCharacterEntity) <= bagBefore && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSecondsRealtime(5f); // a late answer's second charge, and nothing more after it
            self = GameInstance.PlayingCharacterEntity;
            int bagAfter = BagAmount(self);
            if (self != null && self.VendingComponent != null) self.VendingComponent.CallCmdUnsubscribe();
            string detail = $"{price} gold, gold {goldBefore}->{(self != null ? self.Gold : -1)}, bag amount {bagBefore}->{bagAfter}, hopped {hopped} {hopAfter:0.#} s after the purchase";
            done(self != null && hopped && self.Gold == goldBefore - price && bagAfter == bagBefore + 1 ? $"ok ({detail})" : detail);
        }

        /// <summary>
        /// Places a TestBench-CE beside this client on its side of the line, queues the crafts on it, lets two complete,
        /// walks across within its reach and waits for the rest (crafted on the other cell's copy of the bench).
        /// </summary>
        private IEnumerator CraftAtBench(float line, Vector3 home, System.Action<string> done)
        {
            bool atHome = false;
            yield return WalkHome(home, line, r => atHome = r);
            if (!atHome) { done("never got back to its side of the line"); yield break; }
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            ItemCraftFormula formula = null;
            foreach (ItemCraftFormula f in GameInstance.ItemCraftFormulas.Values)
                if (f != null && f.Title == socialCraftFormula) { formula = f; break; }
            if (self == null || formula == null || formula.ItemCraft.CraftingItem == null) { done($"no formula '{socialCraftFormula}'"); yield break; }
            float side = self.EntityTransform.position.x < line ? -1f : 1f;
            Vector3 place = new Vector3(line + side * socialBenchOffset.x, self.EntityTransform.position.y, self.EntityTransform.position.z + socialBenchOffset.y);
            BuildingEntity built = null;
            yield return PlaceBuilding("bench", b => b is QueuedWorkbenchEntity, place, 0, b => built = b);
            if (!(built is QueuedWorkbenchEntity bench)) { done("the bench never appeared"); yield break; }
            string benchId = bench.Id;
            int itemId = formula.ItemCraft.CraftingItem.DataId, per = formula.ItemCraft.Amount, goldPer = formula.ItemCraft.RequireGold;
            self = GameInstance.PlayingCharacterEntity;
            int before = CountItem(self, itemId), goldBefore = self.Gold, hopsBefore = ClientMigrationController.CompletedCount;
            self.CallCmdAppendCraftingQueueItem(bench.ObjectId, formula.DataId, socialCraftAmount);
            Log($"market: bench {benchId} at {bench.EntityTransform.position}; queued {socialCraftAmount} x '{formula.Title}' on it; have {before}, gold {goldBefore}");
            float until = Time.realtimeSinceStartup + 15f;
            while (CountItem(GameInstance.PlayingCharacterEntity, itemId) < before + 2 * per && Time.realtimeSinceStartup < until) yield return null;
            self = GameInstance.PlayingCharacterEntity;
            int doneBeforeHop = (CountItem(self, itemId) - before) / per;
            int leftAtHop = bench != null && bench.QueueItems.Count > 0 ? bench.QueueItems[0].amount : 0;
            // Across the line and within the bench's reach (it stands socialBenchOffset.x on this side): aimed 3 m past the
            // line, WalkTo stops 1.5-3 m past it.
            float targetX = line - side * 3f;
            Log($"market: {doneBeforeHop} crafted here, {leftAtHop} queued on the bench; walking over the line to x={targetX:0.#}");
            bool reached = false;
            _walkZOverride = self.EntityTransform.position.z;
            yield return WalkTo(targetX, Time.realtimeSinceStartup + 30f, hopsBefore, r => reached = r);
            _walkZOverride = null;
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
            yield return new WaitForSecondsRealtime(3f);
            self = GameInstance.PlayingCharacterEntity;
            int crafted = (CountItem(self, itemId) - before) / per;
            QueuedWorkbenchEntity seenBench = FindBuildingById(benchId) as QueuedWorkbenchEntity;
            int benchLeft = seenBench != null ? seenBench.QueueItems.Count : -1;
            float reach = seenBench != null && self != null ? Vector3.Distance(self.EntityTransform.position, seenBench.EntityTransform.position) : -1f;
            string detail = $"{crafted} of {socialCraftAmount} crafted ({doneBeforeHop} before the hop, {leftAtHop} queued at it), gold {goldBefore}->{self.Gold}, hopped {hopped}, {benchLeft} left on the bench, {reach:0.#} m from it";
            bool ok = crafted == socialCraftAmount && hopped && leftAtHop > 0 && goldBefore - self.Gold == socialCraftAmount * goldPer && benchLeft <= 0;

            // Back to the bench's cell and destroy it.
            yield return WalkHome(home, line, r => atHome = r);
            seenBench = FindBuildingById(benchId) as QueuedWorkbenchEntity;
            self = GameInstance.PlayingCharacterEntity;
            bool removed = false;
            if (atHome && seenBench != null && self != null)
            {
                self.BuildingComponent.CallCmdDestroyBuilding(seenBench.ObjectId);
                until = Time.realtimeSinceStartup + 10f;
                while (FindBuildingById(benchId) != null && Time.realtimeSinceStartup < until) yield return null;
                removed = FindBuildingById(benchId) == null;
            }
            detail += removed ? ", bench destroyed" : ", the bench is still standing";
            done(ok && removed ? $"ok ({detail})" : detail);
        }
    }
}
