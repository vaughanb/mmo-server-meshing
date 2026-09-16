# Core changes to upstream (open-mmorpg/OpenMMORPG)

The server-meshing add-on needed four small changes in the kit itself (DESIGN.md §17, CE-6 to CE-9). They are
kept here as a patch series against the upstream `develop` branch, one commit per change, with upstream-neutral
wording (no add-on markers). The local copy of the kit in `Assets/OpenMMORPG/` carries the same changes with
`MESH-EDIT` markers.

| Patch | Kind | Files |
|---|---|---|
| 0001 Fix movement state leaking across an entity's owner change (CE-9) | **bug fix** | `BuiltInEntityMovementFunctions3D.cs`, `RigidBodyEntityMovement2D.cs`, `NavMeshEntityMovement.cs` |
| 0002 Wait for request responses per frame instead of polling every 100 ms (CE-6) | **performance** | `ThirdParty/LiteNetLibManager/Scripts/LiteNetLibClient.cs`, `LiteNetLibServer.cs` |
| 0003 MonsterActivityComponent: make the subscriber gate a virtual IsWatched() (CE-7) | extension seam | `MonsterActivityComponent.cs` |
| 0004 MapNetworkManager: let subclasses reach the pending spawn data (CE-8) | extension seam | `MapNetworkManager.cs` |

Total: 7 files, 30 insertions, 4 deletions. Verified against upstream `develop` at `aa8ecf1` (2026-09-15): every
anchor line is present and unchanged, and the diff contains nothing but these lines (byte-order marks and line
endings preserved).

## Status

**Merged.** Fast-forwarded onto upstream `master` and `develop` on 2026-09-15 (aa8ecf1 → 98a2d18); [PR #1](https://github.com/open-mmorpg/OpenMMORPG/pull/1) records it. The patches stay here as the record of what changed in the kit and why.

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
