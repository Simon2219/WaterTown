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


    private void LateUpdate()
    {
        // Batch adjacency recomputation for placed platforms
        if (_adjacencyDirty && !_isRecomputingAdjacency)
        {
            _adjacencyDirty = false;
            UpdatePlacedPlatforms();
        }
    }


    private void OnDisable()
    {
        // Unsubscribe from platform lifecycle events
        GamePlatform.PlatformEnabled -= OnPlatformEnabled;
        GamePlatform.PlatformDisabled -= OnPlatformDisabled;
        GamePlatform.PlatformPlaced -= HandlePlatformPlaced;
        GamePlatform.PlatformPickedUp -= OnPlatformPickedUp;
    }
    
    #endregion

    #region Platform Lifecycle Event Handlers

    ///
    /// Event handler called when ANY platform becomes enabled
    /// Platforms are only registered when placed, not on enable
    ///
    private void OnPlatformEnabled(GamePlatform platform)
    {
        // Platform will be registered when placed via PlatformPlaced event
        // No auto-registration needed here
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
    /// Registers platform in grid and triggers full adjacency computation
    ///
    private void HandlePlatformPlaced(GamePlatform platform)
    {
        if (!platform) return;
        RegisterPlatform(platform);
    }


    ///
    /// Event handler called when ANY platform is picked up
    /// Clears grid cells and updates preview immediately
    ///
    private void OnPlatformPickedUp(GamePlatform platform)
    {
        if (!platform) return;
        
        // Clear grid cells to free the space
        if (_allPlatforms.TryGetValue(platform, out PlatformGameData data))
        {
            foreach (Vector2Int cell in data.cells)
            {
                _worldGrid.TryRemoveFlag(cell, WorldGrid.CellFlag.Occupied);
                _cellToPlatform.Remove(cell);
            }
        }
        
        // Immediately update preview (lightweight, no NavMesh)
        UpdatePreview(platform);
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
    ///
    public bool IsAreaFree(List<Vector2Int> cells)
    {
        foreach (Vector2Int cell in cells)
        {
            
            if (!_worldGrid.CellInBounds(cell))
                return false;
            
            if (_worldGrid.CellHasAnyFlag(cell, WorldGrid.CellFlag.Occupied))
                return false;
        }
        return true;
    }


    /// Marks adjacency as needing recomputation
    /// Batched to LateUpdate for performance
    /// Multiple changes in the same frame will only trigger one recomputation
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

        // Ensure we have game data
        if (!_allPlatforms.TryGetValue(platform, out var data))
        {
            data = new PlatformGameData();
            _allPlatforms[platform] = data;
        }
        
        data.cells.Clear();
        data.cells.AddRange(platform.occupiedCells);

        // Add to WorldGrid and reverse lookup
        foreach (Vector2Int cell in platform.occupiedCells)
        {
            _worldGrid.TryAddFlag(cell, WorldGrid.CellFlag.Occupied);
            _cellToPlatform[cell] = platform;
        }
        
        // Build NavMesh for placed platform
        platform.BuildLocalNavMesh();
        
        // Invoke UnityEvent for platform placed
        OnPlatformPlaced?.Invoke(platform);

        // Mark adjacency for batched recomputation
        MarkAdjacencyDirty();
    }


    ///
    /// Removes platform occupancy from the grid and clears its connections
    ///
    public void UnregisterPlatform(GamePlatform platform)
    {
        if (!platform) return;
        if (!_allPlatforms.TryGetValue(platform, out PlatformGameData data)) return;

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
    /// Check if two platforms are adjacent by comparing their grid cells
    /// Two platforms are adjacent if any of their cells share an edge (not just a corner)
    /// Creates socket connections and NavMesh links where cells are adjacent
    /// Used by editor tools and runtime systems
    ///
    public void ConnectPlatformsIfAdjacent(GamePlatform a, GamePlatform b, bool rebuildNavMesh)
    {
        if (!a || !b || a == b) return;
        
        // Get cell data for both platforms
        List<Vector2Int> cellsA = GetCellsForPlatform(a);
        List<Vector2Int> cellsB = GetCellsForPlatform(b);
        
        if (cellsA.Count == 0 || cellsB.Count == 0) return;
        
        // Build sockets if needed
        a.BuildSockets();
        b.BuildSockets();

        // Quick check: are any cells edge-adjacent
        bool hasAdjacentCells = false;
        foreach (var cellA in cellsA)
        {
            List<Vector2Int> neighbors = _worldGrid.GetNeighbors4(cellA);
            foreach (var neighbor in neighbors)
            {
                if (cellsB.Contains(neighbor))
                {
                    hasAdjacentCells = true;
                    break;
                }
            }
            if (hasAdjacentCells) break;
        }

        if (!hasAdjacentCells) return;

        // Find matching sockets at connection boundary
        var aSocketIndices = new HashSet<int>();
        var bSocketIndices = new HashSet<int>();
        
        // Match sockets by world position (0.5m intervals)
        const float socketMatchDistance = 0.1f;
        
        for (int i = 0; i < a.SocketCount; i++)
        {
            Vector3 worldPosA = a.GetSocketWorldPosition(i);
            
            for (int j = 0; j < b.SocketCount; j++)
            {
                Vector3 worldPosB = b.GetSocketWorldPosition(j);
                float distSqr = (worldPosA - worldPosB).sqrMagnitude;
                
                if (distSqr <= socketMatchDistance * socketMatchDistance)
                {
                    aSocketIndices.Add(i);
                    bSocketIndices.Add(j);
                    break;
                }
            }
        }

        if (aSocketIndices.Count == 0 && bSocketIndices.Count == 0) return;

        // Apply connection visuals (railings)
        if (aSocketIndices.Count > 0)
            a.ApplyConnectionVisuals(aSocketIndices, true);
        if (bSocketIndices.Count > 0)
            b.ApplyConnectionVisuals(bSocketIndices, true);

        // Only rebuild NavMesh and create links in placed mode
        if (rebuildNavMesh)
        {
            a.QueueRebuild();
            b.QueueRebuild();
            
            // Create NavMesh link at average connection position
            Vector3 avgPosA = Vector3.zero;
            foreach (int idx in aSocketIndices)
                avgPosA += a.GetSocketWorldPosition(idx);
            avgPosA /= aSocketIndices.Count;

            Vector3 avgPosB = Vector3.zero;
            foreach (int idx in bSocketIndices)
                avgPosB += b.GetSocketWorldPosition(idx);
            avgPosB /= bSocketIndices.Count;

            CreateNavLinkBetween(a, avgPosA, b, avgPosB);
        }
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
    /// Full adjacency update for all placed platforms with NavMesh rebuild
    /// Processes all registered platforms and creates NavMesh links
    ///
    private void UpdatePlacedPlatforms()
    {
        _isRecomputingAdjacency = true;

        var placedPlatforms = new List<GamePlatform>();
        
        // Collect all registered platforms
        foreach (var gp in _allPlatforms.Keys)
        {
            if (!gp) continue;
            if (!gp.isActiveAndEnabled) continue;
            placedPlatforms.Add(gp);
        }

        // Ensure all components are registered
        foreach (var p in placedPlatforms)
        {
            p.EnsureChildrenModulesRegistered();
            p.EnsureChildrenRailingsRegistered();
        }

        // Reset all connections first so rails reappear when platforms separate
        foreach (var p in placedPlatforms)
            p.EditorResetAllConnections();

        // Compute pairwise connections with full NavMesh rebuild
        for (int i = 0; i < placedPlatforms.Count; i++)
        {
            for (int j = i + 1; j < placedPlatforms.Count; j++)
            {
                ConnectPlatformsIfAdjacent(placedPlatforms[i], placedPlatforms[j], rebuildNavMesh: true);
            }
        }

        _isRecomputingAdjacency = false;
    }


    ///
    /// Lightweight preview update for picked-up platform
    /// Only checks adjacency with platforms occupying neighbor cells
    /// Updates railings only, no NavMesh or links
    ///
    private void UpdatePreview(GamePlatform previewPlatform)
    {
        _isRecomputingAdjacency = true;
        
        // Ensure preview platform components are registered
        previewPlatform.EnsureChildrenModulesRegistered();
        previewPlatform.EnsureChildrenRailingsRegistered();
        
        // Reset preview platform connections
        previewPlatform.EditorResetAllConnections();
        
        // Get cells the preview platform would occupy
        List<Vector2Int> previewCells = GetCellsForPlatform(previewPlatform);
        if (previewCells == null || previewCells.Count == 0)
        {
            _isRecomputingAdjacency = false;
            return;
        }
        
        // Find adjacent platforms via cell lookup (optimization!)
        var adjacentPlatforms = new HashSet<GamePlatform>();
        foreach (var cell in previewCells)
        {
            List<Vector2Int> neighbors = _worldGrid.GetNeighbors4(cell);
            foreach (var neighbor in neighbors)
            {
                GamePlatform adjacentPlatform = GetPlatformAtCell(neighbor);
                if (adjacentPlatform != null && adjacentPlatform != previewPlatform)
                {
                    adjacentPlatforms.Add(adjacentPlatform);
                }
            }
        }
        
        // Only check adjacency with platforms in neighbor cells
        foreach (var adjacentPlatform in adjacentPlatforms)
        {
            ConnectPlatformsIfAdjacent(previewPlatform, adjacentPlatform, rebuildNavMesh: false);
        }
        
        _isRecomputingAdjacency = false;
    }


    ///
    /// Public API for BuildModeManager to update preview during movement
    /// Updates adjacency immediately for the picked-up platform
    ///
    public void UpdateMovingPlatformPreview(GamePlatform platform)
    {
        if (!platform) return;
        UpdatePreview(platform);
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
        if (!platform) return null;

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



