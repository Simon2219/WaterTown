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
        
        private PlatformManager _platformManager;

        /// Fired whenever this platform's connection/railing state changes
        public event Action<GamePlatform> ConnectionsChanged;

        /// Fired whenever this platform's pose changes (position/rotation/scale)
        public event Action<GamePlatform> PoseChanged;
        
        /// Static event fired when ANY platform becomes enabled
        /// Used by managers for registration
        public static event Action<GamePlatform> PlatformEnabled;
        
        /// Static event fired when ANY platform becomes disabled
        /// Used by managers for cleanup
        public static event Action<GamePlatform> PlatformDisabled;
        
        /// Static event fired when ANY platform is placed (after successful placement)
        /// Used by managers to register platform in grid and trigger adjacency
        public static event Action<GamePlatform> PlatformPlaced;
        
        /// Static event fired when ANY platform is picked up (before being moved)
        /// Used by managers to update adjacency without full unregister overhead
        public static event Action<GamePlatform> PlatformPickedUp;

        public List<Vector2Int> occupiedCells = null;
        
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
                if (!_navSurface)
                    _navSurface = GetComponent<NavMeshSurface>();
                return _navSurface;
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

        // Sockets (grid-based, direction-agnostic)

        public enum SocketStatus { Linkable, Occupied, Connected, Locked, Disabled }

        public enum SocketLocation { Edge, Corner }  // Corner kept for backward compat (we only create Edge sockets)

        [System.Serializable]
        public struct SocketInfo
        {
            [SerializeField, HideInInspector] private int index;
            [SerializeField, HideInInspector] private Vector3 localPos;
            [SerializeField, HideInInspector] private SocketLocation location;
            [SerializeField] private SocketStatus status;

            public int Index => index;
            public Vector3 LocalPos => localPos;
            public SocketStatus Status => status;
            public bool IsLinkable => status == SocketStatus.Linkable;
            public SocketLocation Location => location;

            internal void Initialize(int idx, Vector3 lp, SocketStatus defaultStatus)
            {
                index = idx;
                localPos = lp;
                location = SocketLocation.Edge; // we only create edge sockets in this setup
                status = defaultStatus;
            }

            public void SetStatus(SocketStatus s) => status = s;
        }

        [Header("Sockets (perimeter, 1m spacing)")]
        [SerializeField] private List<SocketInfo> sockets = new();
        private bool _socketsBuilt;
        
        // Cache for socket world positions (invalidated on transform change)
        private Vector3[] _cachedWorldPositions;
        private bool _worldPositionsCacheValid;

        /// Set of socket indices that are currently part of a connection
        private readonly HashSet<int> _connectedSockets = new();

        public IReadOnlyList<SocketInfo> Sockets
        {
            get { if (!_socketsBuilt) BuildSockets(); return sockets; }
        }

        public int SocketCount
        {
            get { if (!_socketsBuilt) BuildSockets(); return sockets.Count; }
        }

        ///
        /// Build sockets along the perimeter of the footprint, in local space,
        /// one socket per 1m edge segment
        /// Order (for compat with Edge/mark API):
        ///   0..(w-1)            : North edge
        ///   w..(2w-1)           : South edge
        ///   2w..(2w+l-1)        : East edge
        ///   2w+l..(2w+2l-1)     : West edge
        ///
        public void BuildSockets()
        {
            var prev = new Dictionary<Vector3, SocketStatus>();
            foreach (var s in sockets)
                prev[s.LocalPos] = s.Status;

            sockets.Clear();
            _socketsBuilt = false;
            
            // Invalidate world position cache when rebuilding sockets
            _worldPositionsCacheValid = false;

            int footprintWidth = Mathf.Max(1, footprintSize.x);
            int footprintLength = Mathf.Max(1, footprintSize.y);
            float halfWidth = footprintWidth * 0.5f;
            float halfLength = footprintLength * 0.5f;

            int socketIndex = 0;

            // North edge (local z ≈ +halfLength), segments along x
            for (int segmentIndex = 0; segmentIndex < footprintWidth; segmentIndex++)
            {
                float localX = -halfWidth + 0.5f + segmentIndex;
                Vector3 localPosition = new Vector3(localX, 0f, +halfLength);
                var socketInfo = new SocketInfo();
                socketInfo.Initialize(socketIndex, localPosition, prev.TryGetValue(localPosition, out var oldStatus) ? oldStatus : SocketStatus.Linkable);
                sockets.Add(socketInfo);
                socketIndex++;
            }

            // South edge (local z ≈ -halfLength)
            for (int segmentIndex = 0; segmentIndex < footprintWidth; segmentIndex++)
            {
                float localX = -halfWidth + 0.5f + segmentIndex;
                Vector3 localPosition = new Vector3(localX, 0f, -halfLength);
                var socketInfo = new SocketInfo();
                socketInfo.Initialize(socketIndex, localPosition, prev.TryGetValue(localPosition, out var oldStatus) ? oldStatus : SocketStatus.Linkable);
                sockets.Add(socketInfo);
                socketIndex++;
            }

            // East edge (local x ≈ +halfWidth), along z
            for (int segmentIndex = 0; segmentIndex < footprintLength; segmentIndex++)
            {
                float localZ = +halfLength - 0.5f - segmentIndex;
                Vector3 localPosition = new Vector3(+halfWidth, 0f, localZ);
                var socketInfo = new SocketInfo();
                socketInfo.Initialize(socketIndex, localPosition, prev.TryGetValue(localPosition, out var oldStatus) ? oldStatus : SocketStatus.Linkable);
                sockets.Add(socketInfo);
                socketIndex++;
            }

            // West edge (local x ≈ -halfWidth)
            for (int segmentIndex = 0; segmentIndex < footprintLength; segmentIndex++)
            {
                float localZ = +halfLength - 0.5f - segmentIndex;
                Vector3 localPosition = new Vector3(-halfWidth, 0f, localZ);
                var socketInfo = new SocketInfo();
                socketInfo.Initialize(socketIndex, localPosition, prev.TryGetValue(localPosition, out var oldStatus) ? oldStatus : SocketStatus.Linkable);
                sockets.Add(socketInfo);
                socketIndex++;
            }

            _socketsBuilt = true;
            
            // Ensure cache is rebuilt on next access
            _worldPositionsCacheValid = false;
        }

        public SocketInfo GetSocket(int index)
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
                Debug.LogWarning($"[GamePlatform] GetSocketWorldPosition: index {index} out of range (0..{sockets.Count - 1}). Returning transform position.", this);
                return transform.position;
            }
            
            // Check if cache is valid
            if (!_worldPositionsCacheValid || _cachedWorldPositions == null || _cachedWorldPositions.Length != sockets.Count)
            {
                // Rebuild cache
                if (_cachedWorldPositions == null || _cachedWorldPositions.Length != sockets.Count)
                    _cachedWorldPositions = new Vector3[sockets.Count];
                
                for (int i = 0; i < sockets.Count; i++)
                    _cachedWorldPositions[i] = transform.TransformPoint(sockets[i].LocalPos);
                
                _worldPositionsCacheValid = true;
            }
            
            return _cachedWorldPositions[index];
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

        public void RegisterModuleOnSockets(GameObject moduleGo, bool occupiesSockets, IEnumerable<int> socketIndices)
        {
            if (!moduleGo) return;
            if (!_socketsBuilt) BuildSockets();

            var list = new List<int>(socketIndices);
            var pm = moduleGo.GetComponent<PlatformModule>();
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
            }
        }

        /// Called by PlatformRailing when disabled/destroyed
        public void UnregisterRailing(PlatformRailing railing)
        {
            if (!railing) return;

            foreach (var kv in _socketToRailings)
            {
                kv.Value.Remove(railing);
            }
        }

        /// True if the given socket index is currently part of a connection
        internal bool IsSocketConnected(int socketIndex) => _connectedSockets.Contains(socketIndex);

        ///
        /// Compute visibility for a single PlatformRailing (rail or post)
        /// Rails: Hidden when ALL their socket indices are Connected
        /// Posts: Hidden when ALL rails connected to the same sockets are hidden
        ///
        internal void UpdateRailingVisibility(PlatformRailing railing)
        {
            if (!railing) return;
            
            var indices = railing.SocketIndices ?? System.Array.Empty<int>();
            if (indices.Length == 0)
            {
                railing.SetHidden(false);
                return;
            }

            // For rails: hide if all sockets are connected
            if (railing.type == PlatformRailing.RailingType.Rail)
            {
                bool allSocketsConnected = true;
                foreach (int socketIndex in indices)
                {
                    if (!_connectedSockets.Contains(socketIndex))
                    {
                        allSocketsConnected = false;
                        break;
                    }
                }
                railing.SetHidden(allSocketsConnected && indices.Length > 0);
                return;
            }

            // For posts: hide if all rails on the same sockets are hidden
            if (railing.type == PlatformRailing.RailingType.Post)
            {
                bool hasVisibleRail = false;
                
                // Check all sockets this post is bound to
                foreach (int socketIndex in indices)
                {
                    // Check if there's any visible rail on this socket
                    if (_socketToRailings.TryGetValue(socketIndex, out var railingsOnSocket))
                    {
                        foreach (var rail in railingsOnSocket)
                        {
                            if (rail == null) continue;
                            if (rail.type != PlatformRailing.RailingType.Rail) continue;
                            
                            // Check if this rail is visible (not all its sockets are connected)
                            var railIndices = rail.SocketIndices ?? System.Array.Empty<int>();
                            bool railIsHidden = true;
                            if (railIndices.Length > 0)
                            {
                                foreach (int railSocketIndex in railIndices)
                                {
                                    if (!_connectedSockets.Contains(railSocketIndex))
                                    {
                                        railIsHidden = false;
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                railIsHidden = false;
                            }
                            
                            if (!railIsHidden)
                            {
                                hasVisibleRail = true;
                                break;
                            }
                        }
                    }
                    
                    if (hasVisibleRail) break;
                }
                
                // Hide post if no visible rails are connected to its sockets
                railing.SetHidden(!hasVisibleRail && indices.Length > 0);
            }
        }

        internal void RefreshAllRailingsVisibility()
        {
            var railings = GetComponentsInChildren<PlatformRailing>(true);
            foreach (var r in railings)
                UpdateRailingVisibility(r);
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
        
        #endregion

        #region Connection Management
        
        public void SetModuleHidden(GameObject moduleGo, bool hidden)
        {
            if (!moduleGo) return;

            var pm = moduleGo.GetComponent<PlatformModule>();
            if (pm != null) pm.SetHidden(hidden);
            else moduleGo.SetActive(!hidden);

            RefreshSocketStatuses();
            QueueRebuild();
        }

        ///
        /// Toggle modules & railings on these sockets and flip sockets to Connected/Linkable
        /// Updates all railings to ensure posts correctly reflect rail visibility changes
        ///
        public void ApplyConnectionVisuals(IEnumerable<int> socketIndices, bool connected)
        {
            var socketSet = new HashSet<int>(socketIndices);
            var modulesToToggle = new HashSet<GameObject>();

            // Update connection state and collect affected modules
            foreach (int socketIndex in socketSet)
            {
                if (connected)
                    _connectedSockets.Add(socketIndex);
                else
                    _connectedSockets.Remove(socketIndex);

                // Collect modules on these sockets
                if (_socketToModules.TryGetValue(socketIndex, out var modules))
                {
                    foreach (var module in modules)
                        modulesToToggle.Add(module);
                }
            }

            // Apply module visibility changes
            foreach (var module in modulesToToggle)
                SetModuleHidden(module, connected);

            // Update all railings to ensure posts correctly reflect rail visibility
            // This ensures cascading updates work correctly (rails -> posts)
            RefreshAllRailingsVisibility();

            RefreshSocketStatuses();
            ConnectionsChanged?.Invoke(this);
        }

        ///
        /// Runtime method to reset all socket connections
        /// Shows all railings and clears connection tracking
        ///
        public void ResetConnections()
        {
            // Show all PlatformModules (railings)
            var allModules = GetComponentsInChildren<PlatformModule>(true);
            foreach (var m in allModules)
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
        ///
        public void EditorResetAllConnections()
        {
            ResetConnections();

#if UNITY_EDITOR
            // Destroy all NavMeshLink GameObjects under "Links" in the editor with Undo
            var linksParent = transform.Find("Links");
            if (linksParent)
            {
                for (int i = linksParent.childCount - 1; i >= 0; i--)
                    UnityEditor.Undo.DestroyObjectImmediate(linksParent.GetChild(i).gameObject);
            }
            
            // Only queue rebuild if we're active (skip during shutdown)
            if (gameObject.activeInHierarchy)
                QueueRebuild();
#endif
        }
        
        #endregion

        #region Lifecycle & Initialization

        private void Awake()
        {
            try
            {
                FindDependencies();
            }
            catch (Exception ex)
            {
                ErrorHandler.LogAndDisable(ex, this);
                return;
            }
            
            if (!_navSurface) _navSurface = GetComponent<NavMeshSurface>();
            InitializePlatform();
        }


        ///
        /// Finds and validates all required manager dependencies
        ///
        private void FindDependencies()
        {
            _platformManager = FindFirstObjectByType<PlatformManager>();
            if (!_platformManager)
            {
                throw ErrorHandler.MissingDependency(typeof(PlatformManager), this);
            }
        }


        private void OnEnable()
        {
            _lastPos = transform.position;
            _lastRot = transform.rotation;
            _lastScale = transform.localScale;
            
            InitializePlatform();
            
            // Fire static event for any listening managers (e.g., PlatformManager)
            PlatformEnabled?.Invoke(this);
        }


        private void OnDisable()
        {
            // Fire static event for any listening managers (e.g., PlatformManager)
            PlatformDisabled?.Invoke(this);
        }


        private void LateUpdate()
        {
            if (transform.position != _lastPos ||
                transform.rotation != _lastRot ||
                transform.localScale != _lastScale)
            {
                _lastPos = transform.position;
                _lastRot = transform.rotation;
                _lastScale = transform.localScale;

                // Invalidate world position cache on transform change
                _worldPositionsCacheValid = false;

                PoseChanged?.Invoke(this);
            }
        }


        private void InitializePlatform()
        {
            // Build sockets once on initialization
            if (!_socketsBuilt) BuildSockets();
            
            // Register child components once on initialization
            EnsureChildrenModulesRegistered();
            EnsureChildrenRailingsRegistered();
            
            // Initial socket status calculation
            RefreshSocketStatuses();
        }


        public void ForcePoseChanged()
        {
            _lastPos = transform.position;
            _lastRot = transform.rotation;
            _lastScale = transform.localScale;
            PoseChanged?.Invoke(this);
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

        public void EnsureChildrenModulesRegistered()
        {
            var modules = GetComponentsInChildren<PlatformModule>(true);
            foreach (var m in modules) m.EnsureRegistered();
        }


        public void EnsureChildrenRailingsRegistered()
        {
            var railings = GetComponentsInChildren<PlatformRailing>(true);
            foreach (var r in railings)
                r.EnsureRegistered();
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
            
            // Disable colliders so we can raycast through the platform
            foreach (var col in GetComponentsInChildren<Collider>(true))
                col.enabled = false;
            
            // Cache renderers and store original materials from all renderers
            _allRenderers.Clear();
            _allRenderers.AddRange(GetComponentsInChildren<Renderer>(true));
            
            // Store all original materials (we'll restore them per-renderer later)
            // Just use the first renderer as reference for now
            if (_allRenderers.Count > 0 && _allRenderers[0] != null)
            {
                _originalMaterials = _allRenderers[0].sharedMaterials;
            }
            
            // Fire pickup event for existing platforms (not for new spawned ones)
            if (!isNewObject)
            {
                PlatformPickedUp?.Invoke(this);
            }
        }


        public void OnPlaced()
        {
            // Restore colliders
            foreach (var col in GetComponentsInChildren<Collider>(true))
                col.enabled = true;
            
            // Restore original materials
            RestoreOriginalMaterials();
            
            // Compute cells for placement
            List<Vector2Int> cells = _platformManager.GetCellsForPlatform(this);
            occupiedCells = cells;
            
            // Set IsPickedUp to false before firing event
            // This ensures managers see the platform in correct state
            IsPickedUp = false;
            
            // Fire event for managers to register platform and trigger adjacency
            PlatformPlaced?.Invoke(this);
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
                
                // Re-enable colliders
                foreach (var col in GetComponentsInChildren<Collider>(true))
                    col.enabled = true;
                
                // Restore original materials
                RestoreOriginalMaterials();
                
                // Compute cells and fire placement event to re-register at original position
                // This triggers adjacency recomputation so railings/NavMesh links update
                List<Vector2Int> cells = _platformManager.GetCellsForPlatform(this);
                occupiedCells = cells;
                
                PlatformPlaced?.Invoke(this);
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
            
            // Compute cells this platform would occupy
            List<Vector2Int> cells = _platformManager.GetCellsForPlatform(this);
            
            if (cells.Count == 0) return false;
            
            // Check if area is free (OccupyPreview doesn't block, only Occupied does)
            return _platformManager.IsAreaFree(cells);
        }
        
        #endregion
    }
}
