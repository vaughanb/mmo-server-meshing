# Server Meshing — Spike 2 (client connection migration)

Throwaway spike testing whether a connected client can be moved from one cell-server to an adjacent
one **without a visible loading screen** — the make-or-break assumption of the meshing design.

**This is extension-only — it edits no core MmoKitCE files.**

- `MeshMapNetworkManager.cs` — `MapNetworkManager` subclass; swap it onto the map-network-manager
  prefab. Intercepts the client warp and routes it through a *masked* migration.
- `ClientMigrationController.cs` — holds a screenshot over a normal `StopClient()/StartClient()`
  while server B re-streams, fades it out when the local player re-spawns, and logs timings.

**Full runbook, setup, and pass/fail criteria:** [`Docs/ServerMeshing/SPIKE2.md`](../../../Docs/ServerMeshing/SPIKE2.md)
(design context: [`Docs/ServerMeshing/DESIGN.md`](../../../Docs/ServerMeshing/DESIGN.md)).

> Scaffold targets confirmed kit APIs but has not been compiled in-project; expect minor signature
> tweaks in the editor. Search `SPIKE-TODO` for the spots most likely to need attention.
