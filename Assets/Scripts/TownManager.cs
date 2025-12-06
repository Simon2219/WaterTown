using System.Collections.Generic;
using Grid;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;
using WaterTown.Platforms;

namespace WaterTown.Town
{
    [DisallowMultipleComponent]
    public class TownManager : MonoBehaviour
    {
        #region Configuration & Constants
        
        // ---------- Constants ----------
        private const int ROTATION_STEP_DEGREES = 90;
        private const int ROTATION_MODULO_MASK = 3; // 0-3 for 4 cardinal directions (0°, 90°, 180°, 270°)

        [Header("Grid")]
        [SerializeField] private WorldGrid grid;
        public WorldGrid Grid => grid;

        [Header("Default Level")]
        [Tooltip("Level index to use when auto-computing a platform's cells.")]
        [SerializeField] private int defaultLevel = 0;

        [Header("NavMesh Link Settings")]
        [Tooltip("Width of NavMesh links created between connected platforms (meters).")]
        [SerializeField] private float navLinkWidth = 0.6f;

        [Header("Events")]
        [Tooltip("Invoked when a platform is successfully placed and registered.")]
        public UnityEvent OnPlatformPlaced = new UnityEvent();
        
        [Tooltip("Invoked when a platform is removed/unregistered.")]
        public UnityEvent OnPlatformRemoved = new UnityEvent();
        
        // Cached reference to last placed/removed platform for event handlers
        public GamePlatform LastPlacedPlatform { get; private set; }
        public GamePlatform LastRemovedPlatform { get; private set; }

        // --- Platform occupancy bookkeeping ---

        private class PlatformEntry
        {
            public GamePlatform platform;
            public List<Vector2Int> cells2D = new();
            public int level;
            public bool marksOccupied; // true = we write Occupied flags into the grid
        }

        // Platforms we know about → their occupied cells
        private readonly Dictionary<GamePlatform, PlatformEntry> _entries = new();

        // temp lists
        private static readonly List<GamePlatform> _tmpPlatforms = new();
        private readonly List<Vector2Int> _tmpCells2D = new();

        // Adjacency recomputation batching (performance optimization)
        private bool _adjacencyDirty = false;
        private bool _isRecomputingAdjacency = false;
        
        #endregion

        #region Unity Lifecycle
        
        // ---------- Unity lifecycle ----------

        private void Awake()
        {
            // Only auto-find if nothing is wired in the inspector
            if (!grid)
                grid = FindFirstObjectByType<WorldGrid>();
        }

        private void OnEnable()
        {
            GamePlatform.PlatformRegistered   += OnPlatformRegistered;
            GamePlatform.PlatformUnregistered += OnPlatformUnregistered;
        }

        private void Start()
        {
            // Ensure all existing platforms in the scene are registered into the grid
            if (!grid)
                grid = FindFirstObjectByType<WorldGrid>();

            foreach (var platform in GamePlatform.AllPlatforms)
            {
                if (!platform) continue;
                if (!platform.isActiveAndEnabled) continue;

                _tmpCells2D.Clear();
                ComputeCellsForPlatform(platform, defaultLevel, _tmpCells2D);
                if (_tmpCells2D.Count > 0)
                {
                    // Registers occupancy AND triggers adjacency for all platforms
                    RegisterPlatform(platform, _tmpCells2D, defaultLevel, markOccupiedInGrid: true);
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
        
        // ---------- GamePlatform events ----------

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

            int level = entry.level;
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
            ComputeCellsForPlatform(platform, level, _tmpCells2D);

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
        
        // ---------- Public API ----------

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
            int level = 0,
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
                        var gridCell = new Vector3Int(cell2D.x, cell2D.y, oldEntry.level);
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

            entry.level         = level;
            entry.marksOccupied = markOccupiedInGrid;
            entry.cells2D.Clear();
            entry.cells2D.AddRange(cells);

            if (markOccupiedInGrid)
            {
                foreach (var cell2D in cells)
                {
                    var gridCell = new Vector3Int(cell2D.x, cell2D.y, level);
                    grid.TryAddFlag(gridCell, WorldGrid.CellFlag.Occupied, platformId: 0, payload: platform.GetInstanceID());
                }
                
                // Build NavMesh for newly placed permanent platforms
                // (Skip for preview platforms where markOccupiedInGrid = false)
                platform.BuildLocalNavMesh();
                
                // Invoke UnityEvent for designer-facing hooks
                LastPlacedPlatform = platform;
                OnPlatformPlaced?.Invoke();
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
                    var gridCell = new Vector3Int(cell2D.x, cell2D.y, entry.level);
                    grid.TryRemoveFlag(gridCell, WorldGrid.CellFlag.Occupied);
                }
            }

            _entries.Remove(platform);

            // Clear connections on this platform
            platform.EditorResetAllConnections();

            // Invoke UnityEvent for designer-facing hooks
            LastRemovedPlatform = platform;
            OnPlatformRemoved?.Invoke();

            // Mark adjacency for batched recomputation
            MarkAdjacencyDirty();
        }
        
        #endregion

        #region Adjacency System (Grid-Based)
        
        // ---------- Core adjacency logic (GLOBAL) ----------

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
                    ComputeCellsForPlatform(platformA, defaultLevel, _tmpCells2D);
                    if (_tmpCells2D.Count > 0)
                    {
                        RegisterPlatform(platformA, _tmpCells2D, defaultLevel, markOccupiedInGrid: false);
                        entryA = _entries[platformA];
                    }
                    else return;
                }
                
