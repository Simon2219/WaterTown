using System;
using System.Collections.Generic;
using System.Linq;
using Grid;
using UnityEngine;

namespace Platforms
{
    
    
    
/// <summary>
/// Manages socket creation, status, grid adjacency, connections, and module registry for a platform
/// Handles all socket-related logic including neighbor detection and connection state
/// </summary>
/// 
[DisallowMultipleComponent]
public class PlatformSocketSystem : MonoBehaviour
{
    #region Configuration
    
    
    private GamePlatform _platform;
    private WorldGrid _worldGrid;
    
    
    [Header("Sockets")]
    private readonly Dictionary<int, SocketData> _platformSockets = new();

    public IReadOnlyDictionary<int, SocketData> PlatformSockets => _platformSockets;
    
    public int SocketCount => _platformSockets.Count;
    
    
    #endregion
    
    
    #region Events
    
    /// Fired when any socket status changes
    public event Action SocketsChanged;
    
    
    #endregion
    
    
    #region Socket Enums & Data Structures
    
    
    // Edge enum (for compatibility with PlatformModule)
    public enum Edge { North, East, South, West }
    
    // Socket status and location enums
    public enum SocketStatus
    {
        Linkable, 
        Occupied, 
        Connected, 
        Locked, 
        Disabled
    }
    
    
    /// Socket data structure
    /// Each socket knows its local position, world position, and which direction faces outward
    [Serializable]
    public class SocketData
    {
        [SerializeField, HideInInspector] private int index;
        [SerializeField, HideInInspector] private Vector3 localPos; // Fixed relative to platform center
        [SerializeField, HideInInspector] private Vector2Int outwardOffset;
        [SerializeField] private SocketStatus status;
        [SerializeField, HideInInspector] private PlatformModule attachedModule;
        
        public bool IsLinkable => status == SocketStatus.Linkable;
        public bool IsOccupied => status == SocketStatus.Occupied;
        public bool IsConnected => status == SocketStatus.Connected;
        public bool IsLocked => status == SocketStatus.Locked;
        public bool IsDisabled => status == SocketStatus.Disabled;
        

        public int Index
        { 
            get => index; 
            private set => index = value;
        }

        public Vector3 LocalPos
        {
            get => localPos;
            private set => localPos = value;
        }

        public Vector3 WorldPos 
        { 
            get; 
            private set; 
        }

        public Vector2Int OutwardOffset
        {
            get => outwardOffset;
            set => outwardOffset = value;
        }

        public SocketStatus Status
        {
            get => status;
            set => status = value;
        }

        public PlatformModule AttachedModule
        {
            get => attachedModule;
            set => attachedModule = value;
        }

        public SocketData(int idx, Vector3 lp, Vector2Int outward, SocketStatus defaultStatus)
        {
            index = idx;
            localPos = lp;
            outwardOffset = outward;
            status = defaultStatus;
            WorldPos = Vector3.zero;
        }
        

        public void SetStatus(SocketStatus s) => Status = s;
        
        public SocketStatus GetStatus() => Status;
        
        internal void SetWorldPosition(Vector3 pos) => WorldPos = pos;
    }
    
    
    #endregion
    
    
    #region Initialization & Lifecycle

 
    public void Initialize(GamePlatform platform, WorldGrid worldGrid)
    {
        _platform = platform;
        _worldGrid = worldGrid;
        
        // Subscribe to platform movement
        // Remove first to avoid double subscription
        _platform.HasMoved -= OnPlatformMoved;
        _platform.HasMoved += OnPlatformMoved;
        
        ReBuildSockets();
        RefreshAllSocketStatuses();
    }
    
    
    
    private void OnDestroy()
    {
        _platform.HasMoved -= OnPlatformMoved;
    }
    
    
    
    private void OnPlatformMoved(GamePlatform platform)
    {
        UpdateSocketPositions();
    }
    
    
    #endregion
    
    
    #region Socket Building
    
    
    
    /// Build sockets along the perimeter of the footprint
    /// In local space - one socket per cell edge segment
    /// Order: Clockwise starting from NW corner
    /// North → East → South → West
    /// Adjacent sockets are always index ±1 (with wrap-around)
    ///
    public void ReBuildSockets(Vector2Int footprintSize = default)
    {
        if(footprintSize == default) footprintSize = _platform.Footprint;
        
        // Preserve existing socket statuses when rebuilding
        var previousStatuses = new Dictionary<int, SocketStatus>();
        foreach (var s in _platformSockets.Values)
            previousStatuses[s.Index] = s.Status;

        BuildSockets(footprintSize);
        SetSocketStatus(previousStatuses);
        
        UpdateSocketPositions();
    }


