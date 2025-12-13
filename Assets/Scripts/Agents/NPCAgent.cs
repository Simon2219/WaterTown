using System;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;

namespace Agents
{
    #region ECS Components
    
    /// <summary>
    /// Agent status enum used by both ECS and MonoBehaviour.
    /// </summary>
    public enum AgentStatus : byte
    {
        Idle = 0,
        Moving = 1,
        Selected = 2,
        Waiting = 3,
        Error = 4
    }

    /// <summary>
    /// Path status enum (mirrors NavMeshPathStatus).
    /// </summary>
    public enum PathStatus : byte
    {
        Complete = 0,
        Partial = 1,
        Invalid = 2,
        Pending = 3
    }

    /// <summary>
    /// Core agent data stored in ECS for efficient batch processing.
    /// </summary>
    public struct AgentData : IComponentData
    {
        public int AgentId;
        public AgentStatus Status;
        public AgentStatus StatusBeforeSelection;
        public bool IsSelected;
        public bool HasDestination;
        public float3 CurrentPosition;
        public float3 TargetPosition;
        public float3 Velocity;
        public float RemainingDistance;
        public float StoppingDistance;
        public PathStatus PathStatus;
    }

    /// <summary>
    /// Tag component for agents that need visual updates.
    /// </summary>
    public struct AgentVisualsDirty : IComponentData { }

    /// <summary>
    /// Tag component for newly spawned agents that need initialization.
    /// </summary>
    public struct AgentNeedsInit : IComponentData { }

    /// <summary>
    /// Link to the managed GameObject (NavMeshAgent).
    /// </summary>
    public class AgentManagedData : IComponentData
    {
        public NPCAgent Agent;
        public NavMeshAgent NavMeshAgent;
        public Renderer Renderer;
    }

    /// <summary>
    /// Agent visual configuration (colors, scale).
    /// </summary>
    public struct AgentVisualConfig : IComponentData
    {
        public float SelectedScaleMultiplier;
        public float BaseScaleX;
        public float BaseScaleY;
        public float BaseScaleZ;
    }
    
    #endregion

    /// <summary>
    /// Individual NPC agent component. Bridges GameObject (NavMeshAgent) with ECS Entity.
    /// Movement and visual state are processed by DOTS systems.
    /// This MonoBehaviour handles the NavMeshAgent control and event callbacks.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavMeshAgent))]
    public class NPCAgent : MonoBehaviour
    {
        #region Events
        
        /// <summary>
        /// Fired when this agent's status changes.
        /// </summary>
        public event Action<NPCAgent, AgentStatus> StatusChanged;
        
        /// <summary>
        /// Fired when this agent reaches its destination.
        /// </summary>
        public event Action<NPCAgent> DestinationReached;
        
        /// <summary>
        /// Fired when this agent is selected/deselected.
        /// </summary>
        public event Action<NPCAgent, bool> SelectionChanged;
        
        #endregion
        
        #region Public Properties
        
        /// <summary>
        /// Current status of this agent (read from ECS).
        /// </summary>
        public AgentStatus Status => _cachedStatus;
        
        /// <summary>
        /// Whether this agent is currently selected.
        /// </summary>
        public bool IsSelected => _isSelected;
        
        /// <summary>
        /// Whether this agent is currently moving toward a destination.
        /// </summary>
        public bool IsMoving => _navAgent && _navAgent.hasPath && !_navAgent.isStopped && 
                                _navAgent.remainingDistance > _navAgent.stoppingDistance;
        
        /// <summary>
        /// Current destination (if any).
        /// </summary>
        public Vector3? CurrentDestination => _hasDestination ? _navAgent.destination : null;
        
        /// <summary>
        /// Reference to the NavMeshAgent component.
        /// </summary>
        public NavMeshAgent NavAgent => _navAgent;
        
        /// <summary>
        /// Unique ID for this agent.
        /// </summary>
        public int AgentId { get; private set; }
        
        /// <summary>
        /// The ECS Entity linked to this agent.
        /// </summary>
        public Entity LinkedEntity => _linkedEntity;
        
        #endregion
        
        #region Private State
        
        private NPCManager _manager;
        private NavMeshAgent _navAgent;
        private Entity _linkedEntity;
        
        private AgentStatus _cachedStatus = AgentStatus.Idle;
        private AgentStatus _previousStatus = AgentStatus.Idle;
        private AgentStatus _statusBeforeSelection = AgentStatus.Idle;
        private bool _isSelected;
        private bool _hasDestination;
        private bool _wasAtDestination = true;
        private bool _isInitialized;
        
        #endregion
        
        #region Initialization
        
        /// <summary>
        /// Initialize the agent with manager reference and ECS entity.
        /// Called by NPCManager after spawn.
        /// </summary>
        internal void Initialize(NPCManager manager, NavMeshAgent navAgent, Entity entity, int agentId)
        {
            if (_isInitialized) return;
            
            _manager = manager;
            _navAgent = navAgent;
            _linkedEntity = entity;
            AgentId = agentId;
            
            _manager.RegisterAgent(this);
            _isInitialized = true;
        }
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            if (!_navAgent)
            {
                _navAgent = GetComponent<NavMeshAgent>();
            }
        }
        
        private void OnDestroy()
        {
            if (_manager)
            {
                _manager.UnregisterAgent(this);
            }
        }
        
        #endregion
        
