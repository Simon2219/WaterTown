using System.Collections.Generic;
using Grid;
using Unity.AI.Navigation;
using Unity.Entities.UniversalDelegates;
using UnityEngine;
using UnityEngine.Events;
using WaterTown.Platforms;


///
/// Manages all platform-specific logic: registration, adjacency, socket connections, and NavMesh links
/// Separated from TownManager to isolate platform concerns from town-level orchestration
///
[DisallowMultipleComponent]
public class PlatformManager : MonoBehaviour
{
    #region Inspector Fields
    
    
    [Header("NavMesh Link Settings")]
    
    [Tooltip("Width of NavMesh links created between connected platforms (meters).")]
    [SerializeField] private float navLinkWidth = 0.6f;

    
    [Header("Events")]
    
    [Tooltip("Invoked when a platform is successfully placed and registered.")]
    public UnityEvent<GamePlatform> OnPlatformPlaced;
    
    [Tooltip("Invoked when a platform is removed/unregistered.")]
    public UnityEvent<GamePlatform> OnPlatformRemoved;
    
    
    #endregion
    
    
    
    
    #region Configuration & Constants
    
    
    private WorldGrid _worldGrid;
    
    private const int ROTATION_STEP_DEGREES = 90;
    private const int ROTATION_MODULO_MASK = 3; // 0-3 for 4 cardinal directions (0°, 90°, 180°, 270°)
    
    
    // Platform Game Data
    
    /// Holds all runtime data for a registered platform
    private class PlatformGameData
    {
        public List<Vector2Int> cells = new(); // Grid cells this platform occupies
        public List<Vector2Int> previousCells = new(); // Track previous cells for efficient updates
    }

    /// Single source of truth for all registered platforms and their data
    /// Maps each platform to its runtime game data (cells, occupancy state, etc)
    private readonly Dictionary<GamePlatform, PlatformGameData> _allPlatforms = new();
    
    /// Read-only access to all registered platforms
    public IReadOnlyCollection<GamePlatform> AllPlatforms => _allPlatforms.Keys;
    
    /// Reverse lookup: which platform occupies a given cell
    private readonly Dictionary<Vector2Int, GamePlatform> _cellToPlatform = new();

    // Adjacency recomputation batching (performance optimization)
    private bool _adjacencyDirty = false;
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
    

    
    ///
    /// Finds and validates all required dependencies
    /// Throws InvalidOperationException if any critical dependency is missing
    ///
    private void FindDependencies()
    {
        if (!_worldGrid)
        {
            _worldGrid = FindFirstObjectByType<WorldGrid>();
            if (!_worldGrid)
            {
                throw ErrorHandler.MissingDependency(typeof(WorldGrid), this);
            }
        }
    }


    private void OnEnable()
    {
        // Subscribe to platform lifecycle events
        GamePlatform.PlatformEnabled += OnPlatformEnabled;
        GamePlatform.PlatformDisabled += OnPlatformDisabled;
        GamePlatform.PlatformPlaced += HandlePlatformPlaced;
        GamePlatform.PlatformPickedUp += OnPlatformPickedUp;
    }


    private void Start()
    {
        SpawnStartupPlatforms();
    }


    private void LateUpdate()
    {
        // Batch adjacency recomputation to once per frame if dirty
        if (_adjacencyDirty && !_isRecomputingAdjacency)
        {
            _adjacencyDirty = false;
            RecomputeAllAdjacency();                                                 
        }
    }


    private void OnDisable()
    {
        // Unsubscribe from platform lifecycle events
        GamePlatform.PlatformEnabled -= OnPlatformEnabled;
        GamePlatform.PlatformDisabled -= OnPlatformDisabled;
        GamePlatform.PlatformPlaced -= HandlePlatformPlaced;
        GamePlatform.PlatformPickedUp -= OnPlatformPickedUp;
        
        // Best-effort cleanup of pose subscriptions
        foreach (GamePlatform kvp in _allPlatforms.Keys)
        {
                kvp.PoseChanged -= OnPlatformPoseChanged;
        }
    }
    
