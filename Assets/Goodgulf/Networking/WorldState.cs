// ============================================================
// Script:      WorldState.cs
// Episode:     EP06 — FishNet multiplayer, Phase 2
// Description: Server-owned world settings shared with every client.
//              Terrain is never sent over the network: each machine
//              generates it locally from the seed held here, so the
//              seed must arrive before any chunk is streamed.
//
//              • The server picks the seed in OnStartServer, from
//                -seed <n> on the command line or the Inspector value,
//                and starts its own streaming with it.
//              • Clients read it in OnStartClient (the seed is already
//                synced in the spawn message) and start streaming.
//
//              Terrain edits (raise/depress) are server authoritative:
//              • Clients ask with RequestTerrainEdit; the server checks
//                reach, rate and size, then appends to _terrainEdits.
//              • Every machine applies new edits to chunks it has
//                loaded, and replays a chunk's edits when it loads
//                (TerrainStreamingController.ChunkTerrainReady).
//              • A per-chunk count of applied edits makes sure each
//                edit is applied exactly once per load of a chunk.
//
//              Persistence (server): the world (seed, pieces, terrain
//              edits) is loaded from WorldSaveFile when the server starts
//              and saved when it stops, or on request (SaveWorld /
//              LoadWorld, bound to the save/load keys through Builder).
//
//              Put this on a prefab with a NetworkObject (Is Global on,
//              Initialize Order -128) listed in the NetworkManager's
//              ServerSpawner. TerrainStreamingController must have
//              "Wait For World Seed" on.
// Author:      Goodgulf
// ============================================================

using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Transporting;
using Goodgulf.Building;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Goodgulf.Logging;
using Goodgulf.Pathfinding;
using Goodgulf.TerrainUtils;
using UnityEngine;

namespace Goodgulf.Networking
{
    /// <summary>
    /// Global networked world state. Holds the shared world seed and the terrain edits.
    /// </summary>
    public class WorldState : NetworkBehaviour, IDebuggable
    {
        // ==================================================================
        // Inspector fields
        // ==================================================================

        [Header("Seed")]
        [Tooltip("World seed used by the server unless -seed <n> is passed on the command line.")]
        [SerializeField] private int _defaultSeed = 12345;

        [Header("Terrain Edit Limits (server)")]
        [Tooltip("Furthest an edit may be from the requesting player, in metres.")]
        [SerializeField] private float _maxEditReach = 50f;

        [Tooltip("Minimum seconds between edits from one client.")]
        [SerializeField] private float _minEditInterval = 0.2f;

        [Tooltip("Largest allowed edit radius, in metres.")]
        [SerializeField] private float _maxEditRadius = 10f;

        [Tooltip("Largest allowed edit depth or height, in metres.")]
        [SerializeField] private float _maxEditDepth = 2f;

        [Header("Persistence (server)")]
        [Tooltip("Load the world save when the server starts. Its seed replaces the default and -seed.")]
        [SerializeField] private bool _loadSaveOnStart = true;

        [Tooltip("Save the world when the server stops.")]
        [SerializeField] private bool _saveOnServerStop = true;

        [Header("Debug")]
        [SerializeField] private bool _debugEnabled = true;
        // IDebuggable contract
        public bool DebugEnabled => _debugEnabled;

        // ==================================================================
        // Synced state
        // ==================================================================

        private readonly SyncVar<int> _seed = new();

        // Every accepted terrain edit, in the order the server applied them
        private readonly SyncList<TerrainEdit> _terrainEdits = new();

        /// <summary>The current world state, or null before it has spawned.</summary>
        public static WorldState Instance { get; private set; }

        /// <summary>The shared world seed.</summary>
        public int Seed => _seed.Value;

        // ==================================================================
        // Private state
        // ==================================================================

        private TerrainStreamingController _streaming;

        // Per loaded chunk: how many entries of _terrainEdits have been considered for it
        private readonly Dictionary<Vector2Int, int> _editsAppliedUpTo = new Dictionary<Vector2Int, int>();

        // Server: time of each client's last accepted edit, for rate limiting
        private readonly Dictionary<int, float> _lastEditTime = new Dictionary<int, float>();

        // ==================================================================
        // Unity / network lifecycle
        // ==================================================================

        private void Awake()
        {
            _terrainEdits.OnChange += TerrainEdits_OnChange;
        }

        public override void OnStartNetwork()
        {
            if (Instance != null && Instance != this)
                this.LogWarning("A second WorldState spawned; the newest one wins");

            Instance = this;
        }

        public override void OnStopNetwork()
        {
            if (_streaming != null)
            {
                _streaming.ChunkTerrainReady -= Streaming_ChunkTerrainReady;
                _streaming.onChunkUnload.RemoveListener(Streaming_OnChunkUnload);
                _streaming = null;
            }

            _editsAppliedUpTo.Clear();
            _lastEditTime.Clear();

            if (Instance == this)
                Instance = null;
        }

