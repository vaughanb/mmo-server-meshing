# Dynamic cells, Slice 7: on-demand links (implementation contract)

Revision 1, 2026-10-20 on machine A, after two independent critiques (log at the end) of revision 0, itself from a read of HEAD 8ce1332 (DESIGN 0.84, package 0.84.0):
`Mesh/MeshMapNetworkManager_Ghosting.cs` (link upkeep, every record table, every send to a zone), `CellMeshLink.cs`,
`_Layout.cs` (the apply path), `_Prune.cs`, `_Fence.cs`, `_Readiness.cs`, `_StaticHandoff.cs`, `_BuildingTransfer.cs`,
`_SplitEntities.cs`, `_Actions.cs`, `MeshWorldRegistry.cs` (adjacency), `MeshLayoutOverride.cs`,
`MeshDynamicSetupChecks.cs`, `Editor/ServerMeshSetupWindow_Dynamic.cs`, `MeshCentralNetworkManager.cs`, the kit's
`ClusterServer`/`MapNetworkManager` online relay, and `Tools/ServerMeshing/regress*.py`. The plan is complete (0.83);
DESIGN §15 "what the plan leaves" and SETUP §6a.12 name the scale ceiling this slice removes: with `linkAllZones` (the
default) every cell of a map links to every other, a cell's link server takes 64 connections, so about 64 live cells per
map is the limit; a 10 km map at the recommended cell sizes, or a busy map cut by the policy, needs more. **A map can now
run on-demand links: a cell keeps links only to the cells it needs (its band neighbours under the applied layout, and any
cell a record of it names), opens a link the moment one becomes needed, and closes it once it has been unneeded and idle
for a while; it never closes a link a pending handoff, ack, ghost, forwarded hit or lent/borrowed record depends on. A
party share for a member beyond the band, the one thing `linkAllZones` exists for (0.33), goes through the central
instead of a link to every cell.** Off by default: Static maps, and Dynamic maps that do not turn it on, run today's code.

Read first: this contract; `slices/slice6_design.md` (its checks are adjusted here) and DESIGN 0.33 (why `linkAllZones`
exists). Where they disagree: THIS CONTRACT, then 6's, 5's, ... contracts.

User decisions: build on-demand links (2026-10-20, through the session that started this one). Choices the user has not
seen are marked (D).

## Rules
As 2b-6: Mesh/ game-neutral; **Static maps byte-identical unless a setting turns the new mode on** (no new wire message,
log line or behaviour without `onDemandLinks` or a `MESH_LINK*` toggle; a toggle-free rig run reads exactly as 0.84);
`linkAllZones` keeps working as it does today; log lines one physical line, invariant numbers, no braces, never the word
"Exception"; files under ~500 lines; `MeshMapNetworkManager.cs` and `_Ghosting.cs` take one-line hooks only (new logic in
a new partial); no kit edit; no edits or compiles while a rig chain measures; nothing committed.

## Kit and add-on facts the design rests on (survey, 2026-10-20)
- Links are one-way pipes: each cell runs one `MeshLinkServer` (inbound, `StartServer(port, 64)`) and one
  `MeshLinkClient` per zone it links to (outbound). Every message goes client → server; a reply goes over the replier's
  own client to the sender (`_linkClients[fromZone]`). A cell's 64 is its **inbound** count: the cells that link to it.