                if (!_entries.TryGetValue(platformB, out entryB))
                {
                    _tmpCells2D.Clear();
                    ComputeCellsForPlatform(platformB, defaultLevel, _tmpCells2D);
                    if (_tmpCells2D.Count > 0)
                    {
                        RegisterPlatform(platformB, _tmpCells2D, defaultLevel, markOccupiedInGrid: false);
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
            if (entryA.level != entryB.level) return; // Must be on same level
            
            // Build sockets if needed (BuildSockets is safe to call multiple times)
            a.BuildSockets();
            b.BuildSockets();

            // Find edge-adjacent cell pairs
            var adjacentCellPairs = new List<(Vector2Int cellA, Vector2Int cellB)>();
            
            foreach (var cellA in entryA.cells2D)
            {
                Vector3Int gridCellA = new Vector3Int(cellA.x, cellA.y, entryA.level);
                
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

            // Match sockets by EXACT world position
            // Sockets are at cell edges (e.g., x=42.5, z=46.0), so we need precise matching
            // Round to 0.5m to handle floating point precision issues
            
            // Build socket position map for platform B (for fast lookup)
            var bSocketsByPosition = new Dictionary<Vector3, int>();
            for (int i = 0; i < b.SocketCount; i++)
            {
                Vector3 worldPos = b.GetSocketWorldPosition(i);
                Vector3 rounded = new Vector3(
                    Mathf.Round(worldPos.x * 2f) / 2f,
                    Mathf.Round(worldPos.y * 2f) / 2f,
                    Mathf.Round(worldPos.z * 2f) / 2f
                );
                
                if (!bSocketsByPosition.ContainsKey(rounded))
                    bSocketsByPosition[rounded] = i;
            }
            
            // Check each socket on platform A for a match on platform B
            for (int i = 0; i < a.SocketCount; i++)
            {
                Vector3 worldPos = a.GetSocketWorldPosition(i);
                Vector3 rounded = new Vector3(
                    Mathf.Round(worldPos.x * 2f) / 2f,
                    Mathf.Round(worldPos.y * 2f) / 2f,
                    Mathf.Round(worldPos.z * 2f) / 2f
                );
                
                if (bSocketsByPosition.TryGetValue(rounded, out int matchingSocketB))
                {
                    // Exact match - these sockets should connect!
                    aSocketIndices.Add(i);
                    bSocketIndices.Add(matchingSocketB);
                    connectionPositions.Add(worldPos);
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

            // Try pairwise connections for currently touching platforms using grid cell adjacency
            int platformCount = _tmpPlatforms.Count;
            for (int platformIndexA = 0; platformIndexA < platformCount; platformIndexA++)
            {
                var platformA = _tmpPlatforms[platformIndexA];
                if (!_entries.TryGetValue(platformA, out var entryA)) continue;
                
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
                ComputeCellsForPlatform(pickedUpPlatform, defaultLevel, _tmpCells2D);
                
                if (_tmpCells2D.Count > 0)
                {
                    // Create temporary entry for preview (NOT registered, marksOccupied = false)
                    var previewEntry = new PlatformEntry
                    {
                        platform = pickedUpPlatform,
                        level = defaultLevel,
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
        
        // ---------- Helpers ----------

        /// <summary>
        /// Compute which 2D grid cells a platform covers on a given level,
        /// assuming its footprint is aligned to the 1x1 world grid AND
        /// that rotation is in 90° steps (0, 90, 180, 270).
        /// This is the single source of truth for runtime footprint.
        /// </summary>
        public void ComputeCellsForPlatform(GamePlatform platform, int level, List<Vector2Int> outputCells)
        {
            outputCells.Clear();
            if (!platform || !grid) return;

            // Use platform's world position to find center cell on the desired level
            Vector3 worldPosition = platform.transform.position;
            var centerCell = grid.WorldToCellOnLevel(worldPosition, new Vector3Int(0, 0, level));
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
                    var gridCell = new Vector3Int(gridX, gridY, level);
                    if (!grid.CellInBounds(gridCell))
                        continue;

                    outputCells.Add(new Vector2Int(gridX, gridY));
                }
            }
        }
        
        #endregion

    }
}

