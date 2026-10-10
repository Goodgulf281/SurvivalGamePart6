# Video script, Part 2: Networked building and collapses (Phases 3–4)

**Working title:** Making my procedural survival game multiplayer, Part 2: building together
**Target length:** 16–20 minutes
**Covers:** Phase 3 (server-owned building), Phase 4 (structural integrity on the server), plus the "blocks don't collapse" bug hunt

## Before recording

- **Multiplayer Play Mode:** host and client (same scenario as Part 1).
- **Diagrams on screen:** `docs/phase3-build-request.svg`, `docs/phase3-piece-removal.svg`
- **Background reading:** `docs/NetworkBuilding.md` explains every block in the diagrams.
- **Files to have open:** `Building/Builder.cs`, `Building/NetworkBuildingArea.cs`, `Building/PlayerBuilderInventoryBridge.cs`, `Building/BuildingArea.cs`, `Building/RecalculationWorker.cs`
- **Demo moments:**
  - both players building on the same structure;
  - a late joiner seeing everything;
  - an overhang collapsing on both screens;
  - a build far from spawn (x ≈ -230) collapsing after the fix.

Line numbers are from `main` at the end of Phase 6.

---

## 1. Cold open (0:00)

**ON SCREEN:** Host builds a wall, client adds a roof to it, the client extends an overhang too far, and it turns blue and crashes down on both screens.

**SAY:**
"Two players building on the same structure, and when one of them overdoes it, physics takes over on both screens. Building was the hardest part of making this game multiplayer: hundreds of pieces, snapping, inventory, and a structural integrity system running on a background thread. Today I'll show you how it's networked, and a bug that made blocks refuse to fall."

## 2. The design: one network object per building area (0:40)

**ON SCREEN:** The game with the building-area outlines visible; then `PLAN.md`, the Decisions table row "Build-piece sync".

**SAY:**
"The obvious approach is to make every block a network object. Don't. Each one costs a spawn message, an object ID and observer bookkeeping, and a big base could have thousands.

My game already groups pieces into building areas of 100 by 100 metres. So there is **one network object per area**: `NetworkBuildingArea`. It holds a synced dictionary of small records, one per piece. The pieces themselves are plain local GameObjects on every client."

**ON SCREEN:** `NetworkBuildingArea.cs` lines 37–66.

```csharp
public struct PieceRecord
{
    public int Id;
    public int TemplateId;
    public Vector3 Position;
    public Quaternion Rotation;
    public int SnapIndex;
}
...
private readonly SyncDictionary<int, PieceRecord> _pieces = new();
```

**SAY:**
"A record is everything a client needs to rebuild a piece: which template, where, how it's rotated and which snap point was used. The grounding, the neighbours and the structural distance only exist on the server."

## 3. Placing a piece, step by step (2:30)

**ON SCREEN:** `docs/phase3-build-request.svg`. Walk through it left to right, highlighting each block as you talk.

### 3a. Owner client: keep the feel local

**SAY:**
"In the left lane, the player who's building. Everything about choosing *where* to build stays local: the transparent preview, raycast, grid snapping, rotation and magnetic snap. That's why it still feels instant. Only when you click does anything go over the network."

**ON SCREEN:** `Builder.cs` line 1092, `RequestBuild`.

**SAY:**
"`Builder.RequestBuild` is the switch. In a network session it calls the ServerRpc on the player's `PlayerBuilderInventoryBridge`. I put the RPCs on the player because then the server automatically knows *who* is asking: the object the RPC arrives on is the caller's own player."

### 3b. Server: trust nothing

**ON SCREEN:** `PlayerBuilderInventoryBridge.cs` line 199, `ServerRequestBuild`, scrolling through the checks.

**SAY:**
"Client input is untrusted, so the server validates everything again: the template exists, the request rate, reach from *this* player, the snap index, inventory, non-buildable areas, duplicates, and that the terrain is loaded. If anything fails, it logs why and stops. That's the gray 'Rejected' box.

Two details worth pointing out. The non-buildable check on the client used the transparent preview to detect overlaps, but the server has no preview, so the server checks by position instead. And I didn't do a full overlap test: snapped pieces are *supposed* to touch, so an overlap test rejects valid builds. The server rejects exact duplicates instead: same template, same position, same rotation."

### 3c. Server builds the real thing

**ON SCREEN:** `Builder.cs` around line 980, `networkArea.AddPiece(new PieceRecord { ... })`.

**SAY:**
"If it passes, the server runs the *same* `InstantiateBuildingPrefab` the single-player game used: grounding check, neighbour detection, structural distance, a new piece ID. The only addition is at the end: add a `PieceRecord` to the area's dictionary, and FishNet sends it to everyone who can see that area. One item is consumed from the inventory, which is a synced dictionary too, written only by the server and readable only by its owner."

**ON SCREEN:** `PlayerBuilderInventoryBridge.cs` lines 52–53.

```csharp
private readonly SyncDictionary<int, int> _inventory =
    new(new SyncTypeSettings(WritePermission.ServerOnly, ReadPermission.OwnerOnly));
```

### 3d. Other clients: build a copy

**ON SCREEN:** `NetworkBuildingArea.cs` lines 201–204, then `Builder.cs` line 1025, `InstantiatePieceCopy`.

```csharp
private void Pieces_OnChange(SyncDictionaryOperation op, int key, PieceRecord value, bool asServer)
{
    // The server (and so the host) owns the real pieces
    if (asServer || IsServerInitialized)
        return;
```

**SAY:**
"On the right, every other client gets the change callback and builds a local copy of the prefab from the record, with the same snap offsets so it lines up exactly. The host skips this, because the server's real piece is already in its scene. Without that check the host would see every block twice.

