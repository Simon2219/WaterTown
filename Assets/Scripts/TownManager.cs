using Grid;
using UnityEngine;
using Platforms;


///
/// High-level town orchestration manager
///
[DisallowMultipleComponent]
public class TownManager : MonoBehaviour
{
    #region Configuration

    [Header("Core Systems")] 
    private WorldGrid _worldGrid;
    private PlatformManager _platformManager;

    [Header("Town-Level Events")] 

    // Cached reference to last placed/removed platform for event handlers/UI
    public GamePlatform LastPlacedPlatform { get; private set; }
    public GamePlatform LastRemovedPlatform { get; private set; }

    #endregion


    #region Lifecycle

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
    /// Injects dependencies between systems to avoid FindFirstObjectByType at runtime
    ///
    private void FindDependencies()
    {
        // Auto-find WorldGrid (once at startup)
        if (!_worldGrid)
        {
            _worldGrid = FindFirstObjectByType<WorldGrid>();
            if (!_worldGrid)
            {
                throw ErrorHandler.MissingDependency(typeof(WorldGrid), this);
            }
        }

        // Auto-find PlatformManager (once at startup)
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
        _platformManager.PlatformPlaced.AddListener(HandlePlatformPlaced);
        _platformManager.PlatformRemoved.AddListener(HandlePlatformRemoved);
    }


    private void OnDisable()
    {
        // Unsubscribe from PlatformManager events
        _platformManager.PlatformPlaced.RemoveListener(HandlePlatformPlaced);
        _platformManager.PlatformRemoved.RemoveListener(HandlePlatformRemoved);
    }

    #endregion


    #region Event Handlers

    private void HandlePlatformPlaced(GamePlatform platform)
    {
        LastPlacedPlatform = platform;
    }


    private void HandlePlatformRemoved(GamePlatform platform)
    {
        LastRemovedPlatform = platform;
    }

    #endregion
    
    
}
    

