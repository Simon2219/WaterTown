using Grid;
using UnityEngine;
using UnityEngine.Events;
using WaterTown.Platforms;

namespace WaterTown.Town
{
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
        [SerializeField] private WorldGrid grid;
        [SerializeField] private PlatformManager platformManager;

        public WorldGrid Grid => grid;
        public PlatformManager PlatformManager => platformManager;

        [Header("Town-Level Events")]
        [Tooltip("Invoked when a platform is successfully placed (designer-facing).")]
        public UnityEvent OnPlatformPlaced = new UnityEvent();
        
        [Tooltip("Invoked when a platform is removed (designer-facing).")]
        public UnityEvent OnPlatformRemoved = new UnityEvent();
        
        // Cached reference to last placed/removed platform for event handlers/UI
        public GamePlatform LastPlacedPlatform { get; private set; }
        public GamePlatform LastRemovedPlatform { get; private set; }
        
        #endregion

        #region Unity Lifecycle
        
        private void Awake()
        {
            // Auto-find core systems if not wired in inspector
            if (!grid)
            {
                grid = FindFirstObjectByType<WorldGrid>();
                if (!grid)
                {
                    Debug.LogError("[TownManager] WorldGrid not found. Component disabled.", this);
                    enabled = false;
                    return;
                }
            }

            if (!platformManager)
            {
                platformManager = FindFirstObjectByType<PlatformManager>();
                if (!platformManager)
                {
                    Debug.LogError("[TownManager] PlatformManager not found. Component disabled.", this);
                    enabled = false;
                    return;
                }
            }
        }

        private void OnEnable()
        {
            // Subscribe to PlatformManager events to propagate town-level feedback
            if (platformManager != null)
            {
                platformManager.OnPlatformPlaced.AddListener(HandlePlatformPlaced);
                platformManager.OnPlatformRemoved.AddListener(HandlePlatformRemoved);
            }
        }

        private void OnDisable()
        {
            // Unsubscribe from PlatformManager events
            if (platformManager != null)
            {
                platformManager.OnPlatformPlaced.RemoveListener(HandlePlatformPlaced);
                platformManager.OnPlatformRemoved.RemoveListener(HandlePlatformRemoved);
            }
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
        public bool IsAreaFree(System.Collections.Generic.List<Vector2Int> cells, int level = 0)
        {
            return platformManager ? platformManager.IsAreaFree(cells, level) : false;
        }

        /// <summary>
        /// Register a platform (delegates to PlatformManager).
        /// </summary>
        public void RegisterPlatform(
            GamePlatform platform,
            System.Collections.Generic.List<Vector2Int> cells,
            int level = 0,
            bool markOccupiedInGrid = true)
        {
            platformManager?.RegisterPlatform(platform, cells, level, markOccupiedInGrid);
        }

        /// <summary>
        /// Unregister a platform (delegates to PlatformManager).
        /// </summary>
        public void UnregisterPlatform(GamePlatform platform)
        {
            platformManager?.UnregisterPlatform(platform);
        }

        /// <summary>
        /// Compute cells for a platform (delegates to PlatformManager).
        /// </summary>
        public void ComputeCellsForPlatform(GamePlatform platform, int level, System.Collections.Generic.List<Vector2Int> outputCells)
        {
            platformManager?.ComputeCellsForPlatform(platform, level, outputCells);
        }

        /// <summary>
        /// Trigger adjacency update (delegates to PlatformManager).
        /// </summary>
        public void TriggerAdjacencyUpdate()
        {
            platformManager?.TriggerAdjacencyUpdate();
        }

        /// <summary>
        /// Check if two platforms are adjacent and connect them (delegates to PlatformManager).
        /// Used by editor tools.
        /// </summary>
        public void ConnectPlatformsIfAdjacent(GamePlatform platformA, GamePlatform platformB)
        {
            platformManager?.ConnectPlatformsIfAdjacent(platformA, platformB);
        }
        
        #endregion
    }
}