- `EnsureNeighbourLinks` (`_Ghosting.cs:414`) runs every second and on every applied layout: with `linkAllZones` every
  cell of the own map, plus `OwnCell.bandNeighbours` (other maps' cells near ours included). Nothing ever closes a link
  except `CloseLink` for a Retired zone (`_Prune.cs:127`) and the stop path.
- Band neighbours are symmetric (`MeshLayoutValidator.BandNear`: touching, or a gap of the interest rectangles within the
  band when either is Dynamic) and at most 32 (`MaxNeighbours`; rule 8 refuses layouts past it on Dynamic maps). A
  Static grid cell has 8.
- What sends to a zone, and to which: snapshots, removes and replays to band neighbours (`SendBandSnapshots`,
  `SendToZonesStreaming`); hits, rewards, kills, pickups, actions and party shares to a ghost's `OwnerZoneId`; handoff
  notices and player/monster/static/server-character/building handoffs to the owner of a position
  (`world.OwnerOf`), normally a touching neighbour but any cell after a warp; `MonsterGone`/`LentMonsterOwner` to a
  borrowed monster's `HomeZoneId` (a monster two cells from home: not a neighbour); `LentStatic` to a static's home; acks,
  hit results, loot, static/monster/building replies to the sender of the request; `RegistrationState` to every linked
  Dynamic cell every 2 s; the party share for a member that is neither here nor a ghost here is **broadcast to every
  link** (`BroadcastPartyShare`), which with `linkAllZones` is every cell of the map. `IsRemotePartyMember` is
  `linkAllZones && ...`, so with `linkAllZones` off such a member gets no share today.
- Records that name a zone: `_pendingHandoffAcks` (zone), `_incomingHandoffFrom` (fromZone), `_deferredAcks` (zone),
  `_ghosts`/`_ghostStatics` (identity `OwnerZoneId`), `_lentMonsters` (ownerZone), owned monsters' `HomeZoneId`
  (borrowed), `_lentStatics` (ownerZone), `_borrowedStatics` (home), `_staticLoans` (toZone), `_buildingReleases`
  (toZone). Each is mirrored on the other cell: a lent record ↔ a borrowed one, a pending ack ↔ an incoming notice, a
  ghost ↔ the owner streaming to us (a band neighbour), a loan ↔ the receiver's reply.
- The release paths read "unreachable" as "not linked": `ReleaseRecordsOfUnreachableZones` (every second) releases lent
  and borrowed monsters of a zone that is linked neither way and that we were ever linked with (`_neighbourIncarnation`
  or `EverConnected`); `OnMeshNeighbourDisconnected` releases them and rolls back handoffs when an inbound connection
  drops and our own link is down too. With links closed on purpose, both would read a closed link as a dead zone.
- The kit's online relay (`UpdateMapUser`, `SocialCharacterData`) says that a user is online, never where: no map
  server knows which cell holds a remote party member. The central has a cluster connection to every map server
  (`ClusterServer.MapServerPeers`, `CentralServerPeerInfo.channelId`/`refId`) and the add-on already sends its own
  cluster messages both ways (`MeshClusterMsg` 30010-30021).
- `MeshLinkClient.Send<T>` is the one path of every typed sender (Hello alone uses `SendPacket`); the server reads every
  packet in `OnServerReceive` (`ReadPacket`; the message type is the first packed ushort).

## Settings
- `MeshMapLayout.onDemandLinks` (bool, default false), per map in the world registry: the cells **of that map** link on
  demand; `linkAllZones` is then ignored for them. Honoured on Static and Dynamic maps alike (a Static map with more cells
  than a link server takes needs it too); Validate judges only worlds with a Dynamic map (6's decision) and says nothing
  about it on a Static-only world. (D) Per map rather than per prefab: the Validate check is per map, and every process
  reads the same registry. `CopyModeFrom` copies it. The field is not written into the rig's registry asset (it is not
  serialized there until an editor save, like `cellMode` today).
- On `MeshMapNetworkManager` (new partial): `linkIdleSeconds` 30 (an unneeded link closes after this long without a
  message either way), **never below `max(handoffAckGuardSeconds, reservedArrivalTimeout) + 5` = 25 s**: every mirrored
  record waits at most that long for its other side (critique A); `linkOpenGraceSeconds` 10 (an on-demand link is not
  taken for a dead zone before it has been tried this long).
- Rig toggles. `MESH_LINKS=<map|all>:<OnDemand|Off>[,...]` sets maps' `onDemandLinks` ("all" first, then each map
  named) in the same runtime copy as `MESH_CELL_MODE` (`MeshLayoutOverride.Resolve`; its early returns and log learn the
  new key, so `MESH_LINKS` alone makes the copy). `MESH_LINK_CAP=<n>` (every cell's link server capacity, default 64)
  and `MESH_LINK_IDLE=<s>` (`linkIdleSeconds`, floor as above) are read by `MeshLinkEnv` at server start and never force
  a registry copy. None set: nothing changes.

## Decisions

### The need rule (pure `Mesh/MeshLinkNeeds.cs`)
1. **Need.** A zone Z other than our own is *wanted* when (a) Z is a band or touching neighbour of our cell in the applied
   layout (reason `band`), (b) a record of ours names Z (reason `record`: pending acks, announced arrivals, deferred acks,
   ghosts and static ghosts by owner, lent monsters and statics by owner, borrowed monsters and statics by home, static
   loans, building releases, a home notice waiting for its link (7a), and the owner of the position of every owned
   character standing outside our cell, which covers a warp's or a respawn's far handoff), (c) we heard a counted message
   from Z, or Z said hello on a new inbound link, within `linkIdleSeconds` (reason `recent`: we may owe it a reply, and the
   link back comes up with the link it opened), or (d) a path demanded it (reason `demand`, 4). A wanted zone with no link
   gets one at once. Retired zones are never wanted; a zone the cluster has not told us about cannot be linked
   (`EnsureLink` needs its peer) and is tried again every tick.
2. **Close.** A link is closed when Z has not been wanted, and no counted message has gone either way on it, for
   `linkIdleSeconds` (one timestamp: the last moment it was wanted or carried a counted message). Band neighbours are
   wanted every tick, so they never close; a record keeps its zone's link for as long as it exists plus the idle time.
   Not counted: `Hello`, `RegistrationState` (sent to every linked Dynamic cell every 2 s, and back over inbound links
   by 6a) and `LinkClose`; otherwise two cells would keep each other's links for ever. The hello's one-time `recent` does
   not renew.
3. **Bounded.** Inbound at a cell = the cells that want it. Band neighbours are symmetric and every record is mirrored
   once its notices are delivered (7a), so a cell's inbound is its band neighbours (≤ 32) plus the zones it has live
   business with; nothing scales with the map's cell count. (`RecomputeAdjacency` truncates a band list at 32 where
   rule 8 does not apply, Static maps and other maps' cells; `recent` covers a one-sided neighbour while it streams.) The
   pure book (`MeshLinkBook`) holds per zone the reason of the tick, the last active time, the last heard time, the start
   of the current want and the close notice time; `Settle(now, linked, wanted, close)`; `IsSettling` (7). The caller
   passes the clock.
4. **Demand.** `DemandLink(zone, reason)` marks a zone wanted now (and so for `linkIdleSeconds`) and opens its link at
   once: the player handoff's "link to zone X is down" branch (`MeshMapNetworkManager.cs:963`, one hook line; its
   throttled warning stays, a known first-attempt line) and every deferred home notice (7a) call it.

