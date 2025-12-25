using System;
using System.Collections.Generic;
using System.Linq;
using Grid;
using UnityEngine;
using UnityEngine.Serialization;

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
    
    // Cell to socket indices lookup for O(1) access
    private readonly Dictionary<Vector2Int, List<int>> _cellToSockets = new();
    
    
    [Header("Sockets")]
    [SerializeField] private List<SocketData> _platformSockets = new();

    private bool SocketsBuilt { get; set; }

    public IReadOnlyList<SocketData> PlatformSockets => _platformSockets;
    
    public int SocketCount => _platformSockets.Count;
    
    
    #endregion
    
    
    #region Events
    /// Fired when socket connection state changes (sockets connected/disconnected)
    public event Action SocketsChanged;
    
    
    #endregion
    
    
    #region Socket Enums & Data Structures
    
    
    // Socket status enum
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
        [SerializeField, HideInInspector] private Vector2Int currentGridCell;
        [SerializeField, HideInInspector] private Vector3 localPos; //Only gets set on Initialization, then stays
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

        public Vector2Int CurrentGridCell
        {
            get => currentGridCell;
            private set => currentGridCell = value;
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
        
        internal void SetCurrentGridCell(Vector2Int cell) => CurrentGridCell = cell;
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
    /// Order: Clockwise starting from bottom-left (SW corner)
    /// South → East → North → West
    /// This ensures adjacent sockets are always index ±1 (with wrap-around)
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


    /// Builds sockets in clockwise order starting from bottom-left corner
    /// Layout for 4x4:
    ///            North (← going left)
    ///        ┌── 11  10   9   8 ──┐
    ///        │                    │
    /// West ↓ 12                   7 ↑ East
    ///        13                   6
    ///        14                   5
    ///        15                   4
    ///        │                    │
    ///        └── 0   1   2   3 ───┘
    ///            South (→ going right)
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

        // South edge: left to right (outward = -Z)
        Vector2Int outwardSouth = new Vector2Int(0, -1);
        for (int i = 0; i < width; i++)
        {
            float localX = -halfWidth + halfCellSize + (i * WorldGrid.CellSize);
            Vector3 localPosition = new Vector3(localX, 0f, -halfLength);
            _platformSockets.Add(new SocketData(socketIndex++, localPosition, outwardSouth, SocketStatus.Linkable));
        }

        // East edge: bottom to top (outward = +X)
        Vector2Int outwardEast = new Vector2Int(1, 0);
        for (int i = 0; i < length; i++)
        {
            float localZ = -halfLength + halfCellSize + (i * WorldGrid.CellSize);
            Vector3 localPosition = new Vector3(+halfWidth, 0f, localZ);
            _platformSockets.Add(new SocketData(socketIndex++, localPosition, outwardEast, SocketStatus.Linkable));
        }

        // North edge: right to left (outward = +Z)
        Vector2Int outwardNorth = new Vector2Int(0, 1);
        for (int i = 0; i < width; i++)
        {
            float localX = +halfWidth - halfCellSize - (i * WorldGrid.CellSize);
            Vector3 localPosition = new Vector3(localX, 0f, +halfLength);
            _platformSockets.Add(new SocketData(socketIndex++, localPosition, outwardNorth, SocketStatus.Linkable));
        }

        // West edge: top to bottom (outward = -X)
        Vector2Int outwardWest = new Vector2Int(-1, 0);
        for (int i = 0; i < length; i++)
        {
            float localZ = +halfLength - halfCellSize - (i * WorldGrid.CellSize);
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


    
    /// Updates world positions for all sockets and rebuilds cell-to-socket lookup
    ///
    private void UpdateSocketPositions()
    {
        _cellToSockets.Clear();
        
        for (int i = 0; i < _platformSockets.Count; i++)
        {
            var socket = _platformSockets[i];
            socket.SetWorldPosition(transform.TransformPoint(socket.LocalPos));
            
            Vector2Int cell = _worldGrid.WorldToCell(socket.WorldPos);
            socket.SetCurrentGridCell(cell);
            
            // Build cell lookup (perimeter cells have 1-2 sockets each)
            if (!_cellToSockets.TryGetValue(cell, out var list))
            {
                list = new List<int>(2);
                _cellToSockets[cell] = list;
            }
            list.Add(i);
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
        if (socketIndices.Length == 0 || socketIndices.Length >= _platformSockets.Count)
            return false;
        
        bool isVisible = socketIndices
            .All(socketIndex => _platformSockets[socketIndex].Status == SocketStatus.Connected);
        
        return isVisible;
    }
    
    
    
    public bool AnySocketsConnected(int[] socketIndices)
    {
        if (socketIndices.Length == 0 || socketIndices.Length >= _platformSockets.Count)
            return false;

        bool anyConnected = socketIndices
            .Any(socketIndex => _platformSockets[socketIndex].Status == SocketStatus.Connected);
        
        return anyConnected;
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
    
    
    #region Socket Lookup
    
    
    /// Gets the nearest socket index to a world position
    /// Uses cell-to-socket lookup for O(1) access
    ///
    public int GetNearestSocketIndex(Vector3 worldPos)
    {
        if (_platformSockets.Count == 0) return -1;
        
        Vector2Int cell = _worldGrid.WorldToCell(worldPos);
        
        int best = -1;
        float bestDistSqr = float.MaxValue;
        
        // Check sockets in this cell
        if (_cellToSockets.TryGetValue(cell, out var socketsInCell))
        {
            foreach (int idx in socketsInCell)
            {
                float distSqr = Vector3.SqrMagnitude(worldPos - _platformSockets[idx].WorldPos);
                if (distSqr < bestDistSqr)
                {
                    bestDistSqr = distSqr;
                    best = idx;
                }
            }
        }
        
        // For positions on cell boundaries (posts), check adjacent cells
        if (best < 0 || IsNearCellBoundary(worldPos, cell))
        {
            foreach (var neighborCell in _worldGrid.GetNeighborCells(cell))
            {
                if (!_cellToSockets.TryGetValue(neighborCell, out var neighborSockets)) continue;
                
                foreach (int idx in neighborSockets)
                {
                    float distSqr = Vector3.SqrMagnitude(worldPos - _platformSockets[idx].WorldPos);
                    if (distSqr < bestDistSqr)
                    {
                        bestDistSqr = distSqr;
                        best = idx;
                    }
                }
            }
        }
        
        return best;
    }
    
    
    /// Gets up to maxCount nearest socket indices within maxDistance
    /// Walks outward from nearest socket using continuous perimeter ordering
    ///
    public List<int> GetNearestSocketIndices(Vector3 worldPos, int maxCount, float maxDistance)
    {
        var result = new List<int>();
        if (maxCount <= 0 || _platformSockets.Count == 0) return result;
        
        int startIndex = GetNearestSocketIndex(worldPos);
        if (startIndex < 0) return result;
        
        float maxDistSqr = maxDistance * maxDistance;
        
        // Check if starting socket is within range
        if (Vector3.SqrMagnitude(worldPos - _platformSockets[startIndex].WorldPos) > maxDistSqr)
            return result;
        
        result.Add(startIndex);
        if (maxCount == 1) return result;
        
        // Walk outward in both directions using continuous perimeter ordering
        int totalSockets = _platformSockets.Count;
        int offset = 1;
        
        while (result.Count < maxCount && offset <= totalSockets / 2)
        {
            // Previous neighbor (counter-clockwise)
            int prevIndex = (startIndex - offset + totalSockets) % totalSockets;
            if (!result.Contains(prevIndex))
            {
                float distSqr = Vector3.SqrMagnitude(worldPos - _platformSockets[prevIndex].WorldPos);
                if (distSqr <= maxDistSqr)
                    result.Add(prevIndex);
            }
            
            if (result.Count >= maxCount) break;
            
            // Next neighbor (clockwise)
            int nextIndex = (startIndex + offset) % totalSockets;
            if (!result.Contains(nextIndex))
            {
                float distSqr = Vector3.SqrMagnitude(worldPos - _platformSockets[nextIndex].WorldPos);
                if (distSqr <= maxDistSqr)
                    result.Add(nextIndex);
            }
            
            offset++;
        }
        
        return result;
    }
    
    
    /// Checks if position is near a cell boundary (for posts between cells)
    ///
    private bool IsNearCellBoundary(Vector3 worldPos, Vector2Int cell)
    {
        Vector3 cellCenter = _worldGrid.GetCellCenter(cell);
        const float boundaryThreshold = 0.1f;
        float halfCell = WorldGrid.CellSize * 0.5f;
        
        float offsetX = Mathf.Abs(worldPos.x - cellCenter.x);
        float offsetZ = Mathf.Abs(worldPos.z - cellCenter.z);
        
        return offsetX > halfCell - boundaryThreshold || offsetZ > halfCell - boundaryThreshold;
    }
    
    #endregion
    
    
    #region Grid Adjacency
    
    
    /// Gets the world-space outward direction for a socket (accounts for platform rotation)
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


    /// Gets all sockets that are connected to a specific neighbor platform
    /// Uses neighbor's occupiedCells for efficient O(1) lookup per socket
    public List<int> GetSocketsConnectedToNeighbor(GamePlatform neighbor)
    {
        var result = new List<int>();
        if (!neighbor || neighbor.occupiedCells == null || neighbor.occupiedCells.Count == 0) 
            return result;

        // Create HashSet from neighbor's cells for O(1) lookup
        var neighborCells = new HashSet<Vector2Int>(neighbor.occupiedCells);

        for (int i = 0; i < _platformSockets.Count; i++)
        {
            var socket = _platformSockets[i];
            if (socket.Status != SocketStatus.Connected) continue;

            // Check if this socket's adjacent cell is one of the neighbor's cells
            Vector2Int adjacentCell = GetAdjacentCellForSocket(i);
            if (neighborCells.Contains(adjacentCell))
            {
                result.Add(i);
            }
        }

        return result;
    }
    
    
    #endregion
    
    
    #region Connection Management
    
    
    // ReSharper disable Unity.PerformanceAnalysis
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


    

    /// <summary>
    /// Determines a socket's status by querying the grid.
    /// Rules:
    /// - Blocked by own module → Occupied
    /// - No neighbor platform → Linkable
    /// - Neighbor has blocking module → Occupied  
    /// - Otherwise → Connected
    /// </summary>
    private SocketStatus DetermineSocketStatus(int socketIndex)
    {
        // Check if this socket has a blocking module
        if (IsSocketBlockedByModule(socketIndex))
            return SocketStatus.Occupied;
        
        Vector2Int adjacentCell = GetAdjacentCellForSocket(socketIndex);
        var cellData = _worldGrid.GetCell(adjacentCell);
        
        // Check if adjacent cell is occupied by a platform (including preview/moving platforms)
        if (cellData == null || !cellData.HasFlag(CellFlag.Occupied | CellFlag.OccupyPreview))
            return SocketStatus.Linkable;
        
        // Verify it's actually a neighbor platform (not self or non-platform)
        if (!_platformManager.GetPlatformAtCell(adjacentCell, out var neighbor) 
            || !neighbor 
            || neighbor == _platform)
            return SocketStatus.Linkable;
        
        // Check if neighbor has a blocking module facing us
        if (cellData.HasFlag(CellFlag.ModuleBlocked))
            return SocketStatus.Occupied;
        
        return SocketStatus.Connected;
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

        // Reset all socket statuses to Linkable/Occupied (no neighbors)
        for (int i = 0; i < _platformSockets.Count; i++)
        {
            var socket = _platformSockets[i];
            if (socket.Status == SocketStatus.Locked || socket.Status == SocketStatus.Disabled)
                continue;
            
            socket.SetStatus(IsSocketBlockedByModule(i) ? SocketStatus.Occupied : SocketStatus.Linkable);
            _platformSockets[i] = socket;
        }
        
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