        public override void OnStartServer()
        {
            // Set here so the value goes out in the spawn message
            _seed.Value = ReadSeedFromArgs(Environment.GetCommandLineArgs(), _defaultSeed);

            // A saved world brings its own seed (and edits, which must be in the list
            // before streaming starts so the first chunks replay them)
            WorldSaveData save = null;
            if (_loadSaveOnStart && WorldSaveFile.TryLoad(out save))
            {
                _seed.Value = save.Seed;
                _terrainEdits.AddRange(save.TerrainEdits);
                this.LogInfo($"Loaded world save: seed {save.Seed}, {save.TerrainEdits.Count} terrain edit(s), {save.Pieces.Count} piece(s)");
            }

            this.LogInfo($"Server world seed: {_seed.Value}");

            ServerManager.OnRemoteConnectionState += ServerManager_OnRemoteConnectionState;

            // The server needs the world too (colliders, pathfinding, building)
            StartStreaming();

            // Pieces are rebuilt as their chunks load
            if (save != null && Builder.Instance != null)
                Builder.Instance.QueueSavedPieces(save.Pieces);
        }

        public override void OnStopServer()
        {
            ServerManager.OnRemoteConnectionState -= ServerManager_OnRemoteConnectionState;

            if (_saveOnServerStop)
                SaveWorld();
        }

        private void ServerManager_OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            // Forget a disconnected client's rate limit (client IDs are reused)
            if (args.ConnectionState == RemoteConnectionState.Stopped)
                _lastEditTime.Remove(conn.ClientId);
        }

        public override void OnStartClient()
        {
            // On the host the server already started streaming; BeginStreaming ignores the repeat
            this.LogInfo($"Client received world seed: {_seed.Value}");
            StartStreaming();
        }

        // ==================================================================
        // Persistence (server)
        // ==================================================================

        /// <summary>
        /// Server: write the seed, every piece and the terrain edits to the world save.
        /// </summary>
        public bool SaveWorld()
        {
            if (!IsServerInitialized)
            {
                this.LogWarning("SaveWorld: only the server can save the world");
                return false;
            }

            if (Builder.Instance == null)
                return false;

            WorldSaveData data = new WorldSaveData
            {
                Seed = _seed.Value,
                Pieces = Builder.Instance.GetSavedPieces(),
                TerrainEdits = new List<TerrainEdit>(_terrainEdits)
            };

            return WorldSaveFile.Save(data);
        }

        /// <summary>
        /// Server: load the world save into the running session. Only into an untouched
        /// world with the same seed: terrain can't be regenerated with another seed while
        /// players are in it, and loading on top of existing pieces would duplicate them.
        /// </summary>
        public bool LoadWorld()
        {
            if (!IsServerInitialized)
            {
                this.LogWarning("LoadWorld: only the server can load the world");
                return false;
            }

            if (!WorldSaveFile.TryLoad(out WorldSaveData save))
            {
                this.LogWarning($"LoadWorld: no world save at {WorldSaveFile.FilePath}");
                return false;
            }

            if (save.Seed != _seed.Value)
            {
                this.LogWarning($"LoadWorld: the save uses seed {save.Seed} but this world uses {_seed.Value}; restart the server to load it");
                return false;
            }

            if (Builder.Instance == null ||
                Builder.Instance.PieceCount > 0 || Builder.Instance.PendingSavedPieceCount > 0 ||
                _terrainEdits.Count > 0)
            {
                this.LogWarning("LoadWorld: the world already has pieces or terrain edits; loading would duplicate them");
                return false;
            }

            // Applied live to loaded chunks and replayed on the others, like new edits
            _terrainEdits.AddRange(save.TerrainEdits);
            Builder.Instance.QueueSavedPieces(save.Pieces);

            this.LogInfo($"Loaded world save: {save.TerrainEdits.Count} terrain edit(s), {save.Pieces.Count} piece(s)");
            return true;
        }

        // ==================================================================
        // Terrain edits — requests
        // ==================================================================

        /// <summary>
        /// Ask the server to edit the terrain. Positive <paramref name="depth"/> pushes it down,
        /// negative raises it. The edit shows up on every machine once the server accepts it.
        /// </summary>
        public void RequestTerrainEdit(Vector3 position, float radius, float depth)
        {
            ServerRequestTerrainEdit(position, radius, depth);
        }

        [ServerRpc(RequireOwnership = false)]
        private void ServerRequestTerrainEdit(Vector3 position, float radius, float depth, NetworkConnection caller = null)
        {
            // Untrusted client input: validate everything
            if (caller == null || caller.FirstObject == null)
            {
                this.LogWarning("Terrain edit rejected: the caller has no player");
                return;
            }

            if (radius <= 0f || radius > _maxEditRadius || Mathf.Abs(depth) > _maxEditDepth ||
                float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z))
            {
                this.LogWarning($"Terrain edit rejected for client {caller.ClientId}: radius {radius} or depth {depth} out of range");
                return;
            }

