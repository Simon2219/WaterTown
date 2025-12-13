# NPC Agent System - Implementation Guide

## Overview

This document describes the NPC Agent system for WaterTown, designed for 500+ agents using **Unity DOTS (ECS + Burst + Jobs)** in a hybrid architecture:

- **GameObjects with NavMeshAgent** for pathfinding (Unity's NavMesh doesn't support pure ECS)
- **ECS Entities** for agent data and state processing (Burst-compiled, parallelized)
- **ECS Systems** for batch updates across all agents

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         ECS WORLD                                │
├─────────────────────────────────────────────────────────────────┤
│  AgentData (IComponentData)     AgentManagedData (class)        │
│  ├── AgentId                    ├── NPCAgent reference          │
│  ├── Status                     ├── NavMeshAgent reference      │
│  ├── Position/Velocity          └── Renderer reference          │
│  ├── HasDestination                                              │
│  └── PathStatus                                                  │
├─────────────────────────────────────────────────────────────────┤
│  SYSTEMS (Burst-compiled, parallel where possible)              │
│  ├── AgentNavMeshReadSystem    → Reads NavMeshAgent state       │
│  ├── AgentStatusUpdateSystem   → Updates status (Burst/Jobs)    │
│  ├── AgentDestinationReached   → Detects arrival, fires events  │
│  └── AgentVisualsUpdateSystem  → Applies colors/scale           │
└─────────────────────────────────────────────────────────────────┘
                              ↕ Sync
┌─────────────────────────────────────────────────────────────────┐
│                      GAMEOBJECT WORLD                           │
├─────────────────────────────────────────────────────────────────┤
│  NPCManager (Singleton)         NPCAgent (Per-Agent)            │
│  ├── Entity Factory             ├── NavMeshAgent control        │
│  ├── Agent Registry             ├── ECS Entity link             │
│  └── Configuration              └── Event callbacks             │
│                                                                  │
│  NPCAgentSpawner (Input)                                        │
│  ├── Spawn/Select/Move actions                                  │
│  └── Raycast handling                                            │
└─────────────────────────────────────────────────────────────────┘
```

---

## Quick Setup

### 1. Create NPCManager GameObject

1. Create empty GameObject: `GameObject > Create Empty`
2. Name it: `NPCManager`
3. Add component: `Agents > NPCManager`
4. Configure settings as desired (see Configuration section)

### 2. Create NPCAgentSpawner GameObject

1. Create empty GameObject: `GameObject > Create Empty`
2. Name it: `NPCAgentSpawner`
3. Add component: `Agents > NPCAgentSpawner`
4. **Assign InputAction references** in Inspector:
   - `Spawn Agent Action`: Your spawn input action
   - `Select Move Action`: Your select/move input action
5. Assign `Main Camera` reference (or leave null to use `Camera.main`)

### 3. Create Input Actions

In your `baseControls.inputactions`, create two new actions (suggested in Player or a new Debug map):

**Example actions:**
- `SpawnAgent` - Button type, bind to a key (e.g., `N`)
- `SelectMoveAgent` - Button type, bind to `Left Mouse Button`

Then create **InputActionReference** assets or directly reference the actions.

---

## Configuration

### NPCManager Settings

| Setting | Default | Description |
|---------|---------|-------------|
| **Agent Prefab** | null | Custom prefab (null = procedural capsule) |
| **Default Speed** | 3.5 | Movement speed (units/sec) |
| **Default Angular Speed** | 120 | Rotation speed (deg/sec) |
| **Default Acceleration** | 8 | Acceleration rate |
| **Default Stopping Distance** | 0.1 | Distance to stop from target |
| **Height Offset** | 0.05 | Float height above NavMesh |
| **Agent Radius** | 0.3 | Capsule radius |
| **Agent Height** | 1.8 | Capsule height |
| **Status Colors** | Various | Colors for each status |
| **Selected Scale Multiplier** | 1.1 | Scale when selected |
| **Max Updates Per Frame** | 100 | Batch size (0 = all) |

### NPCAgentSpawner Settings

| Setting | Default | Description |
|---------|---------|-------------|
| **Spawn Agent Action** | - | InputActionReference for spawning |
| **Select Move Action** | - | InputActionReference for select/move |
| **Spawn Point** | null | Optional transform for fixed spawn |
| **Use Spawn Point** | false | Use spawn point vs mouse cursor |
| **Raycast Layer Mask** | Everything | Layers for spawn/move raycast |
| **Agent Layer Mask** | Everything | Layers for agent selection |
| **Max Raycast Distance** | 1000 | Maximum ray distance |
| **Debug Logs** | false | Enable debug logging |

---

## Agent Statuses

| Status | Color (Default) | Trigger |
|--------|-----------------|---------|
| **Idle** | Green | No destination, standing still |
| **Moving** | Blue | Walking toward destination |
| **Selected** | Yellow | Player has selected this agent |
| **Waiting** | Orange | Path calculating, blocked, or queued |
| **Error** | Red | Invalid path or NavMesh error |

---

## Public API

### NPCManager

```csharp
// Singleton access
NPCManager.Instance

