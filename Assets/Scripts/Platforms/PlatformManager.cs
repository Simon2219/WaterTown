using System.Collections.Generic;
using System.Linq;
using Grid;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;

namespace Platforms
{


///
/// Manages all platform-specific logic: registration, adjacency, socket connections, and NavMesh links
/// Separated from TownManager to isolate platform concerns from town-level orchestration
///
[DisallowMultipleComponent]
public class PlatformManager : MonoBehaviour
{
    #region Inspector Fields


    [Header("Events")] [Tooltip("Invoked when a platform is successfully placed and registered.")]
    public UnityEvent<GamePlatform> PlatformPlaced;

    [FormerlySerializedAs("OnPlatformRemoved")] [Tooltip("Invoked when a platform is removed/unregistered.")]
    public UnityEvent<GamePlatform> PlatformRemoved;


    #endregion




    #region Configuration & Constants


    private WorldGrid _worldGrid;

    private const int ROTATION_STEP_DEGREES = 90;
    private const int ROTATION_MODULO_MASK = 3; // 0-3 for 4 cardinal directions (0°, 90°, 180°, 270°)


    /// All currently registered platforms (uses platform's own occupiedCells field for data)
    private readonly HashSet<GamePlatform> _registeredPlatforms = new();

    /// Read-only access to all registered platforms
    public IReadOnlyCollection<GamePlatform> AllPlatforms => _registeredPlatforms;

    /// Reverse lookup: which platform occupies a given cell
    private readonly Dictionary<Vector2Int, GamePlatform> _cellToPlatform = new();

    // Adjacency recomputation batching (performance optimization)
    // Instead of a boolean, track specific platforms that need updates
    private readonly HashSet<GamePlatform> _platformsNeedingAdjacencyUpdate = new();

    private bool _isRecomputingAdjacency = false;

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




    /// Finds and validates all required dependencies
    /// Throws InvalidOperationException if any critical dependency is missing
    /// Note: WorldGrid should be injected via SetWorldGrid before Start
    ///
    private void FindDependencies()
    {
        // WorldGrid should be injected or found once - prefer dependency injection
        if (!_worldGrid)
        {
            // Fallback: find once at startup if not injected
            _worldGrid = FindFirstObjectByType<WorldGrid>();
            if (!_worldGrid)
            {
                throw ErrorHandler.MissingDependency(typeof(WorldGrid), this);
            }
        }
    }



    /// Dependency injection method for WorldGrid (avoids FindFirstObjectByType)
    public void SetWorldGrid(WorldGrid worldGrid)
    {
        _worldGrid = worldGrid;
    }



    private void OnEnable()
    {
        // Subscribe to static platform creation/destruction events (for discovery and cleanup)
        GamePlatform.Created += OnPlatformCreated;
        GamePlatform.Destroyed += OnPlatformDestroyed;
    }



    private void Start()
    {
        SpawnStartupPlatforms();
    }



    private void LateUpdate()
    {
        // Batch adjacency recomputation to once per frame for affected platforms only
        if (_platformsNeedingAdjacencyUpdate.Count > 0 && !_isRecomputingAdjacency)
        {
            RecomputeAdjacencyForAffectedPlatforms();
        }
    }



    private void OnDisable()
    {
        // Unsubscribe from static platform creation/destruction events
        GamePlatform.Created -= OnPlatformCreated;
        GamePlatform.Destroyed -= OnPlatformDestroyed;

        // Best-effort cleanup of instance event subscriptions
        foreach (GamePlatform platform in _registeredPlatforms)
        {
            if (platform)
            {
                platform.HasMoved -= OnPlatformHasMoved;
                platform.Enabled -= OnPlatformEnabled;
                platform.Disabled -= OnPlatformDisabled;
                platform.Placed -= OnPlatformPlaced;
                platform.PickedUp -= OnPlatformPickedUp;
            }
        }
    }

    #endregion

    #region Platform Lifecycle Event Handlers

    private void SpawnStartupPlatforms()
    {
        // Later implementation for an Asset defining Starting Setups for the Town

        // Ensure all existing platforms in the scene are registered into the grid
        foreach (GamePlatform platform in _registeredPlatforms)
        {
            if (!platform) continue;
            if (!platform.isActiveAndEnabled) continue;

            List<Vector2Int> tmpCells = GetCellsForPlatform(platform);
            platform.occupiedCells = tmpCells;


            if (tmpCells.Count > 0)
            {
                // Registers occupancy AND triggers adjacency for all platforms
                RegisterPlatform(platform);
            }
        }
    }