    #endregion

    #region Platform Lifecycle Event Handlers

    private void SpawnStartupPlatforms()
    {
        // Later implementation for an Asset defining Starting Setups for the Town
        
        // Ensure all existing platforms in the scene are registered into the grid
        foreach (GamePlatform platform in _allPlatforms.Keys)
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
    
    
    
    
    ///
    /// Event handler called when ANY platform becomes enabled
    /// Adds the platform to the global registry and subscribes to pose changes
    ///
    private void OnPlatformEnabled(GamePlatform platform)
    {
        if (!platform) return;
        
        // Add to registry if not already present
        if (!_allPlatforms.ContainsKey(platform))
        {
            _allPlatforms[platform] = new PlatformGameData();
            // Subscribe to pose changes so we can track movement in preview/placed modes
            platform.PoseChanged += OnPlatformPoseChanged;
        }
    }


    ///
    /// Event handler called when ANY platform becomes disabled
    /// Removes the platform from the global registry and unregisters from grid
    ///
    private void OnPlatformDisabled(GamePlatform platform)
    {
        if (!platform) return;
        UnregisterPlatform(platform);
    }


    ///
    /// Event handler called when ANY platform is placed
    /// Registers platform in grid, marks adjacency dirty, and rebuilds NavMesh
    ///
    private void HandlePlatformPlaced(GamePlatform platform)
    {
        if (!platform) return;
        
        // Register platform in grid
        RegisterPlatform(platform);
        
        // Mark adjacency dirty so it recalculates using the same logic as preview
        // This ensures consistent behavior between preview and placement
        MarkAdjacencyDirty();
        
        // Rebuild NavMesh for this platform and all affected neighbors
        RebuildNavMeshForPlatformAndNeighbors(platform);
    }


    ///
    /// Event handler called when ANY platform is picked up
    /// Triggers lightweight adjacency update without unregistering from grid
    ///
    private void OnPlatformPickedUp(GamePlatform platform)
    {
        if (!platform) return;
        
        // Store current cells as previous for the next move operation
        if (_allPlatforms.TryGetValue(platform, out var data))
        {
            data.previousCells.Clear();
            data.previousCells.AddRange(data.cells);
        }
        
        // Calculate adjacency for this platform and neighbors immediately
        CalculateAdjacencyForPlatform(platform);
    }


    ///
    /// Called when a platform reports its transform changed
    /// Lightweight update for runtime platform movement (preview mode only)
    ///
    private void OnPlatformPoseChanged(GamePlatform platform)
    {
        if (!platform) return;
        
        // Only update grid occupancy for platforms in preview mode (being moved)
        // Placed platforms have permanent occupancy set by RegisterPlatform
        if (platform.IsPickedUp)
        {
            MovePlatform(platform);
        }
    }


    ///
    /// Lightweight platform movement update (for runtime pose changes)
    /// Updates grid occupancy and marks adjacency dirty without full rebuild
    /// This is called every frame when platform transform changes
    ///
    private void MovePlatform(GamePlatform platform)
    {
        if (!_allPlatforms.TryGetValue(platform, out PlatformGameData data)) return;

        // Store current cells as previous for the next update
        data.previousCells.Clear();
        data.previousCells.AddRange(data.cells);

        // Clear old occupancy for this platform (only if it has cells)
        foreach (Vector2Int cell in data.cells)
        {
            // Remove from WorldGrid
            _worldGrid.TryRemoveFlag(cell, WorldGrid.CellFlag.Occupied);
            
            // Remove from reverse lookup
            _cellToPlatform.Remove(cell);
        }

        // Compute new footprint cells from current transform (rotation-aware)
        List<Vector2Int> newCells = GetCellsForPlatform(platform);

        data.cells.Clear();
        data.cells.AddRange(newCells);

        // Update grid occupancy for visual updates and adjacency
        foreach (Vector2Int cell in data.cells)
        {
            // Add to WorldGrid
            _worldGrid.TryAddFlag(cell, WorldGrid.CellFlag.Occupied);
            
            // Add to reverse lookup
            _cellToPlatform[cell] = platform;
        }
        
        // Mark adjacency as dirty for batched recomputation (no NavMesh rebuild during movement)
        MarkAdjacencyDirty();
    }
    
    #endregion

    #region Public API (Platform Registration & Queries)

    ///
    /// Get the platform occupying a specific cell, if any
    /// Returns null if cell is empty or out of bounds
    /// O(1) lookup via reverse dictionary
    ///
    public GamePlatform GetPlatformAtCell(Vector2Int cell)
    {
        return _cellToPlatform.GetValueOrDefault(cell);
    }


    ///
    /// Get the platform occupying a specific cell (3D grid cell converted to 2D)
    /// Returns null if cell is empty or out of bounds
    /// O(1) lookup via reverse dictionary
    ///
    public GamePlatform GetPlatformAtCell(Vector3Int cell)
    {
        return GetPlatformAtCell(new Vector2Int(cell.x, cell.y));
    }


    ///
    /// Check if a specific cell is occupied by any platform
    /// O(1) lookup via reverse dictionary
    ///
    public bool IsCellOccupied(Vector2Int cell)
    {
        return _cellToPlatform.ContainsKey(cell);
    }


    ///
    /// True if all given 2D cells are inside the grid and not Occupied (level 0)
    /// Used by BuildModeManager as placement validation
    /// Can optionally ignore a specific platform (useful for validating while moving a platform)
    ///
    public bool IsAreaFree(List<Vector2Int> cells, GamePlatform ignorePlatform = null)
    {
        foreach (Vector2Int cell in cells)
        {
            
            if (!_worldGrid.CellInBounds(cell))
                return false;
            
            // If this cell is occupied, check if it's occupied by the platform we're ignoring
            if (_worldGrid.CellHasAnyFlag(cell, WorldGrid.CellFlag.Occupied))
            {
                // If we have an ignore platform, check if this cell belongs to it
                if (ignorePlatform != null && _cellToPlatform.TryGetValue(cell, out var occupyingPlatform))
                {
                    if (occupyingPlatform == ignorePlatform)
                        continue; // Ignore this cell since it's occupied by the platform we're moving
                }
                
                return false;
            }
        }
        return true;
    }


    /// Marks adjacency as needing recomputation
    /// Batched to LateUpdate for performance
    /// Multiple pose changes in the same frame will only trigger one recomputation
    private void MarkAdjacencyDirty()
    {
        _adjacencyDirty = true;
    }


    /// Public API for external systems to trigger adjacency recomputation
    /// Used by BuildModeManager to update railing preview during placement
    public void TriggerAdjacencyUpdate()
    {
        MarkAdjacencyDirty();
    }


    ///
    /// Register platform and occupy grid cells
    /// Updates platform's occupied cells in the grid and triggers adjacency computation
    ///
    public void RegisterPlatform(GamePlatform platform)
    {
        if (!platform || platform.occupiedCells == null || platform.occupiedCells.Count == 0) return;

        // Remove old occupancy if any
        if (_allPlatforms.TryGetValue(platform, out var oldData))
        {
            // Remove from WorldGrid and reverse lookup
            foreach (Vector2Int cell in oldData.cells)
            {
                _worldGrid.TryRemoveFlag(cell, WorldGrid.CellFlag.Occupied);
                _cellToPlatform.Remove(cell);
            }
        }

        // Ensure we have game data (pose change subscription handled in OnPlatformEnabled)
        if (!_allPlatforms.TryGetValue(platform, out var data))
        {
            data = new PlatformGameData();
            _allPlatforms[platform] = data;
            platform.PoseChanged += OnPlatformPoseChanged;
        }
        
        data.cells.Clear();
        data.cells.AddRange(platform.occupiedCells);

        // Add to WorldGrid and reverse lookup
        foreach (Vector2Int cell in platform.occupiedCells)
        {
            _worldGrid.TryAddFlag(cell, WorldGrid.CellFlag.Occupied);
            _cellToPlatform[cell] = platform;
        }
        
        // Invoke UnityEvent for platform placed
        OnPlatformPlaced?.Invoke(platform);
    }


    ///
    /// Removes platform occupancy from the grid and clears its connections
    ///
    public void UnregisterPlatform(GamePlatform platform)
    {
        if (!platform) return;
        if (!_allPlatforms.TryGetValue(platform, out PlatformGameData data)) return;

        platform.PoseChanged -= OnPlatformPoseChanged;

        // Remove from WorldGrid and reverse lookup
        foreach (Vector2Int cell in data.cells)
        {
            _worldGrid.TryRemoveFlag(cell, WorldGrid.CellFlag.Occupied);
            _cellToPlatform.Remove(cell);
        }

        _allPlatforms.Remove(platform);

        // Clear connections on this platform (only if active)
        if (platform.gameObject.activeInHierarchy)
            platform.EditorResetAllConnections();

        // Invoke UnityEvent for platform removed
        OnPlatformRemoved?.Invoke(platform);

        // Mark adjacency for batched recomputation
        MarkAdjacencyDirty();
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
        
        if (!_allPlatforms.TryGetValue(platform, out var data)) return affected;
        
        // Get all neighboring cells (8-directional ring around platform footprint)
        var neighborCells = new HashSet<Vector2Int>();
        
        // Helper function to add neighbors for a list of cells
        void AddNeighborsForCells(List<Vector2Int> cells)
        {
            foreach (var cell in cells)
            {
                // Check all 8 directions around each cell
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue; // Skip the cell itself
                        
                        var neighborCell = new Vector2Int(cell.x + dx, cell.y + dy);
                        
                        // Skip if this neighbor is occupied by the platform itself
                        if (data.cells.Contains(neighborCell)) continue;
                        if (data.previousCells.Contains(neighborCell)) continue;
                        
                        neighborCells.Add(neighborCell);
                    }
                }
            }
        }
        
