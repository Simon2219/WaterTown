using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using Platforms;

/// <summary>
/// Manages NavMesh link creation between platforms.
/// All links are stored under a single global "NavMeshLinks" GameObject.
/// 
/// Links are created per SEGMENT - a continuous stretch of connected sockets on the same edge.
/// Link endpoints are positioned to ensure proper NavMesh connectivity.
/// </summary>
[DisallowMultipleComponent]
public class NavMeshLinkManager : MonoBehaviour
{
    #region Singleton
    
    private static NavMeshLinkManager _instance;
    public static NavMeshLinkManager Instance => _instance;
    
    #endregion
    
    #region Configuration
    
    [Header("Link Dimensions")]
    [Tooltip("Width per connected socket (meters). Default 1m matches grid cell size.")]
    [SerializeField] private float widthPerSocket = 1f;
    
    [Tooltip("How far the link extends INTO each platform from the boundary (meters).\n" +
             "Should be > agent radius to ensure endpoints land on valid NavMesh.")]
    [SerializeField] private float linkDepth = 0.5f;
    
    [Tooltip("Height offset above platform surface for link endpoints (meters).")]
    [SerializeField] private float heightOffset = 0.05f;
    
    [Header("Agent Type")]
    [Tooltip("NavMesh Agent Type for links. Must match your NPC agent type.")]
    [SerializeField] private NavMeshAgentType agentType;
    
    [Header("Debug")]
    [SerializeField] private bool debugLogs;
    [SerializeField] private bool drawDebugGizmos = true;
    [SerializeField] private float debugDrawDuration = 10f;
    
    #endregion
    
    #region Private Fields
    
    private Transform _linksContainer;
    
    // Track links by platform pair for easy cleanup
    private readonly Dictionary<(int, int), List<GameObject>> _linksByPlatformPair = new();
    
    #endregion
    
