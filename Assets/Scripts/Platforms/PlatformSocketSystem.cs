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

    private bool SocketsBuilt { get; set; }

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
    
    
    /// True if the given socket index is currently connected to a neighbor
    public bool IsSocketConnected(int socketIndex)
    {
        if (socketIndex < 0 || socketIndex >= _platformSockets.Count)
            return false;
        return _platformSockets[socketIndex].Status == SocketStatus.Connected;
    }
    
    
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
    /// Refreshes all socket statuses by querying the grid.
    /// Simple flow: determine status → update → fire events.
    /// </summary>
    public void RefreshAllSocketStatuses()
    {
        if (!SocketsBuilt) BuildSockets();
        
        var previousNeighbors = GetCurrentNeighborPlatforms();
        var currentNeighbors = new HashSet<GamePlatform>();
        bool anyChanged = false;
        
        // Update each socket's status from grid state
        for (int i = 0; i < _platformSockets.Count; i++)
        {
            var socket = _platformSockets[i];
            
            // Skip permanently locked/disabled sockets
            if (socket.Status == SocketStatus.Locked || socket.Status == SocketStatus.Disabled)
                continue;
            
            // Query grid for new status
            SocketStatus oldStatus = socket.Status;
            SocketStatus newStatus = DetermineSocketStatus(i, out GamePlatform neighbor);
            
            // Track neighbor platforms
            if (neighbor != null)
                currentNeighbors.Add(neighbor);
            
            // Apply status change
            if (oldStatus != newStatus)
            {
                socket.SetStatus(newStatus);
                _platformSockets[i] = socket;
                anyChanged = true;
                
                // Update module visibility on connection change
                if (_socketToModules.TryGetValue(i, out var pm))
                {
                    bool nowConnected = (newStatus == SocketStatus.Connected);
                    bool wasConnected = (oldStatus == SocketStatus.Connected);
                    if (nowConnected && !wasConnected) pm.SetHidden(true);
                    else if (!nowConnected && wasConnected) pm.SetHidden(false);
                }
            }
        }
        
        if (anyChanged)
            SocketsChanged?.Invoke();
        
        // Notify about newly discovered neighbors (for NavMesh links)
        if (!_platform.IsPickedUp)
        {
            foreach (var neighbor in currentNeighbors)
            {
                if (!previousNeighbors.Contains(neighbor))
                    NewNeighborDetected?.Invoke(neighbor);
            }
        }
    }
    
    
    /// <summary>
    /// Determines a socket's status by querying the grid directly.
    /// Always attempts to find the neighbor platform first (for NavMesh link tracking),
    /// then determines status based on blocking conditions.
    /// </summary>
    private SocketStatus DetermineSocketStatus(int socketIndex, out GamePlatform neighborPlatform)
    {
        neighborPlatform = null;
        Vector2Int adjacentCell = GetAdjacentCellForSocket(socketIndex);
        
        // Check if this socket has a blocking module
        bool thisSocketBlocked = IsSocketBlockedByModule(socketIndex);
        
        // Check if adjacent cell is occupied (fast WorldGrid lookup)
        if (!_worldGrid.CellHasAnyFlag(adjacentCell, WorldGrid.CellFlag.Occupied))
        {
            // No neighbor - status depends only on own blocking module
            return thisSocketBlocked ? SocketStatus.Occupied : SocketStatus.Linkable;
        }
        
        // Cell is occupied - always try to get the platform for neighbor tracking
        if (!_platformManager.GetPlatformAtCell(adjacentCell, out neighborPlatform) 
            || !neighborPlatform 
            || neighborPlatform == _platform)
        {
            // Occupied but not by a valid neighbor platform
            neighborPlatform = null;
            return thisSocketBlocked ? SocketStatus.Occupied : SocketStatus.Linkable;
        }
        
        // We have a valid neighbor platform - now determine connection status
        
        // If this socket has a blocking module → Occupied (module stays visible, railings show)
        if (thisSocketBlocked)
            return SocketStatus.Occupied;
        
        // If neighbor has a blocking module facing us → Occupied
        if (_worldGrid.CellHasAnyFlag(adjacentCell, WorldGrid.CellFlag.ModuleBlocked))
            return SocketStatus.Occupied;
        
        // Neither side blocked → Connected
        return SocketStatus.Connected;
    }
    
    
    /// Gets current neighbor platforms using WorldGrid's neighbor cell detection
    /// More efficient than looping through all sockets individually
    private HashSet<GamePlatform> GetCurrentNeighborPlatforms()
    {
        var neighbors = new HashSet<GamePlatform>();
        if (_platform.occupiedCells == null || _platform.occupiedCells.Count == 0) 
            return neighbors;

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