            float distance = Vector3.Distance(caller.FirstObject.transform.position, position);
            if (distance > _maxEditReach)
            {
                this.LogWarning($"Terrain edit rejected for client {caller.ClientId}: {distance:F1} m away (max {_maxEditReach} m)");
                return;
            }

            if (_lastEditTime.TryGetValue(caller.ClientId, out float last) && Time.time - last < _minEditInterval)
                return;

            if (_streaming == null)
                return;

            Vector2Int chunk = _streaming.ChunkAt(position);
            if (!_streaming.TryGetLoadedTerrain(chunk, out _))
            {
                this.LogWarning($"Terrain edit rejected for client {caller.ClientId}: chunk {chunk} is not loaded on the server");
                return;
            }

            _lastEditTime[caller.ClientId] = Time.time;

            // Applied on the server (and every client) by TerrainEdits_OnChange
            _terrainEdits.Add(new TerrainEdit(chunk, position, radius, depth));
        }

        // ==================================================================
        // Terrain edits — applying
        // ==================================================================

        private void TerrainEdits_OnChange(SyncListOperation op, int index, TerrainEdit oldItem, TerrainEdit newItem, bool asServer)
        {
            if (op != SyncListOperation.Add)
                return;

            // On the host the server callback already applied it to the shared terrain
            if (!asServer && IsServerInitialized)
                return;

            ApplyNewEdit(index, newItem);
        }

        /// <summary>
        /// Apply a newly added edit to its chunk if that chunk is loaded here.
        /// If it isn't, the edit is replayed when the chunk loads.
        /// </summary>
        private void ApplyNewEdit(int index, TerrainEdit edit)
        {
            if (_streaming == null)
                return;

            Vector2Int chunk = edit.GetChunk();

            // Not loaded, or not yet registered as ready: replay covers it
            if (!_editsAppliedUpTo.TryGetValue(chunk, out int appliedUpTo))
                return;

            // Already applied (e.g. the chunk loaded and replayed it in the meantime)
            if (index < appliedUpTo)
                return;

            if (!_streaming.TryGetLoadedTerrain(chunk, out Terrain terrain))
                return;

            // Catch up on anything between the watermark and this edit, in order
            int count = _terrainEdits.Count;
            for (int i = appliedUpTo; i <= index && i < count; i++)
            {
                TerrainEdit e = _terrainEdits[i];
                if (e.GetChunk() == chunk)
                    e.ApplyTo(terrain);
            }
            _editsAppliedUpTo[chunk] = Mathf.Min(index + 1, count);

            // Pathfinding re-reads the heights in the edited area
            if (TerrainGraphIntegration.Instance != null)
            {
                TerrainGraphIntegration.Instance.InvalidateHeightmapCache(chunk);
                TerrainGraphIntegration.Instance.RequestDirtyRegionRebuild(edit.Position, edit.Radius);
            }
        }

        /// <summary>
        /// A chunk has been generated: replay every edit made to it so far, in order.
        /// Runs before the chunk counts as loaded, so pathfinding reads the edited heights.
        /// </summary>
        private void Streaming_ChunkTerrainReady(Vector2Int chunk, Terrain terrain)
        {
            int count = _terrainEdits.Count;
            int replayed = 0;

            for (int i = 0; i < count; i++)
            {
                TerrainEdit e = _terrainEdits[i];
                if (e.GetChunk() != chunk)
                    continue;

                e.ApplyTo(terrain);
                replayed++;
            }

            _editsAppliedUpTo[chunk] = count;

            if (replayed > 0)
                this.LogInfo($"Replayed {replayed} terrain edit(s) on chunk {chunk}");
        }

        private void Streaming_OnChunkUnload(Vector2Int chunk)
        {
            // Heights are regenerated on the next load, so everything is replayed then
            _editsAppliedUpTo.Remove(chunk);
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        private void StartStreaming()
        {
            if (_streaming == null)
            {
                _streaming = FindFirstObjectByType<TerrainStreamingController>();
                if (_streaming == null)
                {
                    this.LogError("No TerrainStreamingController in the scene; the world will not load");
                    return;
                }

                // Subscribe before streaming starts so the first chunks replay their edits too
                _streaming.ChunkTerrainReady += Streaming_ChunkTerrainReady;
                _streaming.onChunkUnload.AddListener(Streaming_OnChunkUnload);
            }

            _streaming.BeginStreaming(_seed.Value);
        }

        /// <summary>
        /// Returns the value after <c>-seed</c> on the command line, or <paramref name="fallback"/>.
        /// </summary>
        private static int ReadSeedFromArgs(string[] args, int fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-seed", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(args[i + 1], out int seed))
                {
                    return seed;
                }
            }

            return fallback;
        }
    }
}
