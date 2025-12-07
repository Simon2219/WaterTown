using System;
using System.Collections.Generic;
using Grid;
using UnityEngine;
using UnityEngine.Events;
using WaterTown.Platforms;


/// <summary>
/// Manages all platform-specific logic: registration, adjacency, socket connections, and NavMesh links.
/// Separated from TownManager to isolate platform concerns from town-level orchestration.
/// </summary>
[DisallowMultipleComponent]
public class PlatformManager : MonoBehaviour
{
    #region Configuration & Constants
    
    private const int ROTATION_STEP_DEGREES = 90;
    private const int ROTATION_MODULO_MASK = 3; // 0-3 for 4 cardinal directions (0°, 90°, 180°, 270°)

    [Header("Dependencies")]
    [SerializeField] private WorldGrid grid;
    public WorldGrid Grid => grid;

    [Header("Default Level")]
    [Tooltip("Level index to use when auto-computing a platform's cells.")]
    [SerializeField] private int defaultLevel = 0;
    public int DefaultLevel => defaultLevel;

    [Header("NavMesh Link Settings")]
    [Tooltip("Width of NavMesh links created between connected platforms (meters).")]
    [SerializeField] private float navLinkWidth = 0.6f;

    [Header("Events")]
    [Tooltip("Invoked when a platform is successfully placed and registered.")]
    public UnityEvent<GamePlatform> OnPlatformPlaced = new UnityEvent<GamePlatform>();
    
    [Tooltip("Invoked when a platform is removed/unregistered.")]
    public UnityEvent<GamePlatform> OnPlatformRemoved = new UnityEvent<GamePlatform>();
    
    // --- Platform occupancy bookkeeping ---

    private class PlatformEntry
    {
        public GamePlatform platform;
        public List<Vector2Int> cells2D = new();
        public bool marksOccupied; // true = we write Occupied flags into the grid
    }

    // Platforms we know about → their occupied cells
    private readonly Dictionary<GamePlatform, PlatformEntry> _entries = new();