        // Add neighbors from current cells
        AddNeighborsForCells(data.cells);
        
        // Add neighbors from previous cells (for platforms that have moved)
        if (data.previousCells.Count > 0)
            AddNeighborsForCells(data.previousCells);
        
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
    /// Calculate adjacency for a specific platform and its neighbors
    /// Only updates affected platforms (efficient for moving platforms)
    ///
    private void CalculateAdjacencyForPlatform(GamePlatform platform)
    {
        if (!platform) return;
        
        var affectedPlatforms = GetAffectedPlatforms(platform);
        
        // Reset connections for affected platforms
        foreach (var p in affectedPlatforms)
        {
            if (p && p.gameObject.activeInHierarchy)
                ResetPlatformConnections(p);
        }
        
        // Calculate socket connections between all affected platforms
        var platformList = new List<GamePlatform>(affectedPlatforms);
        for (int i = 0; i < platformList.Count; i++)
        {
            for (int j = i + 1; j < platformList.Count; j++)
            {
                UpdateSocketConnections(platformList[i], platformList[j]);
            }
        }
    }


    ///
    /// Resets all socket connections and visuals for a single platform
    ///
    private void ResetPlatformConnections(GamePlatform platform)
    {
        if (!platform || !platform.gameObject.activeInHierarchy) return;
        platform.EditorResetAllConnections();
    }


