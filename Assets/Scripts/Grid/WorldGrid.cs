// Assets/Scripts/Grid/WorldGrid.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using WaterTown.Core;

namespace Grid
{
    /// <summary>
    /// World grid foundation for a water-city builder.
    /// • 1×1 m cells (configurable via cellSize)
    /// • Integer levels (z in Vector3Int = level), spaced by levelStep meters
    /// • Dense storage [x, y, level]
    /// • Flags for lightweight state/tags; area operations for stamping/queries
    /// • Version counter increments on any mutating operation (for efficient visuals)
    /// • Lightweight change events for visualizers (optional: visualizers can still poll Version)
    /// </summary>
    [DisallowMultipleComponent]
    public class WorldGrid : MonoBehaviour
    {
        #region Configuration & Data Structures
        
        [Header("Grid Dimensions (cells)")]
        [Min(1)] public int sizeX = 128;    // columns (world X)
        [Min(1)] public int sizeY = 128;    // rows (world Z)
        [Min(1)] public int levels = 1;     // decks (0..levels-1)

        [Header("Metrics")]
        [Tooltip("Cell edge length in meters (1 => 1×1 m).")]
        public int cellSize = 1;
        [Tooltip("Vertical spacing between decks in meters.")]
        public int levelStep = 10;

        [Header("Origin")]
        [Tooltip("World-space origin of cell (0,0,0) lower-left corner.")]
        public Vector3 worldOrigin = Vector3.zero;

        // ---------- Storage [x, y, level] ----------
        private CellData[,,] _cells;

        /// <summary>Monotonic version stamp bumped on any mutating API call.</summary>
        public int Version { get; private set; }

        // ---------- Change events (optional; visuals can also poll Version) ----------
        public event Action StructureChanged;                    // allocation/size/origin/metrics changed
        public event Action<Vector3Int> CellChanged;             // single cell modified
        public event Action<Vector3Int, Vector3Int> AreaChanged; // inclusive [min..max] area modified

        [Serializable]
        public struct CellData
        {
            public CellFlag flags;   // bit-mask tags (Empty = none)
            public byte platformId;  // optional small group id (0 = none)
            public ushort reserved;  // keep for future binary stability
            public int payload;      // optional external index / id

            public bool IsEmpty => flags == CellFlag.Empty;
            public bool Has(CellFlag f) => (flags & f) != 0;
        }

        /// <summary>
        /// Bit flags: powers of two so they combine cleanly (Buildable | Locked).
        /// Keep mutually-exclusive concepts in separate fields.
        /// </summary>
        [Flags]
        public enum CellFlag
        {
            Empty     = 0,
            Locked    = 1 << 0,
            Buildable = 1 << 1,
            Occupied  = 1 << 2,
            // Extend as needed: Preview = 1<<3, ServiceZone = 1<<4, etc.
        }
        
        #endregion

        #region Lifecycle & Initialization
        
        // ---------- Lifecycle ----------

        private void Awake()
        {
            try
            {
                ValidateConfiguration();
                AllocateIfNeeded();
            }
            catch (Exception ex)
            {
                ErrorHandler.LogAndDisable(ex, this);
            }
        }
        
