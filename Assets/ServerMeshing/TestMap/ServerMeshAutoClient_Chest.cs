// Server Meshing test rig — chest: a chest open across a line (DESIGN 1.57).
//
// chest places a storage chest 1 m short of the line (lineX, else the observe line) and 1.5 m off the lane, opens it and puts a
// scroll in; walks to 2.5 m past the line, inside the chest's reach, and stands: the cell holds the handoff while the chest
// is open, so no migration comes and the window stays; moves scrolls in and out while held; closes the chest, and the hop
// follows; tries the chest's puppet on the new cell (refused: a chest is served by its own cell); walks back, reopens it and
// counts what is in it; then walks 8 m past the line, out of reach, where the chest closes as the kit closes it and the
// hop follows; back on its cell it destroys the chest (buildings persist between the rig's scenarios). Each step is
// logged as `chest: ...`; the RESULT reads `RESULT chest: ...` with the counts a harness grades.
using System.Collections;
using System.Globalization;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshAutoClient
    {
        /// <summary>ChestHoldSeconds is how long the tester stands past the line with the chest open before it moves anything.</summary>
        public const float ChestHoldSeconds = 6f;

        private IEnumerator RunChest(float deadline)
        {
            Phase = "chest";
            float line = hasLineX ? lineX : observeGhostBeyondX, z = walkTargetZ;
            Vector3 chestAt = new Vector3(line - 1f, 0f, z + 1.5f);
            yield return WalkNear(line - 2.5f, z, deadline);
            StorageEntity chest = null;
            yield return PlaceBuilding("chest", b => b is StorageEntity, chestAt, 0, b => chest = b as StorageEntity);
            if (chest == null) { Fail("chest: the chest never appeared"); yield break; }
            string chestId = chest.Id;
            Log($"chest: placed {chestId} at {chest.EntityTransform.position} reach={chest.GetActivatableDistance():0.#}");

            // 1. Open it on its own cell and put a scroll in.
            bool opened = false;
            yield return OpenStorageAndRead(chest, "on its cell", (ok, scrolls) => opened = ok);
            if (!opened) { Fail("chest: the first open failed"); yield break; }
            _expectedStorage = 0;
            yield return MoveScroll(chestId, true, ++_moveTurn);

            // 2. Past the line with it open, inside its reach: held, the window stays.
            int before = ClientMigrationController.CompletedCount;
            Vector3 stop = Vector3.zero;
            bool reached = false;
            yield return RideTo(line + 2.5f, deadline, (r, at) => { reached = r; stop = at; });
            float holdEnd = Time.realtimeSinceStartup + ChestHoldSeconds;
            while (Time.realtimeSinceStartup < holdEnd)
                yield return null;
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            bool held = reached && ClientMigrationController.CompletedCount == before && _storageOpenSeen;
            Log($"chest: past the line at {stop} for {ChestHoldSeconds:0} s: migrations={ClientMigrationController.CompletedCount - before} windowOpen={_storageOpenSeen} dist={(self != null && chest != null ? Vector3.Distance(self.EntityTransform.position, chest.EntityTransform.position) : -1f):0.0} -> {(held ? "held" : "NOT held")}");

            // 3. Moves while held: in, out, in.
            int okBefore = _movesOk;
            yield return MoveScroll(chestId, true, ++_moveTurn);
            yield return MoveScroll(chestId, false, ++_moveTurn);
            yield return MoveScroll(chestId, true, ++_moveTurn);
            int heldMoves = _movesOk - okBefore;
            bool stillHeld = ClientMigrationController.CompletedCount == before;

            // 4. Close it: the hop follows.
            bool closeAnswered = false;
            GameInstance.ClientStorageHandlers.RequestCloseStorage((handler, code, response) => closeAnswered = true);
            float closedAt = Time.realtimeSinceStartup, end = closedAt + 10f;
            while (Time.realtimeSinceStartup < end && ClientMigrationController.CompletedCount == before)
                yield return null;
            bool hopAfterClose = ClientMigrationController.CompletedCount > before;
            Log($"chest: closed (answered={closeAnswered}) -> {(hopAfterClose ? $"hop after {(Time.realtimeSinceStartup - closedAt) * 1000f:0} ms to {CurrentMapEndpoint()}" : "no hop in 10 s")}");
            yield return new WaitForSecondsRealtime(2f);

            // 5. The chest's puppet on the new cell: refused.
            bool puppetOpened = false;
            BuildingEntity puppet = FindBuildingById(chestId);
            if (puppet is StorageEntity puppetChest)
            {
                EnsureStorageHooks();
                _storageOpenSeen = false;
                GameInstance.PlayingCharacterEntity.BuildingComponent.CallCmdOpenStorage(puppetChest.ObjectId, string.Empty);
                float until = Time.realtimeSinceStartup + 3f;
                while (!_storageOpenSeen && Time.realtimeSinceStartup < until)
                    yield return null;
                puppetOpened = _storageOpenSeen;
            }
            Log($"chest: open through the puppet at {CurrentMapEndpoint()} -> {(puppet == null ? "no puppet here" : puppetOpened ? "OPENED" : "refused")}");

            // 6. Back to its cell, reopen and count.
            int back = ClientMigrationController.CompletedCount;
            yield return RideTo(line - 2.5f, deadline, (r, at) => { });
            end = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < end && ClientMigrationController.CompletedCount == back)
                yield return null;
            yield return new WaitForSecondsRealtime(1f);
            int storageAfter = -1;
            chest = FindBuildingById(chestId) as StorageEntity;
            if (chest != null)
                yield return OpenStorageAndRead(chest, "back on its cell", (ok, scrolls) => storageAfter = scrolls);

            // 7. Walk out of reach past the line: the chest closes as the kit closes it, and the hop follows.
            int away = ClientMigrationController.CompletedCount;
            yield return RideTo(line + 8f, deadline, (r, at) => { });
            end = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < end && (ClientMigrationController.CompletedCount == away || _storageOpenSeen))
                yield return null;
            bool closedAway = !_storageOpenSeen && ClientMigrationController.CompletedCount > away;
            Log($"chest: out of reach -> windowOpen={_storageOpenSeen} migrations={ClientMigrationController.CompletedCount - away}");

            self = GameInstance.PlayingCharacterEntity;
            int bag = self != null ? CountScrolls(self.NonEquipItems) : -1;

            // 8. Back on its cell, the chest is destroyed: buildings persist between the rig's scenarios.
            int home = ClientMigrationController.CompletedCount;
            yield return RideTo(line - 2.5f, deadline, (r, at) => { });
            end = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < end && ClientMigrationController.CompletedCount == home)
                yield return null;
            yield return new WaitForSecondsRealtime(1f);
            chest = FindBuildingById(chestId) as StorageEntity;
            if (chest != null && GameInstance.PlayingCharacterEntity != null)
                GameInstance.PlayingCharacterEntity.BuildingComponent.CallCmdDestroyBuilding(chest.ObjectId);
            end = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < end && FindBuildingById(chestId) != null)
                yield return null;
            bool removed = FindBuildingById(chestId) == null;

            Passed = held && stillHeld && heldMoves == 3 && _movesFailed == 0 && hopAfterClose && puppet != null && !puppetOpened
                && storageAfter == _expectedStorage && closedAway && bag + storageAfter == BuildTreeScrolls;
            Finished = true;
            Log(string.Format(CultureInfo.InvariantCulture,
                "RESULT chest: held={0} heldMoves={1}/3 movesOk={2} movesFailed={3} hopAfterClose={4} puppetRefused={5} storage={6}/{7} bag={8} total={9} closedOutOfReach={10} removed={11} -> {12}",
                held && stillHeld, heldMoves, _movesOk, _movesFailed, hopAfterClose, puppet != null && !puppetOpened, storageAfter, _expectedStorage, bag,
                bag + storageAfter, closedAway, removed, Passed ? "PASS" : "FAIL"));
        }
    }
}
