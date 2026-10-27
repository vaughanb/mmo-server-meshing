# Core changes to upstream (open-mmorpg/OpenMMORPG)

The server-meshing add-on needed a few small changes in the kit itself (DESIGN.md §17: CE-6 to CE-9, CE-11 to CE-13).
They are kept here as a patch series against the upstream `develop` branch, one commit per change, with upstream-neutral
wording (no add-on markers). The local copy of the kit in `Assets/OpenMMORPG/` carried the same changes with
`MESH-EDIT` markers until DESIGN 0.94 (2026-10-27); since then it is OpenMMORPG v1.1.4 (`master` at 317d2b8) unchanged,
which has all nine upstream.

| Patch | Kind | Files |
|---|---|---|
| 0001 Fix movement state leaking across an entity's owner change (CE-9) | **bug fix** | `BuiltInEntityMovementFunctions3D.cs`, `RigidBodyEntityMovement2D.cs`, `NavMeshEntityMovement.cs` |
| 0002 Wait for request responses per frame instead of polling every 100 ms (CE-6) | **performance** | `ThirdParty/LiteNetLibManager/Scripts/LiteNetLibClient.cs`, `LiteNetLibServer.cs` |
| 0003 MonsterActivityComponent: make the subscriber gate a virtual IsWatched() (CE-7) | extension seam | `MonsterActivityComponent.cs` |
| 0004 MapNetworkManager: let subclasses reach the pending spawn data (CE-8) | extension seam | `MapNetworkManager.cs` |
| 0005 CharacterDataCache: drop the finalizer that released pooled collections off the main thread (CE-11) | **bug fix** | `CharacterDataCache.cs` |
| 0006 Send one-shot movement flags to every receiver of a tick (CE-12) | **bug fix** | `IEntityMovementDataHandler.cs`, `BaseGameNetworkManager.cs`, `BuiltInEntityMovementFunctions3D.cs`, `NavMeshEntityMovement.cs` |
| 0007 Read entity states synchronously and resume at each state's length (CE-13) | **bug fix** | `BaseGameNetworkManager.cs`, the five movement readers (`BuiltInEntityMovementFunctions3D.cs`, `NavMeshEntityMovement.cs`, `RigidBodyEntityMovement2D.cs`, `BuiltInSimpleEntityMovementFunctions3D.cs`, `SimpleNavMeshEntityMovement.cs`) |
| 0008 MapNetworkManager: let subclasses choose the building persistence channel (CE-10) | extension seam | `MMO/Scripts/MMOGame/Networking/Map/MapNetworkManager.cs`, `MapNetworkManager_DatabaseFunction.cs` |
| 0009 ClusterServer: forget the online users of a map server that disconnects (DESIGN 0.91) | **bug fix** | `Server/Cluster/ClusterServer.cs` |

0001-0004: 7 files, 30 insertions, 4 deletions, verified against upstream `develop` at `aa8ecf1` (2026-09-15): every
anchor line is present and unchanged, and the diff contains nothing but these lines (byte-order marks and line
endings preserved).

0006 and 0007 are the space team's kit patches #9 and #10 (their `VENDOR_PATCHES.md`), reviewed here on 2026-09-30
and carried by both downstream kit copies. They are the same code as the space team's diffs, checked file by file
with comments ignored. Only the comments were reworded upstream-neutral, and NavMesh's class line was applied by hand
because upstream's class is not `partial`. 0007 applies on top of 0006. The add-on's state sender (Phase 0 step 6)
calls `IEntityMovementServerStateFlags.ConsumeServerStateFlags()` from 0006, so it needs a kit that carries 0006.

0008 (2026-10-19, Slice 4 of the dynamic cells) is one `protected virtual string BuildingPersistenceChannelId => ChannelId;`
read at the kit's seven building database sites (the load, the in-scene create, the quit save, the two saves, the
create, the delete); no behaviour change unless overridden. Generated from the kit copy with the `MESH-EDIT: CE-10`
markers stripped; the doc comment stays. Applies cleanly to `develop` at 3897b326 (2026-10-20).

0009 (2026-10-24) is the kit-side fix for the dead-server user mirror the 0.91 soak found: a map server that dies
never sends the `UpdateMapUser` Remove for its players, so every other map server's `_socialCharactersByUserId` keeps
them "online" and `SetPlayerReadyRoutine` kicks their re-login as a nested login. `ClusterServer` now records which
connection hosts each user (`Online` sets the host; `Add` only for an unknown user, because a re-registering map server
re-announces its whole mirror; `Remove` only from the host) and, in its `DisconnectEvent` branch, relays `Remove` for
that connection's users to the remaining map servers. Entries go in that same event, so a reused LiteNetLib connection
id never inherits them. 64 insertions, no deletions. Written directly against `develop` at d077c5f, not generated from the
kit copy: `Assets/OpenMMORPG/` does not carry it, since the add-on's own workaround
(`Mesh/MeshCentralNetworkManager_MapUsers.cs`, a replaced handler and a once-a-second sweep) already covers the gap.
Compiled against the kit's built `Assembly-CSharp.dll`; not run on the rig. Once merged and pulled into the kit copy,
the add-on's workaround is redundant and can go in its own step. Until then they do not double up: the workaround
replaces the kit's `UpdateMapUser` handler, so the kit's table stays empty and its disconnect relay sends nothing.

