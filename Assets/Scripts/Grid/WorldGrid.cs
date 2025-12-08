// Assets/Scripts/Grid/WorldGrid.cs
using System;
using System.Collections.Generic;
using Unity.Entities.UniversalDelegates;
using UnityEngine;

namespace Grid
{
    ///
    /// World grid foundation for a water-city builder
    /// • 1×1 m cells (configurable via cellSize)
    /// • Integer levels (z in Vector3Int = level), spaced by levelStep meters
    /// • Dense storage [x, y, level]
    /// • Flags for lightweight state/tags; area operations for stamping/queries
    /// • Version counter increments on any mutating operation (for efficient visuals)
    /// • Lightweight change events for visualizers (optional: visualizers can still poll Version)
    ///
    [DisallowMultipleComponent]
    public class WorldGrid : MonoBehaviour
    {
        #region Configuration & Data Structures

        [Header("Grid Dimensions (cells)")]
        
        [Min(1)] public int sizeX = 128;    // columns (world X)
        [Min(1)] public int sizeY = 128;    // rows (world Z)
        [Min(1)] public int levels = 1;     // decks (0..levels-1)

        [Header("Origin")]
        
        [Tooltip("World-space origin of cell (0,0,0) lower-left corner.")]
        public Vector3 worldOrigin { get; private set; } = Vector3.zero;

        public const int CellSize = 1;

        // Storage [x, y, level]
        private CellData[,] _cells;

        /// Monotonic version stamp bumped on any mutating API call
        public int Version { get; private set; }

        // Change events (optional visuals can also poll Version)

        public event Action<Vector2Int> CellChanged;             // single cell modified
        public event Action<Vector2Int, Vector2Int> AreaChanged; // inclusive [min..max] area modified

        
        
        [Serializable]
        public struct CellData
        {
            public CellFlag flags;   // bit-mask tags (Empty = none)

            public bool IsEmpty => flags == CellFlag.Empty;
            public bool HasFlag(CellFlag f) => (flags & f) != 0;
        }

        
        /// Bit flags: powers of two so they combine cleanly (Buildable | Locked).
        /// Keep mutually-exclusive concepts in separate fields.
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
        

#if UNITY_EDITOR
        private void OnValidate()
        {
            sizeX = Mathf.Max(1, sizeX);
            sizeY = Mathf.Max(1, sizeY);
            levels = Mathf.Max(1, levels);

            if (!Application.isPlaying)
                AllocateIfNeeded();
        }

        /// Editor-only method to apply inspector changes and reallocate grid.
        public void EditorApplySettings()
        {
            sizeX = Mathf.Max(1, sizeX);
            sizeY = Mathf.Max(1, sizeY);
            levels = Mathf.Max(1, levels);
            
            AllocateIfNeeded();
        }
#endif


        /// Validates grid configuration values.
        /// Throws InvalidOperationException if configuration is invalid.
        private void ValidateConfiguration()
        {
            if (sizeX < 1 || sizeY < 1 || levels < 1)
            {
                throw ErrorHandler.InvalidConfiguration(
                    $"Grid dimensions must be at least 1 (current: {sizeX}x{sizeY}x{levels})", 
                    this
                );
            }
            
            if (CellSize < 1)
            {
                throw ErrorHandler.InvalidConfiguration(
                    $"Cell size must be at least 1 (current: {CellSize})", 
                    this
                );
            }
        }
        
        private void AllocateIfNeeded()
        {
            bool sizeChanged =
               _cells == null ||
               _cells.GetLength(0) != sizeX ||
               _cells.GetLength(1) != sizeY ||
               _cells.GetLength(2) != levels;

            if (sizeChanged)
            {
                _cells = new CellData[sizeX, sizeY]; // default = cleared
                Version++; 
            }
        }
        
        #endregion

        #region Coordinate Transforms & Bounds
        
        // ---------- Bounds & transforms ----------

        
        /// True if (x,y,level) lies inside the grid.
        public bool CellInBounds(Vector2Int cell)
        {
            return (uint)cell.x < (uint)sizeX
                && (uint)cell.y < (uint)sizeY;
        }
        

        
        /// Clamps a cell index to the grid extents (useful for safe math on indices).
        public Vector2Int ClampCell(Vector2Int cell)
        {
            return new Vector2Int(
                Mathf.Clamp(cell.x, 0, sizeX - 1),
                Mathf.Clamp(cell.y, 0, sizeY - 1)
            );
        }
        
        
        /// Converts world → cell (x,y,level = 0)
        /// Floors edges so borders map to the lower/left cell.
        public Vector2Int WorldToCell(Vector3 worldPos)
        {
            var local = worldPos - worldOrigin;
            int x = Mathf.FloorToInt(local.x / (float)CellSize);
            int y = Mathf.FloorToInt(local.z / (float)CellSize);
            return new Vector2Int(x, y);
        }
        
        
        /// As WorldToCellOnLevel, but clamps to grid and returns whether the original was in bounds.
        /// Handy for mouse hover that should stay inside grid.
        public bool WorldToCellClamped(Vector3 worldPos, Vector3Int levelRef, out Vector2Int cell)
        {
            var raw = WorldToCell(worldPos);
            bool inBounds = CellInBounds(raw);
            cell = ClampCell(raw);
            return inBounds;
        }

