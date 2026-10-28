# Open MMORPG: A free, community-maintained Unity MMO framework

![image](Resources/OpenMMORPG.png)

**Open MMORPG** is an opinonated community-maintained distribution of MMORPG Kit. After the original asset was removed from the Unity Asset Store, Ittipon Teerapruettikulchai (insthync) open sourced his work. **Open MMORPG** exists to preserve, improve, and evolve this foundation, and will continue to pull improvements and fixes from his core repos into this distribution where it makes sense.

### The Three S's Guiding Principle

Every change, fix, or removal in Open MMORPG is evaluated against these core goals:

- **Scalability**: Can the system handle hundreds or thousands of concurrent players?
- **Stability**: Does it reduce bugs, crashes, edge cases, and unexpected behavior?
- **Security**: Does it harden the codebase against exploits, cheating, and data leaks?

**No other feature requests or enhancements** are considered unless they demonstrably advance one or more of these three goals. In fact, non-essential or problematic features may be **removed** or **moved to addons** if doing so improves any of the three S's.

## What's Included

### Addon Manager
Addon Manager is an in-editor interface that allows the community and team to modularize functionality.

- Former "core" features that were too niche, experimental, or optional can be extracted into addons.
- Addons are discovered, installed, and updated directly inside Unity, similar to a private Unity Package Manager.
- This keeps the **core distribution lean**, focused, and easier to maintain long-term.

### Login Manager
Login Manager is a clean separation of login/authentication logic from the central game servers.

- Improved scalability: Concurrent login limit prevents the login server from being overwhelmed during spikes. The dedicated login server + cluster client allows independent scaling of auth traffic away from game logic.

### Sharded DatabaseNetworkManager
Added lanes, queueing, deferred/throttled saves, and a working in-memory cache.

- Improved scalability: Vastly improved horizontal/concurrency scaling with sharded lanes + locks + ConcurrentDictionary support higher player counts and multi-threaded server ops without contention or overload. Limits (e.g., max saves/proceed) provide predictable load.

### Cell-Based Position Quantization
Cell-based position quantization dramatically improves network efficiency for entity movement.

- Improved scalability: Lower network traffic supports more concurrent players, higher update rates, and denser entity populations.
- LOD based compression: Close entities (the ones the player actually interacts with) keep high-precision modes, while distant entities (the majority in large MMO worlds) send position data in as little as 7 bytes, against 12 for raw floats.

**World Size:** The world is cut into cubic cells (128 m by default) that start at the world origin and have no edge. Each position carries its cell as three signed packed integers, one byte each within about 15 km of the origin, so maps of any size, negative coordinates and any height work.

**Compatibility:** Quantization is on by default (`isDisabled` in `DefaultGridManagerComponent`), and every server and client must run the same build. The 2D and simple movement types keep sending their own formats, because their clients can't read quantized states.

### Jobs Movement Pipeline
All entity movement data processing converted from monothreaded per-entity updates to Unity Jobs + Burst parallel processing.

- Improved scalability: Combined with vector quantization and packed serialization, network payloads shrink dramatically, improving both server tick rate and bandwidth usage.

## Quick Start

Open MMORPG targets **Unity 6000.3** or newer and runs on the **Universal Render Pipeline (URP)** or the **High Definition Render Pipeline (HDRP)**. The kit's code is the same for both; the demo's materials, shaders, scenes, lights and cameras are not, so the kit carries both versions and installs the one that matches your project (see **Choose your render pipeline** below). Importing the kit adds URP and the other Unity packages it needs so that it compiles before anything is chosen.

1. **Import the kit**

