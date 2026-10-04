using System;
using System.IO;
using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Profiling;
using System.Threading;
using UnityEditor;
using UnityEngine.Serialization;
using Goodgulf.TerrainUtils;
using Unity.VisualScripting;
using UnityEngine.UI;
using Goodgulf.Networking;
using Goodgulf.Pathfinding;
using FishNet;
using FishNet.Object;
using FishNet.Connection;

namespace Goodgulf.Building
{
    /// <summary>
    /// Central manager responsible for all building logic:
    /// - Terrain and grid management
    /// - Building area creation
    /// - Constructed item lifecycle
    /// - Player interaction with the building system
    /// </summary>
    public class Builder : MonoBehaviour
    {
        // All available build templates (prefabs + metadata)
        [SerializeField]
        private List<BuildTemplate> _buildItemTemplates;

        // Terrain/grid constants
        public const int TerrainSize = 1000;
        public const int BuildingAreaSize = 100;
        public const int GridSize = TerrainSize / BuildingAreaSize;

        // Prefab used to visually represent a building area
        [SerializeField] 
        private GameObject _buildingAreaPrefab;

        // Worker update timer (used to periodically recalc areas)
        [Header("Worker Timer")]
        [SerializeField] 
        private float _workerTimerDuration = 5.0f;
        private float _workerTimer = 0.0f;

        // Debug toggles
        [Header("Debug Mode")]
        [SerializeField]
        private bool _debugEnabled = true;

        // World and terrain references
        [Header("World")]
        [SerializeField, Tooltip("Legacy, only drawn as a gizmo. Terrain chunks and building areas are positioned from (0,0,0) by TerrainStreamingController.")]
        private Vector3 _worldOrigin;
        [SerializeField]
        private Terrain _activeTerrain;
        private Collider _activeTerrainCollider;

        // Build constraints and layers
        [FormerlySerializedAs("_buildigBlockLayers")]
        [Header("Buildable")]
        [SerializeField]
        private LayerMask _buildingBlockLayers;
        [SerializeField]
        private LayerMask _buildableLayers;
        [SerializeField]
        private List<NonBuildableArea> _nonBuildableAreas;
        [SerializeField] 
        private LayerMask _transparentBlockLayer;

        // Magnetic snapping parameters
        [SerializeField] 
        private float _magneticRadius = 0.5f;
        [FormerlySerializedAs("_magenticLayer")] [SerializeField] 
        private LayerMask _magneticLayer;

        // Networking (Phase 3): the server owns building, clients mirror it
        [Header("Networking")]
        [Tooltip("Prefab with a NetworkObject and NetworkBuildingArea, spawned by the server for each building area.")]
        [SerializeField]
        private NetworkObject _networkBuildingAreaPrefab;
        [Tooltip("Furthest a piece may be built or destroyed from the requesting player, in metres (server check).")]
        [SerializeField]
        private float _maxBuildReach = 30f;
        [Tooltip("Minimum seconds between build or destroy requests from one player (server check).")]
        [SerializeField]
        private float _minBuildInterval = 0.1f;

        /// <summary>Radius of the pathfinding obstacle invalidation around a built or removed piece.</summary>
        public const float ObstacleInvalidationRadius = 4f;

        public float MaxBuildReach => _maxBuildReach;
        public float MinBuildInterval => _minBuildInterval;

        // Building areas by global building-area grid coordinate. Kept here rather than on
        // the terrain chunk's BuildableTerrain, which is destroyed when the chunk unloads.
        private Dictionary<Vector2Int, BuildingArea> _buildingAreasByGrid;

        // Server: the networked object of each building area, by area ID
        private Dictionary<string, NetworkBuildingArea> _networkAreasById;

        // All terrains indexed by grid coordinate
        private Dictionary<Vector2Int, Terrain> _allTerrains;

        // Source of the terrain chunk coordinates
        private TerrainStreamingController _streamingController;

        /// <summary>
        /// Currently active terrain; also caches its collider
        /// </summary>
        public Terrain ActiveTerrain
        {
            get => _activeTerrain;
            set
            {
                _activeTerrain = value;
                if (_activeTerrain.TryGetComponent<Collider>(out Collider _collider))
                {
                    _activeTerrainCollider = _collider;
                }
                else Debug.LogError($"Builder.Terrain(): ActiveTerrain {_activeTerrain.name} has no Collider");
            }
        }

        // Building areas indexed by GUID
        private Dictionary<string, BuildingArea> _buildingAreasById;

        // All constructed items indexed by global ID
        private Dictionary<int, ConstructedItem> _allConstructedItems;

        // Player references
        private GameObject _player;
        private PlayerBuilderInventoryBridge _playerBuilderInventoryBridge;

        // Global incremental ID for constructed items
        private int _globalID = 0;

        // Ensure the builder does not start before Initialization
        private bool _initialized = false;
        
        // Singleton instance
        public static Builder Instance { get; private set; }

        /// <summary>
        /// Initialize singleton and cache all active terrains
        /// </summary>
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            _buildingAreasById = new Dictionary<string, BuildingArea>();
            _allConstructedItems = new Dictionary<int, ConstructedItem>();
            _allTerrains = new Dictionary<Vector2Int, Terrain>();
            _buildingAreasByGrid = new Dictionary<Vector2Int, BuildingArea>();
            _networkAreasById = new Dictionary<string, NetworkBuildingArea>();

        }

        public void CacheTerrains()
        {
            // Cache all active terrains by world index
            Terrain[] terrains = Terrain.activeTerrains;
            _allTerrains.Clear();
            
            foreach (var terrain in terrains)
            {
                Vector2Int terPos = GetTerrainIndex(terrain.transform.position);

                if (_debugEnabled)
                    Debug.Log($"Found terrain: {terrain.name} at {terrain.transform.position} -> index {terPos}");

                _allTerrains[terPos] = terrain;
            }
        }

