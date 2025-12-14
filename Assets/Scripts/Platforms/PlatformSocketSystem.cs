using System;
using System.Collections.Generic;
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
        public GameObject go;
        public int[] socketIndices;
        public bool blocksLink;
    }
    
    
    #endregion
    
    
    
    
    #region Socket Data
    
    
    [Header("Sockets")]
    [SerializeField] private List<SocketData> sockets = new();

    /// Set of socket indices that are currently part of a connection
    private readonly HashSet<int> _connectedSockets = new();

    private bool SocketsBuilt { get; set; }
    
    /// Read-only access to connected sockets for external systems
    public IReadOnlyCollection<int> ConnectedSockets
    {
        get => _connectedSockets;
        set => throw new NotImplementedException();
    }

    public IReadOnlyList<SocketData> Sockets
    {
        get 
        { 
            if (!SocketsBuilt) BuildSockets(); 
            return sockets; 
        }
    }
    
    

    public int SocketCount
    {
        get 
        { 
            if (!SocketsBuilt) BuildSockets();
            return sockets.Count;
        }
    }
    
    
    #endregion
    
    
    
    
    #region Module Registry
    
    
    private readonly Dictionary<GameObject, ModuleReg> _moduleRegs = new();
    private readonly Dictionary<int, GameObject> _socketToModules = new();
    
    
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
    /// One socket per 1m edge segment
    /// Order: +Z edge, -Z edge, +X edge, -X edge (for compat with Edge API)
    public void BuildSockets()
    {
        if (!_platform) return;
        
        var footprintSize = _platform.Footprint;
        
        var prev = new Dictionary<Vector3, SocketStatus>();
        foreach (var s in sockets)
            prev[s.LocalPos] = s.Status;

        sockets.Clear();
        SocketsBuilt = false;

        int footprintWidth = Mathf.Max(1, footprintSize.x);
        int footprintLength = Mathf.Max(1, footprintSize.y);
        float halfWidth = footprintWidth * 0.5f;
        float halfLength = footprintLength * 0.5f;

        int socketIndex = 0;

        // +Z edge (local z ≈ +halfLength), outward direction is (0, +1)
        Vector2Int outwardPlusZ = new Vector2Int(0, 1);
        for (int segmentIndex = 0; segmentIndex < footprintWidth; segmentIndex++)
        {
            float localX = -halfWidth + 0.5f + segmentIndex;
            Vector3 localPosition = new Vector3(localX, 0f, +halfLength);
            var socketData = new SocketData();
            socketData.Initialize(socketIndex, localPosition, outwardPlusZ, prev.TryGetValue(localPosition, out var oldStatus) ? oldStatus : SocketStatus.Linkable);
            sockets.Add(socketData);
            socketIndex++;
        }

        // -Z edge (local z ≈ -halfLength), outward direction is (0, -1)
        Vector2Int outwardMinusZ = new Vector2Int(0, -1);
        for (int segmentIndex = 0; segmentIndex < footprintWidth; segmentIndex++)
        {
            float localX = -halfWidth + 0.5f + segmentIndex;
            Vector3 localPosition = new Vector3(localX, 0f, -halfLength);
            var socketData = new SocketData();
            socketData.Initialize(socketIndex, localPosition, outwardMinusZ, prev.TryGetValue(localPosition, out var oldStatus) ? oldStatus : SocketStatus.Linkable);
            sockets.Add(socketData);
            socketIndex++;
        }

        // +X edge (local x ≈ +halfWidth), outward direction is (+1, 0)
        Vector2Int outwardPlusX = new Vector2Int(1, 0);
        for (int segmentIndex = 0; segmentIndex < footprintLength; segmentIndex++)
        {
            float localZ = +halfLength - 0.5f - segmentIndex;
            Vector3 localPosition = new Vector3(+halfWidth, 0f, localZ);
            var socketData = new SocketData();
            socketData.Initialize(socketIndex, localPosition, outwardPlusX, prev.TryGetValue(localPosition, out var oldStatus) ? oldStatus : SocketStatus.Linkable);
            sockets.Add(socketData);
            socketIndex++;
        }

        // -X edge (local x ≈ -halfWidth), outward direction is (-1, 0)
        Vector2Int outwardMinusX = new Vector2Int(-1, 0);
        for (int segmentIndex = 0; segmentIndex < footprintLength; segmentIndex++)
        {
            float localZ = +halfLength - 0.5f - segmentIndex;
            Vector3 localPosition = new Vector3(-halfWidth, 0f, localZ);
            var socketData = new SocketData();
            socketData.Initialize(socketIndex, localPosition, outwardMinusX, prev.TryGetValue(localPosition, out var oldStatus) ? oldStatus : SocketStatus.Linkable);
            sockets.Add(socketData);
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
        if (index < 0 || index >= sockets.Count)
        {
            Debug.LogWarning($"[PlatformSocketSystem] GetSocket: index {index} out of range (0..{sockets.Count - 1}).", this);
            return default;
        }
        return sockets[index];
    }


    public Vector3 GetSocketWorldPosition(int index)
    {
        if (!SocketsBuilt) BuildSockets();
        
        if (index < 0 || index >= sockets.Count)
            return transform.position;
        
        return sockets[index].WorldPos;
    }


    public void SetSocketStatus(int index, SocketStatus status)
    {
        if (!SocketsBuilt) BuildSockets();
        if (index < 0 || index >= sockets.Count)
        {
            Debug.LogWarning($"[PlatformSocketSystem] SetSocketStatus: index {index} out of range (0..{sockets.Count - 1}).", this);
            return;
        }
        var s = sockets[index];
        s.SetStatus(status);
        sockets[index] = s;
    }


    /// Updates world positions for all sockets based on current transform
    public void UpdateSocketWorldPositions()
    {
        if (!SocketsBuilt) return;
        
        for (int i = 0; i < sockets.Count; i++)
        {
            var socket = sockets[i];
            socket.SetWorldPosition(transform.TransformPoint(socket.LocalPos));
            sockets[i] = socket;
        }
    }
    
    
    /// True if the given socket index is currently part of a connection
    public bool IsSocketConnected(int socketIndex) => _connectedSockets.Contains(socketIndex);
    
    
    #endregion
    
    
    
    
    #region Edge & Socket Index Helpers
    
    
    /// Length in whole meters along the given edge (number of segments)
    public int EdgeLengthMeters(Edge edge)
    {
        if (!_platform) return 0;
        var footprintSize = _platform.Footprint;
        return (edge == Edge.North || edge == Edge.South) ? footprintSize.x : footprintSize.y;
    }


    /// Gets the socket index range (start, end inclusive) for a given edge
    public void GetSocketIndexRangeForEdge(Edge edge, out int startIndex, out int endIndex)
    {
        if (!SocketsBuilt) BuildSockets();
        if (!_platform) { startIndex = 0; endIndex = 0; return; }

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
        if (!_platform) return 0;

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

        for (int i = 0; i < sockets.Count; i++)
        {
            float d = Vector3.SqrMagnitude(localPos - sockets[i].LocalPos);
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
        if (maxCount <= 0 || sockets.Count == 0) return;

        float maxSqr = maxDistance * maxDistance;

        List<(int idx, float d)> tmp = new List<(int, float)>(sockets.Count);
        for (int i = 0; i < sockets.Count; i++)
        {
            float d = Vector3.SqrMagnitude(localPos - sockets[i].LocalPos);
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
        if (socketIndex < 0 || socketIndex >= sockets.Count)
            return Vector3.zero;

        var socket = sockets[socketIndex];
        Vector3 localOutward = new Vector3(socket.OutwardOffset.x, 0f, socket.OutwardOffset.y);
        return transform.TransformDirection(localOutward).normalized;
    }


    /// Gets the grid cell adjacent to a socket (the cell the socket faces toward)
    public Vector2Int GetAdjacentCellForSocket(int socketIndex)
    {
        if (!SocketsBuilt) BuildSockets();
        if (socketIndex < 0 || socketIndex >= sockets.Count || !_worldGrid)
            return Vector2Int.zero;

        Vector3 socketWorldPos = GetSocketWorldPosition(socketIndex);
        Vector3 worldOutward = GetSocketWorldOutwardDirection(socketIndex);
        Vector3 adjacentWorldPos = socketWorldPos + worldOutward * 0.5f;
        
        return _worldGrid.WorldToCell(adjacentWorldPos);
    }


    /// Updates socket connection statuses based on adjacent grid cell occupancy
    public void UpdateSocketStatusesFromGrid()
    {
        var newConnectedSockets = new HashSet<int>();
        var newNeighbors = new HashSet<GamePlatform>();
        var previousNeighbors = GetCurrentNeighbors();

        for (int i = 0; i < sockets.Count; i++)
        {
            var socket = sockets[i];
            
            if (socket.Status == SocketStatus.Locked || socket.Status == SocketStatus.Disabled)
                continue;

            Vector2Int neighborCell = GetAdjacentCellForSocket(i);
            
            if (_platformManager.GetPlatformAtCell(neighborCell, out var neighborPlatform))
            {
                if (neighborPlatform && neighborPlatform != _platform)
                {
                    newConnectedSockets.Add(i);
                    newNeighbors.Add(neighborPlatform);
                }
            }
        }

        bool hadChanges = !newConnectedSockets.SetEquals(_connectedSockets);
        if (hadChanges)
        {
            UpdateConnections(newConnectedSockets);
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


    /// Gets current neighbor platforms based on connected sockets
    private HashSet<GamePlatform> GetCurrentNeighbors()
    {
        var neighbors = new HashSet<GamePlatform>();
        if (!_platformManager) return neighbors;

        foreach (int socketIndex in _connectedSockets)
        {
            Vector2Int adjacentCell = GetAdjacentCellForSocket(socketIndex);
            if (_platformManager.GetPlatformAtCell(adjacentCell, out GamePlatform neighbor)
                && neighbor && neighbor != _platform)
            {
                neighbors.Add(neighbor);
            }
        }
        return neighbors;
    }


    /// Gets all sockets that are currently connected to a specific neighbor platform
    public List<int> GetSocketsConnectedToNeighbor(GamePlatform neighbor)
    {
        var result = new List<int>();

        for (int i = 0; i < sockets.Count; i++)
        {
            if (!_connectedSockets.Contains(i)) continue;

            Vector2Int adjacentCell = GetAdjacentCellForSocket(i);
            if (_platformManager.GetPlatformAtCell(adjacentCell, out var occupant) && occupant == neighbor)
            {
                result.Add(i);
            }
        }

        return result;
    }
    
    
    #endregion
    
    
    
    
    #region Connection Management
    
    
    /// Incrementally updates connections - only modifies sockets that changed
    public void UpdateConnections(HashSet<int> newConnectedSockets)
    {
        if (newConnectedSockets == null) newConnectedSockets = new HashSet<int>();
        
        var socketsToDisconnect = new List<int>();
        foreach (int socketIndex in _connectedSockets)
        {
            if (!newConnectedSockets.Contains(socketIndex))
                socketsToDisconnect.Add(socketIndex);
        }
        
        var socketsToConnect = new List<int>();
        foreach (int socketIndex in newConnectedSockets)
        {
            if (!_connectedSockets.Contains(socketIndex))
                socketsToConnect.Add(socketIndex);
        }
        
        if (socketsToDisconnect.Count == 0 && socketsToConnect.Count == 0)
            return;
        
        // Update connection state
        foreach (int socketIndex in socketsToDisconnect)
            _connectedSockets.Remove(socketIndex);
        foreach (int socketIndex in socketsToConnect)
            _connectedSockets.Add(socketIndex);
        
        // Update module visibility
        foreach (int socketIndex in socketsToDisconnect)
        {
            if (_socketToModules.TryGetValue(socketIndex, out var module))
            {
                    SetModuleVisibility(module, visible: true);
            }
        }
        
        foreach (int socketIndex in socketsToConnect)
        {
            if (_socketToModules.TryGetValue(socketIndex, out var module))
            {
                    SetModuleVisibility(module, visible: false);
            }
        }
        
        RefreshSocketStatuses();
        SocketsChanged?.Invoke();
    }


    /// Resets all connections to baseline
    public void ResetConnections()
    {
        // Show all modules
        if (_platform)
        {
            foreach (var m in _platform.CachedModules)
            {
                if (m) m.SetHidden(false);
            }
        }

        _connectedSockets.Clear();
        RefreshSocketStatuses();
        SocketsChanged?.Invoke();
    }


    /// Recompute every socket's status from current modules + connection state
    public void RefreshSocketStatuses()
    {
        if (!SocketsBuilt) BuildSockets();

        for (int i = 0; i < sockets.Count; i++)
        {
            var socket = sockets[i];

            // Skip permanently locked/disabled sockets - they don't change
            if (socket.Status == SocketStatus.Locked || socket.Status == SocketStatus.Disabled)
                continue;

            // Determine new status based on connection and module state
            SocketStatus newStatus;
            
            if (_connectedSockets.Contains(i))
            {
                newStatus = SocketStatus.Connected;
            }
            else if (IsSocketBlockedByModule(i))
            {
                newStatus = SocketStatus.Occupied;
            }
            else
            {
                newStatus = SocketStatus.Linkable;
            }

            socket.SetStatus(newStatus);
            sockets[i] = socket;
            }
    }
    
    
    /// Checks if a socket is blocked by an active module that blocks linking
    private bool IsSocketBlockedByModule(int socketIndex)
    {
        if (!_socketToModules.TryGetValue(socketIndex, out var module))
            return false;
        
        if (!module.activeInHierarchy)
            return false;
        
        if (!_moduleRegs.TryGetValue(module, out var reg))
            return false;
        
        return reg.blocksLink;
    }
    
    
    #endregion
    
    
    
    
    #region Module Registry
    
    
    public void RegisterModuleOnSockets(GameObject moduleGo, bool occupiesSockets, IEnumerable<int> socketIndices)
    {
        if (!moduleGo) return;
        if (!SocketsBuilt) BuildSockets();

        var list = new List<int>(socketIndices);
        
        // Find module in platform's cached list
        PlatformModule pm = null;
        if (_platform)
        {
            foreach (var cachedModule in _platform.CachedModules)
            {
                if (cachedModule && cachedModule.gameObject == moduleGo)
                {
                    pm = cachedModule;
                    break;
                }
            }
        }
        if (!pm) pm = moduleGo.GetComponent<PlatformModule>();
        bool blocks = pm && pm.blocksLink;

        var reg = new ModuleReg { go = moduleGo, socketIndices = list.ToArray(), blocksLink = blocks };
        _moduleRegs[moduleGo] = reg;

        // Map each socket to this module (1:1 relationship - one module max per socket)
        foreach (var sIdx in list)
        {
            _socketToModules[sIdx] = moduleGo;
        }
    }


    public void UnregisterModule(GameObject moduleGo)
    {
        if (!moduleGo) return;
        if (!_moduleRegs.TryGetValue(moduleGo, out var reg)) return;

        if (reg.socketIndices != null)
        {
            foreach (var sIdx in reg.socketIndices)
            {
                _socketToModules.Remove(sIdx);
            }
        }
        _moduleRegs.Remove(moduleGo);
    }


    public void SetModuleHidden(GameObject moduleGo, bool hidden)
    {
        if (!moduleGo) return;

        PlatformModule pm = null;
        if (_platform)
        {
            foreach (var cachedModule in _platform.CachedModules)
            {
                if (cachedModule && cachedModule.gameObject == moduleGo)
                {
                    pm = cachedModule;
                    break;
                }
            }
        }
        if (!pm) pm = moduleGo.GetComponent<PlatformModule>();
        
        if (pm) pm.SetHidden(hidden);
        else moduleGo.SetActive(!hidden);

        RefreshSocketStatuses();
    }


    private void SetModuleVisibility(GameObject moduleGo, bool visible)
    {
        if (!moduleGo) return;
        
        PlatformModule pm = null;
        if (_platform)
        {
            foreach (var cachedModule in _platform.CachedModules)
            {
                if (cachedModule && cachedModule.gameObject == moduleGo)
                {
                    pm = cachedModule;
                    break;
                }
            }
        }
        
        if (pm) pm.SetHidden(!visible);
        else moduleGo.SetActive(visible);
    }


    public void EnsureChildrenModulesRegistered()
    {
        if (!_platform) return;
        
        foreach (var m in _platform.CachedModules)
        {
            if (m) m.EnsureRegistered();
        }
    }
    
    
    #endregion
}
}

