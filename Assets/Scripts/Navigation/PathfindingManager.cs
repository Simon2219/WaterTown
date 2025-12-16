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
    /// 
    /// IMPORTANT: Configure RecastGraph settings in the AstarPath component Inspector!
    /// This manager only handles:
    /// - Platform event subscriptions for dynamic graph updates
    /// - Batched graph updates when platforms are picked up or placed
    /// - Position validation methods for other systems
    /// 
    /// The manager does NOT duplicate settings already in AstarPath/RecastGraph.
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
        
        [Header("Dynamic Update Settings")]
        [Tooltip("Extra padding around platform bounds when updating graph (world units).")]
        [SerializeField] private float updatePadding = 0.5f;
        
        [Tooltip("Delay before processing queued graph updates (seconds). Batches rapid changes.")]
        [SerializeField] private float updateDelay = 0.1f;
        
        [Tooltip("Minimum time between graph updates (seconds). Prevents update spam.")]
        [SerializeField] private float updateCooldown = 0.2f;
        
        [Header("References")]
        [Tooltip("Reference to WorldGrid for coordinate transforms (optional).")]
        [SerializeField] private WorldGrid worldGrid;
        
        [Header("Debug")]
        [SerializeField] private bool debugLogs = true;
        [SerializeField] private bool debugDrawUpdates = false;
        
        #endregion
        
        #region Public Properties
        
        public bool IsReady => _isReady;
        public AstarPath AstarPath => _astarPath;
        public RecastGraph RecastGraph => _recastGraph;
        public float UpdatePadding => updatePadding;
        
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
        
        // Platform tracking
        private readonly HashSet<GamePlatform> _registeredPlatforms = new();
        
        // Previous bounds tracking (for pickup/place operations)
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
            GamePlatform.Created += OnPlatformCreated;
            GamePlatform.Destroyed += OnPlatformDestroyed;
        }
        
        private void OnDisable()
        {
            GamePlatform.Created -= OnPlatformCreated;
            GamePlatform.Destroyed -= OnPlatformDestroyed;
            
            foreach (var platform in _registeredPlatforms)
            {
                if (platform) UnsubscribeFromPlatform(platform);
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
            if (_updateScheduled && Time.time - _lastUpdateRequest >= updateDelay)
            {
                if (Time.time - _lastUpdateProcessed >= updateCooldown)
                {
                    ProcessPendingUpdates();
                }
            }
        }
        
        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
        
        #endregion
        
        #region Graph Initialization
        
        private void InitializeGraph()
        {
            if (debugLogs) Debug.Log("[PathfindingManager] Initializing...");
            
            // Find existing RecastGraph configured in AstarPath Inspector
            if (_astarPath.data.graphs != null && _astarPath.data.graphs.Length > 0)
            {
                foreach (var graph in _astarPath.data.graphs)
                {
                    if (graph is RecastGraph rg)
                    {
                        _recastGraph = rg;
                        if (debugLogs) Debug.Log("[PathfindingManager] Found RecastGraph.");
                        break;
                    }
                }
            }
            
            if (_recastGraph == null)
            {
                Debug.LogError("[PathfindingManager] No RecastGraph found! Configure one in AstarPath component.");
                return;
            }
            
            // Perform initial scan
            ScanGraph();
        }
        
        /// <summary>
        /// Perform graph scan using settings configured in AstarPath Inspector.
        /// </summary>
        public void ScanGraph()
        {
            if (_recastGraph == null)
            {
                Debug.LogError("[PathfindingManager] Cannot scan - no RecastGraph found!");
                return;
            }
            
            if (debugLogs) Debug.Log("[PathfindingManager] Scanning graph...");
            
            ScanStarted?.Invoke();
            _astarPath.Scan(_recastGraph);
            _isReady = true;
            ScanCompleted?.Invoke();
            GraphReady?.Invoke();
            
            if (debugLogs)
            {
                int tiles = _recastGraph.tileXCount * _recastGraph.tileZCount;
                Debug.Log($"[PathfindingManager] Scan complete. Tiles: {tiles}");
            }
        }
        
        /// <summary>
        /// Rescan the entire graph. Prefer local updates when possible.
        /// </summary>
        public void RescanGraph() => ScanGraph();
        
        #endregion
        
        #region Platform Event Handlers
        
        private void OnPlatformCreated(GamePlatform platform)
        {
            if (!platform || _registeredPlatforms.Contains(platform)) return;
            
            SubscribeToPlatform(platform);
            _registeredPlatforms.Add(platform);
            _previousBounds[platform] = GetPlatformBounds(platform);
            QueuePlatformUpdate(platform);
            
            if (debugLogs) Debug.Log($"[PathfindingManager] Platform registered: {platform.name}");
        }
        
        private void OnPlatformDestroyed(GamePlatform platform)
        {
            if (!platform) return;
            
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
            
            // Update old position (from pickup) and new position
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
            
            // Store bounds and update to mark as unwalkable
            var bounds = GetPlatformBounds(platform);
            _previousBounds[platform] = bounds;
            QueueBoundsUpdate(bounds);
        }
        
        private void OnPlatformMoved(GamePlatform platform)
        {
            // NO updates during dragging - only on PickedUp and Placed
        }
        
        #endregion
        
        #region Graph Updates
        
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
            
            bounds.Expand(updatePadding * 2f);
            _pendingBoundsUpdates.Add(bounds);
            _lastUpdateRequest = Time.time;
            _updateScheduled = true;
            
            if (debugDrawUpdates) DebugDrawBounds(bounds, Color.yellow, 2f);
        }
        
        /// <summary>
        /// Queue a bounds area for graph update (alias).
        /// </summary>
        public void QueueGraphUpdate(Bounds bounds) => QueueBoundsUpdate(bounds);
        
        private void ProcessPendingUpdates()
        {
            if (_pendingUpdates.Count == 0 && _pendingBoundsUpdates.Count == 0)
            {
                _updateScheduled = false;
                return;
            }
            
            Bounds combinedBounds = new Bounds();
            bool first = true;
            
            foreach (var platform in _pendingUpdates)
            {
                if (!platform) continue;
                var platformBounds = GetPlatformBounds(platform);
                platformBounds.Expand(updatePadding * 2f);
                
                if (first) { combinedBounds = platformBounds; first = false; }
                else combinedBounds.Encapsulate(platformBounds);
            }
            
            foreach (var bounds in _pendingBoundsUpdates)
            {
                if (first) { combinedBounds = bounds; first = false; }
                else combinedBounds.Encapsulate(bounds);
            }
            
            _pendingUpdates.Clear();
            _pendingBoundsUpdates.Clear();
            _updateScheduled = false;
            _lastUpdateProcessed = Time.time;
            
            if (!first) UpdateGraphInBounds(combinedBounds);
        }
        
        private void UpdateGraphInBounds(Bounds bounds)
        {
            if (!_isReady || _recastGraph == null) return;
            
            if (debugLogs) Debug.Log($"[PathfindingManager] Updating: {bounds.center}, size={bounds.size}");
            
            var guo = new GraphUpdateObject(bounds)
            {
                updatePhysics = true,
                modifyWalkability = false,
                modifyTag = false
            };
            
            _astarPath.UpdateGraphs(guo);
            
            if (debugDrawUpdates) DebugDrawBounds(bounds, Color.green, 1f);
            GraphUpdated?.Invoke(bounds);
        }
        
        /// <summary>
        /// Force immediate synchronous graph update.
        /// </summary>
        public void UpdateGraphImmediate(Bounds bounds)
        {
            if (!_isReady || _recastGraph == null) return;
            
            bounds.Expand(updatePadding * 2f);
            if (debugLogs) Debug.Log($"[PathfindingManager] Immediate update: {bounds.center}");
            
            var guo = new GraphUpdateObject(bounds) { updatePhysics = true };
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
        /// </summary>
        public bool SamplePosition(Vector3 worldPosition, out Vector3 navmeshPosition, float maxDistance = 5f)
        {
            return GetNearestWalkablePosition(worldPosition, out navmeshPosition, maxDistance);
        }
        
        #endregion
        
        #region Utility Methods
        
        private Bounds GetPlatformBounds(GamePlatform platform)
        {
            if (!platform) return new Bounds();
            
            // Get bounds from colliders (includes NavMesh floor geometry if present)
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
            
            // Fallback
            var footprint = platform.Footprint;
            return new Bounds(platform.transform.position, new Vector3(footprint.x, 5f, footprint.y));
        }
        
        /// <summary>
        /// Convert WorldGrid cell to world position.
        /// </summary>
        public Vector3 CellToWorldPosition(Vector2Int cell)
        {
            if (worldGrid) return worldGrid.GetCellCenter(cell);
            return new Vector3(cell.x + 0.5f, 0f, cell.y + 0.5f);
        }
        
        /// <summary>
        /// Get debug info about the current graph.
        /// </summary>
        public string GetGraphInfo()
        {
            if (_recastGraph == null) return "No graph";
            int tiles = _recastGraph.tileXCount * _recastGraph.tileZCount;
            return $"RecastGraph: {tiles} tiles, Cell: {_recastGraph.cellSize}m";
        }
        
        private void DebugDrawBounds(Bounds bounds, Color color, float duration)
        {
            var min = bounds.min;
            var max = bounds.max;
            
            Debug.DrawLine(new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z), color, duration);
            Debug.DrawLine(new Vector3(max.x, min.y, min.z), new Vector3(max.x, min.y, max.z), color, duration);
            Debug.DrawLine(new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z), color, duration);
            Debug.DrawLine(new Vector3(min.x, min.y, max.z), new Vector3(min.x, min.y, min.z), color, duration);
            
            Debug.DrawLine(new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z), color, duration);
            Debug.DrawLine(new Vector3(max.x, max.y, min.z), new Vector3(max.x, max.y, max.z), color, duration);
            Debug.DrawLine(new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z), color, duration);
            Debug.DrawLine(new Vector3(min.x, max.y, max.z), new Vector3(min.x, max.y, min.z), color, duration);
            
            Debug.DrawLine(new Vector3(min.x, min.y, min.z), new Vector3(min.x, max.y, min.z), color, duration);
            Debug.DrawLine(new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z), color, duration);
            Debug.DrawLine(new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, max.z), color, duration);
            Debug.DrawLine(new Vector3(min.x, min.y, max.z), new Vector3(min.x, max.y, max.z), color, duration);
        }
        
        #endregion
    }
}
