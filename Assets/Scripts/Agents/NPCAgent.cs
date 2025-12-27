using System;
using UnityEngine;
using Pathfinding;

namespace Agents
{
    /// <summary>
    /// Agent status for visual feedback.
    /// Maps from NavigationState with additional context.
    /// </summary>
    public enum AgentStatus : byte
    {
        /// <summary>Agent is idle with no destination. Color: Green</summary>
        Idle = 0,
        /// <summary>Agent is actively moving to destination. Color: Blue</summary>
        Moving = 1,
        /// <summary>Agent is calculating path or waiting. Color: Orange</summary>
        Calculating = 2,
        /// <summary>Agent encountered an error (path failed, etc). Color: Red</summary>
        Error = 3
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
    /// Uses AgentNavigator for pathfinding and movement.
    /// Handles identity, status, LOD, visuals, and selection.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AgentNavigator))]
    public class NPCAgent : MonoBehaviour
    {
        #region Events
        
        /// <summary>Fired when status changes. Args: (agent, newStatus)</summary>
        public event Action<NPCAgent, AgentStatus> StatusChanged;
        
        /// <summary>Fired when current destination is reached.</summary>
        public event Action<NPCAgent> DestinationReached;
        
        /// <summary>Fired when all queued destinations are completed.</summary>
        public event Action<NPCAgent> AllDestinationsCompleted;
        
        /// <summary>Fired when a new destination starts being processed.</summary>
        public event Action<NPCAgent, Vector3> DestinationStarted;
        
        /// <summary>Fired when destination queue changes. Args: (agent, newQueueCount)</summary>
        public event Action<NPCAgent, int> QueueChanged;
        
        /// <summary>Fired when selection state changes. Args: (agent, isSelected)</summary>
        public event Action<NPCAgent, bool> SelectionChanged;
        
        /// <summary>Fired when LOD level changes. Args: (agent, newLevel)</summary>
        public event Action<NPCAgent, AgentLODLevel> LODChanged;
        
        /// <summary>Fired when movement is stopped (manually or due to error).</summary>
        public event Action<NPCAgent> MovementStopped;
        
        /// <summary>Fired when a destination is unreachable. Args: (agent, targetPosition)</summary>
        public event Action<NPCAgent, Vector3> DestinationUnreachable;
        
        #endregion
        
        #region Public Properties
        
        /// <summary>Unique ID for this agent.</summary>
        public int AgentId { get; private set; }
        
        /// <summary>Current status.</summary>
        public AgentStatus Status => _status;
        
        /// <summary>Whether this agent is selected.</summary>
        public bool IsSelected => _isSelected;
        
        /// <summary>Current LOD level.</summary>
        public AgentLODLevel LODLevel => _lodLevel;
        
        /// <summary>Whether visuals are currently enabled.</summary>
        public bool VisualsEnabled => _visualsEnabled;
        
        /// <summary>Distance to main camera (updated by manager).</summary>
        public float CameraDistance { get; internal set; }
        
        // Navigation delegated properties
        /// <summary>Whether this agent has a current destination.</summary>
        public bool HasDestination => _navigator && _navigator.HasDestination;
        
        /// <summary>Whether this agent has queued destinations.</summary>
        public bool HasQueuedDestinations => _navigator && _navigator.HasQueuedDestinations;
        
        /// <summary>Number of destinations in queue.</summary>
        public int QueuedDestinationCount => _navigator ? _navigator.QueuedDestinationCount : 0;
        
        /// <summary>Whether this agent is actively moving.</summary>
        public bool IsMoving => _navigator && _navigator.IsMoving;
        
        /// <summary>Current destination (if any).</summary>
        public Vector3? CurrentDestination => _navigator?.CurrentDestination;
        
        /// <summary>Remaining distance to destination.</summary>
        public float RemainingDistance => _navigator ? _navigator.RemainingDistance : 0f;
        
        /// <summary>Whether the agent has reached its destination.</summary>
        public bool ReachedDestination => _navigator && _navigator.ReachedDestination;
        
        /// <summary>Reference to Navigator component.</summary>
        public AgentNavigator Navigator => _navigator;
        
        /// <summary>Reference to AIPath component.</summary>
        public AIPath AIPath => _navigator?.AIPath;
        
        /// <summary>Reference to Seeker component.</summary>
        public Seeker Seeker => _navigator?.Seeker;
        
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
        
        private AgentNavigator _navigator;
        
