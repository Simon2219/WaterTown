using System;
using System.Collections.Generic;
using UnityEngine;
using Pathfinding;

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
    /// Individual NPC agent component using A* Pathfinding Project.
    /// Requires Seeker and AIPath components for pathfinding.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Seeker))]
    [RequireComponent(typeof(AIPath))]
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
        
        #endregion
        
        #region Public Properties
        
        /// <summary>Unique ID for this agent.</summary>
        public int AgentId { get; private set; }
        
        /// <summary>Current status.</summary>
        public AgentStatus Status => _status;
        
        /// <summary>Whether this agent is selected.</summary>
        public bool IsSelected => _isSelected;
        
        /// <summary>Whether this agent has a current destination.</summary>
        public bool HasDestination => _hasDestination;
        
        /// <summary>Whether this agent has queued destinations.</summary>
        public bool HasQueuedDestinations => _destinationQueue.Count > 0;
        
        /// <summary>Number of destinations in queue (not including current).</summary>
        public int QueuedDestinationCount => _destinationQueue.Count;
        
        /// <summary>Whether this agent is actively moving.</summary>
        public bool IsMoving => _aiPath && !_aiPath.reachedDestination && _aiPath.hasPath;
        
        /// <summary>Current destination (if any).</summary>
        public Vector3? CurrentDestination => _hasDestination ? (Vector3?)_aiPath.destination : null;
        
        /// <summary>Reference to AIPath component.</summary>
        public AIPath AIPath => _aiPath;
        
        /// <summary>Reference to Seeker component.</summary>
        public Seeker Seeker => _seeker;
        
        /// <summary>Current LOD level.</summary>
        public AgentLODLevel LODLevel => _lodLevel;
        
        /// <summary>Whether visuals are currently enabled.</summary>
        public bool VisualsEnabled => _visualsEnabled;
        
        /// <summary>Distance to main camera (updated by manager).</summary>
        public float CameraDistance { get; internal set; }
        
        /// <summary>Remaining distance to destination.</summary>
        public float RemainingDistance => _aiPath ? _aiPath.remainingDistance : 0f;
        
        /// <summary>Whether the agent has reached its destination.</summary>
        public bool ReachedDestination => _aiPath && _aiPath.reachedDestination;
        
        #endregion
        
        #region Serialized Fields
        
        [Header("Destination Queue")]
        [Tooltip("Maximum number of destinations that can be queued. 0 = unlimited.")]
        [SerializeField] private int maxQueueSize = 10;
        
        [Tooltip("When queue is full, should new destinations replace the last one?")]
        [SerializeField] private bool replaceLastWhenFull = true;
        
        [Header("Debug")]
        [SerializeField] private bool showDebugInfo;
        [SerializeField] private bool logStatusChanges;
        [SerializeField] private bool logQueueChanges;
        
        #endregion
        
        #region Internal State
        
        internal NPCManager Manager;
        internal Renderer AgentRenderer;
        internal Material AgentMaterial;
        
        // A* Pathfinding components
        private AIPath _aiPath;
        private Seeker _seeker;
        
        // State
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
        
        // Destination queue
        private readonly Queue<Vector3> _destinationQueue = new();
        
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
            
            // Get A* components
            _aiPath = GetComponent<AIPath>();
            _seeker = GetComponent<Seeker>();
            
            if (!_aiPath || !_seeker)
            {
                Debug.LogError($"[NPCAgent] Agent {agentId} missing AIPath or Seeker component!");
                return;
            }
            
            // Configure AIPath
            _aiPath.canMove = true;
            _aiPath.canSearch = true;
            _aiPath.enableRotation = true;
            
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
            _aiPath = GetComponent<AIPath>();
            _seeker = GetComponent<Seeker>();
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
            if (!_initialized) return;
            
            UpdateStatus();
            CheckDestinationReached();
        }
        
        /// <summary>
        /// Reduced update - called periodically for Medium/Low LOD agents.
        /// </summary>
        internal void UpdateReduced()
        {
            if (!_initialized) return;
            
            UpdateStatus();
            CheckDestinationReached();
        }
        
        /// <summary>
        /// Minimal update - called rarely for Culled agents.
        /// </summary>
        internal void UpdateMinimal()
        {
            if (!_initialized) return;
            
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
            if (!_aiPath) return AgentStatus.Error;
            
            // Check for path errors
            if (_seeker.GetCurrentPath() != null && _seeker.GetCurrentPath().error)
            {
                return AgentStatus.Error;
            }
            
            if (_hasDestination)
            {
                if (_aiPath.pathPending)
                {
                    return AgentStatus.Waiting;
                }
                
                if (_aiPath.reachedDestination)
                {
                    return AgentStatus.Idle;
                }
                
                if (_aiPath.hasPath && _aiPath.velocity.sqrMagnitude > 0.01f)
                {
                    return AgentStatus.Moving;
                }
                
                return AgentStatus.Waiting;
            }
            
            return AgentStatus.Idle;
        }
        
        private void CheckDestinationReached()
        {
            if (!_hasDestination || !_aiPath) return;
            
            // Check if destination reached
            if (_aiPath.reachedDestination && !_wasAtDestination)
            {
                _wasAtDestination = true;
                _hasDestination = false;
                
                if (logStatusChanges)
                {
                    Debug.Log($"[NPCAgent] Agent {AgentId} reached destination");
                }
                
                DestinationReached?.Invoke(this);
                
                // Process next queued destination
                if (_destinationQueue.Count > 0)
                {
                    Vector3 nextDest = _destinationQueue.Dequeue();
                    
                    if (logQueueChanges)
                    {
                        Debug.Log($"[NPCAgent] Agent {AgentId} processing next queued destination. Remaining: {_destinationQueue.Count}");
                    }
                    
                    QueueChanged?.Invoke(this, _destinationQueue.Count);
                    SetDestinationInternal(nextDest);
                }
                else
                {
                    AllDestinationsCompleted?.Invoke(this);
                }
            }
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
            
            // Adjust AIPath update frequency based on LOD
            if (_aiPath)
            {
                _aiPath.repathRate = level switch
                {
                    AgentLODLevel.High => 0.5f,
                    AgentLODLevel.Medium => 1f,
                    AgentLODLevel.Low => 2f,
                    AgentLODLevel.Culled => 5f,
                    _ => 0.5f
                };
            }
            
            bool shouldShowVisuals = level != AgentLODLevel.Culled;
            if (shouldShowVisuals != _visualsEnabled)
            {
                SetVisualsEnabled(shouldShowVisuals);
            }
            
            LODChanged?.Invoke(this, _lodLevel);
        }
        
        /// <summary>
        /// Enable/disable visual components (renderer).
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
            
            if (AgentMaterial)
            {
                Color color = Manager.GetColorForStatus(_status);
                AgentMaterial.color = color;
            }
            
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
        
        #region Public API - Movement & Queue
        
        /// <summary>
        /// Sets the agent's movement destination.
        /// </summary>
        /// <param name="destination">Target position.</param>
        /// <param name="immediate">If true, cancels current movement and clears queue. If false, queues the destination.</param>
        /// <returns>True if destination was accepted (set or queued).</returns>
        public bool SetDestination(Vector3 destination, bool immediate = false)
        {
            if (!_aiPath)
            {
                Debug.LogWarning($"[NPCAgent] Agent {AgentId} has no AIPath.");
                return false;
            }
            
            if (immediate)
            {
                return HandleImmediateDestination(destination);
            }
            else
            {
                return HandleQueuedDestination(destination);
            }
        }
        
        private bool HandleImmediateDestination(Vector3 destination)
        {
            // Clear the queue
            if (_destinationQueue.Count > 0)
            {
                _destinationQueue.Clear();
                
                if (logQueueChanges)
                {
                    Debug.Log($"[NPCAgent] Agent {AgentId} queue cleared for immediate destination");
                }
                
                QueueChanged?.Invoke(this, 0);
            }
            
            return SetDestinationInternal(destination);
        }
        
        private bool HandleQueuedDestination(Vector3 destination)
        {
            // If no current destination, set it directly
            if (!_hasDestination)
            {
                return SetDestinationInternal(destination);
            }
            
            // Check queue capacity
            if (maxQueueSize > 0 && _destinationQueue.Count >= maxQueueSize)
            {
                if (replaceLastWhenFull)
                {
                    // Remove last and add new
                    var temp = new Vector3[_destinationQueue.Count - 1];
                    for (int i = 0; i < temp.Length; i++)
                    {
                        temp[i] = _destinationQueue.Dequeue();
                    }
                    _destinationQueue.Clear();
                    foreach (var d in temp)
                    {
                        _destinationQueue.Enqueue(d);
                    }
                    
                    if (logQueueChanges)
                    {
                        Debug.Log($"[NPCAgent] Agent {AgentId} queue full, replacing last destination");
                    }
                }
                else
                {
                    Debug.LogWarning($"[NPCAgent] Agent {AgentId} destination queue is full ({maxQueueSize})");
                    return false;
                }
            }
            
            _destinationQueue.Enqueue(destination);
            
            if (logQueueChanges)
            {
                Debug.Log($"[NPCAgent] Agent {AgentId} queued destination. Queue size: {_destinationQueue.Count}");
            }
            
            QueueChanged?.Invoke(this, _destinationQueue.Count);
            return true;
        }
        
        private bool SetDestinationInternal(Vector3 destination)
        {
            if (!_aiPath) return false;
            
            _aiPath.destination = destination;
            _aiPath.SearchPath();
            
            _hasDestination = true;
            _wasAtDestination = false;
            
            if (logStatusChanges)
            {
                Debug.Log($"[NPCAgent] Agent {AgentId} moving to {destination}");
            }
            
            DestinationStarted?.Invoke(this, destination);
            return true;
        }
        
        /// <summary>
        /// Stops current movement immediately and clears the queue.
        /// </summary>
        public void Stop()
        {
            // Clear queue
            if (_destinationQueue.Count > 0)
            {
                _destinationQueue.Clear();
                QueueChanged?.Invoke(this, 0);
            }
            
            // Stop AIPath
            if (_aiPath)
            {
                _aiPath.SetPath(null);
                _aiPath.destination = transform.position;
            }
            
            _hasDestination = false;
            
            if (logStatusChanges)
            {
                Debug.Log($"[NPCAgent] Agent {AgentId} stopped");
            }
            
            MovementStopped?.Invoke(this);
        }
        
        /// <summary>
        /// Clears the destination queue without stopping current movement.
        /// </summary>
        public void ClearQueue()
        {
            if (_destinationQueue.Count > 0)
            {
                _destinationQueue.Clear();
                
                if (logQueueChanges)
                {
                    Debug.Log($"[NPCAgent] Agent {AgentId} queue cleared");
                }
                
                QueueChanged?.Invoke(this, 0);
            }
        }
        
        /// <summary>
        /// Gets a copy of the destination queue.
        /// </summary>
        public Vector3[] GetQueuedDestinations()
        {
            return _destinationQueue.ToArray();
        }
        
        /// <summary>
        /// Peeks at the next queued destination without removing it.
        /// </summary>
        public Vector3? PeekNextDestination()
        {
            return _destinationQueue.Count > 0 ? _destinationQueue.Peek() : null;
        }
        
        /// <summary>
        /// Teleport agent to position. Clears queue.
        /// </summary>
        public bool Teleport(Vector3 worldPosition)
        {
            // Stop everything
            Stop();
            
            // Teleport using AIPath
            if (_aiPath)
            {
                _aiPath.Teleport(worldPosition);
            }
            else
            {
                transform.position = worldPosition;
            }
            
            if (logStatusChanges)
            {
                Debug.Log($"[NPCAgent] Agent {AgentId} teleported to {worldPosition}");
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
        
        #region Public API - Configuration
        
        /// <summary>
        /// Configure AIPath movement settings.
        /// </summary>
        public void ConfigureMovement(float speed, float rotationSpeed, float acceleration)
        {
            if (!_aiPath) return;
            
            _aiPath.maxSpeed = speed;
            _aiPath.rotationSpeed = rotationSpeed;
            _aiPath.maxAcceleration = acceleration;
        }
        
        /// <summary>
        /// Sets the maximum queue size. 0 = unlimited.
        /// </summary>
        public void SetMaxQueueSize(int size)
        {
            maxQueueSize = Mathf.Max(0, size);
        }
        
        #endregion
        
        #region Debug
        
        private void OnDrawGizmosSelected()
        {
            if (!showDebugInfo) return;
            
            // Draw current position marker
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(transform.position, 0.2f);
            
            // Draw path from Seeker
            if (_seeker)
            {
                var path = _seeker.GetCurrentPath();
                if (path != null && path.vectorPath != null && path.vectorPath.Count > 0)
                {
                    Gizmos.color = Color.yellow;
                    for (int i = 0; i < path.vectorPath.Count - 1; i++)
                    {
                        Gizmos.DrawLine(path.vectorPath[i], path.vectorPath[i + 1]);
                        Gizmos.DrawWireSphere(path.vectorPath[i], 0.1f);
                    }
                }
            }
            
            // Draw current destination
            if (_hasDestination && _aiPath)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(_aiPath.destination, 0.3f);
                Gizmos.DrawLine(transform.position, _aiPath.destination);
            }
            
            // Draw queued destinations
            if (_destinationQueue.Count > 0)
            {
                Gizmos.color = Color.blue;
                Vector3 prev = _hasDestination && _aiPath ? _aiPath.destination : transform.position;
                foreach (var dest in _destinationQueue)
                {
                    Gizmos.DrawWireSphere(dest, 0.2f);
                    Gizmos.DrawLine(prev, dest);
                    prev = dest;
                }
            }
        }
        
        #endregion
    }
}
