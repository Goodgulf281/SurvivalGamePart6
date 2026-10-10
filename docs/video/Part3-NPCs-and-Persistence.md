# Video script, Part 3: NPCs on the server and saving the world (Phases 5–6)

**Working title:** Making my procedural survival game multiplayer, Part 3: NPCs and a persistent world
**Target length:** 14–18 minutes
**Covers:** Phase 5 (server-authoritative pathfinding and NPCs), Phase 6 (world save/load, disconnects, hardening), and a series wrap-up

## Before recording

- **Multiplayer Play Mode:** host and client (same scenario as Parts 1–2).
- **Diagrams on screen:** `docs/phase5-pathfinding.svg`, `docs/phase6-persistence.svg`
- **Files to have open:** `GameLogic/GameManager.cs`, `Pathfinding/TerrainGraphIntegration.cs`, `Pathfinding/PathfindingAgent.cs`, `Networking/WorldSave.cs`, `Networking/WorldState.cs`, `Building/Builder.cs`
- **Demo moments:**
  - NPCs patrolling on both screens;
  - an NPC approaching the client's player;
  - an NPC pathing around a freshly built wall;
  - build, quit, restart and everything is back;
  - a late joiner after the load.

Optional: show the TransportManager latency simulator (100–150 ms, 1–3% loss) if you've tried it.

Line numbers are from `main` at the end of Phase 6.

---

## 1. Cold open (0:00)

**ON SCREEN:** An NPC patrols, spots the client's player and walks toward it; the host builds a wall in its way and it walks around. Cut to: stop the game, start it again, and the wall and the terrain edits are still there.

**SAY:**
"NPCs that chase whichever player is closest, that walk around a wall you just built, and a world that's still there when you come back tomorrow. This is the last part of making the survival game multiplayer: moving pathfinding and NPCs to the server, and saving the whole world."

## 2. Phase 5: who should run pathfinding? (0:40)

**ON SCREEN:** `docs/phase5-pathfinding.svg`, the Server lane on the left and the Clients lane on the right.

**SAY:**
"My pathfinder builds a navigation grid from the terrain heights, plus a physics pass for obstacles like trees and walls. Before networking, every machine built its own graph. That wastes CPU on clients, and worse, NPCs could make different decisions on different machines.

The rule from the plan: NPCs are the server's business. So the graph is built only on the server, and NPCs only think and move on the server. Clients just watch."

### 2a. Building the graph when the world is ready

**ON SCREEN:** `GameManager.cs` lines 153–169.

```csharp
private void InitializePathfinding()
{
    TerrainGraphIntegration graph = TerrainGraphIntegration.Instance;
    if (graph == null || graph.IsInitialized)
        return;

    if (Builder.IsNetworkClientOnly)
    {
        this.LogInfo("Pure client: TerrainGraphIntegration disabled (pathfinding runs on the server)");
        graph.enabled = false;
        return;
    }

    graph.InitializeListeners();
    graph.Initialize();
}
```

**SAY:**
"This used to happen once the local player had spawned. A dedicated server has no local player, so it would never have built a graph. Now it happens as soon as the world is ready. On the server we build the graph; on a pure client we just switch the component off."

### 2b. Keeping the graph up to date

**ON SCREEN:** In the diagram, the 'World changes' box; then `TerrainGraphIntegration.cs` lines 752 and 779 (`if (Graph == null) return;`).

**SAY:**
"Everything that changes the world (building, destroying, collapses and terrain edits) already tells the graph to re-check that area, and since Phases 2 to 4 all of those run on the server anyway. The invalidation calls now simply do nothing before the graph exists, so a stray call on a client is harmless instead of quietly filling a queue that nobody processes."

### 2c. NPCs think on the server

**ON SCREEN:** `PathfindingAgent.cs` line 60 and line 193.

```csharp
public class PathfindingAgent : NetworkBehaviour, IDebuggable
...
// Server authoritative: clients only see the NetworkTransform result
if (!IsServerInitialized)
    return;
```

**SAY:**
"The agent became a `NetworkBehaviour`, and its `Update` starts with one guard: if you're not the server, do nothing. The server requests paths and moves the agent, and a server-authoritative `NetworkTransform` carries the result to every client. That's the arrow to 'Agent copy' on the right.

I kept the four agents as scene objects instead of turning them into a spawnable prefab. FishNet spawns scene network objects automatically, and their patrol points and leader stay ordinary scene references, which works because only the server ever reads them."

### 2d. Chasing the nearest player

**ON SCREEN:** `PathfindingAgent.cs` line 112 and `UpdateNearestPlayer` at line 450.

**SAY:**
"The approach and flee behaviour used to target 'the player': one scene object that no longer exists. Now there's a 'Target Nearest Player' option. Every half second the server looks at all connected players and picks the closest. So the NPC reacts to whoever is near it, host or client."