        /// World center of a cell (uses levelStep for Y).
        public Vector3 GetCellCenter(Vector2Int cell)
        {
            return new Vector3(
                worldOrigin.x + (cell.x + 0.5f) * CellSize,
                0,
                worldOrigin.z + (cell.y + 0.5f) * CellSize
            );
        }
        
        
        /// Snaps a world position to the nearest grid-aligned position for a platform with given footprint.
        /// Handles even vs odd footprints correctly:
        /// - Even footprints (4x4): snap to cell edges (whole meters like 44.0)
        /// - Odd footprints (3x3): snap to cell centers (half meters like 44.5)
        public Vector3 SnapToGridForPlatform(Vector3 worldPosition, Vector2Int footprint)
        {
            bool evenWidth = (footprint.x % 2) == 0;
            bool evenLength = (footprint.y % 2) == 0;
            
            float level = 0;
            
            // For even dimensions, snap to cell edges (whole meters)
            // For odd dimensions, snap to cell centers (half meters)
            float snappedX;
            float snappedZ;
            
            if (evenWidth)
            {
                // Snap to whole meters (cell edges)
                snappedX = worldOrigin.x + Mathf.Round((worldPosition.x - worldOrigin.x) / CellSize) * CellSize;
            }
            else
            {
                // Snap to cell centers (half meters)
                snappedX = worldOrigin.x + (Mathf.Floor((worldPosition.x - worldOrigin.x) / CellSize) + 0.5f) * CellSize;
            }
            
            if (evenLength)
            {
                // Snap to whole meters (cell edges)
                snappedZ = worldOrigin.z + Mathf.Round((worldPosition.z - worldOrigin.z) / CellSize) * CellSize;
            }
            else
            {
                // Snap to cell centers (half meters)
                snappedZ = worldOrigin.z + (Mathf.Floor((worldPosition.z - worldOrigin.z) / CellSize) + 0.5f) * CellSize;
            }
            
            return new Vector3(snappedX, level, snappedZ);
        }

        
        
        /// Snaps a world position to the nearest grid cell center, inferring level from Y coordinate.
        public Vector3 SnapWorldPositionToGrid(Vector3 worldPosition)
        {
            Vector2Int cell = WorldToCell(worldPosition);
            return GetCellCenter(cell);
        }

        
        
        /// World-space axis-aligned bounds of a cell (thin in Y).
        public Bounds GetCellBounds(Vector2Int cell)
        {
            Vector3 center = GetCellCenter(cell);
            Vector3 size = new Vector3(CellSize, 0.01f, CellSize);
            return new Bounds(center, size);
        }

        
        
        /// Writes BL, BR, TR, TL corners of a cell to out4. Requires out4.Length ≥ 4.
        public void GetCellCorners(Vector3Int cell, out Vector3[] out4)
        {
            out4 = new Vector3[4];

            float minX = worldOrigin.x + cell.x * (float)CellSize;
            float minZ = worldOrigin.z + cell.y * (float)CellSize;
            float maxX = minX + CellSize;
            float maxZ = minZ + CellSize;

            out4[0] = new Vector3(minX, 0, minZ); // BL
            out4[1] = new Vector3(maxX, 0, minZ); // BR
            out4[2] = new Vector3(maxX, 0, maxZ); // TR
            out4[3] = new Vector3(minX, 0, maxZ); // TL
        }


        
        /// Cell-local to world. uv01 is (U,V) in [0..1] from the cell's lower-left corner.
        public Vector3 LocalToWorldInCell(Vector3Int cell, Vector2 uv01)
        {
            return new Vector3(
                worldOrigin.x + (cell.x + uv01.x) * CellSize,
                0,
                worldOrigin.z + (cell.y + uv01.y) * CellSize
            );
        }


        
        /// World → (cell, uv01). Projects onto levelRef.z, returns the inside-cell UV [0..1].
        /// Returns false if cell is out of bounds.
        public bool WorldToLocalInCell(Vector3 worldPos, out Vector2Int cell, out Vector2 uv01)
        {
            cell = WorldToCell(worldPos);
            uv01 = default;
            
            if (!CellInBounds(cell)) return false;

            float x0 = worldOrigin.x + cell.x * (float)CellSize;
            float z0 = worldOrigin.z + cell.y * (float)CellSize;
            
            uv01 = new Vector2(
                Mathf.Clamp01((worldPos.x - x0) / (float)CellSize),
                Mathf.Clamp01((worldPos.z - z0) / (float)CellSize)
            );
            return true;
        }
        