    #region Unity Lifecycle
    
    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        
        EnsureLinksContainer();
    }
    
    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }
    
    #endregion
    
    #region Public API
    
    /// <summary>
    /// Creates NavMesh links for a newly placed platform to all its neighbors.
    /// Call this ONLY when a platform is placed (not during preview/movement).
    /// </summary>
    public void CreateLinksForPlacedPlatform(GamePlatform platform, PlatformManager platformManager)
    {
        if (!platform || !platformManager) return;
        
        // Get all neighbors this platform connects to
        var neighbors = GetNeighborPlatforms(platform, platformManager);
        
        if (debugLogs)
            Debug.Log($"[NavMeshLinkManager] Creating links for {platform.name} to {neighbors.Count} neighbor(s)");
        
        foreach (var neighbor in neighbors)
        {
            CreateLinksBetweenPlatforms(platform, neighbor);
        }
    }
    
    /// <summary>
    /// Creates NavMesh links between two connected platforms.
    /// </summary>
    public void CreateLinksBetweenPlatforms(GamePlatform platformA, GamePlatform platformB)
    {
        if (!platformA || !platformB) return;
        
        // Clear existing links first
        ClearLinksBetween(platformA, platformB);
        
        // Get sockets that connect these platforms
        var socketsAToB = platformA.GetSocketsConnectedToNeighbor(platformB);
        var socketsBToA = platformB.GetSocketsConnectedToNeighbor(platformA);
        
        if (socketsAToB.Count == 0 || socketsBToA.Count == 0)
        {
            if (debugLogs) 
                Debug.Log($"[NavMeshLinkManager] No connections between {platformA.name} and {platformB.name}");
            return;
        }
        
        // Group sockets into segments
        var segmentsA = GroupSocketsIntoSegments(platformA, socketsAToB);
        var segmentsB = GroupSocketsIntoSegments(platformB, socketsBToA);
        
        if (debugLogs)
        {
            Debug.Log($"[NavMeshLinkManager] {platformA.name} <-> {platformB.name}:\n" +
                      $"  A: {socketsAToB.Count} sockets, {segmentsA.Count} segment(s)\n" +
                      $"  B: {socketsBToA.Count} sockets, {segmentsB.Count} segment(s)");
        }
        
        // Match segments and create links
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
                Debug.Log($"[NavMeshLinkManager] Cleared links between {platformA.name} and {platformB.name}");
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
            Debug.Log($"[NavMeshLinkManager] Cleared all links for {platform.name}");
    }
    
    #endregion
    
    #region Neighbor Discovery
    
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
    
    #region Segment Grouping
    
    private struct SegmentInfo
    {
        public List<int> SocketIndices;
        public Vector3 CenterPosition;      // Average socket world position
        public Vector3 OutwardDirection;    // Direction facing OUT of platform (normalized)
    }
    
    private List<SegmentInfo> GroupSocketsIntoSegments(GamePlatform platform, List<int> connectedSockets)
    {
        var segments = new List<SegmentInfo>();
        if (connectedSockets.Count == 0) return segments;
        
        // Get edge boundaries
        var footprint = platform.Footprint;
        int width = Mathf.Max(1, footprint.x);
        int length = Mathf.Max(1, footprint.y);
        int[] edgeBounds = { 0, width, width * 2, width * 2 + length, width * 2 + length * 2 };
        
        // Sort sockets
        var sorted = new List<int>(connectedSockets);
        sorted.Sort();
        
        // Group adjacent sockets on same edge
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
            // OutwardOffset is in local space (x, y as x, z)
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
    
    #region Link Creation
    
    private void CreateLinksForMatchedSegments(GamePlatform platformA, List<SegmentInfo> segmentsA,
                                                GamePlatform platformB, List<SegmentInfo> segmentsB)
    {
        var usedB = new HashSet<int>();
        
        // Match each A segment to closest B segment
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
        
        // Handle unmatched B segments
        for (int i = 0; i < segmentsB.Count; i++)
        {
            if (usedB.Contains(i)) continue;
            
            // Find closest A segment (allow reuse)
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
        
        // The boundary is exactly between the two segment centers
        Vector3 boundaryPoint = (segA.CenterPosition + segB.CenterPosition) * 0.5f;
        
        // Get platform surface height
        float surfaceY = Mathf.Max(platformA.transform.position.y, platformB.transform.position.y);
        
        // Determine link direction (from A to B)
        Vector3 linkDirection = SnapToCardinal(segA.OutwardDirection);
        
        // Calculate start and end points
        Vector3 startPoint = boundaryPoint - linkDirection * linkDepth;
        Vector3 endPoint = boundaryPoint + linkDirection * linkDepth;
        
        // Set Y to platform surface + offset
        startPoint.y = surfaceY + heightOffset;
        endPoint.y = surfaceY + heightOffset;
        
        // Ensure the perpendicular coordinate is identical (for straight link)
        if (Mathf.Abs(linkDirection.x) > Mathf.Abs(linkDirection.z))
        {
            // Link runs along X axis - Z should be same
            float avgZ = boundaryPoint.z;
            startPoint.z = avgZ;
            endPoint.z = avgZ;
        }
        else
        {
            // Link runs along Z axis - X should be same
            float avgX = boundaryPoint.x;
            startPoint.x = avgX;
            endPoint.x = avgX;
        }
        
        // Calculate width
        int socketCount = Mathf.Max(segA.SocketIndices.Count, segB.SocketIndices.Count);
        float linkWidth = socketCount * widthPerSocket;
        
        // Create link GameObject at ORIGIN (transform stays at 0,0,0)
        string linkName = $"Link_{platformA.name}_{platformB.name}_x{socketCount}";
        var go = new GameObject(linkName);
        go.transform.SetParent(_linksContainer, false);
        // Transform stays at origin - start/end are in world space
        
        // Add NavMeshLink - since transform is at origin, local coords = world coords
        var link = go.AddComponent<NavMeshLink>();
        link.startPoint = startPoint;
        link.endPoint = endPoint;
        link.width = linkWidth;
        link.bidirectional = true;
        link.area = 0; // Walkable
        link.agentTypeID = agentType.AgentTypeID;
        link.autoUpdatePosition = false; // Let agent traverse at normal speed
        
        // Track link
        var key = GetPlatformPairKey(platformA, platformB);
        if (!_linksByPlatformPair.ContainsKey(key))
            _linksByPlatformPair[key] = new List<GameObject>();
        _linksByPlatformPair[key].Add(go);
        
        if (debugLogs)
        {
            Debug.Log($"[NavMeshLinkManager] Created: {linkName}\n" +
                      $"  Start: {startPoint}, End: {endPoint}\n" +
                      $"  Width: {linkWidth}m, Height: {surfaceY + heightOffset}");
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
        
        if (absX > absZ)
            return new Vector3(Mathf.Sign(dir.x), 0, 0);
        else
            return new Vector3(0, 0, Mathf.Sign(dir.z));
    }
    
    #endregion
    
    #region Helpers
    
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
}