        // ==================================================================
        // Network session helpers
        // ==================================================================

        /// <summary>True while a FishNet server or client is running.</summary>
        public static bool IsNetworkSession => InstanceFinder.IsServerStarted || InstanceFinder.IsClientStarted;

        /// <summary>True when this process runs the server (dedicated or host).</summary>
        public static bool IsNetworkServer => InstanceFinder.IsServerStarted;

        /// <summary>True for a client that is not also the server.</summary>
        public static bool IsNetworkClientOnly => InstanceFinder.IsClientStarted && !InstanceFinder.IsServerStarted;

        /// <summary>
        /// The terrain chunk at <paramref name="worldPos"/>. Terrain streams in and out, so the
        /// cache is refreshed once when the chunk isn't in it.
        /// </summary>
        public bool TryGetTerrainAt(Vector3 worldPos, out Terrain terrain)
        {
            Vector2Int index = GetTerrainIndex(worldPos);

            if (_allTerrains.TryGetValue(index, out terrain) && terrain != null)
                return true;

            CacheTerrains();
            return _allTerrains.TryGetValue(index, out terrain) && terrain != null;
        }

        /// <summary>
        /// Global building-area grid coordinate (across all terrain chunks).
        /// </summary>
        private Vector2Int GetGlobalAreaGrid(Vector3 worldPos)
        {
            // Relative to the world origin, like the terrain chunks, so area cells line up
            // with each chunk's own building-area grid
            return new Vector2Int(
                Mathf.FloorToInt(worldPos.x / BuildingAreaSize),
                Mathf.FloorToInt(worldPos.z / BuildingAreaSize));
        }
        

