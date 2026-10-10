# Video script, Part 1: Groundwork, players and a shared world (Phases 0–2)

**Working title:** Making my procedural survival game multiplayer, Part 1: same world, no terrain sent
**Target length:** 16–20 minutes
**Covers:** `PLAN.md` decisions, Phase 0 (groundwork and determinism), Phase 1 (networked player), Phase 2 (world sync)

## Before recording

- **Multiplayer Play Mode:** `NetworkingScenario` with Main Editor = `Host` and Virtual Editor = `Client`. Both caches cleared once for the determinism demo.
- **Diagrams on screen:** `docs/phase1-owner-vs-remote.svg`, `docs/phase1-player-startup.svg`
- **Files to have open:** `PLAN.md`, `TerrainUtils/StableHash.cs`, `TerrainUtils/AsyncTerrainPrefabPlacer.cs`, `Controller/ThirdPersonController.cs`, `GameLogic/GameManager.cs`, `Networking/WorldState.cs`, `TerrainUtils/TerrainStreamingController.cs`, `Networking/WorldPlayerSpawner.cs`
- **Demo moments:**
  - two windows walking around together;
  - an F9 dump diff with no differences;
  - a terrain edit seen on both screens;
  - the client walking far away while the host's view shows terrain under it.

Line numbers are from `main` at the end of Phase 6, so they may shift if the code changes.

---

## 1. Cold open (0:00)

**ON SCREEN:** Host and client side by side, both walking on the same hill, trees in the same places. The client raises the terrain; the bump appears on the host's screen too.

**SAY:**
"Two players, one procedurally generated world, the same trees, the same hills, and when I raise the ground here, it rises over there too. And here's the thing: not a single byte of terrain goes over the network. In this video I'll show you how I took the survival game from the last episodes and made it multiplayer with FishNet, starting with the foundations: a shared world, networked players, and terrain edits everyone agrees on."

## 2. Where we start and the big decisions (0:45)

**ON SCREEN:** Recap footage of episode 5: streaming terrain, building, A* agents. Then `PLAN.md`, the **Decisions** table and **Guiding principles**.

**SAY:**
"Quick recap: the game streams terrain chunks around the player, places trees and rocks procedurally, lets you build structures that can collapse, and has NPCs using my own Burst A* pathfinder. None of that was written with networking in mind.

So before writing any code I made a plan, and three decisions shaped everything.

One: **host mode**, a listen server. One player is both server and client, but I keep the code honest so it would also work with a dedicated server.

Two: the **server is the authority over the world**. Building, terrain edits, structural integrity, pathfinding, NPCs, inventory and saving all run on the server. Clients ask, the server decides.

Three, the most important one: **terrain is never sent over the network**. A 1000 by 1000 metre chunk with trees and rocks is a lot of data. Instead the server shares one number, the seed, and every machine generates the identical world itself. That only works if generation is perfectly deterministic, and that's where Phase 0 begins."

## 3. Phase 0: making generation deterministic (2:30)

### 3a. The GetInstanceID trap

**ON SCREEN:** `AsyncTerrainPrefabPlacer.cs` line 361.

```csharp
int ruleSeed = GetDeterministicSeed(chunkCoord.x, chunkCoord.y, StableHash.Fnv1a(rule.prefab.name));
```

**SAY:**
"The prefab placer seeds a random generator per chunk and per placement rule. The original code used `GetInstanceID()` of the prefab as part of that seed. That's fine in single player, but the instance ID is different in the editor, in a build and even between sessions. So two machines with the same seed would place different trees, and physics, building and pathfinding would all disagree.

`string.GetHashCode()` has the same problem in .NET: it isn't guaranteed stable across processes. So I wrote a tiny FNV-1a hash and use the prefab's *name* instead."

**ON SCREEN:** `StableHash.cs`, lines 25–40.

```csharp
public static int Fnv1a(string text)
{
    uint hash = FnvOffsetBasis;
    ...
        hash = (hash ^ (byte)c) * FnvPrime;
        hash = (hash ^ (byte)(c >> 8)) * FnvPrime;
    ...
    return unchecked((int)hash);
}
```

**SAY:**
"Because the hashes changed, old chunk caches on disk were now wrong, so I bumped the cache version and old caches are rejected. One thing to remember: rules are now keyed by prefab name, so two different prefabs with the same name would share a seed."

### 3b. Proving it: the F9 dump

**ON SCREEN:** Press F9 in both windows; open the two dump files in a diff tool, which shows no differences.

