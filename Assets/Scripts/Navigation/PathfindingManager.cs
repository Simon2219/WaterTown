using System;
using System.Collections.Generic;
using UnityEngine;
using Pathfinding;
using Grid;
using Platforms;

namespace Navigation
{
    /// <summary>
    /// Central manager for A* Pathfinding integration.
    /// Handles graph setup, platform event subscriptions, and dynamic graph updates.
    /// 
    /// Architecture:
    /// - Subscribes to GamePlatform static events for platform lifecycle
    /// - Triggers local graph updates when platforms change
    /// - Provides position validation methods for other systems
    /// - Only this manager should call A* Pathfinding systems directly
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
        
        #region Configuration
        
        [Header("Graph Settings")]
        [Tooltip("Width of the pathfinding graph in world units.")]
        [SerializeField] private int graphWidth = 1000;
        
        [Tooltip("Depth of the pathfinding graph in world units.")]
        [SerializeField] private int graphDepth = 1000;
        
        [Tooltip("Size of each pathfinding node in world units.")]
        [SerializeField] private float nodeSize = 1f;
        
        [Tooltip("Center position of the graph in world space.")]
        [SerializeField] private Vector3 graphCenter = new Vector3(500, 0, 500);
        
        [Header("Agent Settings")]
        [Tooltip("Agent radius for collision testing.")]
        [SerializeField] private float agentRadius = 0.3f;
        
        [Tooltip("Agent height for collision testing.")]
        [SerializeField] private float agentHeight = 1.5f;
        
        [Header("Collision Settings")]
        [Tooltip("Layer mask for walkable surfaces (platforms).")]
        [SerializeField] private LayerMask walkableMask = ~0;
        
        [Tooltip("Layer mask for obstacles.")]
        [SerializeField] private LayerMask obstacleMask = 0;
        
        [Tooltip("Height from which to raycast down to find ground.")]
        [SerializeField] private float raycastHeight = 10f;
        
        [Tooltip("Maximum slope angle for walkable surfaces.")]
        [SerializeField] private float maxSlopeAngle = 45f;
        
        [Header("Update Settings")]
        [Tooltip("Extra padding around platform bounds when updating graph.")]
        [SerializeField] private float updatePadding = 0.5f;
        
        [Tooltip("Delay before processing queued graph updates (batching).")]
        [SerializeField] private float updateDelay = 0.1f;
        
        [Header("References")]
        [Tooltip("Reference to WorldGrid for coordinate transforms.")]
        [SerializeField] private WorldGrid worldGrid;
        
        [Header("Debug")]
        [SerializeField] private bool debugLogs = true;
        [SerializeField] private bool debugDrawUpdates;
        
        #endregion
        
        #region Public Properties
        
        public bool IsReady => _isReady;
        public AstarPath AstarPath => _astarPath;
        public GridGraph GridGraph => _gridGraph;
        public float NodeSize => nodeSize;
        public float AgentRadius => agentRadius;
        public float AgentHeight => agentHeight;
        
        #endregion
        
        #region Private State
        
        private AstarPath _astarPath;
        private GridGraph _gridGraph;
        private bool _isReady;
        
        // Update batching
        private readonly HashSet<GamePlatform> _pendingUpdates = new();
        private float _lastUpdateRequest;
        private bool _updateScheduled;
        
        // Platform tracking
        private readonly HashSet<GamePlatform> _registeredPlatforms = new();
        
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
                ProcessPendingUpdates();
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
            if (debugLogs) Debug.Log("[PathfindingManager] Initializing pathfinding graph...");
            
            // Check if graph already exists
            if (_astarPath.data.graphs != null && _astarPath.data.graphs.Length > 0)
            {
                _gridGraph = _astarPath.data.gridGraph;
                if (_gridGraph != null)
                {
                    if (debugLogs) Debug.Log("[PathfindingManager] Using existing GridGraph from scene.");
                    ConfigureExistingGraph();
                    return;
                }
            }
            
