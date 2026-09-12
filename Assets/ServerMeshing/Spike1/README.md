# Server Meshing — Spike 1 (ghosting & simulation-suppression)

Proves a kit entity can be spawned as a passive **ghost** without running its server logic (AI,
movement, regen, buff ticking) — the gating risk for P2/P3. **Extension-only.**

- `GhostSuppression.cs` — the mechanism: add a movement disabler + `UpdateManager.Unregister(...)` the
  server-driver `IManagedUpdate` components, keeping identity/model/collider. Seeds the production
  `MeshGhostController`.
- `Spike1_GhostSuppressionProbe.cs` — host-play-mode harness: ghostify the nearest monster, check it
  stops self-moving (idle drift), and drive it externally to confirm it still renders.

**Run as Host**, then use the probe's context menu (⋮): *Ghostify nearest monster → Idle drift check →
Toggle external drive*.

**Runbook, verified mechanism, pass/fail:** [`Docs/ServerMeshing/SPIKE1.md`](../../../Docs/ServerMeshing/SPIKE1.md).

> 1a (suppression) is single-server and runnable now. 1b (S2S ghost over CellMeshLink) is a follow-up
> needing the two-server rig (shared with Spike 2).