**SAY:**
"'I think it's deterministic' isn't good enough, so I added a debug dump. Press F9 and every loaded chunk writes its placement records (prefab, position, rotation, scale) plus a hash of the heightmap. Run it on two machines and diff the files. They must be identical, byte for byte.

I tested three cases: both caches empty, one warm and one cold, and a fresh editor session. And this test found two real bugs."

### 3c. Two bugs the dump found

**ON SCREEN:** `AsyncTerrainPrefabPlacer.cs` lines 325–337, the replay of terrain modifications on cache load.

**SAY:**
"Bug one. Some prefabs flatten the terrain under them. When a chunk was loaded from the cache, the prefabs came back, but the flattening didn't, because heights are regenerated from the seed every time. So a player with a warm cache had different ground from a player with a cold cache. The fix: the cache now records which rule placed each prefab, and loading replays the terrain modifications in the original order."

**ON SCREEN:** Line 161.

```csharp
// Pin this chunk's terrain before awaiting ...
Terrain chunkTerrain = terrain;
```

**SAY:**
"Bug two was nastier: a race condition. The streaming controller loads several chunks at once. It set the placer's terrain field, then awaited the cache read, and by the time it resumed another chunk had replaced that field. So a chunk could sample or flatten someone else's terrain, depending on timing. The fix is one line: copy the terrain into a local before the await."

### 3d. Tooling: Multiplayer Play Mode and a bootstrap

**ON SCREEN:** The MPPM scenario window; `NetworkBootstrap` on the NetworkManager.

**SAY:**
"For testing I use Unity's Multiplayer Play Mode: a second editor player inside the same project. Two players writing the same cache folder would trample each other, so `PlayModeInstance` detects a virtual player and gives it its own folder. And `NetworkBootstrap` starts host or client automatically, from command-line flags in a build, or from the Multiplayer Play Mode tags in the editor. No more clicking the HUD every time."

## 4. Phase 1: a networked player (6:30)

### 4a. Owner versus remote copy

**ON SCREEN:** `docs/phase1-owner-vs-remote.svg`

**SAY:**
"Every player now exists on every machine, but only one machine controls each copy. On the owner's machine, input is enabled and `Update` moves the character. Everywhere else, input stays off and the copy just follows its `NetworkTransform` and `NetworkAnimator`. I chose client-authoritative movement: the owner sends its position, which is simple and responsive. Prediction can come later."

**ON SCREEN:** `ThirdPersonController.cs` lines 140–162 and 172–175.

```csharp
public override void OnStartClient()
{
    if (!IsOwner)
        return;
    ...
    if (_playerInput != null)
        _playerInput.enabled = true;

    if (GameManager.Instance != null)
        GameManager.Instance.RegisterLocalPlayer(this);
}

private void Update()
{
    // Remote players are moved by their NetworkTransform
    if (!IsOwner || !_movementEnabled)
```

**SAY:**
"The controller became a `NetworkBehaviour`. `PlayerInput` is disabled on the prefab and only switched on for the owner. Leave it on everywhere and every copy would read your keyboard. And because a spawned prefab can't keep references to scene objects, the owner looks up the camera and the building input when it spawns."

### 4b. Two things must be ready: the startup flow

**ON SCREEN:** `docs/phase1-player-startup.svg`. Point at the two columns, then where they meet in `SetupLocalPlayer`.

**SAY:**
"Startup used to be simple: wait for the terrain, enable the scene player. Now there are two independent things, and they can arrive in either order. On the left, the world: the streaming controller loads the first chunks. On the right, the player: the network spawns it and the owner registers with the `GameManager`. Only when both are ready does `SetupLocalPlayer` run."

**ON SCREEN:** `GameManager.cs` lines 230–242.

```csharp
private IEnumerator SetupLocalPlayer(ThirdPersonController player)
{
    while (!_worldReady)
        yield return null;

    while (_streamingController != null && !_streamingController.IsChunkLoadedAt(player.transform.position))
        yield return null;

    // Let physics pick up the new terrain colliders before raycasting
    yield return new WaitForFixedUpdate();
```

**SAY:**
"This is spawn safety. A player can spawn before its chunk exists, and with gravity on it would fall through the world. So the player is held, gravity and movement off, until the chunk under it has loaded locally. Then we wait one physics step, raycast down, snap it onto the ground and release it. Only then do we switch the camera and hand the player to the building system."

