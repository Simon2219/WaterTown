using System;
using System.Collections;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using WaterTown.Interfaces;
using Grid;

namespace WaterTown.Platforms
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavMeshSurface))]
    public class GamePlatform : MonoBehaviour, IPickupable
    {
        #region Configuration & Dependencies
        
        // Instance References to Manager Systems
        // Set via dependency injection from PlatformManager when platform is created
        private PlatformManager _platformManager;
        
        private WorldGrid _worldGrid;

        /// Fired whenever this platform's connection/railing state changes
        public event Action<GamePlatform> ConnectionsChanged;

        /// Fired whenever this platform moves (position/rotation/scale changes)
        public event Action<GamePlatform> HasMoved;
        
        /// Static event fired when ANY platform is created (for initial discovery)
        /// Used by managers to subscribe to this platform's EVENT |s
        public static event Action<GamePlatform> Created;
        
        /// Static event fired when ANY platform is destroyed (for cleanup)
        /// Used by managers to unsubscribe from this platform's EVENT |s
        public static event Action<GamePlatform> Destroyed;
        
        /// EVENT | fired when this platform becomes enabled
        public event Action<GamePlatform> Enabled;
        
        /// EVENT | fired when this platform becomes disabled
        public event Action<GamePlatform> Disabled;
        
        /// EVENT | fired when this platform is placed (after successful placement)
        public event Action<GamePlatform> Placed;
        
        /// EVENT | fired when this platform is picked up (before being moved)
        public event Action<GamePlatform> PickedUp;

        
        public List<Vector2Int> occupiedCells = new();
        public List<Vector2Int> previousOccupiedCells = new();
        
        
        #endregion

        #region IPickupable Implementation
        
        // Pickup/Placement State
        
        public bool IsPickedUp { get; set; }
        public bool CanBePlaced => ValidatePlacement();
        public Transform Transform => transform;
        public GameObject GameObject => gameObject;
        
        private Vector3 _originalPosition;
        private Quaternion _originalRotation;
        private bool _isNewObject;
        private Material[] _originalMaterials;
        private readonly List<Renderer> _allRenderers = new List<Renderer>();
        
        [Header("Pickup Materials (Optional - will auto-create if not assigned)")]
        [SerializeField] private Material pickupValidMaterial;
        [SerializeField] private Material pickupInvalidMaterial;
        
        // Auto-generated materials for testing
        private static Material _autoValidMaterial;
        private static Material _autoInvalidMaterial;
        
        #endregion

        #region Footprint & NavMesh
        
        [Header("Footprint (cells @ 1m)")]
        [Tooltip("Platform footprint in grid cells. X = width, Y = length.")]
        [SerializeField] private Vector2Int footprintSize = new Vector2Int(4, 4);
        public Vector2Int Footprint => footprintSize;

        private NavMeshSurface _navSurface;
        
        // Cached transform for "Links" GameObject to avoid transform.Find() during runtime
        private Transform _linksParentTransform;

        [Header("NavMesh Rebuild")]
        [SerializeField]
        [Tooltip("Delay before rebuilding this platform's NavMesh after changes.\n" +
                 "Lower = more responsive; higher = fewer rebuilds while moving/editing.")]
        private float rebuildDebounceSeconds = 0.1f;

        private Coroutine _pendingRebuild;

        internal NavMeshSurface NavSurface
        {
            get
            {
                // NavMeshSurface is required component, should always exist after Awake
                return _navSurface;
            }
        }
        
        /// Cached Links parent transform (created during initialization)
        internal Transform LinksParentTransform => _linksParentTransform;
        
        /// Creates or finds the Links parent GameObject during platform initialization
        /// This ensures the Links parent always exists after initialization
        private void EnsureLinksParentExists()
        {
            if (_linksParentTransform != null) return;
            
            // Try to find existing Links parent first
            _linksParentTransform = transform.Find("Links");
            
            if (_linksParentTransform == null)
            {
                // Create Links parent if it doesn't exist
                var go = new GameObject("Links");
                _linksParentTransform = go.transform;
                _linksParentTransform.SetParent(transform, false);
                _linksParentTransform.localPosition = Vector3.zero;
                _linksParentTransform.localRotation = Quaternion.identity;
                _linksParentTransform.localScale = Vector3.one;
            }
        }

        private Vector3 _lastPos;
        private Quaternion _lastRot;
        private Vector3 _lastScale;
        
        #endregion

        #region Edge & Socket System
        
        // Edge enum (for compatibility with PlatformModule)
        public enum Edge { North, East, South, West }

        /// Length in whole meters along the given edge (number of segments)
        public int EdgeLengthMeters(Edge edge)
        {
            // X = width (north/south edges) Y = length (east/west edges)
            return (edge == Edge.North || edge == Edge.South) ? footprintSize.x : footprintSize.y;
        }

        // Socket status and location enums
        public enum SocketStatus { Linkable, Occupied, Connected, Locked, Disabled }
        public enum SocketLocation { Edge, Corner }

        /// Socket data structure - represents a connection point on the platform perimeter
        /// Each socket knows its local position, world position, and which direction faces outward
        [System.Serializable]
        public struct SocketData
        {
            [SerializeField, HideInInspector] private int index;
            [SerializeField, HideInInspector] private Vector3 localPos;
            [SerializeField, HideInInspector] private Vector2Int outwardOffset; // Grid offset to adjacent cell (e.g., (0,1) for +Z direction)
            [SerializeField, HideInInspector] private SocketLocation location;
            [SerializeField] private SocketStatus status;
            
            // World position - updated when platform moves
            private Vector3 worldPos;

            public int Index => index;
            public Vector3 LocalPos => localPos;
            public Vector3 WorldPos => worldPos;
            public Vector2Int OutwardOffset => outwardOffset;
            public SocketStatus Status => status;
            public bool IsLinkable => status == SocketStatus.Linkable;
            public SocketLocation Location => location;

            internal void Initialize(int idx, Vector3 lp, Vector2Int outward, SocketStatus defaultStatus)
            {
                index = idx;
                localPos = lp;
                outwardOffset = outward;
                location = SocketLocation.Edge;
                status = defaultStatus;
                worldPos = Vector3.zero; // Will be set when platform updates world positions
            }

            public void SetStatus(SocketStatus s) => status = s;
            
            internal void SetWorldPosition(Vector3 pos) => worldPos = pos;
        }

        [Header("Sockets (perimeter, 1m spacing)")]
        [SerializeField] private List<SocketData> sockets = new();
        private bool _socketsBuilt;

        /// Set of socket indices that are currently part of a connection
        private readonly HashSet<int> _connectedSockets = new();
        
        /// Read-only access to connected sockets for external systems
        public IReadOnlyCollection<int> ConnectedSockets => _connectedSockets;

        public IReadOnlyList<SocketData> Sockets
        {
            get { if (!_socketsBuilt) BuildSockets(); return sockets; }
        }

        /// Socket count is static after initialization (determined by footprint size)
        /// No need to cache - just ensure sockets are built once, then return count directly
        public int SocketCount
        {
            get 
            { 
                // Build sockets once if not already built
                if (!_socketsBuilt) BuildSockets();
                
                // Socket count is static after BuildSockets() - footprint size doesn't change at runtime
                return sockets.Count;
            }
        }

        ///
        /// Build sockets along the perimeter of the footprint, in local space,
        /// one socket per 1m edge segment
        /// Each socket stores its outward direction (grid offset) for adjacency checks
        /// Order: +Z edge, -Z edge, +X edge, -X edge (for compat with Edge API)
        ///
        public void BuildSockets()
        {
            var prev = new Dictionary<Vector3, SocketStatus>();
            foreach (var s in sockets)
                prev[s.LocalPos] = s.Status;

            sockets.Clear();
            _socketsBuilt = false;
            
            // Invalidate Links parent cache if it existed (will be re-found if needed)
            _linksParentTransform = null;

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

            _socketsBuilt = true;
            
            // Calculate initial world positions for all sockets
            UpdateSocketWorldPositions();
        }

        public SocketData GetSocket(int index)
        {
            if (!_socketsBuilt) BuildSockets();
            if (index < 0 || index >= sockets.Count)
            {
                Debug.LogWarning($"[GamePlatform] GetSocket: index {index} out of range (0..{sockets.Count - 1}).", this);
                return default;
            }
            return sockets[index];
        }

        public Vector3 GetSocketWorldPosition(int index)
        {
            if (!_socketsBuilt) BuildSockets();
            
            // Bounds check
            if (index < 0 || index >= sockets.Count)
            {
                return transform.position;
            }
            
            return sockets[index].WorldPos;
        }


        /// Updates world positions for all sockets based on current transform
        /// Called when the platform moves or after sockets are built
        private void UpdateSocketWorldPositions()
        {
            if (!_socketsBuilt) return;
            
            for (int i = 0; i < sockets.Count; i++)
            {
                var socket = sockets[i];
                socket.SetWorldPosition(transform.TransformPoint(socket.LocalPos));
                sockets[i] = socket;
            }
        }

        public void SetSocketStatus(int index, SocketStatus status)
        {
            if (!_socketsBuilt) BuildSockets();
            if (index < 0 || index >= sockets.Count)
            {
                Debug.LogWarning($"[GamePlatform] SetSocketStatus: index {index} out of range (0..{sockets.Count - 1}).", this);
                return;
            }
            var s = sockets[index];
            s.SetStatus(status);
            sockets[index] = s;
        }

        ///
        /// Gets the socket index range (start, end inclusive) for a given edge
        /// Useful for iterating sockets on a specific edge
        ///
        public void GetSocketIndexRangeForEdge(Edge edge, out int startIndex, out int endIndex)
        {
            if (!_socketsBuilt) BuildSockets();

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

        ///
        /// Compatibility helper for code that thinks in Edge+mark (PlatformModule, old tools)
        /// Uses the socket ordering defined in BuildSockets()
        ///
        public int GetSocketIndexByEdgeMark(Edge edge, int mark)
        {
            if (!_socketsBuilt) BuildSockets();

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
            if (!_socketsBuilt) BuildSockets();
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

        ///
        /// Finds up to maxCount nearest socket indices to localPos within maxDistance
        ///
        public void FindNearestSocketIndicesLocal(
            Vector3 localPos,
            int maxCount,
            float maxDistance,
            List<int> result)
        {
            result.Clear();
            if (!_socketsBuilt) BuildSockets();
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

        ///
        /// Convenience: find nearest socket to a WORLD position
        /// Just converts to local space and reuses FindNearestSocketIndexLocal
        ///
        public int FindNearestSocketIndexWorld(Vector3 worldPos)
        {
            Vector3 local = transform.InverseTransformPoint(worldPos);
            return FindNearestSocketIndexLocal(local);
        }
        
        #endregion

        #region Module Registry
        
        // Module registry

        [System.Serializable]
        public struct ModuleReg
        {
            public GameObject go;
            public int[] socketIndices;
            public bool blocksLink;
        }

        private readonly Dictionary<GameObject, ModuleReg> _moduleRegs = new();
        private readonly Dictionary<int, List<GameObject>> _socketToModules = new();
        
        // Cached lists of all modules and railings (populated at initialization to avoid runtime GetComponentsInChildren)
        private readonly List<PlatformModule> _cachedModules = new List<PlatformModule>();
        private readonly List<PlatformRailing> _cachedRailings = new List<PlatformRailing>();
        private readonly List<Collider> _cachedColliders = new List<Collider>();
        private bool _childComponentsCached = false;

        public void RegisterModuleOnSockets(GameObject moduleGo, bool occupiesSockets, IEnumerable<int> socketIndices)
        {
            if (!moduleGo) return;
            if (!_socketsBuilt) BuildSockets();

            var list = new List<int>(socketIndices);
            // Try to find PlatformModule from cached list first, fallback to GetComponent only if not found
            PlatformModule pm = null;
            foreach (var cachedModule in _cachedModules)
            {
                if (cachedModule && cachedModule.gameObject == moduleGo)
                {
                    pm = cachedModule;
                    break;
                }
            }
            // If not in cache, this shouldn't happen but fallback for safety
            if (!pm) pm = moduleGo.GetComponent<PlatformModule>();
            bool blocks = pm ? pm.blocksLink : false;

            var reg = new ModuleReg { go = moduleGo, socketIndices = list.ToArray(), blocksLink = blocks };
            _moduleRegs[moduleGo] = reg;

            foreach (var sIdx in list)
            {
                if (!_socketToModules.TryGetValue(sIdx, out var l))
                {
                    l = new List<GameObject>();
                    _socketToModules[sIdx] = l;
                }
                if (!l.Contains(moduleGo)) l.Add(moduleGo);
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
                    if (_socketToModules.TryGetValue(sIdx, out var list))
                    {
                        list.Remove(moduleGo);
                        if (list.Count == 0) _socketToModules.Remove(sIdx);
                    }
                }
            }
            _moduleRegs.Remove(moduleGo);
        }
        
        #endregion

        #region Railing Registry
        
        // Railing registry
        private readonly Dictionary<int, List<PlatformRailing>> _socketToRailings = new();
        
        // Counter-based tracking of visible rails per socket (O(1) lookup for post visibility)
        private readonly Dictionary<int, int> _visibleRailCountPerSocket = new();

        /// Called by PlatformRailing to bind itself to given socket indices
        public void RegisterRailing(PlatformRailing railing)
        {
            if (!railing) return;
            if (!_socketsBuilt) BuildSockets();

            var indices = railing.SocketIndices;
            if (indices == null || indices.Length == 0)
            {
                // fallback: bind to nearest socket
                int nearest = FindNearestSocketIndexLocal(transform.InverseTransformPoint(railing.transform.position));
                if (nearest >= 0)
                {
                    indices = new[] { nearest };
                    railing.SetSocketIndices(indices);
                }
                else
                    return;
            }

            foreach (int sIdx in indices)
            {
                if (!_socketToRailings.TryGetValue(sIdx, out var list))
                {
                    list = new List<PlatformRailing>();
                    _socketToRailings[sIdx] = list;
                }
                if (!list.Contains(railing)) list.Add(railing);
                
                // Initialize counter if needed
                if (!_visibleRailCountPerSocket.ContainsKey(sIdx))
                    _visibleRailCountPerSocket[sIdx] = 0;
            }
            
            // If this is a visible Rail, increment counters
            if (railing.type == PlatformRailing.RailingType.Rail && !railing.IsHidden)
            {
                foreach (int sIdx in indices)
                    _visibleRailCountPerSocket[sIdx]++;
            }
        }

        /// Called by PlatformRailing when disabled/destroyed
        public void UnregisterRailing(PlatformRailing railing)
        {
            if (!railing) return;
            
            var indices = railing.SocketIndices;
            
            // If this was a visible Rail, decrement counters before removal
            if (indices != null && railing.type == PlatformRailing.RailingType.Rail && !railing.IsHidden)
            {
                foreach (int sIdx in indices)
                {
                    if (_visibleRailCountPerSocket.TryGetValue(sIdx, out int count))
                        _visibleRailCountPerSocket[sIdx] = Mathf.Max(0, count - 1);
                }
            }

            foreach (var kv in _socketToRailings)
            {
                kv.Value.Remove(railing);
            }
        }
        
        /// Called by PlatformRailing when a Rail's visibility changes
        /// Updates the counter for O(1) post visibility checks
        internal void OnRailVisibilityChanged(PlatformRailing rail, bool nowHidden)
        {
            if (rail == null || rail.type != PlatformRailing.RailingType.Rail) return;
            
            var indices = rail.SocketIndices;
            if (indices == null) return;
            
            int delta = nowHidden ? -1 : 1;
            foreach (int sIdx in indices)
            {
                if (_visibleRailCountPerSocket.TryGetValue(sIdx, out int count))
                    _visibleRailCountPerSocket[sIdx] = Mathf.Max(0, count + delta);
                else if (!nowHidden)
                    _visibleRailCountPerSocket[sIdx] = 1;
            }
        }

        /// True if the given socket index is currently part of a connection
        public bool IsSocketConnected(int socketIndex) => _connectedSockets.Contains(socketIndex);

        /// Returns true if any of the given sockets has at least one visible rail
        /// Directly checks Rail visibility state instead of relying on counters
        public bool HasVisibleRailOnSockets(int[] socketIndices)
        {
            if (socketIndices == null || socketIndices.Length == 0) return false;
            
            foreach (int socketIndex in socketIndices)
            {
                if (_socketToRailings.TryGetValue(socketIndex, out var railings))
                {
                    foreach (var railing in railings)
                    {
                        // Check if this is a visible Rail (not Post)
                        if (railing && railing.type == PlatformRailing.RailingType.Rail && !railing.IsHidden)
                            return true;
                    }
                }
            }
            return false;
        }

        /// Triggers visibility update on all railings
        /// IMPORTANT: Rails must update FIRST so counters are correct when Posts check visibility
        internal void RefreshAllRailingsVisibility()
        {
            // First pass: update all Rails (they update the visibility counters via SetHidden)
            foreach (var r in _cachedRailings)
            {
                if (r && r.type == PlatformRailing.RailingType.Rail)
                    r.UpdateVisibility();
            }
            
            // Second pass: update all Posts (they use HasVisibleRailOnSockets which reads counters)
            foreach (var r in _cachedRailings)
            {
                if (r && r.type == PlatformRailing.RailingType.Post)
                    r.UpdateVisibility();
            }
        }

        /// Recompute every socket's status from current modules + connection state
        public void RefreshSocketStatuses()
        {
            if (!_socketsBuilt) BuildSockets();

            for (int i = 0; i < sockets.Count; i++)
            {
                var s = sockets[i];

                if (s.Status == SocketStatus.Locked || s.Status == SocketStatus.Disabled)
                {
                    sockets[i] = s;
                    continue;
                }

                if (_connectedSockets.Contains(i))
                {
                    s.SetStatus(SocketStatus.Connected);
                    sockets[i] = s;
                    continue;
                }

                bool blocked = false;
                if (_socketToModules.TryGetValue(i, out var mods))
                {
                    foreach (var go in mods)
                    {
                        if (!go) continue;
                        if (!go.activeInHierarchy) continue;
                        if (!_moduleRegs.TryGetValue(go, out var reg)) continue;
                        if (reg.blocksLink) { blocked = true; break; }
                    }
                }

                s.SetStatus(blocked ? SocketStatus.Occupied : SocketStatus.Linkable);
                sockets[i] = s;
            }
        }


        ///
        /// Gets the world-space outward direction for a socket (accounts for platform rotation)
        /// The outward direction points away from the platform toward the adjacent cell
        ///
        public Vector3 GetSocketWorldOutwardDirection(int socketIndex)
        {
            if (!_socketsBuilt) BuildSockets();
            if (socketIndex < 0 || socketIndex >= sockets.Count)
                return Vector3.zero;

            var socket = sockets[socketIndex];
            // Convert the local outward direction to world space
            // OutwardOffset is in grid coords (X, Y) where Y maps to world Z
            Vector3 localOutward = new Vector3(socket.OutwardOffset.x, 0f, socket.OutwardOffset.y);
            return transform.TransformDirection(localOutward).normalized;
        }


        ///
        /// Gets the grid cell adjacent to a socket (the cell the socket faces toward)
        /// Accounts for platform rotation by transforming the outward direction
        ///
        public Vector2Int GetAdjacentCellForSocket(int socketIndex)
        {
            if (!_socketsBuilt) BuildSockets();
            if (socketIndex < 0 || socketIndex >= sockets.Count)
                return Vector2Int.zero;

            // Get socket world position
            Vector3 socketWorldPos = GetSocketWorldPosition(socketIndex);
            
            // Get world-space outward direction (accounts for rotation)
            Vector3 worldOutward = GetSocketWorldOutwardDirection(socketIndex);
            
            // Calculate adjacent cell position (socket pos + small offset in outward direction)
            // We use 0.5m offset to ensure we're clearly in the adjacent cell
            Vector3 adjacentWorldPos = socketWorldPos + worldOutward * 0.5f;
            
            return _worldGrid.WorldToCell(adjacentWorldPos);
        }


        // ReSharper disable Unity.PerformanceAnalysis
        ///
        /// Updates socket connection statuses based on adjacent grid cell occupancy
        /// Each socket checks its adjacent cell - if occupied, the socket becomes Connected
        /// When new connections are detected, requests NavMesh link creation from PlatformManager
        ///
        public void UpdateSocketStatusesFromGrid()
        {
            if (!_socketsBuilt) BuildSockets();
            if (!_worldGrid || !_platformManager) return;

            var newConnectedSockets = new HashSet<int>();
            var newNeighbors = new HashSet<GamePlatform>();
            var previousNeighbors = GetCurrentNeighbors();

            for (int i = 0; i < sockets.Count; i++)
            {
                var socket = sockets[i];
                
                // Skip locked/disabled sockets
                if (socket.Status == SocketStatus.Locked || socket.Status == SocketStatus.Disabled)
                    continue;

                // Get the adjacent cell for this socket
                Vector2Int neighborCell = GetAdjacentCellForSocket(i);
                
                // Check if the adjacent cell is occupied by another platform
                if (_platformManager.GetPlatformAtCell(neighborCell, out var neighborPlatform))
                {
                    if (neighborPlatform && neighborPlatform != this)
                    {
                        newConnectedSockets.Add(i);
                        newNeighbors.Add(neighborPlatform);
                    }
                }
            }

            // Apply connection changes
            bool hadChanges = !newConnectedSockets.SetEquals(_connectedSockets);
            if (hadChanges)
            {
                UpdateConnections(newConnectedSockets);
            }
            
            // Request NavMesh links for newly connected neighbors (only when placed, not preview)
            if (!IsPickedUp && _platformManager)
            {
                foreach (var neighbor in newNeighbors)
                {
                    // Only create link if this is a new connection
                    if (!previousNeighbors.Contains(neighbor))
                    {
                        _platformManager.RequestNavMeshLink(this, neighbor);
                    }
                }
            }
        }


        ///
        /// Gets current neighbor platforms based on connected sockets
        ///
        private HashSet<GamePlatform> GetCurrentNeighbors()
        {
            var neighbors = new HashSet<GamePlatform>();

            foreach (int socketIndex in _connectedSockets)
            {
                Vector2Int adjacentCell = GetAdjacentCellForSocket(socketIndex);
                if (_platformManager.GetPlatformAtCell(adjacentCell, out GamePlatform neighbor)
                    && neighbor != null && neighbor != this)
                {
                    neighbors.Add(neighbor);
                }
            }
            return neighbors;
        }


        ///
        /// Gets all sockets that are currently connected to a specific neighbor platform
        /// Useful for NavMesh link positioning
        ///
        public List<int> GetSocketsConnectedToNeighbor(GamePlatform neighbor)
        {
            var result = new List<int>();
            if (!_socketsBuilt) BuildSockets();
            if (!neighbor || !_platformManager) return result;

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
        
        public void SetModuleHidden(GameObject moduleGo, bool hidden)
        {
            if (!moduleGo) return;

            // Try to find PlatformModule from cached list first
            PlatformModule pm = null;
            foreach (var cachedModule in _cachedModules)
            {
                if (cachedModule && cachedModule.gameObject == moduleGo)
                {
                    pm = cachedModule;
                    break;
                }
            }
            // Fallback if not found in cache (shouldn't happen)
            if (!pm) pm = moduleGo.GetComponent<PlatformModule>();
            
            if (pm != null) pm.SetHidden(hidden);
            else moduleGo.SetActive(!hidden);

            RefreshSocketStatuses();
            QueueRebuild();
        }

        ///
        /// Incrementally updates connections - only modifies sockets that changed.
        /// Compares new connected sockets with current state and applies the delta.
        /// Called by PlatformManager during adjacency recomputation.
        ///
        public void UpdateConnections(HashSet<int> newConnectedSockets)
        {
            if (newConnectedSockets == null) newConnectedSockets = new HashSet<int>();
            
            // Find sockets to disconnect (were connected, now not)
            var socketsToDisconnect = new List<int>();
            foreach (int socketIndex in _connectedSockets)
            {
                if (!newConnectedSockets.Contains(socketIndex))
                    socketsToDisconnect.Add(socketIndex);
            }
            
            // Find sockets to connect (weren't connected, now are)
            var socketsToConnect = new List<int>();
            foreach (int socketIndex in newConnectedSockets)
            {
                if (!_connectedSockets.Contains(socketIndex))
                    socketsToConnect.Add(socketIndex);
            }
            
            // Early exit if nothing changed
            if (socketsToDisconnect.Count == 0 && socketsToConnect.Count == 0)
                return;
            
            // Update _connectedSockets state FIRST (before visibility updates)
            foreach (int socketIndex in socketsToDisconnect)
                _connectedSockets.Remove(socketIndex);
            foreach (int socketIndex in socketsToConnect)
                _connectedSockets.Add(socketIndex);
            
            // Now update module visibility based on new state
            foreach (int socketIndex in socketsToDisconnect)
            {
                if (_socketToModules.TryGetValue(socketIndex, out var modules))
                {
                    foreach (var module in modules)
                        SetModuleVisibility(module, visible: true);
                }
            }
            
            foreach (int socketIndex in socketsToConnect)
            {
                if (_socketToModules.TryGetValue(socketIndex, out var modules))
                {
                    foreach (var module in modules)
                        SetModuleVisibility(module, visible: false);
                }
            }
            
            // Update ALL railings - they check socket state themselves
            // This ensures posts correctly reflect rail visibility changes
            RefreshAllRailingsVisibility();
            
            RefreshSocketStatuses();
            ConnectionsChanged?.Invoke(this);
        }
        
        /// Sets module visibility without triggering full refresh
        private void SetModuleVisibility(GameObject moduleGo, bool visible)
        {
            if (!moduleGo) return;
            
            // Try to find PlatformModule from cached list first
            PlatformModule pm = null;
            foreach (var cachedModule in _cachedModules)
            {
                if (cachedModule && cachedModule.gameObject == moduleGo)
                {
                    pm = cachedModule;
                    break;
                }
            }
            
            if (pm) pm.SetHidden(!visible);
            else moduleGo.SetActive(visible);
        }

        ///
        /// Runtime method to reset all connections to baseline.
        /// Only used when unregistering platform or during editor operations.
        /// For normal adjacency updates, use UpdateConnections() instead.
        ///
        public void ResetConnections()
        {
            // Show all PlatformModules using cached list
            foreach (var m in _cachedModules)
            {
                if (!m) continue;
                m.SetHidden(false);
            }

            // Clear connection bookkeeping
            _connectedSockets.Clear();
            
            // Refresh rail visibility and socket statuses
            RefreshAllRailingsVisibility();
            RefreshSocketStatuses();
            
            ConnectionsChanged?.Invoke(this);
        }


        ///
        /// Editor-only method to reset connections and clean up NavMesh links with Undo support
        /// This should NEVER be called during runtime - use ResetConnections() instead
        ///
#if UNITY_EDITOR
        public void EditorResetAllConnections()
        {
            ResetConnections();

            // Destroy all NavMeshLink GameObjects under "Links" in the editor with Undo
            // Use cached transform if available, otherwise find it (editor-only path)
            var linksParent = LinksParentTransform ?? transform.Find("Links");
            if (linksParent)
            {
                for (int i = linksParent.childCount - 1; i >= 0; i--)
                    UnityEditor.Undo.DestroyObjectImmediate(linksParent.GetChild(i).gameObject);
            }
            
            // Only queue rebuild if we're active (skip during shutdown)
            if (gameObject.activeInHierarchy)
                QueueRebuild();
        }
#endif
        
        #endregion

        #region Lifecycle & Initialization

        private void Awake()
        {
            // Fire static creation event for managers to subscribe to EVENT |s
            // PlatformManager will inject itself via SetPlatformManager() in response to this event
            Created?.Invoke(this);
            
            // NavMeshSurface is required component, cache it at Awake
            _navSurface = GetComponent<NavMeshSurface>();
            if (!_navSurface)
            {
                ErrorHandler.LogAndDisable(new Exception($"Required dependency '{typeof(NavMeshSurface).Name}' not found."), this);
                return;
            }
            
            // Cache all child components at initialization
            CacheChildComponents();
            
            InitializePlatform();
        }
        
        /// Called by PlatformManager to inject itself as a dependency
        /// This avoids FindFirstObjectByType calls during runtime
        public void SetPlatformManager(PlatformManager platformManager)
        {
            _platformManager = platformManager;
        }

        public void SetWorldGrid(WorldGrid worldGrid)
        {
            _worldGrid = worldGrid;
        }

        private void OnEnable()
        {
            _lastPos = transform.position;
            _lastRot = transform.rotation;
            _lastScale = transform.localScale;
            
            InitializePlatform();
            
            // Fire EVENT |
            Enabled?.Invoke(this);
        }


        private void OnDisable()
        {
            // Fire EVENT |
            Disabled?.Invoke(this);
        }


        private void OnDestroy()
        {
            // Fire static destruction event for managers to unsubscribe
            Destroyed?.Invoke(this);
        }


        private void LateUpdate()
        {
            if (IsPickedUp)
            {
                if (transform.position != _lastPos ||
                    transform.rotation != _lastRot ||
                    transform.localScale != _lastScale)
                {
                    _lastPos = transform.position;
                    _lastRot = transform.rotation;
                    _lastScale = transform.localScale;

                    // Update socket world positions based on new transform
                    UpdateSocketWorldPositions();

                    HasMoved?.Invoke(this);
                }
            }
        }


        private void InitializePlatform()
        {
            // Build sockets once on initialization
            if (!_socketsBuilt) BuildSockets();
            
            // Ensure Links parent exists for NavMesh links
            EnsureLinksParentExists();
            
            // Register child components once on initialization
            EnsureChildrenModulesRegistered();
            EnsureChildrenRailingsRegistered();
            
            // Initial socket status calculation
            RefreshSocketStatuses();
        }


        public void ForceHasMoved()
        {
            _lastPos = transform.position;
            _lastRot = transform.rotation;
            _lastScale = transform.localScale;
            HasMoved?.Invoke(this);
        }


        public void BuildLocalNavMesh()
        {
            if (NavSurface) NavSurface.BuildNavMesh();
        }


        public void QueueRebuild()
        {
            if (!NavSurface) return;
            
            // Don't start coroutines on inactive objects (e.g during shutdown/cleanup)
            if (!gameObject.activeInHierarchy) return;
            
            if (_pendingRebuild != null)
                StopCoroutine(_pendingRebuild);
            _pendingRebuild = StartCoroutine(RebuildAfterDelay(rebuildDebounceSeconds));
        }


        private IEnumerator RebuildAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (NavSurface)
                NavSurface.BuildNavMesh();
            _pendingRebuild = null;
        }
        
        #endregion

        #region Gizmos (Editor Visualization)

        public static class GizmoSettings
        {
            public static bool ShowGizmos = true;
            public static bool ShowIndices = true;
            public static float SocketSphereRadius = 0.06f;

            public static Color ColorFree = new(0.20f, 1.00f, 0.20f, 0.90f);
            public static Color ColorOccupied = new(1.00f, 0.60f, 0.20f, 0.90f);
            public static Color ColorLocked = new(0.95f, 0.25f, 0.25f, 0.90f);
            public static Color ColorDisabled = new(0.60f, 0.60f, 0.60f, 0.90f);
            public static Color ColorConnected = new(0.20f, 0.65f, 1.00f, 0.90f);

            public static bool ShowDirections = false;   // we don't need normal arrows anymore
            public static float DirectionLength = 0.28f;

            public static void SetVisibility(bool visible) => ShowGizmos = visible;
            public static void SetShowIndices(bool show) => ShowIndices = show;

            public static void SetColors(Color free, Color occupied, Color locked, Color disabled)
            {
                ColorFree = free;
                ColorOccupied = occupied;
                ColorLocked = locked;
                ColorDisabled = disabled;
            }

            public static void SetColorsAll(Color free, Color occupied, Color locked, Color disabled, Color connected)
            {
                SetColors(free, occupied, locked, disabled);
                ColorConnected = connected;
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!GizmoSettings.ShowGizmos) return;

            // Platform footprint outline
            Gizmos.color = new Color(0f, 0.6f, 1f, 0.25f);
            float hx = footprintSize.x * 0.5f;
            float hz = footprintSize.y * 0.5f;
            var p = transform.position; var r = transform.right; var f = transform.forward;
            Vector3 a = p + (-r * hx) + (-f * hz);
            Vector3 b = p + (r * hx) + (-f * hz);
            Vector3 c = p + (r * hx) + (f * hz);
            Vector3 d = p + (-r * hx) + (f * hz);
            Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c); Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);

            if (!_socketsBuilt) BuildSockets();

            int footprintWidth = Mathf.Max(1, footprintSize.x);
            int footprintLength = Mathf.Max(1, footprintSize.y);

            for (int i = 0; i < sockets.Count; i++)
            {
                var s = sockets[i];
                Color col = s.Status switch
                {
                    SocketStatus.Linkable   => GizmoSettings.ColorFree,
                    SocketStatus.Occupied   => GizmoSettings.ColorOccupied,
                    SocketStatus.Connected  => GizmoSettings.ColorConnected,
                    SocketStatus.Locked     => GizmoSettings.ColorLocked,
                    SocketStatus.Disabled   => GizmoSettings.ColorDisabled,
                    _                       => Color.white
                };
                Gizmos.color = col;

                Vector3 wp = transform.TransformPoint(s.LocalPos);
                Gizmos.DrawSphere(wp, GizmoSettings.SocketSphereRadius);

                if (GizmoSettings.ShowIndices)
                {
                    // Reconstruct pseudo edge+mark only for labeling (for debug)
                    Edge edge;
                    int mark;
                    if (i < footprintWidth)                { edge = Edge.North; mark = i; }
                    else if (i < 2 * footprintWidth)       { edge = Edge.South; mark = i - footprintWidth; }
                    else if (i < 2 * footprintWidth + footprintLength)   { edge = Edge.East;  mark = i - 2 * footprintWidth; }
                    else                      { edge = Edge.West;  mark = i - (2 * footprintWidth + footprintLength); }

                    string label = $"#{i} [{edge}:{mark}] {s.Status}";
                    UnityEditor.Handles.Label(wp + Vector3.up * 0.05f, label);
                }
            }
        }
#endif
        
        #endregion

        #region Child Component Registration

        /// Caches all child components (modules, railings, colliders) at initialization
        /// This avoids expensive GetComponentsInChildren calls during runtime
        private void CacheChildComponents()
        {
            if (_childComponentsCached) return;
            
            _cachedModules.Clear();
            _cachedRailings.Clear();
            _cachedColliders.Clear();
            
            // Only do GetComponentsInChildren once at initialization
            _cachedModules.AddRange(GetComponentsInChildren<PlatformModule>(true));
            _cachedRailings.AddRange(GetComponentsInChildren<PlatformRailing>(true));
            _cachedColliders.AddRange(GetComponentsInChildren<Collider>(true));
            
            _childComponentsCached = true;
        }

        public void EnsureChildrenModulesRegistered()
        {
            // Use cached modules list
            foreach (var m in _cachedModules)
            {
                if (m) m.EnsureRegistered();
            }
        }


        public void EnsureChildrenRailingsRegistered()
        {
            // Use cached railings list
            foreach (var r in _cachedRailings)
            {
                if (r) r.EnsureRegistered();
            }
        }
        
        #endregion

        #region IPickupable Interface Methods

        public void OnPickedUp(bool isNewObject)
        {
            IsPickedUp = true;
            _isNewObject = isNewObject;
            
            // Store original transform for cancellation
            _originalPosition = transform.position;
            _originalRotation = transform.rotation;
            
            // Disable colliders so we can raycast through the platform (use cached list)
            foreach (var col in _cachedColliders)
            {
                if (col) col.enabled = false;
            }
            
            // Cache renderers and store original materials from all renderers
            // Renderers are cached on-demand since they're less frequently accessed
            if (_allRenderers.Count == 0)
            {
                _allRenderers.AddRange(GetComponentsInChildren<Renderer>(true));
            }
            
            // Store all original materials (we'll restore them per-renderer later)
            // Just use the first renderer as reference for now
            if (_allRenderers.Count > 0 && _allRenderers[0] != null)
            {
                _originalMaterials = _allRenderers[0].sharedMaterials;
            }
            
            // Fire pickup event for existing platforms (not for new spawned ones)
            if (!isNewObject)
            {
                PickedUp?.Invoke(this);
            }
        }


        public void OnPlaced()
        {
            // Restore colliders (use cached list)
            foreach (var col in _cachedColliders)
            {
                if (col) col.enabled = true;
            }
            
            // Restore original materials
            RestoreOriginalMaterials();
            
            // Compute cells for placement (defensive null check)
            if (_platformManager == null)
            {
                Debug.LogError($"[GamePlatform] Cannot place platform '{name}' - PlatformManager not initialized!");
                return;
            }
            
            List<Vector2Int> cells = _platformManager.GetCellsForPlatform(this);
            occupiedCells = cells;
            
            // Set IsPickedUp to false before firing event
            // This ensures managers see the platform in correct state
            IsPickedUp = false;
            
            // Fire event for managers to register platform and trigger adjacency
            Placed?.Invoke(this);
        }


        public void OnPlacementCancelled()
        {
            IsPickedUp = false;
            
            if (_isNewObject)
            {
                // New object - destroy it
                // BuildModeManager will trigger adjacency update after this returns
                Destroy(gameObject);
            }
            else
            {
                // Existing object - restore original position
                transform.position = _originalPosition;
                transform.rotation = _originalRotation;
                
                // Re-enable colliders (use cached list)
                foreach (var col in _cachedColliders)
                {
                    if (col) col.enabled = true;
                }
                
                // Restore original materials
                RestoreOriginalMaterials();
                
                // Compute cells and fire placement event to re-register at original position
                // This triggers adjacency recomputation so railings/NavMesh links update
                if (_platformManager == null)
                {
                    Debug.LogError($"[GamePlatform] Cannot cancel placement of platform '{name}' - PlatformManager not initialized!");
                    return;
                }
                
                List<Vector2Int> cells = _platformManager.GetCellsForPlatform(this);
                occupiedCells = cells;
                
                Placed?.Invoke(this);
            }
        }


        public void UpdateValidityVisuals(bool isValid)
        {
            // Use assigned materials if available, otherwise create auto-generated ones
            Material targetMaterial = isValid 
                ? (pickupValidMaterial != null ? pickupValidMaterial : GetAutoValidMaterial())
                : (pickupInvalidMaterial != null ? pickupInvalidMaterial : GetAutoInvalidMaterial());
            
            if (targetMaterial != null)
            {
                foreach (var renderer in _allRenderers)
                {
                    if (renderer != null)
                    {
                        var materials = renderer.sharedMaterials;
                        for (int i = 0; i < materials.Length; i++)
                            materials[i] = targetMaterial;
                        renderer.sharedMaterials = materials;
                    }
                }
            }
        }


        ///
        /// Creates or returns the auto-generated green translucent material for valid placement
        /// AUTO-GENERATED for testing purposes - assign custom materials in inspector for production
        ///
        private static Material GetAutoValidMaterial()
        {
            if (_autoValidMaterial == null)
            {
                // Try URP shader first, fallback to Universal Render Pipeline/Lit, then Standard
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Standard");
                
                _autoValidMaterial = new Material(shader);
                _autoValidMaterial.name = "Auto_ValidPlacement (Testing)";
                
                // Set base color with transparency
                _autoValidMaterial.SetColor("_BaseColor", new Color(0f, 1f, 0f, 0.6f)); // URP
                _autoValidMaterial.SetColor("_Color", new Color(0f, 1f, 0f, 0.6f));     // Standard fallback
                
                // Enable transparency for URP
                _autoValidMaterial.SetFloat("_Surface", 1); // Transparent
                _autoValidMaterial.SetFloat("_Blend", 0);   // Alpha blend
                _autoValidMaterial.SetFloat("_AlphaClip", 0);
                _autoValidMaterial.SetFloat("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _autoValidMaterial.SetFloat("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _autoValidMaterial.SetFloat("_ZWrite", 0);
                
                // Set render queue for transparency
                _autoValidMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                
                // Enable keywords for transparency
                _autoValidMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                _autoValidMaterial.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            }
            return _autoValidMaterial;
        }


        ///
        /// Creates or returns the auto-generated red translucent material for invalid placement
        /// AUTO-GENERATED for testing purposes - assign custom materials in inspector for production
        ///
        private static Material GetAutoInvalidMaterial()
        {
            if (_autoInvalidMaterial == null)
            {
                // Try URP shader first, fallback to Universal Render Pipeline/Lit, then Standard
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Standard");
                
                _autoInvalidMaterial = new Material(shader);
                _autoInvalidMaterial.name = "Auto_InvalidPlacement (Testing)";
                
                // Set base color with transparency
                _autoInvalidMaterial.SetColor("_BaseColor", new Color(1f, 0f, 0f, 0.6f)); // URP
                _autoInvalidMaterial.SetColor("_Color", new Color(1f, 0f, 0f, 0.6f));     // Standard fallback
                
                // Enable transparency for URP
                _autoInvalidMaterial.SetFloat("_Surface", 1); // Transparent
                _autoInvalidMaterial.SetFloat("_Blend", 0);   // Alpha blend
                _autoInvalidMaterial.SetFloat("_AlphaClip", 0);
                _autoInvalidMaterial.SetFloat("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _autoInvalidMaterial.SetFloat("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _autoInvalidMaterial.SetFloat("_ZWrite", 0);
                
                // Set render queue for transparency
                _autoInvalidMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                
                // Enable keywords for transparency
                _autoInvalidMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                _autoInvalidMaterial.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            }
            return _autoInvalidMaterial;
        }


        private void RestoreOriginalMaterials()
        {
            if (_originalMaterials != null && _originalMaterials.Length > 0)
            {
                foreach (var renderer in _allRenderers)
                {
                    if (renderer != null)
                    {
                        renderer.sharedMaterials = _originalMaterials;
                    }
                }
            }
        }


        private bool ValidatePlacement()
        {
            if (!IsPickedUp) return true; // Not being placed
            
            // If platform manager hasn't been injected yet, cannot validate placement
            // This can happen if ValidatePlacement is called before SetPlatformManager()
            if (_platformManager == null)
            {
                return false;
            }
            
            // Compute cells this platform would occupy
            List<Vector2Int> cells = _platformManager.GetCellsForPlatform(this);
            
            if (cells.Count == 0) return false;
            
            // Check if area is free (OccupyPreview doesn't block, only Occupied does)
            return _platformManager.IsAreaEmpty(cells);
        }
        
        #endregion
    }
}
