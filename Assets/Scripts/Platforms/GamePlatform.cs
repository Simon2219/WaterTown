using System;
using System.Collections.Generic;
using UnityEngine;
using Grid;
using Interfaces;

namespace Platforms
{

/// Core platform component
/// Manages identity, lifecycle, footprint, and coordinates sub-systems
/// Sub-components handle specific responsibilities: Sockets, Railings, Pickup, Editor utilities

/// EXECUTION ORDER: This runs before PlatformManager to ensure Platform Updates finished

[DisallowMultipleComponent]
[DefaultExecutionOrder(-10)] // Run before PlatformManager (which is at 0)

[RequireComponent(typeof(PlatformSocketSystem))]
[RequireComponent(typeof(PlatformRailingSystem))]
[RequireComponent(typeof(PickupHandler))]
[RequireComponent(typeof(PlatformEditorUtility))]


public class GamePlatform : MonoBehaviour, IPickupable
{
    #region Configuration
    
    
    [Header("Footprint (cells @ 1m)")]
    [Tooltip("Platform footprint in grid cells. X = width, Y = length.")]
    
    [SerializeField] private Vector2Int footprintSize = new Vector2Int(4, 4);
    public Vector2Int Footprint => footprintSize;
    
    
    private PlatformManager _platformManager;
    private WorldGrid _worldGrid;
    
    
    private PlatformSocketSystem _socketSystem;
    private PlatformRailingSystem _railingSystem;
    private PickupHandler _pickupHandler;
    private PlatformEditorUtility _editorUtility;
    
    
    public List<Vector2Int> occupiedCells = new();
    public List<Vector2Int> previousOccupiedCells = new();
    
    private readonly List<PlatformModule> _platformModules = new();
    private readonly List<PlatformRailing> _platformRailings = new();
    private readonly List<Collider> _platformColliders = new();
    
    // Read-only access for sub-systems
    public IReadOnlyList<PlatformModule> PlatformModules => _platformModules;
    public IReadOnlyList<PlatformRailing> PlatformRailings => _platformRailings;
    public IReadOnlyList<Collider> PlatformColliders => _platformColliders;
    
    
    private Vector3 _lastPos;
    private Quaternion _lastRot;
    private Vector3 _lastScale;
    
    
    #endregion
    
    
    #region Events
    
    
    public event Action<GamePlatform> ConnectionsChanged; // connection/railing state changes
    
    public event Action<GamePlatform> HasMoved; // (position/rotation/scale changes)
    
    public static event Action<GamePlatform> Created; // When ANY platform is created (for initial discovery)
    
    public static event Action<GamePlatform> Destroyed; // When ANY platform is destroyed (for cleanup)
    
    public event Action<GamePlatform> Enabled; // OnEnable
    
    public event Action<GamePlatform> Disabled; // OnDisable
    
    public event Action<GamePlatform> Placed; // When this platform is placed ( successful )
    
    public event Action<GamePlatform> PickedUp; // When this platform is picked up (before being moved)
    
    public event Action<GamePlatform> PlacementCancelled; // When placement is cancelled
    
    
    #endregion
    
    
    #region Initialization


    public void InitializePlatform(PlatformManager platformManager, WorldGrid worldGrid)
    {
        _platformManager = platformManager;
        _worldGrid = worldGrid;
        
        // Cache all child components at initialization
        CacheChildComponents();
        
        // Initialize Sub Systems
        InitializeSubComponents();
    }
    
    
    
    /// Called after all dependencies are set to initialize sub-components
    /// This ensures proper initialization order
    private void InitializeSubComponents()
    {
        // Initialize sub-components with dependencies
        _socketSystem?.Initialize(this, _platformManager, _worldGrid);
        _pickupHandler?.Initialize(this);
        _railingSystem?.Initialize(this, _socketSystem);
        _editorUtility?.Initialize(this, _socketSystem);
        
        // Subscribe to sub-component events (only once)
        SubscribeToSubComponentEvents();
    }
    
    
    
    private void CacheChildComponents()
    {
        _platformModules.Clear();
        _platformRailings.Clear();
        _platformColliders.Clear();
        
        _platformModules.AddRange(GetComponentsInChildren<PlatformModule>(true));
        _platformRailings.AddRange(GetComponentsInChildren<PlatformRailing>(true));
        _platformColliders.AddRange(GetComponentsInChildren<Collider>(true));
    }
    
    
    
    private void SubscribeToSubComponentEvents()
    {
        _socketSystem.SocketsChanged += OnSocketsChanged;
    }
    
    
    private void UnsubscribeFromSubComponentEvents()
    {
        _socketSystem.SocketsChanged -= OnSocketsChanged;
    }
    
    
    #endregion
    
    
    #region Lifecycle
    
    
    private void Awake()
    {
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
        if(CheckPlatformMoved()) HasMoved?.Invoke(this);
    }
    
    
    #endregion
    
    
    #region IPickupable Implementation

    public bool IsPickedUp { get; private set; }

    public bool IsNewObject { get; private set; }

    public bool CanBePlaced => ValidatePlacement();
    public Transform Transform => transform;
    public GameObject GameObject => gameObject;
    
    
    