        /// <summary>
        /// Validates grid configuration values.
        /// Throws InvalidOperationException if configuration is invalid.
        /// </summary>
        private void ValidateConfiguration()
        {
            if (sizeX < 1 || sizeY < 1 || levels < 1)
            {
                throw ErrorHandler.InvalidConfiguration(
                    $"Grid dimensions must be at least 1 (current: {sizeX}x{sizeY}x{levels})", 
                    this
                );
            }
            
            if (cellSize < 1)
            {
                throw ErrorHandler.InvalidConfiguration(
                    $"Cell size must be at least 1 (current: {cellSize})", 
                    this
                );
            }
            
            if (levelStep < 1)
            {
                throw ErrorHandler.InvalidConfiguration(
                    $"Level step must be at least 1 (current: {levelStep})", 
                    this
                );
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            sizeX = Mathf.Max(1, sizeX);
            sizeY = Mathf.Max(1, sizeY);
            levels = Mathf.Max(1, levels);
            cellSize = Mathf.Max(1, cellSize);
            levelStep = Mathf.Max(1, levelStep);

            if (!Application.isPlaying)
                AllocateIfNeeded();
        }

        /// <summary>Editor-only method to apply inspector changes and reallocate grid.</summary>
        public void EditorApplySettings()
        {
            sizeX = Mathf.Max(1, sizeX);
            sizeY = Mathf.Max(1, sizeY);
            levels = Mathf.Max(1, levels);
            cellSize = Mathf.Max(1, cellSize);
            levelStep = Mathf.Max(1, levelStep);
            
            AllocateIfNeeded();
        }
#endif

        private void AllocateIfNeeded()
        {
            bool sizeChanged =
               _cells == null ||
               _cells.GetLength(0) != sizeX ||
               _cells.GetLength(1) != sizeY ||
               _cells.GetLength(2) != levels;

            if (sizeChanged)
            {
                _cells = new CellData[sizeX, sizeY, levels]; // default = cleared
                Version++; 
                StructureChanged?.Invoke();
            }
        }
        
        #endregion

        #region Coordinate Transforms & Bounds
        
        // ---------- Bounds & transforms ----------

        /// <summary>True if (x,y,level) lies inside the grid.</summary>
        public bool CellInBounds(Vector3Int cell)
        {
            return (uint)cell.x < (uint)sizeX
                && (uint)cell.y < (uint)sizeY
                && (uint)cell.z < (uint)levels;
        }

        /// <summary>True if worldPos projected onto level.z maps to a valid cell inside the grid.</summary>
        public bool WorldInBoundsOnLevel(Vector3 worldPos, Vector3Int levelRef)
        {
            var cell = WorldToCellOnLevel(worldPos, levelRef);
            return CellInBounds(cell);
        }

        /// <summary>Clamps a cell index to the grid extents (useful for safe math on indices).</summary>
        public Vector3Int ClampCell(Vector3Int cell)
        {
            return new Vector3Int(
                Mathf.Clamp(cell.x, 0, sizeX - 1),
                Mathf.Clamp(cell.y, 0, sizeY - 1),
                Mathf.Clamp(cell.z, 0, levels - 1)
            );
        }

        /// <summary>World Y coordinate for a given level (uses levelStep).</summary>
        public float GetLevelWorldY(int level)
        {
            level = Mathf.Clamp(level, 0, Mathf.Max(levels - 1, 0));
            return worldOrigin.y + level * (float)levelStep;
        }

        /// <summary>
        /// Converts world → cell (x,y,level) by inferring level from world Y (using levelStep).
        /// Floors edges so borders map to the lower/left cell.
        /// </summary>
        public Vector3Int WorldToCell(Vector3 worldPos)
        {
            var local = worldPos - worldOrigin;
            int x = Mathf.FloorToInt(local.x / (float)cellSize);
            int y = Mathf.FloorToInt(local.z / (float)cellSize);
            int level = Mathf.FloorToInt((worldPos.y - worldOrigin.y) / (float)levelStep);
            return new Vector3Int(x, y, level);
        }

        /// <summary>
        /// Projects a world position onto the plane of levelRef.z and returns (x,y,levelRef.z).
        /// Only levelRef.z is used.
        /// </summary>
        public Vector3Int WorldToCellOnLevel(Vector3 worldPos, Vector3Int levelRef)
        {
            int level = Mathf.Clamp(levelRef.z, 0, Mathf.Max(levels - 1, 0));
            var p = new Vector3(worldPos.x, GetLevelWorldY(level), worldPos.z);
            var local = p - worldOrigin;
            int x = Mathf.FloorToInt(local.x / (float)cellSize);
            int y = Mathf.FloorToInt(local.z / (float)cellSize);
            return new Vector3Int(x, y, level);
        }

        /// <summary>
        /// As WorldToCellOnLevel, but clamps to grid and returns whether the original was in bounds.
        /// Handy for mouse hover that should stay inside grid.
        /// </summary>
        public bool WorldToCellClamped(Vector3 worldPos, Vector3Int levelRef, out Vector3Int cell)
        {
            var raw = WorldToCellOnLevel(worldPos, levelRef);
            bool inBounds = CellInBounds(raw);
            cell = ClampCell(raw);
            return inBounds;
        }

        /// <summary>World center of a cell (uses levelStep for Y).</summary>
        public Vector3 GetCellCenter(Vector3Int cell)
        {
            return new Vector3(
                worldOrigin.x + (cell.x + 0.5f) * cellSize,
                GetLevelWorldY(cell.z),
                worldOrigin.z + (cell.y + 0.5f) * cellSize
            );
        }

        /// <summary>
        /// Converts a world position to a 2D grid cell coordinate (X, Y only).
        /// Used for socket matching - sockets at the same grid cell should connect.
        /// 
        /// IMPORTANT: Uses RoundToInt instead of FloorToInt (like WorldToCellOnLevel) because:
        /// - Sockets are positioned at 0.5m offsets (e.g., 39.5, 39.0) for edge midpoints
        /// - Flooring would cause corner sockets on different edges to map to same cell
        /// - Example: (39.5, 39.0) and (39.0, 39.5) would both floor to (39, 39)
        /// - Rounding keeps them distinct: (40, 39) and (39, 40) respectively
        /// </summary>
        public Vector2Int WorldToCell2D(Vector3 worldPos, int level)
        {
            int levelClamped = Mathf.Clamp(level, 0, Mathf.Max(levels - 1, 0));
            var local = worldPos - worldOrigin;
            
            // Use RoundToInt for socket matching (not FloorToInt like WorldToCellOnLevel)
            int x = Mathf.RoundToInt(local.x / (float)cellSize);
            int y = Mathf.RoundToInt(local.z / (float)cellSize);
            
            return new Vector2Int(x, y);
        }

        /// <summary>
        /// Snaps a world position to the nearest grid-aligned position for a platform with given footprint.
        /// Handles even vs odd footprints correctly:
        /// - Even footprints (4x4): snap to cell edges (whole meters like 44.0)
        /// - Odd footprints (3x3): snap to cell centers (half meters like 44.5)
        /// </summary>
        public Vector3 SnapToGridForPlatform(Vector3 worldPosition, Vector2Int footprint, int level)
        {
            bool evenWidth = (footprint.x % 2) == 0;
            bool evenHeight = (footprint.y % 2) == 0;
            
            float levelY = GetLevelWorldY(level);
            
            // For even dimensions, snap to cell edges (whole meters)
            // For odd dimensions, snap to cell centers (half meters)
            float snappedX;
            float snappedZ;
            
            if (evenWidth)
            {
                // Snap to whole meters (cell edges)
                snappedX = worldOrigin.x + Mathf.Round((worldPosition.x - worldOrigin.x) / cellSize) * cellSize;
            }
            else
            {
                // Snap to cell centers (half meters)
                snappedX = worldOrigin.x + (Mathf.Floor((worldPosition.x - worldOrigin.x) / cellSize) + 0.5f) * cellSize;
            }
            
            if (evenHeight)
            {
                // Snap to whole meters (cell edges)
                snappedZ = worldOrigin.z + Mathf.Round((worldPosition.z - worldOrigin.z) / cellSize) * cellSize;
            }
            else
            {
                // Snap to cell centers (half meters)
                snappedZ = worldOrigin.z + (Mathf.Floor((worldPosition.z - worldOrigin.z) / cellSize) + 0.5f) * cellSize;
            }
            
            return new Vector3(snappedX, levelY, snappedZ);
        }

        /// <summary>
        /// Snaps a world position to the nearest grid cell center on the specified level.
        /// </summary>
        public Vector3 SnapWorldPositionToGrid(Vector3 worldPosition, int level)
        {
            Vector3Int cell = WorldToCellOnLevel(worldPosition, new Vector3Int(0, 0, level));
            return GetCellCenter(cell);
        }

        /// <summary>
        /// Snaps a world position to the nearest grid cell center, inferring level from Y coordinate.
        /// </summary>
        public Vector3 SnapWorldPositionToGrid(Vector3 worldPosition)
        {
            Vector3Int cell = WorldToCell(worldPosition);
            return GetCellCenter(cell);
        }

        /// <summary>World-space axis-aligned bounds of a cell (thin in Y).</summary>
        public Bounds GetCellBounds(Vector3Int cell)
        {
            Vector3 center = GetCellCenter(cell);
            Vector3 size = new Vector3(cellSize, 0.01f, cellSize);
            return new Bounds(center, size);
        }

        /// <summary>Writes BL, BR, TR, TL corners of a cell to out4. Requires out4.Length ≥ 4.</summary>
        public void GetCellCorners(Vector3Int cell, Vector3[] out4)
        {
            if (out4 == null || out4.Length < 4)
            {
                throw new ArgumentException("out4 must be a non-null array of length ≥ 4.", nameof(out4));
            }

            float minX = worldOrigin.x + cell.x * (float)cellSize;
            float minZ = worldOrigin.z + cell.y * (float)cellSize;
            float maxX = minX + cellSize;
            float maxZ = minZ + cellSize;
            float h    = GetLevelWorldY(cell.z);

            out4[0] = new Vector3(minX, h, minZ); // BL
            out4[1] = new Vector3(maxX, h, minZ); // BR
            out4[2] = new Vector3(maxX, h, maxZ); // TR
            out4[3] = new Vector3(minX, h, maxZ); // TL
        }

        /// <summary>
        /// Cell-local to world. uv01 is (U,V) in [0..1] from the cell's lower-left corner.
        /// </summary>
        public Vector3 LocalToWorldInCell(Vector3Int cell, Vector2 uv01)
        {
            return new Vector3(
                worldOrigin.x + (cell.x + uv01.x) * cellSize,
                GetLevelWorldY(cell.z),
                worldOrigin.z + (cell.y + uv01.y) * cellSize
            );
        }

        /// <summary>
        /// World → (cell, uv01). Projects onto levelRef.z, returns the inside-cell UV [0..1].
        /// Only levelRef.z is used. Returns false if cell is out of bounds.
        /// </summary>
        public bool WorldToLocalInCell(Vector3 worldPos, Vector3Int levelRef, out Vector3Int cell, out Vector2 uv01)
        {
            cell = WorldToCellOnLevel(worldPos, levelRef);
            uv01 = default;
            if (!CellInBounds(cell)) return false;

            float x0 = worldOrigin.x + cell.x * (float)cellSize;
            float z0 = worldOrigin.z + cell.y * (float)cellSize;
            uv01 = new Vector2(
                Mathf.Clamp01((worldPos.x - x0) / (float)cellSize),
                Mathf.Clamp01((worldPos.z - z0) / (float)cellSize)
            );
            return true;
        }
        
        #endregion

        #region Raycast Helpers
        
        // ---------- Raycast helper ----------

        /// <summary>
        /// Raycast to plane(y = GetLevelWorldY(levelRef.z)), return hit world point + cell if inside.
        /// Only levelRef.z is used.
        /// </summary>
        public bool RaycastToCell(Ray worldRay, Vector3Int levelRef, out Vector3Int cell, out Vector3 hitPoint)
        {
            int level = Mathf.Clamp(levelRef.z, 0, Mathf.Max(levels - 1, 0));
            float h = GetLevelWorldY(level);
            var plane = new Plane(Vector3.up, new Vector3(0f, h, 0f));
            if (plane.Raycast(worldRay, out float dist))
            {
                hitPoint = worldRay.origin + worldRay.direction * dist;
                cell = WorldToCellOnLevel(hitPoint, new Vector3Int(0, 0, level));
                return CellInBounds(cell);
            }
            cell = default; hitPoint = default;
            return false;
        }
        
        #endregion

        #region Cell Data Access & Flags
        
        // ---------- Single-cell data & flags ----------

        /// <summary>Read cell data if in bounds.</summary>
        public bool TryGetCell(Vector3Int cell, out CellData data)
        {
            if (!CellInBounds(cell)) { data = default; return false; }
            data = _cells[cell.x, cell.y, cell.z];
            return true;
        }

        /// <summary>Write cell data if in bounds. Bumps Version and raises CellChanged.</summary>
        public void SetCell(Vector3Int cell, CellData data)
        {
            if (!CellInBounds(cell)) return;
            _cells[cell.x, cell.y, cell.z] = data;
            Version++;
            CellChanged?.Invoke(cell);
        }

        /// <summary>Clear cell (flags=Empty, payloads zero). Returns true if in bounds; bumps Version & raises CellChanged.</summary>
        public bool ClearCell(Vector3Int cell)
        {
            if (!CellInBounds(cell)) return false;
            _cells[cell.x, cell.y, cell.z] = default;
            Version++;
            CellChanged?.Invoke(cell);
            return true;
        }

        /// <summary>Add a flag to a single cell (in-bounds). Returns true on success; bumps Version & raises CellChanged.</summary>
        public bool TryAddFlag(Vector3Int cell, CellFlag flag, byte platformId = 0, int payload = 0)
        {
            if (!CellInBounds(cell)) return false;
            var cd = _cells[cell.x, cell.y, cell.z];
            cd.flags |= flag;
            if (platformId != 0) cd.platformId = platformId;
            if (payload   != 0) cd.payload    = payload;
            _cells[cell.x, cell.y, cell.z] = cd;
            Version++;
            CellChanged?.Invoke(cell);
            return true;
        }

        /// <summary>Remove a flag from a single cell (in-bounds). Returns true on success; bumps Version & raises CellChanged.</summary>
        public bool TryRemoveFlag(Vector3Int cell, CellFlag flag)
        {
            if (!CellInBounds(cell)) return false;
            var cd = _cells[cell.x, cell.y, cell.z];
            cd.flags &= ~flag;
            _cells[cell.x, cell.y, cell.z] = cd;
            Version++;
            CellChanged?.Invoke(cell);
            return true;
        }

        /// <summary>True if the cell has ALL bits in flags (in-bounds only).</summary>
        public bool CellHasAllFlags(Vector3Int cell, CellFlag flags)
        {
            return CellInBounds(cell) && (_cells[cell.x, cell.y, cell.z].flags & flags) == flags;
        }

        /// <summary>True if the cell has ANY bit in flags (in-bounds only).</summary>
        public bool CellHasAnyFlag(Vector3Int cell, CellFlag flags)
        {
            return CellInBounds(cell) && (_cells[cell.x, cell.y, cell.z].flags & flags) != 0;
        }
        
        #endregion

        #region Area Operations
        
        // ---------- Area helpers (two corners, inclusive, level = a.z) ----------

        private void ClampAreaInclusive(Vector3Int a, Vector3Int b, out Vector3Int min, out Vector3Int max)
        {
            int level = Mathf.Clamp(a.z, 0, Mathf.Max(levels - 1, 0));
            min = new Vector3Int(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), level);
            max = new Vector3Int(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y), level);