Late joiners? When an area object spawns on a client, `OnStartClient` builds every record that already exists. FishNet may also deliver the initial records through the change callback, so building is idempotent: a piece that's already there is skipped."

**ON SCREEN:** Demo: a client joins late and the whole structure appears.

## 4. Two hidden problems networking exposed (7:30)

**ON SCREEN:** `Builder.cs` line 201, `TryGetTerrainAt`, and the area registry fields.

**SAY:**
"Two things worked fine in single player but broke with streaming plus networking.

First, building areas were stored on the terrain chunk's `BuildableTerrain` component. When a chunk unloads, that component is destroyed, and walking back created a *second* area at the same spot. Areas now live in a registry on the `Builder`, keyed by a global grid coordinate.

Second, the `Builder` only knew the terrains that existed at startup, so building on a chunk that streamed in later threw an exception. `TryGetTerrainAt` now refreshes its cache when it doesn't recognise a chunk."

## 5. Removing pieces (9:00)

**ON SCREEN:** `docs/phase3-piece-removal.svg`

**SAY:**
"Pieces disappear for two reasons: you destroy them, or they collapse. Both end in the same place, so clients never need to know *why*.

Destroying works like building in reverse. The client collects the IDs of the pieces under the preview, which works because every local copy carries the server's ID, and sends them. The server keeps only real pieces within your reach and removes them."

**ON SCREEN:** `Builder.cs` line 1053, `OnConstructedItemRemoved`.

**SAY:**
"The key rule: every removal goes through `BuildingArea.RemoveWeakConstructedItem`, which calls one hook: `OnConstructedItemRemoved`. It removes the record from the area's dictionary and updates pathfinding. One hook means destroys, collapses and anything I add later all stay in sync automatically."

## 6. Phase 4: structural integrity on the server (11:00)

### 6a. Only the server does the maths

**ON SCREEN:** `RecalculationWorker.cs` line 122 and `Builder.cs` lines 290–323, `UpdateWorkerAreas` and `GetPlayerPositions`.

**SAY:**
"Structural integrity runs Dijkstra over the piece graph on a background thread to find each piece's distance to the ground. Too far and the piece is unsupported. That's server work now: pure clients don't even have the data, because their copies have no `ConstructedItem`.

Before, the worker only checked the areas around *my* player. On a server that's wrong: a client building on the other side of the map would never collapse. So it now collects the areas around *every* connected player."

### 6b. Making collapses look right

**ON SCREEN:** `NetworkBuildingArea.cs` lines 163–164, and `BuildingArea.cs` line 351.

```csharp
[ObserversRpc(ExcludeServer = true)]
public void ObserversCollapse(int[] pieceIds)
```

**SAY:**
"Here's a nice FishNet detail. If the server just removed the records, clients would see collapsing blocks simply vanish. I want them to fall. So before removing anything, the server sends an RPC with the IDs that are about to collapse. An RPC goes out right away, while SyncType changes are batched and sent at their send rate, 0.1 seconds by default, so the RPC arrives first. Each client hands those copies to the falling effect (blue flash, unfreeze, a push, gone after 10 seconds) and stops tracking them. When the record removal arrives a moment later, there's nothing left to delete. `ExcludeServer` stops the host from playing it twice."

### 6c. A thread-safety fix

**ON SCREEN:** `RecalculationWorker.cs` line 141.

```csharp
_thread = new Thread(() => Recalculate(areas));
```

**SAY:**
"While I was in there: the builder replaced the worker's list of areas every five seconds, even while the background thread was reading it. Now the worker takes its own copy when it starts. A one-line fix for a crash that would have been horrible to track down."

**ON SCREEN:** Demo: an overhang collapses on both screens.

## 7. Bug hunt: blocks that wouldn't fall (14:00)

**ON SCREEN:** Build an overhang near spawn: it collapses. Walk to a new area around x = -230 and build the same overhang: nothing happens.

**SAY:**
"After Phase 4, I found this. Near the spawn point, overhangs collapse. Build the same thing a few hundred metres west, and it just floats. No errors in the log.

A piece only collapses if it's *not* grounded, so the question was: why does the server think these blocks touch the ground?"

**ON SCREEN:** The Builder in the Inspector, `World Origin = (-1500, 0, -1500)`; then a simple table drawn on screen:

| Position | Correct chunk | Chunk the code used |
|---|---|---|
| x = 24 (spawn) | 0 | 0 ✓ |
| x = -230 | -1 | 0 ✗ |

**SAY:**
"The `Builder` works out which terrain a position is on with a world origin and a 1000 metre terrain size. That origin was still minus 1500: a leftover from the old fixed terrain grid. Streamed chunks start at zero, and 1500 isn't a multiple of 1000. So *half of every chunk* was mapped to its neighbour. The spawn area happened to sit in the good half. At x = -230, the grounding check sampled the height of the wrong chunk, at its edge, and decided every block was standing on the ground."

**ON SCREEN:** `Builder.cs` line 396, `GetTerrainIndex`.

**SAY:**
"The fix isn't to change a number in the scene. The terrain lookup now asks the streaming controller, which is the code that actually places the chunks. One source of truth, and the two can never drift apart again."

**ON SCREEN:** Rebuild at x = -230: it collapses.

## 8. Wrap-up (17:00)

**ON SCREEN:** Both diagrams side by side; `PLAN.md` with Phases 3–4 ticked.

**SAY:**
"So: one network object per building area, small records instead of objects, the server builds and validates, clients build copies, one removal hook, and collapses that play on every screen.

In the final part: NPCs and pathfinding moved to the server, and saving the whole world so you can come back to it. See you in Part 3."

---

## Optional cut points

- **Short on time:** drop section 4, or fold it into one line during 3c.
- **Separate short:** section 7, the bug hunt, works on its own.