// Spawn agent at world position (snapped to NavMesh)
NPCAgent agent = NPCManager.Instance.SpawnAgent(worldPosition);

// Spawn at transform
NPCAgent agent = NPCManager.Instance.SpawnAgentAtPoint(spawnTransform);

// Get all agents
IReadOnlyCollection<NPCAgent> agents = NPCManager.Instance.AllAgents;
int count = NPCManager.Instance.AgentCount;

// Destroy all agents
NPCManager.Instance.DestroyAllAgents();

// Get color for status
Color color = NPCManager.Instance.GetColorForStatus(NPCAgent.AgentStatus.Moving);

// Events
NPCManager.Instance.AgentSpawned += OnAgentSpawned;
NPCManager.Instance.AgentDestroyed += OnAgentDestroyed;
```

### NPCAgent

```csharp
// Movement
bool success = agent.SetDestination(targetPosition);
agent.Stop();
agent.Teleport(newPosition);

// Selection
agent.Select();
agent.Deselect();
agent.ToggleSelection();
bool isSelected = agent.IsSelected;

// Status
NPCAgent.AgentStatus status = agent.Status;
agent.SetStatus(NPCAgent.AgentStatus.Waiting);
bool isMoving = agent.IsMoving;
Vector3? destination = agent.CurrentDestination;

// Properties
int id = agent.AgentId;
NavMeshAgent nav = agent.NavAgent;

// Events
agent.StatusChanged += (agent, newStatus) => { };
agent.DestinationReached += (agent) => { };
agent.SelectionChanged += (agent, isSelected) => { };
```

### NPCAgentSpawner

```csharp
// Manual spawning
spawner.SpawnAgent();  // At cursor or spawn point
NPCAgent agent = spawner.SpawnAgentAt(worldPosition);

// Selection
spawner.SelectAgent(agent);
spawner.DeselectAgent();
NPCAgent selected = spawner.SelectedAgent;

// Move command
spawner.IssueMoveCommand(destination);