    // Temp lists (avoid allocations)
    private static readonly List<GamePlatform> _tmpPlatforms = new();
    private readonly List<Vector2Int> _tmpCells2D = new();

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
        catch (Exception ex)
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
        if (!grid)
        {
            grid = FindFirstObjectByType<WorldGrid>();
            if (!grid)
            {
                throw ErrorHandler.MissingDependency(typeof(WorldGrid), this);
            }
        }
    }

    private void OnEnable()
    {
        GamePlatform.PlatformRegistered   += OnPlatformRegistered;
        GamePlatform.PlatformUnregistered += OnPlatformUnregistered;
    }

    private void Start()
    {
        // Ensure all existing platforms in the scene are registered into the grid
        foreach (var platform in GamePlatform.AllPlatforms)
        {
            if (!platform) continue;
            if (!platform.isActiveAndEnabled) continue;

            _tmpCells2D.Clear();
            ComputeCellsForPlatform(platform, _tmpCells2D);
            if (_tmpCells2D.Count > 0)
            {
                // Registers occupancy AND triggers adjacency for all platforms
                RegisterPlatform(platform, _tmpCells2D, markOccupiedInGrid: true);
            }
        }
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
        GamePlatform.PlatformRegistered   -= OnPlatformRegistered;
        GamePlatform.PlatformUnregistered -= OnPlatformUnregistered;

        // Best-effort cleanup of pose subscriptions
        foreach (var kvp in _entries)
        {
            if (kvp.Key)
                kvp.Key.PoseChanged -= OnPlatformPoseChanged;
        }
    }
    
    #endregion

    #region GamePlatform Event Handlers
    
    private void OnPlatformRegistered(GamePlatform platform)
    {
        // We intentionally DO NOT auto-register into the grid here,
        // because BuildModeManager controls when a platform is
        // preview-only vs actually placed.
        // Scene platforms are registered once in Start().
    }

    private void OnPlatformUnregistered(GamePlatform platform)
    {
        if (!platform) return;
        UnregisterPlatform(platform);
    }

    /// <summary>
    /// Called when a platform reports its transform changed.
    /// Optional runtime support for moving platforms (and the ghost).
    /// </summary>
    private void OnPlatformPoseChanged(GamePlatform platform)
    {
        if (!grid || platform == null) return;
        if (!_entries.TryGetValue(platform, out var entry)) return;

        int level = 0;
        bool marksOccupied = entry.marksOccupied;

        // 1) Clear old occupancy for this platform
        if (marksOccupied)
        {
            foreach (var cell2D in entry.cells2D)
            {
                var gridCell = new Vector3Int(cell2D.x, cell2D.y, level);
                grid.TryRemoveFlag(gridCell, WorldGrid.CellFlag.Occupied);
            }
        }

        // 2) Compute new footprint cells from current transform (rotation-aware)
        _tmpCells2D.Clear();
        ComputeCellsForPlatform(platform, _tmpCells2D);

        entry.cells2D.Clear();
        entry.cells2D.AddRange(_tmpCells2D);

        if (marksOccupied)
        {
            foreach (var cell2D in entry.cells2D)
            {
                var gridCell = new Vector3Int(cell2D.x, cell2D.y, level);
                grid.TryAddFlag(gridCell, WorldGrid.CellFlag.Occupied, platformId: 0, payload: platform.GetInstanceID());
            }
        }

        // 3) Mark adjacency as dirty for batched recomputation (performance optimization)
        MarkAdjacencyDirty();
    }
    
    #endregion

    #region Public API (Platform Registration)
    
    /// <summary>
    /// True if all given 2D cells (at level) are inside the grid and not Occupied.
    /// Used by BuildModeManager as placement validation.
    /// </summary>
    public bool IsAreaFree(List<Vector2Int> cells, int level = 0)
    {
        if (!grid) return false;
        
        foreach (var c in cells)
        {
            var cell = new Vector3Int(c.x, c.y, level);
            
            if (!grid.CellInBounds(cell))
                return false;
            
            if (grid.CellHasAnyFlag(cell, WorldGrid.CellFlag.Occupied))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Register a platform at a set of grid cells on a given level.
    /// 
    /// markOccupiedInGrid controls ghost vs. permanent platforms:
    /// - false (GHOST/PREVIEW): Participates in adjacency for railing previews,
    ///                          but does NOT block placement in WorldGrid
    /// - true (PERMANENT): Reserves cells and fully participates in the game world
    /// 
    /// This dual-mode design enables live preview of connections before placement.
    /// </summary>
    public void RegisterPlatform(
        GamePlatform platform,
        List<Vector2Int> cells,
        bool markOccupiedInGrid = true)
    {
        if (!platform || cells == null || cells.Count == 0 || !grid) return;

        // Remove old occupancy if any
        if (_entries.TryGetValue(platform, out var oldEntry))
        {
            if (oldEntry.marksOccupied)
            {
                foreach (var cell2D in oldEntry.cells2D)
                {
                    var gridCell = new Vector3Int(cell2D.x, cell2D.y, 0);
                    grid.TryRemoveFlag(gridCell, WorldGrid.CellFlag.Occupied);
                }
            }
        }

        // Ensure we have an entry and are listening to pose changes
        if (!_entries.TryGetValue(platform, out var entry))
        {
            entry = new PlatformEntry { platform = platform };
            _entries[platform] = entry;
            platform.PoseChanged += OnPlatformPoseChanged;
        }
        
        entry.marksOccupied = markOccupiedInGrid;
        entry.cells2D.Clear();
        entry.cells2D.AddRange(cells);

        if (markOccupiedInGrid)
        {
            foreach (var cell2D in cells)
            {
                var gridCell = new Vector3Int(cell2D.x, cell2D.y, 0);
                grid.TryAddFlag(gridCell, WorldGrid.CellFlag.Occupied, platformId: 0, payload: platform.GetInstanceID());
            }
            
            // Build NavMesh for newly placed permanent platforms
            // (Skip for preview platforms where markOccupiedInGrid = false)
            platform.BuildLocalNavMesh();
            
            // Invoke UnityEvent for platform placed
            OnPlatformPlaced?.Invoke(platform);
        }

        // Mark adjacency for batched recomputation
        MarkAdjacencyDirty();
    }

    /// <summary>
    /// Marks adjacency as needing recomputation. Batched to LateUpdate for performance.
    /// Multiple pose changes in the same frame will only trigger one recomputation.
    /// </summary>
    private void MarkAdjacencyDirty()
    {
        _adjacencyDirty = true;
    }

    /// <summary>
    /// Public API for external systems to trigger adjacency recomputation.
    /// Used by BuildModeManager to update railing preview during placement.
    /// </summary>
    public void TriggerAdjacencyUpdate()
    {
        MarkAdjacencyDirty();
    }

    /// <summary>
    /// Removes platform occupancy from the grid and clears its connections.
    /// </summary>
    public void UnregisterPlatform(GamePlatform platform)
    {
        if (!platform || !grid) return;
        if (!_entries.TryGetValue(platform, out var entry)) return;

        platform.PoseChanged -= OnPlatformPoseChanged;

        if (entry.marksOccupied)
        {
            foreach (var cell2D in entry.cells2D)
            {
                var gridCell = new Vector3Int(cell2D.x, cell2D.y, 0);
                grid.TryRemoveFlag(gridCell, WorldGrid.CellFlag.Occupied);
            }
        }

        _entries.Remove(platform);

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
    
    /// <summary>
    /// Public method for checking if two platforms are adjacent using grid cells.
    /// Used by editor tools and runtime systems.
    /// </summary>
    public void ConnectPlatformsIfAdjacent(GamePlatform platformA, GamePlatform platformB)
    {
        if (!platformA || !platformB || !grid) return;
        
        if (!_entries.TryGetValue(platformA, out var entryA) || !_entries.TryGetValue(platformB, out var entryB))
        {
            // If platforms aren't registered, compute their cells
            if (!_entries.TryGetValue(platformA, out entryA))
            {
                _tmpCells2D.Clear();
                ComputeCellsForPlatform(platformA, _tmpCells2D);
                if (_tmpCells2D.Count > 0)
                {
                    RegisterPlatform(platformA, _tmpCells2D, markOccupiedInGrid: false);
                    entryA = _entries[platformA];
                }
                else return;
            }
            
            if (!_entries.TryGetValue(platformB, out entryB))
            {
                _tmpCells2D.Clear();
                ComputeCellsForPlatform(platformB, _tmpCells2D);
                if (_tmpCells2D.Count > 0)
                {
                    RegisterPlatform(platformB, _tmpCells2D, markOccupiedInGrid: false);
                    entryB = _entries[platformB];
                }
                else return;
            }
        }
        
        ConnectIfAdjacentByGridCells(platformA, entryA, platformB, entryB);
    }

    /// <summary>
    /// Check if two platforms are adjacent by comparing their grid cells.
    /// Two platforms are adjacent if any of their cells share an edge (not just a corner).
    /// Creates socket connections and NavMesh links where cells are adjacent.
    /// </summary>
    private void ConnectIfAdjacentByGridCells(GamePlatform a, PlatformEntry entryA, GamePlatform b, PlatformEntry entryB)
    {
        if (!a || !b || a == b) return;
        
        // Build sockets if needed (BuildSockets is safe to call multiple times)
        a.BuildSockets();
        b.BuildSockets();

        // Find edge-adjacent cell pairs
        var adjacentCellPairs = new List<(Vector2Int cellA, Vector2Int cellB)>();
        
        foreach (var cellA in entryA.cells2D)
        {
            Vector3Int gridCellA = new Vector3Int(cellA.x, cellA.y, 0);
            
            // Check all 4 edge neighbors of cellA
            var neighbors = new List<Vector3Int>();
            grid.GetNeighbors4(gridCellA, neighbors);
            
            foreach (var neighbor in neighbors)
            {
                Vector2Int neighbor2D = new Vector2Int(neighbor.x, neighbor.y);
                
                // Check if this neighbor is occupied by platform B
                if (entryB.cells2D.Contains(neighbor2D))
                {
                    // Found an edge-adjacent pair
                    if (!adjacentCellPairs.Contains((cellA, neighbor2D)) && 
                        !adjacentCellPairs.Contains((neighbor2D, cellA)))
                    {
                        adjacentCellPairs.Add((cellA, neighbor2D));
                    }
                }
            }
        }

        if (adjacentCellPairs.Count == 0)
            return;

        // Find sockets on the edges where cells are adjacent
        var aSocketIndices = new HashSet<int>();
        var bSocketIndices = new HashSet<int>();
        var connectionPositions = new List<Vector3>();

        // Match sockets by EXACT world position (with small tolerance for floating point errors)
        // Sockets are at 0.5m intervals, so we can't use grid cells (which are at 1m intervals)
        const float socketMatchDistance = 0.1f; // 10cm tolerance for floating point errors
        
        // Build socket position map for platform B (for fast lookup by rounded position)
        var bSocketsByRoundedPos = new Dictionary<Vector3Int, List<int>>();
        
        for (int i = 0; i < b.SocketCount; i++)
        {
            Vector3 worldPos = b.GetSocketWorldPosition(i);
            // Round to nearest 0.5m for bucketing (sockets are at 0.5m intervals)
            Vector3Int rounded = new Vector3Int(
                Mathf.RoundToInt(worldPos.x * 2f), // x2 to capture 0.5m precision
                Mathf.RoundToInt(worldPos.y * 2f),
                Mathf.RoundToInt(worldPos.z * 2f)
            );
            
            if (!bSocketsByRoundedPos.TryGetValue(rounded, out var list))
            {
                list = new List<int>();
                bSocketsByRoundedPos[rounded] = list;
            }
            list.Add(i);
        }
        
        // Check each socket on platform A for a match on platform B
        for (int i = 0; i < a.SocketCount; i++)
        {
            Vector3 worldPosA = a.GetSocketWorldPosition(i);
            Vector3Int roundedA = new Vector3Int(
                Mathf.RoundToInt(worldPosA.x * 2f),
                Mathf.RoundToInt(worldPosA.y * 2f),
                Mathf.RoundToInt(worldPosA.z * 2f)
            );
            
            // Check for matches in the same bucket
            if (bSocketsByRoundedPos.TryGetValue(roundedA, out var candidatesB))
            {
                foreach (int j in candidatesB)
                {
                    Vector3 worldPosB = b.GetSocketWorldPosition(j);
                    float distSqr = (worldPosA - worldPosB).sqrMagnitude;
                    
                    if (distSqr <= socketMatchDistance * socketMatchDistance)
                    {
                        // Exact match - these sockets should connect!
                        aSocketIndices.Add(i);
                        bSocketIndices.Add(j);
                        connectionPositions.Add(worldPosA);
                        break; // Only match each socket once
                    }
                }
            }
        }

        if (aSocketIndices.Count == 0 && bSocketIndices.Count == 0)
            return;

        // Apply connection visuals
        if (aSocketIndices.Count > 0)
            a.ApplyConnectionVisuals(aSocketIndices, true);
        if (bSocketIndices.Count > 0)
            b.ApplyConnectionVisuals(bSocketIndices, true);

        a.QueueRebuild();
        b.QueueRebuild();

        // Create NavMesh links at connection points
        if (connectionPositions.Count > 0)
        {
            Vector3 avgPosition = Vector3.zero;
            foreach (var pos in connectionPositions)
                avgPosition += pos;
            avgPosition /= connectionPositions.Count;

            // Create a link between the platforms at the average connection position
            if (aSocketIndices.Count > 0 && bSocketIndices.Count > 0)
            {
                // Calculate average positions of connected sockets
                Vector3 avgPosA = Vector3.zero;
                int countA = 0;
                foreach (int idx in aSocketIndices)
                {
                    avgPosA += a.GetSocketWorldPosition(idx);
                    countA++;
                }
                if (countA > 0) avgPosA /= countA;

                Vector3 avgPosB = Vector3.zero;
                int countB = 0;
                foreach (int idx in bSocketIndices)
                {
                    avgPosB += b.GetSocketWorldPosition(idx);
                    countB++;
                }
                if (countB > 0) avgPosB /= countB;

                GamePlatform.CreateNavLinkBetween(a, avgPosA, b, avgPosB, navLinkWidth);
            }
        }
    }


    /// <summary>
    /// Recomputes connections for ALL platforms (runtime),
    /// using grid cell adjacency checking:
    /// - Reset all connections/modules/railings
    /// - Check pairwise platform cell adjacency using WorldGrid
    /// This is batched to run once per frame maximum via LateUpdate.
    /// </summary>
    private void RecomputeAllAdjacency()
    {
        if (!grid) return;

        // Prevent recursive calls during adjacency computation
        if (_isRecomputingAdjacency) return;
        _isRecomputingAdjacency = true;

        _tmpPlatforms.Clear();
        GamePlatform pickedUpPlatform = null;
        
        foreach (var gp in GamePlatform.AllPlatforms)
        {
            if (!gp) continue;
            if (!gp.isActiveAndEnabled) continue;
            
            if (gp.IsPickedUp)
            {
                // Track picked-up platform for preview
                pickedUpPlatform = gp;
                continue;
            }
            
            _tmpPlatforms.Add(gp);
        }

        // Ensure registration is always valid
        foreach (var p in _tmpPlatforms)
        {
            p.EnsureChildrenModulesRegistered();
            p.EnsureChildrenRailingsRegistered();
        }

        // Reset everything first so rails reappear when platforms separate
        foreach (var p in _tmpPlatforms)
            p.EditorResetAllConnections();
        
        // IMPORTANT: Also reset picked-up platform so preview starts fresh each frame
        if (pickedUpPlatform != null)
            pickedUpPlatform.EditorResetAllConnections();

        // Try pairwise connections for currently touching platforms using grid cell adjacency
        int platformCount = _tmpPlatforms.Count;
        for (int platformIndexA = 0; platformIndexA < platformCount; platformIndexA++)
        {
            var platformA = _tmpPlatforms[platformIndexA];
            if (!_entries.TryGetValue(platformA, out var entryA))
            {
                Debug.LogWarning($"[PlatformManager] Platform '{platformA.name}' in _tmpPlatforms but NOT in _entries!");
                continue;
            }
            
            for (int platformIndexB = platformIndexA + 1; platformIndexB < platformCount; platformIndexB++)
            {
                var platformB = _tmpPlatforms[platformIndexB];
                if (!_entries.TryGetValue(platformB, out var entryB)) continue;
                
                // Check adjacency using grid cells
                ConnectIfAdjacentByGridCells(platformA, entryA, platformB, entryB);
            }
        }

        // Handle picked-up platform for railing PREVIEW ONLY
        // Does NOT mark cells as occupied, but updates socket statuses for visual feedback
        if (pickedUpPlatform != null)
        {
            // Compute cells for preview (doesn't register in grid)
            _tmpCells2D.Clear();
            ComputeCellsForPlatform(pickedUpPlatform, _tmpCells2D);
            
            if (_tmpCells2D.Count > 0)
            {
                // Create temporary entry for preview (NOT registered, marksOccupied = false)
                var previewEntry = new PlatformEntry
                {
                    platform = pickedUpPlatform,
                    marksOccupied = false // IMPORTANT: Does not occupy cells in grid
                };
                previewEntry.cells2D.AddRange(_tmpCells2D);
                
                // Check connections with all placed platforms for preview
                foreach (var placedPlatform in _tmpPlatforms)
                {
                    if (!_entries.TryGetValue(placedPlatform, out var placedEntry)) continue;
                    
                    // This updates socket statuses on BOTH platforms for preview
                    ConnectIfAdjacentByGridCells(pickedUpPlatform, previewEntry, placedPlatform, placedEntry);
                }
            }
        }

        _isRecomputingAdjacency = false;
    }
    
    #endregion

    #region Helper Methods
    
    /// <summary>
    /// Compute which 2D grid cells a platform covers on a given level,
    /// assuming its footprint is aligned to the 1x1 world grid AND
    /// that rotation is in 90° steps (0, 90, 180, 270).
    /// This is the single source of truth for runtime footprint.
    /// </summary>
    public void ComputeCellsForPlatform(GamePlatform platform, List<Vector2Int> outputCells)
    {
        outputCells.Clear();
        if (!platform || !grid) return;

        // Use platform's world position to find center cell on the desired level
        Vector3 worldPosition = platform.transform.position;
        var centerCell = grid.WorldToCellOnLevel(worldPosition, new Vector3Int(0, 0, 0));
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
                var gridCell = new Vector3Int(gridX, gridY, 0);
                if (!grid.CellInBounds(gridCell))
                    continue;

                outputCells.Add(new Vector2Int(gridX, gridY));
            }
        }
    }
    
    #endregion
}