            min.x = Mathf.Clamp(min.x, 0, sizeX - 1);
            min.y = Mathf.Clamp(min.y, 0, sizeY - 1);
            max.x = Mathf.Clamp(max.x, 0, sizeX - 1);
            max.y = Mathf.Clamp(max.y, 0, sizeY - 1);
        }

        /// <summary>Iterate each cell (x,y,level) in an inclusive rectangle (level = a.z).</summary>
        public void ForEachCellInclusive(Vector3Int a, Vector3Int b, Action<Vector3Int> visit)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
                visit(new Vector3Int(x, y, min.z));
        }

        // ---------- Area operations (flags & queries) ----------

        /// <summary>Add (OR) flag to every cell in area. Optional platformId/payload when non-zero. Bumps Version & raises AreaChanged.</summary>
        public void AddFlagInArea(Vector3Int a, Vector3Int b, CellFlag flag, byte platformId = 0, int payload = 0)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var cd = _cells[x, y, min.z];
                cd.flags |= flag;
                if (platformId != 0) cd.platformId = platformId;
                if (payload   != 0) cd.payload    = payload;
                _cells[x, y, min.z] = cd;
            }
            Version++;
            AreaChanged?.Invoke(min, max);
        }

        /// <summary>Replace flags in area with exactFlags (overwrites existing flags). Bumps Version & raises AreaChanged.</summary>
        public void SetFlagsInAreaExact(Vector3Int a, Vector3Int b, CellFlag exactFlags, byte platformId = 0, int payload = 0)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var cd = _cells[x, y, min.z];
                cd.flags = exactFlags;
                if (platformId != 0) cd.platformId = platformId;
                if (payload   != 0) cd.payload    = payload;
                _cells[x, y, min.z] = cd;
            }
            Version++;
            AreaChanged?.Invoke(min, max);
        }

        /// <summary>Remove (AND NOT) flag from every cell in area. Bumps Version & raises AreaChanged.</summary>
        public void RemoveFlagInArea(Vector3Int a, Vector3Int b, CellFlag flag)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var cd = _cells[x, y, min.z];
                cd.flags &= ~flag;
                _cells[x, y, min.z] = cd;
            }
            Version++;
            AreaChanged?.Invoke(min, max);
        }

        /// <summary>Clear cells (set default) within area. Bumps Version & raises AreaChanged.</summary>
        public void ClearArea(Vector3Int a, Vector3Int b)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
                _cells[x, y, min.z] = default;
            Version++;
            AreaChanged?.Invoke(min, max);
        }

        /// <summary>True if every cell in area has ALL bits in flags.</summary>
        public bool AreaHasAllFlags(Vector3Int a, Vector3Int b, CellFlag flags)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
                if ((_cells[x, y, min.z].flags & flags) != flags)
                    return false;
            return true;
        }

        /// <summary>True if any cell in area has ANY bit in flags.</summary>
        public bool AreaHasAnyFlag(Vector3Int a, Vector3Int b, CellFlag flags)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
                if ((_cells[x, y, min.z].flags & flags) != 0)
                    return true;
            return false;
        }

        /// <summary>Fill list with all cells inside area (clears list first).</summary>
        public void GetCellsInArea(Vector3Int a, Vector3Int b, List<Vector3Int> into)
        {
            into.Clear();
            ClampAreaInclusive(a, b, out var min, out var max);
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
                into.Add(new Vector3Int(x, y, min.z));
        }

        /// <summary>Count cells in area with ALL bits in flags.</summary>
        public int CountCellsWithAllFlags(Vector3Int a, Vector3Int b, CellFlag flags)
        {
            int count = 0;
            ClampAreaInclusive(a, b, out var min, out var max);
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
                if ((_cells[x, y, min.z].flags & flags) == flags) count++;
            return count;
        }

        /// <summary>Count cells in area with ANY bit in flags.</summary>
        public int CountCellsWithAnyFlag(Vector3Int a, Vector3Int b, CellFlag flags)
        {
            int count = 0;
            ClampAreaInclusive(a, b, out var min, out var max);
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
                if ((_cells[x, y, min.z].flags & flags) != 0) count++;
            return count;
        }
        
        #endregion

        #region Neighbor Queries
        
        // ---------- Neighbors (pathing/adjacency) ----------

        public void GetNeighbors4(Vector3Int cell, List<Vector3Int> into)
        {
            into.Clear();
            var n = new Vector3Int(cell.x + 1, cell.y, cell.z); if (CellInBounds(n)) into.Add(n);
            n.x = cell.x - 1;                                     if (CellInBounds(n)) into.Add(n);
            n.x = cell.x; n.y = cell.y + 1;                       if (CellInBounds(n)) into.Add(n);
            n.y = cell.y - 1;                                     if (CellInBounds(n)) into.Add(n);
        }

        public void GetNeighbors8(Vector3Int cell, List<Vector3Int> into)
        {
            into.Clear();
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var n = new Vector3Int(cell.x + dx, cell.y + dy, cell.z);
                if (CellInBounds(n)) into.Add(n);
            }
        }

        /// <summary>
        /// Gets the world position of the edge center between two adjacent cells.
        /// Returns the midpoint between the centers of cellA and cellB.
        /// </summary>
        public Vector3 GetEdgeCenterBetweenCells(Vector3Int cellA, Vector3Int cellB)
        {
            Vector3 centerA = GetCellCenter(cellA);
            Vector3 centerB = GetCellCenter(cellB);
            return (centerA + centerB) * 0.5f;
        }

        /// <summary>
        /// Gets all edge-adjacent cells (4-directional neighbors) that are occupied by the specified flag.
        /// Useful for finding connected platforms.
        /// </summary>
        public void GetOccupiedNeighbors(Vector3Int cell, CellFlag flag, List<Vector3Int> occupiedNeighbors)
        {
            occupiedNeighbors.Clear();
            List<Vector3Int> neighbors = new List<Vector3Int>();
            GetNeighbors4(cell, neighbors);
            
            foreach (var neighbor in neighbors)
            {
                if (CellHasAnyFlag(neighbor, flag))
                {
                    occupiedNeighbors.Add(neighbor);
                }
            }
        }
        
        #endregion
    }
}