// Events
spawner.OnAgentSpawned += (agent) => { };
spawner.OnAgentSelected += (agent) => { };
spawner.OnAgentDeselected += (agent) => { };
spawner.OnMoveCommandIssued += (agent, destination) => { };
```

---

## NavMesh Compatibility

The system works with your existing platform NavMesh setup:

- **NavMeshSurface**: Each platform builds its own NavMesh
- **NavMeshLinks**: Connections between platforms now use `agentTypeID = -1` (all agents)
- **Area Mask**: Agents use `NavMesh.AllAreas` to traverse all walkable areas

### Important Change Made

In `PlatformManager.CreateNavLinkBetween()`, the NavMeshLink now uses:

```csharp
link.agentTypeID = -1;  // All agent types can traverse
```

This ensures NPC agents can walk across platform connections regardless of their NavMesh agent type.

---

## Performance Notes

### DOTS Processing

The system uses Unity DOTS for high-performance agent processing:

| System | Burst | Parallel | Purpose |
|--------|-------|----------|---------|
| `AgentNavMeshReadSystem` | ❌ | ❌ | Reads from NavMeshAgent (managed) |
| `AgentStatusUpdateSystem` | ✅ | ✅ | Updates agent status via IJobEntity |
| `AgentDestinationReachedSystem` | ❌ | ❌ | Fires events (managed callbacks) |
| `AgentVisualsUpdateSystem` | ❌ | ❌ | Updates materials/scale (managed) |

### Why Hybrid?

Unity's NavMeshAgent is a MonoBehaviour - it doesn't work in pure ECS. The hybrid approach:

1. **NavMeshAgent (GameObject)**: Handles pathfinding, obstacle avoidance
2. **ECS Entity**: Stores state, processed by Burst-compiled systems
3. **Sync Systems**: Bridge between the two worlds

### Scaling

| Agent Count | Performance | Notes |
|-------------|-------------|-------|
| 100-500 | Excellent | Burst handles all status updates in parallel |
| 500-1000 | Good | NavMeshAgent may become bottleneck |
| 1000+ | Varies | Consider LOD, spatial partitioning, pooling |

### Future Optimizations

1. **Object Pooling**: Reuse GameObjects instead of Instantiate/Destroy
2. **Spatial Partitioning**: Quad-tree for selection queries (ECS-based)
3. **LOD System**: Reduce NavMeshAgent updates for distant agents
4. **Full ECS Navigation**: When `com.unity.ai.navigation.entities` matures

---

## Troubleshooting

### Agents Don't Move

1. Check NavMesh is baked on platforms
2. Ensure NavMeshLinks exist between platforms
3. Verify agent spawn position is on NavMesh
4. Check `Debug Logs` on spawner for error messages

### Agents Can't Cross Platform Connections

1. Verify NavMeshLinks have `agentTypeID = -1` (check PlatformManager change)
2. Ensure `link.bidirectional = true`
3. Check agent's `areaMask` includes the link's area

### Selection Doesn't Work

1. Ensure agents have Colliders (procedural capsules include them)
2. Check `Agent Layer Mask` includes agent layer
3. Verify camera is assigned on NPCAgentSpawner

### Performance Issues with Many Agents

1. Reduce `Max Updates Per Frame`
2. Increase `Stopping Distance` to reduce pathfinding precision
3. Disable `Debug Logs`
4. Consider implementing object pooling

---

## File Structure

```
Assets/Scripts/Agents/
├── Agents.asmdef                    - Assembly definition (references Entities, Burst)
├── NPCManager.cs                    - Central management, entity factory
├── NPCAgent.cs                      - MonoBehaviour bridge to ECS entity
├── NPCAgentSpawner.cs               - Input handling & spawning
├── Components/
│   └── AgentComponents.cs           - ECS IComponentData structs
└── Systems/
    ├── AgentStatusUpdateSystem.cs   - Burst-compiled status processing
    ├── AgentNavMeshSyncSystem.cs    - Syncs NavMeshAgent ↔ ECS
    └── AgentDestinationReachedSystem.cs - Event detection
```

---

## Example: Custom Agent Behavior

To add custom behavior (e.g., workers that gather resources):

```csharp
using Agents;
using UnityEngine;

public class WorkerAgent : MonoBehaviour
{
    private NPCAgent _agent;
    
    void Start()
    {
        _agent = GetComponent<NPCAgent>();
        _agent.DestinationReached += OnReachedDestination;
    }
    
    void OnReachedDestination(NPCAgent agent)
    {
        // Start gathering, then find next task
        agent.SetStatus(NPCAgent.AgentStatus.Waiting);
        // ... custom logic
    }
}
```

---

## Version History

- **v1.0** (Initial) - Basic agent system with hybrid architecture
  - Spawning, selection, movement
  - Status-based coloring
  - Batch processing for 500+ agents
