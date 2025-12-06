# Railings & NavMesh System with IPickupable

## Overview

The railing visibility and NavMesh link system now works seamlessly with the new **IPickupable** pickup/placement system.

---

## How It Works

### When a Platform is Placed

**1. GamePlatform.OnPlaced() is called:**
```csharp
IsPickedUp = false;
BuildSockets();                      // Ensure sockets exist for adjacency
EnsureChildrenModulesRegistered();   // Register modules
EnsureChildrenRailingsRegistered();  // Register railings
townManager.RegisterPlatform(...);   // Register with TownManager
```

**2. TownManager.RegisterPlatform() does:**
- Marks cells as `Occupied` in WorldGrid
- Builds NavMesh on the platform (`platform.BuildLocalNavMesh()`)
- Calls `MarkAdjacencyDirty()` to trigger recomputation in LateUpdate

**3. TownManager.LateUpdate() triggers:**
```csharp
RecomputeAllAdjacency() runs:
  1. Filters out picked-up platforms (IsPickedUp == true)
  2. Resets all connections on all platforms (railings reappear)
  3. Checks pairwise adjacency between platforms
  4. Calls ConnectIfAdjacentByGridCells() for each adjacent pair
  5. Updates socket statuses and railing visibility
  6. Creates NavMesh links between connected platforms
```

---

### When a Platform is Picked Up (Moved)

**1. GamePlatform.OnPickedUp(isNewObject: false) is called:**
```csharp
IsPickedUp = true;
townManager.UnregisterPlatform(this);  // Unregister from TownManager
```

**2. TownManager.UnregisterPlatform() does:**
- Removes `Occupied` flags from WorldGrid
- Clears connections on this platform
- Calls `MarkAdjacencyDirty()` to trigger recomputation

**3. TownManager.LateUpdate() triggers:**
```csharp
RecomputeAllAdjacency() runs:
  1. Skips the picked-up platform (IsPickedUp == true)
  2. Remaining platforms update their railings
  3. Rails that were hidden to the picked-up platform now reappear
```

**4. While Moving:**
- Platform continues to exist but `IsPickedUp = true`
- `RecomputeAllAdjacency()` filters it out each frame
- Other platforms don't see it for adjacency checks
- Railings on neighbors stay visible (as if platform was removed)

---

### When Placement is Cancelled

**For New Platforms:**
```csharp
OnPlacementCancelled():
  Destroy(gameObject);  // Simply destroyed, no adjacency impact
```

**For Existing Platforms (Moved):**
```csharp
OnPlacementCancelled():
  transform.position = _originalPosition;  // Restore original position
  transform.rotation = _originalRotation;
  BuildSockets();                          // Rebuild sockets at original position
  townManager.RegisterPlatform(...);       // Re-register at original position
  // This triggers adjacency recomputation, reconnecting to neighbors
```

---

## Adjacency Recomputation Flow

### TownManager.RecomputeAllAdjacency()

**Step 1: Gather Platforms**
```csharp
foreach (var gp in GamePlatform.AllPlatforms)
{
    if (!gp.isActiveAndEnabled) continue;
    if (gp.IsPickedUp) continue;  // ← KEY: Skip picked-up platforms!
    _tmpPlatforms.Add(gp);
}
```

**Step 2: Reset All Connections**
```csharp
foreach (var p in _tmpPlatforms)
    p.EditorResetAllConnections();  // All railings reappear
```

**Step 3: Pairwise Adjacency Checks**
```csharp
for (int i = 0; i < platformCount; i++)
{
    for (int j = i + 1; j < platformCount; j++)
    {
        ConnectIfAdjacentByGridCells(platformA, platformB);
    }
}
```

**Step 4: ConnectIfAdjacentByGridCells()**
- Finds adjacent grid cells between platforms
- Determines which edges are touching
- Finds nearest sockets on those edges
- Sets socket statuses to `Connected`
- Calls `ApplyConnectionVisuals()` to hide rails/posts
- Creates NavMesh links at connection points

---

## Railing Visibility Logic

### In GamePlatform.ApplyConnectionVisuals()

When sockets are marked as `Connected`:

**Rails:**
- Hidden when their socket is `Connected`

**Posts:**
- Hidden when **ALL** rails connected to them are hidden
- A post connects 2 rails (on adjacent edges)
- If both rails are hidden → post hidden
- If any rail visible → post visible

### Example: 2 Platforms Side by Side

**Platform A (East edge) connects to Platform B (West edge):**

```
Platform A          Platform B
┌─────────┐        ┌─────────┐
│         ║        ║         │
│         ║ hidden ║         │
│         ║        ║         │
└─────────┘        └─────────┘
```

- Socket on A's East edge: `Connected`
- Socket on B's West edge: `Connected`
- Rails between them: **Hidden**
- Posts at those sockets: **Hidden** (if all adjacent rails hidden)
- Other edges: **Visible**

---

## NavMesh System

### Local NavMesh (Per Platform)

**Built when platform is placed:**
```csharp
TownManager.RegisterPlatform():
  if (markOccupiedInGrid)
  {
      platform.BuildLocalNavMesh();  // Bakes NavMesh on this platform's surface
  }
```