## Status

**0001-0004: merged.** Fast-forwarded onto upstream `master` and `develop` on 2026-09-15 (aa8ecf1 → 98a2d18); [PR #1](https://github.com/open-mmorpg/OpenMMORPG/pull/1) records it. The patches stay here as the record of what changed in the kit and why.

**0005: merged.** [PR #2](https://github.com/open-mmorpg/OpenMMORPG/pull/2) merged it into `master` on 2026-10-01
(UTC; e37cfe4), and `develop` was fast-forwarded to the same commit the same evening (5d308aa → e37cfe4).

**0006-0007: merged.** Written against `develop` at e37cfe4 (2026-09-30) on the upstream branch `movement-state-fixes`;
[PR #3](https://github.com/open-mmorpg/OpenMMORPG/pull/3) merged them into `develop` on 2026-10-02 (bb9895f), after the
add-on's kit copy had compiled them (CE-12/CE-13), passed 1094 EditMode tests and passed accept-a on the rig.
From Phase 0 step 6 the add-on requires 0006 (Mesh/Sync references its interface).

**0008: merged.** Pushed as the upstream branch `building-persistence-channel` off `develop` at 3897b326 on 2026-10-20
(`git am` applied it unchanged); [PR #8](https://github.com/open-mmorpg/OpenMMORPG/pull/8) merged it into `develop` the
same day (d077c5f), after both CI checks passed. Every kit change the add-on relies on is now upstream.

**0009: merged.** Committed as 4f23e47 on the upstream branch `forget-dead-map-server-users` off `develop` at d077c5f
on 2026-10-24; [PR #9](https://github.com/open-mmorpg/OpenMMORPG/pull/9) merged it into `develop` the same day
(2026-10-24 UTC; f43d0c0) after both CI checks passed. The add-on does not rely on it. The kit copy has it since
DESIGN 0.94 (v1.1.4); the add-on's workaround is still in place (the two do not double up, above) and can go in its own
step.

## Pushing (how it was done; for a re-run)

Upstream's CONTRIBUTING: open an issue, branch off `develop`, pull-request into `develop`.

```bash
git clone --branch develop https://github.com/open-mmorpg/OpenMMORPG
cd OpenMMORPG
git checkout -b mesh-core-fixes
git am "<this folder>/000*.patch"
git push -u origin mesh-core-fixes
gh pr create --base develop --title "Server-meshing groundwork: owner-change movement fix, request polling, two extension seams" --body-file "<this folder>/PR.md"
```

`git am` keeps the four commits and their messages. If `develop` has moved and a hunk no longer applies, the
intended edit is small enough to redo by hand from the patch text.

## Issue text (CONTRIBUTING asks for one first)

**Title:** Movement state leaks across an entity's owner change; request/response polling floor; two extension seams for multi-server maps

**Body:**

Four small changes found while building a server-meshing add-on on top of the kit (splitting one map across several
map-server processes with seamless handoff). Two are fixes that stand on their own, two are extension points with no
behaviour change:

1. When a live entity changes owner client, the 3D built-in mover and the 2D rigidbody mover keep the previous owner's
   `_acceptedPositionTimestamp` and `_isServerWaitingTeleportConfirm`; the entity snaps back to the old client's last
   accepted position and the new client's first syncs are rejected as an invalid timestamp. The NavMesh mover has no
   `OnSetOwnerClient` at all. Fix: reset both on owner change, add the override to the NavMesh mover.
2. `SendRequestAsync` on `LiteNetLibClient`/`LiteNetLibServer` waits with `do { await UniTask.Delay(100); } while (!done);`,
   a hard 100 ms floor under every awaited request (database, cluster). `UniTask.WaitUntil` completes on the frame the
   response arrives.
3. `MonsterActivityComponent.ManagedUpdate` skips activity while the identity has no subscribers, inline. As a
   `protected virtual bool IsWatched()` a subclass can keep a monster active for observers that are not this server's
   subscribers. Default unchanged.
4. `MapNetworkManager._pendingSpawnPlayerCharactersByUserId` from private to protected, so a subclass can substitute an
   already-spawned entity and have the existing "already spawned entity" branch of `SetPlayerReadyRoutine` taken.

PR to follow.