    ///
    /// Updates socket connections between two platforms
    /// ONLY sets socket statuses and creates NavMesh links - platforms handle their own visuals
    ///
    private void UpdateSocketConnections(GamePlatform platformA, GamePlatform platformB)
    {
        if (!platformA || !platformB || platformA == platformB) return;
        
        // Find matching sockets by world position (socket-by-socket checking)
        var aSocketIndices = new HashSet<int>();
        var bSocketIndices = new HashSet<int>();
        
        const float socketMatchDistance = 0.1f; // 10cm tolerance
        
        // Check each socket on platform A against all sockets on platform B
        for (int i = 0; i < platformA.SocketCount; i++)
        {
            Vector3 worldPosA = platformA.GetSocketWorldPosition(i);
            
            for (int j = 0; j < platformB.SocketCount; j++)
            {
                Vector3 worldPosB = platformB.GetSocketWorldPosition(j);
                float distSqr = (worldPosA - worldPosB).sqrMagnitude;
                
                if (distSqr <= socketMatchDistance * socketMatchDistance)
                {
                    aSocketIndices.Add(i);
                    bSocketIndices.Add(j);
                    break; // Only match each socket once
                }
            }
        }
        
        // Apply socket connections (platforms update their own visuals automatically)
        if (aSocketIndices.Count > 0)
            platformA.ApplyConnectionVisuals(aSocketIndices, true);
        if (bSocketIndices.Count > 0)
            platformB.ApplyConnectionVisuals(bSocketIndices, true);
        
        // Create NavMesh links if both platforms have connections
        if (aSocketIndices.Count > 0 && bSocketIndices.Count > 0)
        {
            CreateNavMeshLinkBetweenPlatforms(platformA, aSocketIndices, platformB, bSocketIndices);
        }
    }


