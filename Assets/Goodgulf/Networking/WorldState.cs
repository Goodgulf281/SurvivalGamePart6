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
//              Put this on a prefab with a NetworkObject (Is Global on,
//              Initialize Order -128) listed in the NetworkManager's
//              ServerSpawner. TerrainStreamingController must have
//              "Wait For World Seed" on.
// Author:      Goodgulf
// ============================================================

using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Goodgulf.Logging;
using Goodgulf.TerrainUtils;
using UnityEngine;

namespace Goodgulf.Networking
{
    /// <summary>
    /// Global networked world state. Holds the shared world seed.
    /// </summary>
    public class WorldState : NetworkBehaviour, IDebuggable
    {
        // ==================================================================
        // Inspector fields
        // ==================================================================

        [Header("Seed")]
        [Tooltip("World seed used by the server unless -seed <n> is passed on the command line.")]
        [SerializeField] private int _defaultSeed = 12345;

        [Header("Debug")]
        [SerializeField] private bool _debugEnabled = true;
        // IDebuggable contract
        public bool DebugEnabled => _debugEnabled;

        // ==================================================================
        // Synced state
        // ==================================================================

        private readonly SyncVar<int> _seed = new();

        /// <summary>The current world state, or null before it has spawned.</summary>
        public static WorldState Instance { get; private set; }

        /// <summary>The shared world seed.</summary>
        public int Seed => _seed.Value;

        // ==================================================================
        // Network lifecycle
        // ==================================================================

        public override void OnStartNetwork()
        {
            if (Instance != null && Instance != this)
                this.LogWarning("A second WorldState spawned; the newest one wins");

            Instance = this;
        }

        public override void OnStopNetwork()
        {
            if (Instance == this)
                Instance = null;
        }

        public override void OnStartServer()
        {
            // Set here so the value goes out in the spawn message
            _seed.Value = ReadSeedFromArgs(Environment.GetCommandLineArgs(), _defaultSeed);
            this.LogInfo($"Server world seed: {_seed.Value}");

            // The server needs the world too (colliders, pathfinding, building)
            StartStreaming();
        }

        public override void OnStartClient()
        {
            // On the host the server already started streaming; BeginStreaming ignores the repeat
            this.LogInfo($"Client received world seed: {_seed.Value}");
            StartStreaming();
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        private void StartStreaming()
        {
            TerrainStreamingController streaming = FindFirstObjectByType<TerrainStreamingController>();
            if (streaming == null)
            {
                this.LogError("No TerrainStreamingController in the scene; the world will not load");
                return;
            }

            streaming.BeginStreaming(_seed.Value);
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