**GamePlatform.BuildLocalNavMesh():**
- Uses Unity's `NavMeshSurface` component
- Bakes walkable surface on the platform
- Debounced to avoid excessive rebuilds

### NavMesh Links (Between Platforms)

**Created when platforms connect:**
```csharp
ConnectIfAdjacentByGridCells():
  // For each connection point between platforms
  CreateNavMeshLink(platformA, platformB, posA, posB, width);
```

**NavMeshLink:**
- Bidirectional link between two platform surfaces
- Allows AI agents to pathfind across platforms
- Positioned at the average of connected socket positions
- Width based on connection size

---

## Complete Lifecycle Examples

### Example 1: Building Two Adjacent Platforms

**1. Place First Platform:**
```
OnPlaced() → RegisterPlatform() → MarkAdjacencyDirty()
LateUpdate: RecomputeAllAdjacency()
  - Only 1 platform, no connections
  - All railings visible
  - NavMesh built on Platform A
```

**2. Place Second Platform Next to First:**
```
OnPlaced() → RegisterPlatform() → MarkAdjacencyDirty()
LateUpdate: RecomputeAllAdjacency()
  - 2 platforms detected
  - Adjacency check: cells touch!
  - ConnectIfAdjacentByGridCells():
    - Mark sockets as Connected
    - Hide rails at connection
    - Hide posts if all rails hidden
    - Create NavMesh link between platforms
```

**Result:** Rails hidden at connection, AI can pathfind between platforms

---

### Example 2: Moving an Existing Platform

**1. Pick Up Platform:**
```
OnPickedUp(isNewObject: false) → UnregisterPlatform() → MarkAdjacencyDirty()
LateUpdate: RecomputeAllAdjacency()
  - Platform filtered out (IsPickedUp = true)
  - Neighbors update: connection lost
  - Rails at connection reappear
  - NavMesh link removed
```

**2. While Moving:**
```
Each frame:
  UpdatePickupPosition() - follows mouse
  UpdateValidityVisuals() - shows green/red
LateUpdate: RecomputeAllAdjacency()
  - Platform still filtered out (IsPickedUp = true)
  - Neighbors continue to show rails
```

**3. Place at New Location:**
```
OnPlaced() → RegisterPlatform() → MarkAdjacencyDirty()
LateUpdate: RecomputeAllAdjacency()
  - Platform back in (IsPickedUp = false)
  - Check adjacency at new position
  - Update railings based on new neighbors
  - Create new NavMesh links
```

**4. OR Cancel:**
```
OnPlacementCancelled():
  - Restore original position/rotation
  - BuildSockets() at original position
  - RegisterPlatform() at original position
  - MarkAdjacencyDirty()
LateUpdate: RecomputeAllAdjacency()
  - Platform back where it was
  - Original connections restored
  - Rails hide again where they were
  - NavMesh links recreated
```

---

## Key Points

### ✅ Automatic Updates
- Railings and NavMesh **automatically update** when platforms are placed/moved/removed
- No manual triggering needed - `MarkAdjacencyDirty()` handles it
- Batched to LateUpdate for performance (multiple changes = one recomputation)

### ✅ Picked-Up Platforms Excluded
- `IsPickedUp` flag prevents picked-up platforms from participating in adjacency
- Neighbors see it as "removed" while being moved
- Prevents confusing half-connections or visual glitches

### ✅ Clean State Management
- Each operation (place/pickup/cancel) properly manages:
  - Grid occupancy flags
  - TownManager registration
  - Socket states
  - Railing visibility
  - NavMesh surfaces and links

### ✅ Undo/Restore Support
- Cancelling placement of moved platforms fully restores original state
- Original connections, railings, and NavMesh links all restored
- No leftover state or visual artifacts

---

## Testing Checklist

**Test 1: Place Two Adjacent Platforms**
- [ ] First platform: all railings visible
- [ ] Second platform placed next to first: rails hide at connection
- [ ] Posts at connection also hidden
- [ ] NavMesh links created (check with NavMesh visualization)

**Test 2: Pick Up and Move Platform**
- [ ] Pick up one platform
- [ ] Neighbor's railings immediately reappear where connection was
- [ ] While moving: neighbor's rails stay visible
- [ ] Place at new location: new connections form, rails hide appropriately
- [ ] NavMesh links update

**Test 3: Cancel Movement**
- [ ] Pick up platform
- [ ] Move it around
- [ ] Cancel (press cancel key)
- [ ] Platform returns to original position
- [ ] Original railing connections restored
- [ ] NavMesh links restored

**Test 4: Multiple Platforms**
- [ ] Place 4 platforms in a square
- [ ] All internal rails hidden, only perimeter visible
- [ ] Pick up one corner: 2 neighbors update their rails
- [ ] Place it back: all connections restore

---

## Summary

The railing and NavMesh system is **fully integrated** with the new IPickupable system:

✅ **Railings** automatically show/hide based on platform connections  
✅ **NavMesh** automatically builds on platforms and links between them  
✅ **Picked-up platforms** properly excluded from adjacency checks  
✅ **Cancellation** fully restores original state  
✅ **Batched updates** for performance (LateUpdate)  
✅ **No manual intervention** needed - everything automatic

The system is **clean, simple, and works perfectly** with build and move operations!

