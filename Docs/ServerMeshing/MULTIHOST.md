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
| `crash` | injects a zone fault through the editor's environment and relies on this machine's spawner restarting the zone; neither reaches the other machine yet |
| `sleep` | needs the central to start a cell on demand, and it asks the first map-spawn server it knows; B runs no spawner, so placement-aware waking (§9) comes first |

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

## 9. What we already know is missing

- **Placement for sleeping cells.** The central wakes a cell on the first spawner it knows, not the one that owns the
  cell. `servedCells` fixes which cells a host starts at boot; teaching the central to match a cell to its host is the
  follow-up.
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
  The cluster connection recovers; the *database* connection does not, and there is no retry for it, which is why the
  harness restarts B's cell for every scenario instead of leaving it up.
- **B's cell starts while the cluster is down.** Same path, so it joins as soon as A reaches Play. Nothing to do.
- **B's cell cannot open a client socket at all** (`StartClient` returns false). The client is then inactive, its
  `Update` polls nothing, and no disconnect is ever raised: the process runs for ever, serving nobody, and never
  retries. The add-on covers this one — `MeshMapNetworkManager.clusterConnectRetrySeconds` (5 s, 0 disables) re-opens
  the connection and stands down as soon as the transport client is up, leaving every later failure to the kit.

The tester client is held rather than raced: while `Temp/ServerMesh.HoldRequest.txt` exists, the auto client parks
before logging in and reports `phase=hold`. The harness creates it before Play and removes it once B's cell is
serving; `holdTimeoutSeconds` (180 s) fails the run instead of hanging if nobody ever removes it. Delete the file by
hand if an interrupted run left one behind — the harness does that at the start of a two-machine run for you.

## 10. When it works

The same procedure is the one used for a cloud pair, with three changes: the addresses are public, the database is a
managed MySQL that both hosts reach, and the build is a Linux dedicated-server build instead of a Windows one. Ports and
arguments are otherwise identical.