        private AgentStatus _status = AgentStatus.Idle;
        private AgentLODLevel _lodLevel = AgentLODLevel.High;
        private bool _isSelected;
        private bool _visualsEnabled = true;
        private Vector3 _baseScale;
        private bool _initialized;
        
        // Frame skip tracking for staggered updates
        private int _frameOffset;
        private static int _globalFrameOffset;
        
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
            
            // Get and initialize navigator
            _navigator = GetComponent<AgentNavigator>();
            if (!_navigator)
            {
                Debug.LogError($"[NPCAgent] Agent {agentId} missing AgentNavigator component!");
                return;
            }
            _navigator.Initialize();
            
            // Subscribe to navigator events
            SubscribeToNavigatorEvents();
            
            // Get renderer
            AgentRenderer = GetComponent<Renderer>();
            if (!AgentRenderer)
            {
                AgentRenderer = GetComponentInChildren<Renderer>();
            }
            
            if (AgentRenderer)
            {
                AgentMaterial = AgentRenderer.material;
            }
            
            _baseScale = transform.localScale;
            _frameOffset = _globalFrameOffset++ % 10;
            
            _initialized = true;
            manager.RegisterAgent(this);
            
            if (logStatusChanges)
            {
                Debug.Log($"[NPCAgent] Agent {agentId} initialized at {transform.position}");
            }
        }
        
        private void SubscribeToNavigatorEvents()
        {
            _navigator.StateChanged += OnNavigatorStateChanged;
            _navigator.DestinationReached += OnNavigatorDestinationReached;
            _navigator.AllDestinationsCompleted += OnNavigatorAllDestinationsCompleted;
            _navigator.DestinationStarted += OnNavigatorDestinationStarted;
            _navigator.QueueChanged += OnNavigatorQueueChanged;
            _navigator.Stopped += OnNavigatorStopped;
            _navigator.DestinationUnreachable += OnNavigatorDestinationUnreachable;
        }
        
        private void UnsubscribeFromNavigatorEvents()
        {
            if (!_navigator) return;
            
            _navigator.StateChanged -= OnNavigatorStateChanged;
            _navigator.DestinationReached -= OnNavigatorDestinationReached;
            _navigator.AllDestinationsCompleted -= OnNavigatorAllDestinationsCompleted;
            _navigator.DestinationStarted -= OnNavigatorDestinationStarted;
            _navigator.QueueChanged -= OnNavigatorQueueChanged;
            _navigator.Stopped -= OnNavigatorStopped;
            _navigator.DestinationUnreachable -= OnNavigatorDestinationUnreachable;
        }
        
        #endregion
        
        #region Navigator Event Handlers
        
        private void OnNavigatorStateChanged(NavigationState navState)
        {
            // Map NavigationState to AgentStatus
            AgentStatus newStatus = navState switch
            {
                NavigationState.Idle => AgentStatus.Idle,
                NavigationState.Moving => AgentStatus.Moving,
                NavigationState.Calculating => AgentStatus.Calculating,
                NavigationState.Error => AgentStatus.Error,
                _ => AgentStatus.Idle
            };
            
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
        
        private void OnNavigatorDestinationReached()
        {
            DestinationReached?.Invoke(this);
        }
        
        private void OnNavigatorAllDestinationsCompleted()
        {
            AllDestinationsCompleted?.Invoke(this);
        }
        
        private void OnNavigatorDestinationStarted(Vector3 destination)
        {
            DestinationStarted?.Invoke(this, destination);
        }
        
        private void OnNavigatorQueueChanged(int queueCount)
        {
            QueueChanged?.Invoke(this, queueCount);
        }
        
        private void OnNavigatorStopped()
        {
            MovementStopped?.Invoke(this);
        }
        
        private void OnNavigatorDestinationUnreachable(Vector3 target)
        {
            DestinationUnreachable?.Invoke(this, target);
        }
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _navigator = GetComponent<AgentNavigator>();
        }
        
        private void OnDestroy()
        {
            UnsubscribeFromNavigatorEvents();
            
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
            if (!_initialized) return;
            _navigator?.UpdateNavigation();
        }
        
        /// <summary>
        /// Reduced update - called periodically for Medium/Low LOD agents.
        /// </summary>
        internal void UpdateReduced()
        {
            if (!_initialized) return;
            _navigator?.UpdateNavigation();
        }
        
