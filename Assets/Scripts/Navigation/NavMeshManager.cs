using System.Collections.Generic;
using Platforms;
using Unity.AI.Navigation;
using UnityEngine;

namespace Navigation
{
    /// <summary>
    /// Manages NavMeshLinks between platforms.
    /// 
    /// Each platform has its own NavMeshSurface that bakes its local NavMesh.
    /// This manager creates NavMeshLinks to connect adjacent platforms,
    /// allowing agents to traverse between them.
    /// 
    /// This manager is EVENT-DRIVEN:
    /// - Subscribes to GamePlatform.Placed to create links when platforms are placed
    /// - Subscribes to GamePlatform.PickedUp to clear links when platforms are picked up
    /// - Does NOT update during preview/movement - only on actual placement
    /// 
    /// Link creation uses a consolidated per-edge approach:
    /// - ONE link per contiguous connected segment on each edge
    /// - Prevents the "step back before crossing" issue when adjacent sockets connect to different platforms
    /// - Only one platform creates the link (lower instance ID wins) to avoid duplicates
    /// </summary>
    [DisallowMultipleComponent]
    public class NavMeshManager : MonoBehaviour
    {
        #region Singleton
        
        private static NavMeshManager _instance;
        public static NavMeshManager Instance => _instance;
        
        #endregion
        
        
        #region Configuration
        
        [Header("Link Dimensions")]
        [Tooltip("Width per connected socket (meters). Default 1m matches grid cell size.")]
        [SerializeField] private float widthPerSocket = 1f;
        
        [Tooltip("How far the link extends INTO each platform from the boundary (meters).")]
        [SerializeField] private float linkDepth = 0.5f;
        
        [Tooltip("Height offset above platform surface for link endpoints (meters).")]
        [SerializeField] private float heightOffset = 0.05f;
        
        [Header("Debug")]
        [SerializeField] private bool debugLogs;
        [SerializeField] private bool drawDebugGizmos = true;
        [SerializeField] private float debugDrawDuration = 10f;
        
        #endregion
        
        
        #region Private Fields
        
        // Links container and tracking
        private Transform _linksContainer;
        private readonly Dictionary<(int, int), List<GameObject>> _linksByPlatform = new();
        
        // Dependency - found at runtime
        private PlatformManager _platformManager;
        
        #endregion
        
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning("[NavMeshManager] Duplicate instance detected. Destroying this one.");
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }
        
        private void OnEnable()
        {
            // Subscribe to static GamePlatform events
            GamePlatform.Created += OnPlatformCreated;
            GamePlatform.Destroyed += OnPlatformDestroyed;
        }
        
        private void Start()
        {
            // Find PlatformManager dependency
            _platformManager = FindFirstObjectByType<PlatformManager>();
            if (!_platformManager)
            {
                Debug.LogError("[NavMeshManager] PlatformManager not found! NavMeshLinks will not be created.");
            }
        }
        
        private void OnDisable()
        {
            // Unsubscribe from static events
            GamePlatform.Created -= OnPlatformCreated;
            GamePlatform.Destroyed -= OnPlatformDestroyed;
        }
        
        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
        
        #endregion
        
        
        #region Event Handlers
        
        /// <summary>
        /// Called when a platform is created - subscribe to its instance events
        /// </summary>
        private void OnPlatformCreated(GamePlatform platform)
        {
            if (!platform) return;
            
            platform.Placed += OnPlatformPlaced;
            platform.PickedUp += OnPlatformPickedUp;
        }
        
        /// <summary>
        /// Called when a platform is destroyed - unsubscribe from its instance events
        /// </summary>
        private void OnPlatformDestroyed(GamePlatform platform)
        {
            if (!platform) return;
            
            platform.Placed -= OnPlatformPlaced;
            platform.PickedUp -= OnPlatformPickedUp;
            
            // Clear any links for this platform
            ClearLinksForPlatform(platform);
        }
        
