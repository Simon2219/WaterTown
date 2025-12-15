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
        private readonly Dictionary<(int, int), List<GameObject>> _linksByPlatformPair = new();
        
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
        
        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
        
        #endregion
        
        
        #region Public API - Link Management
        
        /// <summary>
        /// Creates NavMesh links for a platform using the consolidated per-edge approach.
        /// Creates ONE link per contiguous connected segment on each edge, regardless of which
        /// neighbor(s) those sockets connect to. This prevents the "step back before crossing"
        /// issue when adjacent sockets connect to different platforms.
        /// </summary>
        public void CreateLinksForPlatform(GamePlatform platform, PlatformManager platformManager)
        {
            if (!platform || !platformManager) return;
            
            // Clear existing links for this platform first
            ClearAllLinksForPlatform(platform);
            
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
                    CreateLinkForEdgeSegment(platform, segment, platformManager);
                }
            }
        }
        
        /// <summary>
        /// Clears all links involving a specific platform.
        /// </summary>
        public void ClearAllLinksForPlatform(GamePlatform platform)
        {
            if (!platform) return;
            
            int platformId = platform.GetInstanceID();
            var keysToRemove = new List<(int, int)>();
            
            foreach (var kvp in _linksByPlatformPair)
            {
                if (kvp.Key.Item1 == platformId || kvp.Key.Item2 == platformId)
                {
                    foreach (var go in kvp.Value)
                    {
                        if (go)
                        {
                            if (Application.isPlaying)
                                Destroy(go);
                            else
                                DestroyImmediate(go);
                        }
                    }
                    keysToRemove.Add(kvp.Key);
                }
            }
            
            foreach (var key in keysToRemove)
                _linksByPlatformPair.Remove(key);
            
            if (debugLogs && keysToRemove.Count > 0)
                Debug.Log($"[NavMeshManager] Cleared all links for {platform.name}");
        }
        
        #endregion
        
        
        #region Link Creation - Edge Segments
        
        /// <summary>
        /// Gets all connected sockets grouped by edge, with contiguous sockets grouped into segments.
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
            
            // Collect all connected socket indices per edge
            var connectedByEdge = new Dictionary<int, List<int>>();
            for (int i = 0; i < sockets.Count; i++)
            {
                if (!platform.IsSocketConnected(i)) continue;
                
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
        /// The link spans the entire segment width, regardless of how many different neighbors connect.
        /// To avoid duplicate links at boundaries, only the platform with the HIGHER instance ID creates the link
        /// (when connecting to exactly one neighbor). If connecting to multiple neighbors, always create the link.
        /// </summary>
        private void CreateLinkForEdgeSegment(GamePlatform platform, EdgeSegment segment, PlatformManager platformManager)
        {
            int thisId = platform.GetInstanceID();
            
            // Find all unique neighbors this segment connects to
            GamePlatform singleNeighbor = null;
            bool hasMultipleNeighbors = false;
            
            foreach (int socketIdx in segment.SocketIndices)
            {
                Vector2Int adjacentCell = platform.GetAdjacentCellForSocket(socketIdx);
                if (platformManager.GetPlatformAtCell(adjacentCell, out var neighbor) && neighbor && neighbor != platform)
                {
                    if (singleNeighbor == null)
                    {
                        singleNeighbor = neighbor;
                    }
                    else if (singleNeighbor != neighbor)
                    {
                        hasMultipleNeighbors = true;
                        break; // No need to check further
                    }
                }
            }
            
            // If connecting to exactly one neighbor, apply the "lower ID creates link" rule to avoid duplicates
            // If connecting to multiple neighbors, always create the link (can't use simple ID comparison)
            if (!hasMultipleNeighbors && singleNeighbor != null)
            {
                if (singleNeighbor.GetInstanceID() < thisId)
                {
                    if (debugLogs)
                        Debug.Log($"[NavMeshManager] Skipping link for {platform.name} segment - neighbor {singleNeighbor.name} has lower ID and will create it");
                    return; // Let the neighbor with lower ID create this link
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
            if (!_linksByPlatformPair.ContainsKey(key))
                _linksByPlatformPair[key] = new List<GameObject>();
            _linksByPlatformPair[key].Add(go);
            
            if (debugLogs)
            {
                Debug.Log($"[NavMeshManager] Created link: {linkName}\n" +
                          $"  Start: {startPoint}, End: {endPoint}, Width: {linkWidth}m, Sockets: {segment.SocketIndices.Count}");
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
