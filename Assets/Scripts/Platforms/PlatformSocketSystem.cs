using System;
using System.Collections.Generic;
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
    #region Events
    
    /// Fired when socket connection state changes (sockets connected/disconnected)
    public event Action SocketsChanged;
    
    /// Fired when a new neighbor platform is detected (for NavMesh link creation)
    public event Action<GamePlatform> NewNeighborDetected;
    
    #endregion
    
    #region Dependencies
    
    
    private GamePlatform _platform;
    private PlatformManager _platformManager;
    private WorldGrid _worldGrid;
    
    
    #endregion
    
    
    #region Enums & Data Structures
    
    
    // Edge enum (for compatibility with PlatformModule)
    public enum Edge { North, East, South, West }
    
    public enum SocketLocation { Edge, Corner }
    
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
    public struct SocketData
    {
        [SerializeField, HideInInspector] private int index;
        [SerializeField, HideInInspector] private Vector3 localPos;
        [SerializeField, HideInInspector] private Vector2Int outwardOffset;
        [SerializeField, HideInInspector] private SocketLocation location;
        [SerializeField] private SocketStatus status;
        
        // World position - updated when platform moves

        public int Index
        {
            get => index; 
            set => index = value;
        }

        public Vector3 LocalPos
        {
            get => localPos;
            set => localPos = value;
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

        public bool IsLinkable
        {
            get => status == SocketStatus.Linkable;
            set => throw new NotImplementedException();
        }

        public SocketLocation Location
        {
            get => location;
            set => location = value;
        }

        internal void Initialize(int idx, Vector3 lp, Vector2Int outward, SocketStatus defaultStatus)
        {
            index = idx;
            localPos = lp;
            outwardOffset = outward;
            location = SocketLocation.Edge;
            status = defaultStatus;
            WorldPos = Vector3.zero;
        }

        public void SetStatus(SocketStatus s) => status = s;
        
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
    
    
    
    
    #region Socket Data
    
    
    [FormerlySerializedAs("sockets")]
    [Header("Sockets")]
    [SerializeField] private List<SocketData> _platformSockets = new();

    /// Set of socket indices that are currently part of a connection
    private readonly HashSet<int> _connectedSockets = new();

    private bool SocketsBuilt { get; set; }
    
    /// Read-only access to connected sockets for external systems
    public IReadOnlyCollection<int> ConnectedSockets
    {
        get => _connectedSockets;
        set => throw new NotImplementedException();
    }

    public IReadOnlyList<SocketData> PlatformSockets
    {
        get 
        { 
            if (!SocketsBuilt) BuildSockets(); 
            return _platformSockets; 
        }
    }
    
    

    public int SocketCount
    {
        get 
        { 
            if (!SocketsBuilt) BuildSockets();
            return _platformSockets.Count;
        }
    }
    
    
    #endregion
    
    
    
    
    #region Module Registry
    
    
    private readonly Dictionary<PlatformModule, ModuleReg> _moduleRegs = new();
    private readonly Dictionary<int, PlatformModule> _socketToModules = new();
    
    
    #endregion
    
    
    
    
    #region Initialization
    
    
    /// Called by GamePlatform to inject dependencies
    public void SetDependencies(GamePlatform platform, PlatformManager platformManager, WorldGrid worldGrid)
    {
        _platform = platform;
        _platformManager = platformManager;
        _worldGrid = worldGrid;
        
        // Subscribe to platform movement
        // Remove first to avoid double subscription
        _platform.HasMoved -= OnPlatformMoved;
        _platform.HasMoved += OnPlatformMoved;
    }
    
    
    private void OnDestroy()
    {
        if (_platform)
        {
            _platform.HasMoved -= OnPlatformMoved;
        }
    }
    
    
    private void OnPlatformMoved(GamePlatform platform)
    {
        UpdateSocketWorldPositions();
    }
    
    
    #endregion
    
    
    
    
    #region Socket Building
    
    
    /// Build sockets along the perimeter of the footprint, in local space
    /// One socket per cell edge segment
    /// Order: +Z edge, -Z edge, +X edge, -X edge (for compat with Edge API)
    public void BuildSockets()
    {
        var footprintSize = _platform.Footprint;
        
        // Preserve existing socket statuses when rebuilding
        var previousStatuses = new Dictionary<Vector3, SocketStatus>();
        foreach (var s in _platformSockets)
            previousStatuses[s.LocalPos] = s.Status;

        _platformSockets.Clear();
        SocketsBuilt = false;

        int footprintWidth = Mathf.Max(1, footprintSize.x);
        int footprintLength = Mathf.Max(1, footprintSize.y);
        
        // Use WorldGrid.CellSize for consistency with grid system
        float cellSize = WorldGrid.CellSize;
        float halfCellSize = cellSize * 0.5f;
        float halfWidth = footprintWidth * halfCellSize;
        float halfLength = footprintLength * halfCellSize;

        int socketIndex = 0;

        // +Z edge (North - local z ≈ +halfLength), outward direction is (0, +1)
        Vector2Int outwardPlusZ = new Vector2Int(0, 1);
        for (int segmentIndex = 0; segmentIndex < footprintWidth; segmentIndex++)
        {
            float localX = -halfWidth + halfCellSize + (segmentIndex * cellSize);
            Vector3 localPosition = new Vector3(localX, 0f, +halfLength);
            var socketData = new SocketData();
            socketData.Initialize(socketIndex, localPosition, outwardPlusZ, 
                previousStatuses.TryGetValue(localPosition, out var oldStatus) ? oldStatus : SocketStatus.Linkable);
            _platformSockets.Add(socketData);
            socketIndex++;
        }

        // -Z edge (South - local z ≈ -halfLength), outward direction is (0, -1)
        Vector2Int outwardMinusZ = new Vector2Int(0, -1);
        for (int segmentIndex = 0; segmentIndex < footprintWidth; segmentIndex++)
        {
            float localX = -halfWidth + halfCellSize + (segmentIndex * cellSize);
            Vector3 localPosition = new Vector3(localX, 0f, -halfLength);
            var socketData = new SocketData();
            socketData.Initialize(socketIndex, localPosition, outwardMinusZ, 
                previousStatuses.TryGetValue(localPosition, out var oldStatus) ? oldStatus : SocketStatus.Linkable);
            _platformSockets.Add(socketData);
            socketIndex++;
        }

        // +X edge (East - local x ≈ +halfWidth), outward direction is (+1, 0)
        Vector2Int outwardPlusX = new Vector2Int(1, 0);
        for (int segmentIndex = 0; segmentIndex < footprintLength; segmentIndex++)
        {
            float localZ = +halfLength - halfCellSize - (segmentIndex * cellSize);
            Vector3 localPosition = new Vector3(+halfWidth, 0f, localZ);
            var socketData = new SocketData();
            socketData.Initialize(socketIndex, localPosition, outwardPlusX, 
                previousStatuses.TryGetValue(localPosition, out var oldStatus) ? oldStatus : SocketStatus.Linkable);
            _platformSockets.Add(socketData);
            socketIndex++;
        }

        // -X edge (West - local x ≈ -halfWidth), outward direction is (-1, 0)
        Vector2Int outwardMinusX = new Vector2Int(-1, 0);
        for (int segmentIndex = 0; segmentIndex < footprintLength; segmentIndex++)
        {
            float localZ = +halfLength - halfCellSize - (segmentIndex * cellSize);
            Vector3 localPosition = new Vector3(-halfWidth, 0f, localZ);
            var socketData = new SocketData();
            socketData.Initialize(socketIndex, localPosition, outwardMinusX, 
                previousStatuses.TryGetValue(localPosition, out var oldStatus) ? oldStatus : SocketStatus.Linkable);
            _platformSockets.Add(socketData);
            socketIndex++;
        }

        SocketsBuilt = true;
        
        UpdateSocketWorldPositions();
    }
    
    
    #endregion
    
    
    
    
    #region Socket Accessors
    
    
    public SocketData GetSocket(int index)
    {
        if (!SocketsBuilt) BuildSockets();
        if (index < 0 || index >= _platformSockets.Count)
        {
            Debug.LogWarning($"[PlatformSocketSystem] GetSocket: index {index} out of range (0..{_platformSockets.Count - 1}).", this);
            return default;
        }
        return _platformSockets[index];
    }


    public Vector3 GetSocketWorldPosition(int index)
    {
        if (!SocketsBuilt) BuildSockets();
        
        if (index < 0 || index >= _platformSockets.Count)
            return transform.position;
        
        return _platformSockets[index].WorldPos;
    }


    public void SetSocketStatus(int index, SocketStatus status)
    {
        if (!SocketsBuilt) BuildSockets();
        if (index < 0 || index >= _platformSockets.Count)
        {
            Debug.LogWarning($"[PlatformSocketSystem] SetSocketStatus: index {index} out of range (0..{_platformSockets.Count - 1}).", this);
            return;
        }
        var s = _platformSockets[index];
        s.SetStatus(status);
        _platformSockets[index] = s;
    }


    /// Updates world positions for all sockets based on current transform
    public void UpdateSocketWorldPositions()
    {
        if (!SocketsBuilt) return;
        
        for (int i = 0; i < _platformSockets.Count; i++)
        {
            var socket = _platformSockets[i];
            socket.SetWorldPosition(transform.TransformPoint(socket.LocalPos));
            _platformSockets[i] = socket;
        }
    }
    
    
    /// True if the given socket index is currently part of a connection
    public bool IsSocketConnected(int socketIndex) => _connectedSockets.Contains(socketIndex);
    
    
    #endregion
    
    
    
    
    #region Edge & Socket Index Helpers
    
    
    /// Length in whole meters along the given edge (number of segments)
    public int EdgeLengthMeters(Edge edge)
    {
        var footprintSize = _platform.Footprint;
        return (edge == Edge.North || edge == Edge.South) ? footprintSize.x : footprintSize.y;
    }


    /// Gets the socket index range (start, end inclusive) for a given edge
    public void GetSocketIndexRangeForEdge(Edge edge, out int startIndex, out int endIndex)
    {
        if (!SocketsBuilt) BuildSockets();

        var footprintSize = _platform.Footprint;
        int footprintWidth = Mathf.Max(1, footprintSize.x);
        int footprintLength = Mathf.Max(1, footprintSize.y);

        switch (edge)
        {
            case Edge.North:
                startIndex = 0;
                endIndex = footprintWidth - 1;
                break;
            case Edge.South:
                startIndex = footprintWidth;
                endIndex = 2 * footprintWidth - 1;
                break;
            case Edge.East:
                startIndex = 2 * footprintWidth;
                endIndex = 2 * footprintWidth + footprintLength - 1;
                break;
            case Edge.West:
            default:
                startIndex = 2 * footprintWidth + footprintLength;
                endIndex = 2 * footprintWidth + 2 * footprintLength - 1;
                break;
        }
    }


    /// Compatibility helper for code that thinks in Edge+mark (PlatformModule, old tools)
    public int GetSocketIndexByEdgeMark(Edge edge, int mark)
    {
        if (!SocketsBuilt) BuildSockets();

        var footprintSize = _platform.Footprint;
        int width = Mathf.Max(1, footprintSize.x);
        int length = Mathf.Max(1, footprintSize.y);

        switch (edge)
        {
            case Edge.North:
                mark = Mathf.Clamp(mark, 0, width - 1);
                return mark;
            case Edge.South:
                mark = Mathf.Clamp(mark, 0, width - 1);
                return width + mark;
            case Edge.East:
                mark = Mathf.Clamp(mark, 0, length - 1);
                return 2 * width + mark;
            case Edge.West:
            default:
                mark = Mathf.Clamp(mark, 0, length - 1);
                return 2 * width + length + mark;
        }
    }


    /// Return the single nearest socket index to a local position
    public int FindNearestSocketIndexLocal(Vector3 localPos)
    {
        if (!SocketsBuilt) BuildSockets();
        int best = -1;
        float bestD = float.MaxValue;

        for (int i = 0; i < _platformSockets.Count; i++)
        {
            float d = Vector3.SqrMagnitude(localPos - _platformSockets[i].LocalPos);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }


    /// Finds up to maxCount nearest socket indices to localPos within maxDistance
    public void FindNearestSocketIndicesLocal(Vector3 localPos, int maxCount, float maxDistance, List<int> result)
    {
        result.Clear();
        if (!SocketsBuilt) BuildSockets();
        if (maxCount <= 0 || _platformSockets.Count == 0) return;

        float maxSqr = maxDistance * maxDistance;

        List<(int idx, float d)> tmp = new List<(int, float)>(_platformSockets.Count);
        for (int i = 0; i < _platformSockets.Count; i++)
        {
            float d = Vector3.SqrMagnitude(localPos - _platformSockets[i].LocalPos);
            if (d <= maxSqr)
                tmp.Add((i, d));
        }

        tmp.Sort((a, b) => a.d.CompareTo(b.d));
        for (int i = 0; i < tmp.Count && i < maxCount; i++)
            result.Add(tmp[i].idx);
    }


    /// Convenience: find nearest socket to a WORLD position
    public int FindNearestSocketIndexWorld(Vector3 worldPos)
    {
        Vector3 local = transform.InverseTransformPoint(worldPos);
        return FindNearestSocketIndexLocal(local);
    }
    
    
    #endregion
    
    
    
    
    #region Grid Adjacency
    
    
    /// Gets the world-space outward direction for a socket (accounts for platform rotation)
    public Vector3 GetSocketWorldOutwardDirection(int socketIndex)
    {
        if (!SocketsBuilt) BuildSockets();
        if (socketIndex < 0 || socketIndex >= _platformSockets.Count)
            return Vector3.zero;

        var socket = _platformSockets[socketIndex];
        Vector3 localOutward = new Vector3(socket.OutwardOffset.x, 0f, socket.OutwardOffset.y);
        return transform.TransformDirection(localOutward).normalized;
    }


    /// Gets the grid cell adjacent to a socket (the cell the socket faces toward)
    public Vector2Int GetAdjacentCellForSocket(int socketIndex)
    {
        if (!SocketsBuilt) BuildSockets();
        if (socketIndex < 0 || socketIndex >= _platformSockets.Count)
            return Vector2Int.zero;

        Vector3 socketWorldPos = GetSocketWorldPosition(socketIndex);
        Vector3 worldOutward = GetSocketWorldOutwardDirection(socketIndex);
        Vector3 adjacentWorldPos = socketWorldPos + worldOutward * 0.5f;
        
        return _worldGrid.WorldToCell(adjacentWorldPos);
    }


    /// Gets the grid cell behind a socket (the cell this platform occupies at the socket)
    /// This is the cell on the platform's side of the socket edge
    private Vector2Int GetCellBehindSocket(int socketIndex)
    {
        if (!SocketsBuilt) BuildSockets();
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
    
    
    /// <summary>
    /// Single entry point for refreshing all socket statuses from grid state.
    /// Queries the WorldGrid directly to determine each socket's status.
    /// Updates the _connectedSockets cache, socket statuses, and module visibility.
    /// </summary>
    public void RefreshAllSocketStatuses()
    {
        if (!SocketsBuilt) BuildSockets();
        
        var previousNeighbors = GetCurrentNeighborPlatforms();
        var newNeighbors = new HashSet<GamePlatform>();
        
        bool anyStatusChanged = false;
        bool anyConnectionChanged = false;
        
        for (int i = 0; i < _platformSockets.Count; i++)
        {
            var socket = _platformSockets[i];
            
            // Skip permanently locked/disabled sockets
            if (socket.Status == SocketStatus.Locked || socket.Status == SocketStatus.Disabled)
                continue;
            
            // Determine new status by querying grid
            SocketStatus newStatus = UpdateSocketStatusFromGrid(i, out GamePlatform neighborPlatform);
            
            // Track neighbors for NavMesh link creation
            if (neighborPlatform != null)
                newNeighbors.Add(neighborPlatform);
            
            // Update socket status if changed
            if (socket.Status != newStatus)
            {
                socket.SetStatus(newStatus);
                _platformSockets[i] = socket;
                anyStatusChanged = true;
            }
            
            // Update _connectedSockets cache
            bool shouldBeConnected = (newStatus == SocketStatus.Connected);
            bool wasConnected = _connectedSockets.Contains(i);
            
            if (shouldBeConnected && !wasConnected)
            {
                _connectedSockets.Add(i);
                anyConnectionChanged = true;
                
                // Hide module on newly connected socket
                if (_socketToModules.TryGetValue(i, out var pm))
                    pm.SetHidden(true);
            }
            else if (!shouldBeConnected && wasConnected)
            {
                _connectedSockets.Remove(i);
                anyConnectionChanged = true;
                
                // Show module on newly disconnected socket
                if (_socketToModules.TryGetValue(i, out var pm))
                    pm.SetHidden(false);
            }
        }
        
        // Fire events if anything changed
        if (anyStatusChanged || anyConnectionChanged)
        {
            SocketsChanged?.Invoke();
        }
        
        // Notify about new neighbors (for NavMesh link creation)
        if (!_platform.IsPickedUp)
        {
            foreach (var neighbor in newNeighbors)
            {
                if (!previousNeighbors.Contains(neighbor))
                {
                    NewNeighborDetected?.Invoke(neighbor);
                }
            }
        }
    }
    
    
    /// <summary>
    /// Determines a socket's status by querying the grid directly.
    /// Rules:
    /// - No neighbor platform in adjacent cell → Linkable (or Occupied if blocked by own module)
    /// - Neighbor exists + this socket blocked by own module → Occupied
    /// - Neighbor exists + adjacent cell has ModuleBlocked flag → Occupied (neighbor has blocking module)
    /// - Neighbor exists + no blocking → Connected
    /// </summary>
    private SocketStatus UpdateSocketStatusFromGrid(int socketIndex, out GamePlatform neighborPlatform)
    {
        Vector2Int adjacentCell = GetAdjacentCellForSocket(socketIndex);
        
        // Check if this socket has a blocking module
        bool thisSocketBlocked = IsSocketBlockedByModule(socketIndex);

        if (thisSocketBlocked)
        {
            neighborPlatform = null;
            return SocketStatus.Occupied;
        }
        
        // Early exit: check WorldGrid Occupied flag first (fast array lookup)
        // If cell isn't occupied, no need to query PlatformManager
        if (!_worldGrid.CellHasAnyFlag(adjacentCell, WorldGrid.CellFlag.Occupied))
        {
            neighborPlatform = null;
            return SocketStatus.Linkable;
        }
        
        // Cell is occupied - get the actual platform to track neighbors
        if (!_platformManager.GetPlatformAtCell(adjacentCell, out neighborPlatform) 
            || !neighborPlatform 
            || neighborPlatform == _platform)
        {
            // Occupied but not by another platform (shouldn't happen, but handle gracefully)
            return SocketStatus.Locked;
        }
        
        // Adjacent cell has a neighbor platform
        
        // If adjacent cell has ModuleBlocked flag → Occupied (neighbor has blocking module facing us)
        if (_worldGrid.CellHasAnyFlag(adjacentCell, WorldGrid.CellFlag.ModuleBlocked))
            return SocketStatus.Occupied;
        
        // Neither side has a blocking module → Connected
        return SocketStatus.Connected;
    }
    
    
    
    /// Gets current neighbor platforms using WorldGrid's neighbor cell detection
    /// More efficient than looping through all sockets individually
    private HashSet<GamePlatform> GetCurrentNeighborPlatforms()
    {
        var neighbors = new HashSet<GamePlatform>();
        if (_platform.occupiedCells == null || _platform.occupiedCells.Count == 0) 
            return null;

        // Get all 4-directional neighbor cells at once (sockets only face cardinal directions)
        var neighborCells = _worldGrid.GetNeighborCells(_platform.occupiedCells, include8Directional: false);
        
        // Check which neighbor cells contain platforms
        foreach (var cell in neighborCells)
        {
            if (_platformManager.GetPlatformAtCell(cell, out GamePlatform neighbor)
                && neighbor && neighbor != _platform)
            {
                neighbors.Add(neighbor);
            }
        }
        
        return neighbors;
    }


    /// Resets all connections to baseline (used when platform is unregistered)
    public void ResetConnections()
    {
        // Show all modules
        foreach (var m in _platform.CachedModules)
        {
            if (m) m.SetHidden(false);
        }

        // Clear connection cache and reset all socket statuses to Linkable/Occupied
        _connectedSockets.Clear();
        
        for (int i = 0; i < _platformSockets.Count; i++)
        {
            var socket = _platformSockets[i];
            if (socket.Status == SocketStatus.Locked || socket.Status == SocketStatus.Disabled)
                continue;
            
            // Without neighbors, status is Linkable (or Occupied if blocked by own module)
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
    
    
    #endregion
    
    
    
    
    #region Module Registry
    
    
    public void RegisterModuleOnSockets(PlatformModule module, bool occupiesSockets, IEnumerable<int> socketIndices)
    {
        if (!module) return;
        if (!SocketsBuilt) BuildSockets();

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
                _worldGrid.TrySetCellFlag(cellBehindSocket, WorldGrid.CellFlag.ModuleBlocked, enforcePriority: false);
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
                _worldGrid.TryClearCellFlags(cellBehindSocket, WorldGrid.CellFlag.ModuleBlocked);
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
        foreach (var m in _platform.CachedModules)
        {
            if (m) m.EnsureRegistered();
        }
    }
    
    
    #endregion
}
}