    /// Event handler for static Created event
    /// Subscribes to all instance events for this platform and injects dependencies
    ///
    private void OnPlatformCreated(GamePlatform platform)
    {
        if (!platform) return;

        // Inject dependencies
        platform.SetPlatformManager(this);
        platform.SetWorldGrid(_worldGrid);

        // Initialize sub-components AFTER dependencies are set
        platform.InitializePlatform();

        // Subscribe to all instance events for this platform
        platform.HasMoved += OnPlatformHasMoved;
        platform.Enabled += OnPlatformEnabled;
        platform.Disabled += OnPlatformDisabled;
        platform.Placed += OnPlatformPlaced;
        platform.PickedUp += OnPlatformPickedUp;

        // Add to registry immediately
        _registeredPlatforms.Add(platform);
    }



    /// Event handler for static Destroyed event
    /// Unsubscribes from all instance events and cleans up
    ///
    private void OnPlatformDestroyed(GamePlatform platform)
    {
        if (!platform) return;

        // Unsubscribe from instance events
        platform.HasMoved -= OnPlatformHasMoved;
        platform.Enabled -= OnPlatformEnabled;
        platform.Disabled -= OnPlatformDisabled;
        platform.Placed -= OnPlatformPlaced;
        platform.PickedUp -= OnPlatformPickedUp;

        UnregisterPlatform(platform);
    }



    /// Instance event handler when a platform becomes enabled
    /// No additional action needed (platform already in registry from Created event)
    ///
    private void OnPlatformEnabled(GamePlatform platform)
    {
        // Platform was already added to registry in OnPlatformCreated
        // This event can be used for future per-enable logic if needed
    }



    /// Instance event handler when a platform becomes disabled
    /// Removes the platform from the grid
    ///
    private void OnPlatformDisabled(GamePlatform platform)
    {
        UnregisterPlatform(platform);
    }



    /// Event handler called when ANY platform is placed
    /// Registers platform in grid, triggers adjacency update, rebuilds NavMesh, and creates links
    ///
    private void OnPlatformPlaced(GamePlatform platform)
    {
        if (!platform) return;

        // Register platform in grid
        RegisterPlatform(platform);

        // Mark this platform and its neighbors for adjacency update
        MarkAdjacencyDirtyForPlatform(platform);

        // Force immediate adjacency computation so connections are known
        RecomputeAdjacencyForAffectedPlatforms();

        // Create NavMesh links for this platform to all its neighbors
        var linkManager = NavMeshLinkManager.Instance;
        if (linkManager)
        {
            linkManager.CreateLinksForPlacedPlatform(platform, this);
        }

        // Rebuild NavMesh for this platform and all affected neighbors
        RebuildNavMeshForPlatformAndNeighbors(platform);
    }



    /// Event handler called when ANY platform is picked up
    /// Clears Occupied flags, cell ownership, NavMesh links, and marks adjacency dirty
    ///
    private void OnPlatformPickedUp(GamePlatform platform)
    {
        if (!platform) return;

        // Clear all NavMesh links for this platform
        NavMeshLinkManager.Instance?.ClearAllLinksForPlatform(platform);

        // Store current cells as previous BEFORE clearing (needed for GetAffectedPlatforms)
        platform.previousOccupiedCells.Clear();
        platform.previousOccupiedCells.AddRange(platform.occupiedCells);

        // Clear Occupied flags and cell ownership for cells this platform was occupying
        if (platform.occupiedCells != null)
        {
            foreach (Vector2Int cell in platform.occupiedCells)
            {
                if (_cellToPlatform.TryGetValue(cell, out var owner) && owner == platform)
                {
                    _worldGrid.TrySetCellFlag(cell, WorldGrid.CellFlag.Empty);
                    _cellToPlatform.Remove(cell);
                }
            }

            platform.occupiedCells.Clear();
        }

        // Mark affected platforms (neighbors at old position) for adjacency update
        MarkAdjacencyDirtyForPlatform(platform);
    }




