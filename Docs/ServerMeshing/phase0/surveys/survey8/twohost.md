# Step 8 survey: bots and sink on machine B (read-only, 2026-10-04, machine A, commit 0f81895)

Read-only: grep/read, A's firewall rule, one SSH query to B.

**Blocker:** a bot started on B cannot reach A's login server. The auto client calls `client.StartCentralClient()` with
no address (`Assets/ServerMeshing/TestMap/ServerMeshAutoClient.cs:420`), which uses the prefab's
`networkAddress: localhost` (`Assets/BaseDemo/Prefabs/MMOClientInstance-CE.prefab:100,159`); bot logs read
"Connecting to login server localhost:7500". No argument overrides it; `MMOClientInstance.StartCentralClient(address,
port)` exists (`MMOClientInstance.cs:173-178`). Step 8 needs a login-address bot argument, a rebuild and a deploy.

## 1. twohost.py and remoteb.py
- **twohost.py** (`:149`): `up` (`:49-93`: kill_bots, `db_reset((86.5,0),(20,0))`, Play with `MESH_SPAWN_CELLS`;
  prints the command B should run, `:70-75`; polls C's log for `Zone peer registered`/`Neighbour zone ... linked`);
  `walk` (`:96-129`: one bot on A via `r.launch_bot("twohost","meshbot",[walk,140,-40])`, grades `Handoff SENT/ACK`,
  parses `registered N ms after the notice, M ms after START`); `status` (`:132-137`); `down` (`:140-144`: stops Play,
  kills A's bots). Never calls remoteb.
- **remoteb.py** (`:402-403`): start, stop, status, log, fetch, deploy, clearmarkers, impair, sink-start, sink-stop.
  Defaults `:32-39` (B 192.168.50.211, user brend, key `~/.ssh/meshtest_ed25519`, dir `C:\MeshTest\Server`, A
  192.168.50.83); exe `C:\MeshTest\Server\kit-ce-project.exe` (`:187`). Cells: `start-cell-<cell>.cmd` with optional
  `set K=V` and `--restart` loop (`:168-198`), `launch_detached` (`:138-160`: scp, then `Invoke-CimMethod Win32_Process
  Create`, escaping the SSH job object), polls `running()` 12×2 s. Stop by command line via Win32_Process excluding its
  own SSH session (`NOT_THIS_SESSION`, `stop_command`, `:89-104`). Sink: `sink_arguments` (`:119-135`), launched as
  `start-sink.cmd` (`:269-294`), `--sink-port` required; sink-stop kills `-meshPhantomSink`/`start-sink*` (`:92,
  297-300`). Fetch (`:328-347`): `scp <dir>/Logs/*.log` into `Logs/remote/` (never cleared; includes sink.log).
- **Bots on B would break remoteb:** bots run the same exe as cells and `IS_CELL` = "kit-ce-project.exe without the
  sink flag" (`:85`), so they count as cells in `running()`, make `start` refuse ("B already has a server process",
  `:170`), make `regress.remote_running()` lie, and `remoteb stop` (STOP_CELLS) kills them. Fix: `IS_BOT`
  (`CommandLine -like '*-meshAutoClient*'`) excluded from `IS_CELL`.
- **What `bots` modes need:** a pure `bot_arguments(...)` (testable like `test_remoteb.py:13-84`) mirroring
  `regress.launch_bot` (`regress.py:242-248`): `-batchmode -nographics -meshAutoClient -meshUser meshbotK -meshChannel
  C -meshAutoMode … -meshWalkX … [-meshWalkZ/-meshReturnX/-meshStopOnMigration/-meshBotNetSeconds 5/-meshRigShips v]
  <login override> -logFile C:\MeshTest\Server\Logs\bots\<user>.log`; one `start-bots.cmd` with `start "" /b
  kit-ce-project.exe …` per bot plus `timeout /t 1` staggering; count `count_command(IS_BOT)`; stop
  `stop_command(IS_BOT or '*start-bots*')`; a separate bot-log folder and fetch (`fetch_remote` runs every ≥ 5 s during
  grading, `regress.py:118-129`); results read with `regress.bot_result`/`wait_for_bot` (`:677-684, 960-967`); markers
  "Logged in as", "In map at", RESULT, `FAIL (`.

## 2. Bot arguments and lines (ServerMeshAutoClient.cs)
- Arguments (`:37-49`, parsed `:158-203`): `-meshAutoClient`, `-meshAutoMode`, `-meshChannel`, `-meshUser` (character
  = user+"Char", `:194`), `-meshWalkX/Z`, `-meshMount`, `-meshPet`, `-meshReturnX`, `-meshSeamPreload`,
  `-meshStopOnMigration`, `-meshBotNetSeconds`. Modes (`:62`): none, walk, self, stand, pace, observe, fight, pvp,
  harvest, build. Elsewhere: `-meshRigShips` (`TestMap/Ships/ServerMeshShipLoad.cs`; `p0ships.rig_ship_bot_args`,
  `p0ships.py:303-319`). Not settable: `timeoutSeconds` (240, `:105`, login+select+walk), `observeSeconds` (100,
  `:101`), `holdTimeoutSeconds`.
- Lines (`Debug.Log "[MeshAuto] …"`, unstamped): `RESULT walk: migrations= allReady= maxGap=…ms maxJump= …` (`:696`);
  `RESULT stand` (`:722`; the process does not quit after RESULT); per hop only in `WalkTo`: `Migration observed:
  seamless= playerReady= gap={playerReadyMs}ms jump= resync= …` (`:1432-1440`); pace (`:738-763`, 8 m for 600 s) logs no
  per-hop line, only the Bot net window's hops; Bot net window (`:313-321`): kB/s and pkt/s in/out, hops, rtt, players
  seen, `at <UTC>` on the bot machine's clock.
- **handoff_ready (b9)** — the sheet only names it (`phase0_build_sheet.md:1162, 1035`). Candidates: server side
  `Handoff ACK <name> by zone Z: registered N ms after the notice, M ms after START`
  (`Mesh/MeshMapNetworkManager_Ghosting.cs:3410-3412`; origin clock only, skew-free; parsed only by `twohost.py:117`);
  Mesh stats aggregate count/sum/max (`NoteHandoffCompleted`, `:121-126`); client `gap=` (playerReadyMs).
- b9 setup: halves cut at x = 0 (C (-300,-300,300,600), E (0,-300,300,600), `rigrun.py:38`); walk gives two hops per
  bot run; repeated crossings need a new mode or pace (whether an 8 m pace triggers a handoff each time under band and
  hysteresis is unverified).

## 3. Accounts and login throughput
- DB: MySQL on A via pymysql reading `Config/mySqlConfig.json` (`provision.py:106-124`; `regress.py:16`; not opened).
  `provision.py` handles meshtest/MeshTester2 and meshbot/meshbotChar (`:100-102`), password `meshtest123`; meshbot2..N
  self-register (`:15`): on login failure the bot calls `RequestUserRegister(user, pw, "<user>@example.com")` (`:69-74,
  441-453`), then creates `<user>Char` (`:467-496`).
- meshbot2 and meshbot3 already exist (`Logs/regress_load*_meshbot2.log`: "In map at (40.23,…)"); bot01-16 used on
  2026-09-14 (DB not queried).
- Throughput: on Timeout the bot retries login 4× with 2/4/6 s backoff (`:430-440`), then falls through to register,
  which fails `USERNAME_EXISTED` (`Logs/load_bot_bot05.log`); code comment "Sixteen bots logging in at once queue
  behind the login server's database" (`:435`); map select retries every 2 s on `MAP_SERVER_NOT_READY` (`:503-516`). No
  tool staggers launches (`loadtest.py:80-81` back-to-back; no `launch_bots`). Step 6 precedent: one unmeasured
  registration run (`accept_step6.py:48, 347`; `step6_design.md:242`).
- Registration pass needs Play up (login lives in A's editor), staggered launches (≤ 1-2/s suggested), can run from A.
  Nothing resets meshbot2..N positions; a bot saved on E's half logging into C is handed off at spawn (DESIGN CE-4
  note), polluting b9 counts: a rig-only position reset is advisable (like step 9's `provision.py --burst`, sheet :887).
  `rigrun.run_window`'s `db_reset` puts meshbot at (0,0), on the halves cut (`rigrun.py:43, 97`).

## 4. Reusable two-machine support in regress.py
- `--remote-cells` (`:986-989, 1075-1115`; one remote cell; sets `MESH_SPAWN_CELLS`; skips sleep/wake); `remote()`
  (`:112-115`); `zone_log` redirect to `Logs/remote` (`:133-135`); throttled `fetch_remote` (`:118-129`); clock-free
  line-count marks `remote_log_mark`/`remote_lines_after` (`:498-530`); `start_remote_cells` (`:306-347`); HOLD
  (`:273-281`; holds only the editor tester — a built bot's `HoldPath` is relative to its own build folder,
  `ServerMeshAutoClient.cs:145`); `finally` cleanup (`:1176-1180`).
- Gaps: `kill_bots`/`stop_play`/`kit_process_count` touch only A's build path (`:193, 230-231`), so every run needs a
  remote bots-stop in `finally`; `rigrun.run_window` kills A's bots at start and blocks to the end
  (`accept_step6.run_window_with_bots` `:98-132` and `accept_step7_sink`'s thread pattern `:43-70` are the templates).

## 5. Risks
- Firewall (live): A's `MeshTest` rule = Inbound Allow UDP 6010, 6100, 7500, 8000-8010, 18000-18010 from
  192.168.50.0/24. `RIG_PORTS` goes to 8015/18015 (`regress.py:198`) and a spawner-restarted cell takes a new port
  (MULTIHOST:334-335), possibly outside the rule. A's cells advertise 192.168.50.83 (`MMOServerInstance-CE.prefab:319`).
- B's other servers (live): RelicTestServer, RelicAuth and a GitHub runner run; "Relics of Imperium" holds UDP 6010,
  6100, 7000, 8000, 18000 on B; 7500 free. B's cells must avoid 8000 (remoteb's 8001 default is fine); sink base 9000 and
  bot sockets free. Killing by `Name='kit-ce-project.exe'` + command line cannot touch Relic.
- Clock skew (live): B 08:58:25.57 vs A 08:58:29 after an SSH round trip → ~1-4 s behind. Affects Bot net `at` stamps
  from B bots, B cell lines against A's start string in `zone_lines` (`:533-558`), `remoteb.log_path` using A's date
  (`:163-165`). ACK totals and line-count marks are skew-free. Per run: measure the offset (SSH `Get-Date`, half the
  round trip) or `w32tm /resync` (MULTIHOST:33-34).
- Capacity: B has 24 logical processors and 47.6 GB; per-bot CPU/RAM unknown; whether batchmode bots are frame-capped
  is unverified (no client-side `targetFrameRate` found in `Assets/ServerMeshing`).
- Build parity: bots on B run B's deployed build: `deploy` and its SHA-256 check (`remoteb.py:355-397`; stops cells and
  sink first). B's Config is not deployed (`:352`).
