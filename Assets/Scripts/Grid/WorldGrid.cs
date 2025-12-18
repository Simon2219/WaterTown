using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Unity.VisualScripting;
using UnityEngine;

namespace Grid
{
    
///
/// World grid foundation
/// • 1×1 m cells
/// • Only Flat - NO LEVELS IMPLEMENTED
/// • Management over CellFlag Status 
///
[DisallowMultipleComponent]

public class WorldGrid : MonoBehaviour
{
    
    #region Configuration & Data Structures

    [Header("Grid Dimensions (cells)")]
    
    [Min(1)] public int sizeX = 500;
    [Min(1)] public int sizeY = 500;

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

    
    
    /// <summary>
    /// Cell data container with controlled flag manipulation.
    /// All flag changes route through methods that enforce exclusivity rules.
    /// Now a class (reference type) to avoid struct copy issues.
    /// </summary>
    [Serializable]
    public class CellData
    {
        [SerializeField] private CellFlag flags;

        // --- Read-only accessors ---
        public CellFlag Flags => flags;
        public bool IsEmpty => flags == CellFlag.Empty;
        public bool HasFlag(CellFlag f) => (flags & f) != 0;
        public bool HasAllFlags(CellFlag f) => (flags & f) == f;

        // Exclusive states - only one of these should be active at a time
        // Priority order: Locked > Occupied > OccupyPreview
        private const CellFlag ExclusiveStateMask =
            CellFlag.Locked | CellFlag.Occupied | CellFlag.OccupyPreview;

        /// <summary>
        /// Primary method to modify flags with optional priority enforcement.
        /// Priority: Locked > Occupied > OccupyPreview > Buildable > Empty
        /// </summary>
        /// <param name="toSet">Flags to set (bitwise OR)</param>
        /// <param name="toClear">Flags to clear before setting</param>
        /// <param name="enforcePriority">Apply exclusivity and priority rules</param>
        /// <returns>True if modification succeeded, false if blocked by priority rules</returns>
        public bool TryModify(CellFlag toSet, CellFlag toClear = CellFlag.Empty, bool enforcePriority = true)
        {
            // Fast path: explicit full clear
            if (toSet == CellFlag.Empty && toClear == CellFlag.Empty)
            {
                flags = CellFlag.Empty;
                return true;
            }

            if (!enforcePriority)
            {
                flags = (flags & ~toClear) | toSet;
                return true;
            }

            CellFlag current = flags;
            CellFlag proposed = (current & ~toClear) | toSet;
            
            // Extract exclusive states using the mask
            CellFlag currentExclusive = current & ExclusiveStateMask;
            CellFlag requestedExclusive = toSet & ExclusiveStateMask;
            CellFlag clearingExclusive = toClear & ExclusiveStateMask;

            // --- Priority enforcement ---

            // 1. Locked blocks all changes unless unlocking or re-locking
            if (currentExclusive == CellFlag.Locked && 
                (clearingExclusive & CellFlag.Locked) == 0 && 
                requestedExclusive != CellFlag.Locked)
            {
                return false;
            }

            // 2. Occupied blocks Preview unless Occupied is being cleared
            if ((currentExclusive & CellFlag.Occupied) != 0 &&
                (requestedExclusive & CellFlag.OccupyPreview) != 0 &&
                (clearingExclusive & CellFlag.Occupied) == 0)
            {
                return false;
            }

            // 3. Buildable blocked while any exclusive state exists (unless that state is being cleared)
            if ((toSet & CellFlag.Buildable) != 0 &&
                currentExclusive != CellFlag.Empty &&
                (clearingExclusive & currentExclusive) == 0)
            {
                return false;
            }

            // 4. Normalize: only one exclusive state allowed - pick highest priority
            CellFlag finalExclusive = GetHighestPriorityState(proposed);
            proposed = (proposed & ~ExclusiveStateMask) | finalExclusive;

            // 5. Exclusive states and Buildable are mutually exclusive
            if (finalExclusive != CellFlag.Empty)
                proposed &= ~CellFlag.Buildable;

            flags = proposed;
            return true;
        }

        /// <summary>
        /// Sets flags to exactly the provided value (clears all others).
        /// </summary>
        public bool SetExact(CellFlag exactFlags, bool enforcePriority = true)
        {
            if (!enforcePriority)
            {
                flags = exactFlags;
                return true;
            }

            // Clear all, then set
            return TryModify(toSet: exactFlags, toClear: ~CellFlag.Empty, enforcePriority: true);
        }

