using System;
using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.Events;
using System.Threading.Tasks;
using System.Linq;

namespace Goodgulf.TerrainUtils
{
    /// <summary>
    /// Advanced terrain streaming controller that manages chunk loading/unloading
    /// around a player or camera position. Uses AsyncTerrainPrefabPlacer for
    /// smooth, frame-drop-free chunk streaming.
    /// Integrates with StreamingTerrainGeneratorJobs for terrain geometry generation.
    /// </summary>
    public class TerrainStreamingController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Initial streaming target (the spawn point). Players are added with RegisterStreamingTarget; " +
                 "a client releases this target once its own player has spawned.")]
        [SerializeField] private Transform player;
        [SerializeField] private Terrain terrainPrefab; // Prefab for terrain chunks
        [SerializeField] private PrefabPlacementConfig prefabPlacementConfig;
        
        [Header("Terrain Generation")]
        [SerializeField] private Goodgulf.TerrainUtils.StreamingTerrainGeneratorJobs terrainGenerator;
        [SerializeField] private int seed = 12345;
        [SerializeField] private float chunkSize = 1000f;
        
        [Header("Streaming Settings")]
        [Tooltip("How many chunks to load around the player (radius)")]
        [SerializeField] private int loadRadius = 3;
        
        [Tooltip("Unload chunks beyond this distance (should be > loadRadius)")]
        [SerializeField] private int unloadRadius = 5;
        
        [Tooltip("Maximum chunks to load per frame (prevents spikes)")]
        [SerializeField] private int maxChunksPerFrame = 3;
        
        [Tooltip("Update streaming every N frames (1 = every frame, 10 = every 10 frames)")]
        [SerializeField] private int updateInterval = 10;
        
        [Header("Performance")]
        [Tooltip("Maximum concurrent async load operations")]
        [SerializeField] private int maxConcurrentLoads = 10;
        
        [Tooltip("Priority loading for chunks closer to player")]
        [SerializeField] private bool usePriorityLoading = true;
        
        [Header("Events")]
        [Tooltip("Called when initial chunks are fully loaded")]
        public UnityEvent onInitialChunksLoaded;
        
        [Tooltip("Called when a chunk starts loading")]
        public UnityEvent<Vector2Int> onChunkLoadStart;
        
        [Tooltip("Called when a chunk finishes loading")]
        public UnityEvent<Vector2Int> onChunkLoadComplete;
        
        [Tooltip("Called when a chunk is unloaded")]
        public UnityEvent<Vector2Int> onChunkUnload;
        
        [Header("Debug")]
        [SerializeField] private bool showDebugGizmos = true;
        [SerializeField] private bool logChunkOperations = false;
        
        [Header("Cache Management")]
        [SerializeField] private bool clearCacheOnStart = false;

        [Header("Networking")]
        [Tooltip("Wait for WorldState to deliver the shared seed before streaming. " +
                 "Turn off to stream immediately with the seed above (offline testing).")]
        [SerializeField] private bool _waitForWorldSeed = true;

        // Streaming targets, reference counted: on the host a player is registered both
        // by the server (every player) and by its owning client (its own player)
        private readonly List<Transform> _targets = new List<Transform>();
        private readonly Dictionary<Transform, int> _targetRefCounts = new Dictionary<Transform, int>();
        private Transform _primaryTarget;
        private bool _initialTargetReleased;

        // Chunks the targets occupied at the last streaming update
        private readonly HashSet<Vector2Int> _lastTargetChunks = new HashSet<Vector2Int>();
        private bool _targetsDirty;
        private bool _streamingStarted;

        // Async prefab placer
        private AsyncTerrainPrefabPlacer asyncPrefabPlacer;
        
        // Track loaded and loading chunks
        private HashSet<Vector2Int> loadedChunks = new HashSet<Vector2Int>();
        private HashSet<Vector2Int> loadingChunks = new HashSet<Vector2Int>();
        private Dictionary<Vector2Int, Task> chunkLoadTasks = new Dictionary<Vector2Int, Task>();
        
        // Track terrain instances per chunk
        private Dictionary<Vector2Int, Terrain> terrainInstances = new Dictionary<Vector2Int, Terrain>();
        
        // Priority queue for chunk loading
        private List<ChunkLoadRequest> pendingLoadRequests = new List<ChunkLoadRequest>();
        
        
        // Frame counter for update interval
        private int frameCounter = 0;
        
        // Initial load tracking
        private bool initialLoadComplete = false;
        private int initialChunkCount = 0;
        
        // Statistics
        private int totalChunksLoaded = 0;
        private int totalChunksUnloaded = 0;
        private float averageLoadTime = 0f;
        private List<float> recentLoadTimes = new List<float>();
        
        private struct ChunkLoadRequest
        {
            public Vector2Int chunkCoord;
            public float distanceToPlayer;
            public int priority;
        }
        
        private void Start()
        {
            InitializeAsyncPlacer();

            if (player == null)
            {
                Debug.LogWarning("Initial streaming target not assigned! Using Camera.main");
                if (Camera.main != null)
                {
                    player = Camera.main.transform;
                }
            }

            // In a networked session WorldState calls BeginStreaming with the shared seed
            if (!_waitForWorldSeed)
            {
                BeginStreaming(seed);
            }
        }

        /// <summary>
        /// Start streaming with the world seed. Called once by WorldState (server and
        /// clients) so every machine generates the same world; later calls are ignored.
        /// </summary>
        public void BeginStreaming(int worldSeed)
        {
            if (_streamingStarted)
            {
                if (worldSeed != seed)
                    Debug.LogError($"BeginStreaming: already streaming with seed {seed}, ignoring seed {worldSeed}");
                return;
            }

            _streamingStarted = true;
            seed = worldSeed;

            // Heights are a pure function of world position today; the seed drives prefab placement
            if (asyncPrefabPlacer != null)
                asyncPrefabPlacer.SetBaseSeed(seed);
            if (terrainGenerator != null)
                terrainGenerator.seed = seed;

            if (logChunkOperations)
            {
                Debug.Log($"Streaming started with seed {seed}");
            }

            if (clearCacheOnStart)
            {
                // _ = InitializeAsync();

                ChunkCacheManager.ClearAllCaches();

            }

            // Skip the spawn point if the local player already replaced it
            if (player != null && !_initialTargetReleased)
            {
                RegisterStreamingTarget(player);
            }

            // Load initial chunks around the targets
            CollectTargetChunks(_lastTargetChunks);
            _targetsDirty = false;
            _ = LoadInitialChunks();
        }

        /// <summary>True once BeginStreaming has run.</summary>
        public bool IsStreaming => _streamingStarted;

        /// <summary>
        /// Initialize the async prefab placer.
        /// </summary>
        private void InitializeAsyncPlacer()
        {
            asyncPrefabPlacer = gameObject.GetComponent<AsyncTerrainPrefabPlacer>();

            if (asyncPrefabPlacer == null)
            {
                Debug.LogError("AsyncTerrainPrefabPlacer not assigned!");
                return;
            }
            // Note: Terrain will be set per-chunk in LoadChunk
            // The seed is applied in BeginStreaming

            if (logChunkOperations)
            {
                Debug.Log("TerrainStreamingController initialized with AsyncTerrainPrefabPlacer");
            }
        }

        async Task InitializeAsync()
        {
            try
            {
                await asyncPrefabPlacer.ClearAllCachesAsync();
            }
            catch (Exception e)
            {
                Debug.LogError($"Init failed: {e}");
            }
        }


        private void Update()
        {
            if (!_streamingStarted) return;

            frameCounter++;

            // Update streaming at specified interval
            if (frameCounter >= updateInterval)
            {
                frameCounter = 0;
                UpdateStreaming();
            }

            // Process pending load requests
            ProcessPendingLoadRequests();
        }

        // ============================================================
        // Streaming targets
        // ============================================================

        /// <summary>
        /// Stream terrain around <paramref name="target"/> as well as any existing targets.
        /// Reference counted: every Register needs a matching Unregister.
        /// </summary>
        public void RegisterStreamingTarget(Transform target)
        {
            if (target == null)
            {
                Debug.LogWarning("RegisterStreamingTarget: target is null");
                return;
            }

            if (_targetRefCounts.TryGetValue(target, out int count))
            {
                _targetRefCounts[target] = count + 1;
                return;
            }

            _targetRefCounts[target] = 1;
            _targets.Add(target);
            _targetsDirty = true;

            if (logChunkOperations)
            {
                Debug.Log($"Streaming target added: {target.name} ({_targets.Count} targets)");
            }
        }

        /// <summary>
        /// Release one registration of <paramref name="target"/>. Its chunks unload once no
        /// remaining target is within the unload radius.
        /// </summary>
        public void UnregisterStreamingTarget(Transform target)
        {
            if (target == null || !_targetRefCounts.TryGetValue(target, out int count))
                return;

            if (count > 1)
            {
                _targetRefCounts[target] = count - 1;
                return;
            }

            RemoveTarget(target);
        }

        /// <summary>
        /// The target pathfinding and the debug UI centre on (the local player).
        /// It must also be registered with RegisterStreamingTarget to be streamed around.
        /// </summary>
        public void SetPrimaryTarget(Transform target)
        {
            _primaryTarget = target;
        }

        /// <summary>
        /// Drop the initial target (the spawn point) once the local player streams instead.
        /// </summary>
        public void ReleaseInitialTarget()
        {
            if (_initialTargetReleased) return;
            _initialTargetReleased = true;

            if (player != null)
                UnregisterStreamingTarget(player);
        }

        private void RemoveTarget(Transform target)
        {
            _targetRefCounts.Remove(target);
            _targets.Remove(target);
            _targetsDirty = true;

            if (_primaryTarget == target)
                _primaryTarget = null;

            if (logChunkOperations && target != null)
            {
                Debug.Log($"Streaming target removed: {target.name} ({_targets.Count} targets)");
            }
        }

        /// <summary>
        /// Fill <paramref name="result"/> with the chunk each live target is in.
        /// Destroyed targets are dropped.
        /// </summary>
        private void CollectTargetChunks(HashSet<Vector2Int> result)
        {
            result.Clear();

            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                Transform target = _targets[i];
                if (target == null)
                {
                    // Destroyed without unregistering (e.g. a despawned player)
                    _targetRefCounts.Remove(target);
                    _targets.RemoveAt(i);
                    _targetsDirty = true;
                    continue;
                }

                result.Add(WorldToChunk(target.position));
            }
        }

        private Vector2Int WorldToChunk(Vector3 position)
        {
            return new Vector2Int(
                Mathf.FloorToInt(position.x / chunkSize),
                Mathf.FloorToInt(position.z / chunkSize)
            );
        }

        /// <summary>
        /// Distance in chunks from <paramref name="chunkCoord"/> to the nearest target chunk.
        /// </summary>
        private float DistanceToNearestTarget(Vector2Int chunkCoord, HashSet<Vector2Int> targetChunks)
        {
            float best = float.MaxValue;
            foreach (Vector2Int targetChunk in targetChunks)
            {
                float distance = Vector2.Distance(chunkCoord, targetChunk);
                if (distance < best) best = distance;
            }
            return best;
        }

        /// <summary>
        /// The chunks to keep loaded: every chunk within loadRadius of any target,
        /// with its distance to the nearest target.
        /// </summary>
        private Dictionary<Vector2Int, float> CollectDesiredChunks(HashSet<Vector2Int> targetChunks)
        {
            Dictionary<Vector2Int, float> desired = new Dictionary<Vector2Int, float>();

            foreach (Vector2Int targetChunk in targetChunks)
            {
                for (int x = -loadRadius; x <= loadRadius; x++)
                {
                    for (int z = -loadRadius; z <= loadRadius; z++)
                    {
                        // Check if within circular radius
                        float distance = new Vector2(x, z).magnitude;
                        if (distance > loadRadius) continue;

                        Vector2Int chunkCoord = targetChunk + new Vector2Int(x, z);
                        if (!desired.TryGetValue(chunkCoord, out float existing) || distance < existing)
                        {
                            desired[chunkCoord] = distance;
                        }
                    }
                }
            }

            return desired;
        }

        // ============================================================
        // Streaming
        // ============================================================

        /// <summary>
        /// Main streaming update - determines which chunks to load/unload.
        /// Runs when a target changes chunk or a target is added or removed.
        /// </summary>
        private void UpdateStreaming()
        {
            HashSet<Vector2Int> targetChunks = new HashSet<Vector2Int>();
            CollectTargetChunks(targetChunks);

            if (!_targetsDirty && targetChunks.SetEquals(_lastTargetChunks))
                return;

            if (logChunkOperations)
            {
                Debug.Log($"Streaming targets now in chunks: {string.Join(", ", targetChunks)}");
            }

            _targetsDirty = false;
            _lastTargetChunks.Clear();
            _lastTargetChunks.UnionWith(targetChunks);

            // With no targets there is nothing to stream towards; keep what is loaded
            if (targetChunks.Count == 0) return;

            Dictionary<Vector2Int, float> desired = CollectDesiredChunks(targetChunks);

            // Drop queued loads that no target needs any more
            pendingLoadRequests.RemoveAll(r => !desired.ContainsKey(r.chunkCoord));

            // Queue chunks that need to be loaded
            QueueChunksToLoad(desired);

            // Unload chunks that are far from every target
            UnloadDistantChunks(targetChunks);
        }

        /// <summary>
        /// Load initial chunks around the targets at startup.
        /// </summary>
        private async Task LoadInitialChunks()
        {
            if (logChunkOperations)
            {
                Debug.Log($"Loading initial chunks around {string.Join(", ", _lastTargetChunks)} (radius: {loadRadius})");
            }

            List<ChunkLoadRequest> initialRequests = new List<ChunkLoadRequest>();

            // Generate list of chunks to load
            foreach (var kvp in CollectDesiredChunks(_lastTargetChunks))
            {
                initialRequests.Add(new ChunkLoadRequest
                {
                    chunkCoord = kvp.Key,
                    distanceToPlayer = kvp.Value,
                    priority = Mathf.RoundToInt(kvp.Value * 10)
                });
            }

            // Sort by priority (closest first)
            initialRequests.Sort((a, b) => a.priority.CompareTo(b.priority));

            initialChunkCount = initialRequests.Count;

            // Load chunks in batches to avoid overwhelming the system
            int batchSize = maxConcurrentLoads;
            for (int i = 0; i < initialRequests.Count; i += batchSize)
            {
                int count = Mathf.Min(batchSize, initialRequests.Count - i);
                List<Task> batchTasks = new List<Task>();

                for (int j = 0; j < count; j++)
                {
                    var request = initialRequests[i + j];
                    if (loadedChunks.Contains(request.chunkCoord) || loadingChunks.Contains(request.chunkCoord))
                        continue;
                    batchTasks.Add(LoadChunk(request.chunkCoord));
                }

                // Wait for this batch to complete before starting next
                await Task.WhenAll(batchTasks);

                // Allow a frame to pass for instantiation
                await Task.Yield();
            }

            if (logChunkOperations)
            {
                Debug.Log($"Initial chunk loading complete. Loaded {initialRequests.Count} chunks");
            }

            // Mark initial load as complete and fire event
            initialLoadComplete = true;

            onInitialChunksLoaded?.Invoke();
        }

        /// <summary>
        /// Queue chunks that need to be loaded around the targets.
        /// </summary>
        private void QueueChunksToLoad(Dictionary<Vector2Int, float> desired)
        {
            foreach (var kvp in desired)
            {
                Vector2Int chunkCoord = kvp.Key;

                // Skip if already loaded or loading
                if (loadedChunks.Contains(chunkCoord) || loadingChunks.Contains(chunkCoord))
                {
                    continue;
                }

                int priority = Mathf.RoundToInt(kvp.Value * 10);
                int existing = pendingLoadRequests.FindIndex(r => r.chunkCoord == chunkCoord);

                if (existing < 0)
                {
                    pendingLoadRequests.Add(new ChunkLoadRequest
                    {
                        chunkCoord = chunkCoord,
                        distanceToPlayer = kvp.Value,
                        priority = priority
                    });
                }
                else
                {
                    // Re-prioritise relative to the targets' new positions
                    var request = pendingLoadRequests[existing];
                    request.distanceToPlayer = kvp.Value;
                    request.priority = priority;
                    pendingLoadRequests[existing] = request;
                }
            }

            // Sort by priority if enabled
            if (usePriorityLoading)
            {
                pendingLoadRequests.Sort((a, b) => a.priority.CompareTo(b.priority));
            }
        }

        /// <summary>
        /// Process pending chunk load requests, respecting max concurrent loads.
        /// </summary>
        private void ProcessPendingLoadRequests()
        {
            if (pendingLoadRequests.Count == 0) return;
            
            // Count current loading operations
            int currentlyLoading = loadingChunks.Count;
            
            // Calculate how many more we can start
            int availableSlots = Mathf.Min(
                maxConcurrentLoads - currentlyLoading,
                maxChunksPerFrame
            );
            
            if (availableSlots <= 0) return;
            
            // Start loading for available slots
            int loaded = 0;
            while (loaded < availableSlots && pendingLoadRequests.Count > 0)
            {
                var request = pendingLoadRequests[0];
                pendingLoadRequests.RemoveAt(0);
                
                // Double-check it's not already loaded/loading
                if (!loadedChunks.Contains(request.chunkCoord) && 
                    !loadingChunks.Contains(request.chunkCoord))
                {
                    _ = LoadChunk(request.chunkCoord);
                    loaded++;
                }
            }
        }
        
        /// <summary>
        /// Load a single chunk asynchronously.
        /// </summary>
        private async Task LoadChunk(Vector2Int chunkCoord)
        {
            // Mark as loading
            loadingChunks.Add(chunkCoord);
            
            // Fire start event
            onChunkLoadStart?.Invoke(chunkCoord);
            
            float startTime = Time.realtimeSinceStartup;
            
            try
            {
                if (logChunkOperations)
                {
                    Debug.Log($"Loading chunk {chunkCoord}...");
                }
                
                // Step 1: Create or get terrain instance for this chunk
                Terrain terrainInstance = GetOrCreateTerrainInstance(chunkCoord);

                if (terrainInstance == null)
                {
                    Debug.LogError($"Terrain at {chunkCoord} is null");
                    return;
                }
                
                // Step 2: Generate terrain geometry using StreamingTerrainGeneratorJobs
                if (terrainGenerator != null)
                {
                    terrainGenerator.GenerateChunk(terrainInstance, chunkCoord);
                }
                else
                {
                    Debug.LogWarning("StreamingTerrainGeneratorJobs not assigned! Terrain geometry will not be generated.");
                }
                
                // Step 3: Place prefabs on the terrain using async placer
                asyncPrefabPlacer.SetTerrain(terrainInstance);
                await asyncPrefabPlacer.PlacePrefabsOnChunkAsync(
                    chunkCoord.x, 
                    chunkCoord.y, 
                    chunkSize
                );
                
                // Mark as loaded
                loadedChunks.Add(chunkCoord);
                totalChunksLoaded++;
                
                // Track load time
                float loadTime = (Time.realtimeSinceStartup - startTime) * 1000f;
                UpdateLoadTimeStats(loadTime);
                
                if (logChunkOperations)
                {
                    Debug.Log($"Chunk {chunkCoord} loaded in {loadTime:F2}ms " +
                             $"(cached: {asyncPrefabPlacer.IsChunkCached(chunkCoord.x, chunkCoord.y)})");
                }
                
                // Fire complete event
                onChunkLoadComplete?.Invoke(chunkCoord);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to load chunk {chunkCoord}: {e.Message}");
            }
            finally
            {
                // Remove from loading set
                loadingChunks.Remove(chunkCoord);
            }
        }
        
        /// <summary>
        /// Get existing terrain instance or create a new one for the chunk.
        /// </summary>
        private Terrain GetOrCreateTerrainInstance(Vector2Int chunkCoord)
        {
            // Check if terrain instance already exists
            if (terrainInstances.TryGetValue(chunkCoord, out Terrain existingTerrain))
            {
                return existingTerrain;
            }
            
            // Create new terrain instance
            Terrain newTerrain = Instantiate(terrainPrefab, transform);
            
            // Position terrain at chunk location
            Vector3 chunkWorldPos = new Vector3(
                chunkCoord.x * chunkSize,
                0,
                chunkCoord.y * chunkSize
            );
            newTerrain.transform.position = chunkWorldPos;
            newTerrain.name = $"Terrain_Chunk_{chunkCoord.x}_{chunkCoord.y}";
            
            // Get or create TerrainData from pool if available
            if (Goodgulf.TerrainUtils.TerrainDataPool.Instance != null)
            {
                TerrainData pooledData = Goodgulf.TerrainUtils.TerrainDataPool.Instance.Get();
                newTerrain.terrainData = pooledData;
                
                // Update terrain collider reference
                TerrainCollider collider = newTerrain.GetComponent<TerrainCollider>();
                if(collider != null)
                    collider.terrainData = pooledData;
            }
            else Debug.LogError("TerrainDataPool.Instance is null!");
            
            
            // Store the terrain instance
            terrainInstances[chunkCoord] = newTerrain;
            
            return newTerrain;
        }
        
        /// <summary>
        /// Unload chunks that are beyond the unload radius of every target.
        /// </summary>
        private void UnloadDistantChunks(HashSet<Vector2Int> targetChunks)
        {
            List<Vector2Int> chunksToUnload = new List<Vector2Int>();

            // Find chunks beyond unload radius of all targets
            foreach (var chunkCoord in loadedChunks)
            {
                if (DistanceToNearestTarget(chunkCoord, targetChunks) > unloadRadius)
                {
                    chunksToUnload.Add(chunkCoord);
                }
            }

            // Unload chunks
            foreach (var chunkCoord in chunksToUnload)
            {
                UnloadChunk(chunkCoord);
            }
        }

        /// <summary>
        /// Unload a single chunk.
        /// </summary>
        private void UnloadChunk(Vector2Int chunkCoord)
        {
            if (logChunkOperations)
            {
                Debug.Log($"Unloading chunk {chunkCoord}");
            }
            
            // Clear prefabs
            asyncPrefabPlacer.ClearChunk(chunkCoord);
            
            // Destroy terrain instance and release TerrainData to pool
            if (terrainInstances.TryGetValue(chunkCoord, out Terrain terrainInstance))
            {
                if (terrainInstance != null)
                {
                    // Release TerrainData back to pool if pool exists
                    if (Goodgulf.TerrainUtils.TerrainDataPool.Instance != null && terrainInstance.terrainData != null)
                    {
                        Goodgulf.TerrainUtils.TerrainDataPool.Instance.Release(terrainInstance.terrainData);
                    }
                    
                    // Destroy the terrain GameObject
                    Destroy(terrainInstance.gameObject);
                }
                
                terrainInstances.Remove(chunkCoord);
            }
            
            loadedChunks.Remove(chunkCoord);
            totalChunksUnloaded++;
            
            // Fire unload event
            onChunkUnload?.Invoke(chunkCoord);
        }
        
        /// <summary>
        /// Update load time statistics.
        /// </summary>
        private void UpdateLoadTimeStats(float loadTime)
        {
            recentLoadTimes.Add(loadTime);
            
            // Keep only last 100 load times
            if (recentLoadTimes.Count > 100)
            {
                recentLoadTimes.RemoveAt(0);
            }
            
            // Calculate average
            averageLoadTime = recentLoadTimes.Average();
        }
        
        /// <summary>
        /// Get streaming statistics as a string.
        /// </summary>
        public string GetStreamingStats()
        {
            return $"Loaded: {loadedChunks.Count} | " +
                   $"Loading: {loadingChunks.Count} | " +
                   $"Pending: {pendingLoadRequests.Count} | " +
                   $"Total Loaded: {totalChunksLoaded} | " +
                   $"Avg Load Time: {averageLoadTime:F2}ms";
        }
        
        /// <summary>
        /// Force load a specific chunk (useful for preloading or debugging).
        /// </summary>
        public async Task ForceLoadChunk(int chunkX, int chunkZ)
        {
            Vector2Int chunkCoord = new Vector2Int(chunkX, chunkZ);
            
            if (!loadedChunks.Contains(chunkCoord) && !loadingChunks.Contains(chunkCoord))
            {
                await LoadChunk(chunkCoord);
            }
        }
        
        /// <summary>
        /// Force unload a specific chunk.
        /// </summary>
        public void ForceUnloadChunk(int chunkX, int chunkZ)
        {
            Vector2Int chunkCoord = new Vector2Int(chunkX, chunkZ);
            
            if (loadedChunks.Contains(chunkCoord))
            {
                UnloadChunk(chunkCoord);
            }
        }
        
        /// <summary>
        /// Unload all chunks.
        /// </summary>
        public void UnloadAllChunks()
        {
            List<Vector2Int> allChunks = new List<Vector2Int>(loadedChunks);
            
            foreach (var chunk in allChunks)
            {
                UnloadChunk(chunk);
            }
            
            pendingLoadRequests.Clear();
        }
        
        /// <summary>
        /// Check if a chunk is loaded.
        /// </summary>
        public bool IsChunkLoaded(int chunkX, int chunkZ)
        {
            return loadedChunks.Contains(new Vector2Int(chunkX, chunkZ));
        }
        
        /// <summary>
        /// Get list of currently loaded chunk coordinates.
        /// </summary>
        public List<Vector2Int> GetLoadedChunks()
        {
            return new List<Vector2Int>(loadedChunks);
        }
        
        private void OnDestroy()
        {
            // Clean up
            UnloadAllChunks();
        }
        
        private void OnDrawGizmos()
        {
            if (!showDebugGizmos) return;

            List<Transform> gizmoTargets = Application.isPlaying ? _targets : new List<Transform> { player };

            foreach (Transform target in gizmoTargets)
            {
                if (target == null) continue;

                Vector2Int targetChunk = WorldToChunk(target.position);
                Vector3 center = new Vector3(targetChunk.x * chunkSize, 0, targetChunk.y * chunkSize);

                // Draw load radius
                Gizmos.color = Color.green;
                DrawCircleGizmo(center, loadRadius * chunkSize);

                // Draw unload radius
                Gizmos.color = Color.red;
                DrawCircleGizmo(center, unloadRadius * chunkSize);
            }

            // Draw loaded chunks
            if (Application.isPlaying)
            {
                foreach (var chunk in loadedChunks)
                {
                    Gizmos.color = new Color(0, 1, 0, 0.2f);
                    Vector3 chunkCenter = new Vector3(
                        chunk.x * chunkSize + chunkSize * 0.5f,
                        0,
                        chunk.y * chunkSize + chunkSize * 0.5f
                    );
                    Gizmos.DrawCube(chunkCenter, new Vector3(chunkSize * 0.9f, 1, chunkSize * 0.9f));
                }
                
                // Draw loading chunks in yellow
                foreach (var chunk in loadingChunks)
                {
                    Gizmos.color = new Color(1, 1, 0, 0.2f);
                    Vector3 chunkCenter = new Vector3(
                        chunk.x * chunkSize + chunkSize * 0.5f,
                        0,
                        chunk.y * chunkSize + chunkSize * 0.5f
                    );
                    Gizmos.DrawCube(chunkCenter, new Vector3(chunkSize * 0.9f, 2, chunkSize * 0.9f));
                }
            }
        }
        
        private void DrawCircleGizmo(Vector3 center, float radius)
        {
            int segments = 32;
            float angleStep = 360f / segments;
            
            for (int i = 0; i < segments; i++)
            {
                float angle1 = i * angleStep * Mathf.Deg2Rad;
                float angle2 = (i + 1) * angleStep * Mathf.Deg2Rad;
                
                Vector3 point1 = center + new Vector3(
                    Mathf.Cos(angle1) * radius,
                    0,
                    Mathf.Sin(angle1) * radius
                );
                
                Vector3 point2 = center + new Vector3(
                    Mathf.Cos(angle2) * radius,
                    0,
                    Mathf.Sin(angle2) * radius
                );
                
                Gizmos.DrawLine(point1, point2);
            }
        }
        
        private void OnGUI()
        {
            if (!Application.isPlaying) return;
            
            // Display streaming stats
            GUI.Box(new Rect(10, 220, 500, 80), "");
            GUI.Label(new Rect(20, 230, 480, 20), $"Streaming: {GetStreamingStats()}");
            GUI.Label(new Rect(20, 250, 480, 20), $"Async Cache: {asyncPrefabPlacer.GetAsyncCacheStats()}");
            Transform primary = Player();
            string primaryChunk = primary != null ? WorldToChunk(primary.position).ToString() : "none";
            GUI.Label(new Rect(20, 270, 480, 20), $"Player Chunk: {primaryChunk} | Targets: {_targets.Count}");
        }
        
        
        public GameObject TerrainParent()
        {
            return gameObject;
        }

        public int ViewDistance()
        {
            return loadRadius;
        }

        /// <summary>
        /// True if the chunk containing <paramref name="worldPosition"/> has finished loading.
        /// </summary>
        public bool IsChunkLoadedAt(Vector3 worldPosition)
        {
            return IsChunkLoaded(
                Mathf.FloorToInt(worldPosition.x / chunkSize),
                Mathf.FloorToInt(worldPosition.z / chunkSize));
        }

        /// <summary>
        /// The target pathfinding centres on: the primary target (the local player) if set,
        /// otherwise the first live target, otherwise the initial target.
        /// </summary>
        public Transform Player()
        {
            if (_primaryTarget != null) return _primaryTarget;

            foreach (Transform target in _targets)
            {
                if (target != null) return target;
            }

            return player;
        }

        public Terrain TerrainPrefab()
        {
            return terrainPrefab;
        }
        
        public float ChunkSize()
        {
            return chunkSize;
        }

        public int UnloadRadius()
        {
            return unloadRadius;
        }        


    }    
}
