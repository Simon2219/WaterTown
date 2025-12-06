# System References Guide - Efficient Patterns

## Core Backbone Systems

The following systems are the **backbone** of the game and are safe/efficient to reference directly:

- **`TownManager`** - Central town coordination
- **`WorldGrid`** - Grid system for positioning and alignment
- **`BuildModeManager`** - Build mode coordination
- **`GameUIController`** - UI management

---

## Efficient Reference Patterns

### Pattern 1: Serialized Field with Auto-Find (Recommended for Managers)

**Use for:** Manager scripts that need references to other managers

```csharp
[Header("References")]
[SerializeField] private TownManager townManager;
[SerializeField] private WorldGrid grid;

private void Awake()
{
    // Auto-find if not assigned in inspector
    if (!townManager) townManager = FindFirstObjectByType<TownManager>();
    if (!grid) grid = FindFirstObjectByType<WorldGrid>();
}
```

**Benefits:**
- ✅ Can be wired in inspector (explicit, visible)
- ✅ Auto-finds if not wired (convenient)
- ✅ Only searches once on startup
- ✅ Clear what dependencies exist

**Example:** `BuildModeManager`, `PlayerInputController`

---

### Pattern 2: Static Cached Field with Validation (Recommended for Entity Scripts)

**Use for:** Entity scripts (platforms, props, etc.) that need backbone systems

```csharp
// Static cache shared across all instances
private static TownManager _townManager;
private static WorldGrid _worldGrid;
private static bool _systemReferencesValidated = false;

private static void EnsureSystemReferences()
{
    if (_systemReferencesValidated) return;
    
    _townManager = FindFirstObjectByType<TownManager>();
    _worldGrid = FindFirstObjectByType<WorldGrid>();
    
    // Validate once at startup - if not found, log error and fail fast
    if (_townManager == null)
        Debug.LogError("[GamePlatform] TownManager not found! GamePlatform requires TownManager.");
    
    if (_worldGrid == null)
        Debug.LogError("[GamePlatform] WorldGrid not found! GamePlatform requires WorldGrid.");
    
    _systemReferencesValidated = true;
}

private void Awake()
{
    EnsureSystemReferences();
    // ... rest of Awake
}

// Usage (no null checks needed - validated at startup):
public void SomeMethod()
{
    _townManager.DoSomething();
    _worldGrid.DoSomethingElse();
}
```

