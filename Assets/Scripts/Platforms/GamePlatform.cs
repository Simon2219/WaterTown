using System;
using System.Collections;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using Grid;
using Interfaces;

namespace Platforms
{
    /// <summary>
    /// Core platform component - manages identity, lifecycle, footprint, NavMesh, and coordinates sub-systems
    /// Sub-components handle specific responsibilities: Sockets, Railings, Pickup, Editor utilities
    /// External systems should call facade methods on GamePlatform rather than accessing sub-components directly
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavMeshSurface))]
    [RequireComponent(typeof(PlatformSocketSystem))]
    [RequireComponent(typeof(PlatformRailingSystem))]
    [RequireComponent(typeof(PickupHandler))]
    [RequireComponent(typeof(PlatformEditorUtility))]
    public class GamePlatform : MonoBehaviour, IPickupable
    {
        #region Events
        
        
        /// Fired whenever this platform's connection/railing state changes
        public event Action<GamePlatform> ConnectionsChanged;

        /// Fired whenever this platform moves (position/rotation/scale changes)
        public event Action<GamePlatform> HasMoved;
        
        /// Static event fired when ANY platform is created (for initial discovery)
        public static event Action<GamePlatform> Created;
        
        /// Static event fired when ANY platform is destroyed (for cleanup)
        public static event Action<GamePlatform> Destroyed;
        
        /// Fired when this platform becomes enabled
        public event Action<GamePlatform> Enabled;
        
        /// Fired when this platform becomes disabled
        public event Action<GamePlatform> Disabled;
        
        /// Fired when this platform is placed (after successful placement)
        public event Action<GamePlatform> Placed;
        
        /// Fired when this platform is picked up (before being moved)
        public event Action<GamePlatform> PickedUp;
        
        
        #endregion
        
        
        
        #region Dependencies
        
        
        private PlatformManager _platformManager;
        private WorldGrid _worldGrid;
        
        
        #endregion
        
        
        
        #region Sub-Components
        
        
        private PlatformSocketSystem _socketSystem;
        private PlatformRailingSystem _railingSystem;
        private PickupHandler _pickupHandler;
        private PlatformEditorUtility _editorUtility;
        
        
        #endregion
        
        
        
        #region Platform State
        
        
        public List<Vector2Int> occupiedCells = new();
        public List<Vector2Int> previousOccupiedCells = new();
        
        private readonly List<PlatformModule> _cachedModules = new();
        private readonly List<PlatformRailing> _cachedRailings = new();
        private readonly List<Collider> _cachedColliders = new();
        
        // Read-only access for sub-systems
        public IReadOnlyList<PlatformModule> CachedModules => _cachedModules;
        public IReadOnlyList<PlatformRailing> CachedRailings => _cachedRailings;
        public IReadOnlyList<Collider> CachedColliders => _cachedColliders;
        
        
        #endregion
        
        
        
        #region Footprint & NavMesh
        
        
        [Header("Footprint (cells @ 1m)")]
        [Tooltip("Platform footprint in grid cells. X = width, Y = length.")]
        [SerializeField] private Vector2Int footprintSize = new Vector2Int(4, 4);
        public Vector2Int Footprint => footprintSize;
        
        
        [Header("NavMesh Rebuild")]
        [SerializeField]
        [Tooltip("Delay before rebuilding this platform's NavMesh after changes.")]
        private float rebuildDebounceSeconds = 0.1f;

        
        // Cached transform for "Links" GameObject
        private Transform _linksParentTransform;
        public Transform LinksParentTransform => _linksParentTransform;
        
        private NavMeshSurface _navSurface;
        public NavMeshSurface NavSurface => _navSurface;
        
        
        private Vector3 _lastPos;
        private Quaternion _lastRot;
        private Vector3 _lastScale;

        private Coroutine _pendingRebuild;
        
        
        #endregion
        
        
        
        #region IPickupable Implementation
        
        
        /// Delegate to pickup handler
        public bool IsPickedUp
        {
            get => _pickupHandler.IsPickedUp;
            set => _pickupHandler.IsPickedUp = value;
        }
        
        public bool CanBePlaced => _pickupHandler.CanBePlaced;
        public Transform Transform => transform;
        public GameObject GameObject => gameObject;
        
        public void OnPickedUp(bool isNewObject) => _pickupHandler?.OnPickedUp(isNewObject);
        public void OnPlaced() => _pickupHandler?.OnPlaced();
        public void OnPlacementCancelled() => _pickupHandler?.OnPlacementCancelled();
        public void UpdateValidityVisuals() => _pickupHandler?.UpdateValidityVisuals();
        
        
        #endregion
        
        
        
        #region Unity Lifecycle
        
        
        private void Awake()
        {
            if(!TryGetComponent(out _navSurface))
                throw ErrorHandler.MissingDependency($"[GamePlatform] NavMesh Surface '{nameof(NavMeshSurface)}' not found.", this);
            
            if(!TryGetComponent(out _socketSystem))
                throw ErrorHandler.MissingDependency($"[GamePlatform] Socket System '{nameof(PlatformSocketSystem)}' not found.", this);

            if(!TryGetComponent(out _railingSystem))
                throw ErrorHandler.MissingDependency($"[GamePlatform] Railing System '{nameof(PlatformRailingSystem)}' not found.", this);

            if(!TryGetComponent(out _pickupHandler))
                throw ErrorHandler.MissingDependency($"[GamePlatform] Pickup Handler '{nameof(PickupHandler)}' not found.", this);

            if(!TryGetComponent(out _editorUtility))
                throw ErrorHandler.MissingDependency($"[GamePlatform] Editor Utility '{nameof(PlatformEditorUtility)}' not found.", this);

            
            // Fire static creation event for managers to subscribe
            Created?.Invoke(this);
        }


        private void OnEnable()
        {
            _lastPos = transform.position;
            _lastRot = transform.rotation;
            _lastScale = transform.localScale;
            
            Enabled?.Invoke(this);
        }


        private void OnDisable()
        {
            Disabled?.Invoke(this);
        }


        private void OnDestroy()
        {
            UnsubscribeFromSubComponentEvents();
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

                    HasMoved?.Invoke(this);
                }
            }
        }
        
        
        #endregion
        
        
        
        
        #region Initialization
        
        
        /// Called by PlatformManager to inject itself as a dependency
        public void SetPlatformManager(PlatformManager platformManager)
        {
            _platformManager = platformManager;
        }


        public void SetWorldGrid(WorldGrid worldGrid)
        {
            _worldGrid = worldGrid;
        }
        
        
        


        public void InitializePlatform()
        {
            // Cache all child components at initialization
            CacheChildComponents();
            
            // Initialize Sub Systems
            InitializeSubComponents();
            
            // Ensure Links parent exists for NavMesh links
            EnsureLinksParentExists();
        }
        
        
        
        /// Called after all dependencies are set to initialize sub-components
        /// This ensures proper initialization order
        private void InitializeSubComponents()
        {
            // Initialize sub-components with dependencies
            _socketSystem?.SetDependencies(this, _platformManager, _worldGrid);
            _pickupHandler?.SetDependencies(this, _platformManager);
            _railingSystem?.SetDependencies(this, _socketSystem);
            _editorUtility?.SetDependencies(this, _socketSystem);
            
            // Subscribe to sub-component events (only once)
            SubscribeToSubComponentEvents();
            
            // Build sockets and register children
            _socketSystem?.BuildSockets();
            _socketSystem?.EnsureChildrenModulesRegistered();
            _railingSystem?.EnsureChildrenRailingsRegistered();
            _socketSystem?.RefreshSocketStatuses();
        }
        
        
        
        private void SubscribeToSubComponentEvents()
        {
            _socketSystem.SocketsChanged += OnSocketsChanged;
            _socketSystem.NewNeighborDetected += OnNewNeighborDetected;
            
            _pickupHandler.Placed += OnPickupHandlerPlaced;
            _pickupHandler.PickedUp += OnPickupHandlerPickedUp;
        }
        
        
        private void UnsubscribeFromSubComponentEvents()
        {
            _socketSystem.SocketsChanged -= OnSocketsChanged;
            _socketSystem.NewNeighborDetected -= OnNewNeighborDetected;
            
            _pickupHandler.Placed -= OnPickupHandlerPlaced;
            _pickupHandler.PickedUp -= OnPickupHandlerPickedUp;
        }


        private void EnsureLinksParentExists()
        {
            if (_linksParentTransform) return;
            
            _linksParentTransform = transform.Find("Links");
            
            if (!_linksParentTransform)
            {
                var go = new GameObject("Links");
                _linksParentTransform = go.transform;
                _linksParentTransform.SetParent(transform, false);
                _linksParentTransform.localPosition = Vector3.zero;
                _linksParentTransform.localRotation = Quaternion.identity;
                _linksParentTransform.localScale = Vector3.one;
            }
        }


        private void CacheChildComponents()
        {
            _cachedModules.Clear();
            _cachedRailings.Clear();
            _cachedColliders.Clear();
            
            _cachedModules.AddRange(GetComponentsInChildren<PlatformModule>(true));
            _cachedRailings.AddRange(GetComponentsInChildren<PlatformRailing>(true));
            _cachedColliders.AddRange(GetComponentsInChildren<Collider>(true));
        }
        
        
        #endregion
        
        
        
        #region Event Handlers
        
        
        private void OnSocketsChanged()
        {
            // Refresh railing visibility when socket connections change
            _railingSystem?.RefreshAllRailingsVisibility();
            
            ConnectionsChanged?.Invoke(this);
        }


        private void OnNewNeighborDetected(GamePlatform neighbor)
        {
            // Request NavMesh link creation from PlatformManager
            _platformManager?.RequestNavMeshLink(this, neighbor);
        }


        private void OnPickupHandlerPlaced(GamePlatform platform)
        {
            Placed?.Invoke(this);
        }


        private void OnPickupHandlerPickedUp(GamePlatform platform)
        {
            PickedUp?.Invoke(this);
        }
        
        
        #endregion
        
        
        
        #region Socket Interface Methods & Type Aliases
        
        
        // Type aliases for external compatibility - maps to PlatformSocketSystem enums
        public enum Edge { North = 0, East = 1, South = 2, West = 3 }
        public enum SocketStatus { Linkable = 0, Occupied = 1, Connected = 2, Locked = 3, Disabled = 4 }
        public enum SocketLocation { Edge = 0, Corner = 1 }
        
        /// Access to socket system (read-only list)
        public IReadOnlyList<PlatformSocketSystem.SocketData> Sockets => _socketSystem?.Sockets;
        
        public int SocketCount => _socketSystem?.SocketCount ?? 0;
        
        public IReadOnlyCollection<int> ConnectedSockets => _socketSystem?.ConnectedSockets;


        public PlatformSocketSystem.SocketData GetSocket(int index) 
            => _socketSystem?.GetSocket(index) ?? default;


        public Vector3 GetSocketWorldPosition(int index) 
            => _socketSystem?.GetSocketWorldPosition(index) ?? transform.position;


        public void SetSocketStatus(int index, SocketStatus status) 
            => _socketSystem?.SetSocketStatus(index, (PlatformSocketSystem.SocketStatus)(int)status);


        public bool IsSocketConnected(int socketIndex) 
            => _socketSystem?.IsSocketConnected(socketIndex) ?? false;


        public int EdgeLengthMeters(Edge edge) 
            => _socketSystem?.EdgeLengthMeters((PlatformSocketSystem.Edge)(int)edge) ?? 0;


        public void GetSocketIndexRangeForEdge(Edge edge, out int startIndex, out int endIndex)
        {
            if (_socketSystem)
                _socketSystem.GetSocketIndexRangeForEdge((PlatformSocketSystem.Edge)(int)edge, out startIndex, out endIndex);
            else
            {
                startIndex = 0;
                endIndex = 0;
            }
        }


        public int GetSocketIndexByEdgeMark(Edge edge, int mark) 
            => _socketSystem?.GetSocketIndexByEdgeMark((PlatformSocketSystem.Edge)(int)edge, mark) ?? 0;


        public int FindNearestSocketIndexLocal(Vector3 localPos) 
            => _socketSystem?.FindNearestSocketIndexLocal(localPos) ?? -1;


        public void FindNearestSocketIndicesLocal(Vector3 localPos, int maxCount, float maxDistance, List<int> result)
            => _socketSystem?.FindNearestSocketIndicesLocal(localPos, maxCount, maxDistance, result);


        public int FindNearestSocketIndexWorld(Vector3 worldPos) 
            => _socketSystem?.FindNearestSocketIndexWorld(worldPos) ?? -1;


        public Vector3 GetSocketWorldOutwardDirection(int socketIndex) 
            => _socketSystem?.GetSocketWorldOutwardDirection(socketIndex) ?? Vector3.zero;


        public Vector2Int GetAdjacentCellForSocket(int socketIndex) 
            => _socketSystem?.GetAdjacentCellForSocket(socketIndex) ?? Vector2Int.zero;


        public void UpdateSocketStatusesFromGrid() 
            => _socketSystem?.UpdateSocketStatusesFromGrid();


        public List<int> GetSocketsConnectedToNeighbor(GamePlatform neighbor) 
            => _socketSystem?.GetSocketsConnectedToNeighbor(neighbor) ?? new List<int>();


        public void UpdateConnections(HashSet<int> newConnectedSockets) 
            => _socketSystem?.UpdateConnections(newConnectedSockets);


        public void ResetConnections() 
            => _socketSystem?.ResetConnections();


        public void RefreshSocketStatuses() 
            => _socketSystem?.RefreshSocketStatuses();


        public void BuildSockets() 
            => _socketSystem?.BuildSockets();
        
        
        #endregion
        
        
        
        #region Module Interface Methods
        
        
        public void RegisterModuleOnSockets(GameObject moduleGo, bool occupiesSockets, IEnumerable<int> socketIndices) 
            => _socketSystem?.RegisterModuleOnSockets(moduleGo, occupiesSockets, socketIndices);


        public void UnregisterModule(GameObject moduleGo) 
            => _socketSystem?.UnregisterModule(moduleGo);


        public void SetModuleHidden(GameObject moduleGo, bool hidden) 
            => _socketSystem?.SetModuleHidden(moduleGo, hidden);


        public void EnsureChildrenModulesRegistered() 
            => _socketSystem?.EnsureChildrenModulesRegistered();
        
        
        #endregion
        
        
        
        #region Railing Interface Methods
        
        
        public void RegisterRailing(PlatformRailing railing) 
            => _railingSystem?.RegisterRailing(railing);


        public void UnregisterRailing(PlatformRailing railing) 
            => _railingSystem?.UnregisterRailing(railing);


        public bool HasVisibleRailOnSockets(int[] socketIndices) 
            => _railingSystem?.HasVisibleRailOnSockets(socketIndices) ?? false;


        public void RefreshAllRailingsVisibility() 
            => _railingSystem?.RefreshAllRailingsVisibility();


        public void EnsureChildrenRailingsRegistered() 
            => _railingSystem?.EnsureChildrenRailingsRegistered();
        
        
        #endregion
        
        
        
        #region NavMesh Interface Methods
        
        
        public void BuildLocalNavMesh()
        {
            if (NavSurface) NavSurface.BuildNavMesh();
        }


        public void QueueRebuild()
        {
            if (!NavSurface) return;
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
        
        
        
        #region Utility Methods
        
        
        public void ForceHasMoved()
        {
            _lastPos = transform.position;
            _lastRot = transform.rotation;
            _lastScale = transform.localScale;
            HasMoved?.Invoke(this);
        }
        
        
#if UNITY_EDITOR
        public void EditorResetAllConnections() 
            => _editorUtility?.EditorResetAllConnections();
#endif
        
        
        #endregion
    }
}