        #region ECS Sync
        
        /// <summary>
        /// Called by ECS system to sync state from AgentData to this MonoBehaviour.
        /// </summary>
        internal void SyncFromECS(AgentData data)
        {
            AgentStatus newStatus = data.Status;
            
            if (newStatus != _previousStatus)
            {
                _cachedStatus = newStatus;
                StatusChanged?.Invoke(this, _cachedStatus);
                _previousStatus = newStatus;
            }
            
            bool atDestination = !data.HasDestination && _hasDestination;
            if (atDestination && !_wasAtDestination)
            {
                _hasDestination = false;
                DestinationReached?.Invoke(this);
            }
            _wasAtDestination = !data.HasDestination;
            _hasDestination = data.HasDestination;
        }
        
        /// <summary>
        /// Pushes current state to ECS entity.
        /// </summary>
        private void PushToECS()
        {
            if (_linkedEntity == Entity.Null || _manager == null) return;
            
            var data = _manager.GetAgentData(_linkedEntity);
            
            data.IsSelected = _isSelected;
            data.HasDestination = _hasDestination;
            data.StatusBeforeSelection = _statusBeforeSelection;
            
            if (_isSelected)
            {
                data.Status = AgentStatus.Selected;
            }
            
            if (_hasDestination && _navAgent != null)
            {
                data.TargetPosition = _navAgent.destination;
            }
            
            _manager.UpdateAgentData(_linkedEntity, data);
            _manager.MarkVisualsDirty(_linkedEntity);
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Sets the agent's movement destination.
        /// </summary>
        public bool SetDestination(Vector3 destination)
        {
            if (!_navAgent)
            {
                Debug.LogWarning($"[NPCAgent] Agent {AgentId} has no NavMeshAgent.");
                return false;
            }
            
            if (!NavMesh.SamplePosition(destination, out NavMeshHit hit, 5f, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[NPCAgent] Agent {AgentId} could not find NavMesh position near {destination}");
                SetErrorState();
                return false;
            }
            
            bool result = _navAgent.SetDestination(hit.position);
            
            if (result)
            {
                _hasDestination = true;
                _wasAtDestination = false;
                PushToECS();
            }
            else
            {
                SetErrorState();
            }
            
            return result;
        }
        
        /// <summary>
        /// Stops the agent's current movement.
        /// </summary>
        public void Stop()
        {
            if (_navAgent)
            {
                _navAgent.ResetPath();
                _hasDestination = false;
                PushToECS();
            }
        }
        
        /// <summary>
        /// Selects this agent.
        /// </summary>
        public void Select()
        {
            if (_isSelected) return;
            
            _statusBeforeSelection = _cachedStatus;
            _isSelected = true;
            _cachedStatus = AgentStatus.Selected;
            
            SelectionChanged?.Invoke(this, true);
            StatusChanged?.Invoke(this, _cachedStatus);
            
            PushToECS();
        }
        
        /// <summary>
        /// Deselects this agent.
        /// </summary>
        public void Deselect()
        {
            if (!_isSelected) return;
            
            _isSelected = false;
            _cachedStatus = _statusBeforeSelection;
            
            SelectionChanged?.Invoke(this, false);
            StatusChanged?.Invoke(this, _cachedStatus);
            
            if (_linkedEntity != Entity.Null && _manager != null)
            {
                var data = _manager.GetAgentData(_linkedEntity);
                data.IsSelected = false;
                data.Status = _statusBeforeSelection;
                _manager.UpdateAgentData(_linkedEntity, data);
                _manager.MarkVisualsDirty(_linkedEntity);
            }
        }
        
        /// <summary>
        /// Toggles selection state.
        /// </summary>
        public void ToggleSelection()
        {
            if (_isSelected)
                Deselect();
            else
                Select();
        }
        
        /// <summary>
        /// Manually sets the agent status.
        /// </summary>
        public void SetStatus(AgentStatus newStatus)
        {
            if (newStatus == AgentStatus.Selected)
            {
                Select();
                return;
            }
            
            _cachedStatus = newStatus;
            
            if (_linkedEntity != Entity.Null && _manager != null)
            {
                var data = _manager.GetAgentData(_linkedEntity);
                data.Status = newStatus;
                _manager.UpdateAgentData(_linkedEntity, data);
                _manager.MarkVisualsDirty(_linkedEntity);
            }
            
            StatusChanged?.Invoke(this, _cachedStatus);
        }
        
        /// <summary>
        /// Teleports agent to a new position (snapped to NavMesh).
        /// </summary>
        public bool Teleport(Vector3 worldPosition)
        {
            if (!NavMesh.SamplePosition(worldPosition, out NavMeshHit hit, 10f, NavMesh.AllAreas))
            {
                return false;
            }
            
            _navAgent.Warp(hit.position);
            _hasDestination = false;
            
            PushToECS();
            
            return true;
        }
        
        private void SetErrorState()
        {
            _cachedStatus = AgentStatus.Error;
            
            if (_linkedEntity != Entity.Null && _manager != null)
            {
                var data = _manager.GetAgentData(_linkedEntity);
                data.Status = AgentStatus.Error;
                _manager.UpdateAgentData(_linkedEntity, data);
                _manager.MarkVisualsDirty(_linkedEntity);
            }
            
            StatusChanged?.Invoke(this, _cachedStatus);
        }
        
        #endregion
    }
}