    ///
    /// Creates NavMesh link between connected sockets of two platforms
    ///
    private void CreateNavMeshLinkBetweenPlatforms(
        GamePlatform platformA, HashSet<int> aSocketIndices,
        GamePlatform platformB, HashSet<int> bSocketIndices)
    {
        // Calculate average positions of connected sockets
        Vector3 avgPosA = Vector3.zero;
        foreach (int idx in aSocketIndices)
            avgPosA += platformA.GetSocketWorldPosition(idx);
        avgPosA /= aSocketIndices.Count;

        Vector3 avgPosB = Vector3.zero;
        foreach (int idx in bSocketIndices)
            avgPosB += platformB.GetSocketWorldPosition(idx);
        avgPosB /= bSocketIndices.Count;

        CreateNavLinkBetween(platformA, avgPosA, platformB, avgPosB);
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
    /// Public method for checking if two platforms are adjacent
    /// Used by editor tools (SceneLinkTester)
    ///
    public void ConnectPlatformsIfAdjacent(GamePlatform platformA, GamePlatform platformB)
    {
        if (!platformA || !platformB) return;
        
        // Ensure platforms are registered
        if (!_allPlatforms.ContainsKey(platformA))
        {
            List<Vector2Int> cells = GetCellsForPlatform(platformA);
            if (cells.Count > 0)
            {
                platformA.occupiedCells = cells;
                RegisterPlatform(platformA);
            }
            else return;
        }
        
        if (!_allPlatforms.ContainsKey(platformB))
        {
            List<Vector2Int> cells = GetCellsForPlatform(platformB);
            if (cells.Count > 0)
            {
                platformB.occupiedCells = cells;
                RegisterPlatform(platformB);
            }
            else return;
        }
        
        // Update socket connections (platforms handle their own visuals)
        UpdateSocketConnections(platformA, platformB);
    }




    ///
    /// Creates a NavMesh link between two platforms at specified world positions
    /// Link is attached to platform A
    ///
    private void CreateNavLinkBetween(GamePlatform platformA, Vector3 posA, GamePlatform platformB, Vector3 posB)
    {
        if (!platformA || !platformB) return;
        
        var parent = GetOrCreateLinksParent(platformA.transform);
        var go = new GameObject($"Link_{platformA.name}_to_{platformB.name}");
        go.transform.SetParent(parent, false);

        Vector3 center = 0.5f * (posA + posB);
        go.transform.position = center;

        var link = go.AddComponent<NavMeshLink>();
        link.startPoint = go.transform.InverseTransformPoint(posA);
        link.endPoint   = go.transform.InverseTransformPoint(posB);
        link.bidirectional = true;
        link.width = navLinkWidth;
        link.area = 0;
        link.agentTypeID = platformA.NavSurface ? platformA.NavSurface.agentTypeID : 0;
    }


    ///
    /// Gets or creates a child transform with the given name
    ///
    private static Transform GetOrCreateLinksParent(Transform parent)
    {
        var t = parent.Find("Links");
        if (!t)
        {
            var go = new GameObject("Links");
            t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
        }
        return t;
    }


    ///
    /// Recomputes adjacency for all platforms
    /// Only updates socket connections - NavMesh and setup handled separately
    /// This is batched to run once per frame maximum via LateUpdate
    ///
    private void RecomputeAllAdjacency()
    {
        // Prevent recursive calls during adjacency computation
        if (_isRecomputingAdjacency) return;
        _isRecomputingAdjacency = true;

        var allActivePlatforms = new List<GamePlatform>();
        
        // Collect all active platforms (placed and picked up)
        foreach (var gp in _allPlatforms.Keys)
        {
            if (!gp) continue;
            if (!gp.isActiveAndEnabled) continue;
            allActivePlatforms.Add(gp);
        }

        // Reset all connections to baseline
        foreach (var p in allActivePlatforms)
            ResetPlatformConnections(p);

        // Calculate socket connections for all platform pairs
        // This works the same whether platforms are placed or being moved
        for (int i = 0; i < allActivePlatforms.Count; i++)
        {
            for (int j = i + 1; j < allActivePlatforms.Count; j++)
            {
                UpdateSocketConnections(allActivePlatforms[i], allActivePlatforms[j]);
            }
        }

        _isRecomputingAdjacency = false;
    }


    ///
    /// Calculate adjacency for all platforms
    /// Full recalculation (expensive - use sparingly)
    ///
    public void CalculateAdjacencyForAll()
    {
        MarkAdjacencyDirty();
    }
    
    #endregion

    #region Helper Methods

    ///
    /// Compute which 2D grid cells a platform covers on a given level
    /// assuming its footprint is aligned to the 1x1 world grid AND
    /// that rotation is in 90° steps (0, 90, 180, 270)
    /// This is the single source of truth for runtime footprint
    ///
    public List<Vector2Int> GetCellsForPlatform(GamePlatform platform)
    {
        var outputCells = new List<Vector2Int>();
        if (!platform) return outputCells;

        // Use platform's world position to find center cell on the desired level
        Vector3 worldPosition = platform.transform.position;
        var centerCell = _worldGrid.WorldToCell(worldPosition);
        int centerX = centerCell.x;
        int centerY = centerCell.y;

        int footprintWidth = Mathf.Max(1, platform.Footprint.x);
        int footprintHeight = Mathf.Max(1, platform.Footprint.y);

        // Determine rotation in 90° steps (0..3)
        float yaw = platform.transform.eulerAngles.y;
        int rotationSteps = Mathf.RoundToInt(yaw / ROTATION_STEP_DEGREES) & ROTATION_MODULO_MASK;
        bool isRotated90Or270 = (rotationSteps % 2) == 1; // 90° or 270° rotations swap width/height

        int rotatedWidth = isRotated90Or270 ? footprintHeight : footprintWidth; // width in cells after rotation
        int rotatedHeight = isRotated90Or270 ? footprintWidth : footprintHeight; // height in cells after rotation

        int startX = centerX - rotatedWidth / 2;
        int startY = centerY - rotatedHeight / 2;

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



