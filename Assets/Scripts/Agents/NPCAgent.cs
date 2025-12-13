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
        
        /// <summary>Whether this agent has a destination set.</summary>
        public bool HasDestination => _hasDestination;
        
        /// <summary>Whether this agent is actively moving.</summary>
        public bool IsMoving => _navAgent && _navAgent.hasPath && 
                                _navAgent.remainingDistance > _navAgent.stoppingDistance &&
                                _navAgent.velocity.sqrMagnitude > 0.01f;
        
        /// <summary>Current destination (if any).</summary>
        public Vector3? CurrentDestination => _hasDestination && _navAgent ? (Vector3?)_navAgent.destination : null;
        
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
        [SerializeField] private bool logStatusChanges;
        
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
        private bool _initialized;
        
        // Frame skip tracking for staggered updates
        private int _frameOffset;
        private static int _globalFrameOffset;
        
        // Off-mesh link traversal
        private bool _isTraversingLink;
        private Vector3 _linkStartPos;
        private Vector3 _linkEndPos;
        private float _linkTraversalProgress;
        
        #endregion
        
        #region Initialization
        
        /// <summary>
        /// Initialize the agent. Called by NPCManager after spawn.
        /// </summary>
        internal void Initialize(NPCManager manager, int agentId)
        {
            if (_initialized) return;
            
            Manager = manager;
            AgentId = agentId;
            
            _navAgent = GetComponent<NavMeshAgent>();
            if (!_navAgent)
            {
                Debug.LogError($"[NPCAgent] Agent {agentId} missing NavMeshAgent component!");
                return;
            }
            
            AgentRenderer = GetComponent<Renderer>();
            if (AgentRenderer)
            {
                AgentMaterial = AgentRenderer.material;
            }
            
            _baseScale = transform.localScale;
            
            // Stagger frame updates to distribute load across frames
            _frameOffset = _globalFrameOffset++ % 10;
            
            _initialized = true;
            manager.RegisterAgent(this);
            
            if (logStatusChanges)
            {
                Debug.Log($"[NPCAgent] Agent {agentId} initialized at {transform.position}");
            }
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
            if (!_initialized || !_navAgent) return;
            
            // Handle off-mesh link traversal at normal speed
            HandleOffMeshLinkTraversal();
            
            UpdateStatus();
            CheckDestinationReached();
        }
        
        /// <summary>
        /// Handles manual off-mesh link traversal at normal movement speed.
        /// Called when NavMeshLink.autoUpdatePosition is false.
        /// </summary>
        private void HandleOffMeshLinkTraversal()
        {
            if (!_navAgent.isOnOffMeshLink)
            {
                _isTraversingLink = false;
                return;
            }
            
            // Start traversal
            if (!_isTraversingLink)
            {
                _isTraversingLink = true;
                _linkTraversalProgress = 0f;
                
                var linkData = _navAgent.currentOffMeshLinkData;
                _linkStartPos = linkData.startPos;
                _linkEndPos = linkData.endPos;
            }
            
            // Move along link at normal speed
            float linkDistance = Vector3.Distance(_linkStartPos, _linkEndPos);
            if (linkDistance > 0.01f)
            {
                float speed = _navAgent.speed;
                _linkTraversalProgress += (speed * Time.deltaTime) / linkDistance;
                
                if (_linkTraversalProgress >= 1f)
                {
                    // Completed traversal
                    _linkTraversalProgress = 1f;
                    transform.position = _linkEndPos;
                    _navAgent.CompleteOffMeshLink();
                    _isTraversingLink = false;
                }
                else
                {
                    // Interpolate position
                    transform.position = Vector3.Lerp(_linkStartPos, _linkEndPos, _linkTraversalProgress);
                }
            }
            else
            {
                // Link too short, complete immediately
                _navAgent.CompleteOffMeshLink();
                _isTraversingLink = false;
            }
        }
        
        /// <summary>
        /// Reduced update - called periodically for Medium/Low LOD agents.
        /// </summary>
        internal void UpdateReduced()
        {
            if (!_initialized || !_navAgent) return;
            
            UpdateStatus();
            CheckDestinationReached();
        }
        
        /// <summary>
        /// Minimal update - called rarely for Culled agents.
        /// </summary>
        internal void UpdateMinimal()
        {
            if (!_initialized || !_navAgent) return;
            
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
            if (_isSelected) return; // Don't change status while selected
            
            AgentStatus newStatus = DetermineStatus();
            
            if (newStatus != _status)
            {
                AgentStatus oldStatus = _status;
                _status = newStatus;
                UpdateVisuals();
                StatusChanged?.Invoke(this, _status);
                
                if (logStatusChanges)
                {
                    Debug.Log($"[NPCAgent] Agent {AgentId} status: {oldStatus} -> {newStatus}");
                }
            }
        }
        
        private AgentStatus DetermineStatus()
        {
            if (!_navAgent) return AgentStatus.Error;
            
            // Check for path errors
            if (_navAgent.pathStatus == NavMeshPathStatus.PathInvalid)
            {
                return AgentStatus.Error;
            }
            
            // If we have an active destination
            if (_hasDestination)
            {
                // Path is being calculated
                if (_navAgent.pathPending)
                {
                    return AgentStatus.Waiting;
                }
                
                // Check if we've arrived
                if (!_navAgent.pathPending && _navAgent.remainingDistance <= _navAgent.stoppingDistance)
                {
                    _hasDestination = false;
                    return AgentStatus.Idle;
                }
                
                // Check if we're actually moving
                if (_navAgent.velocity.sqrMagnitude > 0.01f)
                {
                    return AgentStatus.Moving;
                }
                
                // We have a destination but aren't moving (might be stuck or waiting)
                return AgentStatus.Waiting;
            }
            
            return AgentStatus.Idle;
        }
        
        private void CheckDestinationReached()
        {
            if (!_hasDestination && !_wasAtDestination)
            {
                // Just arrived at destination
                DestinationReached?.Invoke(this);
                
                if (logStatusChanges)
                {
                    Debug.Log($"[NPCAgent] Agent {AgentId} reached destination");
                }
            }
            
            _wasAtDestination = !_hasDestination;
        }
        
        #endregion
        
        #region LOD & Culling
        
        /// <summary>
        /// Set LOD level. Called by NPCManager.
        /// </summary>
        internal void SetLODLevel(AgentLODLevel level)
        {
            if (_lodLevel == level) return;
            
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
        /// NavMeshAgent continues to work for movement.
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
            
            // Update color based on status
            if (AgentMaterial)
            {
                Color color = Manager.GetColorForStatus(_status);
                AgentMaterial.color = color;
            }
            
            // Update scale for selection feedback
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
        
        #region Public API - Movement
        
        /// <summary>
        /// Sets the agent's movement destination.
        /// Returns true if path was successfully requested.
        /// </summary>
        public bool SetDestination(Vector3 destination)
        {
            if (!_navAgent)
            {
                Debug.LogWarning($"[NPCAgent] Agent {AgentId} has no NavMeshAgent.");
                return false;
            }
            
            if (!_navAgent.isOnNavMesh)
            {
                Debug.LogWarning($"[NPCAgent] Agent {AgentId} is not on NavMesh. Position: {transform.position}");
                SetStatus(AgentStatus.Error);
                return false;
            }
            
            // Find valid NavMesh position near destination (small radius for precision)
            if (!NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[NPCAgent] Agent {AgentId} could not find NavMesh position near {destination}");
                SetStatus(AgentStatus.Error);
                return false;
            }
            
            // Request path to destination
            bool result = _navAgent.SetDestination(hit.position);
            
            if (result)
            {
                _hasDestination = true;
                _wasAtDestination = false;
                
                if (logStatusChanges)
                {
                    Debug.Log($"[NPCAgent] Agent {AgentId} moving to {hit.position}");
                }
            }
            else
            {
                Debug.LogWarning($"[NPCAgent] Agent {AgentId} failed to set destination to {hit.position}");
                SetStatus(AgentStatus.Error);
            }
            
            return result;
        }
        
        /// <summary>
        /// Stops current movement immediately.
        /// </summary>
        public void Stop()
        {
            if (!_navAgent) return;
            
            _navAgent.ResetPath();
            _hasDestination = false;
            
            if (logStatusChanges)
            {
                Debug.Log($"[NPCAgent] Agent {AgentId} stopped");
            }
        }
        
        /// <summary>
        /// Teleport agent to position (snapped to NavMesh).
        /// </summary>
        public bool Teleport(Vector3 worldPosition)
        {
            if (!_navAgent) return false;
            
            if (!NavMesh.SamplePosition(worldPosition, out NavMeshHit hit, 5f, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[NPCAgent] Agent {AgentId} teleport failed - no NavMesh near {worldPosition}");
                return false;
            }
            
            _navAgent.Warp(hit.position);
            _hasDestination = false;
            
            if (logStatusChanges)
            {
                Debug.Log($"[NPCAgent] Agent {AgentId} teleported to {hit.position}");
            }
            
            return true;
        }
        
        #endregion
        
        #region Public API - Selection
        
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
            
            if (logStatusChanges)
            {
                Debug.Log($"[NPCAgent] Agent {AgentId} selected");
            }
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
            
            if (logStatusChanges)
            {
                Debug.Log($"[NPCAgent] Agent {AgentId} deselected");
            }
        }
        
        /// <summary>
        /// Toggle selection state.
        /// </summary>
        public void ToggleSelection()
        {
            if (_isSelected) Deselect();
            else Select();
        }
        
        #endregion
        
        #region Public API - Status
        
        /// <summary>
        /// Manually set status (use sparingly).
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
        
        #endregion
        
        #region Debug
        
        private void OnDrawGizmosSelected()
        {
            if (!showDebugInfo) return;
            
            if (!_navAgent) _navAgent = GetComponent<NavMeshAgent>();
            if (!_navAgent) return;
            
            // Draw current position marker
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(transform.position, 0.2f);
            
            // Draw path
            if (_navAgent.hasPath && _navAgent.path.corners.Length > 0)
            {
                Gizmos.color = Color.yellow;
                var corners = _navAgent.path.corners;
                for (int i = 0; i < corners.Length - 1; i++)
                {
                    Gizmos.DrawLine(corners[i], corners[i + 1]);
                    Gizmos.DrawWireSphere(corners[i], 0.1f);
                }
            }
            
            // Draw destination
            if (_hasDestination && _navAgent.hasPath)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(_navAgent.destination, 0.3f);
                Gizmos.DrawLine(transform.position, _navAgent.destination);
            }
            
            // Draw velocity direction
            if (_navAgent.velocity.sqrMagnitude > 0.01f)
            {
                Gizmos.color = Color.blue;
                Gizmos.DrawRay(transform.position, _navAgent.velocity);
            }
        }
        
        #endregion
    }
}
