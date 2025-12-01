using System.Collections.Generic;
using Grid;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using WaterTown.Platforms;

namespace WaterTown.Town
{
    [DisallowMultipleComponent]
    public class TownManager : MonoBehaviour
    {
        [Header("Grid")]
        [SerializeField] private WorldGrid grid;
        public WorldGrid Grid => grid;

        [Header("Default Level")]
        [Tooltip("Level index to use when auto-computing a platform's cells.")]
        [SerializeField] private int defaultLevel = 0;

        [Header("NavMesh Link Settings")]
        [Tooltip("Width of NavMesh links created between connected platforms (meters).")]
        [SerializeField] private float navLinkWidth = 0.6f;

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

            // 3) Recompute adjacency for ALL platforms (global pass, same as SceneView tool)
            RecomputeAllAdjacency();
        }

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
                if (!grid.CellInBounds(cell)) return false;
                if (grid.CellHasAnyFlag(cell, WorldGrid.CellFlag.Occupied))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Register a platform at a set of grid cells on a given level.
        /// markOccupiedInGrid=false is used for the GHOST (preview): it still participates
        /// in adjacency, but does NOT reserve cells in the WorldGrid.
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
            }

            // Recompute adjacency for all platforms
            RecomputeAllAdjacency();
        }

        /// <summary>
        /// Helper for preview: register platform using its current transform footprint,
        /// but WITHOUT marking cells as Occupied in the grid.
        /// The ghost still participates in adjacency (railing preview).
        /// </summary>
        public void RegisterPreviewPlatform(GamePlatform platform, int level = 0)
        {
            if (!platform || !grid) return;

            _tmpCells2D.Clear();
            ComputeCellsForPlatform(platform, level, _tmpCells2D);
            if (_tmpCells2D.Count == 0) return;

            RegisterPlatform(platform, _tmpCells2D, level, markOccupiedInGrid: false);
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

            // Recompute adjacency for all remaining platforms
            RecomputeAllAdjacency();
        }

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

            foreach (var (cellA, cellB) in adjacentCellPairs)
            {
                // Find the edge between these two cells
                int deltaX = cellB.x - cellA.x;
                int deltaY = cellB.y - cellA.y;
                
                // Determine which edge of cellA faces cellB
                GamePlatform.Edge edgeA = GamePlatform.Edge.North;
                if (deltaX == 1 && deltaY == 0) edgeA = GamePlatform.Edge.East;
                else if (deltaX == -1 && deltaY == 0) edgeA = GamePlatform.Edge.West;
                else if (deltaX == 0 && deltaY == 1) edgeA = GamePlatform.Edge.North;
                else if (deltaX == 0 && deltaY == -1) edgeA = GamePlatform.Edge.South;

                GamePlatform.Edge edgeB = GamePlatform.Edge.North;
                if (deltaX == 1 && deltaY == 0) edgeB = GamePlatform.Edge.West; // B is east of A, so B's west faces A
                else if (deltaX == -1 && deltaY == 0) edgeB = GamePlatform.Edge.East;
                else if (deltaX == 0 && deltaY == 1) edgeB = GamePlatform.Edge.South;
                else if (deltaX == 0 && deltaY == -1) edgeB = GamePlatform.Edge.North;

                // Find sockets on these edges that are closest to the cell boundary
                // For simplicity, find the socket closest to the center of the shared edge
                Vector3 cellACenter = grid.GetCellCenter(new Vector3Int(cellA.x, cellA.y, entryA.level));
                Vector3 cellBCenter = grid.GetCellCenter(new Vector3Int(cellB.x, cellB.y, entryB.level));
                Vector3 edgeCenter = (cellACenter + cellBCenter) * 0.5f;
                
                // Find nearest socket on platform A's edge
                int socketA = FindNearestSocketOnEdge(a, edgeA, edgeCenter);
                if (socketA >= 0) aSocketIndices.Add(socketA);
                
                // Find nearest socket on platform B's edge  
                int socketB = FindNearestSocketOnEdge(b, edgeB, edgeCenter);
                if (socketB >= 0) bSocketIndices.Add(socketB);
                
                connectionPositions.Add(edgeCenter);
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
        /// Find the socket index on a platform's edge that is closest to a world position.
        /// </summary>
        private int FindNearestSocketOnEdge(GamePlatform platform, GamePlatform.Edge edge, Vector3 worldPosition)
        {
            platform.GetSocketIndexRangeForEdge(edge, out int startIndex, out int endIndex);
            
            float bestDistance = float.MaxValue;
            int bestSocketIndex = -1;
            
            for (int socketIndex = startIndex; socketIndex <= endIndex && socketIndex < platform.SocketCount; socketIndex++)
            {
                Vector3 socketWorldPosition = platform.GetSocketWorldPosition(socketIndex);
                float distanceSquared = Vector3.SqrMagnitude(socketWorldPosition - worldPosition);
                if (distanceSquared < bestDistance)
                {
                    bestDistance = distanceSquared;
                    bestSocketIndex = socketIndex;
                }
            }
            
            return bestSocketIndex;
        }

        /// <summary>
        /// Recomputes connections for ALL platforms (runtime),
        /// using grid cell adjacency checking:
        /// - Reset all connections/modules/railings
        /// - Check pairwise platform cell adjacency using WorldGrid
        /// </summary>
        private void RecomputeAllAdjacency()
        {
            if (!grid) return;

            _tmpPlatforms.Clear();
            foreach (var gp in GamePlatform.AllPlatforms)
            {
                if (!gp) continue;
                if (!gp.isActiveAndEnabled) continue;
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
        }

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

            // Use platform's world position to find a "center" cell on the desired level
            Vector3 worldCenter = platform.transform.position;
            var centerCell = grid.WorldToCellOnLevel(worldCenter, new Vector3Int(0, 0, level));
            int centerX = centerCell.x;
            int centerY = centerCell.y;

            int footprintWidth = Mathf.Max(1, platform.Footprint.x);
            int footprintHeight = Mathf.Max(1, platform.Footprint.y);

            // Determine rotation in 90° steps (0..3)
            float yaw = platform.transform.eulerAngles.y;
            int rotationSteps = Mathf.RoundToInt(yaw / 90f) & 3;
            bool isRotated90Or270 = (rotationSteps % 2) == 1; // 90 or 270

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

    }
}

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
        /// Find the socket index on a platform's edge that is closest to a world position.
        /// </summary>
        private int FindNearestSocketOnEdge(GamePlatform platform, GamePlatform.Edge edge, Vector3 worldPosition)
        {
            platform.GetSocketIndexRangeForEdge(edge, out int startIndex, out int endIndex);
            
            float bestDistance = float.MaxValue;
            int bestSocketIndex = -1;
            
            for (int socketIndex = startIndex; socketIndex <= endIndex && socketIndex < platform.SocketCount; socketIndex++)
            {
                Vector3 socketWorldPosition = platform.GetSocketWorldPosition(socketIndex);
                float distanceSquared = Vector3.SqrMagnitude(socketWorldPosition - worldPosition);
                if (distanceSquared < bestDistance)
                {
                    bestDistance = distanceSquared;
                    bestSocketIndex = socketIndex;
                }
            }
            
            return bestSocketIndex;
        }

        /// <summary>
        /// Recomputes connections for ALL platforms (runtime),
        /// using grid cell adjacency checking:
        /// - Reset all connections/modules/railings
        /// - Check pairwise platform cell adjacency using WorldGrid
        /// </summary>
        private void RecomputeAllAdjacency()
        {
            if (!grid) return;

            _tmpPlatforms.Clear();
            foreach (var gp in GamePlatform.AllPlatforms)
            {
                if (!gp) continue;
                if (!gp.isActiveAndEnabled) continue;
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
        }

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

            // Use platform's world position to find a "center" cell on the desired level
            Vector3 worldCenter = platform.transform.position;
            var centerCell = grid.WorldToCellOnLevel(worldCenter, new Vector3Int(0, 0, level));
            int centerX = centerCell.x;
            int centerY = centerCell.y;

            int footprintWidth = Mathf.Max(1, platform.Footprint.x);
            int footprintHeight = Mathf.Max(1, platform.Footprint.y);

            // Determine rotation in 90° steps (0..3)
            float yaw = platform.transform.eulerAngles.y;
            int rotationSteps = Mathf.RoundToInt(yaw / 90f) & 3;
            bool isRotated90Or270 = (rotationSteps % 2) == 1; // 90 or 270

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

    }
}

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
        /// Find the socket index on a platform's edge that is closest to a world position.
        /// </summary>
        private int FindNearestSocketOnEdge(GamePlatform platform, GamePlatform.Edge edge, Vector3 worldPosition)
        {
            platform.GetSocketIndexRangeForEdge(edge, out int startIndex, out int endIndex);
            
            float bestDistance = float.MaxValue;
            int bestSocketIndex = -1;
            
            for (int socketIndex = startIndex; socketIndex <= endIndex && socketIndex < platform.SocketCount; socketIndex++)
            {
                Vector3 socketWorldPosition = platform.GetSocketWorldPosition(socketIndex);
                float distanceSquared = Vector3.SqrMagnitude(socketWorldPosition - worldPosition);
                if (distanceSquared < bestDistance)
                {
                    bestDistance = distanceSquared;
                    bestSocketIndex = socketIndex;
                }
            }
            
            return bestSocketIndex;
        }

        /// <summary>
        /// Recomputes connections for ALL platforms (runtime),
        /// using grid cell adjacency checking:
        /// - Reset all connections/modules/railings
        /// - Check pairwise platform cell adjacency using WorldGrid
        /// </summary>
        private void RecomputeAllAdjacency()
        {
            if (!grid) return;

            _tmpPlatforms.Clear();
            foreach (var gp in GamePlatform.AllPlatforms)
            {
                if (!gp) continue;
                if (!gp.isActiveAndEnabled) continue;
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
        }

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

            // Use platform's world position to find a "center" cell on the desired level
            Vector3 worldCenter = platform.transform.position;
            var centerCell = grid.WorldToCellOnLevel(worldCenter, new Vector3Int(0, 0, level));
            int centerX = centerCell.x;
            int centerY = centerCell.y;

            int footprintWidth = Mathf.Max(1, platform.Footprint.x);
            int footprintHeight = Mathf.Max(1, platform.Footprint.y);

            // Determine rotation in 90° steps (0..3)
            float yaw = platform.transform.eulerAngles.y;
            int rotationSteps = Mathf.RoundToInt(yaw / 90f) & 3;
            bool isRotated90Or270 = (rotationSteps % 2) == 1; // 90 or 270

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

    }
}
