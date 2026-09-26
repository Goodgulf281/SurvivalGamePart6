# FishNet (Fish-Networking) Reference for Unity Projects

A practical guide to making Unity projects network-enabled with **FishNet: Networking Evolved**.

- **Sources:** the FishNet **4.7.3** source in `Assets/FishNet` (demos, runtime, and editor code) and the official docs at <https://fish-networking.gitbook.io/docs> (full index: `https://fish-networking.gitbook.io/docs/llms.txt`; add `.md` to any page URL to get its markdown).
- **Researched in:** Unity `6000.0.84f1`. FishNet declares a minimum of Unity 2021.3.
- **API reference:** <https://fish-networking.com/FishNet/api/api/index.html>
- **Where the docs and source disagree, this guide follows the source.** Section 32 lists the conflicts.

---

## Table of Contents

1. [Core mental model](#1-core-mental-model)
2. [Installation](#2-installation)
3. [Recipe: converting a single-player project](#3-recipe-converting-a-single-player-project)
4. [The NetworkManager and its sub-managers](#4-the-networkmanager-and-its-sub-managers)
5. [Transports](#5-transports)
6. [Starting and stopping the network](#6-starting-and-stopping-the-network)
7. [NetworkObject](#7-networkobject)
8. [NetworkBehaviour: properties and callbacks](#8-networkbehaviour-properties-and-callbacks)
9. [Running code only on the server or client](#9-running-code-only-on-the-server-or-client)
10. [Spawning and despawning](#10-spawning-and-despawning)
11. [Player spawning](#11-player-spawning)
12. [Ownership](#12-ownership)
13. [Choosing a communication method](#13-choosing-a-communication-method)
14. [Remote Procedure Calls (RPCs)](#14-remote-procedure-calls-rpcs)
15. [SyncTypes (SyncVar, SyncList, and others)](#15-synctypes)
16. [Broadcasts](#16-broadcasts)
17. [Serialization](#17-serialization)
18. [NetworkConnection](#18-networkconnection)
19. [Network events](#19-network-events)
20. [Observers and area of interest](#20-observers-and-area-of-interest)
21. [Scene management](#21-scene-management)
22. [NetworkTransform and NetworkAnimator](#22-networktransform-and-networkanimator)
23. [Time and ticks](#23-time-and-ticks)
24. [Client-side prediction (CSP)](#24-client-side-prediction-csp)
25. [Lag compensation](#25-lag-compensation)
26. [Authentication](#26-authentication)
27. [Cameras and input](#27-cameras-and-input)
28. [Dedicated server builds](#28-dedicated-server-builds)
29. [Testing and debugging](#29-testing-and-debugging)
30. [Pro features and beta toggles](#30-pro-features-and-beta-toggles)
31. [Migrating from Mirror](#31-migrating-from-mirror)
32. [Differences between the docs and the 4.7.3 source](#32-differences-between-the-docs-and-the-473-source)
33. [Troubleshooting and FAQ](#33-troubleshooting-and-faq)
34. [Pitfalls checklist](#34-pitfalls-checklist)
35. [Where to look in the FishNet folder](#35-where-to-look-in-the-fishnet-folder)

---

## 1. Core mental model

A FishNet game is a single-player game running on several machines. FishNet keeps selected state in sync and lets code on one machine trigger methods on another.

| Concept | Meaning |
|---|---|
| **Server** | The authoritative instance. It validates actions, owns the game state, and spawns networked objects. |
| **Client** | A player's instance. It sends input or requests and receives state. |
| **Host (clientHost)** | Server and client running in the same process. Also called a listen server. FishNet does **not** use P2P. A "P2P" game in FishNet is really a host, usually behind a relay such as Steam or EOS. |
| **NetworkManager** | The central hub. It owns ServerManager, ClientManager, TimeManager, SceneManager, ObserverManager, TransportManager, PredictionManager and others. It must **not** have a NetworkObject on itself, its parents, or its children. |
| **NetworkObject** | The component that gives a GameObject a network identity (`ObjectId`). It is required on anything networked. |
| **NetworkBehaviour** | A MonoBehaviour subclass that can hold RPCs, SyncTypes and network callbacks. It needs a NetworkObject on the same object or a parent. |
| **Owner** | The one client (`NetworkConnection`) that owns an object. The server can never be the owner. |
| **Controller** | The owner, or the server when the object has no owner. Check it with `IsController` or `HasAuthority`. |
| **Observer** | A client that can "see" an object. Only observers receive the object's spawn, RPCs and SyncTypes. |
| **Tick** | A fixed network step, 30 Hz by default. Sending, prediction and optionally physics all run on ticks. |
| **Spawnable prefabs** | A prefab registry (`DefaultPrefabObjects`) that is generated automatically. It lets the server say "spawn prefab #N" and have clients instantiate the same prefab. |

**Anti-singleton design.** FishNet can run several NetworkManagers at once. Reach managers through `base.NetworkManager`, `base.ServerManager` and similar inside a NetworkBehaviour. Anywhere else, use `InstanceFinder.*`.

**Server-authoritative by default.** Only the server can spawn objects, change ownership, and write SyncTypes that propagate. A client asks the server for changes with a `ServerRpc` or a broadcast.

**Namespaces you will use most:**

```csharp
using FishNet;                           // InstanceFinder
using FishNet.Object;                    // NetworkBehaviour, NetworkObject, [ServerRpc], [ObserversRpc], [TargetRpc], [Server], [Client]
using FishNet.Object.Synchronizing;      // SyncVar<T>, SyncList<T>, SyncDictionary<K,V>, SyncHashSet<T>, SyncTimer, SyncStopwatch, SyncTypeSettings
using FishNet.Object.Prediction;         // IReplicateData, IReconcileData, [Replicate], [Reconcile], ReplicateState, PredictionRigidbody
using FishNet.Connection;                // NetworkConnection
using FishNet.Transporting;              // Channel, LocalConnectionState, RemoteConnectionState, *ConnectionStateArgs
using FishNet.Managing;                  // NetworkManager
using FishNet.Managing.Scened;           // SceneLoadData, SceneUnloadData, ReplaceOption, event args
using FishNet.Managing.Timing;           // TimeManager, TickRounding, PreciseTick, TickType
using FishNet.Broadcast;                 // IBroadcast
using FishNet.Serializing;               // Writer, Reader (custom serializers, payloads)
using FishNet.CodeGenerating;            // [AllowMutableSyncType], [ExcludeSerialization], [UseGlobalCustomSerializer], [CustomComparer]
using FishNet.Component.Prediction;      // NetworkTrigger, NetworkCollision, OfflineRigidbody
using FishNet.Utility.Template;          // TickNetworkBehaviour
using FishNet.Managing.Logging;          // LoggingType
```

---

## 2. Installation

Pick one of three install routes:
- **Asset Store:** add FishNet (or FishNet Pro), then open the Package Manager, go to My Assets, and choose Download, then Import.
- **.unitypackage:** from GitHub Releases or fish-networking.com, use Assets > Import Package > Custom Package.
- **Git URL (UPM):** `https://github.com/FirstGearGames/FishNet.git?path=Assets/FishNet`. This needs Git 2.14 or later on your PATH. It installs to `Packages/FishNet: Networking Evolved` instead of `Assets/FishNet`.

Import problems:
- If Unity asks whether to update the API automatically, choose **Yes**.
- **Remove Netcode for GameObjects first.** It conflicts with FishNet's ILPostProcessor (`PostProcessorAssemblyResolver` not found).
- The error `FishNet.Codegen.Cecil.dll will not be loaded` means the **API Compatibility Level** in Player Settings is wrong.
- **Updating FishNet:** back up or commit, delete the FishNet folder (leave Unity open), then import the new version. Free and Pro install over each other.
- **Package dependencies:** Newtonsoft JSON 3.2.1, uGUI, and the animation, IMGUI, particle, physics, physics2D and UI modules.

---

## 3. Recipe: converting a single-player project

### 3.1 Decide the authority model first

| Model | Use when | Cost |
|---|---|---|
| **Client-authoritative** (NetworkTransform or NetworkAnimator with *Client Authoritative* on) | Co-op, casual, prototypes, or when cheating doesn't matter | Easy, but trivially cheatable (speed hacks, teleports) |
| **Server-authoritative without prediction** (client sends input by ServerRpc, server moves, NetworkTransform relays) | Turn-based or slow games | Input feels delayed by the full round-trip time |
| **Server-authoritative with client-side prediction** (`[Replicate]` and `[Reconcile]`) | Competitive or fast-paced games | Most code, plus rules about determinism and reconciling (see section 24) |

### 3.2 Minimum scene setup

1. Drag `FishNet/Demos/Prefabs/NetworkManager.prefab` into your first scene. It contains a NetworkManager, an ObserverManager with a **SceneCondition**, a PlayerSpawner, and the **NetworkHudCanvas** (Server and Client buttons, plus an *Auto Start Type* of Host, Server or Client for quick testing).
   - To build it by hand instead: create an empty GameObject, add `NetworkManager`, add `ObserverManager`, and put a **SceneCondition** asset in *Default Conditions*. Every other manager is added automatically at runtime. Add them yourself only when you want to change their settings.
2. Leave **Run In Background** on the NetworkManager enabled.
3. Add a `TimeManager` and set **Physics Mode = TimeManager** if you will use prediction or tick-based physics.

### 3.3 Convert the player

1. Put the root player object in a prefab. Add a **NetworkObject**, which is also added automatically when you add any NetworkBehaviour.
2. Change the player scripts from `MonoBehaviour` to `NetworkBehaviour` and add `using FishNet.Object;`.
3. Guard input and camera code with `if (!IsOwner) return;` in `Update` and `LateUpdate`.
4. Move ownership- or network-dependent initialization out of `Awake` and `Start` into **`OnStartClient`, `OnStartServer` or `OnStartNetwork`**. Ownership is not known yet in `Awake` or `Start`.
5. Add the sync components you need:
   - **Movement:** a `NetworkTransform`. For client-authoritative movement, enable *Client Authoritative*. Set *Component Configuration* to match your mover (CharacterController, Rigidbody, and so on).
   - **Animation:** a `NetworkAnimator` with the *Animator* field set. Call `NetworkAnimator.SetTrigger`, `Play` or `CrossFade` instead of calling the Animator directly.
6. Cameras: keep exactly one live camera for the local player. See section 27.
7. Input System: add a `PlayerInput` to the prefab, **disabled**. Enable it in `OnStartClient` when `IsOwner` is true.
8. Assign the prefab to **PlayerSpawner > Player Prefab** and optionally add spawn-point Transforms to *Spawns*.

### 3.4 Convert the other game objects

| Single-player pattern | FishNet equivalent |
|---|---|
| `Instantiate(prefab)` for a shared object | Server: `Instantiate` then `Spawn(obj, owner)`. Client: ask by `ServerRpc`, or use predicted spawning. |
| `Destroy(go)` for a shared object | Server: `Despawn()`. Use `DespawnType.Pool` for pooling. |
| A field every player must see (health, score, color) | `readonly SyncVar<T>`, written on the server |
| A list or dictionary of shared state | `SyncList<T>` or `SyncDictionary<K,V>`, written on the server |
| A one-shot event (sound, VFX, chat) | `ObserversRpc` (server to all), `TargetRpc` (server to one), `ServerRpc` (client to server) |
| A global manager (game state, player names) | A prefab with a NetworkObject and **Is Global** checked, spawned by `ServerSpawner`, found with `NetworkManager.RegisterInstance` and `GetInstance<T>()` |
| A message with no object (lobby, chat, login) | Broadcast (`IBroadcast` struct) |
| `SceneManager.LoadScene` | `base.SceneManager.LoadGlobalScenes(new SceneLoadData("Name"))` on the server |
| Randomness or a one-time decision | Decide on the server, then sync the result |
| UI that shows network state | A MonoBehaviour that reads `InstanceFinder.*` or subscribes to SyncType `OnChange` |

### 3.5 Test

Test as host in the editor, and add a second client with **Multiplayer Play Mode** (Unity 6) or **ParrelSync**. Use the TransportManager latency simulator to test at 100 to 150 ms with 1 to 3% loss. See section 29.

---

## 4. The NetworkManager and its sub-managers

All sub-managers are optional. They are added at runtime if missing. Add them in the editor to change their settings.

| Component | Key settings (default) | Notes |
|---|---|---|
| **NetworkManager** | Run In Background, Don't Destroy On Load, Persistence (what to do when several NetworkManagers exist), Logging (a `LevelLoggingConfiguration` asset), Spawnable Prefabs (auto `DefaultPrefabObjects`), Object Pool (auto `DefaultObjectPool`), Refresh Default Prefabs | Must not carry a NetworkObject. `RegisterInstance`, `GetInstance`, `TryGetInstance`, `HasInstance` and `UnregisterInstance` act as a per-manager service locator. |
| **ServerManager** | SyncType Rate (**0.1 s**), Spawn Packing, Change Frame Rate, Start On Headless (**true**), Remote Client Timeout, Share Ids (**true**), Authenticator, Allow Predicted Spawning (**false**), Reserved Object Ids (**15**) | `StartConnection()`, `StopConnection(bool)`, `Spawn`, `Despawn`, `Kick`, `Broadcast*`, `Clients`, `Objects.Spawned` |
| **ClientManager** | Remote Server Timeout, Change Frame Rate | `StartConnection([address], [port])`, `StopConnection()`, `Connection` (the local connection), `Clients` (needs Share Ids), `Objects.Spawned`, `Broadcast`, `RegisterBroadcast` |
| **TimeManager** | Update Order (**BeforeTick**), Timing Type (**Tick** or Variable), Allow Tick Dropping and Maximum Frame Ticks, Tick Rate (**30**), Ping Interval, Timing Interval, Physics Mode (**Unity**; set **TimeManager** for CSP) | Tick events and tick-to-time conversions. See section 23. |
| **SceneManager** | Scene Processor, Light Probe Updating, Move Client Host Objects, Set Active Scene | See section 21. |
| **ObserverManager** | Update Host Visibility, Maximum Timed Observers Duration, **Default Conditions** (always include SceneCondition), Level of Detail (Pro) | See section 20. |
| **TransportManager** | Transport (Tugboat when empty), Intermediate Layer, **Latency Simulator** (latency, out-of-order %, packet loss %, simulate host) | `GetTransport<T>()`, `Transport.SetPort()`, `Transport.SetClientAddress()`, `SetMTUReserve()` |
| **PredictionManager** | Create Local States (**true**), State Interpolation (**2**), State Order (**Appended** or Inserted), Drop Excessive Replicates, Maximum Server Replicates, reduce-reconciles-at-low-FPS options | `IsReconciling`, reconcile and replay events |
| **StatisticsManager** | Update Interval, Update Client, Update Server | Needed by `BandwidthDisplay` |
| **DebugManager** | Scene Objects (detailed scene IDs), RPC Lengths validation, disable RPC links | Editor and development builds only. Can break connections between development and release builds. |
| **RollbackManager** (Pro) | Bounding Box Layer, Maximum Rollback Time, Interpolation | Lag compensation |
| **HashGrid** | Grid Axes, Accuracy | Required by GridCondition |

**Utility components:** `PingDisplay`, `BandwidthDisplay`, `DefaultScene` (automatic online and offline scenes), `ServerSpawner` (spawns listed prefabs when the server starts; good for global managers), and `PlayerSpawner`.

**Logging:** create the asset with Create > FishNet > Logging > Level Logging Configuration. It has Development, GUI and Headless levels (Off, Error, Warning, Common) and optional timestamps and local tick.

---

## 5. Transports

| Transport | Use | Included in 4.7.3? |
|---|---|---|
| **Tugboat** (LiteNetLib, UDP) | The default desktop and mobile transport | Yes |
| **Synapse** (SynapseSocket, UDP) | A newer UDP transport (default port 7770, 4095 max clients, IPv4 and IPv6 bind) | Yes (`Transporting/Transports/Synapse`) |
| **Multipass** | The server listens on several transports at once, for example Tugboat and Bayou for WebGL | Yes |
| **Yak** (Pro) | Offline or single-player using the same networking code | Pro (`Plugins/Yak`) |
| Bayou | WebGL (WebSockets) | Separate download |
| FishySteamworks / FishyFacepunch | Steam P2P and relay | Separate download |
| FishyEOS | Epic Online Services | Separate download |
| FishyUnityTransport | Unity Transport and Unity Relay | Separate download |
| FishyRealtime | Photon Realtime relay | Separate download |
| FishyWebRTC / CanoeWebRTC | WebRTC | Separate download |
| Blitz Relay | Self-hosted free relay | Separate download |

**Tugboat settings:** Stop Sockets On Thread, Dont Route, **Reuse Address** (enable on dedicated servers or when running several instances on one machine), IPv4 Bind Address, Enable IPv6 and IPv6 Bind Address, **Port** (**7770** in source), Maximum Clients, and **Client Address**. The client address defaults to `localhost`. Put the server IP there, not in the bind fields.

**Multipass:**

```csharp
Multipass mp = networkManager.TransportManager.GetTransport<Multipass>();
#if UNITY_WEBGL && !UNITY_EDITOR
mp.SetClientTransport<Bayou>();
#else
mp.SetClientTransport<Tugboat>();
#endif
// Server: with GlobalServerActions on, ServerManager.StartConnection() starts every transport.
// Give each transport a different port. A client uses exactly one transport.
```

**IntermediateLayer:** override `HandleIncoming` and `HandleOutgoing` (`ArraySegment<byte>`) to add encryption, compression or headers. Call `SetMTUReserve(n)` to reserve header bytes. There is an example in `Demos/IntermediateLayer`.

---

## 6. Starting and stopping the network

```csharp
using FishNet.Managing;
using UnityEngine;

public class ConnectionStarter : MonoBehaviour   // Not a NetworkBehaviour: it exists before the network starts.
{
    [SerializeField] private NetworkManager _networkManager;

    public void StartHost()   { StartServer(); StartClient(); }
    public void StartServer() => _networkManager.ServerManager.StartConnection();          // or StartConnection(port)
    public void StartClient() => _networkManager.ClientManager.StartConnection();          // or StartConnection(address[, port])
    public void StopServer()  => _networkManager.ServerManager.StopConnection(true);       // true sends disconnect messages
    public void StopClient()  => _networkManager.ClientManager.StopConnection();
    public void SetAddress(string ip) => _networkManager.TransportManager.Transport.SetClientAddress(ip);
    public void SetPort(ushort port)  => _networkManager.TransportManager.Transport.SetPort(port);
}
```

- A headless or dedicated build auto-starts the server when **Start On Headless** is true (the default). If you also start it manually, you get "port already used".
- On mobile and Quest, apps suspend instead of quitting. Call `ClientManager.StopConnection()` in `OnApplicationPause(true)` if you want an immediate disconnect.

---

## 7. NetworkObject

### 7.1 Types

| Type | Meaning | Rules |
|---|---|---|
| **Scene NetworkObject** (`IsSceneObject`) | Placed in a scene at edit time | Disabled until the server or client starts **and** the client observes the scene. It is disabled, not destroyed, on despawn. It **cannot** be global, use DontDestroyOnLoad, or move between scenes. |
| **Spawned (instantiated)** | Instantiated at runtime and spawned by the server | Can move scenes with `SceneLoadData.MovedNetworkObjects`. Can be pooled. |
| **Global** (`IsGlobal`) | An instantiated object placed in DontDestroyOnLoad and known to all clients | Only for instantiated objects. Set it on the prefab or immediately after `Instantiate`. Good for managers. |
| **Nested** (`IsNested`) | A NetworkObject under another NetworkObject | Spawns with the root when active. Can be spawned later if it starts disabled; pass an owner explicitly in that case. **Must not be unparented** at runtime (nested scene objects are the exception). |

### 7.2 Inspector settings

- **Is Networked:** when false, the object runs locally only. `Spawn()` forces it to true.
- **Is Spawnable:** adds the prefab to `DefaultPrefabObjects`. Usually false for scene-only objects.
- **Is Global:** described in 7.1.
- **Initialize Order:** lower values initialize first among objects spawned in the same tick. It affects clients only. Use -128 for managers.
- **Prevent Despawn On Disconnect:** keeps the object when its owner leaves.
- **Default Despawn Type:** Destroy (default) or Pool.
- **Use Level of Detail** (Pro).
- **Prediction:** Enable Prediction, Prediction Type (Rigidbody, Rigidbody2D or Other), **Enable State Forwarding**, **Graphical Object**, Detach Graphical Object, Interpolation, Enable Teleport and Teleport Threshold, and an optional NetworkTransform to auto-configure. See section 24.

Other points:
- Every NetworkObject needs at least one NetworkBehaviour. FishNet adds `EmptyNetworkBehaviour` automatically, and you can delete it once you have your own.
- NetworkObject and NetworkBehaviour components **cannot be added at runtime**.
- **Sending an object reference:** pass the `NetworkObject` or `NetworkBehaviour` itself as an RPC or SyncVar value. It serializes as an ID and resolves on the other side. Don't pass raw ObjectIds.
- **Finding an object by ID:** `ServerManager.Objects.Spawned[id]` on the server, or `ClientManager.Objects.Spawned[id]` on a client.
- **Useful NetworkObject events and fields:** `OnHostVisibilityUpdated`, `RigidbodyPauser`, `PredictedSpawner`, `GetNetworkBehaviour(index)`.

---

## 8. NetworkBehaviour: properties and callbacks

### 8.1 Initialized versus Started

In 4.x the old `IsServer`, `IsClient`, `IsHost`, `IsServerOnly` and `IsClientOnly` are **obsolete**.

| Property | True when |
|---|---|
| `IsServerInitialized` / `IsClientInitialized` | This **object** is network-initialized on that side. Use these in object code. |
| `IsServerStarted` / `IsClientStarted` | The server or client **socket** is started (and the client authenticated). The object may not be initialized yet. |
| `IsHostInitialized` / `IsHostStarted` | Both sides are true |
| `IsServerOnlyInitialized`, `IsClientOnlyInitialized`, `IsServerOnlyStarted`, `IsClientOnlyStarted` | Only one side is true |
| `IsOffline` | Neither server nor client is started |
| `IsOwner` | The local client owns the object. This is a **client-side** check, so it is false in `OnStartServer` even on the host. |
| `IsController` / `HasAuthority` | Owner, or the server when there is no owner |
| `Owner`, `OwnerId`, `ObjectId`, `LocalConnection`, `IsSpawned`, `IsDeinitializing`, `IsManagerReconciling` | As named |
| `NetworkManager`, `ServerManager`, `ClientManager`, `TimeManager`, `SceneManager`, `ObserverManager`, `PredictionManager`, `RollbackManager`, `TransportManager` | Cached manager references |

A `NetworkBehaviour` implicitly converts to `NetworkObject`.

### 8.2 Callback order

Unity's `Awake` and `OnEnable` always run **before** any network activity. In `Awake`, subscribe to SyncType `OnChange` events and fill in SyncType initial values. **Don't** read `IsOwner` or `Owner` there.

Spawn order, with host shown. Server callbacks always come before client callbacks.

```
ReadPayload (if payload)            -> before start callbacks
OnStartNetwork                      -> once, even on host (use Owner.IsLocalClient, not IsOwner)
OnStartServer                       -> Owner, ObjectId, etc. are set; SyncType changes here go out in the spawn message.
                                       Observers are NOT built yet, so a plain ObserversRpc here reaches nobody.
OnOwnershipServer(prevOwner)        -> right after OnStartServer if spawned with an owner, and on every later change
OnSpawnServer(conn)                 -> once per client the object spawns for; good for a TargetRpc to that client
OnStartClient                       -> Owner, ObjectId, and SyncTypes are already synced; buffered RPCs run right after this
OnOwnershipClient(prevOwner)        -> gained or lost ownership
...
OnDespawnServer(conn)               -> before the despawn message goes to conn; RPCs and SyncTypes still work
OnStopServer                        -> too late to message clients about this object
OnStopClient                        -> too late to message the server about this object
OnStopNetwork                       -> once, after both stop callbacks
```

- `WritePayload` and `ReadPayload` let you add extra data to the spawn message. See 10.3.
- You don't need to call `base.OnStartServer()` or the other base methods.
- **SyncType values are reset before `OnDestroy`.** Read them in `OnStopServer` or `OnStopClient` instead.

---

## 9. Running code only on the server or client

```csharp
if (!IsServerInitialized) return;                // Boolean guard (object context)
if (InstanceFinder.IsClientStarted) { ... }     // Guard for code that isn't a NetworkBehaviour

[Server]                                         // Won't run unless the server is active and the object is initialized.
private void ValidateHit() { }

[Server(Logging = LoggingType.Off, UseIsStarted = true)]   // Silent; checks only that the server socket is started.
private void Tick() { }

[Client(RequireOwnership = true, Logging = LoggingType.Off)] // Owner-only client method.
private void PlayLocalVfx() { }
```

- `[Server]` and `[Client]` work in any class, not just NetworkBehaviours. They log a warning when blocked; set `Logging = LoggingType.Off` to silence it.
- Build-level stripping options: `#if UNITY_SERVER` or `#if !UNITY_SERVER`, assembly-definition platform excludes, or **Pro code stripping**. Pro stripping removes the bodies of `[Server]` methods, ServerRpcs and server callbacks from client builds, and client code from server builds.

---

## 10. Spawning and despawning

### 10.1 Basics (server only)

```csharp
[SerializeField] private NetworkObject _bulletPrefab;

[Server]
private void SpawnBullet(Vector3 position, NetworkConnection owner)
{
    NetworkObject nob = NetworkManager.GetPooledInstantiated(_bulletPrefab, position, Quaternion.identity, asServer: true);
    // Set SyncTypes or state BEFORE Spawn; they ride in the spawn message.
    Spawn(nob, owner);                  // Owner is optional (null means no owner). An optional Scene parameter is also available.
}

[Server]
private void Kill() => Despawn();       // or Despawn(DespawnType.Pool)
```

- Spawn entry points: `base.Spawn(...)`, `networkObject.Spawn(...)`, and `ServerManager.Spawn(go or nob, owner, scene)`. The same holds for Despawn.
- Every networked prefab must be in the Spawnable Prefabs collection. `DefaultPrefabObjects` is updated automatically. If the wrong prefabs spawn, use **Tools > Fish-Networking > Utility > Refresh Default Prefabs**.
- A client-initiated spawn is normally a `ServerRpc` that spawns on the server. The other option is predicted spawning (10.4).
- Pending SyncType changes are flushed with the despawn message.
- `DualPrefabObjects` spawns a different prefab on clients than on the server.
- **Addressable prefabs:** register them under a unique ushort collection ID (1 to 65535; never 0) with `NetworkManager.GetPrefabObjects<SinglePrefabObjects>(id, true).AddObjects(list)`. Clients must load the bundle **before** the server spawns from it.

### 10.2 Object pooling

- Set the NetworkObject's **Default Despawn Type = Pool**, or call `Despawn(DespawnType.Pool)`.
- Retrieve from the pool with `NetworkManager.GetPooledInstantiated(prefab, [pos, rot, parent], asServer)` and then `Spawn`.
- Pre-warm with `NetworkManager.CacheObjects(prefab, count, asServer)`.
- For a custom pool, inherit `ObjectPool` and assign it to NetworkManager > Object Pool.
- Scene objects are never pooled; they are just disabled.
- Pooled objects are reused. Reset your own state in `OnStartServer` and `OnStartClient`, or in the stop callbacks.

### 10.3 Spawn payloads

Spawn payloads send data along with the spawn and are read before the start callbacks.

```csharp
public override void WritePayload(NetworkConnection connection, Writer writer)
{
    writer.WriteString(_displayName);
    writer.WriteInt32(_teamId);
}

public override void ReadPayload(NetworkConnection connection, Reader reader)
{
    _displayName = reader.ReadStringAllocated();   // Same order and same count as written.
    _teamId = reader.ReadInt32();
}
// In ReadPayload, connection.IsValid is false when the data came from the server.
// On the server, a valid connection means a predicted spawner sent it.
```

### 10.4 Predicted spawning

Predicted spawning lets a client spawn an object locally and immediately. Setup:
1. Enable **ServerManager > Allow Predicted Spawning** (optionally set Reserved Object Ids).
2. Add a **PredictedSpawn** component to the prefab, with Allow Spawning and Allow Despawning.
3. Inherit `PredictedSpawn` to validate requests on the server.

After setup, the client calls the normal `Spawn` and `Despawn`. Limitations: no nested predicted spawns, and predicted spawns are not aligned with prediction interpolation. The warning "Cannot spawn object because server is not active and predicted spawning is not enabled" means this setup is missing.

---

## 11. Player spawning

### 11.1 Built-in PlayerSpawner (on the NetworkManager prefab)

- Settings: **Player Prefab**, **Add To Default Scene**, and **Spawns** (Transforms, used round-robin).
- It spawns when `SceneManager.OnClientLoadedStartScenes` fires (asServer), using `GetPooledInstantiated` and then `Spawn(nob, conn)`. If *Add To Default Scene* is on, it also calls `AddOwnerToDefaultScene`. It raises `OnSpawned(NetworkObject)`.
- **Add To Default Scene matters.** Without it, or without some other way of adding the client to the scene, the SceneCondition hides every scene object from the client. See section 20.
- The warning "Player prefab is empty and cannot be spawned for connection 0" means Player Prefab is not assigned.

### 11.2 Custom spawner (manual trigger)

Put it on a **scene NetworkObject**, not on the NetworkManager.

```csharp
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

public class ManualPlayerSpawner : NetworkBehaviour
{
    [SerializeField] private NetworkObject _playerPrefab;

    [Server]
    public void SpawnPlayers()
    {
        if (_playerPrefab == null)
        {
            Debug.LogWarning("Player prefab is not assigned.");
            return;
        }

        foreach (NetworkConnection client in ServerManager.Clients.Values)
        {
            if (!client.IsAuthenticated)                      // Clients includes unauthenticated connections.
                continue;
            if (!client.Scenes.Contains(gameObject.scene))    // Make the client an observer of this scene.
                SceneManager.AddConnectionToScene(client, gameObject.scene);

            NetworkObject nob = NetworkManager.GetPooledInstantiated(_playerPrefab, asServer: true);
            Spawn(nob, client, gameObject.scene);
        }
    }
}
```

Other triggers:
- **After a lobby or scene change:** spawn in `SceneManager.OnClientPresenceChangeEnd` (server side, `args.Added`). The client has visibility at that point. Spawning in `OnLoadEnd` can race.
- **Initial data before spawning:** use an Authenticator or broadcasts, then spawn.

**Getting a connection's player:** `conn.FirstObject`, which you can override with `conn.SetFirstObject(nob)`, or `conn.Objects` (all objects the connection owns).

---

## 12. Ownership

```csharp
Spawn(go, ownerConnection);           // Spawn with an owner.
networkObject.GiveOwnership(conn);    // Server only. Replaces the previous owner. Overload: GiveOwnership(conn, includeNested).
networkObject.RemoveOwnership();      // Server only.

bool mine   = base.IsOwner;           // Local client owns it.
NetworkConnection owner = base.Owner; // Owner.IsValid is false when there is no owner.
bool canAct = base.IsController;      // Owner, or the server when there is no owner (same as HasAuthority).
```

- Only the server can assign ownership. Ownership callbacks are `OnOwnershipServer` and `OnOwnershipClient`.
- Checking ownership in `OnStartServer` or `OnStartNetwork` on the host: use `base.Owner.IsLocalClient`, **not** `IsOwner`.
- **PredictedOwner** component: the client calls `TakeOwnership()` and gets ownership immediately (for example, to use a turret without waiting). Override `OnTakeOwnership` on the server to validate. It is controlled by Allow Take Ownership.
- **Share Ids** (ServerManager, on by default) lets clients see other clients' IDs and the owners of objects. Several patterns depend on it.

### 12.1 Pattern: a global per-player data registry

```csharp
public class PlayerNames : NetworkBehaviour        // Prefab with Is Global on and Initialize Order -128, spawned by ServerSpawner.
{
    public event Action<NetworkConnection, string> OnPlayerNameChanged;
    public readonly SyncDictionary<NetworkConnection, string> Names = new();

    private void Awake() => Names.OnChange += Names_OnChange;
    public override void OnStartNetwork() => NetworkManager.RegisterInstance(this);
    public override void OnStopNetwork() { if (NetworkManager != null) NetworkManager.UnregisterInstance<PlayerNames>(); }
    public override void OnStartServer() => ServerManager.OnRemoteConnectionState += ServerManager_OnRemoteConnectionState;
    public override void OnStopServer() => ServerManager.OnRemoteConnectionState -= ServerManager_OnRemoteConnectionState;

    private void ServerManager_OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
    {
        if (args.ConnectionState == RemoteConnectionState.Stopped) Names.Remove(conn);
        else if (args.ConnectionState == RemoteConnectionState.Started) Names.Add(conn, $"Player{conn.ClientId}");
    }

    private void Names_OnChange(SyncDictionaryOperation op, NetworkConnection key, string value, bool asServer)
    {
        if (op == SyncDictionaryOperation.Add || op == SyncDictionaryOperation.Set)
            OnPlayerNameChanged?.Invoke(key, value);
    }

    [ServerRpc(RequireOwnership = false)]
    public void ServerSetName(string newName, NetworkConnection caller = null) => Names[caller] = newName;
}
// Anywhere: NetworkManager.GetInstance<PlayerNames>().Names.TryGetValue(base.Owner, out string name);
```

---

## 13. Choosing a communication method

| Need | Use | Direction | Bound to an object? |
|---|---|---|---|
| Persistent state that late joiners must get | **SyncType** | Server to clients | Yes |
| A one-off event or action | **RPC** | ServerRpc (client to server), ObserversRpc and TargetRpc (server to clients) | Yes (NetworkBehaviour) |
| An RPC-driven value late joiners also need | `ObserversRpc(BufferLast = true)` | Server to clients | Yes |
| Messages with no object (lobby, chat, auth) | **Broadcast** | Either way, through ClientManager or ServerManager | No |
| Continuous transform or animation sync | NetworkTransform / NetworkAnimator | Configurable | Yes |
| Authoritative responsive movement | Prediction (`[Replicate]` / `[Reconcile]`) | Both | Yes |

- **Channels:** `Channel.Reliable` (ordered, guaranteed) or `Channel.Unreliable` (can drop or arrive out of order). Unreliable SyncTypes and NetworkTransform are *eventually consistent*.
- **Ordering:** RPCs are delivered **before** SyncType updates in the same tick, even when the SyncType was set first. Reliable SyncTypes in one script arrive in **reverse declaration order**.

---

## 14. Remote Procedure Calls (RPCs)

RPCs must be in a `NetworkBehaviour`. Method-name prefixes are optional but recommended (`Server*`, `Observers*`, `Target*`, or `Rpc*`).

```csharp
// Client to server. By default only the owner may call it.
[ServerRpc]
private void ServerFire(Vector3 direction) { }

// Any client may call it. The caller is filled in automatically when the LAST parameter is `NetworkConnection x = null`.
[ServerRpc(RequireOwnership = false)]
private void ServerInteract(int itemId, NetworkConnection caller = null) { }

// Server to all observers.
[ObserversRpc(ExcludeOwner = true, BufferLast = true)]
private void ObserversSetState(int state) { }

// Server to one client. The FIRST parameter must be the target connection.
[TargetRpc]
private void TargetSetAmmo(NetworkConnection target, int ammo) { }

// Both at once: a null target means all observers.
[ObserversRpc][TargetRpc]
private void DisplayChat(NetworkConnection target, string sender, string message) { }

// Channel selection: Channel is the last parameter,
// or second to last when followed by the NetworkConnection caller.
[ServerRpc(RequireOwnership = false)]
private void ServerPing(string text, Channel channel = Channel.Reliable, NetworkConnection sender = null) { }
```

| Attribute field | Applies to | Meaning |
|---|---|---|
| `RunLocally` | All | Also run the body on the calling side |
| `DataLength` | All | Maximum expected byte size, so a pre-sized writer is used for large payloads |
| `OrderType` | All | `DataOrderType.Default` or `Last` |
| `RequireOwnership` (true) | ServerRpc | Set false to allow non-owners |
| `ExcludeOwner` / `ExcludeServer` | ObserversRpc | Skip the owner, or skip the server's own client on the host |
| `BufferLast` | ObserversRpc | Resend the last call to clients that join or gain visibility later |
| `ValidateTarget` / `ExcludeServer` | TargetRpc | Target validation, and host exclusion |
| `Logging` | All | Warning level when the RPC is blocked |

Rules:
- **Validate everything in a ServerRpc.** It is untrusted client input.
- A ServerRpc sent before the object is client-initialized produces "Cannot complete action because client is not active".
- An ObserversRpc in `OnStartServer` reaches nobody because observers are not built yet. Use `BufferLast`, or send a `TargetRpc` from `OnSpawnServer`.
- An unreliable RPC falls back to reliable if the transport has no unreliable channel.

---

## 15. SyncTypes

Only the **server** can write SyncTypes that propagate. Only **changes** are sent (deltas), at the SyncType's send rate (default 0.1 s from ServerManager; 0 means every tick).

### 15.1 Declaration rules (enforced by codegen)

```csharp
private readonly SyncVar<float> _health = new(100f);                  // MUST be readonly and initialized.
private readonly SyncVar<int> _score = new(new SyncTypeSettings(1f));  // Send at most once per second.
private readonly SyncVar<Vector3> _aim = new(new SyncTypeSettings(0f, Channel.Unreliable));

// To show it in the inspector: drop readonly and add both attributes.
[AllowMutableSyncType]
[SerializeField]
private SyncVar<int> _level = new();

// Read and write through .Value.
_health.Value -= 10f;
float current = _health.Value;
```

- A NullReferenceException in `SyncBase` when an object spawns means the SyncType has no initializer.
- **Never** reassign a SyncType instance (`_x = new()` at runtime).
- The old `[SyncVar]` and `[SyncObject]` attributes are **obsolete and don't work** in 4.x. Use the `SyncVar<T>` type.
- Changes made in `Awake` are applied on both server and client **without** being synced; use this for identical initial values. `SetInitialValues(value)` works the same way. Changes made in `OnStartServer` are sent in the spawn message.
- Container types need `[System.Serializable]` to show in the inspector.

### 15.2 SyncTypeSettings and permissions

`new SyncTypeSettings(sendRate)`, `(sendRate, channel)`, `(channel)`, `(WritePermission)`, `(ReadPermission)`, `(write, read)` or `(write, read, sendRate, channel)`. Change settings at runtime with `UpdateSettings(...)`, `UpdateSendRate(float)` or `UpdatePermissions(...)`.

- **ReadPermission:** `Observers` (default), `OwnerOnly`, or `ExcludeOwner`.
- **WritePermission:** `ServerOnly` (default), or `ClientUnsynchronized` (a client may set it locally, but the change is not sent).

**The client-side SyncVar pattern:**

```csharp
private readonly SyncVar<string> _name = new(new SyncTypeSettings(WritePermission.ClientUnsynchronized, ReadPermission.ExcludeOwner));

[ServerRpc(RunLocally = true)]
private void ServerSetName(string value) => _name.Value = value;   // The owner sets it locally, the server sets it and syncs it to others.
```

### 15.3 OnChange callbacks

Subscribe in `Awake`. The value is already applied when the callback runs.

```csharp
private void Awake() => _health.OnChange += Health_OnChange;
private void Health_OnChange(float prev, float next, bool asServer)
{
    // HOST LIMITATION: when asServer is false on the host, prev equals next (already overwritten).
    if (asServer || IsClientOnlyStarted)
        ShowDamage(prev - next);
    UpdateHealthBar(next);   // Safe on every side.
}
```

Other SyncVar members: `InterpolatedValue()` and `DirtyAll()`.

### 15.4 Collections

| Type | Callback signature | Operations |
|---|---|---|
| `SyncList<T>` | `(SyncListOperation op, int index, T oldItem, T newItem, bool asServer)` | Add, RemoveAt, Insert, Set, Clear, Complete |
| `SyncDictionary<K,V>` | `(SyncDictionaryOperation op, K key, V value, bool asServer)` | Add, Remove, Set, Clear, Complete |
| `SyncHashSet<T>` | `(SyncHashSetOperation op, T item, bool asServer)` | Add, Remove, Clear, Update, Complete |

- `Complete` fires once after a batch. Refresh UI there instead of once per entry.
- **Mutating an element in place does not sync.** For a class element, change its field and then call `list.Dirty(index)` or `dict.Dirty(key)`. For a struct element, copy it, modify the copy, and write it back (`var s = list[0]; s.Level = 10; list[0] = s;`).
- `SyncList.GetCollection(asServer)` returns the underlying list.

### 15.5 SyncTimer and SyncStopwatch

These send only state changes (start, pause, stop), not every frame.

```csharp
private readonly SyncTimer _roundTimer = new();
// Server:
_roundTimer.StartTimer(60f, sendRemainingOnStop: true);
_roundTimer.PauseTimer(sendRemaining: false);
_roundTimer.UnpauseTimer();
_roundTimer.StopTimer(sendRemaining: false);
// Server and clients: advance it every frame, only once on the host.
private void Update() => _roundTimer.Update();   // or Update(delta)
// Read: _roundTimer.Remaining, _roundTimer.Paused
// OnChange(SyncTimerOperation op, float prev, float next, bool asServer): Start, Pause, PauseUpdated, Unpause, Stop, StopUpdated, Finished, Complete

private readonly SyncStopwatch _elapsed = new();
// StartStopwatch(bool), PauseStopwatch(bool), UnpauseStopwatch(), StopStopwatch(bool), Update(delta), .Elapsed
```

### 15.6 Custom SyncType

For partial updates of a large struct, inherit `SyncBase, ICustomSync` and implement `WriteDelta`, `WriteFull`, `Read`, `ResetState`, and `GetSerializedType()` (return the value type, or null). See `Demos/CustomSyncType/Custom Struct Sync/StructySync.cs` for a complete delta-per-field example. Declare it as `private readonly StructySync _data = new();`.

---

## 16. Broadcasts

Broadcasts are object-independent messages. They must be **structs implementing `IBroadcast`**, and serializers are generated for them.

```csharp
public struct ChatBroadcast : IBroadcast
{
    public string Username;
    public string Message;
}

// Client sends:
InstanceFinder.ClientManager.Broadcast(new ChatBroadcast { Message = text });

// Server receives. Handlers take (conn, msg, channel).
private void OnEnable()  => InstanceFinder.ServerManager.RegisterBroadcast<ChatBroadcast>(OnChatServer);                 // Optional requireAuthentication parameter (default true).
private void OnDisable() => InstanceFinder.ServerManager.UnregisterBroadcast<ChatBroadcast>(OnChatServer);               // ALWAYS unregister.
private void OnChatServer(NetworkConnection conn, ChatBroadcast msg, Channel channel)
{
    msg.Username = LookUpName(conn);
    if (conn.FirstObject != null)
        InstanceFinder.ServerManager.Broadcast(conn.FirstObject, msg, requireAuthenticated: true);   // To that object's observers.
}

// Client receives. Handlers take (msg, channel).
InstanceFinder.ClientManager.RegisterBroadcast<ChatBroadcast>(OnChatClient);
private void OnChatClient(ChatBroadcast msg, Channel channel) { }
```

ServerManager send overloads:
- `Broadcast(conn, msg)`
- `Broadcast(HashSet<NetworkConnection>, msg)`
- `Broadcast(NetworkObject, msg)` (to that object's observers)
- `Broadcast(msg)` (to everyone)
- `BroadcastExcept(...)` variants

Each overload also takes `requireAuthenticated` (default true) and `channel` (default Reliable).

---

## 17. Serialization

- FishNet generates serializers automatically for any type used in an RPC, SyncType, broadcast or replicate/reconcile. That covers **public fields and properties**, inherited fields, arrays, lists, most C# and Unity types, and `NetworkObject` and `NetworkBehaviour` references.
- Exclude a field with `[ExcludeSerialization]`.
- Types that can't be serialized automatically include Sprite and other Unity assets. Send an index or ID into a lookup table instead, or write a custom serializer.

### 17.1 Custom serializers

The rules: a static method in a static class, named `Write*` or `Read*`, whose first parameter is `this Writer` or `this Reader`. Read in the same order you write.

```csharp
public static class EnemySerializers
{
    public static void WriteEnemy(this Writer writer, Enemy value)
    {
        writer.WriteBoolean(value.HasEnergy);
        writer.WriteSingle(value.Health);
        if (value.HasEnergy)
            writer.WriteSingle(value.Energy);
    }

    public static Enemy ReadEnemy(this Reader reader)
    {
        Enemy e = new() { HasEnergy = reader.ReadBoolean(), Health = reader.ReadSingle() };
        if (e.HasEnergy)
            e.Energy = reader.ReadSingle();
        return e;
    }
}

[UseGlobalCustomSerializer]   // Without this, a custom serializer applies only to its own assembly.
public struct Enemy { public bool HasEnergy; public float Health; public float Energy; }
```

- **Inheritance or interfaces:** write a type byte, then `writer.Write(concrete)`. Read the byte, then `reader.Read<Concrete>()`. Check child-most types first. Use an abstract base type (such as `ItemBase`) as the declared parameter, so the reader never calls `Read<SameType>` recursively.
- Writer and Reader helpers include `WriteInt32`, `WriteUInt16`, `WriteString`, `WriteSingle`, `WriteBoolean`, `WriteUInt8Unpacked`, `WriteArraySegmentAndSize`, `Write<T>`, and the matching `Read*` methods (`ReadStringAllocated`, `Read<T>`, ...).
- Pools: `WriterPool.Retrieve()` and `ReaderPool.Retrieve()`, then `.Store()`.

---

## 18. NetworkConnection

`NetworkConnection` represents one client. On the server it is the remote client. On a client, `ClientManager.Connection` or `base.LocalConnection` is the local client.

| Member | Notes |
|---|---|
| `ClientId` | -1 when unset. Incrementing and reused. |
| `IsValid`, `IsActive`, `IsAuthenticated`, `Disconnecting` | State checks |
| `IsHost`, `IsLocalClient` | Host or local checks |
| `FirstObject`, `SetFirstObject(nob)`, `Objects` (HashSet) | The player object, and everything the connection owns |
| `Scenes` | The networked scenes the connection is in |
| `CustomData` (object) | Your own data. **Not synced.** |
| `LoadedStartScenes()` | Whether the connection has loaded the start scenes |
| `Disconnect(bool immediately)`, `Kick(KickReason, ...)`, `GetAddress()` | Actions |
| `PacketTick`, `LocalTick` | `EstimatedTick`s |

Collections: `ServerManager.Clients` (`Dictionary<int, NetworkConnection>`, includes unauthenticated connections) and `ClientManager.Clients` (requires Share Ids).

---

## 19. Network events

| Event | Parameters | Use |
|---|---|---|
| `ServerManager.OnServerConnectionState` | `ServerConnectionStateArgs` (`.ConnectionState`: Started or Stopped) | Server started or stopped. Load scenes and spawn managers here. |
| `ServerManager.OnRemoteConnectionState` | `NetworkConnection, RemoteConnectionStateArgs` | A client connected or disconnected (cleanup) |
| `ServerManager.OnAuthenticationResult` | `NetworkConnection, bool` | A client passed or failed authentication |
| `ServerManager.OnClientKick` | `NetworkConnection, int, KickReason` | Kicks |
| `ClientManager.OnClientConnectionState` | `ClientConnectionStateArgs` | The local client connected or disconnected. It is not in `Clients` yet. |
| `ClientManager.OnAuthenticated` | none | The local client is fully connected and ready |
| `ClientManager.OnRemoteConnectionState` | `RemoteConnectionStateArgs` | Another client joined or left (requires Share Ids) |
| `ClientManager.OnClientTimeOut`, `OnConnectedClients` | | |
| `SceneManager.OnClientLoadedStartScenes` | `NetworkConnection, bool asServer` | The client finished its initial scenes. The standard moment to spawn a player. Guard with `asServer` on the host. |
| `TimeManager.OnPreTick`, `OnTick`, `OnPostTick`, `OnPrePhysicsSimulation(float)`, `OnPostPhysicsSimulation(float)`, `OnUpdate`, `OnLateUpdate`, `OnFixedUpdate`, `OnRoundTripTimeUpdated(long)` | | Tick loop. See section 23. |

Unsubscribe from manager events in the matching stop callback or in `OnDestroy`.

---

## 20. Observers and area of interest

A client that is not an observer of an object doesn't instantiate it, or has the scene object disabled, and receives none of its RPCs or SyncTypes. Conditions are ScriptableObjects. A client observes an object only when it passes **all** active conditions.

| Condition | Behavior |
|---|---|
| **SceneCondition** | The client shares a scene with the object. **Almost always required.** It prevents "SceneId not found" desyncs. |
| DistanceCondition (timed) | Within Maximum Distance of any object the client owns. Hide Distance Percent adds hysteresis. |
| GridCondition (timed) | A cheaper, coarser distance check. Needs a **HashGrid** on the NetworkManager. |
| OwnerOnlyCondition | Only the owner sees it (inventories, hands of cards) |
| HostOnlyCondition | Only the host client sees it |
| MatchCondition | Objects and players tagged with the same match ID see each other. Use it for rooms in one server. |

- **ObserverManager > Default Conditions** apply to every NetworkObject.
- A per-object **NetworkObserver** component can override them. Its *Override Type* can be **Add Missing** (recommended), Use Manager, or Ignore Manager. Be careful not to drop the SceneCondition by accident.

**The number-one "my scene objects are disabled" cause:** the client was never added to the scene. Fix it with one of:
- PlayerSpawner's *Add To Default Scene*
- `SceneManager.AddOwnerToDefaultScene(nob)`
- `SceneManager.AddConnectionToScene(conn, scene)`
- loading the scene through FishNet's SceneManager

A helper for scenes you entered play mode in:

```csharp
public class InitialSceneObserver : MonoBehaviour   // Put it in the scene, NOT on the DontDestroyOnLoad NetworkManager.
{
    private void Start()     { if (InstanceFinder.NetworkManager != null) InstanceFinder.SceneManager.OnClientLoadedStartScenes += OnLoaded; }
    private void OnDestroy() { if (InstanceFinder.NetworkManager != null) InstanceFinder.SceneManager.OnClientLoadedStartScenes -= OnLoaded; }
    private void OnLoaded(NetworkConnection conn, bool asServer)
    {
        if (asServer && !conn.Scenes.Contains(gameObject.scene))
            InstanceFinder.SceneManager.AddConnectionToScene(conn, gameObject.scene);
    }
}
```

**Runtime changes to conditions:**

```csharp
base.NetworkObserver.GetObserverCondition<DistanceCondition>().MaximumDistance = 10f;
base.NetworkObject.NetworkObserver.GetObserverCondition<OwnerOnlyCondition>().SetIsEnabled(false);
ServerManager.Objects.RebuildObservers();   // After moving objects between scenes manually.
```

**Custom condition:**

```csharp
[CreateAssetMenu(menuName = "FishNet/Observers/Team Condition")]
public class TeamCondition : ObserverCondition
{
    public override bool ConditionMet(NetworkConnection connection, bool currentlyAdded, out bool notProcessed)
    {
        notProcessed = false;
        return /* your rule */ true;
    }
    public override ObserverConditionType GetConditionType() => ObserverConditionType.Normal;   // Use Timed for periodic checks.
}
```

**Host visibility:** objects the host client doesn't observe get their renderers **hidden**. Turn this off in ObserverManager or NetworkObserver with *Update Host Visibility*, or react to `NetworkObject.OnHostVisibilityUpdated`.

**Level of Detail (Pro):** enable it in ObserverManager (distance brackets and Maximum Send Interval) and on each NetworkObject with *Use Level of Detail*. It mainly throttles reconciles.

---

## 21. Scene management

**Load networked scenes with FishNet's SceneManager on the server**, never with Unity's SceneManager, or clients never become observers of those scenes.

```csharp
// Global: every current and future client.
SceneLoadData sld = new("Town");
sld.ReplaceScenes = ReplaceOption.OnlineOnly;     // None (default), OnlineOnly (FishNet-managed scenes), or All (every scene)
sld.PreferredActiveScene = new PreferredScene(new SceneLookupData("Town"));
base.SceneManager.LoadGlobalScenes(sld);

// Per connection. New scenes load by NAME only.
base.SceneManager.LoadConnectionScenes(conn, sld);
base.SceneManager.LoadConnectionScenes(new[] { connA, connB }, sld);
base.SceneManager.LoadConnectionScenes(sld);      // Server only (preload).

// Load clients into an EXISTING instance by Scene reference or handle.
base.SceneManager.LoadConnectionScenes(conns, new SceneLoadData(existingScene));   // or new SceneLoadData(existingScene.handle)

// Unload.
base.SceneManager.UnloadGlobalScenes(new SceneUnloadData("Town"));
base.SceneManager.UnloadConnectionScenes(conn, new SceneUnloadData("Dungeon"));
```

### 21.1 SceneLoadData fields

| Field | Default | Meaning |
|---|---|---|
| `SceneLookupDatas` | filled by the constructor | Name (creates a new instance) or handle (an existing instance). A handle wins when it is greater than 0. |
| `MovedNetworkObjects` | empty | Spawned, non-global, non-nested NetworkObjects to move into the **first** loaded scene. Nested objects move their root. |
| `ReplaceScenes` | `None` | Replaced scenes are unloaded first |
| `PreferredActiveScene` | null (the first loaded scene) | A `PreferredScene(client, server)` struct |
| `Params.ServerParams` (object[]) and `Params.ClientParams` (byte[]) | empty | Data visible in scene events. Only ClientParams is networked. |
| `Options.AutomaticallyUnload` | true | Unload connection scenes when they are empty |
| `Options.AllowStacking` | false | Load multiple instances (connection scenes only, loaded by name) |
| `Options.LocalPhysics` | None | `Physics3D` or `Physics2D` for stacked scenes. **You must simulate those physics scenes yourself.** |
| `Options.Addressables` | false | A flag for your custom processor |

`SceneUnloadData.Options.Mode`: `UnloadUnused` (default) or `KeepUnused`. `KeepUnused` gives **scene caching**, where the server keeps an empty scene loaded. It overrides AutomaticallyUnload.

### 21.2 Events, in order

1. `OnQueueStart`
2. `OnLoadStart` (per entry)
3. `OnLoadPercentChange` (`.Percent`)
4. `OnLoadEnd` (`.LoadedScenes`, `.SkippedSceneNames`, `.QueueData.AsServer`)
5. `OnClientPresenceChangeStart` and `OnClientPresenceChangeEnd` (server only; `Scene`, `Connection`, `Added`). **End** means the client can now see the scene's objects. Spawn per-scene players here.
6. Unloading raises `OnUnloadStart` and `OnUnloadEnd` (`.UnloadedScenes`), then `OnQueueEnd`.

Other events: `OnActiveSceneSet`. Queries: `SceneConnections` (`Dictionary<Scene, HashSet<NetworkConnection>>`) and `SceneManager.GetScene(name or handle)`.

### 21.3 Common scene tasks

- **Online and offline scenes with no code:** add a `DefaultScene` component to the NetworkManager. It needs two different scenes. It can Enable Global Scenes, Start In Offline, and Replace Scenes.
- **Loading screen:** inherit `DefaultSceneProcessor`, override `LoadStart` and `LoadEnd` (call `base.` in each), then show or hide your UI. Assign it to SceneManager > Scene Processor.
- **Addressable scenes:** inherit `DefaultSceneProcessor` and override `BeginLoadAsync`, `BeginUnloadAsync`, `IsPercentComplete`, `GetPercentComplete` and `AsyncsIsDone`.
- **Rooms or instances in one server:** stacked scenes plus LocalPhysics plus the SceneCondition (or MatchCondition). Stacked scenes cannot be global.
- **Moving an object manually:** Unity's `SceneManager.MoveGameObjectToScene`, then `ServerManager.Objects.RebuildObservers()` (server only).
- **Host caveat:** unloading a scene for the host client while the server keeps it only removes host visibility. It requires the SceneCondition, and your meshes need NetworkObjects to be hidden.

Physics for stacked scenes, driven by TimeManager:

```csharp
base.TimeManager.OnPrePhysicsSimulation += delta =>
{
    gameObject.scene.GetPhysicsScene().Simulate(delta);
    // Use GetPhysicsScene2D() for 2D.
};
```

---

## 22. NetworkTransform and NetworkAnimator

### 22.1 NetworkTransform

| Setting | Meaning |
|---|---|
| Component Configuration | Auto-configures CharacterController, Rigidbody and similar on non-owners |
| Synchronize Parents | Sync the parent |
| Packing | Packed, Packed Less, or Unpacked, per property |
| Interpolation | Buffer in ticks |
| Extrapolation (Pro) | |
| Enable Teleport / Teleport Threshold | Snap instead of smoothing past a distance |
| **Client Authoritative** | The owner drives it. Otherwise the server moves it. |
| Send Interval | In ticks |
| Synchronize Position, Rotation, Scale + Snapping | Per axis |

- You can place several NetworkTransforms on children. Each syncs its own object.
- A simple networked rigidbody without prediction: NetworkTransform with Component Configuration = Rigidbody, where the server simulates.

### 22.2 NetworkAnimator

| Setting | Meaning |
|---|---|
| Animator | The Animator to sync |
| Synchronize When Disabled | Sync while the Animator is disabled |
| Interpolation | Match the NetworkTransform value to keep them aligned |
| Smooth Floats | Blend float parameters over time |
| **Client Authoritative** | The owner drives it |
| Send To Owner | Shown when server-authoritative |
| Synchronized Parameters (Pro) | Choose which parameters sync |

- Parameter, layer-weight and speed changes are detected automatically.
- For actions, call them on the NetworkAnimator: `Play`, `CrossFade`, `SetTrigger`, `ResetTrigger`, `SetController`, `SetAnimator`.
- It is **not aligned with the prediction tick**. With CSP it may lead the predicted state slightly.

---

## 23. Time and ticks

- **Tick Rate** is 30 by default. `TimeManager.TickDelta` is a double, the seconds per tick. `Tick` is the server-estimated tick; `LocalTick` is local.
- Each frame: `OnPreTick` (before data is read), `OnTick` (run gameplay and replicate here), physics (when Physics Mode = TimeManager, surrounded by `OnPre/PostPhysicsSimulation`), then `OnPostTick` (send the reconcile here after physics).
- Timing Type **Tick** processes data only on frames that tick. **Variable** sends and reads every frame.
- Helpers:
  - `TimeToTicks(seconds, TickRounding.RoundUp)` and `TicksToTime(ticks or TickType)`
  - `TimePassed(previousTick, allowNegative: false)`
  - `TickToLocalTick(serverTick)` and `LocalTickToTick(localTick)`
  - `GetPreciseTick(TickType.LastPacketTick)`
  - `RoundTripTime`, `HalfRoundTripTime` (ms), `ServerUptime`, `ClientUptime`, `TimeManager.UNSET_TICK` (0)
  - `SetTickRate`, `SetPhysicsMode`, `SetPhysicsTimeScale`

**TickNetworkBehaviour** (`FishNet.Utility.Template`) subscribes to the tick events for you:

```csharp
public class Mover : TickNetworkBehaviour
{
    public override void OnStartNetwork() => SetTickCallbacks(TickCallback.Tick | TickCallback.PostTick);
    protected override void TimeManager_OnTick() { }
    protected override void TimeManager_OnPostTick() { }
    // Also available: TimeManager_OnPreTick, TimeManager_OnUpdate, TimeManager_OnLateUpdate.
}
```

**Server-decided timed state** (EMP, stun): send a *future* tick so everyone applies it on the same tick.

```csharp
uint start = TimeManager.LocalTick + TimeManager.TimeToTicks(0.05f, TickRounding.RoundUp);
ObserversSetStunTick(start);   // [ObserversRpc(RunLocally = true)]

// Receiver: _stunStart = TimeManager.TickToLocalTick(serverTick);
// The replicate method checks LocalTick against the range. Don't reset the fields; replays need them.
```

---

## 24. Client-side prediction (CSP)

CSP lets the owner move instantly while the server stays authoritative. The server runs the same inputs, sends back state, and the client **reconciles** (rewinds to the server's state) and **replays** the inputs it has buffered.

### 24.1 Setup checklist

1. **TimeManager > Physics Mode = TimeManager**, which is mandatory. Pick a tick rate; higher is smoother but costs more.
2. **NetworkObject:**
   - Enable Prediction, and set Prediction Type to Rigidbody, Rigidbody2D, or Other (CharacterController or transform).
   - **Enable State Forwarding** (recommended): all clients run the same inputs. When it is off, only the owner and server predict, and spectators need a NetworkTransform, RPCs, or a NetworkAnimator.
   - **Graphical Object:** a child that holds only visuals. It is smoothed between ticks. **Colliders, triggers and movers must NOT be under it**; keep them on the root.
   - Optionally Detach Graphical Object (helps cameras and complex rigs), Interpolation, and Teleport.
3. **PredictionManager (optional):** State Interpolation (default 2; use 2 to 3 for casual games, 1 for fast games), State Order (Appended by default, or Inserted), Create Local States, Drop Excessive Replicates. Use **NetworkTickSmoother** components for per-graphic smoothing (owner versus spectator interpolation, adaptive or flat).
4. **Every non-networked rigidbody** the player can touch needs an **OfflineRigidbody**, or it will be over-simulated during replays.
5. Use **NetworkTrigger / NetworkCollision** (and the 2D versions) instead of `OnTriggerEnter` and `OnCollisionEnter` for gameplay that affects prediction. They support primitive colliders only.

### 24.2 Data structures

```csharp
public struct ReplicateData : IReplicateData
{
    public Vector2 Move;
    public bool Jump;
    public ReplicateData(Vector2 move, bool jump) : this() { Move = move; Jump = jump; }
    private uint _tick;
    public void Dispose() { }                      // Release pooled or allocated members here.
    public uint GetTick() => _tick;
    public void SetTick(uint value) => _tick = value;
}

public struct ReconcileData : IReconcileData
{
    public Vector3 Position;
    public float VerticalVelocity;
    public float Stamina;                          // EVERY value that affects the simulation must be here.
    public ReconcileData(Vector3 position, float verticalVelocity, float stamina) : this()
    { Position = position; VerticalVelocity = verticalVelocity; Stamina = stamina; }
    private uint _tick;
    public void Dispose() { }
    public uint GetTick() => _tick;
    public void SetTick(uint value) => _tick = value;
}
```

Arrays or generics inside prediction data need a `[CustomComparer] public static bool Compare(T a, T b)` method.

### 24.3 Template: CharacterController (non-physics)

This follows the 4.7.3 demo `Demos/Prediction/CharacterController`.

```csharp
using FishNet.Object.Prediction;
using FishNet.Transporting;
using FishNet.Utility.Template;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PredictedCharacter : TickNetworkBehaviour
{
    [SerializeField] private float _moveRate = 4f;
    [SerializeField] private float _jumpForce = 12f;

    private CharacterController _characterController;
    private float _verticalVelocity;
    private bool _jumpQueued;                              // A one-frame input gathered in Update.

    private void Awake()
    {
        _characterController = GetComponent<CharacterController>();
        SetTickCallbacks(TickCallback.Tick | TickCallback.PostTick);
    }

    private void Update()
    {
        if (IsOwner && Input.GetKeyDown(KeyCode.Space))
            _jumpQueued = true;
    }

    protected override void TimeManager_OnTick() => PerformReplicate(BuildReplicateData());
    protected override void TimeManager_OnPostTick() => CreateReconcile();   // Reconciling in OnTick is fine when no triggers are involved.

    private ReplicateData BuildReplicateData()
    {
        if (!IsOwner)                                      // Use HasAuthority to let the server drive unowned AI.
            return default;
        ReplicateData data = new(new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), _jumpQueued);
        _jumpQueued = false;
        return data;
    }

    public override void CreateReconcile()                 // Call it on EVERY side; only the server actually sends.
    {
        PerformReconcile(new ReconcileData(transform.position, _verticalVelocity, 0f));
    }

    [Replicate]
    private void PerformReplicate(ReplicateData data, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
    {
        float delta = (float)TimeManager.TickDelta;        // ALWAYS use TickDelta, never Time.deltaTime.

        // Optional: stop spectated objects from moving into the unknown future (prevents jump snapping).
        if (state.IsFuture())
            return;

        if (data.Jump && _characterController.isGrounded)
            _verticalVelocity = _jumpForce;
        _verticalVelocity += Physics.gravity.y * delta;

        Vector3 move = new Vector3(data.Move.x, 0f, data.Move.y).normalized * _moveRate;
        move.y = _verticalVelocity;
        _characterController.Move(move * delta);

        // One-shot effects: only when first ticked, never on a replay.
        if (data.Jump && state.ContainsTicked() && !state.ContainsReplayed())
            PlayJumpSound();
    }

    [Reconcile]
    private void PerformReconcile(ReconcileData data, Channel channel = Channel.Unreliable)
    {
        _verticalVelocity = data.VerticalVelocity;
        _characterController.enabled = false;              // REQUIRED, or the CharacterController keeps its old internal position.
        transform.position = data.Position;
        _characterController.enabled = true;
    }

    private void PlayJumpSound() { }
}
```

### 24.4 Template: Rigidbody with PredictionRigidbody

This follows `Demos/Prediction/Rigidbody/RigidbodyPrediction.cs`. The Prediction Type is Rigidbody.

```csharp
private readonly PredictionRigidbody _predictionRigidbody = new();

private void Awake() => _predictionRigidbody.Initialize(GetComponent<Rigidbody>());
// Or use pooling: PredictionRigidbody pr = ObjectCaches<PredictionRigidbody>.Retrieve(); ... ObjectCaches<PredictionRigidbody>.StoreAndDefault(ref pr) in OnDestroy.

protected override void TimeManager_OnTick() => PerformReplicate(BuildReplicateData());
protected override void TimeManager_OnPostTick() => CreateReconcile();   // AFTER physics.

public override void CreateReconcile() => PerformReconcile(new RbReconcileData(_predictionRigidbody));   // The struct holds `public PredictionRigidbody Root;`.

[Replicate]
private void PerformReplicate(ReplicateData data, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
{
    Vector3 force = new Vector3(data.Move.x, 0f, data.Move.y) * _moveForce;   // Forces do NOT multiply by delta (same as Unity physics).
    _predictionRigidbody.AddForce(force);
    if (data.Jump)
        _predictionRigidbody.AddForce(Vector3.up * _jumpForce, ForceMode.Impulse);
    _predictionRigidbody.Simulate();                       // Call once, at the END of the replicate.
}

[Reconcile]
private void PerformReconcile(RbReconcileData data, Channel channel = Channel.Unreliable) => _predictionRigidbody.Reconcile(data.Root);
```

PredictionRigidbody rules:
- **Never touch `Rigidbody` velocity or forces directly** on a predicted body, including from other scripts. Use the PredictionRigidbody API: `AddForce`, `AddRelativeForce`, `AddTorque`, `AddRelativeTorque`, `AddExplosiveForce`, `AddForceAtPosition`, `Velocity(v)`, `AngularVelocity(v)`, `MovePosition`, `MoveRotation`, `ClearVelocities`, `ClearPendingForces`.
- Outside scripts, such as a bumper's NetworkTrigger, may call `AddForce` on another object's PredictionRigidbody. They must **never** call `Simulate()`; only the owning replicate does.
- For multiple rigidbodies, give each its own PredictionRigidbody and include each in the reconcile. PredictionRigidbodies in reconcile data are pooled automatically, so there is no need to dispose them.
- To stop future-state motion on a spectated rigidbody: when `state.IsFuture()`, call `Velocity(Vector3.zero)` and `AngularVelocity(Vector3.zero)` and return. The alternative is `NetworkObject.RigidbodyPauser.Pause()` and `Unpause()`, but pausing makes the body kinematic, so other objects can pass through it.

A **non-controlled predicted rigidbody** (a crate or ball) uses the same structs with an empty ReplicateData. In `OnPostTick`, call `PerformReplicate(default); CreateReconcile();`.

### 24.5 ReplicateState (flags)

| Flag | Meaning |
|---|---|
| `Ticked` | Ran outside a reconcile, from `OnTick`. Server and client. |
| `Replayed` | Running during a reconcile replay. Client only; the server never replays. |
| `Created` | The controller actually created this data. Otherwise the data is `default` (no input known or sent). |

| Extension | Meaning |
|---|---|
| `ContainsTicked()`, `ContainsReplayed()`, `ContainsCreated()` | Flag tests |
| `IsTickedCreated()` | Normal current input |
| `IsReplayedCreated()` | A known input being replayed |
| **`IsFuture()`** | `Replayed` only: input for this tick is unknown. It happens on spectated objects. |
| `IsTickedNonCreated()` | Ticked, but the controller sent nothing |

Common uses:
- **One-shot VFX or audio:** `ContainsTicked() && !ContainsReplayed()`.
- **Animator updates:** only when `ContainsCreated()`, to avoid flicker between real input and defaults.
- **Predicting future input on spectators:** in a future state, reuse the last created input for 1 or 2 ticks, but don't repeat one-time inputs like jump. With the Inserted state order, `Created` appears on spectated objects only during reconciles.
- Non-owned objects receive some `Created` states that hold default values. That is the PredictionManager redundancy feature at work, and it is normal.
- You never reach a future state on objects you control.

### 24.6 The golden rules of prediction

1. **Every value that affects the simulation** (stamina, cooldowns, boost ticks, current platform, vertical velocity) must be **changed inside the replicate** and **restored in the reconcile**. Otherwise replays diverge, which shows up as jitter.
2. Validate inside the replicate, not when gathering input. The replicate is where server authority lives (stamina checks, ground checks).
3. Use `TimeManager.TickDelta` for time. For durations, store a **tick** (for example `_boostStartTick`) and compare it to `data.GetTick()`, which is replay-safe.
4. Gather one-frame inputs (`GetKeyDown`) in `Update` into a flag, consume the flag in `BuildReplicateData`, and reset it.
5. Call `CreateReconcile()` on every side. The client keeps its own copy as a fallback for lost packets.
6. For moving platforms, include the parent reference in the reconcile, set the parent **before** the local position, and use local position.
7. You may reconcile less often (for example, send only every Nth tick on the server) to save bandwidth. Keep building the reconcile on clients anyway.
8. Collider callbacks fire again on replays. Guard one-shot effects with `!PredictionManager.IsReconciling`.

### 24.7 NetworkCollider components

```csharp
private void Awake()
{
    NetworkTrigger trigger = GetComponent<NetworkTrigger>();
    trigger.OnEnter += Trigger_OnEnter;     // 4.7.3 signature: Action<Collider, uint tick>. The docs omit the tick parameter.
    trigger.OnExit  += Trigger_OnExit;      // Components on the same object need no unsubscribe.
}
private void Trigger_OnEnter(Collider other, uint tick)
{
    if (other.transform.root.TryGetComponent(out PredictedCar car))
        car.BoostHit();                     // Sets state that the replicate reads and the reconcile restores.
}
private void Trigger_OnExit(Collider other, uint tick) { }
```

Settings: Maximum Simultaneous Hits (16), Additional Size, Layers, and History Duration on the 2D versions.

---

## 25. Lag compensation

### 25.1 Projectiles (free)

Projectiles are local, non-networked GameObjects that are fast-forwarded by the time that has passed.
1. The client spawns the projectile locally with a passed time of 0, then calls `ServerFire(pos, dir, TimeManager.Tick)`.
2. The server computes `passedTime = (float)TimeManager.TimePassed(tick, false)`, caps it at `MAX_PASSED_TIME / 2` (for example 0.15 s), spawns the projectile locally, and calls `[ObserversRpc(ExcludeOwner = true)] ObserversFire(...)`.
3. Observers compute the passed time the same way, cap it at `MAX_PASSED_TIME`, and spawn.
4. Each frame, the projectile consumes about 8% of the remaining passed time as extra delta until it catches up.
5. On collision, clients play effects (`IsClientStarted`) and the server applies damage (`IsServerStarted`).

This avoids networking the projectile's movement entirely.

### 25.2 Raycast rollback (Pro)

- Put hitbox colliders on child transforms and list them under **ColliderRollback > Collider Parents**. Add a **RollbackManager** to the NetworkManager.
- With tick smoothers, put the hitboxes on the *target*, not on the smoothed graphic, and set the smoother's flat Interpolation to match RollbackManager's.

```csharp
[Client]
private void Fire() => ServerFire(TimeManager.GetPreciseTick(TickType.LastPacketTick));

[ServerRpc]
private void ServerFire(PreciseTick pt)
{
    RollbackManager.Rollback(pt, RollbackPhysicsType.Physics, IsOwner);   // In 4.x the parameter type is RollbackPhysicsType.
    if (Physics.Raycast(transform.position, transform.forward, out RaycastHit hit)) { /* apply damage */ }
    RollbackManager.Return();
}
```

---

## 26. Authentication

Assign an Authenticator in **ServerManager > Authenticator**. When it is empty, anyone can join. To write one, inherit `Authenticator`:
- Implement `public override event Action<NetworkConnection, bool> OnAuthenticationResult;`.
- Override `InitializeOnce(NetworkManager)`.
- Optionally override `OnRemoteConnection`.

The pattern, from `Demos/Authenticator/Scripts/PasswordAuthenticator.cs`:
1. In `InitializeOnce`, subscribe to `ClientManager.OnClientConnectionState`. Register the server broadcast handler with **`requireAuthentication: false`**. Register the client response handler.
2. When the client reaches `Started`, send a credentials broadcast.
3. On the server, if `conn.IsAuthenticated` is already true, disconnect the connection (possible attack). Otherwise validate, send a response broadcast, **then** invoke `OnAuthenticationResult(conn, passed)`. A failure kicks the client.
4. `HostAuthenticator` shows how to let the host bypass authentication with a random server-side hash.

**Never send plaintext passwords without encryption**, for example through an IntermediateLayer or a secure transport. Authenticators are also the place to send initial data before any object spawns.

---

## 27. Cameras and input

- **Exactly one active camera for the local player.** A camera left on every player prefab causes "I can see my player but can't control it", because you are looking through someone else's camera.
- Three patterns, all in `OnStartClient` or `OnOwnershipClient`:
  1. **Instantiate a camera prefab** when `IsOwner` is true, under a `_cameraHolder`.
  2. **Claim the scene camera:** `Camera.main.transform.SetParent(_cameraHolder)` when `IsOwner` is true. Using `OnOwnershipClient` also handles ownership that arrives later.
  3. **Cinemachine:** a CinemachineBrain on the main camera, and a `CinemachineCamera` on the player that is `enabled = IsOwner`. Alternatively, instantiate a follow camera for the owner.
- **Input System:** keep `PlayerInput` disabled on the prefab and enable it only for the owner in `OnStartClient`.
- With prediction, a camera that follows the graphical object can jitter. Use Detach Graphical Object, or a NetworkTickSmoother with *Detach On Start* on the camera target.

---

## 28. Dedicated server builds

1. Install the **Dedicated Server Build Support** module (usually Linux) and use **File > Build Profiles > Linux Server** or **Windows Server**.
2. The server starts automatically (ServerManager **Start On Headless**). To set the port or address first, disable that option and start it yourself:

```csharp
[RequireComponent(typeof(NetworkManager))]
public class ServerBootstrap : MonoBehaviour
{
    private void Start()
    {
        NetworkManager networkManager = GetComponent<NetworkManager>();
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-port" && ushort.TryParse(args[i + 1], out ushort port))
                networkManager.TransportManager.Transport.SetPort(port);
        }
#if UNITY_SERVER
        networkManager.ServerManager.StartConnection();
#endif
    }
}
```

3. Enable Tugboat **Reuse Address**. Set Port and Bind Address.
4. Create a Level Logging Configuration with **Headless Logging = Common** so connection logs show, optionally with timestamps and tick.
5. Enable Player Settings > Other > **Dedicated Server Optimizations** to strip shaders and fonts.
6. Exclude client-only code with `#if !UNITY_SERVER` or asmdef platform excludes, or with Pro code stripping. Exclude assets with tools such as Exclude From Build or BuildExcluder.
7. The NetworkManager must exist in the server's start scene, a scene loaded from code, or be instantiated at runtime.
8. **Hosting options:** PlayFlow Cloud, AWS, Edgegap, or your own machine. For internet play, open the firewall and forward the port. A virtual LAN is easier for quick tests.
9. Consider a server frame-rate cap (ServerManager > Change Frame Rate).

---

## 29. Testing and debugging

- **Multiple editors:**
  - **Multiplayer Play Mode** (`com.unity.multiplayer.playmode`, Unity 6+, up to 4 clones; Window > Multiplayer > Multiplayer Play Mode).
  - **ParrelSync** (`https://github.com/VeriorPies/ParrelSync.git?path=/ParrelSync`). Edit only in the main editor.
  - A second instance that shows "Server failed to start" is harmless; the port is taken, so it runs as a client. Turn off **Error Pause** in the console.
- **Bad networks:** turn on **TransportManager > Latency Simulator**. Test at 100 to 150 ms with 1 to 3% loss (desktop) and 100 to 300 ms with 2 to 5% loss plus jitter (mobile). Stress test at 500 to 1000 ms or more. For OS-level testing use Clumsy (Windows), Network Link Conditioner (macOS), or netem (Linux).
- **Diagnostics:**
  - `PingDisplay` and `BandwidthDisplay` (needs the StatisticsManager).
  - The DebugManager's **Scene Objects** option names missing scene IDs, and **RPC Lengths** catches serializer read and write mismatches.
  - A LevelLoggingConfiguration with Add Local Tick.
  - The built-in Network Profiler editor window (`Runtime/Editor/NetworkProfiler`).
- **Editor tools (Tools > Fish-Networking):**
  - Utility: **Refresh Default Prefabs**, **Reserialize NetworkObjects** (fixes scene-ID errors; save the scenes afterward), Upgrading From Mirror.
  - Configuration: code stripping (Pro), the NetworkBehaviour template (Assets > FishNet > NetworkBehaviour Script), and the Prefab Objects Generator (search scope, Spawnable Only, Full Rebuild).

---

## 30. Pro features and beta toggles

**Pro** (Asset Store, GitHub Sponsors or Patreon; installs over the free version):
- Automatic **code stripping**, with Redirect or Empty_Experimental modes. Recompile after changing it, and verify the result with ILSpy.
- **Collider rollback** (raycast lag compensation).
- **Level of Detail**.
- NetworkTransform **extrapolation**.
- NetworkAnimator **synchronized-parameter filtering**.
- The **Yak** offline transport.

**Beta toggles** (Tools > Fish-Networking > Beta) in 4.7.3:
- Animator Channel
- Recursive Despawns
- Threaded TickSmoothers
- Threaded Collider Rollback

Treat these as opt-in and test before shipping.

---

## 31. Migrating from Mirror

Use **Tools > Fish-Networking > Utility > Upgrading From Mirror > Replace Components**. Both frameworks must be installed while it runs, and it needs the `MIRROR` scripting define. Remove the defines afterward. It replaces NetworkIdentity, NetworkTransform, NetworkAnimator, NetworkProximityChecker and NetworkSceneChecker, but **not their settings**. Delete `[RequireComponent(typeof(NetworkIdentity))]` from your scripts first.

| Mirror | FishNet |
|---|---|
| NetworkIdentity | NetworkObject |
| Interest management, proximity and scene checkers | ObserverManager and NetworkObserver conditions |
| `hasAuthority` / `isLocalPlayer` | `IsOwner` (for the local player, use `OnStartClient` with an `IsOwner` check) |
| `isServer` / `isClient` / `isServerOnly` / `isClientOnly` | `IsServerInitialized` / `IsClientInitialized` / `IsServerOnlyInitialized` / `IsClientOnlyInitialized` |
| `connectionToClient` / `connectionToServer` | `Owner` / `LocalConnection` |
| `netId` | `ObjectId` |
| `[Command]` / `[ClientRpc]` / `[TargetRpc]` | `[ServerRpc]` / `[ObserversRpc]` / `[TargetRpc]` (first parameter is a NetworkConnection) |
| `[SyncVar]` field | `readonly SyncVar<T>` accessed through `.Value` |
| NetworkServer / NetworkClient | ServerManager / ClientManager |
| NetworkManager overrides (`OnServerConnect` and similar) | Subscribe to manager events, such as `ServerManager.OnRemoteConnectionState` |
| `OnServerReady` | `SceneManager.OnClientLoadedStartScenes` |
| `OnStartServer`/`OnStopServer` or `OnStartClient`/`OnStopClient` on the manager | `ServerManager.OnServerConnectionState` / `ClientManager.OnClientConnectionState` |
| Server and client scene-change callbacks | `SceneManager.OnLoadEnd` / `OnClientPresenceChangeEnd` |
| Online and offline scenes | The `DefaultScene` component |
| Singletons | `InstanceFinder`, or `NetworkManager.RegisterInstance` and `GetInstance` |

---

## 32. Differences between the docs and the 4.7.3 source

| Topic | Docs say | 4.7.3 source |
|---|---|---|
| Tugboat default port | 7777 | **7770** (`Tugboat.cs`; Synapse also uses 7770) |
| NetworkCollider event signature | `OnEnter(Collider other)` | **`Action<Collider, uint tick>`**, and `Collider2D, uint` for 2D |
| PredictionRigidbody | `AddVelocity(...)` in the examples | There is **no `AddVelocity`**. Use `Velocity(v)`, or `AddForce(v, ForceMode.VelocityChange)`. |
| PredictionManager state order | Tooltip says Future and Past | Enum values are **Appended** (default) and **Inserted** |
| Rollback call | `RollbackManager.PhysicsType.Physics` | Changed in 4.1 to `RollbackPhysicsType` |
| Predicting-states page | `ReplicateState.ReplayedCreated` enum value | These are now flag checks: `state.IsReplayedCreated()`, `IsFuture()` and similar |
| Obsolete `IsServer`, `IsClient`, ... | Still appear in some examples | Obsolete. Use the `*Initialized` or `*Started` variants. |
| Broadcast handlers | Some examples omit `Channel` | Handlers **require** a trailing `Channel` parameter |
| Character controller demo readme | "Beta ReplicateStates must be enabled" | 4.7.3's Beta menu has no such toggle; it is standard now |
| Transports | Synapse isn't listed | **Synapse** ships in `Transporting/Transports/Synapse` |

API renames from the 4.1 "Break Solutions" page that you may meet in old tutorials:
- `ServerManager.Authenticator` became `GetAuthenticator()` and `SetAuthenticator()`.
- `TimeManager.LastPacketTick` is now an `EstimatedTick` (use `.LastRemoteTick`).
- `ReaderPool.GetReader` became `Retrieve`, and `Recycle` became `Store`.
- `ObserversRpc(IncludeOwner)` became `ExcludeOwner`, with the value inverted.
- `ReadDictionary` became `ReadDictionaryAllocated`.
- `ListCache` became `CollectionCaches` or `ObjectCaches`.
- `NetworkConnection.Tick` became `LocalTick`.
- The replicate and reconcile `Resends` parameter was replaced by `PredictionManager.RedundancyCount`.

---

## 33. Troubleshooting and FAQ

| Symptom | Cause and fix |
|---|---|
| Scene objects are disabled on the client | The client is not in the scene (SceneCondition). Use PlayerSpawner *Add To Default Scene*, `AddConnectionToScene`, or load the scene with FishNet's SceneManager. |
| `SceneId of N not found in SceneObjects` | The server thinks the client is in a scene the client hasn't loaded. Add the SceneCondition, run **Reserialize NetworkObjects** on the scene and save, and turn on DebugManager > Scene Objects for names. |
| The client can't see a spawned object | Not an observer. Check the conditions, and Owner or Scene membership. |
| Renderers are hidden on the host | This is host visibility for unobserved objects. Toggle Update Host Visibility. |
| SyncVar changed on a client doesn't sync | Only the server writes. Send a `ServerRpc`, or use `ClientUnsynchronized` plus a `RunLocally` ServerRpc. |
| RPCs arrive before SyncType updates | By design: SyncTypes send after RPCs, at their send rate. Change the per-SyncType settings, or send the value in the RPC. |
| The OnChange `prev` value is wrong on the host | The host limitation. Use `if (asServer \|\| IsClientOnlyStarted)`. |
| `NullReferenceException in SyncBase` | The SyncType was not initialized. It must be `readonly` and `= new()`. |
| "Cannot complete action because client/server is not active" | Called before initialization, or a `[Client]`/`[Server]` method ran on the wrong side. Set `Logging = LoggingType.Off` if that is intended. |
| "Cannot spawn object because server is not active and predicted spawning is not enabled" | A client tried to spawn. Use a ServerRpc, or enable predicted spawning plus the PredictedSpawn component. |
| `FishNet.Codegen.Cecil.dll will not be loaded` | Wrong API compatibility level |
| `PostProcessorAssemblyResolver` not found | Netcode for GameObjects is installed. Remove it and reimport FishNet. |
| `System.ParamArrayAttribute::.ctor() is declared in another module` | A Unity cache bug. Add an empty method with a parameter, save, and remove it later. |
| `AssetPathHash is not set` | Restart Unity. Check the prefab for missing scripts or saving problems. |
| "Port already used" | The server was started twice (auto-start plus manual). Enable Tugboat Reuse Address. |
| LAN clients can't connect | Firewall, a wrong *Client Address* (not a bind address), port forwarding, or mismatched builds. Network discovery add-ons: Fish-Networking-Discovery. |
| "Player prefab is empty" | Assign PlayerSpawner > Player Prefab. |
| I see my player but can't control it | A camera is left on every player prefab. Enable the camera only for the owner. |
| The wrong prefab spawns on clients | Refresh Default Prefabs, and make sure server and client builds match. |
| Predicted object jitters | A simulation value isn't reconciled, `Time.deltaTime` is used, a collider is under the graphical object, Physics Mode isn't TimeManager, or the Rigidbody is modified directly. |
| A CharacterController snaps back after a reconcile | Disable and re-enable the controller around the position set. |
| Mobile or Quest players don't disconnect | The app suspends instead of quitting. Disconnect in `OnApplicationPause`. |
| How many players can I have? | There is no framework CCU limit. Games have run 500+ CCU with thousands of objects. |
| Rooms or matches | Use separate server instances (PlayFlow and similar) or stacked scenes with LocalPhysics and Scene or Match conditions. |
| Host migration | Not built in. |

---

## 34. Pitfalls checklist

- [ ] There is no NetworkObject on, above, or below the NetworkManager.
- [ ] ObserverManager has a **SceneCondition**, and clients are added to scenes.
- [ ] Every networked prefab has a NetworkObject and is spawned **by the server** (or predicted-spawned).
- [ ] Ownership and network initialization happen in `OnStart*` callbacks, not `Awake` or `Start`.
- [ ] `IsOwner` guards all input, camera and UI code for players.
- [ ] One camera, enabled for the owner only. `PlayerInput` is enabled for the owner only.
- [ ] SyncTypes are `readonly` and initialized, written only on the server, and accessed through `.Value`.
- [ ] Changed fields inside collection elements are marked with `Dirty(index or key)` or written back.
- [ ] Every ServerRpc validates its input. A NetworkConnection caller parameter is last; a Channel parameter comes just before it.
- [ ] TargetRpc's first parameter is a NetworkConnection.
- [ ] Broadcasts are structs with `IBroadcast`, handlers take `Channel`, and handlers are unregistered in `OnDisable`.
- [ ] Networked scenes are loaded with FishNet's SceneManager on the server.
- [ ] Prediction: Physics Mode is TimeManager, TickDelta is used, all state is reconciled, the graphical object holds visuals only, PredictionRigidbody is used, NetworkTrigger replaces OnTrigger, and OfflineRigidbody is on unnetworked bodies.
- [ ] CharacterController is disabled and re-enabled around the reconcile position set.
- [ ] Tested as host, as a separate client, and with a separate client under simulated latency and packet loss.
- [ ] Dedicated server: Start On Headless (or a manual start, not both), Reuse Address, headless logging, and stripped assets and code.

---

## 35. Where to look in the FishNet folder

| Path | Contents |
|---|---|
| `Demos/Prefabs/NetworkManager.prefab` | A ready NetworkManager with the HUD, ObserverManager (SceneCondition) and PlayerSpawner |
| `Demos/Scripts/NetworkHudCanvases.cs` | The HUD, including its AutoStartType |
| `Demos/Authenticator/Scripts/` | `PasswordAuthenticator`, `HostAuthenticator`, and broadcast structs |
| `Demos/Prediction/CharacterController/` | CSP with a CharacterController, a stamina system, a moving platform (predicted into the future), NetworkTrigger parenting, and future-input prediction |
| `Demos/Prediction/Rigidbody/` | CSP vehicle with three PredictionRigidbodies, a boost pad (tick-based), a spring, and NetworkTrigger |
| `Demos/CustomSyncType/` | A custom SyncType (`StructySync`) and component state sync |
| `Demos/SceneManager/Additive Scenes/` | Per-connection additive scenes, a server scene prewarmer (KeepUnused), and scene plus distance area of interest |
| `Demos/HashGrid/` | GridCondition with HashGrid |
| `Demos/IntermediateLayer/` | Caesar cipher IntermediateLayer |
| `Demos/ColliderRollback/` | Raycast lag compensation (Pro) |
| `Demos/Benchmarks/NetworkTransform/` | NetworkTransform benchmark |
| `Runtime/Object/NetworkBehaviour/*.cs` | Callbacks, QOL properties, RPC attributes (`Attributes.cs`) |
| `Runtime/Object/Synchronizing/*.cs` | All SyncTypes and their settings and permissions |
| `Runtime/Object/Prediction/*.cs` | `ReplicateState` and its extensions, `PredictionRigidbody`, and the interfaces |
| `Runtime/Managing/**` | All managers (well-commented XML; SceneManager documents its load flow) |
| `Runtime/Generated/Component/**` | NetworkTransform, NetworkAnimator, PlayerSpawner, ServerSpawner, tick smoothers, NetworkCollider, PredictedOwner and PredictedSpawn, PingDisplay, BandwidthDisplay, DefaultScene |
| `Runtime/Observing/Conditions/` | Built-in conditions (read these before writing your own) |
| `Runtime/Utility/Template/TickNetworkBehaviour.cs` | The tick-callback base class |
| `Runtime/Transporting/Transports/` | Tugboat, Synapse, Multipass |
| `Upgrading/` | The Mirror upgrade tool |

**Help:** the Discord at <https://discord.gg/Ta9HgDh4Hj> and the getting-started sample repo at <https://github.com/maxkratt/fish-networking-getting-started>, which has one branch per tutorial step.
