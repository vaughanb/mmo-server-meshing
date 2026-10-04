# Phase 0 step 2 — the measuring instrument: implementation contract

Repository: `C:\dev\Game Design\mmo-server-meshing`. Add-on: `Assets/ServerMeshing` (Mesh/ ships; TestMap/ is the rig;
Tests/Editor holds NUnit EditMode tests, namespace `MultiplayerARPG.MMO.Mesh.Tests`, compiled into Assembly-CSharp-Editor
with no InternalsVisibleTo, so everything a test calls must be public).

Background you may read (all read-only): the build sheet's step 2 (`...\7cb7243d-...\scratchpad\phase0_build_sheet.md`
lines 417-493 and rules lines 94-272) and the survey that checked it against today's code:
`C:\Users\brend\AppData\Local\Temp\claude\C--dev-Game-Design-mmo-server-meshing\2cf88996-d970-455a-bb4e-fffe6bad122c\scratchpad\survey\`
(`critic.md` is the synthesis; `probe.md`, `kit.md`, `rig.md`, `threads.md`, `harness.md`, `verify.md` are the detail).
Where this contract and the sheet disagree, THIS CONTRACT WINS (it folds in the survey's corrections).

## Rules (apply to every file)
- C# 9 on .NET Framework 4.7.1 API: no records, no `init`, no default interface members. `readonly struct` and `in` are fine.
- Match the surrounding code: a one-line file header comment `// Server Meshing add-on — ...`, XML doc comments on every
  public type and member starting with its name, other comments minimal and only where needed.
- Errors: constructors throw `ArgumentOutOfRangeException`/`ArgumentNullException` naming the argument; logs name the object,
  type and connection or cell; nothing is swallowed. Use `Logging.Log/LogWarning/LogError("Mesh", ...)` (TestMap uses the
  same). NEVER put a literal `{` or `}` in a logged string (the logger runs `AppendFormat` and throws on a lone brace).
- No allocation in per-tick or per-frame paths: no lambdas/closures, no string formatting, no LINQ, no boxing
  (`FieldInfo.GetValue` on a value-type field boxes; on a reference-type field it does not). Per-window work may allocate.
- `System.Diagnostics.Stopwatch` must be written fully qualified in files that also use `UnityEngine` (a
  `using System.Diagnostics;` next to `using UnityEngine;` makes `Debug` ambiguous).
- Log lines: one physical line; invariant culture via `System.FormattableString.Invariant($"...")`; never print NaN,
  Infinity, negative numbers or E notation (clamp to 0, use fixed formats); never contain the substring `Exception`, the
  capitalised whole words `Split`, `Merge`, `Retire`, `Retired`, or start with `Work window`.
- **Stray-reference rule (a unit test enforces it):** every type declared in TestMap/ or Tests/ becomes a reserved word that
  Mesh/ text (code AND comments, whole word, case-sensitive) must never contain; every type declared in Tests/ is also
  reserved for TestMap/ text. So: Mesh/ never names ServerMesh* rig types or test classes, TestMap never names test classes,
  and NO new TestMap/Tests type (nested ones included) may have a generic name that Mesh/ already uses as a word — e.g.
  `Sample`, `State`, `Tick`, `Item`, `Rates`, `Timing`, `Mode`, `Placement`, `Driver`, `Installer`, `Burn`, `Window`.
  Prefix every new TestMap/Tests type with `ServerMesh` (TestMap) or `Mesh`/`ServerMesh` (Tests) and keep names distinctive.
- Shipped Mesh/ features default off/unchanged: with no environment set, the Work window line, the probe state and the
  wire (MeshLoadReport) are byte-identical to today. The new Work ticks line and the tick stamps ARE always on (log-only,
  like the Work window line; a few QueryPerformanceCounter calls per 50 ms tick).