    /// Called when a platform reports its transform changed
    /// Lightweight update for runtime platform movement
    ///
    private void OnPlatformHasMoved(GamePlatform platform)
    {
        // Store current cells as previous for the next update
        // On first move after pickup, occupiedCells is empty but previousOccupiedCells has the pre-pickup position
        // Don't overwrite previousOccupiedCells if occupiedCells is empty (preserve pre-pickup position)
        if (platform.occupiedCells.Count > 0)
        {
            platform.previousOccupiedCells.Clear();
            platform.previousOccupiedCells.AddRange(platform.occupiedCells);
        }

        // Clear old preview/occupied flags for this platform's old cells
        foreach (Vector2Int cell in platform.occupiedCells)
        {
            // Only clear if this platform owns the cell
            if (_cellToPlatform.TryGetValue(cell, out var owner) && owner == platform)
            {
                _worldGrid.TrySetCellFlag(cell, WorldGrid.CellFlag.Empty);
                _cellToPlatform.Remove(cell);
            }
        }

        // Compute new footprint cells from current transform (rotation-aware)
        List<Vector2Int> newCells = GetCellsForPlatform(platform);
        platform.occupiedCells.Clear();
        platform.occupiedCells.AddRange(newCells);

        // Different Flag based on if Preview or Placed
        WorldGrid.CellFlag flagToUse = platform.IsPickedUp ? WorldGrid.CellFlag.OccupyPreview : WorldGrid.CellFlag.Occupied;

        // Update grid occupancy with proper flags
        foreach (Vector2Int cell in platform.occupiedCells)
        {
            // TrySetCellFlag enforces priority (won't overwrite Occupied with OccupyPreview)
            if (_worldGrid.TrySetCellFlag(cell, flagToUse))
            {
                _cellToPlatform[cell] = platform;
            }
        }

        // Mark this platform and affected neighbors for adjacency update
        MarkAdjacencyDirtyForPlatform(platform);
    }



    #endregion

    #region Public API (Platform Registration & Queries)


    /// Get the platform occupying a specific cell, if any
    /// Returns null if cell is empty or out of bounds
    ///
    public bool GetPlatformAtCell(Vector2Int cell, out GamePlatform platform)
    {
        if (_cellToPlatform.TryGetValue(cell, out var owner))
        {
            platform = owner;
            return true;
        }

        platform = null;
        return false;
    }


    // Vector3Int Override
    public bool GetPlatformAtCell(Vector3Int cell, out GamePlatform platform)
    {
        return GetPlatformAtCell(new Vector2Int(cell.x, cell.y), out platform);
    }



    /// Check if cell is occupied by any platform
    /// OPTIONAL BOOL: Include similar Flags other than Occupied (Preview)
    ///
    public bool IsCellOccupied(Vector2Int cell, bool includeAllOccupation = true)
    {
        return
            includeAllOccupation
                ? _worldGrid.CellHasAnyFlag(cell, WorldGrid.CellFlag.Occupied | WorldGrid.CellFlag.OccupyPreview)
                : _worldGrid.CellHasAnyFlag(cell, WorldGrid.CellFlag.Occupied);
    }


    /// TRUE IF: All cells inside Area are FLAG Empty
    /// OccupyPreview -> considered free (allows placement over preview)
    /// Assumes cells are sorted (from GetCellsForPlatform)
    public bool IsAreaEmpty(List<Vector2Int> cells)
    {
        if (cells == null || cells.Count == 0)
            return false;

        // Verify all cells are in bounds
        foreach (Vector2Int cell in cells)
        {
            if (!_worldGrid.CellInBounds(cell))
                return false;
        }

        // Cells are sorted: first = min (bottom-left), last = max (top-right)
        Vector2Int min = cells.First();
        Vector2Int max = cells.Last();

        // Use WorldGrid's optimized area check
        return _worldGrid.AreaIsEmpty(min, max);
    }


    /// Marks a platform and its neighbors as needing adjacency recomputation
    /// Only the affected platforms will be updated in LateUpdate
    private void MarkAdjacencyDirtyForPlatform(GamePlatform platform)
    {
        if (!platform) return;

        // Get all affected platforms (the platform + its neighbors at old and new positions)
        var affected = GetAffectedPlatforms(platform);

        foreach (var p in affected)
        {
            if (p && p.isActiveAndEnabled)
                _platformsNeedingAdjacencyUpdate.Add(p);
        }
    }


    /// Marks all registered platforms as needing adjacency update
    /// Use sparingly - for full recalculation scenarios
    private void MarkAllPlatformsAdjacencyDirty()
    {
        foreach (var platform in _registeredPlatforms)
        {
            if (platform && platform.isActiveAndEnabled)
                _platformsNeedingAdjacencyUpdate.Add(platform);
        }
    }