        /// <summary>
        /// Called when a platform is placed - create links for it and update neighbors
        /// </summary>
        private void OnPlatformPlaced(GamePlatform platform)
        {
            if (!platform || !_platformManager) return;
            
            if (debugLogs)
                Debug.Log($"[NavMeshManager] Platform placed: {platform.name}");
            
            // Get all affected platforms (this platform + neighbors)
            var affectedPlatforms = GetAffectedPlatforms(platform);
            
            // Recreate links for all affected platforms
            foreach (var p in affectedPlatforms)
            {
                if (p && !p.IsPickedUp)
                {
                    CreateLinksForPlatform(p);
                }
            }
        }
        
        /// <summary>
        /// Called when a platform is picked up - clear its links and update neighbors
        /// </summary>
        private void OnPlatformPickedUp(GamePlatform platform)
        {
            if (!platform || !_platformManager) return;
            
            if (debugLogs)
                Debug.Log($"[NavMeshManager] Platform picked up: {platform.name}");
            
            // Get neighbors from PREVIOUS position (cells have already been cleared by PlatformManager)
            // Use previousOccupiedCells which was set before cell clearing
            var neighbors = GetNeighborsFromPreviousCells(platform);
            
            // Clear links for the picked up platform
            ClearLinksForPlatform(platform);
            
            // Recreate links for neighbors (their connections to this platform are now gone)
            foreach (var neighbor in neighbors)
            {
                if (neighbor && !neighbor.IsPickedUp)
                {
                    CreateLinksForPlatform(neighbor);
                }
            }
        }
        
        #endregion
        
        
        #region Link Management
        
        /// <summary>
        /// Creates NavMesh links for a platform using the consolidated per-edge approach.
        /// </summary>
        private void CreateLinksForPlatform(GamePlatform platform)
        {
            if (!platform || !_platformManager) return;
            
            // Clear existing links for this platform first
            ClearLinksForPlatform(platform);
            
            // Get all connected sockets grouped by edge
            var edgeSegments = GetConnectedSegmentsByEdge(platform);
            
            if (debugLogs)
            {
                int totalSegments = 0;
                foreach (var edge in edgeSegments)
                    totalSegments += edge.Value.Count;
                Debug.Log($"[NavMeshManager] Creating links for {platform.name}: {totalSegments} segment(s) across {edgeSegments.Count} edge(s)");
            }
            
            // Create one link per segment
            foreach (var edgeKvp in edgeSegments)
            {
                foreach (var segment in edgeKvp.Value)
                {
                    CreateLinkForEdgeSegment(platform, segment);
                }
            }
        }
        
        /// <summary>
        /// Clears all links for a specific platform.
        /// </summary>
        private void ClearLinksForPlatform(GamePlatform platform)
        {
            if (!platform) return;
            
            int platformId = platform.GetInstanceID();
            var key = (platformId, platformId);
            
            if (_linksByPlatform.TryGetValue(key, out var links))
            {
                foreach (var go in links)
                {
                    if (go)
                    {
                        if (Application.isPlaying)
                            Destroy(go);
                        else
                            DestroyImmediate(go);
                    }
                }
                _linksByPlatform.Remove(key);
                
                if (debugLogs)
                    Debug.Log($"[NavMeshManager] Cleared links for {platform.name}");
            }
        }
        
        #endregion
        
        
        #region Neighbor Discovery
        
        /// <summary>
        /// Gets all platforms affected by a platform placement (the platform + its neighbors).
        /// </summary>
        private HashSet<GamePlatform> GetAffectedPlatforms(GamePlatform platform)
        {
            var affected = new HashSet<GamePlatform> { platform };
            
            var neighbors = GetPlacedNeighborPlatforms(platform);
            foreach (var neighbor in neighbors)
            {
                affected.Add(neighbor);
            }
            
            return affected;
        }
        
