// ============================================================
// Script:      GameManager.cs
// Episode:     EP06 — FishNet multiplayer, Phase 1
// Description: Coordinates startup. Two things have to be ready
//              before play begins, and they can arrive in either order:
//
//              • The world: the streaming controller's initial chunks
//                have loaded (InitializeGameLogic, wired in the scene).
//              • The local player: spawned by FishNet and registered
//                by its ThirdPersonController (RegisterLocalPlayer).
//
//              Once both are ready, the player is held until the chunk
//              under it has loaded, snapped to the terrain, and then
//              released; the camera, Builder and pathfinding follow.
// Author:      Goodgulf
// ============================================================

using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using Goodgulf.Building;
using Goodgulf.Controller;
using Goodgulf.Logging;
using Goodgulf.Pathfinding;
using Goodgulf.TerrainUtils;

namespace Goodgulf.GameLogic
{

    public class GameManager : MonoBehaviour, IDebuggable
    {
        public Camera SceneCamera;
        public Camera PlayerCamera;

        [Header("Streaming & Spawning")]
        [Tooltip("Streams terrain; switched to follow the local player once it spawns.")]
        [SerializeField] private TerrainStreamingController _streamingController;

        [Tooltip("Layers the spawned player is snapped onto (the terrain layer).")]
        [SerializeField] private LayerMask _groundMask = ~0;

        [Tooltip("Height above the player from which the ground is probed when snapping.")]
        [SerializeField] private float _groundProbeHeight = 5000f;

        [Header("Events")]
        [Tooltip("Invoked once the world is loaded and the local player is placed on it.")]
        public UnityEvent OnTerrainReady;

        [Header("Debug")]
        [SerializeField, Tooltip("Enable verbose logging for this component")]
        private bool _debugEnabled = true;
        // IDebuggable contract
        public bool DebugEnabled => _debugEnabled;


        public static GameManager Instance { get; private set; }

        /// <summary>The player owned by this client, or null before it spawns.</summary>
        public GameObject LocalPlayer => _localPlayer != null ? _localPlayer.gameObject : null;

        /// <summary>The scene's terrain streaming controller.</summary>
        public TerrainStreamingController StreamingController => _streamingController;

        private ThirdPersonController _localPlayer;
        private bool _worldReady;
        private bool _gameLogicInitialized;
        private Coroutine _setupRoutine;

        // ============================================================
        // Unity Lifecycle
        // ============================================================

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;

            UseSceneCamera();

            if (_streamingController == null)
                _streamingController = FindFirstObjectByType<TerrainStreamingController>();

            // Show everything during development
            GameLogger.GlobalMinLevel = LogLevel.Verbose;

        }

        // ============================================================
        // Cameras
        // ============================================================

        private void UseSceneCamera()
        {
            if (SceneCamera)
            {
                SceneCamera.enabled = true;
            }
            else this.LogError("Scene Camera is not assigned");

            if (PlayerCamera)
            {
                PlayerCamera.enabled = false;
            }
            else this.LogError("Player Camera is not assigned");
        }

        private void UsePlayerCamera(Transform target)
        {
            if (SceneCamera)
                SceneCamera.enabled = false;

            if (PlayerCamera)
            {
                PlayerCamera.enabled = true;

                if (PlayerCamera.TryGetComponent(out OrbitCamera orbitCamera))
                    orbitCamera.SetTarget(target);
                else
                    this.LogError("Player Camera has no OrbitCamera");
            }
        }

        // ============================================================
        // Startup
        // ============================================================

        /// <summary>
        /// The world is ready: the streaming controller's initial chunks have loaded.
        /// Wired to <c>TerrainStreamingController.onInitialChunksLoaded</c> in the scene.
        /// </summary>
        public void InitializeGameLogic()
        {
            this.LogInfo("World ready");

            _worldReady = true;

            Builder.Instance.CacheTerrains();
        }