**ON SCREEN:** Demo: the NPC approaches the client's player, then paths around a new wall.

### 2e. An honest limitation

**SAY:**
"One limitation I've written down in the plan: the graph covers the area around *one* player, the host's, or the first player on a dedicated server. If another player is far away, NPCs over there have no graph to walk on. The fix is to build the graph around every streaming target, the same way terrain streaming already works. That's on the list for later."

## 3. Phase 6: saving the world (7:00)

**ON SCREEN:** `docs/phase6-persistence.svg`

### 3a. What's in a save

**ON SCREEN:** `WorldSave.cs` line 38, `WorldSaveData`.

**SAY:**
"The old save wrote one file per building area, and the loader read three hard-coded file names and forgot the snap points. With the server owning the world, a save is now one file holding three things: the seed, every piece in the order it was built, and the list of terrain edits.

Notice what's *not* in there: no terrain heights, no trees, no structural distances. The seed regenerates the terrain, the edit list reshapes it, and the build code recomputes the rest. A whole world fits in a small JSON file."

**ON SCREEN:** `WorldSave.cs` lines 72–76.

```csharp
string tempPath = path + ".tmp";
File.WriteAllText(tempPath, JsonUtility.ToJson(data, true));
if (File.Exists(path))
    File.Delete(path);
File.Move(tempPath, path);
```

**SAY:**
"A small safety habit: write to a temporary file first, then swap it in. If the game crashes halfway through saving, you keep the old save instead of a broken one."

### 3b. Loading: follow the diagram top to bottom

**SAY:**
"Loading happens when the server starts, in `WorldState.OnStartServer`. The save's seed replaces the default, because pieces only make sense on the terrain they were built on. The terrain edits go into the synced list *before* streaming starts, so the very first chunks already replay them, on the server and on every client.

And the pieces? They can't all be built straight away: a piece needs the terrain under it to check whether it's grounded. So they're queued by chunk."

**ON SCREEN:** `Builder.cs` lines 1316 and 1352, `QueueSavedPieces` and `BuildPendingSavedPieces`.

**SAY:**
"When a chunk finishes loading on the server, its saved pieces are rebuilt in their original order, through the *normal* build code. That's the trick: grounding, neighbours, IDs and the network records all come out exactly as if a player had just built them. So clients, including late joiners, get their copies through the same records as in Part 2. No special load code on the client at all.

Pieces on chunks nobody has visited yet just wait, and they're still written back on the next save, so nothing is lost."

### 3c. Saving and loading by hand

**ON SCREEN:** `WorldState.cs` lines 204 and 230.

**SAY:**
"The server saves automatically when it stops, and the save key works too. Loading mid-game is stricter: it only loads into an untouched world with the same seed. Anything else would duplicate pieces or need different terrain under players who are already walking on it, so it's refused with a clear log message."

**ON SCREEN:** Demo: build, edit terrain, stop, restart; everything is back. A client joins and sees it all.

### 3d. Disconnects and what belongs to whom

**SAY:**
"When a player disconnects, their character despawns, which already unregisters it as a streaming target, so the server stops keeping their part of the world loaded.

Then a design decision: what persists per player? Building belongs to the world: if you build a house and log off, it stays for everyone. Inventory is different. Right now it's per connection and resets when you reconnect, because without a login there's no reliable way to know it's 'you' again. An authenticator is the next step if you want that."

## 4. Hardening (12:30)

**ON SCREEN:** NetworkManager → TransportManager → Latency Simulator. Optional: the game at 150 ms.

**SAY:**
"Two last checks from the plan. Test with fake lag: FishNet's latency simulator at 100 to 150 milliseconds and a few percent packet loss. Building should still feel fine, because the preview is local and only the confirmation waits for the server. And set up FishNet's logging configuration, so its warnings are useful without drowning out the game's own logs."

## 5. Series wrap-up (13:30)

**ON SCREEN:** All six diagrams in sequence; then `PLAN.md` scrolled top to bottom with the ticks.

**SAY:**
"Let's look back. Three ideas carried the whole project.

**Send rules, not data.** A seed instead of terrain, records instead of objects, an edit list instead of heightmaps.

**The server decides, clients ask.** Building, terrain edits, collapses, NPCs and saving are all server authoritative, and every request is validated.

**Prove it.** The F9 dump turned 'I think it's the same world' into a diff, and it found real bugs.

What's next? Building the pathfinding graph around every player, an authenticator so inventories can be saved per player, a dedicated server build, and maybe client-side prediction for movement.

Everything is on GitHub: the code, the plan with every step ticked off, and these diagrams. Links are in the description. Thanks for watching, and I'll see you in the next episode."

---

## Optional cut points

- **Short on time:** drop section 4 (hardening) and 2e (the limitation, but mention it in the wrap-up).
- **Separate short:** 3a–3b ("a whole world in one small JSON file") works on its own.