        /// <summary>
        /// Gets all PLACED neighbor platforms (excludes preview/picked-up platforms).
        /// Only considers cells that are actually Occupied (not OccupyPreview).
        /// </summary>
        private HashSet<GamePlatform> GetPlacedNeighborPlatforms(GamePlatform platform)
        {
            var neighbors = new HashSet<GamePlatform>();
            if (!platform || !_platformManager) return neighbors;
            
            var sockets = platform.Sockets;
            if (sockets == null) return neighbors;
            
            for (int i = 0; i < sockets.Count; i++)
            {
                // Check if the socket is connected to a PLACED platform (not preview)
                if (!IsSocketConnectedToPlacedPlatform(platform, i)) continue;
                
                Vector2Int adjacentCell = platform.GetAdjacentCellForSocket(i);
                if (_platformManager.GetPlatformAtCell(adjacentCell, out var neighbor))
                {
                    if (neighbor && neighbor != platform && !neighbor.IsPickedUp)
                        neighbors.Add(neighbor);
                }
            }
            
            return neighbors;
        }
        
        /// <summary>
        /// Checks if a socket is connected to an actually PLACED platform (not preview).
        /// This differs from GamePlatform.IsSocketConnected which includes preview connections for railing purposes.
        /// </summary>
        private bool IsSocketConnectedToPlacedPlatform(GamePlatform platform, int socketIndex)
        {
            Vector2Int adjacentCell = platform.GetAdjacentCellForSocket(socketIndex);
            
            // Check cell is actually Occupied (not OccupyPreview) using includeAllOccupation=false
            if (!_platformManager.IsCellOccupied(adjacentCell, includeAllOccupation: false))
                return false;
            
            // Verify it's a different, non-picked-up platform
            if (!_platformManager.GetPlatformAtCell(adjacentCell, out var neighbor))
                return false;
            
            return neighbor && neighbor != platform && !neighbor.IsPickedUp;
        }
        
        /// <summary>
        /// Gets neighbor platforms from a platform's previous occupied cells.
        /// Used when a platform is picked up (its current cells are already cleared).
        /// </summary>
        private HashSet<GamePlatform> GetNeighborsFromPreviousCells(GamePlatform platform)
        {
            var neighbors = new HashSet<GamePlatform>();
            if (!platform || !_platformManager) return neighbors;
            
            // Use previousOccupiedCells which contains cells before pickup
            if (platform.previousOccupiedCells == null || platform.previousOccupiedCells.Count == 0)
                return neighbors;
            
            // For each previous cell, check all 4 adjacent cells for placed neighbors
            foreach (var cell in platform.previousOccupiedCells)
            {
                Vector2Int[] adjacents = 
                {
                    cell + Vector2Int.up,
                    cell + Vector2Int.down,
                    cell + Vector2Int.left,
                    cell + Vector2Int.right
                };
                
                foreach (var adjacent in adjacents)
                {
                    // Only consider cells that are actually Occupied (not preview)
                    if (!_platformManager.IsCellOccupied(adjacent, includeAllOccupation: false))
                        continue;
                    
                    if (_platformManager.GetPlatformAtCell(adjacent, out var neighbor))
                    {
                        if (neighbor && neighbor != platform && !neighbor.IsPickedUp)
                            neighbors.Add(neighbor);
                    }
                }
            }
            
            return neighbors;
        }
        
        #endregion
        
        
        #region Link Creation - Edge Segments
        
