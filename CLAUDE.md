# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Unity **6000.0.68f1** (Unity 6) project using URP, the new Input System, Cinemachine, Burst/Jobs/Collections/Mathematics. It is a YouTube tutorial series project (episode 5: https://youtu.be/49oQ7giICds) combining streamed procedural terrain, a player building system, and a custom Burst-accelerated A* grid pathfinder. Episode 6 (in progress) adds FishNet 4.x multiplayer; `PLAN.md` holds the phased plan, decisions (host/listen server, one networked object per `BuildingArea`, client-authoritative movement) and a checklist of what's done. Read it before any networking work and tick items off as they land.

FishNet 4.7.3 is committed under `Assets/FishNet` (third-party; don't edit it). `fishnet.md` is the FishNet reference guide that `PLAN.md` section numbers (§) point to.

**Determinism check:** in Play Mode, press F9 (or use the `AsyncTerrainPrefabPlacer` context menu "Dump Placement Records") to write every loaded chunk's placement records and heightmap hash to `persistentDataPath/PlacementDumps[_<mppm id>]/`. Dumps from two players with the same seed must be identical.

There is no command-line build, lint, or test setup: no asmdefs, no test assemblies (the Test Framework package is installed but unused). Compile and run by opening the project in the Unity Editor; main scene is `Assets/main scene.unity`. All project code lives in `Assets/Goodgulf/` under `Goodgulf.*` namespaces (a few files like `NonBuildableArea` and `PlayerBuilderInventoryBridge` sit in the global namespace). Everything else under `Assets/` is Unity/TextMesh Pro/template content.

## Architecture

Five subsystems, the first four coordinated at startup by `GameLogic/GameManager.cs`:

1. **Terrain streaming (`TerrainUtils/`)**: `TerrainStreamingController` loads/unloads terrain chunks in a radius around the player. Heights come from `StreamingTerrainGeneratorJobs` + `TerrainHeightJob` (Burst jobs, biome params); `TerrainDataPool` recycles `TerrainData`. Prefab scatter (trees/rocks) runs through `AsyncTerrainPrefabPlacer` driven by the `PrefabPlacementConfig` ScriptableObject, cached per chunk by `ChunkPrefabCache` / `AsyncChunkCacheManager`. The controller exposes `UnityEvent`s: `onInitialChunksLoaded`, `onChunkLoadStart`, `onChunkLoadComplete`, `onChunkUnload`.

2. **Pathfinding (`Pathfinding/`)**:
   - `PathfindingGraph` is a double-buffered grid of blittable `NodeData` in `NativeArray`s. Buffer A is live and read by A*; buffer B is staging and written by jobs. They swap only when no search holds a reference (ref-counted).
   - `GraphRebuildJobs` holds the Burst jobs (`FullGraphRebuildJob`, `PartialGraphRebuildJob`) plus `PhysicsObstacleOverlay`, a main-thread physics pass.
   - `TerrainGraphIntegration` (singleton, `[RequireComponent(TerrainStreamingController)]`) is the bridge. It listens to chunk events, caches heightmaps, queues `DirtyRegion`s for partial rebuilds, and spreads the physics overlay across frames (`_maxOverlayNodesPerFrame`). It keeps a static obstacle cache, so physics is only re-sampled for cells marked dirty. Resolution comes from `GraphConfig.NodesPerMetre` (0.5 means 2 m cells), and graph width/height are in node units, not world units.
   - `AStarSearch.FindPath` returns a `PathResult`. `PathfindingAgent` follows paths in several modes (move/patrol/follow, with LOS behaviour) and includes a custom Editor.

3. **Building (`Building/`)**: `Builder` (singleton) is the central manager for buildable terrains, `BuildingArea`s and `ConstructedItem` lifecycle. `BuilderInput` handles placement, snapping and rotation. `RecalculationWorker` runs structural integrity on a **background thread**: Dijkstra (`DijkstraUnity`) over item connectivity gives each item a distance-to-grounded, and `_maxStrength` is the cutoff. Results go back to Unity via `MainThreadDispatcher.Enqueue`. Only touch the Unity API from the main thread.

4. **Player (`Controller/`, `Input/`)**: `ThirdPersonController` and `OrbitCamera`.

5. **Networking (`Networking/`)**: `NetworkBootstrap` (on the NetworkManager) picks host/server/client from command-line flags (`-host`/`-server`/`-client`, `-address`, `-port`), then Multiplayer Play Mode tags (`Host`/`Server`/`Client`), then an Inspector default. `PlayModeInstance` detects MPPM virtual players via reflection and gives each its own `TerrainPrefabCache_<id>` folder, so chunk caches aren't shared.

### Startup order
`GameManager.Awake` disables the player and uses the scene camera. `GameManager.InitializeGameLogic()`, wired to the streaming controller's initial-load event in the scene, then enables the player, calls `Builder.Instance.CacheTerrains()` / `SetPlayer`, fires `OnTerrainReady`, and finally `TerrainGraphIntegration.Instance.InitializeListeners()` + `Initialize()`. Chunk events arriving before `Initialize()` are ignored.

### Key cross-system contract
After placing or destroying anything that should block pathing, call:
```csharp
TerrainGraphIntegration.Instance.InvalidateObstacleCache(position, radius); // or (worldMin, worldMax)
```
Otherwise the obstacle cache keeps stale walkability. Terrain height edits need `InvalidateHeightmapCache(chunkCoord)` and/or `RequestDirtyRegionRebuild(...)`.

### Determinism (multiplayer)
Terrain is never sent over the network: every machine regenerates chunks and scatter from a shared seed, so generation must be bit-identical across editor, builds and sessions. Never seed or hash with `GetInstanceID()` or `string.GetHashCode()`; use `StableHash.Fnv1a(...)` (scatter rules are keyed by prefab *name*). If the cached placement format or its inputs change, bump `ChunkPrefabCache.CurrentCacheVersion` so stale caches are rejected. Server-authoritative code should check `IsServerInitialized` / `InstanceFinder.IsServerStarted`, not "am I the host".

## Conventions

- **Logging:** use `Goodgulf.Logging`, not raw `Debug.Log`. MonoBehaviours implement `IDebuggable`, back it with `[SerializeField] private bool _debugEnabled` + `public bool DebugEnabled => _debugEnabled;`, and log via `this.LogInfo/LogWarning/LogError(...)`. Output is gated per instance and by `GameLogger.GlobalMinLevel`. Static code calls `GameLogger.Info/Warning/Error` directly. Older files still use `Debug.Log`.
- Private serialized fields use `_camelCase` with `[SerializeField]`, and singletons follow `public static X Instance { get; private set; }` set in `Awake`. Use `TryGetComponent` for component lookups.
- Newer files start with a header block (`Script / Episode / Description / Author / Date`) and use `// ====` section dividers; keep that style when editing them.
- Native containers owned by MonoBehaviours must be disposed in `OnDestroy`. Complete in-flight `JobHandle`s before disposing or swapping buffers.