### Upkeep (new partials `MeshMapNetworkManager_Links.cs`, `_LinkReports.cs`)
5. **Where it runs.** `EnsureNeighbourLinks` gets one hook line at its top: on an on-demand cell it runs the upkeep
   instead (gather wanted zones with reasons, `Settle`, stop links whose notice went out, flush home notices, `EnsureLink`
   each wanted zone, announce the closes) and returns. It already runs every second and right after every applied layout,
   so a new band neighbour is linked within the apply and one that left closes `linkIdleSeconds` later; the layout path
   is unchanged.
6. **The close is announced, and a closing link reads as down.** `LinkClose` (133) goes first; from that moment the
   client's `IsConnected` is false (`BeginClose`), so no handoff, lend, loan or notice is sent over it and every handoff
   gate treats the zone as unlinked (critique B7, A5: nothing can create a mirrored record on a receiver that will not
   release it); the client is stopped half a second later on the next tick and removed with its streamed sets and stats
   entry (`_meshPeers` and the zone's incarnation are kept). A zone wanted again while closing is reopened after the
   stop. The receiver marks that connection idle-closed; when it drops, one hook line at the top of
   `OnMeshNeighbourDisconnected` hands it to the new partial, which forgets the connection (zone, frame offset,
   registration report), logs Info, and **releases nothing and rolls nothing back**: the zone said it is alive and done
   with us, and by the mirror rule holds nothing that names us.
6a. **Readiness without the reverse link (critique A1, a blocker in revision 0).** A handoff to a Dynamic cell waits for
   the target's live `RegistrationState`, which the target sends over *its* links; a far target has no reason to link
   to us, and our notice cannot go before its report, so the handoff waited for ever. An on-demand cell therefore also
   sends its report back over every inbound connection from a Dynamic map's cell (on change, on a new connection, every
   2 s: one hook line at the end of `SendRegistrationStates`); `MeshLinkClient` handles it and the manager keeps it as the
   target's report **for that connection only** (`ConnectedAt`); `NeighbourBlocksHandoff` reads it when the target's own
   report is missing. The hello also makes the zone `recent` (1c), so the link back, and the replies it carries, come up
   with the link it answers.
