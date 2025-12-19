using System;
using UnityEngine;


namespace Grid
{
/// Bit flags
/// Powers of 2 - so they combine cleanly
/// 
[Flags]
public enum CellFlag
{
    Empty = 0,
    Locked = 1 << 0,
    Buildable = 1 << 1,
    Occupied = 1 << 2,
    OccupyPreview = 1 << 3,
    ModuleBlocked = 1 << 4, // Cell has a blocking module on its edge (prevents socket connection)
    // Extend as needed: ServiceZone = 1<<5, etc
}


/// Cell data container
/// Controlled flag manipulation
/// All flag changes route through methods that enforce exclusivity rules
/// Class (reference type) with back-reference to grid for automatic notifications
///
[Serializable]
public class CellData
{
    [SerializeField] private CellFlag flags;

    // Back-reference to grid for notifications
    private WorldGrid _grid;
    private Vector2Int _position;

    // --- Read-only accessors ---
    public CellFlag Flags => flags;
    public bool IsEmpty => flags == CellFlag.Empty;
    public Vector2Int Position => _position;
    public bool HasFlag(CellFlag f) => (flags & f) != 0;
    public bool HasAllFlags(CellFlag f) => (flags & f) == f;

    // Exclusive states - only one of these should be active at a time
    // Priority order: Locked > Occupied > OccupyPreview
    private const CellFlag ExclusiveStateMask =
        CellFlag.Locked | CellFlag.Occupied | CellFlag.OccupyPreview;



    /// Initializes the cell with its grid reference and position
    /// Called by WorldGrid during allocation
    ///
    internal void Initialize(WorldGrid grid, Vector2Int position)
    {
        _grid = grid;
        _position = position;
        flags = CellFlag.Empty;
    }


    /// Notifies the grid that this cell has changed
    ///
    private void NotifyChanged()
    {
        _grid?.NotifyCellChanged(_position);
    }


    /// Primary method to modify flags with optional priority enforcement
    /// Priority: Locked > Occupied > OccupyPreview > Buildable > Empty
    /// 
    /// <param name="toSet"> Flags to set (bitwise OR) </param>
    /// <param name="toClear"> Flags to clear before setting </param>
    /// <param name="enforcePriority"> Apply exclusivity and priority rules </param>
    /// <returns> True if modification succeeded - False if blocked by priority rules </returns>
    /// 
    private bool TryModify(CellFlag toSet, CellFlag toClear = CellFlag.Empty, bool enforcePriority = true)
    {
        // Fast path: explicit full clear
        if (toSet == CellFlag.Empty && toClear == CellFlag.Empty)
        {
            flags = CellFlag.Empty;
            NotifyChanged();
            return true;
        }

        if (!enforcePriority)
        {
            flags = (flags & ~toClear) | toSet;
            NotifyChanged();
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
        NotifyChanged();
        return true;
    }



    /// Sets flags to exactly the provided value
    /// Clears all other Flags
    /// 
    public bool SetExact(CellFlag exactFlags, bool enforcePriority = true)
    {
        if (!enforcePriority)
        {
            flags = exactFlags;
            NotifyChanged();
            return true;
        }

        // Clear all, then set
        return TryModify(toSet: exactFlags, toClear: ~CellFlag.Empty, enforcePriority: true);
    }



    /// Adds Flags
    ///
    public bool AddFlags(CellFlag toAdd, bool enforcePriority = true)
    {
        return TryModify(toSet: toAdd, toClear: CellFlag.Empty, enforcePriority: enforcePriority);
    }



    /// Removes Flags (bitwise AND NOT)
    /// Removal doesn't require priority checks
    ///
    public void RemoveFlags(CellFlag toRemove)
    {
        flags &= ~toRemove;
        NotifyChanged();
    }


    /// Clears all flags
    /// Sets to Empty
    ///
    public void Clear()
    {
        flags = CellFlag.Empty;
        NotifyChanged();
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
}



