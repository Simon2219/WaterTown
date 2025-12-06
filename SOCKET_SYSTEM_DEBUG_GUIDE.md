# Socket System Debug Guide

## What Are Platform Sockets?

**Platform Sockets are logical connection points** - they're not physical GameObjects, they're data structures (`SocketInfo`) that represent potential connection positions around a platform's edges.

### Purpose:
- **Connection Logic:** Determine where two platforms can connect
- **Railing Management:** Control which rails/posts to hide when platforms connect
- **NavMesh Links:** Create AI pathfinding connections between platforms

### Socket Positioning:
- **Location:** At the CENTER of each cell edge (e.g., x=42.5 for a socket between cells)
- **Spacing:** 1 meter apart (one socket per grid cell edge)
- **Calculation:** Built in local space, then transformed to world space

---

## Recent Fixes

### 1. Fixed `footprint` → `footprintSize` Rename
**File:** `Assets/Scripts/Editor/EditorAssetManager.cs:133`
- Changed `so.FindProperty("footprint")` → `so.FindProperty("footprintSize")`

### 2. Fixed Platform Alignment for Even/Odd Footprints
**File:** `Assets/Scripts/Grid/WorldGrid.cs`
**New Method:** `SnapToGridForPlatform()`

**Problem:** Platforms with even footprints (4x4) have their pivot at a cell EDGE, not center.

**Solution:**
- **Even footprints (4x4):** Snap to whole meters (44.0, 46.0)
- **Odd footprints (3x3):** Snap to half meters (44.5, 45.5)
- **Rotation-aware:** Accounts for 90°/270° rotations swapping width/height

### 3. Fixed Ghost Platform Interference
**File:** `Assets/Scripts/BuildModeManager.cs`

**Problem:** Ghost remained registered in TownManager when placing real platform, interfering with adjacency detection.

**Solution:**
1. Unregister ghost BEFORE placing real platform
2. Place real platform
3. Re-register ghost for continued placement

### 4. Added Comprehensive Debug Logging

**TownManager.cs:**
- `ComputeCellsForPlatform()` - logs which cells each platform occupies
- `IsAreaFree()` - logs which platform InstanceID occupies blocked cells
- `ConnectIfAdjacentByGridCells()` - logs adjacency detection
- `FindNearestSocketOnEdge()` - logs socket matching for connections

**GamePlatform.cs:**
- `BuildSockets()` - logs socket count and first 4 socket positions

---

## Testing Guide

### Step 1: Place First Platform
1. Enter Build Mode
2. Select a platform (e.g., 4x4)
3. Place it in empty space

**Expected Logs:**
```
[BuildModeManager] Unregistered ghost before placing real platform
[TownManager] ComputeCells for 'Platform_01': WorldPos=(44.00, 0.00, 44.00), CenterCell=(44,44), Footprint=4x4...
[GamePlatform] Built 16 sockets for 'Platform_01' at worldPos=(44.00, 0.00, 44.00) (Footprint: 4x4)
  Socket[0] localPos=(-1.50, 0.00, 2.00), worldPos=(42.50, 0.00, 46.00)
  Socket[1] localPos=(-0.50, 0.00, 2.00), worldPos=(43.50, 0.00, 46.00)
  ...
[BuildModeManager] Re-registered ghost after placing
```

**Verify:**
- Platform sits perfectly on grid (edges at whole meters for 4x4)
- No "Area not free" errors
- No "Adjacency requirement not met" errors (if adjacency is disabled)

### Step 2: Place Second Platform Adjacent
1. Move ghost next to first platform (edge-to-edge)
2. Place it

**Expected Logs:**
```
[TownManager] Found X adjacent cell pairs between 'Platform_01' and 'Platform_02'
[TownManager] Finding socket on 'Platform_01' edge East: range [8..11], target worldPos=...
  Socket[8] at (46.00, 0.00, 45.50), distance²=...
  Socket[9] at (46.00, 0.00, 44.50), distance²=...
  ...
  -> Best socket: 9 (distance²=...)
```

**Verify:**
- Platforms connect visually (rails hide at connection)
- Posts hide if all connected rails are hidden
- Adjacency detection works correctly

### Step 3: Check Socket Positions Visually
Enable Gizmos in Scene view and look for:
- Socket positions drawn at cell edges
- Sockets 1m apart
- Connected sockets highlighted

---

## Expected Socket Layout (4x4 Platform at 44.0, 0.0, 44.0)

**Platform Bounds:** (42.0, 0.0, 42.0) to (46.0, 0.0, 46.0)

**North Edge (z=46.0):** 4 sockets at x = 42.5, 43.5, 44.5, 45.5
**South Edge (z=42.0):** 4 sockets at x = 42.5, 43.5, 44.5, 45.5
**East Edge (x=46.0):** 4 sockets at z = 45.5, 44.5, 43.5, 42.5
**West Edge (x=42.0):** 4 sockets at z = 45.5, 44.5, 43.5, 42.5

**Total:** 16 sockets (4 per edge)

---

## Common Issues

### Issue: "Area not free" even in empty space
**Cause:** Ghost platform or previous platform still occupying cells
**Fix:** Check debug logs for which platform InstanceID is blocking
**Verify:** Ghost is unregistered before placing

### Issue: "No adjacency found"
**Cause:** Platforms not actually touching (cell-to-cell)
**Fix:** Check `ComputeCellsForPlatform` logs to verify cell coverage
**Verify:** Adjacent cells share an edge (not just a corner)

### Issue: Rails don't hide when connecting
**Cause:** Socket matching failed in `FindNearestSocketOnEdge`
**Fix:** Check socket world positions match edge centers
**Verify:** `ConnectIfAdjacentByGridCells` logs show matched sockets

---

## Next Steps

1. **Test with debug logs enabled** - see exactly what's happening
2. **Report back with console output** - I'll help diagnose any issues
3. **Once working, remove debug logs** - clean up for production

The extensive logging will show us exactly where the socket system is failing (if it still is).

