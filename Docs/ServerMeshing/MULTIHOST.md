# Cross-network test: running the mesh on two machines

What to prepare on the second machine, and how to run the mesh across two of them. Everything the add-on does had only
ever crossed loopback; it now crosses a real network adapter, and six of the eight regression scenarios run that way
(§8b, §8c).

Machine names used below:

| Name | Role |
|---|---|
| **A** | This desktop, `192.168.50.83`. Runs the Unity editor, which hosts the central, login, database-manager and map-spawn servers, plus MySQL and the harness. |
| **B** | The second machine. Runs map-server processes only. No Unity, no database, no editor. |

## 1. What we are proving

That a player walks from a cell served by A into a cell served by B and back with no visible break, that the two cells
exchange ghosts across their shared edge over the LAN, and that the cluster, the links and the handoff work with real
addresses instead of loopback. The suggested split for the first run:

| Cell | Host |
|---|---|
| C, N, NE | A |
| E | B |

C and E share an edge, so the bot's usual walk from C into E and back becomes a cross-machine handoff, and the standing
tester on C is ghosted to B across the line. Leave the second map's cell C2 on A, or off, for now.

## 2. On B, before anything else

1. **Windows 10 or 11, 64-bit**, on the same LAN as A, wired if possible. Nothing else is needed: the server build is
   self-contained and needs no Unity, no .NET install and no database client.
2. **Fix its address.** Give B a static LAN address or a DHCP reservation. Note it down; the guide calls it `<B-IP>`.
3. **Match the clock to A.** Both machines' logs are read side by side and the harness greps by timestamp, so a skew of
   even a few seconds makes a cross-host trace unreadable. `w32tm /resync` on both, or point both at the same NTP server.
4. **Turn off anything that throttles or inspects UDP**: the links are LiteNetLib over UDP, and a consumer security
   suite will happily add latency or drop packets and make the results meaningless.

## 3. Copy the build to B

On A, build the server player as usual, then copy the **whole** `Build/Server` folder to B, for example to
`C:\MeshTest\Server`. It must include:

- `kit-ce-project.exe` and `kit-ce-project_Data\`
- `Config\serverConfig.json`
- `MonoBleedingEdge\` and the rest of the folder as produced by the build

Both machines must run the **same build**: the world registry is baked into it, and two hosts disagreeing about cell
rectangles or neighbours is the one failure that will not be obvious in the logs.

B does **not** need `Config\mySqlConfig.json`. Map servers never touch the database directly; they talk to the
database-manager server on A.

## 4. Ports and firewall

Everything is UDP unless noted. A map server's mesh-link port is its map port plus the link offset, which is 10 000 by
default, so a cell on map port 8001 listens for links on 18001.

**On A, allow inbound from B:**

| Port | Purpose |
|---|---|
| 6010 | cluster, where every server registers |
| 6100 | database manager |
| 7500 | login, for clients |
| 8000-8010 | A's map servers |
| 18000-18010 | A's mesh links |

**On B, allow inbound from A:**

| Port | Purpose |
|---|---|
| 8000-8010 | B's map servers |
| 18000-18010 | B's mesh links |

A one-line rule per machine is enough for a lab, for example on A:

```powershell
New-NetFirewallRule -DisplayName "MeshTest" -Direction Inbound -Protocol UDP -LocalPort 6010,6100,7500,8000-8010,18000-18010 -Action Allow -Profile Private
```

and the same on B with only the map and link ranges. Remove the rule when the test is over.

## 5. Changes on A

1. **Advertise A's real address.** In `Build/Server/Config/serverConfig.json` set `publicAddress` to `192.168.50.83`.
   It is currently `127.0.0.1`, which B cannot reach. A's own cells still work, because the LAN address is reachable
   from A too.
2. **Give A's map spawner only A's cells.** `MeshMapSpawnNetworkManager` now has a `servedCells` list. Set it to
   `C, N, NE` on the server-instance prefab, or start the editor with `MESH_SPAWN_CELLS=C,N,NE`. Without this, A starts
   a server for every cell in the registry, B's included, and the cluster rejects B's duplicate registration.
3. **Leave sleeping cells off for this test.** The central still asks the first map-spawn server it knows about when it
   wakes a cell, so with two hosts a sleeping cell can wake on the wrong one. Placement-aware waking is the next piece
   of work; until then, run with `sleepCells` off so every cell is started by its own host.

## 6. Launching B's cells

B runs its cells by hand, one process per cell. No spawner and no editor is involved. From `C:\MeshTest\Server`:

```powershell
.\kit-ce-project.exe -batchmode -nographics -startMapServer `
  -mapName ServerMeshTestMap -channelId E `
  -mapPort 8001 -publicAddress <B-IP> `
  -centralAddress 192.168.50.83 -centralPort 6010 `
  -databaseManagerAddress 192.168.50.83 -databaseManagerPort 6100
