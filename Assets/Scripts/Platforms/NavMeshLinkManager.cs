using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using Platforms;

/// <summary>
/// Manages NavMesh link creation between platforms.
/// All links are stored under a single global GameObject for easy management.
/// 
/// Links are created per SEGMENT - a segment is a continuous stretch of connected sockets
/// on the same edge, connecting to the SAME target platform.
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
    
    [Tooltip("How far the link extends INTO each platform from the edge (meters).\n" +
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
    
    #endregion
    
    #region Private Fields
    
    private Transform _linksContainer;
    
    // Track links by platform pair for easy cleanup
    // Key: (platformA.GetInstanceID(), platformB.GetInstanceID()) sorted
    private readonly Dictionary<(int, int), List<NavMeshLink>> _linksByPlatformPair = new();
    
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
    /// Creates NavMesh links between two connected platforms.
    /// Called when platforms detect they are neighbors.
    /// </summary>
    public void CreateLinksBetweenPlatforms(GamePlatform platformA, GamePlatform platformB)
    {
        if (!platformA || !platformB) return;
        
        // Clear existing links between these platforms
        ClearLinksBetween(platformA, platformB);
        
        // Get sockets from each platform that connect to the other
        var socketsAToB = platformA.GetSocketsConnectedToNeighbor(platformB);
        var socketsBToA = platformB.GetSocketsConnectedToNeighbor(platformA);
        
        if (socketsAToB.Count == 0 || socketsBToA.Count == 0)
        {
            if (debugLogs) Debug.Log($"[NavMeshLinkManager] No connections between {platformA.name} and {platformB.name}");
            return;
        }
        
        // Group sockets into segments (adjacent sockets on same edge)
        var segmentsA = GroupSocketsIntoSegments(platformA, socketsAToB);
        var segmentsB = GroupSocketsIntoSegments(platformB, socketsBToA);
        
        if (debugLogs)
        {
            Debug.Log($"[NavMeshLinkManager] Creating links: {platformA.name} <-> {platformB.name}\n" +
                      $"  A: {socketsAToB.Count} sockets in {segmentsA.Count} segment(s)\n" +
                      $"  B: {socketsBToA.Count} sockets in {segmentsB.Count} segment(s)");
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
            int count = links.Count;
            foreach (var link in links)
            {
                if (link && link.gameObject)
                {
                    if (Application.isPlaying)
                        Destroy(link.gameObject);
                    else
                        DestroyImmediate(link.gameObject);
                }
            }
            _linksByPlatformPair.Remove(key);
            
            if (debugLogs && count > 0)
                Debug.Log($"[NavMeshLinkManager] Cleared {count} link(s) between {platformA.name} and {platformB.name}");
        }
    }
    
    /// <summary>
    /// Clears all links involving a specific platform.
    /// Call when a platform is removed.
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
                foreach (var link in kvp.Value)
                {
                    if (link && link.gameObject)
                    {
                        if (Application.isPlaying)
                            Destroy(link.gameObject);
                        else
                            DestroyImmediate(link.gameObject);
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
    
    #region Segment Grouping
    
    /// <summary>
    /// Groups connected sockets into segments.
    /// A segment is a continuous run of adjacent sockets on the same edge.
    /// </summary>
    private List<SegmentData> GroupSocketsIntoSegments(GamePlatform platform, List<int> connectedSockets)
    {
        var segments = new List<SegmentData>();
        if (connectedSockets.Count == 0) return segments;
        
        // Get edge boundaries for this platform's footprint
        var footprint = platform.Footprint;
        int width = Mathf.Max(1, footprint.x);
        int length = Mathf.Max(1, footprint.y);
        
        // Edge boundaries: North[0,width), South[width,2*width), East[2*width,2*width+length), West[2*width+length,...)
        int[] edgeBounds = { 0, width, width * 2, width * 2 + length, width * 2 + length * 2 };
        
        // Sort sockets and group by edge first
        var sortedSockets = new List<int>(connectedSockets);
        sortedSockets.Sort();
        
        // Group into segments: adjacent indices on same edge
        var currentSegment = new List<int> { sortedSockets[0] };
        int currentEdge = GetEdgeIndex(sortedSockets[0], edgeBounds);
        
        for (int i = 1; i < sortedSockets.Count; i++)
        {
            int socket = sortedSockets[i];
            int edge = GetEdgeIndex(socket, edgeBounds);
            int prevSocket = sortedSockets[i - 1];
            
            // Check if this socket continues the current segment
            bool isAdjacent = (socket - prevSocket) == 1;
            bool sameEdge = edge == currentEdge;
            
            if (isAdjacent && sameEdge)
            {
                currentSegment.Add(socket);
            }
            else
            {
                // Finish current segment and start new one
                segments.Add(CreateSegmentData(platform, currentSegment, currentEdge));
                currentSegment = new List<int> { socket };
                currentEdge = edge;
            }
        }
        
        // Add final segment
        segments.Add(CreateSegmentData(platform, currentSegment, currentEdge));
        
        return segments;
    }
    
    private SegmentData CreateSegmentData(GamePlatform platform, List<int> socketIndices, int edgeIndex)
    {
        // Calculate segment center and direction
        Vector3 center = Vector3.zero;
        Vector3 outwardDir = Vector3.zero;
        
        foreach (int idx in socketIndices)
        {
            center += platform.GetSocketWorldPosition(idx);
            var socket = platform.GetSocket(idx);
            outwardDir += new Vector3(socket.OutwardOffset.x, 0, socket.OutwardOffset.y);
        }
        
        center /= socketIndices.Count;
        outwardDir = platform.transform.TransformDirection(outwardDir.normalized);
        
        // Calculate segment width direction (perpendicular to outward, along the edge)
        Vector3 widthDir = Vector3.Cross(Vector3.up, outwardDir).normalized;
        
        return new SegmentData
        {
            SocketIndices = socketIndices,
            EdgeIndex = edgeIndex,
            Center = center,
            OutwardDirection = outwardDir,
            WidthDirection = widthDir,
            SocketCount = socketIndices.Count
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
    
    private struct SegmentData
    {
        public List<int> SocketIndices;
        public int EdgeIndex;
        public Vector3 Center;
        public Vector3 OutwardDirection;
        public Vector3 WidthDirection;
        public int SocketCount;
    }
    
    #endregion
    
    #region Link Creation
    
    private void CreateLinksForMatchedSegments(GamePlatform platformA, List<SegmentData> segmentsA,
                                                GamePlatform platformB, List<SegmentData> segmentsB)
    {
        var usedB = new HashSet<int>();
        
        // Match each segment from A to closest segment from B
        foreach (var segA in segmentsA)
        {
            int bestIdx = -1;
            float bestDist = float.MaxValue;
            
            for (int i = 0; i < segmentsB.Count; i++)
            {
                if (usedB.Contains(i)) continue;
                
                float dist = Vector3.Distance(segA.Center, segmentsB[i].Center);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }
            
            if (bestIdx >= 0)
            {
                usedB.Add(bestIdx);
                CreateLink(platformA, segA, platformB, segmentsB[bestIdx]);
            }
        }
        
        // Handle any unmatched B segments
        for (int i = 0; i < segmentsB.Count; i++)
        {
            if (usedB.Contains(i)) continue;
            
            // Find closest A segment (allow reuse)
            var segB = segmentsB[i];
            int bestIdx = 0;
            float bestDist = float.MaxValue;
            
            for (int j = 0; j < segmentsA.Count; j++)
            {
                float dist = Vector3.Distance(segB.Center, segmentsA[j].Center);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = j;
                }
            }
            
            CreateLink(platformA, segmentsA[bestIdx], platformB, segB);
        }
    }
    
    private void CreateLink(GamePlatform platformA, SegmentData segA,
                            GamePlatform platformB, SegmentData segB)
    {
        EnsureLinksContainer();
        
        // Use the larger socket count for width
        int socketCount = Mathf.Max(segA.SocketCount, segB.SocketCount);
        float linkWidth = socketCount * widthPerSocket;
        
        // Get platform surface height (use platform's transform Y as base)
        float surfaceY = Mathf.Max(platformA.transform.position.y, platformB.transform.position.y);
        
        // Calculate link endpoints:
        // Start: segment A center, moved INWARD (opposite of outward direction)
        // End: segment B center, moved INWARD
        Vector3 startPoint = segA.Center - segA.OutwardDirection * linkDepth;
        startPoint.y = surfaceY + heightOffset;
        
        Vector3 endPoint = segB.Center - segB.OutwardDirection * linkDepth;
        endPoint.y = surfaceY + heightOffset;
        
        // Create link GameObject
        string linkName = $"Link_{platformA.name}_to_{platformB.name}_x{socketCount}";
        var go = new GameObject(linkName);
        go.transform.SetParent(_linksContainer, true);
        
        // Position at midpoint
        Vector3 midpoint = (startPoint + endPoint) * 0.5f;
        go.transform.position = midpoint;
        
        // Align rotation to the connection direction (snapped to 90 degrees)
        Vector3 linkDir = (endPoint - startPoint).normalized;
        if (linkDir.sqrMagnitude > 0.001f)
        {
            // Snap direction to nearest 90-degree axis
            linkDir = SnapToCardinalDirection(linkDir);
            go.transform.rotation = Quaternion.LookRotation(linkDir, Vector3.up);
        }
        
        // Add NavMeshLink component
        var link = go.AddComponent<NavMeshLink>();
        link.startPoint = go.transform.InverseTransformPoint(startPoint);
        link.endPoint = go.transform.InverseTransformPoint(endPoint);
        link.width = linkWidth;
        link.bidirectional = true;
        link.area = 0; // Walkable
        link.agentTypeID = agentType.AgentTypeID;
        
        // Track this link
        var key = GetPlatformPairKey(platformA, platformB);
        if (!_linksByPlatformPair.ContainsKey(key))
            _linksByPlatformPair[key] = new List<NavMeshLink>();
        _linksByPlatformPair[key].Add(link);
        
        if (debugLogs)
        {
            Debug.Log($"[NavMeshLinkManager] Created: {linkName}\n" +
                      $"  Start: {startPoint}, End: {endPoint}\n" +
                      $"  Width: {linkWidth}m, Sockets: {socketCount}");
        }
        
        if (drawDebugGizmos)
        {
            Debug.DrawLine(startPoint, endPoint, Color.green, 10f);
            Debug.DrawRay(startPoint, Vector3.up * 0.5f, Color.cyan, 10f);
            Debug.DrawRay(endPoint, Vector3.up * 0.5f, Color.magenta, 10f);
        }
    }
    
    /// <summary>
    /// Snaps a direction vector to the nearest cardinal direction (90-degree aligned).
    /// </summary>
    private Vector3 SnapToCardinalDirection(Vector3 dir)
    {
        // Find which axis has the largest component
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
        
        // Look for existing container
        var existing = GameObject.Find("NavMeshLinks");
        if (existing)
        {
            _linksContainer = existing.transform;
            return;
        }
        
        // Create new container
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
