# Pickup System Refactor - Complete Redesign

## Overview

**Completely refactored the build/placement system** to use a clean, unified **IPickupable** interface. This eliminates the overcomplicated "ghost" system and provides a single, simple approach for both **building NEW platforms** and **moving EXISTING ones**.

---

## Key Changes

### 1. **New IPickupable Interface** (`Assets/Scripts/Interfaces/IPickupable.cs`)

```csharp
public interface IPickupable
{
    bool IsPickedUp { get; set; }
    bool CanBePlaced { get; }
    Transform Transform { get; }
    GameObject GameObject { get; }
    
    void OnPickedUp(bool isNewObject);
    void OnPlaced();
    void OnPlacementCancelled();
    void UpdateValidityVisuals(bool isValid);
}
```

**Benefits:**
- Can be implemented by **platforms, props, buildings, decorations** - anything placeable
- Object handles its own visual state (materials, colliders, etc.)
- Clear separation of concerns

### 2. **GamePlatform Implements IPickupable**

**Fields Added:**
- `IsPickedUp` - tracks pickup state
- `CanBePlaced` - validates placement (checks grid occupancy)
- `_originalPosition/_originalRotation` - for cancellation
- `pickupValidMaterial/pickupInvalidMaterial` - visual feedback

**Methods:**
- `OnPickedUp(bool isNewObject)` - disables colliders, stores original state, unregisters from TownManager if existing
- `OnPlaced()` - restores colliders, registers with TownManager, restores materials
- `OnPlacementCancelled()` - destroys if new, restores position if existing
- `UpdateValidityVisuals(bool isValid)` - shows green/red materials
- `ValidatePlacement()` - checks if current position is valid

### 3. **BuildModeManager Simplified**

**Removed:**
- ❌ Ghost platform concept
- ❌ `_ghostPlatform`, `_ghostPlatformComponent`, `_ghostRenderers`
- ❌ `CreateGhost()`, `ClearGhost()`
- ❌ `UpdateGhostValidity()`, `UpdateGhostVisuals()`
- ❌ `RegisterPreviewPlatform()` calls
- ❌ Preview materials (now on GamePlatform)
- ❌ Adjacency checking (can add back if needed)
- ❌ ~300 lines of complex ghost management code

**Added:**
- ✅ `_currentPickup` (IPickupable) - tracks what's being placed/moved
- ✅ `SpawnPlatformForPlacement()` - spawns new platform
- ✅ `PickupExistingPlatform()` - picks up existing platform for moving
- ✅ `UpdatePickupPosition()` - moves pickup to follow mouse
- ✅ `PlacePickup()` - confirms placement
- ✅ `CancelPlacement()` - cancels and cleans up

**Result:** **~150 lines total** (was ~500+) - **70% code reduction!**

---

## How It Works

### Building a NEW Platform:
1. User clicks blueprint in UI
2. `BuildModeManager` spawns platform, gets `IPickupable`
3. Calls `OnPickedUp(isNewObject: true)`
4. Platform disables colliders, prepares for placement
5. Each frame: position updates to mouse, validity checked
6. User places: `OnPlaced()` registers with TownManager
7. User cancels: `OnPlacementCancelled()` destroys object

### Moving an EXISTING Platform:
1. User clicks existing platform
2. `BuildModeManager.PickupExistingPlatform(platform)`
3. Calls `OnPickedUp(isNewObject: false)` 
4. Platform unregisters from TownManager, stores original position
5. Each frame: position updates to mouse, validity checked
6. User places: `OnPlaced()` re-registers at new position
7. User cancels: `OnPlacementCancelled()` restores original position

---

## Benefits

### 1. **Simplicity**
- No "ghost" concept - it's just a regular platform that's temporarily picked up
- No complex registration/unregistration dance with TownManager
- Platform manages its own state (materials, colliders, etc.)

### 2. **Unified System**
- Same code for building NEW and moving EXISTING
- Same interface for platforms, props, decorations, etc.
- Easy to extend to new types (just implement IPickupable)

### 3. **Clean Separation**
- `BuildModeManager` - handles input, mouse tracking, placement confirmation
- `GamePlatform` - validates placement, manages visuals, registers with managers
- `TownManager` - tracks registered platforms, manages grid occupancy

### 4. **Extensible**
Want to add pickup/placement for props/decorations?
```csharp
public class PropObject : MonoBehaviour, IPickupable
{
    // Implement interface methods
}
```
Done! BuildModeManager works with it automatically.

---

## Migration Notes

### Prefab Changes Required:
1. **Add materials to GamePlatform prefabs:**
   - `pickupValidMaterial` (green semi-transparent)
   - `pickupInvalidMaterial` (red semi-transparent)

2. **Remove BuildModeManager material references:**
   - `previewValidMaterial` - moved to GamePlatform
   - `previewInvalidMaterial` - moved to GamePlatform

### TownManager Changes:
- **Remove** `RegisterPreviewPlatform()` method (no longer needed)
- Ghost platforms never existed in TownManager's tracking

### Testing:
1. Place first platform - should work normally
2. Place second platform adjacent - railings should hide
3. Try to place in occupied space - should show red
4. Cancel placement - new platforms destroyed
5. (Future) Pick up existing platform, move it, place - should work identically

---

## Debug Logs

**BuildModeManager:**
- `Spawned 'PlatformName' for placement.`
- `Picked up existing platform 'Name' for moving.`
- `Placed 'Name' at (x, y, z)`
- `Cancelled placement of 'Name'`

**GamePlatform (via IPickupable):**
- Platform handles all its own state silently
- Visual feedback through materials (green/red)

---

## Code Comparison

### Before (Ghost System):
```csharp
// Create ghost
_ghostPlatform = Instantiate(prefab);
_ghostPlatformComponent = _ghostPlatform.GetComponent<GamePlatform>();
_ghostRenderers.Clear();
_ghostRenderers.AddRange(_ghostPlatform.GetComponentsInChildren<Renderer>());
// Disable colliders, NavMesh, etc.
townManager.RegisterPreviewPlatform(_ghostPlatformComponent, level);

// Each frame:
UpdateGhostPosition();
UpdateGhostValidity();
UpdateGhostVisuals();

// Place:
townManager.UnregisterPlatform(_ghostPlatformComponent);
GameObject placed = Instantiate(prefab, _ghostPos, _ghostRot);
townManager.RegisterPlatform(placed.GetComponent<GamePlatform>(), cells, level, true);
// Keep ghost active for next placement
```

### After (Pickup System):
```csharp
// Spawn platform
GameObject platform = Instantiate(blueprint.RuntimePrefab);
_currentPickup = platform.GetComponent<IPickupable>();
_currentPickup.OnPickedUp(isNewObject: true);

// Each frame:
UpdatePickupPosition();
_currentPickup.UpdateValidityVisuals(_currentPickup.CanBePlaced);

// Place:
_currentPickup.OnPlaced(); // Platform handles everything
_currentPickup = null;
```

**Much cleaner!**

---

## Future Enhancements

1. **Props/Decorations:** Implement IPickupable on decoration objects
2. **Multi-Select:** Extend to pick up groups of objects
3. **Copy/Paste:** Store IPickupable state and recreate
4. **Undo/Redo:** Stack of placement/movement operations
5. **Drag-to-Place:** Hold and drag to place multiple

All of these are much easier now!

---

## Summary

✅ **70% less code**
✅ **Unified build/move system**
✅ **Extensible to any object type**
✅ **Platform handles own state**
✅ **No complex ghost tracking**
✅ **Clean, simple, maintainable**

This is how build systems **should** work!