Get Open MMORPG from the Unity Asset Store and import it from Window → **Package Manager** → My Assets, or download `OpenMMORPG.unitypackage` from the [latest release](https://github.com/open-mmorpg/OpenMMORPG/releases/latest) and import it via Assets → Import Package → **Custom Package**. Either way the kit lands in `Assets/OpenMMORPG`.

2. **Import the project settings**

Run Tools → Open MMORPG → Install → **Import Project Settings**. It lists exactly which files it replaces before doing anything:

 - ProjectSettings/DynamicsManager.asset
 - ProjectSettings/InputManager.asset
 - ProjectSettings/ProjectSettings.asset
 - ProjectSettings/QualitySettings.asset
 - ProjectSettings/TagManager.asset
 - ProjectSettings/TimeManager.asset

3. **Choose your render pipeline**

The kit's demo content ships as URP. If the project renders with URP there is nothing to do. If it renders with HDRP, a **Render Pipeline Content** window opens by itself after the import; press **Install HDRP content**. It replaces the demo's pipeline-specific files (shaders, materials, scenes, prefabs with lights or cameras, and a few scripts) with their HDRP versions and removes the URP-only ones, after copying any of those files you had edited to `OpenMMORPG_PipelineBackups` beside `Assets`. Afterwards it offers to remove the URP packages the import added, which HDRP does not need. Tools → Open MMORPG → Install → **Render Pipeline Content** brings the window back, to check what is installed or to switch pipelines later. If your project does not use the demo, press **Don't ask again** and the window stops opening by itself in that project; **Not now**, or closing it, only puts it off until Unity next starts.

After installation, browse available addons via the Addon Manager window (Tools → Open MMORPG → Develop → **Addon Manager**). Have fun building!

## Running the demo

The kit ships with a demo in `Assets/OpenMMORPG/Demo`: a small island with a village, three enemy families, a rideable horse and a dungeon, running on the kit's MMO servers. A **Welcome** window walks through these steps the first time the project opens, and Tools → Open MMORPG → Demo → **Welcome** brings it back.

1. **Import the project settings**, as in Quick Start.
2. **Use the demo's render settings.**
   - **URP.** The demo is lit for its own URP asset, `Demo/Settings/DemoURP`, and the sea needs the depth and opaque textures it turns on. Press **Use Demo Render Pipeline** in the Welcome window: it sets that as the default and gives the lower half of your Quality levels the lighter `DemoURP_Low` (no ambient occlusion, two shadow cascades, hard shadows), so the Quality setting does something on a slower machine. Or set `DemoURP` yourself under Project Settings → Graphics → Default Render Pipeline.
   - **HDRP.** Install the HDRP content first (Quick Start, step 3). The demo needs nothing special of your HDRP asset, which is left alone: the sky, fog, exposure and sun shadows are Volumes in the scenes, and the lights are in physical units. The Welcome window offers to turn on dynamic resolution, which the Resolution Scaling graphics setting uses.
3. **Build the map server.** In the MMO flow each map is hosted by a separate process that the map spawner launches, and that process is a build of this project. Until it exists, Play gets as far as character select and stops there, with nowhere to enter.
   - Install **Windows Dedicated Server Build Support** for your editor from the Unity Hub (Installs → Add modules).
   - Open File → **Build Profiles**, select **Demo Map Server** (under `Demo/Build Profiles`), press **Switch Profile**, then **Build**, and save it as `builds/OpenMMORPG.exe` in the project folder, beside `Assets`.
   - Switch back to **Demo Client** afterwards. While the server profile is active the editor compiles as a server, and Play does not work.
   - Rebuild it whenever you change a scene, an entity or any game data the server uses. A client that disagrees with a stale server is turned away when it tries to enter the map.
4. **Play.** Open `Demo/Scenes/00Init` and press Play. The editor runs the login, central, database (SQLite) and map spawn servers and shows the server list; register an account, create a character and start.

The map server build is Windows only, because the spawner launches `OpenMMORPG.exe`. On other platforms, change the spawn path on `MMOServerInstance` in `00Init` to your own server build.

`Demo/README.md` describes how the demo is put together, and `Demo/CREDITS.md` lists where every asset comes from.

## Updating Open MMORPG

To update, import the newer version the same way you installed it, from the Asset Store or a GitHub release. Re-run **Import Project Settings** only if the release notes say the settings changed.

## Developing Open MMORPG

The kit lives directly in your project's `Assets` folder, so you can work on it in place. Delete the imported `Assets/OpenMMORPG` directory and clone this repository in its place:

```sh
git clone https://github.com/open-mmorpg/OpenMMORPG.git Assets/OpenMMORPG
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for the branch model and how the kit is assembled from its source repositories.

## License

Open MMORPG is released under the [MIT License](LICENSE). Third-party components under `ThirdParty` carry their own licenses in their respective folders. The demo's art, animation, sound and music are dedicated to the public domain under CC0 1.0; see [Demo/CREDITS.md](Demo/CREDITS.md). [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) lists everything bundled.

## Community & Support

Join our [Discord community](https://discord.gg/Czgrg4YGgq) for support, questions, and discussion with other developers and contributors.

## Thanks

Huge thanks to Ittipon Teerapruettikulchai for open sourcing the original kit, and to the MmoKitCE team at [Denarii Games](https://github.com/denariigames) for preserving and hardening it, and for blessing this continuation. Special thanks to the entire community of former customers and new developers who continue to keep this ecosystem alive.

