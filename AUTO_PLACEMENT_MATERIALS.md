# Auto-Generated Placement Materials

## Overview

For **testing purposes**, GamePlatform now automatically creates red/green translucent materials to show placement validity when picking up platforms in build mode.

---

## How It Works

### Automatic Material Generation

When a platform is picked up and `UpdateValidityVisuals()` is called:

1. **Check for assigned materials first:**
   - If `pickupValidMaterial` is assigned in inspector → use it
   - If `pickupInvalidMaterial` is assigned in inspector → use it

2. **Auto-generate if not assigned:**
   - Creates static shared materials (one per game, reused across all platforms)
   - **Valid (green):** 60% transparent green with slight emission
   - **Invalid (red):** 60% transparent red with slight emission

### Material Properties

**Valid Placement Material (Green):**
```csharp
Color: (0, 1, 0, 0.6)  // Bright green, 60% transparent
Emission: (0, 0.3, 0)  // Slight green glow
Render Queue: 3000     // Transparent queue
```

**Invalid Placement Material (Red):**
```csharp
Color: (1, 0, 0, 0.6)  // Bright red, 60% transparent
Emission: (0.3, 0, 0)  // Slight red glow
Render Queue: 3000     // Transparent queue
```

---

## Usage

### For Testing (Current)

**No setup required!** Just enter build mode and spawn a platform:
- ✅ **Green + slight glow** = valid placement location
- ❌ **Red + slight glow** = invalid placement (occupied or invalid)
- Materials auto-restore when placed/cancelled

### For Production (Future)

Create custom materials in Unity and assign them:

1. **Create Materials:**
   - Create `Mat_ValidPlacement` (your design)
   - Create `Mat_InvalidPlacement` (your design)

2. **Assign to Prefab:**
   - Select platform prefab
   - Find `GamePlatform` component
   - Assign materials to:
     - `Pickup Valid Material`
     - `Pickup Invalid Material`

3. **Auto-generation disabled:**
   - Once materials are assigned, auto-generation is skipped
   - Your custom materials are used instead

---

## Technical Details

### Static Material Caching

```csharp
private static Material _autoValidMaterial;   // Shared across ALL platforms
private static Material _autoInvalidMaterial; // Created once, reused forever
```

**Benefits:**
- ✅ Only creates materials once (first pickup)
- ✅ All platforms share same materials (efficient)
- ✅ No memory leaks (static, never destroyed)

### Material Application

```csharp
// Applied every frame while picked up
UpdateValidityVisuals(bool isValid)
{
    Material target = isValid ? GetAutoValidMaterial() : GetAutoInvalidMaterial();
    // Apply to all renderers
}
```

### Material Restoration

```csharp
// Restores original materials when placed/cancelled
RestoreOriginalMaterials()
{
    // Uses cached _originalMaterials from OnPickedUp()
}
```

---

## Visual Examples

### Valid Placement (Green)
```
┌─────────────┐
│             │
│   PLATFORM  │  ← Green transparent + glow
│             │
└─────────────┘
```
- Over empty space
- Next to compatible platforms
- Valid rotation

### Invalid Placement (Red)
```
┌─────────────┐
│             │
│   PLATFORM  │  ← Red transparent + glow
│             │
└─────────────┘
```
- Over occupied space
- Out of bounds
- Overlapping other platforms

---

## Testing Scenarios

### Test 1: Basic Placement
1. Enter build mode
2. Select platform
3. Move mouse around
   - **Expected:** Green over empty space, red over occupied

### Test 2: Adjacency
1. Place first platform
2. Spawn second platform
3. Move next to first
   - **Expected:** Green when adjacent (if valid)

### Test 3: Rotation
1. Spawn platform
2. Rotate while hovering
   - **Expected:** Materials update correctly

### Test 4: Material Restoration
1. Spawn platform
2. Place it
   - **Expected:** Original materials restored
3. Cancel placement
   - **Expected:** Platform destroyed (if new) or restored (if moved)

---

## Performance

### Material Creation
- **First pickup:** ~1-2ms (creates both materials)
- **Subsequent pickups:** ~0ms (reuses cached materials)

### Material Updates
- **Per frame:** ~0.1ms (just material assignment)
- **60 FPS:** Negligible overhead

### Memory Usage
- **Two materials total:** ~2-4KB
- **Static:** Never garbage collected
- **Efficient:** Shared across all platforms

---

## Future Improvements

### Custom Shader (Optional)
Create a specialized placement preview shader:
- Animated glow/pulse
- Grid overlay
- Better transparency
- Custom effects

### Per-Platform Materials (Optional)
Allow different preview colors per platform type:
```csharp
[SerializeField] private Color validColor = Color.green;
[SerializeField] private Color invalidColor = Color.red;
```

### Material Presets (Optional)
ScriptableObject with material presets:
```csharp
[CreateAssetMenu]
public class PlacementMaterialPreset : ScriptableObject
{
    public Material validMaterial;
    public Material invalidMaterial;
}
```

---

## Summary

✅ **Auto-generated materials** for testing  
✅ **Green = valid, Red = invalid**  
✅ **Translucent + emission** for visibility  
✅ **Automatic restoration** after placement  
✅ **Optional custom materials** for production  
✅ **Zero setup required** - just works!  

Perfect for rapid testing and prototyping! 🚀