    /// Builds sockets in clockwise order starting from NW corner
    /// Edge order: North → East → South → West
    /// Layout for 4x4:
    ///            North (→ indices increase)
    ///        ┌── 0   1   2   3 ───┐
    ///        │                    │
    /// West ↑ 15                   4 ↓ East
    ///        14                   5
    ///        13                   6
    ///        12                   7
    ///        │                    │
    ///        └── 11  10   9   8 ──┘
    ///            South (← indices increase)
    ///
    private void BuildSockets(Vector2Int footprintSize)
    {
        _platformSockets.Clear();

        int width = footprintSize.x;
        int length = footprintSize.y;
        
        float halfCellSize = WorldGrid.CellSize * 0.5f;
        float halfWidth = width * halfCellSize;
        float halfLength = length * halfCellSize;

        int socketIndex = 0;

        // North edge: left to right, NW → NE (outward = +Z)
        Vector2Int outwardNorth = new Vector2Int(0, 1);
        for (int i = 0; i < width; i++)
        {
            float localX = -halfWidth + halfCellSize + (i * WorldGrid.CellSize);
            Vector3 localPosition = new Vector3(localX, 0f, +halfLength);
            socketIndex++;
            _platformSockets.Add(socketIndex,new SocketData(socketIndex, localPosition, outwardNorth, SocketStatus.Linkable));
        }

        // East edge: top to bottom, NE → SE (outward = +X)
        Vector2Int outwardEast = new Vector2Int(1, 0);
        for (int i = 0; i < length; i++)
        {
            float localZ = +halfLength - halfCellSize - (i * WorldGrid.CellSize);
            Vector3 localPosition = new Vector3(+halfWidth, 0f, localZ);
            socketIndex++;
            _platformSockets.Add(socketIndex, new SocketData(socketIndex, localPosition, outwardEast, SocketStatus.Linkable));
        }

        // South edge: right to left, SE → SW (outward = -Z)
        Vector2Int outwardSouth = new Vector2Int(0, -1);
        for (int i = 0; i < width; i++)
        {
            float localX = +halfWidth - halfCellSize - (i * WorldGrid.CellSize);
            Vector3 localPosition = new Vector3(localX, 0f, -halfLength);
            socketIndex++;
            _platformSockets.Add(socketIndex, new SocketData(socketIndex++, localPosition, outwardSouth, SocketStatus.Linkable));
        }

        // West edge: bottom to top, SW → NW (outward = -X)
        Vector2Int outwardWest = new Vector2Int(-1, 0);
        for (int i = 0; i < length; i++)
        {
            float localZ = -halfLength + halfCellSize + (i * WorldGrid.CellSize);
            Vector3 localPosition = new Vector3(-halfWidth, 0f, localZ);
            socketIndex++;
            _platformSockets.Add(socketIndex, new SocketData(socketIndex++, localPosition, outwardWest, SocketStatus.Linkable));
        }
    }
    
    
    #endregion
    
    
    #region Socket Functions
    
    
    public SocketData GetSocket(int index)
    {
        if (index < 0 || index >= _platformSockets.Count)
        {
            Debug.LogWarning($"[PlatformSocketSystem] GetSocket: index {index} out of range (0..{_platformSockets.Count - 1}).", this);
            return null;
        }
        
        return _platformSockets[index];
    }


    
    public Vector3 GetSocketWorldPosition(int index)
    {
        if (index < 0 || index >= _platformSockets.Count)
            return transform.position;
        
        return _platformSockets[index].WorldPos;
    }


    
    public bool SetSocketStatus(int socketIndex, SocketStatus newStatus)
    {
        if (_platformSockets[socketIndex].Status == newStatus) // OldStatus == New Status
            return false;
        
        _platformSockets[socketIndex].SetStatus(newStatus);
        UpdateModuleVisibility(socketIndex);
        
        return true;
    }


    
    //Returns false if ONE of the Status could not be set
    private bool SetSocketStatus(Dictionary<int, SocketStatus> statuses)
    {
        return statuses.Keys.All(socket => SetSocketStatus(socket, statuses[socket]));
    }


    
    /// Updates world positions for all sockets based on current platform transform
    private void UpdateSocketPositions()
    {
        for (int i = 0; i < _platformSockets.Count; i++)
        {
            _platformSockets[i].SetWorldPosition(transform.TransformPoint(_platformSockets[i].LocalPos));
        }
    }
    
    
    
