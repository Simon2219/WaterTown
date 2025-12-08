using System;
using System.Collections.Generic;
using Grid;
using UnityEngine;
using UnityEngine.Events;
using WaterTown.Platforms;


///
/// High-level town orchestration manager
/// Coordinates specialized subsystems (PlatformManager, etc) and provides
/// designer-facing events and feedback
///
[DisallowMultipleComponent]
public class TownManager : MonoBehaviour
{
    #region Configuration & Dependencies

    [Header("Core Systems")]
    
    private WorldGrid _worldGrid;
    private PlatformManager _platformManager;

    [Header("Town-Level Events")]
    
    [Tooltip("Invoked when a platform is successfully placed (designer-facing).")]
    public UnityEvent OnPlatformPlaced;
    
    [Tooltip("Invoked when a platform is removed (designer-facing).")]
    public UnityEvent OnPlatformRemoved;

    // Cached reference to last placed/removed platform for event handlers/UI
    public GamePlatform LastPlacedPlatform { get; private set; }
    public GamePlatform LastRemovedPlatform { get; private set; }
    
    #endregion

    #region Unity Lifecycle

    private void Awake()
    {
        try
        {
            FindDependencies();
        }
        catch (MissingReferenceException ex)
        {
            ErrorHandler.LogAndDisable(ex, this);
        }
    }


    ///
    /// Finds and validates all required dependencies
    /// Throws InvalidOperationException if any critical dependency is missing
    ///
    private void FindDependencies() 
    {
        // Auto-find WorldGrid
        if (!_worldGrid)
        {
            _worldGrid = FindFirstObjectByType<WorldGrid>();
            if (!_worldGrid)
            {
                throw ErrorHandler.MissingDependency(typeof(WorldGrid), this);
            }
        }

        // Auto-find PlatformManager
        if (!_platformManager)
        {
            _platformManager = FindFirstObjectByType<PlatformManager>();
            if (!_platformManager)
            {
                throw ErrorHandler.MissingDependency(typeof(PlatformManager), this);
            }
        }
    }


    private void OnEnable()
    {
        // Subscribe to PlatformManager events to propagate town-level feedback
        _platformManager.OnPlatformPlaced.AddListener(HandlePlatformPlaced);
        _platformManager.OnPlatformRemoved.AddListener(HandlePlatformRemoved);
    }


    private void OnDisable()
    {
        // Unsubscribe from PlatformManager events
        _platformManager.OnPlatformPlaced.RemoveListener(HandlePlatformPlaced);
        _platformManager.OnPlatformRemoved.RemoveListener(HandlePlatformRemoved);
    }
    
    #endregion

    #region Event Handlers

    private void HandlePlatformPlaced(GamePlatform platform)
    {
        LastPlacedPlatform = platform;
        OnPlatformPlaced?.Invoke();
    }


    private void HandlePlatformRemoved(GamePlatform platform)
    {
        LastRemovedPlatform = platform;
        OnPlatformRemoved?.Invoke();
    }
    
    #endregion

    #region Public API (Delegation to Subsystems)

    // Convenience API that delegates to PlatformManager

    ///
    /// Check if an area is free for building (delegates to PlatformManager)
    ///
    public bool IsAreaFree(List<Vector2Int> cells, GamePlatform ignorePlatform = null)
    {
        return _platformManager.IsAreaFree(cells, ignorePlatform);
    }


    ///
    /// Register a platform (delegates to PlatformManager)
    ///
    public void RegisterPlatform(GamePlatform platform)
    {
        _platformManager.RegisterPlatform(platform);
    }


    public void RegisterPlatformOnArea(GamePlatform platform, List<Vector2Int> occupiedCells)
    {
        platform.occupiedCells = occupiedCells;
        _platformManager.RegisterPlatform(platform);
    }


    ///
    /// Unregister a platform (delegates to PlatformManager)
    ///
    public void UnregisterPlatform(GamePlatform platform)
    {
        _platformManager.UnregisterPlatform(platform);
    }


    ///
    /// Trigger adjacency update (delegates to PlatformManager)
    ///
    public void TriggerAdjacencyUpdate()
    {
        _platformManager.TriggerAdjacencyUpdate();
    }
    
    
    #endregion
}