        /// <summary>
        /// Gets all sockets connected to PLACED platforms, grouped by edge, with contiguous sockets grouped into segments.
        /// Only includes connections to actually placed platforms (not preview/picked-up).
        /// </summary>
        private Dictionary<int, List<EdgeSegment>> GetConnectedSegmentsByEdge(GamePlatform platform)
        {
            var result = new Dictionary<int, List<EdgeSegment>>();
            var sockets = platform.Sockets;
            if (sockets == null || sockets.Count == 0) return result;
            
            var footprint = platform.Footprint;
            int width = Mathf.Max(1, footprint.x);
            int length = Mathf.Max(1, footprint.y);
            int[] edgeBounds = { 0, width, width * 2, width * 2 + length, width * 2 + length * 2 };
            
            // Collect all socket indices connected to PLACED platforms, per edge
            var connectedByEdge = new Dictionary<int, List<int>>();
            for (int i = 0; i < sockets.Count; i++)
            {
                // Only include sockets connected to actually placed platforms (not preview)
                if (!IsSocketConnectedToPlacedPlatform(platform, i)) continue;
                
                int edge = GetEdgeIndex(i, edgeBounds);
                if (!connectedByEdge.ContainsKey(edge))
                    connectedByEdge[edge] = new List<int>();
                connectedByEdge[edge].Add(i);
            }
            
            // Group contiguous sockets on each edge into segments
            foreach (var kvp in connectedByEdge)
            {
                int edge = kvp.Key;
                var socketIndices = kvp.Value;
                socketIndices.Sort();
                
                var segments = new List<EdgeSegment>();
                var currentSegment = new List<int> { socketIndices[0] };
                
                for (int i = 1; i < socketIndices.Count; i++)
                {
                    // Check if this socket is adjacent to the previous one
                    if (socketIndices[i] - socketIndices[i - 1] == 1)
                    {
                        currentSegment.Add(socketIndices[i]);
                    }
                    else
                    {
                        // Start a new segment
                        segments.Add(CreateEdgeSegment(platform, currentSegment, edge));
                        currentSegment = new List<int> { socketIndices[i] };
                    }
                }
                
                // Add the last segment
                segments.Add(CreateEdgeSegment(platform, currentSegment, edge));
                result[edge] = segments;
            }
            
            return result;
        }
        
        private struct EdgeSegment
        {
            public List<int> SocketIndices;
            public int EdgeIndex;
            public Vector3 CenterPosition;
            public Vector3 OutwardDirection;
        }
        
        private EdgeSegment CreateEdgeSegment(GamePlatform platform, List<int> socketIndices, int edgeIndex)
        {
            Vector3 center = Vector3.zero;
            Vector3 outward = Vector3.zero;
            
            foreach (int idx in socketIndices)
            {
                center += platform.GetSocketWorldPosition(idx);
                
                var socket = platform.GetSocket(idx);
                Vector3 localOutward = new Vector3(socket.OutwardOffset.x, 0, socket.OutwardOffset.y);
                outward += platform.transform.TransformDirection(localOutward);
            }
            
            center /= socketIndices.Count;
            outward = outward.normalized;
            
            return new EdgeSegment
            {
                SocketIndices = socketIndices,
                EdgeIndex = edgeIndex,
                CenterPosition = center,
                OutwardDirection = outward
            };
        }
        