- Do not change demo defaults (prefabs, TimeManager, updateFps). Only environment toggles change behaviour.
- Do NOT run Unity, the rig, `regress.py`, git add/commit/push, or touch files outside your assigned list. Unity writes
  `.meta` files; never hand-write one. You MAY compile-check with
  `CSCHECK_OUT=<unique dir under the scratchpad> python "C:\Users\brend\AppData\Local\Temp\claude\C--dev-Game-Design-mmo-server-meshing\2cf88996-d970-455a-bb4e-fffe6bad122c\scratchpad\cscheck.py" --editor`
  (compiles Assembly-CSharp then Assembly-CSharp-Editor with the tests). Other implementers are editing other files at the
  same time, so errors in files that are not yours may appear; only fix yours, and code against the APIs below exactly.

## Names (new; registry §0.15 plus the survey's additions)
- Mesh/: `MeshTickWindow` (new file `Mesh/MeshTickWindow.cs`); edits to `MeshWorkHistogram`, `MeshWorkMeter`,
  `MeshWorkProbe` (`Mesh/MeshWorkProbe.cs`), `MeshMapNetworkManager` (`Mesh/MeshMapNetworkManager_Load.cs`).
- TestMap/Phase0/ (new folder): `ServerMeshRates`, `ServerMeshRatesSetting`, `ServerMeshTickBurn`, `ServerMeshTimedAoi`,
  `ServerMeshAoiCountdown`, `ServerMeshTimedAoiInstaller`, `ServerMeshAoiLateBegin`, `ServerMeshAoiLateEnd`,
  `ServerMeshThreadCpu`, `ServerMeshThreadClass`, `ServerMeshThreadCpuNative`, `ServerMeshThreadCpuReading` (if needed).
- Tests/Editor: `MeshTickScheduleCopy`, `MeshTickWindowLineTests`, `ServerMeshRatesSettingTests`,
  `ServerMeshAoiCountdownTests`, `ServerMeshThreadClassTests`, `ServerMeshTickBurnSettingTests` (if needed),
  plus new cases inside the existing `MeshWorkHistogramTests` / `MeshWorkMeterTests` in `MeshMeasurementTests.cs`.
- Environment keys (all default "unset = today"): Mesh/: `MESH_WORK_BINS`. TestMap/: `MESH_RATES`, `MESH_UNCAPPED_CELLS`,
  `MESH_AOI_TIMING`, `MESH_THREAD_CPU`, `MESH_PROBE_BURN_TICK_MS`. All go into `ENV_KEYS` in
  `Tools/ServerMeshing/regress.py` (currently lines ~69-74).

## A. Core meter (Mesh/) — public API other parts code against

### MeshWorkHistogram (edit `Mesh/MeshWorkHistogram.cs`)
- Keep the constructor `(float binMs = 0.25f, int binCount = 400)` and every existing result. Store the width in whole
  microseconds (`_binUs = (int)Math.Round(binMs * 1000)`, must be >= 1 else throw naming `binMs`).
- `Add(float ms)`: NaN or negative -> 0; +Infinity (or anything >= binCount x width) -> overflow, never an index error;
  bin = `(int)Math.Floor(ms * 1000.0 / _binUs)`. (2.5 ms at 50 µs bins lands in bin 50; so does 2.52 ms.)