    /// Public API for external systems to trigger adjacency recomputation for a specific platform
    /// Used by BuildModeManager to update railing preview during placement
    public void TriggerAdjacencyUpdate(GamePlatform platform = null)
    {
        if (platform)
            MarkAdjacencyDirtyForPlatform(platform);
        else
            MarkAllPlatformsAdjacencyDirty();
    }


    ///
    /// Register platform and occupy grid cells with Occupied flag
    /// Called when a platform is successfully placed
    ///
    public void RegisterPlatform(GamePlatform platform)
    {
        if (!platform || platform.occupiedCells == null || platform.occupiedCells.Count == 0) return;

        // Mark cells as Occupied (placed platform)
        foreach (Vector2Int cell in platform.occupiedCells)
        {
            _worldGrid.TrySetCellFlag(cell, WorldGrid.CellFlag.Occupied);
            _cellToPlatform[cell] = platform;
        }

        // Invoke UnityEvent for platform placed
        PlatformPlaced?.Invoke(platform);
    }



    /// Removes platform occupancy from the grid and clears its connections
    /// Does NOT unsubscribe from events (handled by OnPlatformDestroyed)
    ///
    public void UnregisterPlatform(GamePlatform platform)
    {
        if (!platform) return;
        if (!_registeredPlatforms.Contains(platform)) return;

        // Clear cells from WorldGrid and reverse lookup
        if (platform.occupiedCells is { Count: > 0 })
        {
            // Use area method for WorldGrid (cells are sorted: first = min, last = max)
            Vector2Int min = platform.occupiedCells.First();
            Vector2Int max = platform.occupiedCells.Last();
            _worldGrid.SetFlagsInAreaExact(min, max, WorldGrid.CellFlag.Empty);

            // Remove from reverse lookup using GetPlatformAtCell for ownership check
            foreach (Vector2Int cell in platform.occupiedCells)
            {
                if (GetPlatformAtCell(cell, out var owner) && owner == platform)
                {
                    _cellToPlatform.Remove(cell);
                }
            }
        }

        _registeredPlatforms.Remove(platform);

        // Clear connections on this platform (only if active)
        if (platform.gameObject.activeInHierarchy)
            platform.ResetConnections();

        // Clear all NavMesh links involving this platform
        NavMeshLinkManager.Instance?.ClearAllLinksForPlatform(platform);

        // Invoke UnityEvent for platform removed
        PlatformRemoved?.Invoke(platform);

        // Mark affected platforms (neighbors) for adjacency update
        MarkAdjacencyDirtyForPlatform(platform);
    }

    #endregion

    #region Adjacency System (Grid-Based)

    ///
    /// Gets all platforms affected by the given platform
    /// Returns the platform itself plus all platforms in neighboring cells (8-directional)
    /// Considers both current and previous cells for platforms that have moved
    ///
    private HashSet<GamePlatform> GetAffectedPlatforms(GamePlatform platform)
    {
        var affected = new HashSet<GamePlatform>();
        if (!platform) return affected;

        affected.Add(platform);

        // Get all cells to check (current + previous)
        var allCellsToCheck = new List<Vector2Int>();

        if (platform.occupiedCells != null)
            allCellsToCheck.AddRange(platform.occupiedCells);

        if (platform.previousOccupiedCells is { Count: > 0 })
        {
            foreach (var cell in platform.previousOccupiedCells)
            {
                if (!allCellsToCheck.Contains(cell))
                    allCellsToCheck.Add(cell);
            }
        }

        // Get all neighboring cells (8-directional)
        HashSet<Vector2Int> neighborCells = _worldGrid.GetNeighborCells(allCellsToCheck, include8Directional: true);

        // Find platforms occupying neighboring cells
        foreach (var neighborCell in neighborCells)
        {
            if (_cellToPlatform.TryGetValue(neighborCell, out var neighborPlatform))
            {
                if (neighborPlatform != platform)
                    affected.Add(neighborPlatform);
            }
        }

        return affected;
    }


    ///
    /// Rebuilds NavMesh for a platform and all its neighbors
    /// Called only when platform is successfully placed (not while moving)
    ///
    private void RebuildNavMeshForPlatformAndNeighbors(GamePlatform platform)
    {
        if (!platform) return;

        var affectedPlatforms = GetAffectedPlatforms(platform);

        foreach (var p in affectedPlatforms)
        {
            if (p && !p.IsPickedUp)
                p.QueueRebuild();
        }
    }


