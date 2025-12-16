using System;
using System.Collections.Generic;
using UnityEngine;
using Pathfinding;
using Grid;
using Platforms;

namespace Navigation
{
    /// <summary>
    /// Central manager for A* Pathfinding integration using RecastGraph.
    /// Generates a NavMesh from scene geometry for true free movement.
    /// 
    /// Architecture:
    /// - Subscribes to GamePlatform static events for platform lifecycle
    /// - Triggers local tile-based graph updates when platforms change
    /// - Provides position validation methods for other systems
    /// - Only this manager should call A* Pathfinding systems directly
    /// 
    /// RecastGraph Features:
    /// - True NavMesh with free movement (not grid-based)
    /// - Multi-level support (overhangs, ramps, tunnels)
    /// - Tile-based for efficient local updates
    /// - Automatic mesh generation from scene geometry
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AstarPath))]
    public class PathfindingManager : MonoBehaviour
    {
        #region Singleton
        
        private static PathfindingManager _instance;
        public static PathfindingManager Instance
        {
            get
            {
                if (!_instance)
                {
                    _instance = FindFirstObjectByType<PathfindingManager>();
                }
                return _instance;
            }
        }
        
        #endregion
        
        #region Events
        
        /// <summary>Fired when the pathfinding graph is ready for use.</summary>
        public event Action GraphReady;
        
        /// <summary>Fired when a graph update completes. Args: (bounds updated)</summary>
        public event Action<Bounds> GraphUpdated;
        
        /// <summary>Fired when graph scanning starts.</summary>
        public event Action ScanStarted;
        
        /// <summary>Fired when graph scanning completes.</summary>
        public event Action ScanCompleted;
        
        #endregion
        
        #region Configuration - Graph Bounds
        
        [Header("Graph Bounds")]
        [Tooltip("Center position of the navmesh in world space.")]
        [SerializeField] private Vector3 graphCenter = new Vector3(500, 0, 500);
        
        [Tooltip("Size of the navmesh area in world units (X, Y, Z).")]
        [SerializeField] private Vector3 graphSize = new Vector3(1000, 50, 1000);
        
        [Tooltip("Rotation of the navmesh bounds.")]
        [SerializeField] private Vector3 graphRotation = Vector3.zero;
        
        #endregion
        
        #region Configuration - Agent Settings
        
        [Header("Agent Settings")]
        [Tooltip("Agent radius for navigation. Agents cannot get closer to walls than this.")]
        [SerializeField] private float agentRadius = 0.3f;
        
        [Tooltip("Agent height. Areas with lower ceilings will be excluded.")]
        [SerializeField] private float agentHeight = 1.5f;
        
        [Tooltip("Maximum height an agent can step up (stairs, curbs). Standard stairs ~0.3-0.4m.")]
        [SerializeField] private float maxClimb = 0.4f;
        
        [Tooltip("Maximum slope angle for walkable surfaces (degrees).")]
        [SerializeField] private float maxSlope = 45f;
        
        #endregion
        
        #region Configuration - Layers
        
        [Header("Layer Configuration")]
        [Tooltip("Layers to include when scanning for walkable geometry (platforms, floors, terrain).")]
        [SerializeField] private LayerMask walkableLayers = ~0;
        
        [Tooltip("Layers that should NEVER be walkable, even if in walkableLayers.")]
        [SerializeField] private LayerMask unwalkableLayers = 0;
        
        #endregion
        
        #region Configuration - Quality & Performance
        
        [Header("Quality Settings")]
        [Tooltip("Cell size for navmesh generation. Smaller = more precise but slower/more memory.\n" +
                 "Recommended: 0.1-0.3 for detailed scenes, 0.3-0.5 for large open areas.")]
        [Range(0.05f, 1f)]
        [SerializeField] private float cellSize = 0.3f;
        
        // Note: Cell height is configured directly on the RecastGraph in the Inspector
        // It's typically set to 0.5-1x the cellSize value
        
        [Tooltip("Tile size in voxels. Larger tiles = faster initial scan, slower updates.\n" +
                 "Smaller tiles = slower initial scan, faster local updates.\n" +
                 "Recommended: 64-256 for frequently updated scenes.")]
        [Range(16, 512)]
        [SerializeField] private int tileSize = 128;
        
        [Tooltip("Minimum region area (in square world units). Removes small isolated walkable areas.")]
        [SerializeField] private float minRegionArea = 1f;
        
        [Header("Performance Settings")]
        [Tooltip("Use multi-threading for graph scanning (highly recommended).")]
        [SerializeField] private bool useMultithreading = true;
        
        [Tooltip("Maximum tiles to update per frame when doing async updates.")]
        [Range(1, 50)]
        [SerializeField] private int maxTilesPerFrame = 10;
        
        #endregion
        
        #region Configuration - Updates
        
        [Header("Dynamic Update Settings")]
        [Tooltip("Extra padding around platform bounds when updating graph (world units).")]
        [SerializeField] private float updatePadding = 0.5f;
        
        [Tooltip("Delay before processing queued graph updates (seconds). Batches rapid changes.")]
        [SerializeField] private float updateDelay = 0.1f;
        
        [Tooltip("Minimum time between graph updates (seconds). Prevents update spam.")]
        [SerializeField] private float updateCooldown = 0.2f;
        
        [Tooltip("If true, updates during platform movement are throttled for performance.")]
        [SerializeField] private bool throttleMovementUpdates = true;
        
        [Tooltip("Interval for updates during platform movement (seconds).")]
        [SerializeField] private float movementUpdateInterval = 0.5f;
        
        #endregion
        
        #region Configuration - References
        
        [Header("References")]
        [Tooltip("Reference to WorldGrid for coordinate transforms.")]
        [SerializeField] private WorldGrid worldGrid;
        
        #endregion
        
        #region Configuration - Debug
        
        [Header("Debug")]
        [SerializeField] private bool debugLogs = true;
        [SerializeField] private bool debugDrawUpdates = false;
        [SerializeField] private bool debugDrawBounds = false;
        
        #endregion
        
        #region Public Properties
        
        public bool IsReady => _isReady;
        public AstarPath AstarPath => _astarPath;
        public RecastGraph RecastGraph => _recastGraph;
        
        // Agent settings (read-only)
        public float AgentRadius => agentRadius;
        public float AgentHeight => agentHeight;
        public float MaxClimb => maxClimb;
        public float MaxSlope => maxSlope;
        
        // Quality settings (read-only)
        public float CellSize => cellSize;
        public int TileSize => tileSize;
        
        #endregion
        
        #region Private State
        
        private AstarPath _astarPath;
        private RecastGraph _recastGraph;
        private bool _isReady;
        
        // Update batching
        private readonly HashSet<GamePlatform> _pendingUpdates = new();
        private readonly HashSet<Bounds> _pendingBoundsUpdates = new();
        private float _lastUpdateRequest;
        private float _lastUpdateProcessed;
        private bool _updateScheduled;
        
        // Movement update throttling
        private float _lastMovementUpdate;
        
        // Platform tracking
        private readonly HashSet<GamePlatform> _registeredPlatforms = new();
        
        // Previous bounds tracking (for move operations)
        private readonly Dictionary<GamePlatform, Bounds> _previousBounds = new();
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning("[PathfindingManager] Duplicate instance destroyed.");
                Destroy(gameObject);
                return;
            }
            _instance = this;
            
            _astarPath = GetComponent<AstarPath>();
            if (!_astarPath)
            {
                Debug.LogError("[PathfindingManager] AstarPath component not found!");
                enabled = false;
                return;
            }
            
            // Find WorldGrid if not assigned
            if (!worldGrid)
            {
                worldGrid = FindFirstObjectByType<WorldGrid>();
            }
        }
        
        private void OnEnable()
        {
            // Subscribe to platform events
            GamePlatform.Created += OnPlatformCreated;
            GamePlatform.Destroyed += OnPlatformDestroyed;
        }
        
        private void OnDisable()
        {
            // Unsubscribe from platform events
            GamePlatform.Created -= OnPlatformCreated;
            GamePlatform.Destroyed -= OnPlatformDestroyed;
            
            // Unsubscribe from individual platform events
            foreach (var platform in _registeredPlatforms)
            {
                if (platform)
                {
                    UnsubscribeFromPlatform(platform);
                }
            }
            _registeredPlatforms.Clear();
            _previousBounds.Clear();
        }
        
        private void Start()
        {
            InitializeGraph();
        }
        
        private void Update()
        {
            // Process batched updates
            if (_updateScheduled && Time.time - _lastUpdateRequest >= updateDelay)
            {
                // Check cooldown
                if (Time.time - _lastUpdateProcessed >= updateCooldown)
                {
                    ProcessPendingUpdates();
                }
            }
        }
        
        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
        
        #endregion
        
        #region Graph Initialization
        
        private void InitializeGraph()
        {
            if (debugLogs) Debug.Log("[PathfindingManager] Initializing RecastGraph...");
            
            // Check if RecastGraph already exists
            if (_astarPath.data.graphs != null && _astarPath.data.graphs.Length > 0)
            {
                foreach (var graph in _astarPath.data.graphs)
                {
                    if (graph is RecastGraph existingGraph)
                    {
                        _recastGraph = existingGraph;
                        if (debugLogs) Debug.Log("[PathfindingManager] Using existing RecastGraph from scene.");
                        ConfigureExistingGraph();
                        return;
                    }
                }
            }
            
            // Create new RecastGraph
            CreateNewGraph();
        }
        
        private void CreateNewGraph()
        {
            if (debugLogs) Debug.Log("[PathfindingManager] Creating new RecastGraph...");
            
            _recastGraph = _astarPath.data.AddGraph(typeof(RecastGraph)) as RecastGraph;
            if (_recastGraph == null)
            {
                Debug.LogError("[PathfindingManager] Failed to create RecastGraph!");
                return;
            }
            
            ConfigureGraph();
            
            // Initial scan
            ScanGraph();
        }
        
        private void ConfigureExistingGraph()
        {
            // Apply runtime settings to existing graph
            ApplyGraphSettings(_recastGraph);
            
            _isReady = true;
            GraphReady?.Invoke();
            
            if (debugLogs) Debug.Log("[PathfindingManager] RecastGraph ready (existing).");
        }
        
        private void ConfigureGraph()
        {
            ApplyGraphSettings(_recastGraph);
            
            if (debugLogs)
            {
                Debug.Log($"[PathfindingManager] RecastGraph configured:\n" +
                          $"  Bounds: {graphSize} at {graphCenter}\n" +
                          $"  Agent: radius={agentRadius}m, height={agentHeight}m, climb={maxClimb}m\n" +
                          $"  Quality: cellSize={cellSize}m, tileSize={tileSize}\n" +
                          $"  Max Slope: {maxSlope}°");
            }
        }
        
        private void ApplyGraphSettings(RecastGraph graph)
        {
            // === Bounds ===
            graph.forcedBoundsCenter = graphCenter;
            graph.forcedBoundsSize = graphSize;
            graph.rotation = graphRotation;
            
            // === Agent ===
            graph.characterRadius = agentRadius;
            graph.walkableHeight = agentHeight;
            graph.walkableClimb = maxClimb;
            graph.maxSlope = maxSlope;
            
            // === Voxelization Quality ===
            graph.cellSize = cellSize;
            // Note: cellHeight is typically derived from cellSize in A* Pro 5.4
            // It can be configured in the Inspector on the RecastGraph component
            graph.useTiles = true;
            graph.editorTileSize = tileSize;
            
            // === Region Settings ===
            graph.minRegionSize = minRegionArea;
            
            // === Layers ===
            graph.mask = walkableLayers;
            // Note: unwalkableLayers should be excluded from walkableLayers by user
            
            // === Performance ===
            // Multi-threading is handled by AstarPath settings
        }
        
        /// <summary>
        /// Perform initial graph scan.
        /// </summary>
        public void ScanGraph()
        {
            if (debugLogs) Debug.Log("[PathfindingManager] Scanning RecastGraph...");
            
            ScanStarted?.Invoke();
            
            _astarPath.Scan(_recastGraph);
            
            _isReady = true;
            ScanCompleted?.Invoke();
            GraphReady?.Invoke();
            
            if (debugLogs) 
            {
                int tileCount = _recastGraph.tileXCount * _recastGraph.tileZCount;
                Debug.Log($"[PathfindingManager] RecastGraph scan complete. Tiles: {tileCount}");
            }
        }
        
        /// <summary>
        /// Rescan the entire graph. Use sparingly - prefer local updates.
        /// </summary>
        public void RescanGraph()
        {
            if (debugLogs) Debug.Log("[PathfindingManager] Full rescan requested...");
            ScanGraph();
        }
        
        #endregion
        
        #region Platform Event Handlers
        
        private void OnPlatformCreated(GamePlatform platform)
        {
            if (!platform || _registeredPlatforms.Contains(platform)) return;
            
            SubscribeToPlatform(platform);
            _registeredPlatforms.Add(platform);
            
            // Store initial bounds
            _previousBounds[platform] = GetPlatformBounds(platform);
            
            // Queue update for the new platform area
            QueuePlatformUpdate(platform);
            
            if (debugLogs) Debug.Log($"[PathfindingManager] Platform registered: {platform.name}");
        }
        
        private void OnPlatformDestroyed(GamePlatform platform)
        {
            if (!platform) return;
            
            // Queue update for the area this platform occupied
            if (_previousBounds.TryGetValue(platform, out Bounds bounds))
            {
                QueueBoundsUpdate(bounds);
                _previousBounds.Remove(platform);
            }
            else
            {
                QueueBoundsUpdate(GetPlatformBounds(platform));
            }
            
            UnsubscribeFromPlatform(platform);
            _registeredPlatforms.Remove(platform);
            
            if (debugLogs) Debug.Log($"[PathfindingManager] Platform unregistered: {platform.name}");
        }
        
        private void SubscribeToPlatform(GamePlatform platform)
        {
            platform.Placed += OnPlatformPlaced;
            platform.PickedUp += OnPlatformPickedUp;
            platform.HasMoved += OnPlatformMoved;
        }
        
        private void UnsubscribeFromPlatform(GamePlatform platform)
        {
            platform.Placed -= OnPlatformPlaced;
            platform.PickedUp -= OnPlatformPickedUp;
            platform.HasMoved -= OnPlatformMoved;
        }
        
        private void OnPlatformPlaced(GamePlatform platform)
        {
            if (debugLogs) Debug.Log($"[PathfindingManager] Platform placed: {platform.name}");
            
            // Update both old and new positions
            if (_previousBounds.TryGetValue(platform, out Bounds oldBounds))
            {
                QueueBoundsUpdate(oldBounds);
            }
            
            var newBounds = GetPlatformBounds(platform);
            QueueBoundsUpdate(newBounds);
            _previousBounds[platform] = newBounds;
        }
        
        private void OnPlatformPickedUp(GamePlatform platform)
        {
            if (debugLogs) Debug.Log($"[PathfindingManager] Platform picked up: {platform.name}");
            
            // Store current bounds before it moves
            _previousBounds[platform] = GetPlatformBounds(platform);
            
            // Queue update for where it was
            QueueBoundsUpdate(_previousBounds[platform]);
        }
        
        private void OnPlatformMoved(GamePlatform platform)
        {
            // Throttle updates during movement for performance
            if (throttleMovementUpdates)
            {
                if (Time.time - _lastMovementUpdate < movementUpdateInterval)
                {
                    return;
                }
                _lastMovementUpdate = Time.time;
            }
            
            // Update both old position (now empty) and new position
            if (_previousBounds.TryGetValue(platform, out Bounds oldBounds))
            {
                QueueBoundsUpdate(oldBounds);
            }
            
            var newBounds = GetPlatformBounds(platform);
            QueueBoundsUpdate(newBounds);
            _previousBounds[platform] = newBounds;
        }
        
        #endregion
        
        #region Graph Updates
        
        /// <summary>
        /// Queue a platform for graph update (batched).
        /// </summary>
        private void QueuePlatformUpdate(GamePlatform platform)
        {
            if (!platform) return;
            
            _pendingUpdates.Add(platform);
            _lastUpdateRequest = Time.time;
            _updateScheduled = true;
        }
        
        /// <summary>
        /// Queue a bounds area for graph update.
        /// </summary>
        public void QueueBoundsUpdate(Bounds bounds)
        {
            if (!_isReady) return;
            
            // Expand bounds for safety
            bounds.Expand(updatePadding * 2f);
            
            _pendingBoundsUpdates.Add(bounds);
            _lastUpdateRequest = Time.time;
            _updateScheduled = true;
            
            if (debugDrawUpdates)
            {
                DebugDrawBounds(bounds, Color.yellow, 2f);
            }
        }
        
        /// <summary>
        /// Queue a bounds area for graph update (public API).
        /// </summary>
        public void QueueGraphUpdate(Bounds bounds)
        {
            QueueBoundsUpdate(bounds);
        }
        
        /// <summary>
        /// Process all pending platform and bounds updates.
        /// </summary>
        private void ProcessPendingUpdates()
        {
            if (_pendingUpdates.Count == 0 && _pendingBoundsUpdates.Count == 0)
            {
                _updateScheduled = false;
                return;
            }
            
            // Collect all bounds
            Bounds combinedBounds = new Bounds();
            bool first = true;
            
            // From pending platforms
            foreach (var platform in _pendingUpdates)
            {
                if (!platform) continue;
                
                var platformBounds = GetPlatformBounds(platform);
                platformBounds.Expand(updatePadding * 2f);
                
                if (first)
                {
                    combinedBounds = platformBounds;
                    first = false;
                }
                else
                {
                    combinedBounds.Encapsulate(platformBounds);
                }
            }
            
            // From pending bounds
            foreach (var bounds in _pendingBoundsUpdates)
            {
                if (first)
                {
                    combinedBounds = bounds;
                    first = false;
                }
                else
                {
                    combinedBounds.Encapsulate(bounds);
                }
            }
            
            _pendingUpdates.Clear();
            _pendingBoundsUpdates.Clear();
            _updateScheduled = false;
            _lastUpdateProcessed = Time.time;
            
            if (!first) // Had at least one valid update
            {
                UpdateGraphInBounds(combinedBounds);
            }
        }
        
        /// <summary>
        /// Update the RecastGraph within the specified bounds (tile-based local update).
        /// </summary>
        private void UpdateGraphInBounds(Bounds bounds)
        {
            if (!_isReady || _recastGraph == null) return;
            
            if (debugLogs) 
            {
                Debug.Log($"[PathfindingManager] Updating graph in bounds: center={bounds.center}, size={bounds.size}");
            }
            
            // Use GraphUpdateObject for tile-based updates
            // RecastGraph automatically determines which tiles need rebuilding based on bounds
            var guo = new GraphUpdateObject(bounds)
            {
                updatePhysics = true,  // Re-scan physics for the area
                modifyWalkability = false,  // Let RecastGraph determine walkability from geometry
                modifyTag = false
            };
            
            // Queue the update - A* will process affected tiles only
            _astarPath.UpdateGraphs(guo);
            
            if (debugDrawUpdates)
            {
                DebugDrawBounds(bounds, Color.green, 1f);
            }
            
            GraphUpdated?.Invoke(bounds);
        }
        
        /// <summary>
        /// Force immediate graph update for an area (synchronous).
        /// Use sparingly - prefer queued updates.
        /// </summary>
        public void UpdateGraphImmediate(Bounds bounds)
        {
            if (!_isReady || _recastGraph == null) return;
            
            bounds.Expand(updatePadding * 2f);
            
            if (debugLogs) Debug.Log($"[PathfindingManager] Immediate graph update: {bounds.center}");
            
            // Synchronous update
            var guo = new GraphUpdateObject(bounds);
            guo.updatePhysics = true;
            
            _astarPath.UpdateGraphs(guo);
            _astarPath.FlushGraphUpdates();
            
            GraphUpdated?.Invoke(bounds);
        }
        
        /// <summary>
        /// Update graph for a specific platform.
        /// </summary>
        public void UpdateGraphForPlatform(GamePlatform platform)
        {
            if (!platform || !_isReady) return;
            
            var bounds = GetPlatformBounds(platform);
            bounds.Expand(updatePadding * 2f);
            UpdateGraphInBounds(bounds);
        }
        
        #endregion
        
        #region Position Validation
        
        /// <summary>
        /// Check if a world position is on a walkable node.
        /// </summary>
        public bool IsPositionWalkable(Vector3 worldPosition)
        {
            if (!_isReady || _recastGraph == null) return false;
            
            var nearest = _recastGraph.GetNearest(worldPosition);
            return nearest.node != null && nearest.node.Walkable;
        }
        
        /// <summary>
        /// Get the nearest walkable position to a world position.
        /// </summary>
        public bool GetNearestWalkablePosition(Vector3 worldPosition, out Vector3 walkablePosition, float maxDistance = 5f)
        {
            walkablePosition = worldPosition;
            
            if (!_isReady || _recastGraph == null) return false;
            
            // Use new 5.4 API with NearestNodeConstraint
            var constraint = NearestNodeConstraint.Walkable;
            
            var nearest = _astarPath.GetNearest(worldPosition, constraint);
            
            if (nearest.node != null && nearest.node.Walkable)
            {
                float distance = Vector3.Distance(worldPosition, (Vector3)nearest.node.position);
                if (distance <= maxDistance)
                {
                    walkablePosition = (Vector3)nearest.node.position;
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Check if a path exists between two positions.
        /// </summary>
        public bool CanReach(Vector3 from, Vector3 to)
        {
            if (!_isReady) return false;
            
            var path = ABPath.Construct(from, to);
            AstarPath.StartPath(path);
            path.BlockUntilCalculated();
            
            return !path.error && path.vectorPath.Count > 0;
        }
        
        /// <summary>
        /// Sample the navmesh at a position with a max distance.
        /// Returns true if a point on the navmesh was found.
        /// </summary>
        public bool SamplePosition(Vector3 worldPosition, out Vector3 navmeshPosition, float maxDistance = 5f)
        {
            return GetNearestWalkablePosition(worldPosition, out navmeshPosition, maxDistance);
        }
        
        #endregion
        
        #region Utility Methods
        
        /// <summary>
        /// Get world bounds for a platform.
        /// </summary>
        private Bounds GetPlatformBounds(GamePlatform platform)
        {
            if (!platform) return new Bounds();
            
            // Try to get bounds from colliders first
            var colliders = platform.GetComponentsInChildren<Collider>();
            if (colliders.Length > 0)
            {
                Bounds bounds = colliders[0].bounds;
                for (int i = 1; i < colliders.Length; i++)
                {
                    bounds.Encapsulate(colliders[i].bounds);
                }
                return bounds;
            }
            
            // Fallback: Calculate bounds from footprint
            var footprint = platform.Footprint;
            Vector3 center = platform.transform.position;
            Vector3 size = new Vector3(footprint.x, agentHeight * 2f, footprint.y);
            
            return new Bounds(center, size);
        }
        
        /// <summary>
        /// Convert WorldGrid cell to graph node position.
        /// </summary>
        public Vector3 CellToWorldPosition(Vector2Int cell)
        {
            if (worldGrid)
            {
                return worldGrid.GetCellCenter(cell);
            }
            
            // Fallback: assume 1m cells at origin
            return new Vector3(cell.x + 0.5f, 0f, cell.y + 0.5f);
        }
        
        /// <summary>
        /// Set the graph bounds center.
        /// </summary>
        public void SetGraphCenter(Vector3 center)
        {
            graphCenter = center;
            if (_recastGraph != null)
            {
                _recastGraph.forcedBoundsCenter = center;
            }
        }
        
        /// <summary>
        /// Set the graph bounds size.
        /// </summary>
        public void SetGraphSize(Vector3 size)
        {
            graphSize = size;
            if (_recastGraph != null)
            {
                _recastGraph.forcedBoundsSize = size;
            }
        }
        
        /// <summary>
        /// Get info about the current graph for debugging.
        /// </summary>
        public string GetGraphInfo()
        {
            if (_recastGraph == null) return "No graph initialized";
            
            int tileCount = _recastGraph.tileXCount * _recastGraph.tileZCount;
            
            return $"RecastGraph:\n" +
                   $"  Bounds: {_recastGraph.forcedBoundsSize} at {_recastGraph.forcedBoundsCenter}\n" +
                   $"  Tiles: {_recastGraph.tileXCount}x{_recastGraph.tileZCount} = {tileCount}\n" +
                   $"  Cell Size: {_recastGraph.cellSize}m\n" +
                   $"  Agent: radius={_recastGraph.characterRadius}m, height={_recastGraph.walkableHeight}m\n" +
                   $"  Max Climb: {_recastGraph.walkableClimb}m, Max Slope: {_recastGraph.maxSlope}°";
        }
        
        private void DebugDrawBounds(Bounds bounds, Color color, float duration)
        {
            var min = bounds.min;
            var max = bounds.max;
            
            // Bottom face
            Debug.DrawLine(new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z), color, duration);
            Debug.DrawLine(new Vector3(max.x, min.y, min.z), new Vector3(max.x, min.y, max.z), color, duration);
            Debug.DrawLine(new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z), color, duration);
            Debug.DrawLine(new Vector3(min.x, min.y, max.z), new Vector3(min.x, min.y, min.z), color, duration);
            
            // Top face
            Debug.DrawLine(new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z), color, duration);
            Debug.DrawLine(new Vector3(max.x, max.y, min.z), new Vector3(max.x, max.y, max.z), color, duration);
            Debug.DrawLine(new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z), color, duration);
            Debug.DrawLine(new Vector3(min.x, max.y, max.z), new Vector3(min.x, max.y, min.z), color, duration);
            
            // Vertical edges
            Debug.DrawLine(new Vector3(min.x, min.y, min.z), new Vector3(min.x, max.y, min.z), color, duration);
            Debug.DrawLine(new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z), color, duration);
            Debug.DrawLine(new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, max.z), color, duration);
            Debug.DrawLine(new Vector3(min.x, min.y, max.z), new Vector3(min.x, max.y, max.z), color, duration);
        }
        
        #endregion
        
        #region Runtime Configuration API
        
        /// <summary>
        /// Update agent settings at runtime. Requires rescan to take effect.
        /// </summary>
        public void SetAgentSettings(float radius, float height, float climb, float slope)
        {
            agentRadius = radius;
            agentHeight = height;
            maxClimb = climb;
            maxSlope = slope;
            
            if (_recastGraph != null)
            {
                _recastGraph.characterRadius = radius;
                _recastGraph.walkableHeight = height;
                _recastGraph.walkableClimb = climb;
                _recastGraph.maxSlope = slope;
            }
        }
        
        /// <summary>
        /// Update quality settings at runtime. Requires rescan to take effect.
        /// </summary>
        public void SetQualitySettings(float newCellSize, int newTileSize)
        {
            cellSize = Mathf.Clamp(newCellSize, 0.05f, 1f);
            tileSize = Mathf.Clamp(newTileSize, 16, 512);
            
            if (_recastGraph != null)
            {
                _recastGraph.cellSize = cellSize;
                _recastGraph.editorTileSize = tileSize;
            }
        }
        
        /// <summary>
        /// Update layer masks at runtime. Requires rescan to take effect.
        /// </summary>
        public void SetLayerMasks(LayerMask walkable, LayerMask unwalkable)
        {
            walkableLayers = walkable;
            unwalkableLayers = unwalkable;
            
            if (_recastGraph != null)
            {
                _recastGraph.mask = walkableLayers;
            }
        }
        
        #endregion
        
        #region Editor
        
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!debugDrawBounds) return;
            
            // Draw graph bounds
            Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.3f);
            Gizmos.DrawWireCube(graphCenter, graphSize);
            
            // Draw agent size
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(transform.position, agentRadius);
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * agentHeight);
        }
        
        private void OnValidate()
        {
            // Clamp values
            agentRadius = Mathf.Max(0.1f, agentRadius);
            agentHeight = Mathf.Max(0.5f, agentHeight);
            maxClimb = Mathf.Clamp(maxClimb, 0f, agentHeight);
            maxSlope = Mathf.Clamp(maxSlope, 0f, 85f);
            cellSize = Mathf.Clamp(cellSize, 0.05f, 1f);
            tileSize = Mathf.Clamp(tileSize, 16, 512);
            minRegionArea = Mathf.Max(0f, minRegionArea);
        }
#endif
        
        #endregion
    }
}