        #endregion

        #region Raycast Helpers
        
        // ---------- Raycast helper ----------
        
        /// Raycast to plane(y = GetLevelWorldY(levelRef.z)), return hit world point + cell if inside.
        /// Only levelRef.z is used.
        public bool RaycastToCell(Ray worldRay, out Vector2Int cell, out Vector3 hitPoint)
        {
            float h = 0;
            
            var plane = new Plane(Vector3.up, new Vector3(0f, h, 0f));
            
            if (plane.Raycast(worldRay, out float dist))
            {
                hitPoint = worldRay.origin + worldRay.direction * dist;
                
                cell = WorldToCell(hitPoint);
                
                return CellInBounds(cell);
            }
            
            cell = default; 
            hitPoint = default;
            
            return false;
        }
        
        #endregion

        #region Cell Data Access & Flags
        
        // ---------- Single-cell data & flags ----------

        /// Read cell data if in bounds.
        public bool TryGetCell(Vector2Int cell, out CellData data)
        {
            if (!CellInBounds(cell)) { data = default; return false; }
            
            data = _cells[cell.x, cell.y];
            
            return true;
        }

        /// Write cell data if in bounds.
        /// Bumps Version and raises CellChanged.
        public void SetCell(Vector2Int cell, CellData data)
        {
            if (!CellInBounds(cell)) return;
            
            _cells[cell.x, cell.y] = data;
            
            Version++;
            CellChanged?.Invoke(cell);
        }

        
        /// Clear cell (flags=Empty, payloads zero).
        /// Returns true if in bounds;
        /// bumps Version & raises CellChanged.
        public void ClearCell(Vector2Int cell)
        {
            if (!CellInBounds(cell)) return;
            
            _cells[cell.x, cell.y] = default;
            
            Version++;
            CellChanged?.Invoke(cell);
        }

        
        /// Add a flag to a single cell (in-bounds).
        /// Returns true on success;
        /// bumps Version & raises CellChanged.
        public bool TryAddFlag(Vector2Int cell, CellFlag flag)
        {
            if (!CellInBounds(cell)) return false;
            
            var cd = _cells[cell.x, cell.y];
            cd.flags |= flag;

            _cells[cell.x, cell.y] = cd;
            
            Version++;
            CellChanged?.Invoke(cell);
            
            return true;
        }

        
        
        /// Remove a flag from a single cell (in-bounds).
        /// Returns true on success; bumps Version & raises CellChanged.
        public bool TryRemoveFlag(Vector2Int cell, CellFlag flag)
        {
            if (!CellInBounds(cell)) return false;
            
            var cd = _cells[cell.x, cell.y];
            cd.flags &= ~flag;
            
            _cells[cell.x, cell.y] = cd;
            
            Version++;
            CellChanged?.Invoke(cell);
            
            return true;
        }

        
        
        /// True if the cell has ALL bits in flags (in-bounds only).
        public bool CellHasAllFlags(Vector2Int cell, CellFlag flags)
        {
            if (!CellInBounds(cell)) return false;
            
            return (_cells[cell.x, cell.y].flags & flags) == flags;
        }

        /// True if the cell has ANY bit in flags (in-bounds only).
        public bool CellHasAnyFlag(Vector2Int cell, CellFlag flags)
        {
            if (!CellInBounds(cell)) return false;
            
            return (_cells[cell.x, cell.y].flags & flags) != 0;
        }
        
        #endregion

        #region Area Operations
        
        // ---------- Area helpers (two corners, inclusive, level = a.z) ----------

        private void ClampAreaInclusive(Vector2Int a, Vector2Int b, out Vector2Int min, out Vector2Int max)
        {
            min = new Vector2Int(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
            max = new Vector2Int(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

            min.x = Mathf.Clamp(min.x, 0, sizeX - 1);
            min.y = Mathf.Clamp(min.y, 0, sizeY - 1);
            max.x = Mathf.Clamp(max.x, 0, sizeX - 1);
            max.y = Mathf.Clamp(max.y, 0, sizeY - 1);
        }

        /// Iterate each cell (x,y,level) in an inclusive rectangle (level = a.z).
        public void ForEachCellInclusive(Vector2Int a, Vector2Int b, Action<Vector2Int> visit)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
                visit(new Vector2Int(x, y));
        }

        // ---------- Area operations (flags & queries) ----------

        /// Add (OR) flag to every cell in area.
        /// Optional platformId/payload when non-zero.
        /// Bumps Version & raises AreaChanged.
        public void AddFlagInArea(Vector2Int a, Vector2Int b, CellFlag flag)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var cd = _cells[x, y];
                cd.flags |= flag;

                _cells[x, y] = cd;
            }
            
