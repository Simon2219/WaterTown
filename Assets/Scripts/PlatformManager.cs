using System.Collections.Generic;
using Grid;
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
        public bool marksOccupied; // true = writes Occupied flags into WorldGrid (permanent) false = preview only
    }

    /// Single source of truth for all registered platforms and their data
    /// Maps each platform to its runtime game data (cells, occupancy state, etc)
    private readonly Dictionary<GamePlatform, PlatformGameData> _allPlatforms = new();
    
    /// Read-only access to all registered platforms
    public IReadOnlyCollection<GamePlatform> AllPlatforms => _allPlatforms.Keys;
    
    /// Reverse lookup: which platform occupies a given cell
    /// Only contains cells for platforms with marksOccupied = true
    private readonly Dictionary<Vector2Int, GamePlatform> _cellToPlatform = new();
    
    

    // Temp lists
    private static readonly List<GamePlatform> _tmpPlatforms = new();
    private readonly List<Vector2Int> _tmpCells = new();

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

            var tmpCells = new List<Vector2Int>();
            platform.occupiedCells = tmpCells;
            GetCellsForPlatform(platform, tmpCells);
            
            if (tmpCells.Count > 0)
            {
                // Registers occupancy AND triggers adjacency for all platforms
                RegisterPlatform(platform, markOccupiedInGrid: true);
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
    
    ///
    /// Event handler called when ANY platform becomes enabled
    /// Adds the platform to the global registry (without grid occupancy)
    /// Platform must call RegisterPlatform() separately to occupy grid cells
    ///
    private void OnPlatformEnabled(GamePlatform platform)
    {
        if (!platform) return;
        
        // Add to registry if not already present (without occupying grid)
        if (!_allPlatforms.ContainsKey(platform))
        {
            _allPlatforms[platform] = new PlatformGameData
            {
                marksOccupied = false // Not occupying grid yet (will be set by RegisterPlatform)
            };
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
    /// Registers platform in grid and triggers full adjacency computation
    ///
    private void HandlePlatformPlaced(GamePlatform platform)
    {
        if (!platform) return;
        RegisterPlatform(platform, markOccupiedInGrid: true);
    }
    
    ///
    /// Event handler called when ANY platform is picked up
    /// Triggers lightweight adjacency update without unregistering from grid
    ///
    private void OnPlatformPickedUp(GamePlatform platform)
    {
        if (!platform) return;
        
        // Just mark adjacency as dirty for lightweight update
        // Don't unregister - that's expensive and unnecessary
        MarkAdjacencyDirty();
    }
    
    ///
    /// Called when a platform reports its transform changed
    /// Lightweight update for runtime platform movement
    ///
    private void OnPlatformPoseChanged(GamePlatform platform)
    {
        if (!platform) return;
        
        // Use lightweight MovePlatform for pose changes (called every frame)
        MovePlatform(platform);
    }
    
    ///
    /// Lightweight platform movement update (for runtime pose changes)
    /// Updates grid occupancy and marks adjacency dirty without full rebuild
    /// This is called every frame when platform transform changes
    ///
    private void MovePlatform(GamePlatform platform)
    {
        if (!_allPlatforms.TryGetValue(platform, out PlatformGameData data)) return;

        // Clear old occupancy for this platform
        if (data.marksOccupied)
        {
            foreach (Vector2Int cell in data.cells)
            {
                // Remove from WorldGrid
                _worldGrid.TryRemoveFlag(cell, WorldGrid.CellFlag.Occupied);
                
                // Remove from reverse lookup
                _cellToPlatform.Remove(cell);
            }
        }

        // Compute new footprint cells from current transform (rotation-aware)
        var tmpCells = new List<Vector2Int>();
        GetCellsForPlatform(platform, tmpCells);

        data.cells.Clear();
        data.cells.AddRange(tmpCells);

        if (data.marksOccupied)
        {
            foreach (Vector2Int cell in data.cells)
            {
                // Add to WorldGrid
                _worldGrid.TryAddFlag(cell, WorldGrid.CellFlag.Occupied);
                
                // Add to reverse lookup
                _cellToPlatform[cell] = platform;
            }
        }
        
        // Mark adjacency as dirty for batched recomputation
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
    /// Register platform
    /// markOccupiedInGrid controls ghost vs permanent platforms:
    /// - false (GHOST): Participates in adjacency for railing previews
    ///                          but does NOT block placement in WorldGrid
    /// - true (PERMANENT): Reserves cells and fully participates in the game world
    /// 
    /// This dual-mode design enables live preview of connections before placement
    ///
    public void RegisterPlatform(
        GamePlatform platform,
        bool markOccupiedInGrid = true)
    {
        if (!platform || platform.occupiedCells == null || platform.occupiedCells.Count == 0) return;

        // Remove old occupancy if any
        if (_allPlatforms.TryGetValue(platform, out var oldData))
        {
            if (oldData.marksOccupied)
            {
                // Remove from WorldGrid and reverse lookup
                foreach (Vector2Int cell in oldData.cells)
                {
                    _worldGrid.TryRemoveFlag(cell, WorldGrid.CellFlag.Occupied);
                    _cellToPlatform.Remove(cell);
                }
            }
        }

        // Ensure we have game data and are listening to pose changes
        if (!_allPlatforms.TryGetValue(platform, out var data))
        {
            data = new PlatformGameData();
            _allPlatforms[platform] = data;
            platform.PoseChanged += OnPlatformPoseChanged;
        }
        
        data.marksOccupied = markOccupiedInGrid;
        data.cells.Clear();
        data.cells.AddRange(platform.occupiedCells);

        if (markOccupiedInGrid)
        {
            // Add to WorldGrid and reverse lookup
            foreach (Vector2Int cell in platform.occupiedCells)
            {
                _worldGrid.TryAddFlag(cell, WorldGrid.CellFlag.Occupied);
                _cellToPlatform[cell] = platform;
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
     
     
     
    /// Removes platform occupancy from the grid and clears its connections
    public void UnregisterPlatform(GamePlatform platform)
    {
        if (!platform) return;
        if (!_allPlatforms.TryGetValue(platform, out var data)) return;

        platform.PoseChanged -= OnPlatformPoseChanged;

        if (data.marksOccupied)
        {
            // Remove from WorldGrid and reverse lookup
            foreach (Vector2Int cell in data.cells)
            {
                _worldGrid.TryRemoveFlag(cell, WorldGrid.CellFlag.Occupied);
                _cellToPlatform.Remove(cell);
            }
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
    /// Public method for checking if two platforms are adjacent using grid cells
    /// Used by editor tools and runtime systems
    ///
    public void ConnectPlatformsIfAdjacent(GamePlatform platformA, GamePlatform platformB)
    {
        if (!platformA || !platformB) return;
        
        if (!_allPlatforms.TryGetValue(platformA, out var dataA) || !_allPlatforms.TryGetValue(platformB, out var dataB))
        {
            // If platforms aren't registered, compute their cells
            if (!_allPlatforms.TryGetValue(platformA, out dataA))
            {
                var cells = new List<Vector2Int>();
                GetCellsForPlatform(platformA, cells);
                if (cells.Count > 0)
                {
                    platformA.occupiedCells = cells;
                    RegisterPlatform(platformA, markOccupiedInGrid: false);
                    dataA = _allPlatforms[platformA];
                }
                else return;
            }
            
            if (!_allPlatforms.TryGetValue(platformB, out dataB))
            {
                var cells = new List<Vector2Int>();
                GetCellsForPlatform(platformB, cells);
                if (cells.Count > 0)
                {
                    platformB.occupiedCells = cells;
                    RegisterPlatform(platformB, markOccupiedInGrid: false);
                    dataB = _allPlatforms[platformB];
                }
                else return;
            }
        }
        
        ConnectIfAdjacentByGridCells(platformA, dataA, platformB, dataB);
    }

    ///
    /// Check if two platforms are adjacent by comparing their grid cells
    /// Two platforms are adjacent if any of their cells share an edge (not just a corner)
    /// Creates socket connections and NavMesh links where cells are adjacent
    ///
    private void ConnectIfAdjacentByGridCells(GamePlatform a, PlatformGameData dataA, GamePlatform b, PlatformGameData dataB)
    {
        if (!a || !b || a == b) return;
        
        // Build sockets if needed (BuildSockets is safe to call multiple times)
        a.BuildSockets();
        b.BuildSockets();

        // Find edge-adjacent cell pairs
        var adjacentCellPairs = new List<(Vector2Int cellA, Vector2Int cellB)>();
        
        foreach (var cellA in dataA.cells)
        {
            // Check all 4 edge neighbors of cellA
            List<Vector2Int> neighbors = _worldGrid.GetNeighbors4(cellA);
            
            foreach (var neighbor in neighbors)
            {
                // Check if this neighbor is occupied by platform B
                if (dataB.cells.Contains(neighbor))
                {
                    // Found an edge-adjacent pair
                    if (!adjacentCellPairs.Contains((cellA, neighbor)) && 
                        !adjacentCellPairs.Contains((neighbor, cellA)))
                    {
                        adjacentCellPairs.Add((cellA, neighbor));
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
        // Sockets are at 0.5m intervals so we can't use grid cells (which are at 1m intervals)
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
                        // Exact match - these sockets should connect
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


    ///
    /// Recomputes connections for ALL platforms (runtime)
    /// using grid cell adjacency checking:
    /// - Reset all connections/modules/railings
    /// - Check pairwise platform cell adjacency using WorldGrid
    /// This is batched to run once per frame maximum via LateUpdate
    ///
    private void RecomputeAllAdjacency()
    {
        // Prevent recursive calls during adjacency computation
        if (_isRecomputingAdjacency) return;
        _isRecomputingAdjacency = true;

            _tmpPlatforms.Clear();
            GamePlatform pickedUpPlatform = null;
            
            foreach (var gp in _allPlatforms.Keys)
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
            if (!_allPlatforms.TryGetValue(platformA, out var dataA))
            {
                Debug.LogWarning($"[PlatformManager] Platform '{platformA.name}' in scene but NOT registered in _allPlatforms!");
                continue;
            }
            
            for (int platformIndexB = platformIndexA + 1; platformIndexB < platformCount; platformIndexB++)
            {
                var platformB = _tmpPlatforms[platformIndexB];
                if (!_allPlatforms.TryGetValue(platformB, out var dataB)) continue;
                
                // Check adjacency using grid cells
                ConnectIfAdjacentByGridCells(platformA, dataA, platformB, dataB);
            }
        }

        // Handle picked-up platform for railing PREVIEW ONLY
        // Does NOT mark cells as occupied but updates socket statuses for visual feedback
        if (pickedUpPlatform != null)
        {
            // Compute cells for preview (doesn't register in grid)
            _tmpCells.Clear();
            GetCellsForPlatform(pickedUpPlatform, _tmpCells);
            
            if (_tmpCells.Count > 0)
            {
                // Create temporary data for preview (NOT registered, marksOccupied = false)
                var previewData = new PlatformGameData
                {
                    marksOccupied = false // IMPORTANT: Does not occupy cells in grid
                };
                previewData.cells.AddRange(_tmpCells);
                
                // Check connections with all placed platforms for preview
                foreach (var placedPlatform in _tmpPlatforms)
                {
                    if (!_allPlatforms.TryGetValue(placedPlatform, out var placedData)) continue;
                    
                    // This updates socket statuses on BOTH platforms for preview
                    ConnectIfAdjacentByGridCells(pickedUpPlatform, previewData, placedPlatform, placedData);
                }
            }
        }

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
    public void GetCellsForPlatform(GamePlatform platform, List<Vector2Int> outputCells)
    {
        outputCells.Clear();
        if (!platform) return;

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
    }
    
    #endregion
}



