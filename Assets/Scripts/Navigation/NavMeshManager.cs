using System.Collections;
using System.Collections.Generic;
using Platforms;
using Unity.AI.Navigation;
using UnityEngine;

namespace Navigation
{
    /// <summary>
    /// Unified NavMesh management for the entire town.
    /// 
    /// Primary: Global NavMeshSurface baking
    /// - All platform floor colliders on NavMeshGeometry layer are baked together
    /// - Adjacent platforms automatically connect (no links needed for same-level)
    /// - Call RebuildNavMesh() when platforms are placed/removed
    /// 
    /// Secondary: NavMeshLink management (for special cases)
    /// - Multi-level platforms with height differences
    /// - Special jump/climb points
    /// - One-way traversals
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavMeshSurface))]
    public class NavMeshManager : MonoBehaviour
    {
        #region Singleton
        
        private static NavMeshManager _instance;
        public static NavMeshManager Instance => _instance;
        
        #endregion
        
        
        #region Configuration - Surface
        
        [Header("Layer Configuration")]
        [Tooltip("The layer used for NavMesh floor geometry colliders.")]
        [SerializeField] private string navMeshGeometryLayerName = "NavMeshGeometry";
        
        [Header("Rebuild Settings")]
        [Tooltip("Delay before rebuilding NavMesh after a change (allows multiple changes to batch).")]
        [SerializeField] private float rebuildDelay = 0.1f;
        
        [Tooltip("Use async NavMesh building (non-blocking but slight delay).")]
        [SerializeField] private bool useAsyncBuild = true;
        
        #endregion
        
        
        #region Configuration - Links (for multi-level)
        
        [Header("Link Dimensions (Multi-Level Only)")]
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
        
        private NavMeshSurface _surface;
        private Coroutine _pendingRebuild;
        private AsyncOperation _asyncOperation;
        private bool _isRebuilding;
        
        // Links container and tracking
        private Transform _linksContainer;
        private readonly Dictionary<(int, int), List<GameObject>> _linksByPlatformPair = new();
        
        #endregion
        
        
        #region Properties
        
        /// <summary>True if a NavMesh rebuild is currently in progress.</summary>
        public bool IsRebuilding => _isRebuilding;
        
        /// <summary>The NavMeshSurface component used for global baking.</summary>
        public NavMeshSurface Surface => _surface;
        
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
            
            _surface = GetComponent<NavMeshSurface>();
            if (!_surface)
            {
                Debug.LogError("[NavMeshManager] NavMeshSurface component not found!");
                return;
            }
            
            ConfigureSurface();
        }
        
        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
        
        #endregion
        
        
        #region Surface Configuration
        
        /// <summary>
        /// Configures the NavMeshSurface to use the NavMeshGeometry layer.
        /// </summary>
        private void ConfigureSurface()
        {
            if (!_surface) return;
            
            int layer = LayerMask.NameToLayer(navMeshGeometryLayerName);
            if (layer == -1)
            {
                Debug.LogError($"[NavMeshManager] Layer '{navMeshGeometryLayerName}' not found! " +
                               "Please create this layer in Edit > Project Settings > Tags and Layers.");
                return;
            }
            
            // Configure surface to collect from scene, using only NavMeshGeometry layer
            _surface.collectObjects = CollectObjects.All;
            _surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            _surface.layerMask = 1 << layer;
            
            if (debugLogs)
                Debug.Log($"[NavMeshManager] Configured to use layer '{navMeshGeometryLayerName}' (index {layer})");
        }
        
        #endregion
        
        
        #region Public API - NavMesh Rebuilding
        
        /// <summary>
        /// Queues a NavMesh rebuild. Multiple calls within rebuildDelay are batched together.
        /// </summary>
        public void RebuildNavMesh()
        {
            if (!_surface)
            {
                Debug.LogWarning("[NavMeshManager] Cannot rebuild - no NavMeshSurface.");
                return;
            }
            
            if (!gameObject.activeInHierarchy)
            {
                Debug.LogWarning("[NavMeshManager] Cannot rebuild - manager is inactive.");
                return;
            }
            
            // Cancel pending rebuild to batch changes
            if (_pendingRebuild != null)
                StopCoroutine(_pendingRebuild);
            
            _pendingRebuild = StartCoroutine(RebuildAfterDelay());
        }
        
        /// <summary>
        /// Immediately rebuilds the NavMesh (blocking).
        /// Use RebuildNavMesh() for normal use to allow batching.
        /// </summary>
        public void RebuildNavMeshImmediate()
        {
            if (!_surface)
            {
                Debug.LogWarning("[NavMeshManager] Cannot rebuild - no NavMeshSurface.");
                return;
            }
            
            if (_pendingRebuild != null)
            {
                StopCoroutine(_pendingRebuild);
                _pendingRebuild = null;
            }
            
            PerformRebuild();
        }
        
        private IEnumerator RebuildAfterDelay()
        {
            yield return new WaitForSeconds(rebuildDelay);
            _pendingRebuild = null;
            PerformRebuild();
        }
        
        private void PerformRebuild()
        {
            if (_isRebuilding)
            {
                if (debugLogs)
                    Debug.Log("[NavMeshManager] Rebuild already in progress, queuing another.");
                RebuildNavMesh();
                return;
            }
            
            _isRebuilding = true;
            
            if (useAsyncBuild)
            {
                StartCoroutine(RebuildAsync());
            }
            else
            {
                _surface.BuildNavMesh();
                _isRebuilding = false;
                if (debugLogs)
                    Debug.Log("[NavMeshManager] NavMesh rebuilt (sync).");
            }
        }
        
        private IEnumerator RebuildAsync()
        {
            _asyncOperation = _surface.UpdateNavMesh(_surface.navMeshData);
            
            while (!_asyncOperation.isDone)
                yield return null;
            
            _isRebuilding = false;
            _asyncOperation = null;
            
            if (debugLogs)
                Debug.Log("[NavMeshManager] NavMesh rebuilt (async).");
        }
        
        #endregion
        
        
        #region Public API - Link Management (Multi-Level)
        
        /// <summary>
        /// Creates NavMesh links for a platform to all its neighbors.
        /// Only needed for multi-level connections (same-level handled by global bake).
        /// </summary>
        public void CreateLinksForPlatform(GamePlatform platform, PlatformManager platformManager)
        {
            if (!platform || !platformManager) return;
            
            var neighbors = GetNeighborPlatforms(platform, platformManager);
            
            if (debugLogs)
                Debug.Log($"[NavMeshManager] Creating links for {platform.name} to {neighbors.Count} neighbor(s)");
            
            foreach (var neighbor in neighbors)
            {
                // Only create links if there's a height difference
                float heightDiff = Mathf.Abs(platform.transform.position.y - neighbor.transform.position.y);
                if (heightDiff > 0.1f)
                {
                    CreateLinksBetweenPlatforms(platform, neighbor);
                }
            }
        }
        
        /// <summary>
        /// Creates NavMesh links between two platforms (for multi-level connections).
        /// </summary>
        public void CreateLinksBetweenPlatforms(GamePlatform platformA, GamePlatform platformB)
        {
            if (!platformA || !platformB) return;
            
            ClearLinksBetween(platformA, platformB);
            
            var socketsAToB = platformA.GetSocketsConnectedToNeighbor(platformB);
            var socketsBToA = platformB.GetSocketsConnectedToNeighbor(platformA);
            
            if (socketsAToB.Count == 0 || socketsBToA.Count == 0)
            {
                if (debugLogs) 
                    Debug.Log($"[NavMeshManager] No connections between {platformA.name} and {platformB.name}");
                return;
            }
            
            var segmentsA = GroupSocketsIntoSegments(platformA, socketsAToB);
            var segmentsB = GroupSocketsIntoSegments(platformB, socketsBToA);
            
            if (debugLogs)
            {
                Debug.Log($"[NavMeshManager] {platformA.name} <-> {platformB.name}:\n" +
                          $"  A: {socketsAToB.Count} sockets, {segmentsA.Count} segment(s)\n" +
                          $"  B: {socketsBToA.Count} sockets, {segmentsB.Count} segment(s)");
            }
            
            CreateLinksForMatchedSegments(platformA, segmentsA, platformB, segmentsB);
        }
        
        /// <summary>
        /// Clears all NavMesh links between two platforms.
        /// </summary>
        public void ClearLinksBetween(GamePlatform platformA, GamePlatform platformB)
        {
            if (!platformA || !platformB) return;
            
            var key = GetPlatformPairKey(platformA, platformB);
            
            if (_linksByPlatformPair.TryGetValue(key, out var links))
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
                _linksByPlatformPair.Remove(key);
                
                if (debugLogs)
                    Debug.Log($"[NavMeshManager] Cleared links between {platformA.name} and {platformB.name}");
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
        
        
        #region Link Helpers - Neighbor Discovery
        
        private HashSet<GamePlatform> GetNeighborPlatforms(GamePlatform platform, PlatformManager platformManager)
        {
            var neighbors = new HashSet<GamePlatform>();
            
            var sockets = platform.Sockets;
            if (sockets == null) return neighbors;
            
            for (int i = 0; i < sockets.Count; i++)
            {
                if (!platform.IsSocketConnected(i)) continue;
                
                Vector2Int adjacentCell = platform.GetAdjacentCellForSocket(i);
                if (platformManager.GetPlatformAtCell(adjacentCell, out var neighbor))
                {
                    if (neighbor && neighbor != platform)
                        neighbors.Add(neighbor);
                }
            }
            
            return neighbors;
        }
        
        #endregion
        
        
        #region Link Helpers - Segment Grouping
        
        private struct SegmentInfo
        {
            public List<int> SocketIndices;
            public Vector3 CenterPosition;
            public Vector3 OutwardDirection;
        }
        
        private List<SegmentInfo> GroupSocketsIntoSegments(GamePlatform platform, List<int> connectedSockets)
        {
            var segments = new List<SegmentInfo>();
            if (connectedSockets.Count == 0) return segments;
            
            var footprint = platform.Footprint;
            int width = Mathf.Max(1, footprint.x);
            int length = Mathf.Max(1, footprint.y);
            int[] edgeBounds = { 0, width, width * 2, width * 2 + length, width * 2 + length * 2 };
            
            var sorted = new List<int>(connectedSockets);
            sorted.Sort();
            
            var currentGroup = new List<int> { sorted[0] };
            int currentEdge = GetEdgeIndex(sorted[0], edgeBounds);
            
            for (int i = 1; i < sorted.Count; i++)
            {
                int socket = sorted[i];
                int edge = GetEdgeIndex(socket, edgeBounds);
                bool adjacent = (socket - sorted[i - 1]) == 1;
                
                if (adjacent && edge == currentEdge)
                {
                    currentGroup.Add(socket);
                }
                else
                {
                    segments.Add(CreateSegmentInfo(platform, currentGroup));
                    currentGroup = new List<int> { socket };
                    currentEdge = edge;
                }
            }
            
            segments.Add(CreateSegmentInfo(platform, currentGroup));
            return segments;
        }
        
        private SegmentInfo CreateSegmentInfo(GamePlatform platform, List<int> socketIndices)
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
            
            return new SegmentInfo
            {
                SocketIndices = socketIndices,
                CenterPosition = center,
                OutwardDirection = outward
            };
        }
        
        private int GetEdgeIndex(int socketIndex, int[] edgeBounds)
        {
            for (int i = 0; i < 4; i++)
            {
                if (socketIndex >= edgeBounds[i] && socketIndex < edgeBounds[i + 1])
                    return i;
            }
            return 3;
        }
        
        #endregion
        
        
        #region Link Helpers - Link Creation
        
        private void CreateLinksForMatchedSegments(GamePlatform platformA, List<SegmentInfo> segmentsA,
                                                    GamePlatform platformB, List<SegmentInfo> segmentsB)
        {
            var usedB = new HashSet<int>();
            
            foreach (var segA in segmentsA)
            {
                int bestIdx = -1;
                float bestDist = float.MaxValue;
                
                for (int i = 0; i < segmentsB.Count; i++)
                {
                    if (usedB.Contains(i)) continue;
                    float dist = Vector3.Distance(segA.CenterPosition, segmentsB[i].CenterPosition);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = i;
                    }
                }
                
                if (bestIdx >= 0)
                {
                    usedB.Add(bestIdx);
                    CreateLinkForSegmentPair(platformA, segA, platformB, segmentsB[bestIdx]);
                }
            }
            
            for (int i = 0; i < segmentsB.Count; i++)
            {
                if (usedB.Contains(i)) continue;
                
                int bestIdx = 0;
                float bestDist = float.MaxValue;
                
                for (int j = 0; j < segmentsA.Count; j++)
                {
                    float dist = Vector3.Distance(segmentsB[i].CenterPosition, segmentsA[j].CenterPosition);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = j;
                    }
                }
                
                CreateLinkForSegmentPair(platformA, segmentsA[bestIdx], platformB, segmentsB[i]);
            }
        }
        
        private void CreateLinkForSegmentPair(GamePlatform platformA, SegmentInfo segA,
                                               GamePlatform platformB, SegmentInfo segB)
        {
            EnsureLinksContainer();
            
            Vector3 boundaryPoint = (segA.CenterPosition + segB.CenterPosition) * 0.5f;
            float surfaceY = Mathf.Max(platformA.transform.position.y, platformB.transform.position.y);
            Vector3 linkDirection = SnapToCardinal(segA.OutwardDirection);
            
            Vector3 startPoint = boundaryPoint - linkDirection * linkDepth;
            Vector3 endPoint = boundaryPoint + linkDirection * linkDepth;
            
            startPoint.y = surfaceY + heightOffset;
            endPoint.y = surfaceY + heightOffset;
            
            if (Mathf.Abs(linkDirection.x) > Mathf.Abs(linkDirection.z))
            {
                float avgZ = boundaryPoint.z;
                startPoint.z = avgZ;
                endPoint.z = avgZ;
            }
            else
            {
                float avgX = boundaryPoint.x;
                startPoint.x = avgX;
                endPoint.x = avgX;
            }
            
            int socketCount = Mathf.Max(segA.SocketIndices.Count, segB.SocketIndices.Count);
            float linkWidth = socketCount * widthPerSocket;
            
            string linkName = $"Link_{platformA.name}_{platformB.name}_x{socketCount}";
            var go = new GameObject(linkName);
            go.transform.SetParent(_linksContainer, false);
            
            var link = go.AddComponent<NavMeshLink>();
            link.startPoint = startPoint;
            link.endPoint = endPoint;
            link.width = linkWidth;
            link.bidirectional = true;
            link.area = 0;
            
            var key = GetPlatformPairKey(platformA, platformB);
            if (!_linksByPlatformPair.ContainsKey(key))
                _linksByPlatformPair[key] = new List<GameObject>();
            _linksByPlatformPair[key].Add(go);
            
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
        
        private (int, int) GetPlatformPairKey(GamePlatform a, GamePlatform b)
        {
            int idA = a.GetInstanceID();
            int idB = b.GetInstanceID();
            return idA < idB ? (idA, idB) : (idB, idA);
        }
        
        #endregion
        
        
        #region Editor Helpers
        
#if UNITY_EDITOR
        [ContextMenu("Rebuild NavMesh Now")]
        private void EditorRebuildNavMesh()
        {
            ConfigureSurface();
            _surface.BuildNavMesh();
            Debug.Log("[NavMeshManager] NavMesh rebuilt from editor.");
        }
        
        private void OnValidate()
        {
            if (_surface)
                ConfigureSurface();
        }
#endif
        
        #endregion
    }
}