        /// <summary>
        /// Minimal update - called rarely for Culled agents.
        /// </summary>
        internal void UpdateMinimal()
        {
            if (!_initialized) return;
            _navigator?.UpdateNavigation();
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
        
        #endregion
        
        #region LOD & Culling
        
        /// <summary>
        /// Set LOD level. Called by NPCManager.
        /// </summary>
        internal void SetLODLevel(AgentLODLevel level)
        {
            if (_lodLevel == level) return;
            
            _lodLevel = level;
            
            // Adjust navigator repath rate based on LOD
            if (_navigator)
            {
                float repathRate = level switch
                {
                    AgentLODLevel.High => 0.5f,
                    AgentLODLevel.Medium => 1f,
                    AgentLODLevel.Low => 2f,
                    AgentLODLevel.Culled => 5f,
                    _ => 0.5f
                };
                _navigator.SetRepathRate(repathRate);
            }
            
            bool shouldShowVisuals = level != AgentLODLevel.Culled;
            if (shouldShowVisuals != _visualsEnabled)
            {
                SetVisualsEnabled(shouldShowVisuals);
            }
            
            LODChanged?.Invoke(this, _lodLevel);
        }
        
        /// <summary>
        /// Enable/disable visual components.
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
        /// Update visual appearance based on status and selection.
        /// </summary>
        internal void UpdateVisuals()
        {
            if (!_visualsEnabled || !Manager) return;
            
            if (AgentMaterial)
            {
                Color color = Manager.GetColorForAgent(this);
                AgentMaterial.color = color;
            }
            
            transform.localScale = _isSelected 
                ? _baseScale * Manager.SelectedScaleMultiplier 
                : _baseScale;
        }
        
        /// <summary>
        /// Force visual update.
        /// </summary>
        internal void ForceVisualUpdate()
        {
            if (!_visualsEnabled) return;
            UpdateVisuals();
        }
        
        #endregion
        
        #region Public API - Movement (Delegated to Navigator)
        
        /// <summary>
        /// Sets the agent's movement destination.
        /// </summary>
        public bool SetDestination(Vector3 destination, bool immediate = false)
        {
            return _navigator && _navigator.SetDestination(destination, immediate);
        }
        
        /// <summary>
        /// Stops current movement immediately and clears the queue.
        /// </summary>
        public void Stop()
        {
            _navigator?.Stop();
        }
        
        /// <summary>
        /// Clears the destination queue without stopping current movement.
        /// </summary>
        public void ClearQueue()
        {
            _navigator?.ClearQueue();
        }
        
        /// <summary>
        /// Gets a copy of the destination queue.
        /// </summary>
        public Vector3[] GetQueuedDestinations()
        {
            return _navigator?.GetQueuedDestinations() ?? Array.Empty<Vector3>();
        }
        
        /// <summary>
        /// Peeks at the next queued destination.
        /// </summary>
        public Vector3? PeekNextDestination()
        {
            return _navigator?.PeekNextDestination();
        }
        
        /// <summary>
        /// Teleport agent to position.
        /// </summary>
        public void Teleport(Vector3 worldPosition)
        {
            _navigator?.Teleport(worldPosition);
            
            if (logStatusChanges)
            {
                Debug.Log($"[NPCAgent] Agent {AgentId} teleported to {worldPosition}");
            }
        }
        
        /// <summary>
        /// Clear the failed destinations list.
        /// </summary>
        public void ClearFailedDestinations()
        {
            _navigator?.ClearFailedDestinations();
        }
        
        #endregion
        
        #region Public API - Selection
        
        /// <summary>
        /// Select this agent.
        /// </summary>
        public void Select()
        {
            if (_isSelected) return;
            
            _isSelected = true;
            UpdateVisuals();
            SelectionChanged?.Invoke(this, true);
            
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
            UpdateVisuals();
            SelectionChanged?.Invoke(this, false);
            
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
            if (_status == newStatus) return;
            
            _status = newStatus;
            UpdateVisuals();
            StatusChanged?.Invoke(this, _status);
        }
        
        #endregion
        
        #region Public API - Configuration
        
        /// <summary>
        /// Configure movement settings.
        /// </summary>
        public void ConfigureMovement(float speed, float rotationSpeed, float acceleration)
        {
            _navigator?.ConfigureMovement(speed, rotationSpeed, acceleration);
        }
        
        /// <summary>
        /// Sets the maximum queue size.
        /// </summary>
        public void SetMaxQueueSize(int size)
        {
            _navigator?.SetMaxQueueSize(size);
        }
        
        #endregion
        
        #region Debug
        
        private void OnDrawGizmosSelected()
        {
            if (!showDebugInfo) return;
            
            // Draw current position marker
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(transform.position, 0.2f);
        }
        
        #endregion
    }
}
