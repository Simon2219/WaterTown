using System;
using System.Collections.Generic;
using Grid;
using UnityEngine;
using UnityEngine.Events;
using WaterTown.Platforms;


/// <summary>
/// High-level town orchestration manager.
/// Coordinates specialized subsystems (PlatformManager, etc.) and provides
/// designer-facing events and feedback.
/// </summary>
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
    
    /// <summary>
    /// Finds and validates all required dependencies.
    /// Throws InvalidOperationException if any critical dependency is missing.
    /// </summary>
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
    
    // ---------- Convenience API that delegates to PlatformManager ----------

    /// <summary>
    /// Check if an area is free for building (delegates to PlatformManager).
    /// </summary>
    public bool IsAreaFree(List<Vector2Int> cells)
    {
        return _platformManager.IsAreaFree(cells);
    }
    
    /// <summary>
    /// Register a platform (delegates to PlatformManager).
    /// </summary>
    public void RegisterPlatform(GamePlatform platform, List<Vector2Int> cells, bool markOccupiedInGrid = true)
    {
        _platformManager.RegisterPlatform(platform, cells, markOccupiedInGrid);
    }
    
    /// <summary>
    /// Unregister a platform (delegates to PlatformManager).
    /// </summary>
    public void UnregisterPlatform(GamePlatform platform)
    {
        _platformManager.UnregisterPlatform(platform);
    }
    

    /// <summary>
    /// Trigger adjacency update (delegates to PlatformManager).
    /// </summary>
    public void TriggerAdjacencyUpdate()
    {
        _platformManager.TriggerAdjacencyUpdate();
    }
    
    #endregion
}