            Version++;
            AreaChanged?.Invoke(min, max);
        }

        
        
        /// Replace flags in area with exactFlags (overwrites existing flags).
        /// Bumps Version & raises AreaChanged.
        public void SetFlagsInAreaExact(Vector2Int a, Vector2Int b, CellFlag exactFlags)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var cd = _cells[x, y];
                cd.flags = exactFlags;

                _cells[x, y] = cd;
            }
            
            Version++;
            AreaChanged?.Invoke(min, max);
        }

        
        
        /// Remove (AND NOT) flag from every cell in area. Bumps Version & raises AreaChanged.
        public void RemoveFlagInArea(Vector2Int a, Vector2Int b, CellFlag flag)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var cd = _cells[x, y];
                cd.flags &= ~flag;
                
                _cells[x, y] = cd;
            }
            
            Version++;
            AreaChanged?.Invoke(min, max);
        }

        
        
        /// Clear cells (set default) within area. Bumps Version & raises AreaChanged.
        public void ClearArea(Vector2Int a, Vector2Int b)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                _cells[x, y] = default;
            }

            Version++;
            AreaChanged?.Invoke(min, max);
        }

        
        
        /// True if every cell in area has ALL bits in flags.
        public bool AreaHasAllFlags(Vector2Int a, Vector2Int b, CellFlag flags)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                if ((_cells[x, y].flags & flags) != flags) return false;
            }

            return true;
        }

        
        
        /// True if any cell in area has ANY bit in flags.
        public bool AreaHasAnyFlag(Vector2Int a, Vector2Int b, CellFlag flags)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                if ((_cells[x, y].flags & flags) != 0) return true;
            }

            return false;
        }

        
        
        /// Returns List with all cells inside area.
        public List<Vector2Int> GetCellsInArea(Vector2Int a, Vector2Int b)
        {
            var cells = new List<Vector2Int>();
            
            ClampAreaInclusive(a, b, out var min, out var max);
            
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                cells.Add(new Vector2Int(x, y));
            }
            
            return cells;
        }

        
        
        /// Count cells in area with ALL bits in flags.
        public List<CellData> GetCellsWithAllFlags(Vector2Int a, Vector2Int b, CellFlag flags)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            
            var cells = new List<CellData>();
            
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var cell = _cells[x, y];
                if (cell.flags == flags) cells.Add(cell);
            }

            return cells;
        }

        
        
        /// Get cells in area with ANY bit in flags.
        public List<CellData> GetCellsWithAnyFlag(Vector2Int a, Vector2Int b, CellFlag flags)
        {
            ClampAreaInclusive(a, b, out var min, out var max);
            
            var cells = new List<CellData>();
            
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var cell = _cells[x, y];
                if ((cell.flags & flags) != 0) cells.Add(cell);
            }

            return cells;
        }
        
        #endregion

        #region Neighbor Queries
        
        // ---------- Neighbors (pathing/adjacency) ----------

        public List<Vector2Int> GetNeighbors4(Vector2Int cell)
        {
            var neighbors = new List<Vector2Int>();
            
            var n = new Vector2Int(cell.x + 1, cell.y); if (CellInBounds(n)) neighbors.Add(n);
            n.x = cell.x - 1;                                     if (CellInBounds(n)) neighbors.Add(n);
            n.x = cell.x; n.y = cell.y + 1;                       if (CellInBounds(n)) neighbors.Add(n);
            n.y = cell.y - 1;                                     if (CellInBounds(n)) neighbors.Add(n);
            
            return neighbors;
        }

        public List<Vector2Int> GetNeighbors8(Vector2Int cell)
        {
            var neighbors = new List<Vector2Int>();
            
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                
                var n = new Vector2Int(cell.x + dx, cell.y + dy);
                
                if (CellInBounds(n)) neighbors.Add(n);
            }
            
            return neighbors;
        }

        
        
        /// Gets the world position of the edge center between two adjacent cells.
        /// Returns the midpoint between the centers of cellA and cellB.
        public Vector3 GetEdgeCenterBetweenCells(Vector2Int cellA, Vector2Int cellB)
        {
            Vector3 centerA = GetCellCenter(cellA);
            Vector3 centerB = GetCellCenter(cellB);
            return (centerA + centerB) * 0.5f;
        }
        
        #endregion
    }
}