            // Create new GridGraph
            CreateNewGraph();
        }
        
        private void CreateNewGraph()
        {
            if (debugLogs) Debug.Log("[PathfindingManager] Creating new GridGraph...");
            
            _gridGraph = _astarPath.data.AddGraph(typeof(GridGraph)) as GridGraph;
            if (_gridGraph == null)
            {
                Debug.LogError("[PathfindingManager] Failed to create GridGraph!");
                return;
            }
            
            ConfigureGraph();
            
            // Initial scan
            ScanGraph();
        }
        
        private void ConfigureExistingGraph()
        {
            // Apply runtime settings to existing graph
            _gridGraph.collision.mask = walkableMask;
            _gridGraph.collision.heightMask = walkableMask;
            _gridGraph.collision.diameter = agentRadius * 2f;
            _gridGraph.collision.height = agentHeight;
            _gridGraph.maxSlope = maxSlopeAngle;
            
            _isReady = true;
            GraphReady?.Invoke();
            
            if (debugLogs) Debug.Log("[PathfindingManager] Graph ready (existing).");
        }
        
        private void ConfigureGraph()
        {
            // Calculate node count based on world size and node size
            int width = Mathf.CeilToInt(graphWidth / nodeSize);
            int depth = Mathf.CeilToInt(graphDepth / nodeSize);
            
            // Configure graph dimensions
            _gridGraph.SetDimensions(width, depth, nodeSize);
            _gridGraph.center = graphCenter;
            
            // Configure collision detection
            _gridGraph.collision.type = ColliderType.Capsule;
            _gridGraph.collision.diameter = agentRadius * 2f;
            _gridGraph.collision.height = agentHeight;
            _gridGraph.collision.mask = obstacleMask;
            
            // Configure height testing (raycast down to find ground)
            _gridGraph.collision.heightCheck = true;
            _gridGraph.collision.heightMask = walkableMask;
            _gridGraph.collision.fromHeight = raycastHeight;
            
            // Configure walkability
            _gridGraph.maxSlope = maxSlopeAngle;
            _gridGraph.collision.collisionCheck = true;
            
            // Configure connections (8-directional for smoother paths)
            _gridGraph.neighbours = NumNeighbours.Eight;
            _gridGraph.cutCorners = true;
            
            if (debugLogs)
            {
                Debug.Log($"[PathfindingManager] Graph configured: {width}x{depth} nodes, {nodeSize}m each");
            }
        }
        
        /// <summary>
        /// Perform initial graph scan.
        /// </summary>
        public void ScanGraph()
        {
            if (debugLogs) Debug.Log("[PathfindingManager] Scanning graph...");
            
            ScanStarted?.Invoke();
            
            _astarPath.Scan(_gridGraph);
            
            _isReady = true;
            ScanCompleted?.Invoke();
            GraphReady?.Invoke();
            
            if (debugLogs) Debug.Log("[PathfindingManager] Graph scan complete.");
        }
        
        #endregion
        
        #region Platform Event Handlers
        
        private void OnPlatformCreated(GamePlatform platform)
        {
            if (!platform || _registeredPlatforms.Contains(platform)) return;
            
            SubscribeToPlatform(platform);
            _registeredPlatforms.Add(platform);
            
            if (debugLogs) Debug.Log($"[PathfindingManager] Platform registered: {platform.name}");
        }
        
        private void OnPlatformDestroyed(GamePlatform platform)
        {
            if (!platform) return;
            
            // Queue update for the area this platform occupied
            QueueGraphUpdate(GetPlatformBounds(platform));
            
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
            QueuePlatformUpdate(platform);
        }
        
        private void OnPlatformPickedUp(GamePlatform platform)
        {
            if (debugLogs) Debug.Log($"[PathfindingManager] Platform picked up: {platform.name}");
            // Update the area where the platform was (now empty)
            QueuePlatformUpdate(platform);
        }
        
        private void OnPlatformMoved(GamePlatform platform)
        {
            // Only relevant during placement preview - batch these updates
            QueuePlatformUpdate(platform);
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
        public void QueueGraphUpdate(Bounds bounds)
        {
            if (!_isReady) return;
            
            // Expand bounds slightly for safety
            bounds.Expand(updatePadding * 2f);
            
            var guo = new GraphUpdateObject(bounds);
            guo.updatePhysics = true;
            
            _astarPath.UpdateGraphs(guo);
            
            if (debugLogs) Debug.Log($"[PathfindingManager] Queued graph update: {bounds.center}, size: {bounds.size}");
            
            if (debugDrawUpdates)
            {
                DebugDrawBounds(bounds, Color.yellow, 2f);
            }
            
            GraphUpdated?.Invoke(bounds);
        }
        
        /// <summary>
        /// Process all pending platform updates.
        /// </summary>
        private void ProcessPendingUpdates()
        {
            if (_pendingUpdates.Count == 0)
            {
                _updateScheduled = false;
                return;
            }
            
            // Calculate combined bounds of all pending platforms
            Bounds combinedBounds = new Bounds();
            bool first = true;
            
            foreach (var platform in _pendingUpdates)
            {
                if (!platform) continue;
                
                var platformBounds = GetPlatformBounds(platform);
                
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
            
            _pendingUpdates.Clear();
            _updateScheduled = false;
            
            if (!first) // Had at least one valid platform
            {
                QueueGraphUpdate(combinedBounds);
            }
        }
        
        /// <summary>
        /// Force immediate graph update for an area.
        /// </summary>
        public void UpdateGraphImmediate(Bounds bounds)
        {
            if (!_isReady) return;
            
            bounds.Expand(updatePadding * 2f);
            
            var guo = new GraphUpdateObject(bounds);
            guo.updatePhysics = true;
            
            // Use FlushGraphUpdates to process immediately
            _astarPath.UpdateGraphs(guo);
            _astarPath.FlushGraphUpdates();
            
            if (debugLogs) Debug.Log($"[PathfindingManager] Immediate graph update: {bounds.center}");
            
            GraphUpdated?.Invoke(bounds);
        }
        
        /// <summary>
        /// Update graph for a specific platform.
        /// </summary>
        public void UpdateGraphForPlatform(GamePlatform platform)
        {
            if (!platform || !_isReady) return;
            
            var bounds = GetPlatformBounds(platform);
            QueueGraphUpdate(bounds);
        }
        
        #endregion
        
        #region Position Validation
        
        /// <summary>
        /// Check if a world position is on a walkable node.
        /// </summary>
        public bool IsPositionWalkable(Vector3 worldPosition)
        {
            if (!_isReady || _gridGraph == null) return false;
            
            var node = _gridGraph.GetNearest(worldPosition).node;
            return node != null && node.Walkable;
        }
        
        /// <summary>
        /// Get the nearest walkable position to a world position.
        /// </summary>
        public bool GetNearestWalkablePosition(Vector3 worldPosition, out Vector3 walkablePosition, float maxDistance = 5f)
        {
            walkablePosition = worldPosition;
            
            if (!_isReady || _gridGraph == null) return false;
            
            var constraint = NNConstraint.Default;
            constraint.constrainWalkability = true;
            constraint.walkable = true;
            
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
        
        #endregion
        
        #region Utility Methods
        
        /// <summary>
        /// Get world bounds for a platform.
        /// </summary>
        private Bounds GetPlatformBounds(GamePlatform platform)
        {
            if (!platform) return new Bounds();
            
            // Calculate bounds from footprint
            var footprint = platform.Footprint;
            float halfWidth = footprint.x * 0.5f;
            float halfDepth = footprint.y * 0.5f;
            
            Vector3 center = platform.transform.position;
            Vector3 size = new Vector3(footprint.x + updatePadding * 2f, 2f, footprint.y + updatePadding * 2f);
            
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
        /// Set the graph center (useful when WorldGrid changes).
        /// </summary>
        public void SetGraphCenter(Vector3 center)
        {
            graphCenter = center;
            if (_gridGraph != null)
            {
                _gridGraph.center = center;
            }
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
        
        #region Editor
        
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            // Draw graph bounds
            Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.3f);
            Vector3 size = new Vector3(graphWidth, 1f, graphDepth);
            Gizmos.DrawWireCube(graphCenter, size);
            
            // Draw agent size
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(transform.position, agentRadius);
        }
#endif
        
        #endregion
    }
}