        /// <summary>
        /// Called by the owning client's <see cref="ThirdPersonController"/> when it spawns.
        /// </summary>
        public void RegisterLocalPlayer(ThirdPersonController player)
        {
            if (_localPlayer != null && _localPlayer != player)
            {
                this.LogWarning($"Replacing local player {_localPlayer.name} with {player.name}");
                UnregisterLocalPlayer(_localPlayer);
            }

            this.LogInfo($"Local player registered: {player.name}");

            _localPlayer = player;
            _localPlayer.SetMovementEnabled(false);

            // Stream around the player from now on, so the chunk it spawned on gets loaded.
            // The spawn point is no longer needed as a target on this machine.
            if (_streamingController != null)
            {
                _streamingController.RegisterStreamingTarget(player.transform);
                _streamingController.SetPrimaryTarget(player.transform);
                _streamingController.ReleaseInitialTarget();
            }
            else
                this.LogError("No TerrainStreamingController: terrain won't follow the local player");

            _setupRoutine = StartCoroutine(SetupLocalPlayer(player));
        }

        /// <summary>
        /// Called when the local player despawns (e.g. the client disconnects).
        /// </summary>
        public void UnregisterLocalPlayer(ThirdPersonController player)
        {
            if (_localPlayer != player)
                return;

            if (_setupRoutine != null)
            {
                StopCoroutine(_setupRoutine);
                _setupRoutine = null;
            }

            _localPlayer = null;

            if (_streamingController != null)
                _streamingController.UnregisterStreamingTarget(player.transform);

            if (PlayerCamera && PlayerCamera.TryGetComponent(out OrbitCamera orbitCamera))
                orbitCamera.SetTarget(null);

            UseSceneCamera();
        }

        /// <summary>
        /// Waits for the world and the chunk under the player, snaps the player onto the
        /// terrain, releases it, then hands it to the camera, Builder and pathfinding.
        /// </summary>
        private IEnumerator SetupLocalPlayer(ThirdPersonController player)
        {
            while (!_worldReady)
                yield return null;

            while (_streamingController != null && !_streamingController.IsChunkLoadedAt(player.transform.position))
                yield return null;

            // Let physics pick up the new terrain colliders before raycasting
            yield return new WaitForFixedUpdate();

            bool warned = false;
            while (!TrySnapToGround(player))
            {
                if (!warned)
                {
                    this.LogWarning($"No ground under {player.name} at {player.transform.position}; holding the player until there is");
                    warned = true;
                }
                yield return new WaitForFixedUpdate();
            }

            player.SetMovementEnabled(true);
            UsePlayerCamera(player.transform);

            // Chunks may have streamed in around the player since the world became ready
            Builder.Instance.CacheTerrains();
            Builder.Instance.SetPlayer(player.gameObject);

            if (!_gameLogicInitialized)
            {
                _gameLogicInitialized = true;

                this.LogInfo("Invoke OnTerrainReady");
                OnTerrainReady?.Invoke();

                this.LogInfo("Initializing TerrainGraphIntegration");
                TerrainGraphIntegration.Instance.InitializeListeners();
                TerrainGraphIntegration.Instance.Initialize();
            }

            _setupRoutine = null;
        }

        private bool TrySnapToGround(ThirdPersonController player)
        {
            Vector3 origin = player.transform.position + Vector3.up * _groundProbeHeight;

            // RaycastAll so the player's own CharacterController can't be the hit.
            // Set _groundMask to the terrain layer, or the player may land on a tree top.
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, Mathf.Infinity, _groundMask, QueryTriggerInteraction.Ignore);

            bool found = false;
            RaycastHit ground = default;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider.transform.IsChildOf(player.transform))
                    continue;

                if (!found || hit.distance < ground.distance)
                {
                    ground = hit;
                    found = true;
                }
            }

            if (!found)
                return false;

            player.TeleportCharacter(ground.point);
            this.LogInfo($"Placed {player.name} on {ground.collider.name} at {ground.point}");
            return true;
        }

    }

}