    /// Initiates pickup
    public void PickUp(bool isNewObject)
    {
        IsNewObject = isNewObject;
        IsPickedUp = true;
        
        PickedUp?.Invoke(this);
    }
    
    
    
    /// Confirms Placement
    public void Place()
    {
        IsPickedUp = false;
        
        occupiedCells = _platformManager.GetCellsForPlatform(this);
        
        Placed?.Invoke(this);
    }
    
    
    
    /// Cancels placement
    /// For new objects: fires PlacementCancelled (handler destroys)
    /// For existing objects: fires PlacementCancelled (handler restores position), then Place() is called
    public void CancelPlacement()
    {
        IsPickedUp = false;
        
        // PickupHandler restores position (existing) or marks for destroy (new)
        PlacementCancelled?.Invoke(this);
    }
    
    
    
    /// Visual feedback during pickup - delegates to handler
    public void UpdateValidityVisuals() => _pickupHandler?.UpdateValidityVisuals();
    
    
    #endregion
    
    
    #region Event Handlers
    
    
    private void OnSocketsChanged()
    {
        // Refresh railing visibility when socket connections change
        _railingSystem?.RefreshAllRailingsVisibility();
        
        ConnectionsChanged?.Invoke(this);
    }
    
    
    #endregion
    
    
    #region Socket Interface Methods & Type Aliases
    
    
    // Socket status enum for external compatibility
    public enum SocketStatus { Linkable = 0, Occupied = 1, Connected = 2, Locked = 3, Disabled = 4 }
    
    /// Access to socket system (read-only list)
    public IReadOnlyList<PlatformSocketSystem.SocketData> Sockets => _socketSystem?.PlatformSockets;
    
    public int SocketCount => _socketSystem?.SocketCount ?? 0;


    public PlatformSocketSystem.SocketData GetSocket(int index) 
        => _socketSystem?.GetSocket(index) ?? default;


    public Vector3 GetSocketWorldPosition(int index) 
        => _socketSystem?.GetSocketWorldPosition(index) ?? transform.position;


    public void SetSocketStatus(int index, SocketStatus status) 
        => _socketSystem?.SetSocketStatus(index, (PlatformSocketSystem.SocketStatus)(int)status);


    public bool IsSocketConnected(int socketIndex) 
        => _socketSystem?.IsSocketConnected(socketIndex) ?? false;


    public int GetNearestSocketIndex(Vector3 worldPos) 
        => _socketSystem?.GetNearestSocketIndex(worldPos) ?? -1;


    public List<int> GetNearestSocketIndices(Vector3 worldPos, int maxCount, float maxDistance)
        => _socketSystem?.GetNearestSocketIndices(worldPos, maxCount, maxDistance) ?? new List<int>();


    public Vector3 GetSocketWorldOutwardDirection(int socketIndex) 
        => _socketSystem?.GetSocketWorldOutwardDirection(socketIndex) ?? Vector3.zero;


    public Vector2Int GetAdjacentCellForSocket(int socketIndex) 
        => _socketSystem?.GetAdjacentCellForSocket(socketIndex) ?? Vector2Int.zero;


    public List<int> GetSocketsConnectedToNeighbor(GamePlatform neighbor) 
        => _socketSystem?.GetSocketsConnectedToNeighbor(neighbor) ?? new List<int>();


    public void ResetConnections() 
        => _socketSystem?.ResetConnections();


    public void RefreshSocketStatuses() 
        => _socketSystem?.RefreshAllSocketStatuses();


    public void BuildSockets() 
        => _socketSystem?.ReBuildSockets();
    
    
    #endregion
    
    
    #region Module Interface Methods
    
    
    public void RegisterModuleOnSockets(PlatformModule module, bool occupiesSockets, IEnumerable<int> socketIndices) 
        => _socketSystem?.RegisterModuleOnSockets(module, occupiesSockets, socketIndices);


    public void UnregisterModule(PlatformModule module) 
        => _socketSystem?.UnregisterModule(module);


    public void SetModuleHidden(PlatformModule module, bool hidden) 
        => _socketSystem?.SetModuleHidden(module, hidden);


    public void EnsureChildrenModulesRegistered() 
        => _socketSystem?.EnsureChildrenModulesRegistered();
    
    
    #endregion
    
    
    #region Utility Methods

    private bool CheckPlatformMoved()
    {
        if (transform.position != _lastPos ||
            transform.rotation != _lastRot ||
            transform.localScale != _lastScale)
        {
            _lastPos = transform.position;
            _lastRot = transform.rotation;
            _lastScale = transform.localScale;
            
            return true;
        }
        return false;
    }
    
    
    
    public void ForceHasMoved()
    {
        _lastPos = transform.position;
        _lastRot = transform.rotation;
        _lastScale = transform.localScale;
        HasMoved?.Invoke(this);
    }
    
    
    
    private bool ValidatePlacement()
    {
        List<Vector2Int> cells = _platformManager.GetCellsForPlatform(this);
            
        return cells.Count != 0 && _platformManager.IsAreaEmpty(cells);
    }



    internal Vector2Int Editor_GetFootprint()
    {
        return _editorUtility.Editor_GetFootprint();
    }
    #endregion
}
}