7. **The release paths are told about links that are coming up.** `ReleaseRecordsOfUnreachableZones` (one hook line in
   its loop) and `OnMeshNeighbourDisconnected`'s release and rollback (the two conditions gain `&& !LinkStillOpening`) do
   not take a zone for gone while, on an on-demand cell, our link to it is not up and its current want began less than
   `linkOpenGraceSeconds` ago, or its last connect was refused (`ConnectionRejected`: a full link server, not a dead
   cell). The want of a band neighbour is as old as the cell, so a crashed neighbour is released as today; the pending
   handoffs keep their own guards (link down 3 s, ack 20 s). Note `wasLinked` (sweep) now means "linked once in this
   process", which a far zone becomes after one on-demand link.
7a. **Home notices wait for their link (critique A2).** `LentMonsterOwner` (Ghosting two sites, `_SplitEntities`),
   `MonsterGone` and `LentStatic` (two sites) were sent only if the home link was up at that instant; with every cell
   linked it always was. On an on-demand cell a notice whose link is not up is kept (`DeferWhileUnlinked`, one hook per
   site), its zone is wanted (record) and demanded, and it goes out once the link is up (`Notice <what> to zone <Z> sent
   <s>s late`), or is dropped after 60 s with a warning (the home's record then stays as it was, as a dropped notice
   left it before). The mirror in decision 3 holds once these are delivered.
7b. **A restart seen late (critique A3).** `OnMeshNeighbourHello` releases what a restarted zone's old process held:
   records whose owner was set before `linkUpAt`, our link's up time. On demand our link may come up long after the
   restart, and a third cell may have re-pointed a record to the new process meanwhile; the cutoff is then the new
   incarnation's start (its id is its UTC start in hex ticks) less 2 s of clock skew (`RestartCutoff`, one hook line).
8. **Traffic stamps.** `MeshLinkClient.Send<T>` stamps `LastSentAt` after its `IsConnected` check, except for `Hello`,
   `RegistrationState` and `LinkClose`; `MeshLinkServer` peeks each data packet's type (position restored before
   `ReadPacket`) and calls `NoteLinkHeard(connectionId)` for the counted types, on an on-demand cell only. A snapshot
   that overtakes the hello (another channel) finds no zone for its connection and is not stamped: harmless.