    /// True if the given socket index is currently connected to a neighbor
    public bool IsSocketConnected(int socketIndex)
    {
        if (socketIndex < 0 || socketIndex >= _platformSockets.Count)
            return false;

        return _platformSockets[socketIndex].IsConnected;
    }

    
    
    public bool AllSocketsConnected(int[] socketIndices)
    {
        if (socketIndices == null || socketIndices.Length == 0)
            return false;

        return socketIndices.All(IsSocketConnected);
    }
    
    
    
    public bool AnySocketsConnected(int[] socketIndices)
    {
        if (socketIndices == null || socketIndices.Length == 0)
            return false;

        return socketIndices.Any(IsSocketConnected);
    }



    private void UpdateModuleVisibility(int socketIndex)
    {
        // Update module visibility when connection state changes
        if (_platformSockets[socketIndex].AttachedModule is not null)
        {
            _platformSockets[socketIndex].AttachedModule.UpdateVisibility();
        }
    }
    
    
    #endregion
    
    
    #region Edge & Socket Helpers
    
    
    
    /// Finds the nearest socket to a world position
    /// Uses platform-local cell coordinates for O(1) lookup
    /// For corner cases, checks both adjacent edges (max 2 socket comparisons)
    ///
    public int FindNearestSocketIndex(Vector3 worldPos)
    {
        if (_platformSockets.Count == 0) return -1;
        
        var footprint = _platform.Footprint;
        int width = footprint.x;
        int length = footprint.y;
        float cellSize = WorldGrid.CellSize;
        
        // Transform to platform-local cell coordinates
        // Platform center is at local (0,0), convert to cell space where (0,0) is SW corner
        Vector3 localPos = transform.InverseTransformPoint(worldPos);
        float cellX = (localPos.x / cellSize) + (width * 0.5f);
        float cellZ = (localPos.z / cellSize) + (length * 0.5f);
        
        // Clamp to platform bounds
        cellX = Mathf.Clamp(cellX, 0f, width);
        cellZ = Mathf.Clamp(cellZ, 0f, length);
        
        // Distance to each edge (in cell units)
        float dNorth = length - cellZ;
        float dSouth = cellZ;
        float dEast = width - cellX;
        float dWest = cellX;
        
        // Find the two smallest distances (for corner handling)
        float minDist = Mathf.Min(dNorth, dSouth, dEast, dWest);
        const float cornerThreshold = 0.5f; // Within half a cell of corner
        
        // Get primary socket (on closest edge)
        int primarySocket = GetSocketOnEdge(Mathf.Approximately(minDist, dNorth) ? Edge.North :
                                            Mathf.Approximately(minDist, dEast) ? Edge.East :
                                            Mathf.Approximately(minDist, dSouth) ? Edge.South : Edge.West,
                                            cellX, cellZ, width, length);
        
        // Check if near a corner - if so, compare with secondary edge socket
        float secondMinDist = float.MaxValue;
        Edge secondEdge = Edge.North;
        
        if (dNorth > minDist && dNorth < secondMinDist) { secondMinDist = dNorth; secondEdge = Edge.North; }
        if (dEast > minDist && dEast < secondMinDist) { secondMinDist = dEast; secondEdge = Edge.East; }
        if (dSouth > minDist && dSouth < secondMinDist) { secondMinDist = dSouth; secondEdge = Edge.South; }
        if (dWest > minDist && dWest < secondMinDist) { secondMinDist = dWest; secondEdge = Edge.West; }
        
        // If near corner, compare primary vs secondary socket distance
        if (secondMinDist - minDist < cornerThreshold)
        {
            int secondarySocket = GetSocketOnEdge(secondEdge, cellX, cellZ, width, length);
            
            float primaryDistSqr = Vector3.SqrMagnitude(localPos - _platformSockets[primarySocket].LocalPos);
            float secondaryDistSqr = Vector3.SqrMagnitude(localPos - _platformSockets[secondarySocket].LocalPos);
            
            return secondaryDistSqr < primaryDistSqr ? secondarySocket : primarySocket;
        }
        
        return primarySocket;
    }
    
    
    