        /// <summary>
        /// Draws world origin gizmo for debugging
        /// </summary>
        void OnDrawGizmos()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawLine(_worldOrigin, _worldOrigin + Vector3.up * 20f);
        }

        public void Initialize()
        {
            Debug.Log("<color=yellow>Builder.Initialize(): Invoked</color>");
            
            CacheTerrains();

            if (_activeTerrain && _activeTerrainCollider == null)
            {
                if (_activeTerrain.TryGetComponent<Collider>(out Collider _collider))
                {
                    _activeTerrainCollider = _collider;
                }
                else Debug.LogError($"Builder.Initialize(): ActiveTerrain {_activeTerrain.name} has no Collider");
            }

            if (_player)
            {
                if (_player.TryGetComponent<PlayerBuilderInventoryBridge>(out PlayerBuilderInventoryBridge bridge))
                {
                    _playerBuilderInventoryBridge = bridge;
                }
                else
                    Debug.LogError("Builder.Initialize(): PlayerBuilderInventoryBridge not found");
            }
            
            _initialized = true;
        }
        
        
        public PlayerBuilderInventoryBridge GetPlayerBuilderInventoryBridge()
        {
            return _playerBuilderInventoryBridge;
        }

        /// <summary>
        /// Returns a unique global ID for constructed items, now threadsafe
        /// </summary>
        public int GetNewGlobalID()
        {
            // return _globalID++;
            return Interlocked.Increment(ref _globalID);
        }

        /// <summary>
        /// Fetch a constructed item by ID
        /// </summary>
        public ConstructedItem GetConstructedItem(int itemID)
        {
            _allConstructedItems.TryGetValue(itemID, out ConstructedItem constructedItem);
            return constructedItem;
        }

        /// <summary>
        /// Recalculates which building areas should be processed by the worker,
        /// based on player position (3x3 grid around player).
        /// </summary>
        private void UpdateWorkerAreas()
        {
            List<BuildingArea> areas = new List<BuildingArea>();

            if (_buildingAreasByGrid.Count > 0)
            {
                // Collect the 3x3 areas around every player: on the server all connected
                // players, offline the local player
                foreach (Vector3 playerPosition in GetPlayerPositions())
                {
                    Vector2Int playerGrid = GetGlobalAreaGrid(playerPosition);

                    for (int x = playerGrid.x - 1; x <= playerGrid.x + 1; x++)
                    {
                        for (int y = playerGrid.y - 1; y <= playerGrid.y + 1; y++)
                        {
                            if (_buildingAreasByGrid.TryGetValue(new Vector2Int(x, y), out var area) &&
                                !areas.Contains(area))
                            {
                                areas.Add(area);
                            }
                        }
                    }
                }
            }

            // Always replace the instructions, so areas nobody is near stop being recalculated
            RecalculationWorker.Instance.SetWorkerInstructions(areas);
        }

        /// <summary>
        /// Positions of the players whose surroundings get structural recalculation.
        /// </summary>
        private List<Vector3> GetPlayerPositions()
        {
            List<Vector3> positions = new List<Vector3>();

            if (IsNetworkServer)
            {
                foreach (NetworkConnection conn in InstanceFinder.ServerManager.Clients.Values)
                {
                    if (conn.FirstObject != null)
                        positions.Add(conn.FirstObject.transform.position);
                }
            }
            else if (_player != null)
            {
                positions.Add(_player.transform.position);
            }

            return positions;
        }

        /// <summary>
        /// Periodically updates worker areas based on timer
        /// </summary>
        private void Update()
        {
            // Structural integrity runs on the server, which may have no local player
            // (and so is never initialized through OnTerrainReady); clients never run it
            if (IsNetworkClientOnly || (!_initialized && !IsNetworkServer))
                return;
            
            _workerTimer += Time.deltaTime;
            if (_workerTimer > _workerTimerDuration)
            {
                _workerTimer = 0.0f;
                UpdateWorkerAreas();
            }
        }

        /// <summary>
        /// Assigns the player and determines which terrain they are on
        /// </summary>
        public void SetPlayer(GameObject player)
        {
            _player = player;

            if (_player == null)
                throw new Exception("Builder.SetPlayer(): Player is null");
            
            if (!TryGetTerrainAt(player.transform.position, out var terrain))
                throw new KeyNotFoundException($"Builder.SetPlayer(): No terrain found for {GetTerrainIndex(player.transform.position)}");

            ActiveTerrain = terrain;

            if (_player.TryGetComponent<PlayerBuilderInventoryBridge>(out PlayerBuilderInventoryBridge bridge))
            {
                _playerBuilderInventoryBridge = bridge;    
            }
            else throw new Exception($"Builder.SetPlayer(): PlayerBuilderInventoryBridge not found"); 
        }

        /// <summary>
        /// Sets the active terrain and caches its collider
        /// </summary>
        [Obsolete("Use ActiveTerrain instead.")]
        public void SetTerrain(Terrain t)
        {
            _activeTerrain = t;
            _activeTerrainCollider = _activeTerrain.GetComponent<Collider>();
        }

        /// <summary>
        /// Converts a world position into a terrain grid index
        /// </summary>
        private Vector2Int GetTerrainIndex(Vector3 worldPos)
        {
            // Terrain chunks are streamed at chunkCoord * chunkSize from the world origin
            // (TerrainStreamingController), so its chunk coordinates are the terrain index.
            // _worldOrigin must not be used here: it is a leftover from the fixed terrain
            // grid (-1500 in the scene), which isn't a multiple of the chunk size and mapped
            // half of every chunk to the neighbouring terrain.
            if (_streamingController == null)
                _streamingController = FindFirstObjectByType<TerrainStreamingController>();

            if (_streamingController != null)
                return _streamingController.ChunkAt(worldPos);

            return new Vector2Int(
                Mathf.FloorToInt(worldPos.x / TerrainSize),
                Mathf.FloorToInt(worldPos.z / TerrainSize));
        }

        /// <summary>
        /// Converts world position to building-area grid coordinates
        /// </summary>
        public static Vector2Int WorldToGrid(Vector3 worldPos, Vector3 terrainPosition, int buildingAreaSize)
        {
            Vector3 localPos = worldPos - terrainPosition;
            return new Vector2Int(
                Mathf.FloorToInt(localPos.x / buildingAreaSize),
                Mathf.FloorToInt(localPos.z / buildingAreaSize)
            );
        }

        /// <summary>
        /// Converts grid position back to world space
        /// </summary>
        public static Vector3 GridToWorld(Vector2Int gridPos, Vector3 terrainPosition, int buildingAreaSize)
        {
            float x = gridPos.x * buildingAreaSize + buildingAreaSize * 0.5f;
            float z = gridPos.y * buildingAreaSize + buildingAreaSize * 0.5f;
            return terrainPosition + new Vector3(x, 0f, z);
        }



        /// <summary>
        /// Returns an existing BuildingArea for the given world position,
        /// or creates and registers a new one if none exists.
        /// </summary>
        public BuildingArea GetOrCreateBuildingArea(Vector3 worldPosition)
        {
            // Determine which terrain this world position belongs to
            if (!TryGetTerrainAt(worldPosition, out var terrain))
            {
                throw new KeyNotFoundException($"Builder.GetOrCreateBuildingArea(): Terrain not found for {GetTerrainIndex(worldPosition)}");
            }
            else if (_debugEnabled)
                Debug.Log($"Builder.GetOrCreateBuildingArea(): We have a terrain {terrain.name} at world position {worldPosition}");

            // Return existing building area if one already exists at this grid position
            Vector2Int globalGrid = GetGlobalAreaGrid(worldPosition);
            if (_buildingAreasByGrid.TryGetValue(globalGrid, out var area))
            {
                if (_debugEnabled)
                    Debug.Log($"Builder.GetOrCreateBuildingArea(): <color=green>area found at {globalGrid}</color>");

                return area;
            }

            // Grid coordinates relative to this terrain
            Vector2Int gridPos = WorldToGrid(worldPosition, terrain.transform.position, BuildingAreaSize);

            if (_debugEnabled)
                Debug.Log($"Builder.GetOrCreateBuildingArea(): <color=yellow>no area at {globalGrid} (grid {gridPos} on {terrain.name})</color>");

            // Convert grid position back to world position for spawning
            Vector3 spawnPos = GridToWorld(gridPos, terrain.transform.position, BuildingAreaSize);

            if (_debugEnabled)
                Debug.Log($"Builder.GetOrCreateBuildingArea(): spawnPos={spawnPos}");

            // Create and initialize the logical BuildingArea data
            area = new BuildingArea();
            area.Position = spawnPos;
            area.DebugEnabled = _debugEnabled;
            area.Initialize(gridPos, BuildingAreaSize);
            area.Id = System.Guid.NewGuid().ToString();

            if (IsNetworkServer)
            {
                // Networked: spawn the area's NetworkObject; every client draws the outline itself
                SpawnNetworkBuildingArea(area, spawnPos);
            }
            else
            {
                // Offline: instantiate the BuildingArea visual prefab directly
                GameObject go = Instantiate(
                    _buildingAreaPrefab,
                    spawnPos,
                    Quaternion.identity,
                    this.transform
                );

                // Assign terrain reference to the outline component
                if (go.TryGetComponent<BuildingAreaOutline>(out BuildingAreaOutline outline))
                {
                    outline.Terrain = terrain;
                    outline.BuildingArea = area;
                    outline.SetValid(true);
                }
                else throw new Exception($"Builder.GetOrCreateBuildingArea(): no outline found for the building area prefab");
            }

            if (_debugEnabled)
                Debug.Log($"Builder.GetOrCreateBuildingArea(): created area with id ={area.Id}");

            // Register area for lookup by grid position and by ID
            _buildingAreasByGrid.Add(globalGrid, area);
            _buildingAreasById.Add(area.Id, area);

            return area;
        }

        /// <summary>
        /// Server: spawn the networked object for a new building area.
        /// </summary>
        private void SpawnNetworkBuildingArea(BuildingArea area, Vector3 spawnPos)
        {
            if (_networkBuildingAreaPrefab == null)
            {
                Debug.LogError("Builder: Network Building Area Prefab is not assigned; clients will not see this area");
                return;
            }

            NetworkObject nob = Instantiate(_networkBuildingAreaPrefab, spawnPos, Quaternion.identity);
            if (!nob.TryGetComponent(out NetworkBuildingArea networkArea))
            {
                Debug.LogError("Builder: Network Building Area Prefab has no NetworkBuildingArea component");
                Destroy(nob.gameObject);
                return;
            }

            networkArea.SetArea(area);
            InstanceFinder.ServerManager.Spawn(nob);
            _networkAreasById[area.Id] = networkArea;
        }

        /// <summary>
        /// Client: create the outline visual for a networked building area centred on
        /// <paramref name="center"/>. Returns null if the terrain there isn't loaded.
        /// </summary>
        public GameObject CreateAreaOutline(Vector3 center)
        {
            if (_buildingAreaPrefab == null || !TryGetTerrainAt(center, out Terrain terrain))
                return null;

            GameObject go = Instantiate(_buildingAreaPrefab, center, Quaternion.identity, this.transform);

            if (go.TryGetComponent<BuildingAreaOutline>(out BuildingAreaOutline outline))
            {
                // The outline only needs the area's position and size
                BuildingArea outlineArea = new BuildingArea();
                outlineArea.Position = center;
                outlineArea.Initialize(WorldToGrid(center, terrain.transform.position, BuildingAreaSize), BuildingAreaSize);

                outline.Terrain = terrain;
                outline.BuildingArea = outlineArea;
                outline.SetValid(true);
            }

            return go;
        }

        /// <summary>
        /// Retrieves a BuildingArea by its unique ID.
        /// </summary>
        public BuildingArea GetBuildingAreaByID(string id)
        {
            if (_buildingAreasById.TryGetValue(id, out var area))
            {
                return area;
            }
            else
            {
                throw new KeyNotFoundException($"Builder.GetBuildingAreaByID(): no area found with id {id}");
            }
        }

        /// <summary>
        /// Returns a BuildTemplate by index, or null if index is invalid.
        /// </summary>
        public BuildTemplate GetBuildItemTemplate(int index)
        {
            if (index < 0 || index >= _buildItemTemplates.Count)
                return null;

            return _buildItemTemplates[index];
        }

        /// <summary>
        /// Instantiates a transparent preview prefab for placement visualization.
        /// </summary>
        public GameObject InstantiateTransparentPrefab(int index, Vector3 position, Quaternion rotation)
        {
            if (index < 0 || index >= _buildItemTemplates.Count)
                return null;

            return Instantiate(_buildItemTemplates[index].transparentPrefab, position, rotation);
        }

        /// <summary>
        /// Debug method that spawns a 10x10 grid of building prefabs for testing.
        /// </summary>
        public void InstantiateBuildingPrefabDebug(int index, Vector3 position, Quaternion rotation)
        {
            // Networked: only the server builds; a client would create pieces nobody else sees
            if (IsNetworkClientOnly)
            {
                Debug.LogWarning("Builder.InstantiateBuildingPrefabDebug(): only the server can do this in a network session");
                return;
            }

            BuildTemplate b;
            
            if (index < 0 || index >= _buildItemTemplates.Count)
            {
                throw new Exception($"Builder.InstantiateBuildingPrefabDebug(): {index} is out of bounds.");
            }
            else b = _buildItemTemplates[index]; 

            // Spawn a grid of buildings to test spacing and alignment
            for (int i = 0; i < 10; i++)
            {
                for (int j = 0; j < 10; j++)
                {
                    Vector3 pos = new Vector3(
                        position.x + b.width * i,
                        position.y + b.height * j - j * 0.001f, // slight offset to avoid z-fighting
                        position.z
                    );

                    InstantiateBuildingPrefab(index, pos, rotation, 0);
                }
            }
        }

        /// <summary>
        /// Finds and marks constructed items within the bounds of a transparent
        /// placement prefab so they can be removed or weakened.
        /// </summary>
        /// <param name="instantiatedTransparentPrefab">
        /// The temporary placement prefab used to define the destruction area.
        /// </param>
        public void DestructConstructedItemsAtPos(GameObject instantiatedTransparentPrefab)
        {
            List<int> ids = CollectPieceIdsAt(instantiatedTransparentPrefab);
            if (ids.Count == 0)
                return;

            if (IsNetworkSession)
            {
                // The server validates and removes them; clients follow the records
                if (_playerBuilderInventoryBridge != null)
                    _playerBuilderInventoryBridge.RequestDestruct(ids.ToArray());
                else
                    Debug.LogWarning("Builder.DestructConstructedItemsAtPos(): no local player yet");
                return;
            }

            DestructConstructedItems(ids);
        }

        /// <summary>
        /// IDs of the pieces overlapping the placement preview's volume.
        /// Works on clients too: their piece copies carry the server's IDs.
        /// </summary>
        private List<int> CollectPieceIdsAt(GameObject instantiatedTransparentPrefab)
        {
            List<int> ids = new List<int>();

            // Safety check: nothing to process
            if (instantiatedTransparentPrefab == null)
            {
                if(_debugEnabled)
                    Debug.LogWarning($"DestructConstructedItemsAtPos(): instantiatedTransparentPrefab is null");
                return ids;
            }

            // Expecting the prefab to have at least one child containing a collider
            if (instantiatedTransparentPrefab.transform.childCount == 0)
            {
                if(_debugEnabled)
                    Debug.LogWarning($"DestructConstructedItemsAtPos(): instantiatedTransparentPrefab childCount is 0");
                return ids;
            }

            // Use the first child's collider as the spatial reference
            Collider collider1 = instantiatedTransparentPrefab
                .transform
                .GetChild(0)
                .GetComponent<Collider>();

            // Collect all colliders within a sphere that fully encloses the collider bounds
            Collider[] hits = Physics.OverlapSphere(
                collider1.bounds.center,
                collider1.bounds.extents.magnitude
            );

            foreach (Collider collider in hits)
            {
                if (collider.gameObject.transform.parent == null)
                    continue;

                // Constructed item behaviours are expected to live on the parent GameObject
                if (collider.gameObject.transform.parent.TryGetComponent(out ConstructedItemBehaviour cib) &&
                    !ids.Contains(cib.id))
                {
                    ids.Add(cib.id);
                }
            }

            return ids;
        }

        /// <summary>
        /// Removes the given pieces (offline, or on the server after validation).
        /// </summary>
        public void DestructConstructedItems(IEnumerable<int> ids)
        {
            foreach (int id in ids)
            {
                ConstructedItem item = GetConstructedItem(id);
                if (item == null)
                    continue;

                // Retrieve the building area this constructed item belongs to
                BuildingArea area = GetBuildingAreaByID(item.BuildingAreaId);

                // Mark the item as infinitely distant so it is guaranteed to be removed
                item.Distance = float.PositiveInfinity;

                // Visual feedback before the item is destroyed
                if (item.ConstructedItemBehaviour != null)
                    item.ConstructedItemBehaviour.SetMeshColor(Color.magenta);

                // Remove the constructed item from its building area
                area.RemoveWeakConstructedItem(item);
            }
        }

        /// <summary>
        /// Legacy / unreliable grounding check.
        /// Uses collider bounds intersection with the active terrain collider.
        /// </summary>
        /// <remarks>
        /// This method is discouraged because terrain collider bounds
        /// often extend to the highest terrain point, causing false positives.
        /// </remarks>
        [Obsolete("Use IsTouchingTerrain or IsTouchingAnyTerrain instead.")]
        public bool IsGrounded(Collider collider)
        {
            Debug.LogWarning("Builder.IsGrounded(): do not use this method");

            // Terrain collider bounds include the highest terrain point,
            // so many objects below that height will appear "grounded".
            if (_activeTerrainCollider)
            {
                return collider.bounds.Intersects(_activeTerrainCollider.bounds);
            }
            else
            {
                Debug.LogWarning("Builder.IsGrounded(): terrainCollider == null");
                return false;
            }
        }

        /// <summary>
        /// Checks whether a collider is touching the currently active terrain
        /// by comparing its lowest Y bound to the terrain height.
        /// </summary>
        public bool IsTouchingTerrain(Collider col)
        {
            if (_activeTerrain)
            {
                // Use the collider's center position for terrain sampling
                Vector3 pos = col.bounds.center;

                // Sample terrain height in world space
                float terrainHeight =
                    ActiveTerrain.SampleHeight(pos) + ActiveTerrain.transform.position.y;

                // Touching if the collider bottom is at or below the terrain surface
                return col.bounds.min.y <= terrainHeight;
            }
            else
            {
                Debug.LogWarning("Builder.IsTouchingTerrain(): _terrain == null");
                return false;
            }
        }

        /// <summary>
        /// Checks whether a collider is touching the terrain at its current world position,
        /// taking terrain tiling into account.
        /// </summary>
        public bool IsTouchingAnyTerrain(Collider col)
        {
            Vector3 pos = col.bounds.center;

            // Determine which terrain tile this position belongs to
            if (!TryGetTerrainAt(pos, out var terrain))
                throw new KeyNotFoundException($"Builder.IsTouchingAnyTerrain(): No terrain found for {GetTerrainIndex(pos)}");

            if (terrain != null)
            {
                // Sample terrain height for the specific terrain tile
                float terrainHeight =
                    terrain.SampleHeight(pos) + terrain.transform.position.y;

                // Collider is touching terrain if its bottom is below the surface
                return col.bounds.min.y <= terrainHeight;
            }

            return false;
        }

        /// <summary>
        /// Checks whether a collider overlaps with any collider on a buildable layer.
        /// Trigger colliders are ignored.
        /// </summary>
        public bool IsTouchingBuildableLayer(Collider col)
        {
            return Physics.OverlapBox(
                col.bounds.center,
                col.bounds.extents,
                col.transform.rotation,
                _buildableLayers,
                QueryTriggerInteraction.Ignore
            ).Length > 0;
        }

        
        /// <summary>
        /// Instantiates a building prefab, registers it in a BuildingArea,
        /// detects grounding, snap points, neighbours, and updates distances.
        /// </summary>
        public GameObject InstantiateBuildingPrefab(int index, Vector3 position, Quaternion rotation, int snapPointIndex)
        {
            // Validate template index. No local player is needed: on the server this also
            // builds for remote players.
            if (index < 0 || index >= _buildItemTemplates.Count)
                throw new Exception($"Builder.InstantiateBuildingPrefab(): invalid parameter {index}");

            // Instantiate the building prefab under this Builder transform
            GameObject builtItem = Instantiate(
                _buildItemTemplates[index].buildPrefab,
                position,
                rotation,
                this.transform
            );

            // Rename the mesh child for clarity in the hierarchy
            builtItem.transform.GetChild(0).gameObject.name = builtItem.name + " (mesh)";

            // Retrieve the collider from the mesh child
            if(!builtItem.transform.GetChild(0).TryGetComponent<Collider>(out Collider collider1))
                throw new Exception($"Builder.InstantiateBuildingPrefab(): no Collider on {builtItem.name}'s Child Object 0");

            // Collider collider1 = builtItem.transform.GetChild(0).GetComponent<Collider>();

            // Debug grounding checks
            if (_debugEnabled)
            {
                Debug.Log($"Builder.InstantiateBuildingPrefab(): Is touching buildable layer {IsTouchingBuildableLayer(collider1)}");
                Debug.Log($"Builder.InstantiateBuildingPrefab(): Is touching terrain {IsTouchingAnyTerrain(collider1)}");
            }

            // Determine whether the object is grounded
            bool grounded = IsTouchingAnyTerrain(collider1) || IsTouchingBuildableLayer(collider1);

            if (grounded && _debugEnabled)
                Debug.Log($"Builder.InstantiateBuildingPrefab(): <color=yellow>{builtItem.name} is grounded!</color>");

            ConstructedItem constructedItem;
            int cid = -1;

            // Get or create the BuildingArea for this position
            BuildingArea buildingArea = GetOrCreateBuildingArea(position);
            if (buildingArea != null)
            {
                // Register constructed item in the building area
                constructedItem = buildingArea.AddBuildItem(
                    _buildItemTemplates[index],
                    builtItem,
                    grounded,
                    _debugEnabled
                );

                cid = constructedItem.Id;

                // Rename instance to include its unique ID
                builtItem.name = _buildItemTemplates[index].name + " (" + constructedItem.Id + ")";

                // Store snap point index used
                constructedItem.SnapPointIndex = snapPointIndex;

                // Apply snap point offsets to all submeshes if applicable
                ApplySnapPointOffsets(constructedItem.ConstructedItemBehaviour, index, snapPointIndex);

                // Register globally
                _allConstructedItems.Add(constructedItem.Id, constructedItem);
            }
            else
            {
                // Safety check for terrain mismatch or missing building area
                Vector2Int tpos = GetTerrainIndex(position);

                if (TryGetTerrainAt(position, out var terrain))
                {
                    if (terrain == _activeTerrain)
                    {
                        throw new Exception($"Builder.InstantiateBuildingPrefab(): no BuildingArea created for {tpos}");                        
                    }
                    else
                    {
                        throw new Exception($"Builder.InstantiateBuildingPrefab(): we crossed into another terrain {terrain.name} for {tpos}");
                    }
                }

                throw new Exception($"Builder.InstantiateBuildingPrefab(): no terrain found for {tpos}");
            }

            // --------------------------------------------------
            // Neighbour detection
            // --------------------------------------------------

            if(_debugEnabled)
                Debug.Log($"Builder.InstantiateBuildingPrefab(): Neighbour detection");
            
            // Find nearby colliders within build radius
            Collider[] hitColliders = Physics.OverlapSphere(
                position,
                _buildItemTemplates[index].radius,
                _buildingBlockLayers,
                QueryTriggerInteraction.Ignore
            );

            for (int i = 0; i < hitColliders.Length; i++)
            {
                Collider collider2 = hitColliders[i];

                // Check bounding-box intersection
                if (collider1.bounds.Intersects(collider2.bounds))
                {
                    // Attempt to retrieve neighbour constructed item

                    if (collider2.gameObject.transform.parent != null)
                    {
                        if (collider2.gameObject.transform.parent.TryGetComponent<ConstructedItemBehaviour>(
                                out ConstructedItemBehaviour cib))
                        {
                            if (cib.id != cid)
                            {
                                // Register mutual neighbours
                                constructedItem.AddNeighbour(cib.constructedItem.Id);
                                cib.constructedItem.AddNeighbour(constructedItem.Id);

                                // Update distance propagation if not grounded
                                if (!constructedItem.Grounded)
                                {
                                    float distance = cib.constructedItem.Distance + constructedItem.Strength;
                                    if (distance < constructedItem.Distance)
                                        constructedItem.Distance = distance;
                                }
                            }
                        }
                    }
                }
            }

            // Visualize distance if debugging
            if (_debugEnabled)
                buildingArea.ShowDistance(constructedItem);

            // Networked: record the piece so every client builds its own copy
            if (IsNetworkServer && _networkAreasById.TryGetValue(buildingArea.Id, out NetworkBuildingArea networkArea))
            {
                networkArea.AddPiece(new PieceRecord
                {
                    Id = constructedItem.Id,
                    TemplateId = index,
                    Position = position,
                    Rotation = rotation,
                    SnapIndex = snapPointIndex
                });
            }

            // Pathfinding must see the new obstacle
            if (TerrainGraphIntegration.Instance != null)
                TerrainGraphIntegration.Instance.InvalidateObstacleCache(position, ObstacleInvalidationRadius);

            return builtItem;
        }

        /// <summary>
        /// Shifts a piece's submeshes by the snap point offsets used to place it.
        /// Shared by the server's pieces and the clients' copies so both line up.
        /// </summary>
        private void ApplySnapPointOffsets(ConstructedItemBehaviour cib, int templateIndex, int snapPointIndex)
        {
            if (cib == null || snapPointIndex <= 0)
                return;

            List<GameObject> submeshes = cib.MeshObjects;
            BuildTemplate bt = _buildItemTemplates[templateIndex];

            for (int i = 0; i < snapPointIndex; i++)
            {
                if (i < bt.snapPoints.Count)
                {
                    for (int j = 0; j < submeshes.Count; j++)
                    {
                        submeshes[j].transform.localPosition += bt.snapPoints[i];
                    }
                }
            }
        }

        /// <summary>
        /// Client: build a local copy of a piece the server has built. It has no
        /// ConstructedItem; structural integrity runs on the server.
        /// </summary>
        public GameObject InstantiatePieceCopy(PieceRecord record, Transform parent)
        {
            BuildTemplate template = GetBuildItemTemplate(record.TemplateId);
            if (template == null)
            {
                Debug.LogError($"Builder.InstantiatePieceCopy(): unknown template {record.TemplateId} for piece {record.Id}");
                return null;
            }

            GameObject copy = Instantiate(template.buildPrefab, record.Position, record.Rotation, parent);
            copy.name = template.name + " (" + record.Id + ")";

            if (copy.transform.childCount > 0)
                copy.transform.GetChild(0).gameObject.name = copy.name + " (mesh)";

            if (copy.TryGetComponent(out ConstructedItemBehaviour cib))
            {
                cib.id = record.Id;
                cib.DebugEnabled = _debugEnabled;
                ApplySnapPointOffsets(cib, record.TemplateId, record.SnapIndex);
            }

            return copy;
        }

        /// <summary>
        /// Called by BuildingArea whenever a constructed item is removed (destroyed or collapsed).
        /// </summary>
        public void OnConstructedItemRemoved(ConstructedItem item)
        {
            if (item == null)
                return;

            _allConstructedItems.Remove(item.Id);

            // Networked: drop the record so clients destroy their copy
            if (IsNetworkServer && item.BuildingAreaId != null &&
                _networkAreasById.TryGetValue(item.BuildingAreaId, out NetworkBuildingArea networkArea))
            {
                networkArea.RemovePiece(item.Id);
            }

            if (TerrainGraphIntegration.Instance != null)
                TerrainGraphIntegration.Instance.InvalidateObstacleCache(item.Position, ObstacleInvalidationRadius);
        }

        /// <summary>
        /// Server: the given pieces of an area are about to collapse. Sent to the clients
        /// before the records are removed, so they can play the falling effect.
        /// </summary>
        public void NotifyCollapse(string areaId, int[] pieceIds)
        {
            if (IsNetworkServer && areaId != null &&
                _networkAreasById.TryGetValue(areaId, out NetworkBuildingArea networkArea))
            {
                networkArea.ObserversCollapse(pieceIds);
            }
        }

        // ==================================================================
        // Build and destroy requests
        // ==================================================================

        /// <summary>
        /// Build a piece for the local player. In a network session this asks the server
        /// (which validates it and consumes inventory); offline it builds directly.
        /// </summary>
        public void RequestBuild(int templateId, Vector3 position, Quaternion rotation, int snapPointIndex)
        {
            if (IsNetworkSession)
            {
                if (_playerBuilderInventoryBridge == null)
                {
                    Debug.LogWarning("Builder.RequestBuild(): no local player yet");
                    return;
                }

                _playerBuilderInventoryBridge.RequestBuild(templateId, position, rotation, snapPointIndex);
                return;
            }

            InstantiateBuildingPrefab(templateId, position, rotation, snapPointIndex);
            _playerBuilderInventoryBridge.ConsumeItemAmount(templateId, 1);
        }

        /// <summary>
        /// Server check: is <paramref name="position"/> inside a non-buildable area?
        /// Position based, because the server has no placement preview to overlap with.
        /// </summary>
        public bool IsPositionInNonBuildableArea(Vector3 position)
        {
            if (_nonBuildableAreas == null)
                return false;

            foreach (NonBuildableArea nba in _nonBuildableAreas)
            {
                if (nba != null && Vector3.Distance(nba.transform.position, position) <= nba.Range)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Server check: is there already a piece of this template at (almost) the same
        /// position and rotation? Pieces are meant to touch, so this only rejects duplicates.
        /// </summary>
        public bool IsDuplicatePiece(int templateId, Vector3 position, Quaternion rotation)
        {
            foreach (ConstructedItem item in _allConstructedItems.Values)
            {
                if (item.BuildTemplateId == templateId &&
                    (item.Position - position).sqrMagnitude < 0.0001f &&
                    Quaternion.Angle(item.Rotation, rotation) < 1f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether a position overlaps any defined non-buildable area.
        /// </summary>
        public bool IsInNonBuildableArea(Vector3 position)
        {
            bool result = false;
            int i = 0;

            // Iterate through all non-buildable zones
            while (i < _nonBuildableAreas.Count && !result)
            {
                NonBuildableArea nba = _nonBuildableAreas[i];

                // Check for overlaps with transparent block layer
                Collider[] hitColliders = Physics.OverlapSphere(
                    nba.transform.position,
                    nba.Range,
                    _transparentBlockLayer,
                    QueryTriggerInteraction.Ignore
                );

                result = hitColliders.Length > 0;
                i++;
            }

            return result;
        }

        // Terrain edit sizes (radius in metres, depth in metres; negative depth raises)
        private const float DepressRadius = 3.0f;
        private const float DepressDepth  = 1f;
        private const float RaiseRadius   = 10.0f;
        private const float RaiseDepth    = -2f;

        /// <summary>
        /// Pushes terrain downward at the given position.
        /// In a network session this is a request to the server (see WorldState).
        /// </summary>
        public void DepressTerrainAtPosition(Vector3 position)
        {
            if (_debugEnabled)
                Debug.Log($"DepressTerrainAtPosition {position}");

            EditTerrain(position, DepressRadius, DepressDepth);
        }

        /// <summary>
        /// Raises terrain at the given position.
        /// In a network session this is a request to the server (see WorldState).
        /// </summary>
        public void RaiseTerrainAtPosition(Vector3 position)
        {
            if (_debugEnabled)
                Debug.Log($"RaiseTerrainAtPosition {position}");

            EditTerrain(position, RaiseRadius, RaiseDepth);
        }

        private void EditTerrain(Vector3 position, float radius, float depth)
        {
            // Networked: the server validates the edit and every machine applies it
            if (WorldState.Instance != null)
            {
                WorldState.Instance.RequestTerrainEdit(position, radius, depth);
                return;
            }

            // Offline: edit the active terrain directly
            TerrainUtils.TerrainUtils.PushDownTerrain(_activeTerrain, position, radius, depth);
        }

        /// <summary>
        /// Saves the world (pieces, terrain edits, seed) through WorldState. Server only.
        /// </summary>
        public void SaveAllBuildingAreas()
        {
            // Networked: only the server owns the world
            if (IsNetworkClientOnly)
            {
                Debug.LogWarning("Builder.SaveAllBuildingAreas(): only the server can do this in a network session");
                return;
            }

            if (WorldState.Instance == null)
            {
                Debug.LogWarning("Builder.SaveAllBuildingAreas(): no WorldState; saving needs a network session");
                return;
            }

            WorldState.Instance.SaveWorld();
        }

        /// <summary>
        /// Loads the saved world into the current (empty) world through WorldState. Server only.
        /// </summary>
        public void LoadBuildingArea()
        {
            // Networked: only the server builds; a client would create pieces nobody else sees
            if (IsNetworkClientOnly)
            {
                Debug.LogWarning("Builder.LoadBuildingArea(): only the server can do this in a network session");
                return;
            }

            if (WorldState.Instance == null)
            {
                Debug.LogWarning("Builder.LoadBuildingArea(): no WorldState; loading needs a network session");
                return;
            }

            WorldState.Instance.LoadWorld();
        }

        // ==================================================================
        // Persistence (server)
        // ==================================================================

        // Saved pieces waiting for their terrain chunk to load, by chunk, in build order
        private readonly Dictionary<Vector2Int, List<SavedPiece>> _pendingSavedPieces =
            new Dictionary<Vector2Int, List<SavedPiece>>();
        private bool _listeningForChunkLoads;

        /// <summary>Number of pieces that exist on the server.</summary>
        public int PieceCount => _allConstructedItems.Count;

        /// <summary>Number of saved pieces still waiting for their chunk to load.</summary>
        public int PendingSavedPieceCount
        {
            get
            {
                int count = 0;
                foreach (List<SavedPiece> pieces in _pendingSavedPieces.Values)
                    count += pieces.Count;
                return count;
            }
        }

        /// <summary>
        /// Every piece on the server, in build order (by ID), for the world save.
        /// </summary>
        public List<SavedPiece> GetSavedPieces()
        {
            List<ConstructedItem> items = new List<ConstructedItem>(_allConstructedItems.Values);
            items.Sort((a, b) => a.Id.CompareTo(b.Id));

            List<SavedPiece> pieces = new List<SavedPiece>(items.Count);
            foreach (ConstructedItem item in items)
            {
                pieces.Add(new SavedPiece
                {
                    TemplateId = item.BuildTemplateId,
                    Position = item.Position,
                    Rotation = item.Rotation,
                    SnapIndex = item.SnapPointIndex
                });
            }

            // Pieces waiting for their chunk are part of the world too
            foreach (List<SavedPiece> pending in _pendingSavedPieces.Values)
                pieces.AddRange(pending);

            return pieces;
        }

        /// <summary>
        /// Server: rebuild saved pieces. Pieces on loaded chunks are built now; the rest when
        /// their chunk loads (they need its terrain for grounding). The NetworkBuildingArea
        /// records then reach clients like any other build, late joiners included.
        /// </summary>
        public void QueueSavedPieces(List<SavedPiece> pieces)
        {
            if (pieces == null || pieces.Count == 0)
                return;

            if (_streamingController == null)
                _streamingController = FindFirstObjectByType<TerrainStreamingController>();

            foreach (SavedPiece piece in pieces)
            {
                Vector2Int chunk = GetTerrainIndex(piece.Position);
                if (!_pendingSavedPieces.TryGetValue(chunk, out List<SavedPiece> list))
                {
                    list = new List<SavedPiece>();
                    _pendingSavedPieces.Add(chunk, list);
                }
                list.Add(piece);
            }

            if (!_listeningForChunkLoads && _streamingController != null)
            {
                _streamingController.onChunkLoadComplete.AddListener(BuildPendingSavedPieces);
                _listeningForChunkLoads = true;
            }

            // Chunks that are already loaded don't get another load event
            foreach (Vector2Int chunk in new List<Vector2Int>(_pendingSavedPieces.Keys))
            {
                if (_streamingController != null && _streamingController.TryGetLoadedTerrain(chunk, out _))
                    BuildPendingSavedPieces(chunk);
            }
        }

        /// <summary>
        /// Builds the saved pieces of a chunk that has finished loading, in build order.
        /// </summary>
        private void BuildPendingSavedPieces(Vector2Int chunk)
        {
            if (!_pendingSavedPieces.TryGetValue(chunk, out List<SavedPiece> pieces))
                return;

            _pendingSavedPieces.Remove(chunk);

            int built = 0;
            foreach (SavedPiece piece in pieces)
            {
                try
                {
                    InstantiateBuildingPrefab(piece.TemplateId, piece.Position, piece.Rotation, piece.SnapIndex);
                    built++;
                }
                catch (Exception e)
                {
                    Debug.LogError($"Builder: could not rebuild a saved piece (template {piece.TemplateId}) at {piece.Position}: {e.Message}");
                }
            }

            if (_debugEnabled)
                Debug.Log($"Builder: rebuilt {built} of {pieces.Count} saved piece(s) on chunk {chunk}");
        }

        /// <summary>
        /// Checks whether a magnetic snap point is close to any magnetic collider.
        /// </summary>
        public bool IsMagneticPointClose(GameObject prefabT, out Vector3 distanceClosestPoint)
        {
            // Validate hierarchy
            if (prefabT.transform.childCount > 0)
            {
                GameObject child = prefabT.transform.GetChild(0).gameObject;

                int c = child.transform.childCount;
                for (int i = 0; i < c; i++)
                {
                    GameObject go = child.transform.GetChild(i).gameObject;

                    // Check magnetic overlap
                    Collider[] hitColliders = Physics.OverlapSphere(
                        go.transform.position,
                        _magneticRadius,
                        _magneticLayer,
                        QueryTriggerInteraction.Collide
                    );

                    if (hitColliders.Length > 0)
                    {
                        distanceClosestPoint =
                            go.transform.position - hitColliders[0].transform.position;
                        return true;
                    }
                }
            }

            distanceClosestPoint = Vector3.zero;
            return false;
        }
    }
}