### Party shares beyond the band (the central relays them)
9. On an on-demand cell `IsRemotePartyMember` reads `(linkAllZones || LinksOnDemand) && ...` (one-line edit), and
   `BroadcastPartyShare` gets one hook line after it builds the message: on an on-demand cell with a registered cluster
   connection it sends `MeshClusterMsg.PartyShareRelay` (30022: map id, sender cell id, the share) to the central and
   returns; otherwise the broadcast over the links runs as today. The central (`MeshCentralNetworkManager_Relay.cs`)
   takes the map from the sender's registration (`MeshClusterCoordinatorTransport.TryGetMapServer`) and sends the share
   on to every other cell's map server of that map (`GetMapServers`: instance servers are not cells; pure selection
   `MeshPartyRelay.Targets`); each cell hands it to `OnGhostPartyShare` as a broadcast, so only the cell holding the
   member applies it. Cost: one message up and N-1 down per remote share over existing cluster connections. (D) Same map
   only: the kit shares with members on the same map server; today's broadcast also reached other maps' cells near the
   sender. During a central outage a remote share falls back to the links (band neighbours). The cell's handler is
   registered on every map at server start (inert until a share comes).

### Capacity, logs and the checks
10. **Capacity.** `StartServer(port, 64)` becomes `StartServer(port, LinkServerCapacity)` (one-line edit): 64, or
    `MESH_LINK_CAP` (logged at start when set). A refused connect is already logged by the client (`Disconnected from
    neighbour (ConnectionRejected)`), and marks the zone full, not gone (7).
11. **Lines (on-demand cells only):** at start `Mesh links on demand: band neighbours and the cells our records name;
    an unneeded link closes after <s> s idle (linkAllZones ignored)`; `Mesh link to zone <Z> opened on demand
    (<reason>)` for reasons other than band (`demand: <what>` for a demand); `Mesh link to zone <Z> closed on demand:
    unneeded and idle <s> s`; receiver `Neighbour zone <Z> unlinked (idle close)`; `Neighbour zone <Z> reports itself
    <registered|not registered> over our link to it`; `Notice <what> to zone <Z> waits for the link to it` / `sent <s>s
    late, once the link came up` / `dropped: no link to it for 60s` (warning); every stats interval after the `Mesh
    stats` line (one hook line) `Mesh links: <n> out (<b> band, <r> record, <t> recent, <d> demand, <c> closing), <m>
    in; opened <o>, closed <x>; peak out <po>, peak in <pi>`; with trace on, `Party share for remote member <id> relayed
    through the central to map <M>: ...`.
12. **Validate** (`MeshDynamicSetupChecks.LinkFindings`): a map with `onDemandLinks` is not judged by the all-zones count
    (its links are its band neighbours, which the band check bounds at 32, plus live business); the warning's advice
    becomes `(turn onDemandLinks on for the map, lower maxCellsPerBase, or turn linkAllZones off on the map managers)`;
    the OK line of an on-demand map adds `, links on demand`. Still judged on worlds with a Dynamic map only. No Fix (it
    changes runtime behaviour).

### Rig
13. **`linkcap`** (new, `regress_links.py`, graders in `riglinks.py`, editor topology): the test map re-cut by
    `MESH_LAYOUT_OVERRIDE` into a Static 4x4 of 150 m cells, ids `L<col><row>` (col and row 0-3 from -300), plus C2:
    17 cells, 17 ports of 8000-8063. `MESH_LINKS=all:OnDemand`, `MESH_LINK_CAP=10`, `MESH_NO_MONSTERS=1` (a walk with
    nothing to die of): with `linkAllZones` an L cell would need 15 or 16 inbound links, over 10. The tester stands at
    (-100, 100) in L12; the bot starts at (-200, 75) in L02, walks east at z 75 to x 160 (three lines) and back to x 100
    (one): four migrations, about 140 s. Graded: every cell logged (expect = the 16 ids and C2); every cell heard a hello
    from each of its touching neighbours (`Neighbour zone X linked`, computed from the rectangles; C2 touches L31 and
    L32); no `ConnectionRejected` anywhere; every cell's last `Mesh links` line has out ≤ its touching count + 2 and peak
    in ≤ 10; the bot's `migrations=4` and PASS, four `Handoff ACK`s; grade_common and grade_layout (Static, nothing
    applied). The record, demand, reverse-report and notice paths are covered by EditMode tests and by what the other
    on-demand runs log (a note counts every `opened on demand (record|demand` line); the rig has no deterministic far
    handoff (its portals warp onto each other). A manual control run with `MESH_LINKS` unset (all-zones) is recorded
    in the log below, not graded.