**Benefits:**
- ✅ No inspector clutter (platforms don't need manual wiring)
- ✅ Only searches once across ALL instances
- ✅ Validates once at startup (fail fast if missing)
- ✅ No null checks needed after validation
- ✅ Clean, simple API

**Example:** `GamePlatform`, `PlatformModule`, `PlatformRailing`

---

### Pattern 3: Direct Singleton (For Special Cases)

**Use for:** True singleton systems

```csharp
public class TownManager : MonoBehaviour
{
    public static TownManager Instance { get; private set; }
    
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }
}

// Usage:
TownManager.Instance.DoSomething();
```

**⚠️ Avoid unless truly needed** - adds global coupling

---

## Anti-Patterns (DON'T DO THIS)

### ❌ Repeated FindFirstObjectByType Calls

**BAD:**
```csharp
public void OnPickedUp()
{
    var townManager = FindFirstObjectByType<TownManager>();
    townManager.UnregisterPlatform(this);
}

public void OnPlaced()
{
    var townManager = FindFirstObjectByType<TownManager>();
    townManager.RegisterPlatform(this);
}

public void OnCancelled()
{
    var townManager = FindFirstObjectByType<TownManager>();
    townManager.RegisterPlatform(this);
}
```

**Why it's bad:**
- 🔴 Searches entire scene EVERY time
- 🔴 Very expensive operation (O(n) scene objects)
- 🔴 Repeated unnecessarily

**GOOD:**
```csharp
// Cache once, validate at startup
private static TownManager _townManager;

private static void EnsureSystemReferences()
{
    if (_townManager == null)
    {
        _townManager = FindFirstObjectByType<TownManager>();
        if (_townManager == null)
            Debug.LogError("[GamePlatform] TownManager not found!");
    }
}

private void Awake()
{
    EnsureSystemReferences();
}

// No null checks needed - validated at startup
public void OnPickedUp()
{
    _townManager.UnregisterPlatform(this);
}

public void OnPlaced()
{
    _townManager.RegisterPlatform(this);
}

public void OnCancelled()
{
    _townManager.RegisterPlatform(this);
}
```

---

### ❌ GetComponent Every Frame

**BAD:**
```csharp
void Update()
{
    var renderer = GetComponent<Renderer>();
    renderer.enabled = IsVisible();
}
```

**GOOD:**
```csharp
private Renderer _renderer;

void Awake()
{
    _renderer = GetComponent<Renderer>();
}

void Update()
{
    _renderer.enabled = IsVisible();
}
```

---

## Current Implementation

### GamePlatform
```csharp
// Uses Pattern 2: Static Cached Field with Validation
private static TownManager _townManager;
private static WorldGrid _worldGrid;
private static bool _systemReferencesValidated = false;

private static void EnsureSystemReferences() { ... }

// Called in Awake() - validates once, then assumes valid
private void Awake()
{
    EnsureSystemReferences();
    // ...
}

// Usage throughout class (no null checks):
_townManager.DoSomething();
_worldGrid.DoSomethingElse();
```

### BuildModeManager
```csharp
// Uses Pattern 1: Serialized Field with Auto-Find
[SerializeField] private TownManager townManager;
[SerializeField] private WorldGrid grid;

private void Awake()
{
    if (!townManager) townManager = FindFirstObjectByType<TownManager>();
    if (!grid) grid = FindFirstObjectByType<WorldGrid>();
}
```

### TownManager
```csharp
// Uses Pattern 1 for its own dependencies
[SerializeField] private WorldGrid grid;

private void Awake()
{
    if (!grid) grid = FindFirstObjectByType<WorldGrid>();
}
```

---

## Performance Impact

### Scene Search Costs (FindFirstObjectByType)

| Objects in Scene | Search Time (approx) |
|------------------|---------------------|
| 100 objects      | ~0.05ms            |
| 1,000 objects    | ~0.5ms             |
| 10,000 objects   | ~5ms               |

**If called every frame at 60 FPS:**
- 100 objects: 3ms/frame (20% of 16ms budget) ❌
- 1,000 objects: 30ms/frame (BELOW 60 FPS) 🔴

**If cached (one-time search):**
- ANY scene size: ~0ms/frame after first access ✅

---

## When to Use Each Pattern

### Use Pattern 1 (Serialized + Auto-Find) when:
- ✅ Script is a Manager/Controller
- ✅ Few instances (1-10)
- ✅ Want inspector visibility
- ✅ Debugging/testing benefits from manual wiring

**Examples:**
- BuildModeManager
- PlayerInputController
- CameraController
- GameUIController

### Use Pattern 2 (Static Cached Property) when:
- ✅ Script is an Entity/Object
- ✅ Many instances (10-1000s)
- ✅ Don't need inspector wiring
- ✅ Want shared cache across all instances

**Examples:**
- GamePlatform (many platforms)
- PlatformModule (many modules)
- PlatformRailing (many railings)
- PropObject (many props)
- DecorativeObject (many decorations)

### Use Pattern 3 (Singleton) when:
- ⚠️ Truly need global access
- ⚠️ System must be unique
- ⚠️ Other patterns won't work

**Be cautious** - singletons add global coupling

---

## Migration Checklist

When updating a script to use cached references:

1. [ ] Identify which backbone systems it needs
2. [ ] Choose appropriate pattern (1 or 2)
3. [ ] Add cached reference(s)
4. [ ] Replace all `FindFirstObjectByType` calls
5. [ ] Test that it still works
6. [ ] Verify performance (if applicable)

---

## Summary

**Backbone Systems (fine to reference):**
- `TownManager`
- `WorldGrid`
- `BuildModeManager`
- `GameUIController`

**Efficient Patterns:**
- **Manager scripts:** Serialized field + auto-find in Awake
- **Entity scripts:** Static cached property
- **Never:** Repeated FindFirstObjectByType calls

**Performance:**
- ✅ Cache once: ~0ms/frame overhead
- ❌ Search every call: 0.05-5ms per call (multiplied by call frequency)

**Current Status:**
✅ GamePlatform - optimized (static cache)
✅ BuildModeManager - optimized (serialized + auto-find)
✅ TownManager - optimized (serialized + auto-find)

All systems now use efficient reference patterns! 🚀