        /// <summary>
        /// Adds flags (bitwise OR) with priority enforcement.
        /// </summary>
        public bool AddFlags(CellFlag toAdd, bool enforcePriority = true)
        {
            return TryModify(toSet: toAdd, toClear: CellFlag.Empty, enforcePriority: enforcePriority);
        }

        /// <summary>
        /// Removes specified flags (bitwise AND NOT). Removal doesn't require priority checks.
        /// </summary>
        public void RemoveFlags(CellFlag toRemove)
        {
            flags &= ~toRemove;
        }

        /// <summary>
        /// Clears all flags (sets to Empty).
        /// </summary>
        public void Clear()
        {
            flags = CellFlag.Empty;
        }

        // --- Internal helper ---
        private static CellFlag GetHighestPriorityState(CellFlag f)
        {
            if ((f & CellFlag.Locked) != 0) return CellFlag.Locked;
            if ((f & CellFlag.Occupied) != 0) return CellFlag.Occupied;
            if ((f & CellFlag.OccupyPreview) != 0) return CellFlag.OccupyPreview;
            return CellFlag.Empty;
        }
    }

    
    /// Bit flags: powers of two so they combine cleanly (Buildable | Locked).
    /// Keep mutually-exclusive concepts in separate fields.
    [Flags]
    public enum CellFlag
    {
        Empty         = 0,
        Locked        = 1 << 0,
        Buildable     = 1 << 1,
        Occupied      = 1 << 2,
        OccupyPreview = 1 << 3,
        ModuleBlocked = 1 << 4,  // Cell has a blocking module on its edge (prevents socket connection)
        // Extend as needed: ServiceZone = 1<<5, etc.
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
            AllocateIfNeeded();
    }

    /// Editor-only method to apply inspector changes and reallocate grid.
    public void EditorApplySettings()
    {
        AllocateIfNeeded();
    }
    
#endif


    /// Validates grid configuration values.
    /// Throws InvalidOperationException if configuration is invalid.
    [SuppressMessage("ReSharper", "ConditionIsAlwaysTrueOrFalse")]
    private void ValidateConfiguration()
    {
        if (sizeX < 1 || sizeY < 1)
        {
            throw ErrorHandler.InvalidConfiguration(
                $"Grid dimensions must be at least 1 (current: {sizeX}x{sizeY})", 
                this
            );
        }
    }
    
    private void AllocateIfNeeded()
    {
        sizeX = Mathf.Max(1, sizeX);
        sizeY = Mathf.Max(1, sizeY);
        
        if (_cells == null)
        {
            _cells = new CellData[sizeX, sizeY];
            InitializeCellInstances();
            return;
        }
        
        bool sizeChanged = 
            _cells.GetLength(0) != sizeX ||
            _cells.GetLength(1) != sizeY;

        if (sizeChanged)
        {
            _cells = new CellData[sizeX, sizeY];
            InitializeCellInstances();
            Version++; 
        }
    }
    
    /// <summary>
    /// Initializes all cell instances in the array.
    /// Required because CellData is a class (reference type).
    /// </summary>
    private void InitializeCellInstances()
    {
        for (int y = 0; y < sizeY; y++)
        for (int x = 0; x < sizeX; x++)
        {
            _cells[x, y] = new CellData();
        }
    }
    
    #endregion

    #region Coordinate Transforms & Bounds
    
    // ---------- Bounds & transforms ----------

    
    /// True if (x,y) lies inside the grid
    /// 
    public bool CellInBounds(Vector2Int cell)
    {
        return (uint)cell.x < (uint)sizeX
            && (uint)cell.y < (uint)sizeY;
    }


    
    
