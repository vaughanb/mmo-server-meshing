# Tests

The kit's own tests. Upstream's tests stay in `Core/Tests` as a mirror of upstream; the
demo's are in `Demo/Tests`.

## Running them

- **In the editor:** Window > General > Test Runner, EditMode tab.
- **On GitHub:** the Tests workflow runs them on every pull request (it needs the Unity licence secrets; see `Tools~/README.md`).
- **From the command line:** `python "Tools~/run_tests.py"` builds a clean project that holds
  only the kit, runs the tests in Unity batch mode and prints the result. It works while your
  own editor is open. See [Tools~/README.md](../Tools~/README.md#tests).

## Where tests go

| Folder | What |
|---|---|
| `Tests/Editor/Support` | Shared helpers (below) and tests of the helpers themselves |
| `Tests/Editor/Core`, `MMO`, `Server`, `Database`, `Networking` | Tests for that part of the kit |
| `Demo/Tests/Editor/Logic` | Demo scripts |
| `Demo/Tests/Editor/Content` | Rules the demo's assets must always keep |

The kit has no assembly definitions, so everything compiles into `Assembly-CSharp`, which a
test assembly cannot reference. Tests therefore live in plain `Editor` folders and compile
into `Assembly-CSharp-Editor`. A test that does not compile breaks every editor script in the
project, so run the tests before pushing.

## Kinds of test

Tag every test, or its class, with one category from `TestCategories`:

| Category | What it may use |
|---|---|
| `Logic` | Plain code and ScriptableObjects made in the test. No GameInstance, database or real asset. |
| `Harnessed` | A `TestGameInstance`, a `TempSqliteDatabase` or the network layer. |
| `Content` | The real demo assets. Check rules that must hold (unique ids, nothing missing), never tuning values, so rebalancing the demo does not fail a test. |
| `Play` | Play mode, through `[UnityTest]` and `EnterPlayMode`. Slow; run before a release. |

## Helpers

Derive test classes from `KitTestFixture`. Each test then gets:

- `Objects`, which creates ScriptableObjects and GameObjects and destroys them after the test.
  A ScriptableObject's name is its game-data id, so name it when the id matters.
- A fixed random seed. The gameplay rules roll crits, hits and blocks with `UnityEngine.Random`.
- Empty game-data slots, before and after.

Helpers:

- `UseGameInstance()` sets `GameInstance.Singleton` to a GameInstance with the defaults Awake
  would create (Awake itself cannot run in edit mode). Register game data straight into the
  static dictionaries, e.g. `GameInstance.Quests[quest.DataId] = quest;`. Everything is cleared
  after the test.
- `TempSqliteDatabase.Create(Objects)` creates a migrated SQLite database on a temp file. It
  never touches `./Config` or the project's real database. `CreateUser()` adds a login.
- `TestCharacters.NewPlayer()` makes a player character the databases accept.
- `TestCombatants.Create<PlayerCharacterEntity>(Objects, level, stats)` makes a bare character
  entity whose stats cache holds the given stats, for the gameplay rule's combat and recovery
  maths. `SetObjectId` gives it a network id (unspawned entities all share id 0).
- `TestGear.Equipment<ArmorItem>(Objects, stats, set, maxDurability)` and `TestGear.Set(...)`
  make registered equipment and equipment sets; `TestGear.StatsOf(character)` runs GetAllStats.
  They need a GameInstance.
- `TestInventory.Item(Objects, maxStack, sellPrice, weight)` makes a registered plain item,
  `TestInventory.Player(Objects)` a player whose stats cache (slot and weight limits, price
  rates) is dropped after the test, `LimitSlots(gameInstance, slots)` switches to slot-limited
  bags and `Count(character, item)` adds up a bag's stacks. The kit's sell, refine, repair,
  dismantle, socket and storage actions write to `GameInstance.ServerLogHandlers`, which only a
  network manager sets, and throw without it after changing the bag; `UseServerLog(Objects)`
  installs the kit's do-nothing handlers for the test. They need a GameInstance.
- `TestGameMessages.Use(Objects)` stands in for `GameInstance.ServerGameMessageHandlers`
  until the test ends. Quest hand-ins, crafting and other entity actions send the player
  messages and reward notices through it and throw without one (the kit's own handler needs a
  running network manager). It records `Messages` and `RewardedItems` so a test can check what
  the player was told.
- `TestAsync.Wait(task)` waits for a UniTask that finishes synchronously and fails if it does
  not.
- `TestNetData` is for network messages (LiteNetLib `INetSerializable`). `Fill(type)` builds an
  instance with every public field and settable property set to a distinct non-default value
  (ints above 65535 and longs above 2^32, so a narrowing write shows; enums at their first
  non-default value, every flag for `[Flags]`; two filled elements in each list, array and
  dictionary; nested types filled too); an optional callback sees every object built, to put a
  message on the branch a test wants. `RoundTrip(value)` writes and reads it back and fails if
  the reader leaves bytes unread. `Differences(expected, actual, rules)` lists the members that
  differ, recursing into nested types and collections; null and empty count as the same (the
  kit writes a null list as zero elements, LiteNetLib a null string as ""). The rules leave a
  member out or compare its floats within a tolerance. `Tests/Editor/Networking/MessageRoundTripTests`
  runs every kit message through it; its tables say which members do not travel and why. A new
  message type is covered automatically, or fails `EveryKitTypeIsCoveredOrListed` until it is
  listed.
- `TestLogger.Use(Objects)` initializes LiteNetLib's `LogManager` for the test (and removes it
  after). Outside play mode nothing initializes it and `Logging.Log` throws, so call it before
  server code that logs (the cluster server logs every registration and disconnect).
- `TestDatabaseClient.Install(Objects)` makes a fake `IDatabaseClient` the database client of
  `MMOServerInstance.Singleton`, where the login and central servers read it. The real client is
  a network client whose requests would never be answered. Calls set up with
  `On<TRequest, TResponse>(nameof(IDatabaseClient.X), ...)` or `Succeed(...)` answer at once,
  any other call returns an error result; `Calls`, `WasCalled` and `LastRequest<T>` show what the
  server asked. Pair it with a never-started `LoginNetworkManager` and call its handlers by
  reflection (see `Tests/Editor/Server/LoginNetworkManagerTests`).
- `TestDatabaseServer.Create(Objects)` is a never-started `DatabaseNetworkManager` over a
  `TempSqliteDatabase` and a `LocalDatabaseCache`. `Request<TRequest, TResponse>("GetCharacter",
  req)` calls a request handler and returns its reply; `UseSaveScheduler(...)` installs a deferred
  character save scheduler that is never started, so a test flushes or ticks it itself.
- `TestReflection` sets private fields and properties and calls private methods. Core mirrors
  upstream, so it is not changed to make it testable. Use `asset.With("field", value)` to build
  data in one expression.

## Proving a test can fail

A test that has never failed may not test anything. Before relying on a new one, break the thing
it guards and watch it fail:

- **Code:** undo the fix, or make the smallest change that brings the bug back, run the test,
  then `git checkout` the file.
- **Demo content:** edit the asset file on disk (clear a reference, zero a price), refresh, run,
  then `git checkout` it. Changes made only in memory do not survive into the run: the Test
  Runner unloads assets nothing references and swaps the open scenes before it starts.

## Rules

- A bug fix comes with a test that fails without the fix. Check that it does.
- Run every test after porting upstream changes.
- Test files use the same `#if` as the code they test. The SQLite tests, for example, only
  compile for standalone targets.