**ON SCREEN:** Demo: host and client spawn and walk around.

## 5. Phase 2: a shared world (10:30)

### 5a. One number: the seed

**ON SCREEN:** `WorldState.cs`, the `SyncVar` at line 92, then `OnStartServer` around line 151.

```csharp
private readonly SyncVar<int> _seed = new();
...
_seed.Value = ReadSeedFromArgs(Environment.GetCommandLineArgs(), _defaultSeed);
```

**SAY:**
"`WorldState` is a global network object the server spawns at startup. Its job in Phase 2 is to carry the seed. The server sets it in `OnStartServer`, so it goes out in the spawn message, and every client receives it before anything else. Then each machine calls `BeginStreaming(seed)`. Streaming no longer starts by itself in `Start`; it waits for the seed, so nobody can generate the wrong world.

A fun discovery: my terrain heights don't actually use the seed. The height noise is a pure function of world position. The seed drives where the trees and rocks go."

### 5b. Streaming around every player

**ON SCREEN:** `TerrainStreamingController.cs` lines 259–300, `RegisterStreamingTarget` and `UnregisterStreamingTarget`.

**SAY:**
"Single player streams terrain around one transform. But the host has to simulate the world around *every* player: if the client walks two kilometres away, the host needs colliders under it. So streaming now follows a list of targets. A chunk loads if it's within the load radius of *any* target, and unloads only when it's outside the unload radius of *all* of them.

The server registers every player. A client registers only its own. And the host's own player? It's registered twice, once as server and once as owner. So registrations are reference counted, and a target only disappears when both are released."

**ON SCREEN:** Demo: client walks far away; on the host, the free camera shows terrain loading under the client.

### 5c. Spawning on real ground

**ON SCREEN:** `WorldPlayerSpawner.cs` lines 126–178.

**SAY:**
"FishNet's `PlayerSpawner` just cycles through spawn points; it knows nothing about terrain. So I replaced it with `WorldPlayerSpawner`. It picks a spawn point whose chunk the server has already loaded. If none is loaded, for example because everyone walked away, it streams around that spawn point first, waits for the chunk, snaps to the ground the server sees, and only then spawns the player."

### 5d. Terrain edits everyone agrees on

**ON SCREEN:** Demo: raise terrain on the client, see it on the host. Then `WorldState.cs`, the `SyncList` at line 95 and `ServerRequestTerrainEdit` at line 280.

**SAY:**
"Terrain edits were the last Phase 2 problem. If I raise the ground locally, nobody else sees it, and the moment the chunk unloads, it's gone, because heights are regenerated from the seed.

So an edit is now a *request*. The client sends a ServerRpc, and the server checks that you have a player, the edit is within reach, you're not spamming, the size is sane and the chunk is loaded. Then it appends a small `TerrainEdit` record to a `SyncList` on `WorldState`."

**ON SCREEN:** Lines 326–333, then 382–397.

```csharp
private void TerrainEdits_OnChange(SyncListOperation op, int index, TerrainEdit oldItem, TerrainEdit newItem, bool asServer)
{
    ...
    // On the host the server callback already applied it to the shared terrain
    if (!asServer && IsServerInitialized)
        return;
```

**SAY:**
"Every machine applies new edits to chunks it has loaded. On the host, the change callback fires twice, once as server and once as client, but the host only has one terrain, so we skip the second one.

And when a chunk loads later, the streaming controller raises `ChunkTerrainReady`, after generation and tree placement but *before* the chunk counts as loaded. `WorldState` replays every edit for that chunk in order, so colliders and pathfinding only ever see the edited ground.

There's one subtle case: an edit can arrive just as its chunk is loading. To make sure each edit is applied exactly once, I keep a per-chunk counter of how far down the list we've applied. Live or replayed, never twice."

## 6. Wrap-up (17:00)

**ON SCREEN:** `PLAN.md` with Phases 0–2 ticked.

**SAY:**
"So that's the foundation. A deterministic world we can prove with a diff, players that spawn safely and move smoothly, a seed instead of terrain data, streaming that follows every player, and terrain edits that survive reloads and late joiners.

Next time it gets interesting: building. Hundreds of blocks, structural integrity and collapses. How do you network that without making every block a network object? See you in Part 2."

---

## Optional cut points

- **Short on time:** drop 3d (tooling) and 5c (spawner); mention them in one line each.
- **Separate short:** 3b–3c ("the F9 dump found two bugs") works as its own clip.