    ///
    /// Public method for checking if two platforms are adjacent and connecting them
    /// Used by editor tools (SceneLinkTester)
    /// Each platform updates its own sockets - connections trigger NavMesh link requests automatically
    ///
    public void ConnectPlatformsIfAdjacent(GamePlatform platformA, GamePlatform platformB)
    {
        if (!platformA || !platformB) return;

        // Ensure platforms are registered with their grid cells
        if (!_registeredPlatforms.Contains(platformA))
        {
            List<Vector2Int> cells = GetCellsForPlatform(platformA);
            if (cells.Count > 0)
            {
                platformA.occupiedCells = cells;
                RegisterPlatform(platformA);
            }
            else return;
        }

        if (!_registeredPlatforms.Contains(platformB))
        {
            List<Vector2Int> cells = GetCellsForPlatform(platformB);
            if (cells.Count > 0)
            {
                platformB.occupiedCells = cells;
                RegisterPlatform(platformB);
            }
            else return;
        }

        // Each platform updates its own sockets - NavMesh links are requested automatically
        platformA.RefreshSocketStatuses();
        platformB.RefreshSocketStatuses();
    }






    ///
    /// Recomputes adjacency only for platforms marked as needing update
    /// Much more efficient than updating all platforms every frame
    /// This is batched to run once per frame maximum via LateUpdate
    ///
    private void RecomputeAdjacencyForAffectedPlatforms()
    {
        // Prevent recursive calls during adjacency computation
        if (_isRecomputingAdjacency) return;
        _isRecomputingAdjacency = true;

        // Update only the affected platforms
        foreach (var platform in _platformsNeedingAdjacencyUpdate)
        {
            if (!platform || !platform.isActiveAndEnabled) continue;
            platform.RefreshSocketStatuses();
        }

        // Clear the set after processing
        _platformsNeedingAdjacencyUpdate.Clear();
        _isRecomputingAdjacency = false;
    }



    #endregion

    #region Helper Methods

    ///
    /// Compute which 2D grid cells a platform covers on a given level
    /// assuming its footprint is aligned to the 1x1 world grid AND
    /// that rotation is in 90° steps (0, 90, 180, 270)
    /// This is the single source of truth for runtime footprint
    /// 
    /// IMPORTANT: Returns cells in sorted order (left-to-right, bottom-to-top)
    /// First element = minimum (bottom-left), Last element = maximum (top-right)
    ///
    public List<Vector2Int> GetCellsForPlatform(GamePlatform platform)
    {
        var outputCells = new List<Vector2Int>();
        if (!platform) return outputCells;

        // Use platform's world position to find center cell on the desired level
        Vector3 worldPosition = platform.Transform.position;
        var centerCell = _worldGrid.WorldToCell(worldPosition);

        int footprintWidth = Mathf.Max(1, platform.Footprint.x);
        int footprintHeight = Mathf.Max(1, platform.Footprint.y);

        // Determine rotation in 90° steps (0..3)
        float yaw = platform.Transform.eulerAngles.y;
        int rotationSteps = Mathf.RoundToInt(yaw / ROTATION_STEP_DEGREES) & ROTATION_MODULO_MASK;
        bool isRotated90Or270 = (rotationSteps % 2) == 1; // 90° or 270° rotations swap width/height

        int rotatedWidth = isRotated90Or270 ? footprintHeight : footprintWidth; // width in cells after rotation
        int rotatedHeight = isRotated90Or270 ? footprintWidth : footprintHeight; // height in cells after rotation

        int startX = centerCell.x - rotatedWidth / 2;
        int startY = centerCell.y - rotatedHeight / 2;

        // Add cells in sorted order: left-to-right, bottom-to-top
        // This ensures first element = min, last element = max
        for (int cellY = 0; cellY < rotatedHeight; cellY++)
        {
            for (int cellX = 0; cellX < rotatedWidth; cellX++)
            {
                int gridX = startX + cellX;
                int gridY = startY + cellY;
                var gridCell = new Vector2Int(gridX, gridY);
                if (!_worldGrid.CellInBounds(gridCell))
                    continue;

                outputCells.Add(new Vector2Int(gridX, gridY));
            }
        }

        return outputCells;
    }

    #endregion
}
}