    /// Finds up to maxCount nearest socket indices within maxDistance
    /// Walks outward from nearest socket using perimeter order
    ///
    public List<int> FindNearestSocketIndices(Vector3 worldPos, int maxCount, float maxDistance)
    {
        List<int> result = new();
        
        if (maxCount <= 0 || _platformSockets.Count == 0) return result;
        
        int startIdx = FindNearestSocketIndex(worldPos);
        if (startIdx < 0) return result;
        
        float maxDistSqr = maxDistance * maxDistance;
        float startDistSqr = Vector3.SqrMagnitude(worldPos - _platformSockets[startIdx].WorldPos);
        
        if (startDistSqr > maxDistSqr) return result;
        result.Add(startIdx);
        
        // Walk outward in both directions using the clockwise ordering
        int prev = startIdx;
        int next = startIdx;
        
        while (result.Count < maxCount)
        {
            prev = GetPreviousSocketIndex(prev);
            next = GetNextSocketIndex(next);
            
            // Avoid infinite loop on small platforms
            if (prev == startIdx && next == startIdx) break;
            
            bool addedAny = false;
            
            // Check previous (counter-clockwise)
            if (prev != startIdx && !result.Contains(prev))
            {
                float distSqr = Vector3.SqrMagnitude(worldPos - _platformSockets[prev].WorldPos);
                if (distSqr <= maxDistSqr)
                {
                    result.Add(prev);
                    addedAny = true;
                    if (result.Count >= maxCount) break;
                }
            }
            
            // Check next (clockwise)
            if (next != startIdx && !result.Contains(next))
            {
                float distSqr = Vector3.SqrMagnitude(worldPos - _platformSockets[next].WorldPos);
                if (distSqr <= maxDistSqr)
                {
                    result.Add(next);
                    addedAny = true;
                }
            }
            
            // Stop if we've checked all sockets or hit distance limit in both directions
            if (!addedAny) break;
            if (prev == next) break; // Wrapped around
        }
        
        return result;
    }

    
    
    /// Gets the socket index on a specific edge based on cell coordinates
    /// Socket order: North (0 to W-1), East (W to W+L-1), South (W+L to 2W+L-1), West (2W+L to 2W+2L-1)
    private int GetSocketOnEdge(Edge edge, float cellX, float cellZ, int width, int length)
    {
        int idx;
        switch (edge)
        {
            case Edge.North:
                // North: left to right (increasing cellX)
                idx = Mathf.Clamp(Mathf.FloorToInt(cellX), 0, width - 1);
                return idx;
                
            case Edge.East:
                // East: top to bottom (decreasing cellZ)
                idx = Mathf.Clamp(Mathf.FloorToInt(length - cellZ), 0, length - 1);
                return width + idx;
                
            case Edge.South:
                // South: right to left (decreasing cellX)
                idx = Mathf.Clamp(Mathf.FloorToInt(width - cellX), 0, width - 1);
                return width + length + idx;
                
            case Edge.West:
            default:
                // West: bottom to top (increasing cellZ)
                idx = Mathf.Clamp(Mathf.FloorToInt(cellZ), 0, length - 1);
                return 2 * width + length + idx;
        }
    }
    
    
    
    /// Gets the next socket index in clockwise direction (with wrap-around)
    ///
    private int GetNextSocketIndex(int socketIndex)
    {
        if (_platformSockets.Count == 0) return -1;
        return (socketIndex + 1) % _platformSockets.Count;
    }
    
    
    /// Gets the previous socket index in counter-clockwise direction (with wrap-around)
    ///
    private int GetPreviousSocketIndex(int socketIndex)
    {
        if (_platformSockets.Count == 0) return -1;
        return (socketIndex - 1 + _platformSockets.Count) % _platformSockets.Count;
    }
    
    
    
    #endregion
    
    
    #region Grid Adjacency
    
    
    /// Gets the world-space outward direction for a socket (accounts for platform rotation)
    /// 
    public Vector3 GetSocketWorldOutwardDirection(int socketIndex)
    {
        if (socketIndex < 0 || socketIndex >= _platformSockets.Count)
            return Vector3.zero;

        var socket = _platformSockets[socketIndex];
        Vector3 localOutward = new Vector3(socket.OutwardOffset.x, 0f, socket.OutwardOffset.y);
        
        return transform.TransformDirection(localOutward).normalized;
    }