```

Points worth knowing:

- `-channelId` is the **cell id**. The server resolves its own rectangle, frame and neighbours from the world registry
  using that id and `-mapName`, so nothing else describes the cell.
- `-publicAddress` must be B's own address. It is what A's cells and the clients are told to connect to.
- Start A first, so the cluster is up; B's cell will retry registration anyway, but the logs are easier to read in order.
- For a second cell on B, repeat with a different `-channelId` and `-mapPort`.

## 7. Checking that it came up

**Where the logs are.** The kit writes its own logs to a `Logs` folder beside the *working directory*, not to Unity's
`-logFile`. Launched from `C:\MeshTest\Server`, B's cell writes:

    C:\MeshTest\Server\Logs\Log_Map(ServerMeshTestMap)-Ch(E)-Alloc(False)-Instance()_<date>.info.log

with `.warn.log` and `.error.log` beside it. Every line below is in the `.info.log`. Unity's `-logFile` is the engine
log and carries almost none of this, which is a good way to waste twenty minutes.

In B's `.info.log`:

- `Own cell from channel id: E of map ServerMeshTestMap, grid (1,0), world x [100, 300) …` with the expected rectangle
  and neighbour count. A wrong rectangle here means the two builds differ.
- `Zone peer registered: C_ServerMeshTestMap -> 192.168.50.83:8000` for each of A's cells, with **A's LAN address**,
  not loopback.
- `Neighbour zone C linked (conn N)`.

On A's C log, the mirror image: `Zone peer registered: E_ServerMeshTestMap -> <B-IP>:8001` and `Neighbour zone E linked`.

If the peers register but the links never come up, the link ports are blocked: that is the map port plus 10 000, and it
is the rule people forget.

## 7a. Driving both machines from one place (recommended)

Relaying instructions to a second operator is slow and loses detail. With OpenSSH on B, one session starts, stops and
reads B's cells directly, and `Tools/ServerMeshing/remoteb.py` wraps it:

    python Tools/ServerMeshing/remoteb.py start [--cell E] [--port 8001]
    python Tools/ServerMeshing/remoteb.py stop
    python Tools/ServerMeshing/remoteb.py status
    python Tools/ServerMeshing/remoteb.py log [--lines 40] [--grep "Handoff"] [--suffix error]
    python Tools/ServerMeshing/remoteb.py fetch      copies B's zone logs into Logs/remote/
    python Tools/ServerMeshing/remoteb.py deploy     copies this machine's server build to B

**A cell runs the build, not the editor's scripts.** An add-on change is invisible on B until the build is rebuilt
here and copied over, so `deploy` is the step between editing and testing. It stops B's cell first (Windows will not
overwrite a running executable's files), leaves B's own `Config` and `Logs` alone, and checks the copy by comparing
`Assembly-CSharp.dll` by SHA-256 rather than by size — the assembly is padded to a 512-byte boundary, so a
build with a few methods added can come out exactly the same length as the one before it, and a size check then
passes happily on a stale copy.

Set-up on B, in an elevated PowerShell:

```powershell
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
Set-Service sshd -StartupType Automatic
Start-Service sshd
```

Then install A's public key. **If B's account is an administrator, Windows ignores `~/.ssh/authorized_keys`** and reads
a separate file with strict permissions instead. This is the step that usually goes wrong:

```powershell
$k = '<contents of A''s meshtest_ed25519.pub>'
Add-Content C:\ProgramData\ssh\administrators_authorized_keys $k
icacls C:\ProgramData\ssh\administrators_authorized_keys /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F'
```

Two things worth knowing. A process started over SSH on Windows is killed when the session closes, because OpenSSH puts
the session in a job object; `remoteb.py start` therefore launches through `Win32_Process.Create`, which escapes it.
And turn the server off again when the testing is done: `Stop-Service sshd; Set-Service sshd -StartupType Disabled`.

## 8. A run by hand

The regression harness now drives both machines (§8b); this is the manual version, useful when something is wrong and
you want to watch it happen.

1. Start A's rig and B's cell, and confirm the link lines above.
2. Put a client on C standing near the C/E edge, and walk a second client from C into E and back.
3. Watch for, on A: `Handoff START … zone C->E`, `Handoff SENT … saved=True demoted=True`, `Handoff ACK … promoted=True`;
   on B: `Arrival … ghost promoted in place`. The ack time in the `Handoff ACK` line is the number that matters, against
   the 200 to 500 ms we see on one machine.
4. Watch the standing client's view: the character crossing the line should never disappear or rubber-band.
5. On both sides the `Mesh stats` line every ten seconds gives ghosts held, link traffic per neighbour and
   `ghostSteer`, the puppet steering error. On one machine that is 0.02 to 0.2 m for a walking ghost; across a LAN it
   should barely change, and a large jump points at the band being too narrow for the new latency.

## 8a. Result of the first run (2026-09-16)

A served C, N and NE; B served E; both wired to the same LAN, with A hosting MySQL and the cluster. A bot walked from C
into E and back. Everything matched the single-machine baseline:

| | one machine | across two machines |
|---|---|---|
| handoff ack after START | 334-485 ms | 300-383 ms |
| ghost steering error, walking | 0.02-0.20 m mean | 0.03-0.17 m mean |
| exceptions | 0 | 0 |

Both crossings promoted the puppet in place, B held the character and handed it back, and B's link carried 11-15 kB/s
out and 3-5 kB/s in while holding one character and its ghosts. A LAN hop costs the mesh nothing measurable, which is
what the design predicted: the band is sized by visible range, not by link latency.

## 8b. Running the regression across both machines

    python Tools/ServerMeshing/regress.py --remote-cells E

`--remote-cells` names the cells the other machine owns. The harness then

- gives this machine's spawner the remaining cells through `MESH_SPAWN_CELLS`, so the two hosts never start the same
  cell;
- starts B's cell for every scenario over SSH and waits until it logs `Own cell`, because each Play recreates this
  machine's cells and a cell left over from the previous scenario holds a dead cluster connection and a dead database
  connection, neither of which the kit re-opens;
- holds the editor's tester client before its login for as long as that takes (`Temp/ServerMesh.HoldRequest.txt`,
  §9b), since a character whose map server has not joined yet has nowhere to go;
- copies B's zone logs into `Logs/remote/` before grading, and **fails** rather than grading a cell whose log is
  missing. A missing log reads as "no lines", which turns every check on that cell into a silent pass: the first
  two-machine run reported `zone E: 0 exception lines` while copying nothing at all.

Two scenarios are skipped in this mode and say so when they are:

| Scenario | Why |
|---|---|
| `sleep` | needs the central to start a cell on demand, and it asks the first map-spawn server it knows; B runs no spawner, so placement-aware waking (§9) comes first |

`crash` **does** run across both machines. Its fault variable is handed to the remote cell explicitly (A's cells
inherit it from the editor through the spawner; B's cannot), `remoteb.py start --restart` stands in for the spawner,
and `remoteb.py clearmarkers` clears B's one-shot marker — `MESH_FAULT_DIE_ON_NOTICE` fires once per
`Temp/ServerMesh.FaultFired.<zone>`, written relative to the working directory, so clearing this machine's `Temp`
does nothing for the other's.

## 8c. Result of the first full suite across two machines (2026-09-16)

A served C, N, NE and C2; B served E. Six of the eight scenarios apply in this mode and all six passed, 71 checks with
no exception on either host:

| Scenario | Checks | What it proves across the network |
|---|---|---|
| `pet` | 12/12 | a summoned pet is promoted in place on both hops |
| `party` | 13/13 | forwarded arrow hits, kill credit and party shares cross the line |
| `harvest` | 8/8 | swings forwarded to the other machine's node, yield back to the real character |
| `build` | 10/10 | placement handed over, the building created there and ghosted back |
| `seam` | 16/16 | a cross-*map* hop: frame-translated save, promoted arrival, client scene swap |
| `seamcold` | 12/12 | the same crossing with the pre-load off, falling back to the kit's scene reload |

Nothing needed a different setting for the two-machine case, which is the result worth having: the same rig, the same
band width and the same timings.

## 8d. What the cross-machine crash costs (2026-09-17)

The mesh behaviour holds: the fault fires on B, A rolls the handoff back, the client falls back to the cell it left,
B's cell restarts and re-registers, and a later handoff to the restarted cell is acked. 13 of the scenario's 14
checks pass. The one that does not is worth more than the others:

| | one machine | across two machines |
|---|---|---|
| client gap over the crash | 6.1 s | **10.5 s** |
| does a watching client see the crossing player pop? | no | **yes** (`playerSwaps=1`) |

**The cost is in the client, not the servers.** B's cell died at 11:29:00 and was serving again at 11:29:03, and A
rolled the handoff back promptly:

    11:29:03  C: Handoff SENT -> E
    11:29:06  C: Disconnected from neighbour (PeerNotFound)
    11:29:08  C: Handoff ROLLBACK ... link to the destination lost   (+5 s)
    11:29:14  C: Handoff ROLLBACK complete                           (the client is back)

(`ROLLBACK complete` is when the client returns, not when the rollback fires. Reading it as the trigger is how an
earlier version of this section came to blame the server for a delay that was never there.)

The client's own log says where the six seconds after the rollback went:

    11:29:06.913  C client connected              (to B's restarted cell)
    11:29:06.963  C enter-game response Error     (a definitive refusal: the reservation died with the old process)
    11:29:13      Migration failed (timeout); falling back to 192.168.50.83:8000

**The client is given an explicit refusal and ignores it**, waiting out `migrationReadyTimeoutSeconds` (10 s) before
falling back. `ClientMigrationController`'s wait loop breaks on its deadline or on `MigrationDisconnected`, and a
refusal is neither — the destination answered and stayed connected. About 7 s of the 10.5 s gap is the client
sitting on an answer it already had.

**Acting on the refusal is necessary but not sufficient, and this is measured, not guessed.** Three builds:

| Change | checks | client gap | `playerSwaps` |
|---|---|---|---|
| none (current code) | **13/14** | 10.5 s | 1 |
| fall back the moment the refusal arrives | 12/14 | **4.1 s** | 65 |
| that, plus a 10 s no-handoff backoff for the rolled-back cell | 11/14 | bot never crossed | 0 |

The fast fallback more than halves the gap and then destabilises the crossing: the client returns while the
destination's failure is still being noticed (its link was only declared lost 8 s after it died), the boundary tick
immediately hands off again, and the second attempt rolls back too. Backing off handoffs to the failed cell for 10 s
stops the thrash and is too blunt — nobody crosses at all inside the scenario's window.

A third attempt went further and is also reverted, but it narrowed the problem usefully. Two things were learned.

**There is no handoff thrash.** Only two handoffs occur; the 65-71 "player swaps" are a **duplicate pet**, two
instances alternating — one standing still, one moving with its owner. `TryPromoteSummonPuppet` matches a puppet to
a summon by `summonId`, and a character returning from a rolled-back handoff can carry a new one, so the promotion
fails, the kit re-summons, and the held puppet stays beside the new pet. The fresh-spawn branch has always swept
those up; the promotion branch did not. That sweep is now in place (it took the swaps from 71 to 0) and is the one
piece of these experiments that was kept — latent in current code, since a client that takes 10 s to come back lets
the puppet expire first.

**Acting on the refusal can abandon a crossing that succeeded.** With the sweep in, the run reached `playerSwaps=0`
and a 3.5 s gap, and then failed differently: the restarted cell accepted the character (`Arrival ... player
registered` on B at 14:23:08) and the client fell back to the origin anyway two seconds later, with no `Handoff ACK`
logged at all. A refusal recorded earlier in a hop is evidently still set when a later attempt within that hop
succeeds. Bouncing a character off a cell that accepted it is worse than the delay being fixed.

So the work is in the migration state machine, not in a constant: when `MigrationRefused` may be honoured, and
whether a refusal that precedes a success inside one hop should be forgotten. The numbers above are the argument for
doing it properly rather than by patch.

## 8e. Degrading the link, and what it costs (2026-09-17)

Cells are co-located to start with, and at most one datacentre later, so steady tens of milliseconds between them
is a case this deployment never meets and is deliberately not tested. What a datacentre does produce is loss,
jitter and reordering, and those are.

`clumsy` 0.3 (with the WinDivert driver) lives in `C:\MeshTest\clumsy` on the other machine and is driven by

    python Tools/ServerMeshing/remoteb.py impair --profile loss2     # lag5 loss05 loss2 loss5 reorder jitter nasty
    python Tools/ServerMeshing/remoteb.py impair --profile off
    python Tools/ServerMeshing/regress.py pet --remote-cells E --impair loss2

The filter is **UDP and scoped to this machine's address**, so SSH (TCP) survives any loss level and the control
channel cannot be cut by the test it is driving. clumsy holds the impairment only while it runs: if it is killed,
or the harness dies, the link restores itself. `regress.py --impair` refuses to run if the impairment cannot be
applied, because a mislabelled row is worse than a missing one. Verified before use: 2% configured dropped 13 of
400 datagrams, `off` dropped 0 of 400.

`pet` across every profile, one run each:

| profile | client gap | handoff ack | steer mean | steer max | player pops | monster pops |
|---|---|---|---|---|---|---|
| `off` | 335 ms | 266 ms | 0.12-0.15 m | 1.73 m | 0 | 0 |
| `lag5` | 450 ms | 352 ms | 0.13-0.21 m | 1.49 m | 0 | 0 |
| `loss05` | 386 ms | 303 ms | 0.04-0.12 m | 1.45 m | 0 | 0 |
| `loss2` | 868 ms | 754 ms | 0.09-0.13 m | 1.06 m | 0 | 0 |
| `loss5` | 452 ms | 327 ms | 0.19-0.33 m | 2.08 m | 0 | 0 |
| `reorder` | 350 ms | 284 ms | 0.12-0.27 m | 1.74 m | 0 | 0 |
| `jitter` | 648 ms | 520 ms | 0.07-0.20 m | 1.11 m | 0 | 0 |
| `nasty` | 665 ms | 529 ms | 0.20-0.34 m | 1.39 m | 0 | 0 |

Everything passes, every profile, with no exceptions anywhere. Three conclusions are worth trusting and one number
in that table is not:

- **No character ever pops.** Across three sweeps, zero *player* puppets were teleported past
  `ghostTeleportDistance`. Monster puppets were, four times, up to 14.26 m, always the same instance drifting
  twice in a row. Visible, but a monster jumping is not a character jumping, and the two are now counted apart
  (`teleports=0p/0o` in the `Mesh stats` line).
- **Loss is absorbed; latency is not.** 0.5% to 5% loss does not move anything consistently — LiteNetLib
  retransmits the reliable parts of a handoff. Added delay does: every latency profile raised the ack above
  baseline in both sweeps that measured it.
- **Single rows are noise.** `nasty` measured 1727 ms of ack in one sweep and 529 ms in the next; `loss2` measured
  246 ms and 754 ms. Run-to-run variance is comparable to the differences between adjacent profiles, so this table
  says "nothing breaks and latency costs more than loss", and nothing finer. A real curve needs repeats per
  profile, which is cheap to add (`sweep.py` takes a scenario name; give it a loop).

A note on how this table got here, because it matters for the next one: the first two sweeps were run with broken
instrumentation and both reported healthy. A puppet teleport was neither logged nor graded, so a 12.89 m monster
pop passed 12/12; and the check written to catch it grepped for a character name, which monster puppets do not
have, so it reported zero while the stats line beside it said `teleports=2`. Only the third sweep's numbers are
trustworthy. Instrument first, then measure.

## 9. What we already know is missing

- **The client ignores an explicit handoff refusal.** It waits out `migrationReadyTimeoutSeconds` instead of
  falling back when the destination answers `enter-game` with an error, which is most of the cross-machine
  crash gap (§8d). Acting on the refusal alone makes the crossing thrash, so it needs the origin's retry
  policy designed with it. Measurements and two reverted experiments are in §8d.
- **A direct login into a cell that outlived a central restart is kicked.** Narrowed on 2026-09-17, not solved.
  What happens, in order: the cell answers `enter-game validated+loaded ok=True` and loads the character at the
  right position; the client loads the scene and sends client-ready; the cell receives it; and 0.4 s later the
  cell drops the peer with `DisconnectPeerCalled` and **nothing written in any log on either machine**. Only a
  *direct* login to such a cell fails; a character that enters on another cell and walks across is fine, which is
  why only `seam` and `seamcold` fail under `--remote-persist`.

  Ruled out: the four synchronous refusals in `DeserializeClientReadyData` (a diagnostic in the add-on names them
  and stays silent here), a stale ghost or reserved arrival (`reserved=0`, no player ghosts on the cell
  beforehand), and login routing (the central routes to E correctly). What remains is the async
  `ValidatePlayerConnection` in `SetPlayerReadyRoutine`: not-ready, the database access-token check, or
  "character already registered". All four of its paths log behind the manager's `LogError` flag, which is off on
  a server build, which is why the kick is silent — turning that flag on for one run is the cheapest next step.

  One trap for whoever picks this up: do **not** treat "the user is already registered on this cell" at
  client-ready as the cause. Enter-game registers the user a moment earlier, so it is true on every successful
  login too; a diagnostic that warned on it fired three times during a run that passed 16/16.
- **A map-spawn server on the second machine.** The central now asks the spawner that owns a cell rather than the
  first one it knows (each spawner advertises its `servedCells` when it registers, and the central routes by that,
  falling back to the old behaviour for a cell nobody claimed). What is still missing is a spawner *process* on the
  other machine: its cell is started by hand through `remoteb.py`, so there is nothing there to receive a start
  request and `sleep` still cannot run across two hosts.
- **A fault path to the other machine.** `crash` sets `MESH_FAULT_DIE_ON_NOTICE` in this editor's environment and
  relies on this spawner restarting the zone. `remoteb.py start --env` already carries a variable across, so what is
  missing is the restart, not the fault.
- **Band sizing under latency.** The band width was chosen assuming a link hop costs nothing. A LAN barely changes that;
  a wide-area link would, and the start-up band check should compute the requirement from the measured link round trip
  rather than a constant.

## 9a. Troubleshooting

| Symptom | Cause |
|---|---|
| B's cell logs `Own cell ...` but never `Zone peer registered`, and every database call times out | **A is blocking inbound traffic to the Unity editor.** Driven from the editor, the cluster, login and database manager all run inside `Unity.exe`, and a Windows Firewall **block** rule for Unity beats any port-based allow rule. Windows creates one silently if a firewall prompt for Unity was ever dismissed. This cost an hour on the first attempt; look for it first. |
| Ping between the two machines fails although everything else works | Windows blocks inbound ICMP by default. Ping proves nothing here. Send a UDP datagram to a port in the allowed range instead. |
| A cell started over SSH disappears seconds later | OpenSSH kills the session's process tree on disconnect. Launch through `Win32_Process.Create`, which is what `remoteb.py start` does. |
| Peers register but no link comes up | The link port is the map port plus 10 000 and is a separate firewall entry. |
| A scenario fails because a cell on the other machine never saw the tester | The tester logged in before that cell joined. Check for `phase=hold` in `Temp/ServerMesh.PlayStatus.txt` and for `remote cell E is serving` in the harness output (§8b, §9b). |
| The tester never logs in and the run times out | A leftover `Temp/ServerMesh.HoldRequest.txt`. Delete it. |
| A cell on the other machine serves but every database call times out after the first scenario | It is a cell left over from the previous Play: the kit does not re-open the database connection. Restart it. |

To find a blocking application rule:

```powershell
Get-NetFirewallRule -Direction Inbound | ForEach-Object {
  $r = $_; $a = $r | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue
  if ($a.Program -match 'Unity.exe') { $r | Select-Object Action,Enabled,Profile,Name }
}
```

Disable the blocking one with `Set-NetFirewallRule -Name "{...}" -Enabled False`, keeping it for later rather than
deleting it.

## 9b. Start-up ordering between the two machines

The cluster lives in A's editor and only exists while A is in Play, so B's cell and A's rig cannot both be "already
running" when the other starts. What the kit does with each order is worth knowing, because two of the three cases
recover by themselves and one does not:

- **B's cell connects, then the cluster goes away** (A stops Play between scenarios). LiteNetLib raises
  `ConnectionFailed`, which the kit treats as a disconnect and answers by reconnecting every five seconds, for ever.
  The cluster connection recovers by itself.
- **B's cell starts while the cluster is down.** Same path, so it joins as soon as A reaches Play. Nothing to do.
- **B's cell cannot open a client socket at all** (`StartClient` returns false). The client is then inactive, its
  `Update` polls nothing, and no disconnect is ever raised: the process runs for ever, serving nobody, and never
  retries. The add-on covers this one — `MeshMapNetworkManager.clusterConnectRetrySeconds` (5 s, 0 disables) re-opens
  the connection and stands down as soon as the transport client is up, leaving every later failure to the kit.
- **The database connection, in every one of those cases.** The kit opens it once, in
  `MMOServerInstance.StartDatabaseManagerClient`, and never again: the transport calls `Manager.StopClient()` on a
  disconnect, `DatabaseNetworkManager` does not override `OnClientDisconnected`, and the base is an empty virtual. The
  cell keeps running and keeps accepting players while every save fails, which is why the harness used to restart B
  for every scenario. `MeshMapNetworkManager.databaseReconnectSeconds` now re-opens it, and
  `regress.py --remote-persist` is the test: it leaves the cell running so it has to recover on its own.

`--remote-persist` is **experimental**, and useful for one thing: it is how the database reconnect is regression
tested. The cell is never restarted, so before each later scenario it has to re-register with the cluster, relink and
re-open its database connection unaided, and the run fails if any of that is missing. Observed recoveries: 5 s for
all three signals, repeatedly.

What it cannot yet do is run a scenario that logs a character **directly into** the persisted cell. `seam` and
`seamcold` put the bot at x=280, inside E, so its login goes straight there; the client connects, the cell answers
`enter-game response Success`, and the character then never spawns. The four scenarios whose characters enter on a
local cell and walk across (`pet`, `party`, `harvest`, `build`) pass. The cause is not established — what is known is
that the cell has been through a central restart, and that the kit clears player state through central-driven paths
(`OnPlayerCharacterRemoved`, force-despawn) that never arrive when the central is the thing that restarted. Treat it
as an open question about surviving a central restart, not as a harness quirk: in production a central restart is
rare but not impossible, and every map server would be in exactly this state afterwards.

Do not trust the kit's `IsClient` or `IsClientConnected` to mean "the other end is there". Both go true the moment a
socket exists and `Connect()` returned a peer — seconds before that peer connects, and even when it never does.
The add-on reads `ITransport.IsClientStarted` (a peer in `ConnectionState.Connected`) instead. The same optimism
explains a cell logging `Cannot GetBuildingsAsync status: Timeout` 30 s after start-up: the map server issued the
query before the connection existed, and `ClientSend` dropped it silently.

The tester client is held rather than raced: while `Temp/ServerMesh.HoldRequest.txt` exists, the auto client parks
before logging in and reports `phase=hold`. The harness creates it before Play and removes it once B's cell is
serving; `holdTimeoutSeconds` (180 s) fails the run instead of hanging if nobody ever removes it. Delete the file by
hand if an interrupted run left one behind — the harness does that at the start of a two-machine run for you.

## 10. When it works

The same procedure is the one used for a cloud pair, with three changes: the addresses are public, the database is a
managed MySQL that both hosts reach, and the build is a Linux dedicated-server build instead of a Windows one. Ports and
arguments are otherwise identical.
