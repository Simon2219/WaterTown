using System;
using UnityEngine;
using UnityEngine.AI;

namespace Agents
{
    /// <summary>
    /// Agent status for visual feedback and logic.
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
    /// LOD level for performance optimization.
    /// </summary>
    public enum AgentLODLevel : byte
    {
        /// <summary>Full updates every frame, full visuals.</summary>
        High = 0,
        /// <summary>Updates every 2-3 frames, full visuals.</summary>
        Medium = 1,
        /// <summary>Updates every 5 frames, simplified visuals.</summary>
        Low = 2,
        /// <summary>Visuals disabled, minimal updates.</summary>
        Culled = 3
    }

    /// <summary>
    /// Individual NPC agent component.
    /// Handles NavMeshAgent control, status, and visual state.
    /// Performance is managed by NPCManager through LOD and culling.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavMeshAgent))]
    public class NPCAgent : MonoBehaviour
    {
        #region Events
        
        /// <summary>Fired when status changes.</summary>
        public event Action<NPCAgent, AgentStatus> StatusChanged;
        
        /// <summary>Fired when destination is reached.</summary>
        public event Action<NPCAgent> DestinationReached;
        
        /// <summary>Fired when selection state changes.</summary>
        public event Action<NPCAgent, bool> SelectionChanged;
        
        /// <summary>Fired when LOD level changes.</summary>
        public event Action<NPCAgent, AgentLODLevel> LODChanged;
        
        #endregion
        
        #region Public Properties
        
        /// <summary>Unique ID for this agent.</summary>
        public int AgentId { get; private set; }
        
        /// <summary>Current status.</summary>
        public AgentStatus Status => _status;
        
        /// <summary>Whether this agent is selected.</summary>
        public bool IsSelected => _isSelected;
        
        /// <summary>Whether this agent is moving toward a destination.</summary>
        public bool IsMoving => _navAgent && _navAgent.hasPath && !_navAgent.isStopped && 
                                _navAgent.remainingDistance > _navAgent.stoppingDistance;
        
        /// <summary>Current destination (if any).</summary>
        public Vector3? CurrentDestination => _hasDestination ? _navAgent.destination : (Vector3?)null;
        
        /// <summary>Reference to NavMeshAgent.</summary>
        public NavMeshAgent NavAgent => _navAgent;
        
        /// <summary>Current LOD level.</summary>
        public AgentLODLevel LODLevel => _lodLevel;
        
        /// <summary>Whether visuals are currently enabled.</summary>
        public bool VisualsEnabled => _visualsEnabled;
        
        /// <summary>Distance to main camera (updated by manager).</summary>
        public float CameraDistance { get; internal set; }
        
        #endregion
        
        #region Serialized Fields
        
        [Header("Debug")]
        [SerializeField] private bool showDebugInfo;
        
        #endregion
        
        #region Internal State
        
        internal NPCManager Manager;
        internal Renderer AgentRenderer;
        internal Material AgentMaterial;
        
        private NavMeshAgent _navAgent;
        private AgentStatus _status = AgentStatus.Idle;
        private AgentStatus _statusBeforeSelection = AgentStatus.Idle;
        private AgentLODLevel _lodLevel = AgentLODLevel.High;
        private bool _isSelected;
        private bool _hasDestination;
        private bool _wasAtDestination = true;
        private bool _visualsEnabled = true;
        private Vector3 _baseScale;
        
        // Frame skip tracking
        private int _frameOffset;
        private static int _globalFrameOffset;
        
        #endregion
        
        #region Initialization
        
        /// <summary>
        /// Initialize the agent. Called by NPCManager after spawn.
        /// </summary>
        internal void Initialize(NPCManager manager, int agentId)
        {
            Manager = manager;
            AgentId = agentId;
            _navAgent = GetComponent<NavMeshAgent>();
            AgentRenderer = GetComponent<Renderer>();
            
            if (AgentRenderer)
            {
                AgentMaterial = AgentRenderer.material;
            }
            
            _baseScale = transform.localScale;
            
            // Stagger frame updates to distribute load
            _frameOffset = _globalFrameOffset++ % 10;
            
            manager.RegisterAgent(this);
        }
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _navAgent = GetComponent<NavMeshAgent>();
        }
        
        private void OnDestroy()
        {
            if (Manager)
            {
                Manager.UnregisterAgent(this);
            }
            
            if (AgentMaterial)
            {
                Destroy(AgentMaterial);
            }
        }
        
        #endregion
        
        #region Update Methods (Called by Manager)
        
        /// <summary>
        /// Full update - called every frame for High LOD agents.
        /// </summary>
        internal void UpdateFull()
        {
            UpdateStatus();
            CheckDestinationReached();
        }
        
        /// <summary>
        /// Reduced update - called periodically for Medium/Low LOD agents.
        /// </summary>
        internal void UpdateReduced()
        {
            UpdateStatus();
            CheckDestinationReached();
        }
        
        /// <summary>
        /// Minimal update - called rarely for Culled agents.
        /// </summary>
        internal void UpdateMinimal()
        {
            // Only check if we've reached destination
            CheckDestinationReached();
        }
        
        /// <summary>
        /// Check if this agent should update this frame based on LOD.
        /// </summary>
        internal bool ShouldUpdateThisFrame(int frameCount)
        {
            return _lodLevel switch
            {
                AgentLODLevel.High => true,
                AgentLODLevel.Medium => (frameCount + _frameOffset) % 3 == 0,
                AgentLODLevel.Low => (frameCount + _frameOffset) % 5 == 0,
                AgentLODLevel.Culled => (frameCount + _frameOffset) % 15 == 0,
                _ => true
            };
        }
        
