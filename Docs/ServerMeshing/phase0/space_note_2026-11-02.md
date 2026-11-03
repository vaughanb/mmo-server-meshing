# Note from the space team, 2026-11-02: mode 3 as shipped, its golden fixture, and the seated-character patch

**Status: committed on the branch `space/mode3-golden-and-seated-patch` (off `origin/master` at bb99b71), not merged and
not pushed.** Written by the space side (Large Systems P2, space repo at `d27709d05`) on Brenden's word. What the branch
changes is listed below under each part; what it leaves is at the end.

> **To the server-meshing add-on, from the large-systems plan (2026-11-02).**
>
> Two things from our Phase 2c, both agreed on 2026-10-05 (proposals 4 and 6), now built and measured on our side.
>
> ### 1. Mode 3 shipped as 31 bytes, and its golden fixture
>
> Your `compact28` is, in your words, "our reading of their proposed mode 3 … rig-only until their golden pins it". The
> golden is pinned now, and mode 3 came out different from the reading in four ways. The fixture is
> `Docs/ServerMeshing/fixtures/ship_wire_mode3_golden.json` on this branch: version 1, seven cases, with the byte layout
> and the quantization rules written out in it. Its source of truth is the space repo's
> `Docs/Fixtures/ship_wire_mode3_golden.json`, and our `ShipWireTests` fail if our encoder stops writing it. A change to
> either copy is a wire change, made in both repos.
>
> | | Your compact28 | Mode 3 as shipped | Why |
> |---|---|---|---|
> | Size | 28 B | **31 B** | the two fields below |
> | Stamp | none | **2 B**: the writer's frame clock in 100 µs ticks, mod 65,536 | the kit's stamp steps; our observer smoothing needs the time a state describes, and mode 0 carries it as a double |
> | Frame tag | none | **1 B**: FNV-1a of S, folded | the position is relative to S, so a state from a ship in another bubble would be drawn in this one at its offset there; a reader drops a tag that isn't its own frame's |
> | Angular scale | 4 rad/s / 127 | **8 rad/s / 127** | Unity caps a body at 7 rad/s; hulls turn up to that |
> | Rounding | doubles; rotation half to even, angular half away from zero | **float32; half rounds up** | an exact 0 component lands on step 511.5, so every identity or one-axis rotation is a tie: the rule has to be pinned, and the fixture does |
>
> The header, the position, the smallest-three layout, the halves and the state byte are as you read them.
>
> **What it changes for you.** Bandwidth rows taken with `MESH_SHIP_WIRE=compact28` count each compact record 3 B short
> of ours (about 10 %). The pooled S9 rows should be re-taken, or corrected, with the 31 B record.
>
> **On this branch:**
> - `ServerMeshShipWire.cs`: the codec as shipped. `CompactBytes = 31`, `WriteCompact`/`ReadCompact` (were
>   `WriteCompact28`/`ReadCompact28`), `CompactAngularFullScale = 8`, `StampOf`, `FrameTagOf`, `RigFrameTag` (S = 0), and
>   the rotation and angular packing in our float32 arithmetic. Run against the fixture in the space editor first: all
>   seven cases byte for byte, each read back.
> - `ServerMeshShipSample.cs`: `SampleTime` (seconds), and `Stamp` and `FrameTag` (as read). The ship and the puppet
>   body write `SampleTime = sampleTimestamp / 1000` (the kit's milliseconds) and `FrameTag = RigFrameTag`.
> - `ServerMeshShipWireFixtureTests.cs` (NUnit, EditMode) and `Docs/ServerMeshing/fixtures/ship_wire_mode3_golden.json`:
>   every fixture case written and read back.
> - The rig's tests follow: `WriteCompact_GoldenSample_IsByteExact` runs again (it was ignored "until the space team
>   decides") with the 31 B golden of `MeshSyncGoldenFixture.Sample(true)`; sizes, the clamp at 8 rad/s and the client
>   record size are 31 where they were 28.
> - The tools: `p0plans0b.py`'s `WIRE_B["compact28"]` is 31, and `phase0/accept_step7.py` checks 31 B compact records
>   (`COMPACT_B`); their tests and `SETUP.md` follow.
> - **Kept:** the kind `ServerMeshShipWireKind.Compact28` and the word `compact28` in `MESH_SHIP_WIRE`, the bot specs and
>   the tools. They are your user-facing grammar; the name is now historical, as their comments say. The plan id
>   `ship-sender-28-tiered` is kept too, since past results are keyed by it.
>
> ### 2. The seated-character kit patch (proposal 6)
>
> Our kit patch #29 is built and measured: the server writes no movement state for a character seated in a vehicle
> unless a jump, dash or teleport is pending. Every client already puts a seated character on its seat each frame, and
> the spawn message carries its position. A client copy holds its interpolation at the seat, or a passenger seen before
> boarding is drawn back at that spot between leaving the seat and the exit teleport's state. Measured on our
> `sc-wire`: two seated characters cost 1.0-1.2 kB/s per client when they are sent (about 27 B a record with the kit's
> framing), and 64 seated crew would add about 35 kB/s against the 100 kB/s budget.
>
> **It applies to your kit copy too, and you need it.** `MeshKitMovementStateSource` writes through `WriteServerState`
> and `CanWriteQuantizedServerState`, the two calls the patch guards. Without it, the add-on's sender sends every seated
> character again.
>
> **Names.** The contract reserved "CE-14" and "upstream 0008" for it on 2026-10-05; both have gone to other changes since
> (CE-14 is the client migration, 0008 is CE-10). So it would be your next free CE number and **0011**.
>
> **On this branch:**
> - The kit copy carries it, in a commit of its own, worded upstream-neutral (no markers). Upstream's
>   `Docs/ServerMeshing/upstream/0011-*.patch` is generated from that commit, with `PR-0011.md` beside it and a row in
>   the README. It is not checked against upstream `develop`: that wants `git apply --check` in a throwaway clone, as
>   for 0010.
>
> **One difference from our copy.** Here and upstream, the switch `BuiltInEntityMovementFunctions3D.SkipSeatedServerStates`
> is **off** by default, so nothing changes for a game that doesn't ask; only the interpolation hold for a seated copy
> is unconditional. Our kit copy has it on. **The add-on does not turn it on on this branch:** with it on, the sender
> skips seated characters, and the seated-pilot load runs (`MESH_LOAD_SHIP_PILOTS=1`) would measure less. That is your
> call. Once upstream has it, we set it on at startup and drop our patch.
>
> ### Not done on this branch
>
> - DESIGN.md and the package version are not bumped.
> - The pooled S9 rows taken with the 28 B record are not re-taken.
> - 0011 is not checked against upstream `develop`, and no upstream branch or PR exists for it.
