// Server Meshing add-on — automated test client, Slice 4 additions (slices/slice4_design.md decision 15).
//
// buildtree: walk to the position, place a wall 3 m past it, then a storage chest and a door snapped to the wall (a
// tree of three), open the chest and put one skill scroll in; then move scrolls in and out every 2 s (and in a burst
// right after the first migration, which is when the tree changes cells) until 20 s after that migration; on the new
// cell find the chest and the door again by id, open the storage and the door; stand for the window; after a lost
// session count the tree and open the storage once more. The RESULT carries the counts a harness grades: nothing is
// duplicated or lost by moves across the transfer when the scrolls in the bag and the storage still sum to 20.
// Phases (the play runner mirrors them): buildtree-placed, buildtree-ready, buildtree-checked.

using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshAutoClient
    {
        /// <summary>BuildTreeReach is how close the tester stands to a chest or a door before it uses it (the kit's storage open is strict against the entity's activatable distance).</summary>
        public const float BuildTreeReach = 1.5f;
        /// <summary>BuildTreeMoverAfterMigrationSeconds is how long the mover keeps going after the first migration.</summary>
        public const float BuildTreeMoverAfterMigrationSeconds = 20f;
        public const float BuildTreeBurstSeconds = 10f;
        public const float BuildTreeMoveEverySeconds = 2f;
        public const float BuildTreeBurstEverySeconds = 0.25f;
        public const int BuildTreeScrolls = 20;   // provision_rows.COMMON_BAG: the skill item stack

        private string _treeWallId, _treeChestId, _treeDoorId;
        private int _movesOk, _movesRefused, _movesFailed, _moveTurn, _expectedStorage, _reopens, _reopensOk;
        private string _storageBefore = "", _storageAfter = "";
        private int _scrollsStorage = -1, _storageAfterRelogin = -1, _buildingsAfterRelogin = -1;
        private bool _doorOpened;
        private bool _storageOpenSeen, _storageClosedSeen;
        private string _storageOwnerSeen;
        private List<CharacterItem> _storageItemsSeen;

        /// <summary>BuildTreeResult is the RESULT line of buildtree (the harness parses these fields).</summary>
        public static string BuildTreeResult(int placed, string rootId, string storageBefore, string storageAfter, bool doorOpened, int movesOk, int movesRefused,
            int movesFailed, int scrollsBag, int scrollsStorage, int buildingsAfterRelogin, int storageAfterRelogin, string suffix, bool passed, int reopensOk = 0, int reopens = 0)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "RESULT buildtree: placed={0} root={1} storageBefore={2} storageAfter={3} doorOpened={4} movesOk={5} movesRefused={6} movesFailed={7} scrollsBag={8} scrollsStorage={9} total={10} buildingsAfterRelogin={11} storageAfterRelogin={12} reopens={15}/{16}{13} -> {14}",
                placed, string.IsNullOrEmpty(rootId) ? "-" : rootId, string.IsNullOrEmpty(storageBefore) ? "-" : storageBefore, string.IsNullOrEmpty(storageAfter) ? "-" : storageAfter,
                doorOpened ? "True" : "False", movesOk, movesRefused, movesFailed, scrollsBag, scrollsStorage, scrollsBag + scrollsStorage,
                buildingsAfterRelogin, storageAfterRelogin, suffix ?? "", passed ? "PASS" : "FAIL", reopensOk, reopens);
        }

        /// <summary>BuildTreePassed: the tree stood, both storage opens and the door worked, no move failed for another reason than the transfer, and the scrolls add up.</summary>
        public static bool BuildTreePassed(int placed, string storageBefore, string storageAfter, bool doorOpened, int movesFailed, int scrollsBag, int scrollsStorage,
            int buildingsAfterRelogin, int storageAfterRelogin)
        {
            bool relogin = buildingsAfterRelogin < 0 || (buildingsAfterRelogin == 3 && storageAfterRelogin == scrollsStorage);
            return placed == 3 && !string.IsNullOrEmpty(storageBefore) && !string.IsNullOrEmpty(storageAfter) && doorOpened && movesFailed == 0
                && scrollsStorage >= 0 && scrollsBag + scrollsStorage == BuildTreeScrolls && relogin;
        }

        /// <summary>The kit clears every ClientStorageActions event on a new map connection (a migration, a re-login): hook again before each use.</summary>
        private void EnsureStorageHooks()
        {
            ClientStorageActions.onNotifyStorageOpened -= OnBuildTreeStorageOpened;
            ClientStorageActions.onNotifyStorageClosed -= OnBuildTreeStorageClosed;
            ClientStorageActions.onNotifyStorageItemsUpdated -= OnBuildTreeStorageItems;
            ClientStorageActions.onNotifyStorageOpened += OnBuildTreeStorageOpened;
            ClientStorageActions.onNotifyStorageClosed += OnBuildTreeStorageClosed;
            ClientStorageActions.onNotifyStorageItemsUpdated += OnBuildTreeStorageItems;
        }

        /// <summary>The serving cell as the client knows it: the kit sends every map server's channel id with its server info (UpdateServerInfoMessage), so after a migration this is the new cell's id.</summary>
        private static string CurrentCell()
        {
            BaseGameNetworkManager manager = BaseGameNetworkManager.Singleton;
            string channel = manager != null ? manager.ChannelId : null;
            return string.IsNullOrEmpty(channel) ? "?" : channel;
        }

        private string CurrentMapEndpoint()
        {
            string endpoint;
            if (ClientMigrationController.CompletedCount > 0 && !string.IsNullOrEmpty(ClientMigrationController.LastResult.address))
                endpoint = ClientMigrationController.LastResult.address + ":" + ClientMigrationController.LastResult.port.ToString(CultureInfo.InvariantCulture);
            else
                endpoint = (_mapAddress ?? "?") + ":" + _mapPort.ToString(CultureInfo.InvariantCulture);
            return CurrentCell() + " " + endpoint;
        }

        private static bool IsScrollStack(CharacterItem item)
        {
            return !item.IsEmptySlot() && item.GetItem() is ISkillItem;
        }

        private static int CountScrolls(IList<CharacterItem> items)
        {
            int n = 0;
            if (items == null)
                return 0;
            for (int i = 0; i < items.Count; ++i)
                if (IsScrollStack(items[i])) n += items[i].amount;
            return n;
        }

        private static int FirstScrollIndex(IList<CharacterItem> items)
        {
            if (items == null)
                return -1;
            for (int i = 0; i < items.Count; ++i)
                if (IsScrollStack(items[i])) return i;
            return -1;
        }

        private static int BuildingItemIndex(BasePlayerCharacterEntity self, System.Func<BuildingEntity, bool> kind)
        {
            for (int i = 0; i < self.NonEquipItems.Count; ++i)
            {
                CharacterItem ci = self.NonEquipItems[i];
                if (ci.IsEmptySlot())
                    continue;
                IBuildingItem bi = ci.GetBuildingItem();
                if (bi != null && bi.BuildingEntity != null && kind(bi.BuildingEntity))
                    return i;
            }
            return -1;
        }

        private static BuildingEntity FindBuildingById(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            foreach (BuildingEntity be in FindObjectsByType<BuildingEntity>(FindObjectsSortMode.None))
                if (be != null && string.Equals(be.Id, id, System.StringComparison.Ordinal)) return be;
            return null;
        }

        private void OnBuildTreeStorageOpened(StorageType type, string ownerId, uint objectId, int weightLimit, int slotLimit)
        {
            if (type != StorageType.Building) return;
            _storageOwnerSeen = ownerId;
            _storageOpenSeen = true;
            _storageClosedSeen = false;
        }

        private void OnBuildTreeStorageClosed(StorageType type, string ownerId)
        {
            if (type != StorageType.Building) return;
            _storageClosedSeen = true;
            _storageOpenSeen = false;
        }

        private void OnBuildTreeStorageItems(StorageType type, string ownerId, List<CharacterItem> items)
        {
            if (type != StorageType.Building) return;
            _storageOwnerSeen = ownerId;
            _storageItemsSeen = items != null ? new List<CharacterItem>(items) : new List<CharacterItem>();
        }

        /// <summary>Places one building of a kind at a position (snapped to the parent when given) and waits for its entity; null when it never appeared.</summary>
        private IEnumerator PlaceBuilding(string what, System.Func<BuildingEntity, bool> kind, Vector3 place, uint parentObjectId, System.Action<BuildingEntity> onDone)
        {
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (self == null) { onDone(null); yield break; }
            int index = BuildingItemIndex(self, kind);
            if (index < 0) { Log($"buildtree: no {what} item in the bag"); onDone(null); yield break; }
            bool sent = self.BuildingComponent.CallCmdConstructBuilding(index, place, Vector3.zero, parentObjectId);
            LogInvariant($"buildtree: placing {what} from slot {index} at {place} parent={parentObjectId} sent={sent}");
            float end = Time.realtimeSinceStartup + 30f;
            BuildingEntity built = null;
            while (Time.realtimeSinceStartup < end && built == null)
            {
                foreach (BuildingEntity be in FindObjectsByType<BuildingEntity>(FindObjectsSortMode.None))
                {
                    if (be != null && kind(be) && !string.IsNullOrEmpty(be.Id) && Vector3.Distance(be.EntityTransform.position, place) < 1f) { built = be; break; }
                }
                yield return null;
            }
            onDone(built);
        }

        /// <summary>Opens a storage entity and waits for the opened notice and the item list; `ok` says whether both came.</summary>
        private IEnumerator OpenStorageAndRead(StorageEntity chest, string what, System.Action<bool, int> onDone)
        {
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (self == null || chest == null) { onDone(false, -1); yield break; }
            EnsureStorageHooks();
            _storageOpenSeen = false;
            _storageItemsSeen = null;
            bool sent = self.BuildingComponent.CallCmdOpenStorage(chest.ObjectId, string.Empty);
            float end = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < end && !(_storageOpenSeen && _storageItemsSeen != null && string.Equals(_storageOwnerSeen, chest.Id, System.StringComparison.Ordinal)))
                yield return null;
            bool ok = _storageOpenSeen && _storageItemsSeen != null;
            int scrolls = ok ? CountScrolls(_storageItemsSeen) : -1;
            LogInvariant($"buildtree: storage opened at {CurrentMapEndpoint()} ({what}) sent={sent} ok={ok} items={(ok ? _storageItemsSeen.Count : -1)} scrolls={scrolls}");
            onDone(ok, scrolls);
        }

        /// <summary>One scroll move, in or out, with its outcome counted and logged.</summary>
        private IEnumerator MoveScroll(string chestId, bool into, int n)
        {
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (self == null) yield break;
            bool done = false;
            AckResponseCode code = AckResponseCode.Unimplemented;
            UITextKeys message = UITextKeys.NONE;
            bool sent;
            if (into)
            {
                int index = FirstScrollIndex(self.NonEquipItems);
                if (index < 0) { Log($"buildtree: move {n} to -> no scroll in the bag"); yield break; }
                sent = GameInstance.ClientStorageHandlers.RequestMoveItemToStorage(new RequestMoveItemToStorageMessage
                {
                    storageType = StorageType.Building, storageOwnerId = chestId, inventoryType = InventoryType.NonEquipItems,
                    inventoryItemIndex = index, inventoryItemAmount = 1, storageItemIndex = -1,
                }, (handler, responseCode, response) => { code = responseCode; message = response.message; done = true; });
            }
            else
            {
                sent = GameInstance.ClientStorageHandlers.RequestMoveItemFromStorage(new RequestMoveItemFromStorageMessage
                {
                    storageType = StorageType.Building, storageOwnerId = chestId, storageItemIndex = 0, storageItemAmount = 1,
                    inventoryType = InventoryType.NonEquipItems, inventoryItemIndex = -1,
                }, (handler, responseCode, response) => { code = responseCode; message = response.message; done = true; });
            }
            if (!sent) { _movesFailed++; Log($"buildtree: move {n} {(into ? "to" : "from")} -> not sent"); yield break; }
            float end = Time.realtimeSinceStartup + 10f;
            while (!done && Time.realtimeSinceStartup < end)
                yield return null;
            string outcome;
            if (done && code == AckResponseCode.Success)
            {
                _movesOk++;
                _expectedStorage += into ? 1 : -1;
                outcome = "ok";
            }
            else if (done && message == UITextKeys.UI_ERROR_CANNOT_ACCESS_STORAGE)
            {
                _movesRefused++;
                outcome = message.ToString();
            }
            else
            {
                _movesFailed++;
                outcome = done ? $"{code} {message}" : "no answer";
            }
            Log($"buildtree: move {n} {(into ? "to" : "from")} -> {outcome}");
        }

        /// <summary>The mover (decision 15 step 3): a move every 2 s, a burst after the first migration, until 20 s after it.</summary>
        private IEnumerator BuildTreeMover(string chestId, int migrationsBefore, float windowEnd)
        {
            float migratedAt = -1f, nextMove = 0f;
            while (Time.realtimeSinceStartup < windowEnd)
            {
                float now = Time.realtimeSinceStartup;
                if (migratedAt < 0f && ClientMigrationController.CompletedCount > migrationsBefore)
                    migratedAt = now;
                if (migratedAt >= 0f && now - migratedAt > BuildTreeMoverAfterMigrationSeconds)
                    break;
                bool burst = migratedAt >= 0f && now - migratedAt <= BuildTreeBurstSeconds;
                if (now >= nextMove)
                {
                    nextMove = now + (burst ? BuildTreeBurstEverySeconds : BuildTreeMoveEverySeconds);
                    BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
                    if (self != null && !_storageOpenSeen)
                    {
                        // The chest is closed here (the holder closed it for the transfer, or this is a new cell): open it
                        // again before any move, as the kit's own UI does; a refused open is the transfer in progress.
                        yield return ReopenChest(chestId, self);
                        if (!_storageOpenSeen)
                        {
                            nextMove = Time.realtimeSinceStartup + 0.5f;
                            continue;
                        }
                    }
                    if (self != null)
                    {
                        bool into = _expectedStorage <= 0 || (CountScrolls(self.NonEquipItems) > 0 && _moveTurn % 2 == 0);
                        if (CountScrolls(self.NonEquipItems) == 0) into = false;
                        _moveTurn++;
                        yield return MoveScroll(chestId, into, _moveTurn);
                        continue;
                    }
                }
                yield return null;
            }
            Log($"buildtree: mover done ok={_movesOk} refused={_movesRefused} failed={_movesFailed} reopens={_reopensOk}/{_reopens}");
        }

        /// <summary>Opens the chest where the tester stands (no walk: it stands within reach of the chest's position on every cell); counts the attempts.</summary>
        private IEnumerator ReopenChest(string chestId, BasePlayerCharacterEntity self)
        {
            BuildingEntity chestNow = FindBuildingById(chestId);
            if (!(chestNow is StorageEntity chest) || Vector3.Distance(self.EntityTransform.position, chest.EntityTransform.position) > chest.GetActivatableDistance())
                yield break;
            EnsureStorageHooks();
            _reopens++;
            self.BuildingComponent.CallCmdOpenStorage(chest.ObjectId, string.Empty);
            float until = Time.realtimeSinceStartup + 1f;
            while (!_storageOpenSeen && Time.realtimeSinceStartup < until)
                yield return null;
            if (_storageOpenSeen)
                _reopensOk++;
            Log($"buildtree: reopen {_reopens} at {CurrentMapEndpoint()} -> {(_storageOpenSeen ? "open" : "not opened")}");
        }

        private IEnumerator WalkNear(float x, float z, float deadline)
        {
            _walkZOverride = z;
            bool reached = false;
            yield return WalkTo(x, deadline, ClientMigrationController.CompletedCount, r => reached = r);
            _walkZOverride = null;
            if (!reached)
                LogInvariant($"buildtree: did not reach x={x} z={z} in time");
        }

        private IEnumerator RunBuildTree(float deadline)
        {
            SetPhase("buildtree");
            ClientStorageActions.onNotifyStorageOpened += OnBuildTreeStorageOpened;
            ClientStorageActions.onNotifyStorageClosed += OnBuildTreeStorageClosed;
            ClientStorageActions.onNotifyStorageItemsUpdated += OnBuildTreeStorageItems;
            try
            {
                yield return RunBuildTreeBody(deadline);
            }
            finally
            {
                ClientStorageActions.onNotifyStorageOpened -= OnBuildTreeStorageOpened;
                ClientStorageActions.onNotifyStorageClosed -= OnBuildTreeStorageClosed;
                ClientStorageActions.onNotifyStorageItemsUpdated -= OnBuildTreeStorageItems;
            }
        }

        private void FinishBuildTree(int placed, string suffix)
        {
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            int bag = self != null ? CountScrolls(self.NonEquipItems) : 0;
            Passed = BuildTreePassed(placed, _storageBefore, _storageAfter, _doorOpened, _movesFailed, bag, _scrollsStorage, _buildingsAfterRelogin, _storageAfterRelogin);
            Finished = true;
            Log(BuildTreeResult(placed, _treeWallId, _storageBefore, _storageAfter, _doorOpened, _movesOk, _movesRefused, _movesFailed, bag, _scrollsStorage,
                _buildingsAfterRelogin, _storageAfterRelogin, suffix, Passed, _reopensOk, _reopens));
        }

        private IEnumerator RunBuildTreeBody(float deadline)
        {
            // 1. The tree: a wall 3 m past the position, a chest and a door snapped to it.
            yield return WalkNear(walkTargetX, walkTargetZ, deadline);
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (self == null) { Fail("no playing character"); yield break; }
            Vector3 wallAt = new Vector3(walkTargetX + 3f, 0f, walkTargetZ);
            BuildingEntity wall = null, chest = null, door = null;
            yield return PlaceBuilding("wall", b => !(b is StorageEntity) && !(b is DoorEntity), wallAt, 0, b => wall = b);
            if (wall == null) { FinishBuildTree(0, StandMigrationsSuffix()); yield break; }
            yield return PlaceBuilding("chest", b => b is StorageEntity, wallAt + new Vector3(0f, 0f, 1.5f), wall.ObjectId, b => chest = b);
            yield return PlaceBuilding("door", b => b is DoorEntity, wallAt - new Vector3(0f, 0f, 1.5f), wall.ObjectId, b => door = b);
            int placed = 1 + (chest != null ? 1 : 0) + (door != null ? 1 : 0);
            _treeWallId = wall.Id;
            _treeChestId = chest != null ? chest.Id : null;
            _treeDoorId = door != null ? door.Id : null;
            Log($"buildtree: placed wall {_treeWallId} chest {_treeChestId ?? "-"} door {_treeDoorId ?? "-"}");
            SetPhase("buildtree-placed");
            if (chest == null || door == null) { FinishBuildTree(placed, StandMigrationsSuffix()); yield break; }

            // 2. Open the chest, one scroll in.
            yield return WalkNear(wallAt.x - BuildTreeReach, wallAt.z + 1.5f, deadline);
            bool opened = false;
            yield return OpenStorageAndRead((StorageEntity)chest, "before", (ok, n) => { opened = ok; });
            if (opened)
                _storageBefore = CurrentCell();
            _expectedStorage = 0;
            yield return MoveScroll(_treeChestId, true, 0);
            self = GameInstance.PlayingCharacterEntity;
            LogInvariant($"Standing at {(self != null ? self.EntityTransform.position : Vector3.zero)} party={(self != null ? self.PartyId : 0)}; buildtree ready, watching for {observeSeconds}s");
            BeginStandMigrations(self);
            SetPhase("buildtree-ready");
            float windowEnd = Time.realtimeSinceStartup + observeSeconds;

            // 3. The mover, until 20 s after the first migration.
            yield return BuildTreeMover(_treeChestId, ClientMigrationController.CompletedCount, windowEnd);
            PollStandMigrations();

            // 4. On the new cell: the chest and the door again, by id.
            if (_standMigrations > 0)
            {
                BuildingEntity chestNow = FindBuildingById(_treeChestId), doorNow = FindBuildingById(_treeDoorId);
                Log($"buildtree: after migration chest={(chestNow != null ? "found" : "missing")} door={(doorNow != null ? "found" : "missing")} at {CurrentMapEndpoint()}");
                if (chestNow is StorageEntity chestEntity)
                {
                    yield return WalkNear(wallAt.x - BuildTreeReach, wallAt.z + 1.5f, deadline);
                    bool ok = false; int scrolls = -1;
                    yield return OpenStorageAndRead(chestEntity, "after", (o, n) => { ok = o; scrolls = n; });
                    if (ok) { _storageAfter = CurrentCell(); _scrollsStorage = scrolls; }
                }
                if (doorNow is DoorEntity doorEntity)
                {
                    yield return WalkNear(wallAt.x - BuildTreeReach, wallAt.z - 1.5f, deadline);
                    self = GameInstance.PlayingCharacterEntity;
                    bool sent = self != null && self.BuildingComponent.CallCmdOpenDoor(doorEntity.ObjectId, string.Empty);
                    float end = Time.realtimeSinceStartup + 5f;
                    while (Time.realtimeSinceStartup < end && doorEntity != null && !doorEntity.IsOpen)
                        yield return null;
                    _doorOpened = doorEntity != null && doorEntity.IsOpen;
                    Log($"buildtree: door opened sent={sent} open={_doorOpened} at {CurrentMapEndpoint()}");
                    if (_doorOpened && self != null)
                        self.BuildingComponent.CallCmdCloseDoor(doorEntity.ObjectId);
                }
            }
            else
                Log("buildtree: no migration inside the mover window");
            SetPhase("buildtree-checked");

            // 5. Stand out the window; after a lost session, count the tree and read the storage again.
            int reloginsSeen = Relogins;
            float nextLog = 0f;
            while (Time.realtimeSinceStartup < windowEnd)
            {
                PollStandMigrations();
                self = GameInstance.PlayingCharacterEntity;
                if (self != null && Time.realtimeSinceStartup >= nextLog)
                {
                    nextLog = Time.realtimeSinceStartup + 10f;
                    Log($"buildtree: standing hp={self.CurrentHp}/{self.MaxHp} scrollsBag={CountScrolls(self.NonEquipItems)} relogins={Relogins}");
                }
                if (Relogins > reloginsSeen && self != null && !_relogging)
                {
                    reloginsSeen = Relogins;
                    yield return CountTreeAfterRelogin(wallAt, deadline);
                }
                yield return null;
            }
            FinishBuildTree(placed, StandMigrationsSuffix());
        }

        /// <summary>After a re-login: the tree within 10 m (the first poll within 10 s that reaches 3, else the last), then the storage once more.</summary>
        private IEnumerator CountTreeAfterRelogin(Vector3 wallAt, float deadline)
        {
            float end = Time.realtimeSinceStartup + 10f;
            int near = 0;
            while (Time.realtimeSinceStartup < end)
            {
                near = 0;
                foreach (BuildingEntity be in FindObjectsByType<BuildingEntity>(FindObjectsSortMode.None))
                    if (be != null && Vector3.Distance(be.EntityTransform.position, wallAt) <= 10f) near++;
                if (near >= 3) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }
            _buildingsAfterRelogin = near;
            Log($"buildtree: after re-login buildings near={near} at {CurrentMapEndpoint()}");
            BuildingEntity chestNow = FindBuildingById(_treeChestId);
            if (chestNow is StorageEntity chestEntity)
            {
                yield return WalkNear(wallAt.x - BuildTreeReach, wallAt.z + 1.5f, deadline);
                bool ok = false; int scrolls = -1;
                yield return OpenStorageAndRead(chestEntity, "after re-login", (o, n) => { ok = o; scrolls = n; });
                if (ok) _storageAfterRelogin = scrolls;
            }
        }
    }
}