        /// <summary>
        /// Creates a single NavMesh link for an edge segment.
        /// To avoid duplicate links, only the platform with the HIGHER instance ID creates the link
        /// (when connecting to exactly one neighbor). If connecting to multiple neighbors, always create.
        /// </summary>
        private void CreateLinkForEdgeSegment(GamePlatform platform, EdgeSegment segment)
        {
            int thisId = platform.GetInstanceID();
            
            // Find all unique PLACED neighbors this segment connects to (excludes preview platforms)
            GamePlatform singleNeighbor = null;
            bool hasMultipleNeighbors = false;
            
            foreach (int socketIdx in segment.SocketIndices)
            {
                Vector2Int adjacentCell = platform.GetAdjacentCellForSocket(socketIdx);
                
                // Only consider placed platforms (not preview)
                if (!_platformManager.IsCellOccupied(adjacentCell, includeAllOccupation: false))
                    continue;
                    
                if (_platformManager.GetPlatformAtCell(adjacentCell, out var neighbor) && 
                    neighbor && neighbor != platform && !neighbor.IsPickedUp)
                {
                    if (singleNeighbor == null)
                    {
                        singleNeighbor = neighbor;
                    }
                    else if (singleNeighbor != neighbor)
                    {
                        hasMultipleNeighbors = true;
                        break;
                    }
                }
            }
            
            // If connecting to exactly one neighbor, apply the "lower ID creates link" rule
            if (!hasMultipleNeighbors && singleNeighbor != null)
            {
                if (singleNeighbor.GetInstanceID() < thisId)
                {
                    if (debugLogs)
                        Debug.Log($"[NavMeshManager] Skipping link for {platform.name} segment - neighbor {singleNeighbor.name} has lower ID");
                    return;
                }
            }
            
            EnsureLinksContainer();
            
            // Get the outward direction and calculate link endpoints
            Vector3 linkDirection = SnapToCardinal(segment.OutwardDirection);
            float platformY = platform.transform.position.y;
            
            // Link start is inside the platform, end is outside (at the boundary)
            Vector3 startPoint = segment.CenterPosition - linkDirection * linkDepth;
            Vector3 endPoint = segment.CenterPosition + linkDirection * linkDepth;
            
            startPoint.y = platformY + heightOffset;
            endPoint.y = platformY + heightOffset;
            
            // Align perpendicular coordinate
            if (Mathf.Abs(linkDirection.x) > Mathf.Abs(linkDirection.z))
            {
                startPoint.z = segment.CenterPosition.z;
                endPoint.z = segment.CenterPosition.z;
            }
            else
            {
                startPoint.x = segment.CenterPosition.x;
                endPoint.x = segment.CenterPosition.x;
            }
            
            // Calculate link width based on number of sockets
            float linkWidth = segment.SocketIndices.Count * widthPerSocket;
            
            // Create the link GameObject
            string edgeName = segment.EdgeIndex switch { 0 => "N", 1 => "S", 2 => "E", _ => "W" };
            string linkName = $"Link_{platform.name}_{edgeName}_{segment.SocketIndices[0]}-{segment.SocketIndices[^1]}";
            
            var go = new GameObject(linkName);
            go.transform.SetParent(_linksContainer, false);
            
            var link = go.AddComponent<NavMeshLink>();
            link.startPoint = startPoint;
            link.endPoint = endPoint;
            link.width = linkWidth;
            link.bidirectional = true;
            link.area = 0; // Walkable
            
            // Track link by platform
            var key = (thisId, thisId);
            if (!_linksByPlatform.ContainsKey(key))
                _linksByPlatform[key] = new List<GameObject>();
            _linksByPlatform[key].Add(go);
            
            if (debugLogs)
            {
                Debug.Log($"[NavMeshManager] Created link: {linkName}\n" +
                          $"  Start: {startPoint}, End: {endPoint}, Width: {linkWidth}m");
            }
            
            if (drawDebugGizmos)
            {
                Debug.DrawLine(startPoint, endPoint, Color.green, debugDrawDuration);
                Debug.DrawRay(startPoint, Vector3.up * 0.3f, Color.cyan, debugDrawDuration);
                Debug.DrawRay(endPoint, Vector3.up * 0.3f, Color.magenta, debugDrawDuration);
            }
        }
        
        #endregion
        
        
        #region Helpers
        
        private int GetEdgeIndex(int socketIndex, int[] edgeBounds)
        {
            for (int i = 0; i < 4; i++)
            {
                if (socketIndex >= edgeBounds[i] && socketIndex < edgeBounds[i + 1])
                    return i;
            }
            return 3;
        }
        
        private Vector3 SnapToCardinal(Vector3 dir)
        {
            float absX = Mathf.Abs(dir.x);
            float absZ = Mathf.Abs(dir.z);
            return absX > absZ 
                ? new Vector3(Mathf.Sign(dir.x), 0, 0) 
                : new Vector3(0, 0, Mathf.Sign(dir.z));
        }
        
        private void EnsureLinksContainer()
        {
            if (_linksContainer) return;
            
            var existing = GameObject.Find("NavMeshLinks");
            if (existing)
            {
                _linksContainer = existing.transform;
                return;
            }
            
            var go = new GameObject("NavMeshLinks");
            _linksContainer = go.transform;
            _linksContainer.position = Vector3.zero;
        }
        
        #endregion
    }
}