    /// Get neighboring cells (4-directional or 8-directional)
    /// For single cell: returns its neighbors
    /// For multiple cells: returns neighbors excluding the input cells (ring around area)
    ///
    public HashSet<Vector2Int> GetNeighborCells(List<Vector2Int> cells, bool include8Directional = false)
    {
        var neighbors = new HashSet<Vector2Int>();
        
        foreach (Vector2Int cell in cells)
        {
            neighbors.AddRange(GetNeighborCells(cell, include8Directional));
        }
        
        return neighbors;
    }
    

    
    public HashSet<Vector2Int> GetNeighborCells(Vector2Int cell, bool include8Directional = false)
    {
        var neighbors = new HashSet<Vector2Int>();
        
        if (include8Directional)
        {
            // 8-directional neighbors
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    
                    var neighborCell = new Vector2Int(cell.x + dx, cell.y + dy);
                    if (CellInBounds(neighborCell) && cell != neighborCell)
                        neighbors.Add(neighborCell);
                }
            }
        }
        else
        {
            // 4-directional neighbors (cardinal only)
            var n = new Vector2Int(cell.x + 1, cell.y);
            if (CellInBounds(n) && cell != n) neighbors.Add(n);
            
            n = new Vector2Int(cell.x - 1, cell.y);
            if (CellInBounds(n) && cell != n) neighbors.Add(n);
            
            n = new Vector2Int(cell.x, cell.y + 1);
            if (CellInBounds(n) && cell != n) neighbors.Add(n);
            
            n = new Vector2Int(cell.x, cell.y - 1);
            if (CellInBounds(n) && cell != n) neighbors.Add(n);
        }
        return neighbors;
    }
    
    
    
    /// Clamps a cell index to the grid extents (useful for safe math on indices)
    /// 
    public Vector2Int ClampCell(Vector2Int cell)
    {
        return new Vector2Int(
            Mathf.Clamp(cell.x, 0, sizeX - 1),
            Mathf.Clamp(cell.y, 0, sizeY - 1)
        );
    }
    
    
    
    /// Converts world → cell (x,y,level = 0)
    /// Floors edges so borders map to the lower/left cell
    /// 
    public Vector2Int WorldToCell(Vector3 worldPos)
    {
        var local = worldPos - worldOrigin;
        int x = Mathf.FloorToInt(local.x / CellSize);
        int y = Mathf.FloorToInt(local.z / CellSize);
        return new Vector2Int(x, y);
    }
    
    
    
    /// Clamps to grid and returns whether the original was in bounds
    /// Handy for mouse hover that should stay inside grid
    /// 
    public bool WorldToCellClamped(Vector3 worldPos, Vector3Int levelRef, out Vector2Int cell)
    {
        var raw = WorldToCell(worldPos);
        bool inBounds = CellInBounds(raw);
        cell = ClampCell(raw);
        return inBounds;
    }

    
    
    /// World center of a cell
    public Vector3 GetCellCenter(Vector2Int cell)
    {
        return new Vector3(
            worldOrigin.x + (cell.x + 0.5f) * CellSize,
            0,
            worldOrigin.z + (cell.y + 0.5f) * CellSize
        );
    }
    
    
    
    /// Snaps world position to nearest grid position for platform with given footprint
    /// Handles even vs odd footprints correctly:
    /// - Even footprints (4x4): snap to cell edges (whole meters like 44.0)
    /// - Odd footprints (3x3): snap to cell centers (half meters like 44.5)
    /// 
    public Vector3 SnapToGridForPlatform(Vector3 worldPosition, Vector2Int footprint)
    {
        bool evenWidth = (footprint.x % 2) == 0;
        bool evenLength = (footprint.y % 2) == 0;
        float level = 0;
        
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

    
    
    /// Snaps world position to nearest grid cell center
    ///
    public Vector3 SnapWorldPositionToGrid(Vector3 worldPosition)
    {
        Vector2Int cell = WorldToCell(worldPosition);
        return GetCellCenter(cell);
    }

    
    
    /// World-space axis-aligned bounds of a cell
    /// 
    public Bounds GetCellBounds(Vector2Int cell)
    {
        Vector3 center = GetCellCenter(cell);
        Vector3 size = new Vector3(CellSize, 0.01f, CellSize);
        return new Bounds(center, size);
    }

    
    
    /// Writes BL, BR, TR, TL corners of a cell to out4
    /// Requires out4.Length ≥ 4
    /// 
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


    
    /// Cell-local to world
    /// uv01 is (U,V) in [0..1] from cell's lower-left corner
    /// 
    public Vector3 CellLocalToWorld(Vector3Int cell, Vector2 uv01)
    {
        return new Vector3(
            worldOrigin.x + (cell.x + uv01.x) * CellSize,
            0,
            worldOrigin.z + (cell.y + uv01.y) * CellSize
        );
    }


    
    /// World → (cell, uv01)
    /// returns the inside-cell UV [0..1]
    /// Returns false if cell is out of bounds
    /// 
    public bool WorldToLocalInCell(Vector3 worldPos, out Vector2Int cell, out Vector2 uv01)
    {
        cell = WorldToCell(worldPos);
        uv01 = default;
        
        if (!CellInBounds(cell)) return false;

        float x0 = worldOrigin.x + cell.x * (float)CellSize;
        float z0 = worldOrigin.z + cell.y * (float)CellSize;
        
        uv01 = new Vector2(
            Mathf.Clamp01((worldPos.x - x0) / CellSize),
            Mathf.Clamp01((worldPos.z - z0) / CellSize)
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
        var plane = new Plane(Vector3.up, new Vector3(0f, 0f, 0f));
        
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
    

    /// Read cell data if in bounds
    /// returns if cell is in bounds
    public bool TryGetCell(Vector2Int cell, out CellData data)
    {
        if (!CellInBounds(cell)) { data = default; return false; }
        
        data = _cells[cell.x, cell.y];
        
        return true;
    }

    
    
    /// Gets the world position of the edge center between two adjacent cells
    /// Returns the midpoint between the centers of cellA and cellB
    /// 
    public Vector3 GetEdgeCenterBetweenCells(Vector2Int cellA, Vector2Int cellB)
    {
        Vector3 centerA = GetCellCenter(cellA);
        Vector3 centerB = GetCellCenter(cellB);
        return (centerA + centerB) * ((float)CellSize / 2);
    }
    
    
    
    /// <summary>
    /// Primary method to set cell flags with priority enforcement.
    /// Priority: Locked > Occupied > OccupyPreview > Buildable > Empty
    /// </summary>
    /// <returns>False if operation is blocked by higher priority flag</returns>
    public bool TrySetCellFlag(Vector2Int cell, CellFlag newFlag, bool enforcePriority = true)
    {
        if (!CellInBounds(cell)) return false;
        
        var cd = _cells[cell.x, cell.y];
        
        bool success;
        if (newFlag == CellFlag.Empty)
        {
            cd.Clear();
            success = true;
        }
        else
        {
            // Delegate to CellData's methods which handle all priority logic
            success = cd.AddFlags(newFlag, enforcePriority);
        }
        
        if (success)
        {
            Version++;
            CellChanged?.Invoke(cell);
        }
        
        return success;
    }



    /// <summary>
    /// Remove specific flags from a cell.
    /// </summary>
    /// <returns>False if cell is out of bounds</returns>
    public bool TryClearCellFlags(Vector2Int cell, CellFlag flagsToClear)
    {
        if (!CellInBounds(cell)) return false;
        
        _cells[cell.x, cell.y].RemoveFlags(flagsToClear);
        
        Version++;
        CellChanged?.Invoke(cell);
        
        return true;
    }



    
    
    /// <summary>
    /// True if the cell has ALL bits in flags (in-bounds only).
    /// </summary>
    public bool CellHasAllFlags(Vector2Int cell, CellFlag flags)
    {
        if (!CellInBounds(cell)) return false;
        
        return _cells[cell.x, cell.y].HasAllFlags(flags);
    }

    /// <summary>
    /// True if the cell has ANY bit in flags (in-bounds only).
    /// </summary>
    public bool CellHasAnyFlag(Vector2Int cell, CellFlag flags)
    {
        if (!CellInBounds(cell)) return false;
        
        return _cells[cell.x, cell.y].HasFlag(flags);
    }
    
    #endregion

    #region Area Operations
    
    // ---------- Area helpers (two corners, inclusive) ----------

    private void ClampAreaInclusive(Vector2Int a, Vector2Int b, out Vector2Int min, out Vector2Int max)
    {
        min = new Vector2Int(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
        max = new Vector2Int(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

        min.x = Mathf.Clamp(min.x, 0, sizeX - 1);
        min.y = Mathf.Clamp(min.y, 0, sizeY - 1);
        max.x = Mathf.Clamp(max.x, 0, sizeX - 1);
        max.y = Mathf.Clamp(max.y, 0, sizeY - 1);
    }

    
    
    /// Iterate each cell (x,y,level) in an inclusive rectangle
    /// 
    public void ForEachCellInclusive(Vector2Int a, Vector2Int b, Action<Vector2Int> visit)
    {
        ClampAreaInclusive(a, b, out var min, out var max);
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
            visit(new Vector2Int(x, y));
    }

    
    
    // ---------- Area operations (flags & queries) ----------

    /// <summary>
    /// Add (OR) flag to every cell in area.
    /// Uses priority enforcement by default.
    /// </summary>
    public void AddFlagInArea(Vector2Int a, Vector2Int b, CellFlag flag, bool enforcePriority = true)
    {
        ClampAreaInclusive(a, b, out var min, out var max);
        
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            _cells[x, y].AddFlags(flag, enforcePriority);
        }
        
        Version++;
        AreaChanged?.Invoke(min, max);
    }

    
    
    /// <summary>
    /// Replace flags in area with exactFlags (overwrites existing flags).
    /// Does NOT enforce priority by default for exact replacements.
    /// </summary>
    public void SetFlagsInAreaExact(Vector2Int a, Vector2Int b, CellFlag exactFlags, bool enforcePriority = false)
    {
        ClampAreaInclusive(a, b, out var min, out var max);
        
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            _cells[x, y].SetExact(exactFlags, enforcePriority);
        }
        
        Version++;
        AreaChanged?.Invoke(min, max);
    }

    
    
    /// <summary>
    /// Remove (AND NOT) flag from every cell in area.
    /// </summary>
    public void RemoveFlagInArea(Vector2Int a, Vector2Int b, CellFlag flag)
    {
        ClampAreaInclusive(a, b, out var min, out var max);
        
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            _cells[x, y].RemoveFlags(flag);
        }
        
        Version++;
        AreaChanged?.Invoke(min, max);
    }

    
    
    /// <summary>
    /// Clear cells (set to Empty) within area.
    /// </summary>
    public void ClearArea(Vector2Int a, Vector2Int b)
    {
        ClampAreaInclusive(a, b, out var min, out var max);
        
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            _cells[x, y].Clear();
        }

        Version++;
        AreaChanged?.Invoke(min, max);
    }

    
    
    /// <summary>
    /// True if every cell in area has ALL bits in flags.
    /// </summary>
    public bool AreaHasAllFlags(Vector2Int a, Vector2Int b, CellFlag flags)
    {
        ClampAreaInclusive(a, b, out var min, out var max);
        
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            if (!_cells[x, y].HasAllFlags(flags)) return false;
        }

        return true;
    }

    
    
    /// <summary>
    /// True if any cell in area has ANY bit in flags.
    /// </summary>
    public bool AreaHasAnyFlag(Vector2Int a, Vector2Int b, CellFlag flags)
    {
        ClampAreaInclusive(a, b, out var min, out var max);
        
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            if (_cells[x, y].HasFlag(flags)) return true;
        }

        return false;
    }


    
    /// <summary>
    /// True if area is completely free (no Occupied flags).
    /// </summary>
    public bool AreaIsEmpty(Vector2Int a, Vector2Int b)
    {
        ClampAreaInclusive(a, b, out var min, out var max);
        
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            if (_cells[x, y].HasFlag(CellFlag.Occupied)) return false;
        }

        return true;
    }

    
    
    /// Returns List with all cells inside area
    /// 
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

    
    
    /// <summary>
    /// Get cells in area with exactly the specified flags.
    /// </summary>
    public List<CellData> GetCellsWithAllFlags(Vector2Int a, Vector2Int b, CellFlag flags)
    {
        ClampAreaInclusive(a, b, out var min, out var max);
        
        var cells = new List<CellData>();
        
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            var cell = _cells[x, y];
            if (cell.Flags == flags) cells.Add(cell);
        }

        return cells;
    }

    
    
    /// <summary>
    /// Get cells in area with ANY bit in flags.
    /// </summary>
    public List<CellData> GetCellsWithAnyFlag(Vector2Int a, Vector2Int b, CellFlag flags)
    {
        ClampAreaInclusive(a, b, out var min, out var max);
        
        var cells = new List<CellData>();
        
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            var cell = _cells[x, y];
            if (cell.HasFlag(flags)) cells.Add(cell);
        }

        return cells;
    }
    
    #endregion
}
}
