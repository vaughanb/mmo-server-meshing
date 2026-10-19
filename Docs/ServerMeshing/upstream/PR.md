Four small changes found while building a server-meshing add-on on top of the kit (one map split across several map-server processes with seamless handoff). Two stand on their own as fixes; two are extension points with no behaviour change. One commit each.

**1. Fix movement state leaking across an entity's owner change** (`BuiltInEntityMovementFunctions3D`, `RigidBodyEntityMovement2D`, `NavMeshEntityMovement`)

When a live entity gets a new owner client, the server-side movers kept the previous owner's `_acceptedPositionTimestamp` and `_isServerWaitingTeleportConfirm`. The entity was snapped back to the old client's last accepted position and the new client's first movement syncs were rejected as an invalid timestamp until they passed the old ones. Both are reset in `OnSetOwnerClient`; the NavMesh mover, which had no override, gets the same one.

**2. Wait for request responses per frame instead of polling every 100 ms** (`LiteNetLibClient`, `LiteNetLibServer`)

`SendRequestAsync` waited with `do { await UniTask.Delay(100); } while (!done);`, a hard 100 ms floor under every awaited request (database calls, cluster requests, everything built on them) on top of the round trip. `UniTask.WaitUntil(() => done)` completes on the frame the response arrives. On a map server's enter-game path each awaited step went from about 220 ms to the round trip alone.

**3. `MonsterActivityComponent`: subscriber gate as a virtual `IsWatched()`**

The inline `CountSubscribers() == 0` check becomes `protected virtual bool IsWatched()` with the same default, so a subclass can keep a monster active for observers the server does not count as subscribers.

**4. `MapNetworkManager`: `_pendingSpawnPlayerCharactersByUserId` protected**

`SetPlayerReadyRoutine` already handles an entry that is an already-spawned entity. With the dictionary reachable, a subclass can substitute a standing entity for an arriving character and have that path taken.

All four have been running under a regression harness (eight scenarios, five map servers over two maps) for the add-on; the first two are useful regardless of it.

---

**Later, for its own pull request (0008, 2026-10-19): `MapNetworkManager`: let subclasses choose the building persistence channel**

Buildings are loaded, created, saved and deleted per channel and map. A map server that serves one channel's map from several processes wants every process to read and write the same rows under the channel they were placed on, so a building keeps its row when its ground changes hands. `protected virtual string BuildingPersistenceChannelId => ChannelId;`, read at the seven building database sites. No behaviour change for a `MapNetworkManager` that does not override it.
