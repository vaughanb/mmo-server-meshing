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
        public Vector2 socialBenchOffset = new Vector2(2f, 1f);
        private bool _standing;

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
            Finished = true;
            Passed = ok && results.Count > 0;
            Log($"RESULT social: across={across} {string.Join(" ", results)} -> {(Passed ? "PASS" : "FAIL")}");
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