14. **`partyfar`** (new): `party`, plus `meshbot2` (a registered rig bot, `p0load register-bots`) put in party 1 and
    standing at (-250, 0) on W, beyond ghost range of C (150 m past its edge) though a band neighbour of C on the 3x3
    grid: so the run proves the relay is used, not that it is needed. Every other `db_reset` takes meshbot2..9 out of any
    party. Graded as `party`, plus a kill split counting a remote member (`+ [1-9]\d* remote`) and `Party share applied
    to meshbot2Char` on W; with `MESH_LINKS` on demand the remote share went through the central (`relayed through the
    central`) and no cell logged `broadcast to`; without it, by broadcast.
15. **`split` with `MESH_LINKS`**: graded too that E closed its link to C on demand after the cut (E's band no longer
    reaches C's 100 m half) and C logged `Neighbour zone E unlinked (idle close)`.
16. **`grade_common`, every scenario:** with `MESH_LINKS` in the Play's environment, one check (no `ConnectionRejected`
    in any cell's log) and one note (links opened and closed on demand over the cells, the largest peaks); without it,
    the byte-identical check: no cell logs any of `Mesh links`, `on demand`, `relayed through the central`, `unlinked
    (idle close)`, `over our link to it`, `waits for the link to it`.

### Documents
17. DESIGN 0.86 row (0.85 is the unfreeze session's), §15's ceiling bullet rewritten (the per-map link ceiling applies
    to maps without `onDemandLinks`; the per-host port range of 64 stays a per-host ceiling), the status line; SETUP §5's
    `linkAllZones` row and a new `onDemandLinks` row, :309 ("room for 64 cells"), §6a.3's warning row, :394-395,
    §6a.11, §6a.12 "Scale" rewritten, §7 (the new scenarios, toggles and count); MULTIHOST §4's "64 is also where the
    links stop" (on-demand links lift the per-map cap, not the per-host port cap); `Addons/ServerMeshing/README.md:56`
    ("64 links a cell takes"); `ServerMeshPackageBuilder` 0.86.0 and date.

### Acceptance
18. EditMode (failure cases first), `MeshLinkNeedsTests`: band neighbours never close; a record keeps its zone and the
    link closes `idle` after the record goes; traffic postpones the close; heard zones are wanted as recent; a silent
    link the book never saw closes `idle` after it is seen; a closing link is not closed twice and a want after the stop
    reopens; reasons in priority band > record > demand > recent; empty zones and None ignored; `Settle` with no links
    wants every wanted zone; forgotten once idle; `IsSettling` within the grace, not after, not while connected, not for
    a band neighbour wanted since start, restarted by a new want; the idle floor; the inbound bound on a 10x10 grid
    (each cell wanted by at most its 8 neighbours where all-zones takes 99); `MESH_LINKS` parsing and `ApplyLinkModes`;
    `CopyModeFrom`; the cap and idle parsing; the relay's targets (same map, sender and other maps left out) and its
    message round trip. `MeshDynamicSetupChecksTests`: an on-demand map not judged, a mixed world judging only the
    all-zones map, the new advice text. Every 2a-6 test unchanged. Python: the `riglinks` graders (touching sets, the
    links line parser, the byte-identity check) and `partyfar`'s with fakes; `ENV_KEYS` holds the three keys; the suite
    and ruff.
19. Rig, after one player build (about 3 h of rig time): `linkcap` 1/1; `partyfar` 1/1 default and 1/1 with
    `MESH_LINKS=all:OnDemand`; the eleven base scenarios Static 11/11 and `all:Dynamic` 11/11 with no toggle (both
    byte-identical by 16), and `all:Dynamic` with `MESH_LINKS=all:OnDemand` 11/11; `split`, `merge`,
    `splitcrash_drain`, `splitbuild` and `splitcentral` 1/1 each with `MESH_LINKS=all:OnDemand`; one rerun on a fail,
    recorded. Package 0.86.0 (`regress.py --package`). The rig is shared with the unfreeze session: neither builds or
    plays while the other's chain runs.

## Interfaces and log lines (exact)
- `MeshMapLayout.onDemandLinks`; `MeshLayoutOverride.ApplyLinkModes(List<MeshMapLayout>, Dictionary<string,bool>,
  List<string> unknown)`; `MeshLinkEnv.ENV_LINKS/ENV_CAP/ENV_IDLE`, `ParseLinkModes`, `Capacity`, `IdleSeconds`,
  `MinIdleSeconds`, `DefaultCapacity` 64.
- `MeshLinkNeedReason { None, Band, Record, Demand, Recent }`; `MeshLinkBook`: `IdleSeconds`, `GraceSeconds`,
  `BeginTick()`, `Want(zone, reason, now)`, `NoteHeard(zone, now)`, `NoteSent(zone, at)`, `Settle(now, linked, wanted,
  close)`, `ReasonOf`, `WasWanted`, `NoteOpened`, `MarkClosing`, `IsClosing`, `ClosingFor`, `Stopped`, `Forget`,
  `Clear`, `IsSettling(zone, now, connected)`, `Opened`, `Closed`; `MeshPartyRelay.Targets(IEnumerable<MeshMapServerPeer>,
  mapId, fromConnectionId)`; `MeshPartyShareRelayMessage { mapId, fromCellId, share }`.
- `MeshLinkMessageTypes.LinkClose = 133`, `IsCountedTraffic(type)`; `MeshClusterMsg.PartyShareRelay = 30022`;
  `MeshLinkClient.BeginClose()`, `Refused`, `ConnectedAt`, `LastSentAt`.
- Manager: `LinksOnDemand`, `LinkServerCapacity`, `DemandLink`, `NoteLinkHeard`, `OnLinkClose`,
  `OnReverseRegistrationState`, and private `EnsureLinksOnDemand`, `LinkStillOpening`, `OnIdleClosedDisconnect`,
  `NoteLinkPeerHello`, `TryRelayPartyShare`, `DeferWhileUnlinked`, `TryReverseReport`, `SendReverseRegistrationStates`,
  `RestartCutoff`, `LogLinkSummary`.

## Files
Mesh/: new `MeshLinkNeeds.cs` (pure book, toggles, relay targets and message), `MeshMapNetworkManager_Links.cs` (upkeep,
close, stamps, guards, capacity, lines, the cell side of the relay), `MeshMapNetworkManager_LinkReports.cs` (reverse
reports, home notices, the restart cutoff), `MeshCentralNetworkManager_Relay.cs`; `CellMeshLink.cs`, `MeshSleep.cs`,
`MeshWorldRegistry.cs`, `MeshLayoutOverride.cs`, `MeshDynamicSetupChecks.cs`, `Editor/ServerMeshSetupWindow_Dynamic.cs`,
`MeshCentralNetworkManager.cs` (one registration line), `_Fence.cs` (two hooks), `_SplitEntities.cs`, `_StaticHandoff.cs`
(home-notice hooks), one-line hooks in `MeshMapNetworkManager.cs` and `_Ghosting.cs`; `ServerMeshPackageBuilder.cs`.
Tests: new `MeshLinkNeedsTests.cs`; `MeshDynamicSetupChecksTests.cs`. Tools: new `regress_links.py`, `riglinks.py`,
`test_riglinks.py`; `regress.py` (ENV_KEYS, registration, grade_common's link checks, meshbot2..9 out of parties),
`test_regress.py`. Docs: as decision 17, this contract.

## Critique log
- **Rev 0 → 1 (2026-10-20), two independent reviews against the code.** *A (need rule and safety):* a far handoff on a
  Dynamic map deadlocked: the target's readiness report travels only over its own links, and nothing made it link back
  (blocker) → 6a, reports back over the inbound connections and the hello as `recent`; one-shot home notices were dropped
  while the home link was still opening, breaking the mirror → 7a; a restart seen late released records already
  re-pointed to the new process → 7b; the disconnect path released while an on-demand link was still coming up, and a
  full target looked dead → 7's guard on both paths, `Refused`; sends in the second between the notice and the stop →
  a closing link reads as down; the safety argument assumed an idle time longer than the ack guard → the 25 s floor;
  relay to instance servers of the same map → the coordinator transport's peers. *B (feasibility, rig, byte identity):*
  `MESH_LINKS` alone was ignored by `Resolve` (fixed in the code with the toggle); linkcap exercised no non-band path →
  said so plainly, the paths covered by EditMode tests and notes, `split` graded for the close; linkcap needed an expect
  set, correct walk geometry and counts (z 75, four migrations, the 240 s client budget) and no monsters; partyfar's
  member is a band neighbour of C → reworded, and the on-demand run graded for no broadcast; meshbot2 is not a fixture
  → registered bots, every other reset clears their party; the byte-identity claim had no check → 16; no plain Dynamic
  run → added; the relay's map id taken from the sender's registration; `LinkClose` excluded from the stamps; Static
  maps may turn it on; the README and the SETUP lines named. Confirmed by both: band links never close, no periodic
  counted message reaches a non-band zone, acks are mirrored, the sweep's `wasLinked` survives a closed client,
  retire/prune are independent, reconnects and frame offsets are handled at hello, cluster identity (`refId` = map,
  `channelId` = cell, children too), ids 133 and 30022 free, every remote share goes through `BroadcastPartyShare`.
- **Implementation deviations (2026-10-21), kept:** `MeshLinkBook.Plan` is `Settle` (the stray-reference check reserves
  `Plan`, a TestMap type); `Recent` also comes from a hello (6a); the rig toggles run on the shared tree with the
  unfreeze session's contract (`slices/unfreeze_design.md`, DESIGN 0.85) and its rig lock (`Temp/ServerMesh.RigLock.txt`).
- **Acceptance (2026-10-21, BRENDOAIBOX):** EditMode 3051 (3050 passed, 1 skipped as before), 851 harness tests, ruff
  clean, no compiler warning; `linkcap` 122/122 (every cell linked exactly its touching neighbours, peak in at most 8
  under a cap of 10); `partyfar` 56/56 default (9 broadcast) and 56/56 on demand (15 relayed, 0 broadcast); the eleven
  base scenarios Static 11/11 and `all:Dynamic` 11/11 without the toggle (byte-identical by decision 16) and `all:Dynamic`
  with `MESH_LINKS=all:OnDemand` 11/11 (`wake`: E opened its link to the just-woken C2 on demand); `split` 120/120 (E
  closed its link to C after the cut; C logged the idle close), `merge` 144/144, `splitcrash_drain` 104/104, `splitbuild`
  142/142 on demand. **`splitcentral` failed** 157/161 three times on demand and once without `MESH_LINKS` (the same
  four checks): the late bot's enter-game on C.1 timed out while C.1's database reconnect waited out its 35 s drain (its
  first retry after the central process restart failed with 2-4 requests in flight), and the kit's response-timeout
  path threw, so the bot never retried; the unfreeze session's default run passed at 23:07 when the first retry found
  the database up. Not the links: a follow-up for the database reconnect or the bot. Manual control: `linkcap` with
  all-zones links under the same cap refused hundreds of connections and failed a crossing (102/122). Logs:
  `Logs/regress_slice7_*.txt`. DESIGN 0.86, package 0.86.0. Accepted except `splitcentral`.