        private void UpdateStatus()
        {
            if (_isSelected) return;
            
            AgentStatus newStatus = DetermineStatus();
            
            if (newStatus != _status)
            {
                _status = newStatus;
                UpdateVisuals();
                StatusChanged?.Invoke(this, _status);
            }
        }
        
        private AgentStatus DetermineStatus()
        {
            if (!_navAgent) return AgentStatus.Error;
            
            if (_navAgent.pathStatus == NavMeshPathStatus.PathInvalid)
            {
                return AgentStatus.Error;
            }
            
            if (_hasDestination)
            {
                if (_navAgent.pathPending)
                {
                    return AgentStatus.Waiting;
                }
                
                if (_navAgent.remainingDistance <= _navAgent.stoppingDistance)
                {
                    _hasDestination = false;
                    return AgentStatus.Idle;
                }
                
                if (_navAgent.velocity.sqrMagnitude > 0.01f)
                {
                    return AgentStatus.Moving;
                }
                
                return AgentStatus.Waiting;
            }
            
            return AgentStatus.Idle;
        }
        
        private void CheckDestinationReached()
        {
            bool atDestination = !_hasDestination;
            
            if (atDestination && !_wasAtDestination)
            {
                DestinationReached?.Invoke(this);
            }
            
            _wasAtDestination = atDestination;
        }
        
        #endregion
        
        #region LOD & Culling
        
        /// <summary>
        /// Set LOD level. Called by NPCManager.
        /// </summary>
        internal void SetLODLevel(AgentLODLevel level)
        {
            if (_lodLevel == level) return;
            
            AgentLODLevel previousLevel = _lodLevel;
            _lodLevel = level;
            
            // Handle visual culling
            bool shouldShowVisuals = level != AgentLODLevel.Culled;
            if (shouldShowVisuals != _visualsEnabled)
            {
                SetVisualsEnabled(shouldShowVisuals);
            }
            
            LODChanged?.Invoke(this, _lodLevel);
        }
        
        /// <summary>
        /// Enable/disable visual components (renderer).
        /// NavMeshAgent continues to work.
        /// </summary>
        internal void SetVisualsEnabled(bool enabled)
        {
            if (_visualsEnabled == enabled) return;
            _visualsEnabled = enabled;
            
            if (AgentRenderer)
            {
                AgentRenderer.enabled = enabled;
            }
        }
        
        #endregion
        
        #region Visual Updates
        
        /// <summary>
        /// Update visual appearance based on status.
        /// </summary>
        internal void UpdateVisuals()
        {
            if (!_visualsEnabled || !Manager) return;
            
            // Update color
            if (AgentMaterial)
            {
                Color color = Manager.GetColorForStatus(_status);
                AgentMaterial.color = color;
            }
            
            // Update scale for selection
            transform.localScale = _isSelected 
                ? _baseScale * Manager.SelectedScaleMultiplier 
                : _baseScale;
        }
        
        /// <summary>
        /// Force visual update (e.g., after LOD change).
        /// </summary>
        internal void ForceVisualUpdate()
        {
            if (!_visualsEnabled) return;
            UpdateVisuals();
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
                SetStatus(AgentStatus.Error);
                return false;
            }
            
            bool result = _navAgent.SetDestination(hit.position);
            
            if (result)
            {
                _hasDestination = true;
                _wasAtDestination = false;
            }
            else
            {
                SetStatus(AgentStatus.Error);
            }
            
            return result;
        }
        
        /// <summary>
        /// Stops current movement.
        /// </summary>
        public void Stop()
        {
            if (_navAgent)
            {
                _navAgent.ResetPath();
                _hasDestination = false;
            }
        }
        
        /// <summary>
        /// Select this agent.
        /// </summary>
        public void Select()
        {
            if (_isSelected) return;
            
            _statusBeforeSelection = _status;
            _isSelected = true;
            _status = AgentStatus.Selected;
            
            UpdateVisuals();
            SelectionChanged?.Invoke(this, true);
            StatusChanged?.Invoke(this, _status);
        }
        
        /// <summary>
        /// Deselect this agent.
        /// </summary>
        public void Deselect()
        {
            if (!_isSelected) return;
            
            _isSelected = false;
            _status = _statusBeforeSelection;
            
            UpdateVisuals();
            SelectionChanged?.Invoke(this, false);
            StatusChanged?.Invoke(this, _status);
        }
        
        /// <summary>
        /// Toggle selection.
        /// </summary>
        public void ToggleSelection()
        {
            if (_isSelected) Deselect();
            else Select();
        }
        
        /// <summary>
        /// Manually set status.
        /// </summary>
        public void SetStatus(AgentStatus newStatus)
        {
            if (newStatus == AgentStatus.Selected)
            {
                Select();
                return;
            }
            
            _status = newStatus;
            UpdateVisuals();
            StatusChanged?.Invoke(this, _status);
        }
        
        /// <summary>
        /// Teleport to position (snapped to NavMesh).
        /// </summary>
        public bool Teleport(Vector3 worldPosition)
        {
            if (!NavMesh.SamplePosition(worldPosition, out NavMeshHit hit, 10f, NavMesh.AllAreas))
            {
                return false;
            }
            
            _navAgent.Warp(hit.position);
            _hasDestination = false;
            
            return true;
        }
        
        #endregion
        
        #region Debug
        
        private void OnDrawGizmosSelected()
        {
            if (!showDebugInfo || !_navAgent) return;
            
            // Draw path
            if (_navAgent.hasPath)
            {
                Gizmos.color = Color.yellow;
                var corners = _navAgent.path.corners;
                for (int i = 0; i < corners.Length - 1; i++)
                {
                    Gizmos.DrawLine(corners[i], corners[i + 1]);
                }
            }
            
            // Draw destination
            if (_hasDestination)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(_navAgent.destination, 0.3f);
            }
        }
        
        #endregion
    }
}