    /// Gets the grid cell adjacent to a socket (the cell the socket faces toward)
    /// 
    public Vector2Int GetSocketCellAdjacent(int socketIndex)
    {
        if (socketIndex < 0 || socketIndex >= _platformSockets.Count)
            return Vector2Int.zero;

        Vector3 socketWorldPos = GetSocketWorldPosition(socketIndex);
        Vector3 worldOutward = GetSocketWorldOutwardDirection(socketIndex).normalized;
        
        Vector3 adjacentWorldPos = socketWorldPos + worldOutward * (WorldGrid.CellSize * 0.5f);
        
        return _worldGrid.WorldToCell(adjacentWorldPos);
    }


    /// Gets the grid cell behind a socket 
    /// - the cell the socket is "attached" to
    /// 
    internal Vector2Int GetSocketCellBehind(int socketIndex)
    {
        if (socketIndex < 0 || socketIndex >= _platformSockets.Count)
            return Vector2Int.zero;

        Vector3 socketWorldPos = GetSocketWorldPosition(socketIndex);
        Vector3 worldOutward = GetSocketWorldOutwardDirection(socketIndex).normalized;
        
        Vector3 cellBehindPos = socketWorldPos - worldOutward * (WorldGrid.CellSize * 0.5f);
        
        return _worldGrid.WorldToCell(cellBehindPos);
    }
    
    
    #endregion
    
    
    #region Connection Management
    
    
    /// Refreshes all socket statuses by querying the grid.
    public void RefreshAllSocketStatuses()
    {
        bool anyChanged = false;
        
        for (int i = 0; i < _platformSockets.Count; i++)
        {
            if (_platformSockets[i].Status is SocketStatus.Locked or SocketStatus.Disabled) 
                continue;
            
            SocketStatus newStatus = DetermineSocketStatus(i);
            
            if (SetSocketStatus(i, newStatus))
                anyChanged = true;
        }
        
        if (anyChanged)
            SocketsChanged?.Invoke();
    }

    
    
    /// Determines a socket's status by querying the grid.
    /// Rules:
    /// - Blocked by own module → Occupied
    /// - No neighbor platform → Linkable
    /// - Neighbor has blocking module → Occupied  
    /// - Otherwise → Connected
    ///
    private SocketStatus DetermineSocketStatus(int socketIndex)
    {
        Vector2Int adjacentCell = GetSocketCellAdjacent(socketIndex);
        var cellData = _worldGrid.GetCell(adjacentCell);
        
        // Check if this socket has a blocking module
        if (IsSocketBlockedByModule(socketIndex))
            return SocketStatus.Occupied;

        if (cellData == null)
            return SocketStatus.Linkable;

        // Check flags in priority order using HasFlag() for proper [Flags] enum handling
        // Priority: Locked > ModuleBlocked > Occupied/Preview > default (Linkable)
        
        if (cellData.HasFlag(CellFlag.Locked))
            return SocketStatus.Locked;
        
        if (cellData.HasFlag(CellFlag.ModuleBlocked))
            return SocketStatus.Occupied;
        
        if (cellData.HasFlag(CellFlag.Occupied) || cellData.HasFlag(CellFlag.OccupyPreview))
            return SocketStatus.Connected;
        
        // Empty or Buildable only = Linkable
        return SocketStatus.Linkable;
    }


    
    /// Resets all connections to baseline (used when platform is unregistered)
    /// 
    public void ResetConnections()
    {
        bool anyChanged = false;
        
        // Reset all socket statuses to Linkable/Occupied (no neighbors)
        foreach (var socket in _platformSockets.Values)
        {
            if (socket.Status is SocketStatus.Locked or SocketStatus.Disabled)
                continue;
            
            socket.SetStatus(SocketStatus.Linkable);
            anyChanged = true;
        }
        
        if (anyChanged)
            SocketsChanged?.Invoke();
    }
    
    
    
    /// Checks if a socket is blocked by an active module that blocks linking
    private bool IsSocketBlockedByModule(int socketIndex)
    {
        return _platformSockets[socketIndex].Status is SocketStatus.Occupied;
    }
    
    
    #endregion
}
}

