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
    private PlatformManager _platformManager;
    private WorldGrid _worldGrid;
    
    
    private readonly Dictionary<PlatformModule, ModuleReg> _moduleRegs = new();
    private readonly Dictionary<int, PlatformModule> _socketToModules = new();
    
    
    [Header("Sockets")]
    [SerializeField] private List<SocketData> _platformSockets = new();

    private bool SocketsBuilt { get; set; }

    public IReadOnlyList<SocketData> PlatformSockets => _platformSockets;
    
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
    
    
    /// Module registration data
    [Serializable]
    public struct ModuleReg
    {
        public PlatformModule module;
        public int[] socketIndices;
        public bool blocksLink;
    }
    
    
    #endregion
    
    
    #region Initialization & Lifecycle

 
    public void Initialize(GamePlatform platform, PlatformManager platformManager, WorldGrid worldGrid)
    {
        _platform = platform;
        _platformManager = platformManager;
        _worldGrid = worldGrid;
        
        // Subscribe to platform movement
        // Remove first to avoid double subscription
        _platform.HasMoved -= OnPlatformMoved;
        _platform.HasMoved += OnPlatformMoved;
        
        ReBuildSockets();
        EnsureChildrenModulesRegistered();
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
        if(footprintSize == default) footprintSize = GetFootprint();
        
        // Preserve existing socket statuses when rebuilding
        var previousStatuses = new Dictionary<int, SocketStatus>();
        foreach (var s in _platformSockets)
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
        SocketsBuilt = false;

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
            _platformSockets.Add(new SocketData(socketIndex++, localPosition, outwardNorth, SocketStatus.Linkable));
        }

        // East edge: top to bottom, NE → SE (outward = +X)
        Vector2Int outwardEast = new Vector2Int(1, 0);
        for (int i = 0; i < length; i++)
        {
            float localZ = +halfLength - halfCellSize - (i * WorldGrid.CellSize);
            Vector3 localPosition = new Vector3(+halfWidth, 0f, localZ);
            _platformSockets.Add(new SocketData(socketIndex++, localPosition, outwardEast, SocketStatus.Linkable));
        }

        // South edge: right to left, SE → SW (outward = -Z)
        Vector2Int outwardSouth = new Vector2Int(0, -1);
        for (int i = 0; i < width; i++)
        {
            float localX = +halfWidth - halfCellSize - (i * WorldGrid.CellSize);
            Vector3 localPosition = new Vector3(localX, 0f, -halfLength);
            _platformSockets.Add(new SocketData(socketIndex++, localPosition, outwardSouth, SocketStatus.Linkable));
        }

        // West edge: bottom to top, SW → NW (outward = -X)
        Vector2Int outwardWest = new Vector2Int(-1, 0);
        for (int i = 0; i < length; i++)
        {
            float localZ = -halfLength + halfCellSize + (i * WorldGrid.CellSize);
            Vector3 localPosition = new Vector3(-halfWidth, 0f, localZ);
            _platformSockets.Add(new SocketData(socketIndex++, localPosition, outwardWest, SocketStatus.Linkable));
        }

        SocketsBuilt = true;
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
        
        int socketCount = _platformSockets.Count;
        
        foreach (int idx in socketIndices)
        {
            // Validate index is in bounds
            if (idx < 0 || idx >= socketCount)
                return false;
            
            // Check if this socket is connected
            if (_platformSockets[idx].Status != SocketStatus.Connected)
                return false;
        }
        
        return true;
    }
    
    
    
    public bool AnySocketsConnected(int[] socketIndices)
    {
        if (socketIndices == null || socketIndices.Length == 0)
            return false;

        int socketCount = _platformSockets.Count;
        
        foreach (int idx in socketIndices)
        {
            // Skip invalid indices
            if (idx < 0 || idx >= socketCount)
                continue;
            
            // Return true as soon as we find a connected socket
            if (_platformSockets[idx].Status == SocketStatus.Connected)
                return true;
        }
        
        return false;
    }



    private void UpdateModuleVisibility(int socketIndex)
    {
        // Update module visibility when connection state changes
        if (_socketToModules.TryGetValue(socketIndex, out var pm))
        {
            pm.UpdateVisibility();
        }
    }
    
    
    #endregion
    
    
    #region Edge & Socket Helpers
    


    /// Gets the socket index range (start, end inclusive) for a given edge
    /// Socket order: North → East → South → West (clockwise from NW corner)
    public void GetSocketIndexRangeForEdge(Edge edge, out int startIndex, out int endIndex)
    {
        var footprintSize = GetFootprint();
        int width = Mathf.Max(1, footprintSize.x);
        int length = Mathf.Max(1, footprintSize.y);

        switch (edge)
        {
            case Edge.North:
                startIndex = 0;
                endIndex = width - 1;
                break;
            
            case Edge.East:
                startIndex = width;
                endIndex = width + length - 1;
                break;
            
            case Edge.South:
                startIndex = width + length;
                endIndex = 2 * width + length - 1;
                break;
            
            case Edge.West:
            default:
                startIndex = 2 * width + length;
                endIndex = 2 * width + 2 * length - 1;
                break;
        }
    }
    
    
    
    /// Finds the nearest socket to a world position using edge-based lookup
    /// Only checks sockets on the closest edge(s) - O(1) for edge, O(k) for corner where k ≤ 6
    ///
    public int FindNearestSocketIndex(Vector3 worldPos)
    {
        if (_platformSockets.Count == 0) return -1;
        
        // Transform to local space - socket local positions are constant
        Vector3 localPos = transform.InverseTransformPoint(worldPos);
        var footprint = GetFootprint();
        
        float cellSize = WorldGrid.CellSize;
        float halfWidth = footprint.x * cellSize * 0.5f;
        float halfLength = footprint.y * cellSize * 0.5f;
        
        // Distance to each edge (negative = inside platform)
        float distToNorth = localPos.z - halfLength;
        float distToSouth = -halfLength - localPos.z;
        float distToEast = localPos.x - halfWidth;
        float distToWest = -halfWidth - localPos.x;
        
        // Find which edge(s) are closest
        float minDist = Mathf.Max(distToNorth, distToSouth, distToEast, distToWest);
        float threshold = cellSize * 0.5f; // Check adjacent edge if near corner
        
        int best = -1;
        float bestDistSqr = float.MaxValue;
        
        // Check each edge that's within threshold of closest
        if (distToNorth >= minDist - threshold)
            CheckEdgeForNearest(Edge.North, localPos, footprint, ref best, ref bestDistSqr);
        if (distToSouth >= minDist - threshold)
            CheckEdgeForNearest(Edge.South, localPos, footprint, ref best, ref bestDistSqr);
        if (distToEast >= minDist - threshold)
            CheckEdgeForNearest(Edge.East, localPos, footprint, ref best, ref bestDistSqr);
        if (distToWest >= minDist - threshold)
            CheckEdgeForNearest(Edge.West, localPos, footprint, ref best, ref bestDistSqr);
        
        return best;
    }
    
    
    /// Checks sockets on a specific edge, only checking the 1-3 nearest based on position
    private void CheckEdgeForNearest(Edge edge, Vector3 localPos, Vector2Int footprint, ref int best, ref float bestDistSqr)
    {
        GetSocketIndexRangeForEdge(edge, out int start, out int end);
        
        float cellSize = WorldGrid.CellSize;
        float halfWidth = footprint.x * cellSize * 0.5f;
        float halfLength = footprint.y * cellSize * 0.5f;
        
        // Calculate approximate socket index based on position along edge
        float t;
        switch (edge)
        {
            case Edge.North: // Left to right (increasing X)
                t = (localPos.x + halfWidth - cellSize * 0.5f) / cellSize;
                break;
            case Edge.East: // Top to bottom (decreasing Z)
                t = (halfLength - cellSize * 0.5f - localPos.z) / cellSize;
                break;
            case Edge.South: // Right to left (decreasing X)
                t = (halfWidth - cellSize * 0.5f - localPos.x) / cellSize;
                break;
            case Edge.West: // Bottom to top (increasing Z)
            default:
                t = (localPos.z + halfLength - cellSize * 0.5f) / cellSize;
                break;
        }
        
        int approxIdx = Mathf.RoundToInt(t);
        int edgeLength = end - start + 1;
        
        // Check socket at approx position and its neighbors (at most 3 sockets)
        for (int offset = -1; offset <= 1; offset++)
        {
            int idx = approxIdx + offset;
            if (idx < 0 || idx >= edgeLength) continue;
            
            int socketIdx = start + idx;
            float distSqr = Vector3.SqrMagnitude(localPos - _platformSockets[socketIdx].LocalPos);
            if (distSqr < bestDistSqr)
            {
                bestDistSqr = distSqr;
                best = socketIdx;
            }
        }
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
    public Vector2Int GetAdjacentCellForSocket(int socketIndex)
    {
        if (socketIndex < 0 || socketIndex >= _platformSockets.Count)
            return Vector2Int.zero;

        Vector3 socketWorldPos = GetSocketWorldPosition(socketIndex);
        Vector3 worldOutward = GetSocketWorldOutwardDirection(socketIndex);
        Vector3 adjacentWorldPos = socketWorldPos + worldOutward * 0.5f;
        
        return _worldGrid.WorldToCell(adjacentWorldPos);
    }


    /// Gets the grid cell behind a socket 
    /// - the cell the socket is "attached" to
    /// 
    private Vector2Int GetCellBehindSocket(int socketIndex)
    {
        if (socketIndex < 0 || socketIndex >= _platformSockets.Count)
            return Vector2Int.zero;

        Vector3 socketWorldPos = GetSocketWorldPosition(socketIndex);
        Vector3 worldOutward = GetSocketWorldOutwardDirection(socketIndex);
        // Move inward (opposite of outward) to get the cell behind the socket
        Vector3 cellBehindPos = socketWorldPos - worldOutward * 0.5f;
        
        return _worldGrid.WorldToCell(cellBehindPos);
    }
    
    
    #endregion
    
    
    #region Connection Management
    
    
    /// Refreshes all socket statuses by querying the grid.
    public void RefreshAllSocketStatuses()
    {
        bool anyChanged = false;
        int connectedCount = 0;
        
        for (int i = 0; i < _platformSockets.Count; i++)
        {
            if (_platformSockets[i].Status is SocketStatus.Locked or SocketStatus.Disabled)
                continue;
            
            SocketStatus newStatus = DetermineSocketStatus(i);
            if (newStatus == SocketStatus.Connected)
                connectedCount++;
            
            if (SetSocketStatus(i, newStatus))
                anyChanged = true;
        }
        
        if (anyChanged)
        {
            SocketsChanged?.Invoke();
        }
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
        Vector2Int adjacentCell = GetAdjacentCellForSocket(socketIndex);
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
    public void ResetConnections()
    {
        // Show all modules
        if (_platform)
        {
            foreach (var m in _platform.PlatformModules)
            {
                if (m) m.SetHidden(false);
            }
        }

        bool anyChanged = false;
        
        // Reset all socket statuses to Linkable/Occupied (no neighbors)
        for (int i = 0; i < _platformSockets.Count; i++)
        {
            var socket = _platformSockets[i];
            if (socket.Status == SocketStatus.Locked || socket.Status == SocketStatus.Disabled)
                continue;
            
            var newStatus = IsSocketBlockedByModule(i) ? SocketStatus.Occupied : SocketStatus.Linkable;
            if (socket.Status != newStatus)
            {
                socket.SetStatus(newStatus);
                anyChanged = true;
            }
        }
        
        if (anyChanged)
            SocketsChanged?.Invoke();
    }
    
    
    
    /// Checks if a socket is blocked by an active module that blocks linking
    private bool IsSocketBlockedByModule(int socketIndex)
    {
        if (!_socketToModules.TryGetValue(socketIndex, out var pm))
            return false;
        
        if (!pm.gameObject.activeInHierarchy)
            return false;
        
        return pm.blocksLink;
    }
    
    
    /// Gets the footprint from the GamePlatform component.
    /// 
    private Vector2Int GetFootprint()
    {
        return _platform.Footprint;
    }
    
    
    #endregion
    
    
    #region Module Registry
    
    
    public void RegisterModuleOnSockets(PlatformModule module, bool occupiesSockets, IEnumerable<int> socketIndices)
    {
        var list = new List<int>(socketIndices);
        bool blocks = module.blocksLink;

        var reg = new ModuleReg { module = module, socketIndices = list.ToArray(), blocksLink = blocks };
        _moduleRegs[module] = reg;

        // Map each socket to this module (1:1 relationship - one module max per socket)
        foreach (var sIdx in list)
        {
            _socketToModules[sIdx] = module;
        }
        
        // If module blocks linking, mark the cells behind each socket with ModuleBlocked flag
        // This allows neighboring platforms to know there's a blocking module facing them
        if (blocks)
        {
            foreach (var sIdx in list)
            {
                Vector2Int cellBehindSocket = GetCellBehindSocket(sIdx);
                _worldGrid.GetCell(cellBehindSocket)?.AddFlags(CellFlag.ModuleBlocked, enforcePriority: false);
            }
        }
    }


    public void UnregisterModule(PlatformModule module)
    {
        if (!module) return;
        if (!_moduleRegs.TryGetValue(module, out var reg)) return;

        // Clear ModuleBlocked flag from cells if this module was blocking
        if (reg.blocksLink && reg.socketIndices != null)
        {
            foreach (var sIdx in reg.socketIndices)
            {
                Vector2Int cellBehindSocket = GetCellBehindSocket(sIdx);
                _worldGrid.GetCell(cellBehindSocket)?.RemoveFlags(CellFlag.ModuleBlocked);
            }
        }

        if (reg.socketIndices != null)
        {
            foreach (var sIdx in reg.socketIndices)
            {
                _socketToModules.Remove(sIdx);
            }
        }
        _moduleRegs.Remove(module);
    }


    public void SetModuleHidden(PlatformModule module, bool hidden)
    {
        if (!module) return;
        
        module.SetHidden(hidden);
        RefreshAllSocketStatuses();
    }


    public void EnsureChildrenModulesRegistered()
    {
        if (!_platform) return;
        
        foreach (var m in _platform.PlatformModules)
        {
            if (m) m.EnsureRegistered();
        }
    }
    
    
    #endregion
}
}