- `Percentile(float fraction)`: exact integer rank: `ppm = round(clamp01(fraction) * 1e6)`,
  `rank = max(1, (ppm * count + 999999) / 1000000)` in long arithmetic; returns the upper edge of that bin, clamped to Max,
  or Max when the rank is in the overflow. (p99 of exactly 100 values is the 99th value's bin, not the max.)
- New public read-only API: `float BinMs`, `int BinCount`, `int CountInBin(int bin)` (throws ArgumentOutOfRangeException
  naming `bin`), `int Overflow`. XML comments on `Count`, `Mean`, `Max`.

### MeshWorkMeter tick extension (edit `Mesh/MeshWorkProbe.cs`)
Existing behaviour (WorkEnded/WorkStarted/TakeWindow/Judge/State/ProofOverdue) is unchanged. Validate the constructor
argument (`ticksPerSecond > 0`, else ArgumentOutOfRangeException naming it). Add:
- `public const int MaxTicksPerFrame = 5;` (the kit's LogicUpdater runs at most 5 ticks per Update, then drops its backlog).
- `public bool LogBins { get; set; }` — when true, `TakeTickWindow` also fills `MeshTickWindow.bins`.
- `public void BeginTick(long stamp)` — a kit tick starts. If a tick is still open (the previous one threw), count it in
  `unclosed` and start over. Flushes the pending send sample of the previous tick.
- `public void MarkAoiCall(long startStamp, long endStamp, bool ranPass)` — the interest manager's call inside the open
  tick: keeps the first start and the last end, ORs `ranPass`. Outside an open tick it is only counted (`aoiOutsideTick`).
- `public void EndTick(long stamp)` — the tick's kit work is over: adds the tick duration; when an AOI call was marked,
  adds the three parts (sync+ping = aoiStart - tickStart, aoi schedule = aoiEnd - aoiStart, movement loop = tickEnd -
  aoiEnd, each clamped at 0, kept as long tick sums so they add up to the duration exactly); if the AOI pass ran, marks the
  current frame as an AOI frame and counts a pass. Leaves a pending send sample = movement-loop ms (0 when no AOI call was
  marked). EndTick without an open tick is ignored.
- `public void AddTickWork(double ms)` — work done for the tick after EndTick (step 6's sender): added to the pending send
  sample (creating one if none is pending). The pending sample is flushed into the send histogram at the next BeginTick,
  WorkEnded or TakeTickWindow.
- `public void MarkAoiComplete(long startStamp, long endStamp)` — the interest manager's completion work outside the tick
  (its LateUpdate, bracketed by the rig): per-window count, sum and max.
- `public void NotePhysicsStep()` — one FixedUpdate step in the current frame.
- Frame tagging: per-frame accumulators (ticks in this frame, AOI pass in this frame, physics steps in this frame) are
  incremented by BeginTick/EndTick/NotePhysicsStep and consumed in `WorkEnded` right after the existing work sample: the
  frame's work ms goes to the tick-frame histogram (>= 1 tick) or the other-frame histogram (0 ticks); tick frames go to the
  non-AOI tick-frame histogram unless the frame had an AOI pass (then to AOI-frame count/sum/max); 0/1/2+ tick counts and
  frames at the cap (ticks >= MaxTicksPerFrame); physics total/max/frames with 2+ steps; the work sum. The accumulators are
  reset in WorkEnded even when there was no work start yet (the first frame). TakeTickWindow must NOT reset them (it is
  called mid-frame, after that frame's ticks), only the window totals; a frame whose ticks fell before a window boundary is
  recorded in the next window (±1 frame per window, by design).
- Histograms for the tick window: `new MeshWorkHistogram(0.05f, 4000)` (200 ms range, 50 µs bins) for tick frames,
  non-AOI tick frames, other frames, tick duration and send work.
- `public MeshTickWindow TakeTickWindow(double seconds, int tickHz, float nominalBudgetMs)` — summarises and resets window
  totals. `expected = seconds > 0 && tickHz > 0 ? (int)Math.Round(seconds * tickHz) : 0`, `dropped = max(0, expected -
  ticks)`; work ms/s = frame work sum / seconds (0 when seconds <= 0); busy share = work ms/s / 1000. Fills `bins` only
  when LogBins. Never NaN.

### MeshTickWindow (new `Mesh/MeshTickWindow.cs`)
A public struct, XML-commented public fields, never serialised (the load-report wire is frozen). Fields (names exact):
`seconds` (float), `tickHz`, `ticks`, `expected`, `dropped`, `capFrames`, `unclosed`, `aoiOutsideTick`, `frames`,
`frames0`, `frames1`, `frames2Plus` (ints); `tickFrameMeanMs`, `tickFrameP95Ms`, `tickFrameP99Ms`, `tickFrameMaxMs`,
`nonAoiTickFrameP95Ms`, `nonAoiTickFrameP99Ms`; `aoiPasses`, `aoiFrames` (ints), `aoiFrameMeanMs`, `aoiFrameMaxMs`;
`otherFrameMeanMs`, `otherFrameP95Ms`; `tickMeanMs`, `tickP95Ms`, `tickP99Ms`, `tickMaxMs`; `partsTicks` (int),
`syncMeanMs`, `aoiScheduleMeanMs`, `movementMeanMs`; `aoiCompleteCalls` (int), `aoiCompleteMeanMs`, `aoiCompleteMaxMs`,
`aoiCompleteMsPerSecond`; `sendSamples` (int), `sendMeanMs`, `sendP95Ms`, `sendP99Ms`; `physicsSteps`,
`physicsMaxPerFrame`, `physics2PlusFrames` (ints), `physicsPerSecond`, `physicsMeanPerFrame`; `workMsPerSecond`,
`busyShare`, `nominalBudgetMs` (floats); `gen0Collections` (int, filled by the caller); `bins` (string, null unless LogBins).
`public string ToLine()` returns EXACTLY this shape (invariant culture; every field always printed; zeros when empty):
```
Work ticks 5.00 s: ticks 100 expected 100 dropped 0 cap 0 unclosed 0 | frames 150 t0 50 t1 100 t2 0 | tick frames mean 3.412 ms p95 4.10 ms p99 5.20 ms max 6.1 ms | nonaoi p95 4.00 ms p99 4.50 ms | aoi passes 5 frames 5 mean 4.000 ms max 8.2 ms | other mean 1.203 ms p95 1.50 ms | kit mean 0.812 ms p95 1.00 ms p99 1.20 ms max 2.0 ms | parts 100 sync 0.300 ms aoi 0.050 ms move 0.462 ms | late 150 mean 0.020 ms max 0.9 ms 0.6 ms/s | send 100 mean 0.462 ms p95 0.60 ms p99 0.70 ms | physics 50.0/s mean 1.67 max 2 f2 100 | work 312.4 ms/s busy 31.2% | budget 33.33 ms | gc0 0 | outside 0
```
Formats: seconds `0.00`; means `0.000`; p95/p99 `0.00`; max `0.0`; ms/s `0.0`; busy percent `0.0`; physics/s `0.0`,
physics mean `0.00`; budget `0.00`. When `bins` is non-null it is a second line (not part of ToLine) of the form
`Work bins 5.00 s: tick 50:3 51:7 over:0 | nonaoi ... | other ... | kit ... | send ...` listing only non-empty bins as
`index:count` (bin width 0.05 ms) plus `over:N`; no braces.

### MeshWorkProbe additions (same file)
- `public static int NominalFrameRate { get; set; }` — the frame rate a run is modelled at when the process is uncapped
  (0 = none). Set by rig code; Mesh/ comments must not name the rig type.
- `public static float BudgetFor(int targetFrameRate, int nominalFrameRate)` — `1000/target` when target > 0, else
  `1000/nominal` when nominal > 0, else 0.
- `public static float EffectiveBudgetMs => BudgetFor(Application.targetFrameRate, NominalFrameRate);` — for the Work
  ticks line only. NEVER pass it to `TakeWindow`, the wire or the Work window line (`BudgetMs` stays as is).
- The Meter getter also adds `PlayerLoopHelper.AddAction(PlayerLoopTiming.FixedUpdate, new PhysicsStepItem())` after
  `s_meter` is assigned; the item calls `s_meter.NotePhysicsStep()` and returns true; it must never throw.
- XML comments on `MeshProbeState` members, `MeshWorkWindow` fields and `State` (currently missing).

### MeshMapNetworkManager (edit `Mesh/MeshMapNetworkManager_Load.cs`, inside the existing server guard)
- `public const string ENV_WORK_BINS = "MESH_WORK_BINS";` beside the other ENV constants (outside the guard, XML comment).
  Read in `LoadOnStartServer`: `MeshWorkProbe.Meter.LogBins = value == "1"` (log one line when on).
- The only override of the tick, placed in the guarded region:
  `protected override void OnServerUpdate(LogicUpdater updater)` → `meter.BeginTick(Stopwatch.GetTimestamp()); try {
  base.OnServerUpdate(updater); } finally { meter.EndTick(Stopwatch.GetTimestamp()); }`.
- In `LoadTick`, right after `_lastWorkWindow = MeshWorkProbe.Meter.TakeWindow(MeshWorkProbe.BudgetMs, window);` (keep that
  line exactly): take `MeshTickWindow ticks = MeshWorkProbe.Meter.TakeTickWindow(window, tick rate,
  MeshWorkProbe.EffectiveBudgetMs)` BEFORE the retired-cell early return, set `ticks.gen0Collections` to the
  `GC.CollectionCount(0)` delta since the previous window, and after `Logging.Log("Mesh", WorkWindowLine(report));` log
  `ticks.ToLine()` and, when non-null, `ticks.bins`. Tick rate = `(int)Math.Round(1.0 / LogicUpdater.DeltaTime)` when the
  updater exists and DeltaTime > 0, else `updateFps`. `WorkWindowLine` stays byte-identical.

### Tests for A (extend `Tests/Editor/MeshMeasurementTests.cs`; new `Tests/Editor/MeshTickScheduleCopy.cs`,
`Tests/Editor/MeshTickWindowLineTests.cs`)
Failure cases first; `TestCaseSource` + `SetName("Subject_Condition_Expectation")` where it is a table. Never touch
`MeshWorkProbe.Meter` or `NominalFrameRate` from a test (the editor's player loop would feed the static meter).
- Histogram: Add(+Infinity) goes to overflow and does not throw; 2.5 and 2.52 ms at (0.05, 4000) land in bin 50;
  above 200 ms goes to overflow and Percentile returns the max; p99 of 1..100 ms values (1 ms bins) is bin 99's edge;
  CountInBin(-1) and CountInBin(BinCount) throw naming `bin`; zero-width after rounding (e.g. 0.0001 ms) throws.
- Meter: a window with no ticks gives zeros (no NaN anywhere, ToLine has no NaN); dropped = expected - actual; at 30 fps /
  20 Hz (MeshTickScheduleCopy driving ticks on the Driver's timeline, frame period 333,334 ticks at 10 MHz) ticks land on
  2 of 3 frames; two ticks in one frame count as a 2+ frame; five as a cap frame; tick and other frames get separate
  p95; AOI-pass frames are left out of the non-AOI p95 and counted; the nominal budget flows through; physics steps and 2+
  step frames are counted; work ms/s = sum of work / seconds; the three parts sum to the tick duration within 1 µs; an
  unclosed tick is counted; AddTickWork after EndTick lands in the send sample, flushed at WorkEnded; MarkAoiCall outside
  a tick is counted as outside; the window straddle (ticks before TakeTickWindow, WorkEnded after it) files the frame in
  the next window as a tick frame; LogBins fills bins with `50:` for a 2.52 ms frame. `BudgetFor` table: (60,0)->16.67,
  (-1,30)->33.33, (0,0)->0, (-1,0)->0, (30,60)->33.33.
- MeshTickScheduleCopy: a copy of the kit LogicUpdater's accumulator (period = (long)(1/tickHz * frequency); accumulate
  frame elapsed; while accumulator >= period: if updates >= 5 { accumulator = 0; return } fire; accumulator -= period).
- MeshTickWindowLineTests: `[SetCulture("de-DE")]`, a hand-filled MeshTickWindow, the exact expected string; plus a
  default(MeshTickWindow) line with all zeros and no "NaN".

## B. Rates, uncapping, tick burn, keys, Python (TestMap + Tools)

### `TestMap/Phase0/ServerMeshRates.cs` — `ServerMeshRates` (MonoBehaviour) + `ServerMeshRatesSetting` (readonly struct)
- `public const string ENV_RATES = "MESH_RATES"`, `ENV_UNCAPPED_CELLS = "MESH_UNCAPPED_CELLS"`.
- `ServerMeshRatesSetting.TryParse(string text, out ServerMeshRatesSetting setting, out string error)`; fields/properties
  `FramesPerSecond` (-1 = uncapped), `NominalFramesPerSecond` (0 = take the kit's cap at apply time), `FixedStepSeconds`.
  Grammar (whitespace around parts allowed; invariant culture): `<fps>/<step>` | `u/<step>` | `u<fps>/<step>`; fps is an
  integer 1..1000; step is seconds, > 0 and <= 1. Failures name the failing part: `""`, `"abc"`, `"0/0.02"`, `"30/0"`,
  `"30/-0.02"`, `"30/x"`, `"30"`, `"u0/0.02"`, `"30/2"`. Parses: `"30/0.02"`, `"60/0.03"`, `"u/0.02"`, `"u30/0.02"`,
  `" 30 / 0.02 "`, `"U30/0.02"`.
- `public static bool OwnsFrameRate` — true when either env value is non-empty (read from the environment, so ordering
  with the load generator cannot matter).
- `public static IReadOnlyList<string> ParseCellList(string text)` (or equivalent public helper): comma list, trimmed,
  empty entries dropped, duplicates removed ignoring case.
- Install: `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]` AutoStart that returns when both
  keys are unset; otherwise creates one DontDestroyOnLoad GameObject with the component (guard duplicates). Follow the
  existing TestMap installer pattern (ServerMeshLoadGenerator.AutoStart and its IsServer polling).
- Apply once when `BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh && mesh.IsServer`, and never in the
  editor (`Application.isEditor` → one warning, nothing applied). Capture the kit's values first (targetFrameRate,
  fixedDeltaTime). Semantics:
  - MESH_RATES fps part: every cell's cap and the nominal budget. `<fps>`: targetFrameRate = fps, NominalFrameRate = 0.
    `u`/`u<fps>`: targetFrameRate = -1, NominalFrameRate = fps or (bare u) the kit's captured cap.
  - Step part: `Time.fixedDeltaTime = step`. Always `QualitySettings.vSyncCount = 0`.
  - MESH_UNCAPPED_CELLS: once `mesh.HasOwnZone`, if `mesh.OwnZoneId` is listed (ignoring case): targetFrameRate = -1 and
    NominalFrameRate = the MESH_RATES fps (or the kit's captured cap when MESH_RATES is unset or uncapped without fps);
    log one `Rates: cell <id> uncapped (nominal <n> fps)` line. With MESH_RATES unset, fps and step stay as the kit set them.
  - Invalid MESH_RATES: `Logging.LogError("Mesh", ...)` naming the key and the failing part; apply nothing.
  - One line after applying (invariant culture, no braces), e.g.
    `Rates: fps 30 (MESH_RATES; kit 60) vSync 0 | tick 20 Hz (updateFps unchanged) | physics 0.02 s (project 0.03) | maxDeltaTime 0.333 | probe budget 33.33 ms | uncapped cells -`
    (`fps u30` when uncapped; tick from `mesh.updateFps`; maxDeltaTime from `Time.maximumDeltaTime`; budget =
    `MeshWorkProbe.EffectiveBudgetMs`; uncapped cells = the list or `-`).
- Edit `TestMap/ServerMeshLoadGenerator.cs` (the uncap at ~lines 100-104): uncap only when `MESH_LOAD_UNCAPPED == "1" &&
  !ServerMeshRates.OwnsFrameRate`; when MESH_LOAD_UNCAPPED is "1" but ServerMeshRates owns the rate, log once that the frame
  rate is left to MESH_RATES / MESH_UNCAPPED_CELLS. With neither new key set the path is byte-identical.
- Tests: `Tests/Editor/ServerMeshRatesSettingTests.cs` (failures first, table) and cell-list cases.

### `TestMap/Phase0/ServerMeshTickBurn.cs` — `ServerMeshTickBurn` (MonoBehaviour)
- `MESH_PROBE_BURN_TICK_MS=[cell:]ms` (same parse and messages as MESH_PROBE_BURN_MS in `MeshMapNetworkManager_Load.cs`
  LoadOnStartServer). A public static parse helper that tests can call.
- Refuses (LogError, no burn) when `MESH_PROBE_BURN_MS` is also set (both land in tick frames).
- Once `mesh.IsServer` (and, for a cell prefix, once `mesh.HasOwnZone` and the id matches ignoring case), subscribe a
  cached method to `mesh.LogicUpdater.OnTick` (check the event and its delegate signature in LiteNetLibManager's
  LogicUpdater / LiteNetLibDelegates). The burn busy-waits with Stopwatch like `LoadTick`'s burn. Unsubscribe in OnDestroy.
  It runs after the Mesh tick stamps, so it shows in tick-frame work, not in tick duration. Log once
  `Tick burn: N ms per tick on <cell|every cell>`. Never in the editor.

### Tools
- `Tools/ServerMeshing/regress.py`: add the six keys to `ENV_KEYS`.
- New `Tools/ServerMeshing/p0tools.py` (pure) with `parse_work_ticks(line) -> dict | None`, `parse_rates(line) -> dict |
  None`, `parse_threads_window(line) -> dict | None`, `ENV_REGISTRY` (the six keys with one-line meanings), and
  `rates_setting(text) -> (fps, nominal, step, error)` mirroring the C# grammar. New `Tools/ServerMeshing/test_p0tools.py`:
  unittest, `cases = {name: (input, expected)}` + `subTest`, failure first (see `test_rigtools.py:12-24`); parse the exact
  sample lines of this contract; every ENV_REGISTRY key is in `regress.ENV_KEYS`. Must pass `ruff check` and
  `python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"`.

## C. Timed AOI (TestMap/Phase0/ServerMeshTimedAoi.cs)
- `ServerMeshTimedAoi : JobifiedGridSpatialPartitioningAOI` with `[DefaultExecutionOrder(30000)]` (verify no script uses
  29999-30001). Overrides ONLY `UpdateInterestManagement(float deltaTime)` and `UpdateInterestManagementImmediate()`.
  It must NOT declare LateUpdate, OnDestroy, OnDrawGizmosSelected, Awake or any other Unity message (JGAOI's are private;
  redeclaring hides them and loses query completion, owner changes and disposal).
  - `UpdateInterestManagement(dt)`: `bool due = _countdown.Step(dt, updateInterval, systemReady)` where systemReady =
    the private `_system` field (resolved once as a static FieldInfo; reference type, no boxing) is non-null; stamp;
    `base.UpdateInterestManagement(dt)`; stamp; `MeshWorkProbe.Meter.MarkAoiCall(start, end, due)`.
  - `UpdateInterestManagementImmediate()`: `_countdown.ForceDue(); base.UpdateInterestManagementImmediate();` (the base
    re-enters the virtual UpdateInterestManagement(0), so the call is timed and counted once).
- `ServerMeshAoiCountdown` (public struct or class, pure): mirrors JGAOI's float arithmetic in the same order —
  `Step(float dt, float interval, bool systemReady)`: when !systemReady return false and do not touch the countdown;
  else `c -= dt; if (c > 0) return false; c = interval; return true;` (starts at 0, so the first ready call is due);
  `ForceDue()` sets c = 0. Verify against the kit source (JGAOI ~163-178).
- `ServerMeshAoiLateBegin` (`[DefaultExecutionOrder(29999)]`) and `ServerMeshAoiLateEnd` (`[DefaultExecutionOrder(30001)]`):
  tiny components on the same child GameObject whose LateUpdate stamps; End calls
  `MeshWorkProbe.Meter.MarkAoiComplete(beginStamp, endStamp)` (Begin stores its stamp in a static or shared field; no
  allocation).
- `ServerMeshTimedAoiInstaller` (MonoBehaviour, `[DefaultExecutionOrder(int.MaxValue)]`), AutoStart at AfterSceneLoad when
  `MESH_AOI_TIMING == "1"`, never in the editor. In its LateUpdate, once `mesh.IsServer`: require `mesh.InterestManager` to
  be a `JobifiedGridSpatialPartitioningAOI` and not already a ServerMeshTimedAoi (else warn with the actual type and stop);
  resolve the `_system` field (missing → LogError naming it, stop); create a child GameObject under `mesh.transform`; add
  Begin, the timed AOI and End; copy `cellSize`, `maxObjects`, `updateInterval`, `bufferedCells`, `boundsGizmosColor` and
  `defaultVisibleRange` from the old component BEFORE `mesh.InterestManager = timed` (the setter runs Setup at once);
  then `old.enabled = false` (never destroy it: it is the kit's fallback); log `AOI timing: installed ...` with the copied
  values. Stay alive: if the timed component ever becomes Unity-null, re-enable the old one and LogError.
- Tests `Tests/Editor/ServerMeshAoiCountdownTests.cs`: at dt `(float)(1.0/20)` and interval 1: 1 pass in 20 (passes at
  calls 0, 20, 40 over 60 calls); ForceDue makes the next call due; the first ready call is due even at dt 0; after a pass
  dt 0 is not due; not ready → never due and the countdown is untouched (a later ready call is still due); interval 0.25 →
  every 6 calls.

## D. Thread CPU (TestMap/Phase0/ServerMeshThreadCpu.cs)
- `ServerMeshThreadClass` enum: `Main, LiteNetLib, Jobs, Unnamed, Other`.
- `public static ServerMeshThreadClass Classify(string description, uint id, uint mainId)` in this order: id == mainId →
  Main; `"LogicThread"` or starts with `"ReceiveThread("` → LiteNetLib; contains `"Job.Worker"` → Jobs (catches
  "Background Job.Worker N"); null/empty → Unnamed; else Other. No `#if` in Classify (tests compile in the editor).
- `ServerMeshThreadCpuNative`: kernel32 DllImports (ExactSpelling, SetLastError where meaningful), marshalled, no unsafe:
  CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD), Thread32First/Thread32Next with a correct THREADENTRY32 (dwSize set), OpenThread
  (THREAD_QUERY_LIMITED_INFORMATION 0x0800), GetThreadTimes, GetThreadDescription (HRESULT, LocalFree the buffer always;
  EntryPointNotFoundException on old Windows → names unavailable), GetProcessTimes on GetCurrentProcess, GetCurrentThreadId,
  GetCurrentProcessId, CloseHandle. Filter the snapshot to this process id.
- `ServerMeshThreadCpu` (MonoBehaviour): AutoStart at AfterSceneLoad when `MESH_THREAD_CPU == "1"`; inert with one log
  line on non-Windows (`RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`); never in the editor. Capture the main thread
  id (GetCurrentThreadId) on the main thread at install. A startup self-probe: create a short-lived thread named BEFORE
  Start exactly as LiteNetLib does, read its description, and record `names pre-start=present|absent`. Sample on ONE
  background thread (named after Start, so its name reaches Windows; label it Other and report its own cost) every 5 s:
  per-thread kernel+user CPU deltas (keep handles and cached descriptions per thread id until the thread leaves the
  snapshot; re-read only empty descriptions; close every handle; LocalFree every description buffer), grouped by
  Classify; process CPU delta from GetProcessTimes. Hand the finished result to the main thread through a lock or a
  volatile reference swap; the main thread logs (Logging is main-thread only):
  `Threads window 5.00 s: main X litenetlib X jobs X unnamed X other X total X process X ms/s | threads N sampler X ms names pre-start=absent`
  (all values ms of CPU per second, `0.0` format, invariant culture, no braces). Stop and join the thread in OnDestroy /
  OnApplicationQuit.
- Tests `Tests/Editor/ServerMeshThreadClassTests.cs`: "LogicThread" and "ReceiveThread(7770)" → LiteNetLib; "Job.Worker 3"
  and "Background Job.Worker 2" → Jobs; "" and null → Unnamed; "Thread Pool Worker" → Other; the main id → Main even when
  its description is "LogicThread".
