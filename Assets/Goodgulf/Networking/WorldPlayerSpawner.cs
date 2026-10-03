// ============================================================
// Script:      WorldPlayerSpawner.cs
// Episode:     EP06 — FishNet multiplayer, Phase 2
// Description: Replaces FishNet's PlayerSpawner. The server only
//              spawns a player on terrain it has loaded itself, so
//              the spawn position has real ground under it:
//
//              • Spawn points are tried round robin; the first one
//                whose chunk is loaded on the server wins.
//              • If none is loaded (e.g. every player has walked
//                away from the spawn area), the chosen spawn point
//                becomes a temporary streaming target until its chunk
//                loads and the player is spawned.
//              • The spawn position is snapped to the ground the
//                server sees (GameManager.TryFindGround).
//
//              The client still holds its player until its own copy
//              of the chunk has loaded (GameManager.SetupLocalPlayer).
//
//              Put this on the NetworkManager GameObject in place of
//              PlayerSpawner (remove that one, or players spawn twice).
// Author:      Goodgulf
// ============================================================

using System;
using System.Collections;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using Goodgulf.GameLogic;
using Goodgulf.Logging;
using Goodgulf.TerrainUtils;
using UnityEngine;

namespace Goodgulf.Networking
{
    /// <summary>
    /// Spawns a player object for each client once the server has loaded the terrain at a spawn point.
    /// </summary>
    public class WorldPlayerSpawner : MonoBehaviour, IDebuggable
    {
        // ==================================================================
        // Inspector fields
        // ==================================================================

        [Header("Player")]
        [Tooltip("Prefab to spawn for each client.")]
        [SerializeField] private NetworkObject _playerPrefab;

        [Tooltip("Add the player's owner to the active scene (same as PlayerSpawner's Add To Default Scene). " +
                 "Without it, the SceneCondition hides scene objects from the client.")]
        [SerializeField] private bool _addToDefaultScene = true;

        [Header("Spawn Points")]
        [Tooltip("Candidate spawn points, tried round robin. Empty uses the prefab's own position.")]
        [SerializeField] private Transform[] _spawns = Array.Empty<Transform>();

        [Tooltip("Height above the ground at which the player is spawned.")]
        [SerializeField] private float _spawnHeightOffset = 0.1f;

        [Header("Debug")]
        [SerializeField] private bool _debugEnabled = true;
        // IDebuggable contract
        public bool DebugEnabled => _debugEnabled;

        /// <summary>Called on the server when a player is spawned.</summary>
        public event Action<NetworkObject> OnSpawned;

        // ==================================================================
        // Private state
        // ==================================================================

        private NetworkManager _networkManager;
        private int _nextSpawn;

        // ==================================================================
        // Unity lifecycle
        // ==================================================================

        private void Awake()
        {
            if (!TryGetComponent(out _networkManager))
                _networkManager = GetComponentInParent<NetworkManager>();

            if (_networkManager == null)
                _networkManager = InstanceFinder.NetworkManager;

            if (_networkManager == null)
            {
                this.LogError("No NetworkManager found; players will not be spawned");
                return;
            }

            _networkManager.SceneManager.OnClientLoadedStartScenes += SceneManager_OnClientLoadedStartScenes;
        }

        private void OnDestroy()
        {
            if (_networkManager != null)
                _networkManager.SceneManager.OnClientLoadedStartScenes -= SceneManager_OnClientLoadedStartScenes;
        }

        // ==================================================================
        // Spawning
        // ==================================================================

        private void SceneManager_OnClientLoadedStartScenes(NetworkConnection conn, bool asServer)
        {
            if (!asServer)
                return;

            if (_playerPrefab == null)
            {
                this.LogError($"Player prefab is not assigned; cannot spawn a player for connection {conn.ClientId}");
                return;
            }

            StartCoroutine(SpawnWhenGroundIsLoaded(conn));
        }

        /// <summary>
        /// Waits until the server has the chunk at a spawn point loaded, snaps the spawn
        /// position to the ground there and spawns the player for <paramref name="conn"/>.
        /// </summary>
        private IEnumerator SpawnWhenGroundIsLoaded(NetworkConnection conn)
        {
            TerrainStreamingController streaming = GameManager.Instance != null ? GameManager.Instance.StreamingController : null;

            // Without streaming (or a GameManager) there is no terrain to wait for
            if (streaming != null)
            {
                // WorldState starts streaming when the server starts
                while (!streaming.IsStreaming)
                    yield return null;
            }

            Transform spawn = PickSpawn(streaming);
            Vector3 position = spawn != null ? spawn.position : _playerPrefab.transform.position;
            Quaternion rotation = spawn != null ? spawn.rotation : _playerPrefab.transform.rotation;

            if (streaming != null && !streaming.IsChunkLoadedAt(position))
            {
                // No spawn point is on loaded terrain: load this one
                this.LogInfo($"Loading terrain at spawn point {position} for connection {conn.ClientId}");

                Transform target = spawn != null ? spawn : _playerPrefab.transform;
                streaming.RegisterStreamingTarget(target);

                while (!streaming.IsChunkLoadedAt(position))
                {
                    if (!conn.IsActive)
                    {
                        streaming.UnregisterStreamingTarget(target);
                        yield break;
                    }
                    yield return null;
                }

                // Let physics pick up the new terrain colliders before the ground raycast
                yield return new WaitForFixedUpdate();

                // Keep the spawn point as a target until the spawned player has registered
                // itself (ThirdPersonController.OnStartServer runs inside Spawn)
                SpawnPlayer(conn, position, rotation);
                streaming.UnregisterStreamingTarget(target);
                yield break;
            }

            yield return new WaitForFixedUpdate();
            SpawnPlayer(conn, position, rotation);
        }

        /// <summary>
        /// Round robin over the spawn points, preferring the first whose chunk is already
        /// loaded on the server. Falls back to the next spawn point in turn.
        /// </summary>
        private Transform PickSpawn(TerrainStreamingController streaming)
        {
            if (_spawns.Length == 0)
                return null;

            for (int i = 0; i < _spawns.Length; i++)
            {
                int index = (_nextSpawn + i) % _spawns.Length;
                Transform candidate = _spawns[index];

                if (candidate == null)
                    continue;

                if (streaming == null || streaming.IsChunkLoadedAt(candidate.position))
                {
                    _nextSpawn = (index + 1) % _spawns.Length;
                    return candidate;
                }
            }

            // None loaded: take the next one in turn and load it
            Transform fallback = _spawns[_nextSpawn];
            _nextSpawn = (_nextSpawn + 1) % _spawns.Length;
            return fallback;
        }

        private void SpawnPlayer(NetworkConnection conn, Vector3 position, Quaternion rotation)
        {
            if (!conn.IsActive || !_networkManager.IsServerStarted)
                return;

            // Spawn on the ground the server sees; the client snaps again on its own terrain
            if (GameManager.Instance != null &&
                GameManager.Instance.TryFindGround(position, null, out Vector3 groundPoint, out _))
            {
                position = groundPoint + Vector3.up * _spawnHeightOffset;
            }
            else
            {
                this.LogWarning($"No ground found at spawn point {position}; spawning there unchanged");
            }

            NetworkObject nob = _networkManager.GetPooledInstantiated(_playerPrefab, position, rotation, true);
            _networkManager.ServerManager.Spawn(nob, conn);

            if (_addToDefaultScene)
                _networkManager.SceneManager.AddOwnerToDefaultScene(nob);

            this.LogInfo($"Spawned player for connection {conn.ClientId} at {position}");

            OnSpawned?.Invoke(nob);
        }
    }
}
