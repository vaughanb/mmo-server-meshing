// Server Meshing add-on — the build must define DISABLE_ADDRESSABLES (DESIGN.md 0.99).
//
// The add-on resolves every ghost, lent monster, mount and handed-over static entity through the kit's prefab tables
// (GameInstance.MonsterCharacterEntities, VehicleEntities, ...) and calls the kit's non-Addressables signatures
// (MountType.GetPrefab, GameSpawnArea.Spawn). Without the define the kit compiles its Addressables variants of those,
// and the add-on fails with CS1929/CS7036 errors that do not say why. This line says why. Each build target keeps its
// own define list: Dedicated Server is separate from Windows/Mac/Linux, and a build profile can add its own.
#if !DISABLE_ADDRESSABLES
#error Server Meshing needs the scripting define DISABLE_ADDRESSABLES on this build target. Add it in Project Settings > Player > (this target's tab, e.g. Dedicated Server) > Other Settings > Scripting Define Symbols, or in the build profile's Scripting Defines. The Setup window (Tools > Open MMORPG > Server Meshing > Setup > Validate) checks it and adds it. See SETUP.md section 3.
#endif
