using System;
using System.Collections.Generic;
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
/// How the agent handles height during link traversal.
/// </summary>
public enum LinkHeightMode
{
    /// <summary>Use the actual link endpoint heights.</summary>
    FollowLinkEndpoints,
    /// <summary>Keep the height from before entering the link.</summary>
    KeepStartHeight,
    /// <summary>Smoothly interpolate between start and end heights.</summary>
    InterpolateHeight,
    /// <summary>Always use current transform height (ignore link heights).</summary>
    UseCurrentHeight
}

/// <summary>
/// How the agent handles being interrupted during link traversal.
/// </summary>
public enum LinkInterruptBehavior
{
    /// <summary>Complete the link traversal before processing new destination.</summary>
    FinishLinkFirst,
    /// <summary>Immediately cancel link traversal and redirect.</summary>
    CancelImmediately,
    /// <summary>Wait until reaching a safe point (midpoint or end) before redirecting.</summary>
    WaitForSafePoint
}

/// <summary>
/// How the agent rotates during link traversal.
/// </summary>
public enum LinkRotationMode
{
    /// <summary>No rotation during traversal.</summary>
    None,
    /// <summary>Smoothly rotate toward movement direction.</summary>
    SmoothLookAhead,
    /// <summary>Instantly face movement direction.</summary>
    InstantLookAhead,
    /// <summary>Keep the rotation from before entering the link.</summary>
    KeepStartRotation
}

/// <summary>
/// Individual NPC agent component.
/// Handles NavMeshAgent control, destination queue, link traversal, and visual state.
/// Performance is managed by NPCManager through LOD and culling.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
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
    
    /// <summary>Fired when link traversal starts.</summary>
    public event Action<NPCAgent> LinkTraversalStarted;
    
    /// <summary>Fired when link traversal completes.</summary>
    public event Action<NPCAgent> LinkTraversalCompleted;
    
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
    public bool IsMoving => _navAgent && _navAgent.hasPath && 
                            _navAgent.remainingDistance > _navAgent.stoppingDistance &&
                            (_navAgent.velocity.sqrMagnitude > 0.01f || _isTraversingLink);
    
    /// <summary>Whether this agent is currently traversing a link.</summary>
    public bool IsTraversingLink => _isTraversingLink;
    
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
    
    /// <summary>Current link traversal progress (0-1), or -1 if not traversing.</summary>
    public float LinkProgress => _isTraversingLink ? _linkProgress : -1f;
    
    #endregion
    
    #region Serialized Fields - Link Traversal
    
    [Header("Link Traversal - Mode")]
    [Tooltip("If TRUE: Unity handles link traversal automatically (recommended for flush/same-height platforms).\n" +
             "If FALSE: Manual traversal with custom easing/height/rotation control (for special effects or height differences).")]
    [SerializeField] private bool useAutoLinkTraversal = true;
    
    [Header("Link Traversal - Manual Settings (only used when Auto is OFF)")]
    [Tooltip("Easing function for link traversal movement.")]
    [SerializeField] private EasingType linkMovementEasing = EasingType.SmoothStep;
    
    [Tooltip("How to handle height during link traversal.")]
    [SerializeField] private LinkHeightMode linkHeightMode = LinkHeightMode.InterpolateHeight;
    
    [Tooltip("Additional smoothing applied to position during link traversal (0 = none, 1 = max).")]
    [Range(0f, 1f)]
    [SerializeField] private float linkPositionSmoothing = 0.1f;
    
    [Tooltip("Minimum duration for link traversal regardless of distance (prevents instant traversal).")]
    [SerializeField] private float minLinkDuration = 0.1f;
    
    [Tooltip("Speed multiplier during link traversal (1 = normal speed).")]
    [SerializeField] private float linkSpeedMultiplier = 1f;
    
    [Header("Link Traversal - Rotation (Manual only)")]
    [Tooltip("How to handle rotation during link traversal.")]
    [SerializeField] private LinkRotationMode linkRotationMode = LinkRotationMode.SmoothLookAhead;
    
    [Tooltip("Easing function for rotation during link traversal.")]
    [SerializeField] private EasingType linkRotationEasing = EasingType.EaseOutQuad;
    
    [Tooltip("Rotation speed multiplier during link traversal.")]
    [SerializeField] private float linkRotationSpeed = 5f;
    
    [Header("Link Traversal - Behavior")]
    [Tooltip("How to handle new destinations while traversing a link.")]
    [SerializeField] private LinkInterruptBehavior linkInterruptBehavior = LinkInterruptBehavior.FinishLinkFirst;
    
    [Tooltip("Progress threshold considered 'safe' for WaitForSafePoint behavior (0.5 = midpoint).")]
    [Range(0f, 1f)]
    [SerializeField] private float linkSafePointThreshold = 0.5f;
    
    #endregion
    
    #region Serialized Fields - Destination Queue
    
    [Header("Destination Queue")]
    [Tooltip("Maximum number of destinations that can be queued. 0 = unlimited.")]
    [SerializeField] private int maxQueueSize = 10;
    
    [Tooltip("When queue is full, should new destinations replace the last one?")]
    [SerializeField] private bool replaceLastWhenFull = true;
    
    #endregion
    
    #region Serialized Fields - Debug
    
    [Header("Debug")]
    [SerializeField] private bool showDebugInfo;
    [SerializeField] private bool logStatusChanges;
    [SerializeField] private bool logQueueChanges;
    [SerializeField] private bool logLinkTraversal;
    
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
    
    // Destination queue
    private readonly Queue<Vector3> _destinationQueue = new();
    private Vector3? _pendingImmediateDestination;
    
    // Off-mesh link traversal
    private bool _isTraversingLink;
    private Vector3 _linkStartPos;
    private Vector3 _linkEndPos;
    private float _linkStartHeight;
    private Quaternion _linkStartRotation;
    private float _linkProgress;
    private float _linkDuration;
    private Vector3 _smoothedPosition;
    private bool _linkInterruptPending;
    
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
        
        // Configure link traversal mode based on setting
        _navAgent.autoTraverseOffMeshLink = useAutoLinkTraversal;
        
        AgentRenderer = GetComponent<Renderer>();
        if (AgentRenderer)
        {
            AgentMaterial = AgentRenderer.material;
        }
        
        _baseScale = transform.localScale;
        _smoothedPosition = transform.position;
        
        // Stagger frame updates to distribute load across frames
        _frameOffset = _globalFrameOffset++ % 10;
        
        _initialized = true;
        manager.RegisterAgent(this);
        
        if (logStatusChanges)
        {
            Debug.Log($"[NPCAgent] Agent {agentId} initialized at {transform.position}, autoLinkTraversal={useAutoLinkTraversal}");
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
        
        // Handle off-mesh link traversal
        HandleOffMeshLinkTraversal();
        
        // Process any pending immediate destination
        ProcessPendingDestination();
        
        UpdateStatus();
        CheckDestinationReached();
    }
    
    /// <summary>
    /// Handles manual off-mesh link traversal with full configuration options.
    /// Only active when useAutoLinkTraversal is FALSE.
    /// </summary>
    private void HandleOffMeshLinkTraversal()
    {
        // Skip if using Unity's automatic traversal
        if (useAutoLinkTraversal)
        {
            _isTraversingLink = false;
            return;
        }
        
        // Check if we're no longer on a link (completed or cancelled)
        if (!_navAgent.isOnOffMeshLink)
        {
            if (_isTraversingLink)
            {
                CompleteOrCancelLinkTraversal(true);
            }
            return;
        }
        
        float baseOffset = _navAgent.baseOffset;
        
        // Start traversal
        if (!_isTraversingLink)
        {
            StartLinkTraversal(baseOffset);
        }
        
        // Check for interrupt
        if (_linkInterruptPending && ShouldInterruptLink())
        {
            CompleteOrCancelLinkTraversal(false);
            return;
        }
        
        // Progress the traversal
        _linkProgress += Time.deltaTime / _linkDuration;
        _linkProgress = Mathf.Clamp01(_linkProgress);
        
        // Apply easing to progress
        float easedProgress = EasingFunctions.Evaluate(linkMovementEasing, _linkProgress);
        
        // Calculate position
        Vector3 newPos = CalculateLinkPosition(easedProgress, baseOffset);
        
        // Apply additional smoothing if configured
        if (linkPositionSmoothing > 0f)
        {
            float smoothFactor = 1f - Mathf.Pow(linkPositionSmoothing, Time.deltaTime * 60f);
            _smoothedPosition = Vector3.Lerp(_smoothedPosition, newPos, smoothFactor);
            newPos = _smoothedPosition;
        }
        
        transform.position = newPos;
        
        // Sync NavMeshAgent's internal position
        _navAgent.nextPosition = transform.position;
        
        // Handle rotation
        HandleLinkRotation(easedProgress);
        
        // Check if traversal is complete
        if (_linkProgress >= 1f)
        {
            FinalizeLinkTraversal(baseOffset);
        }
    }
    
    private void StartLinkTraversal(float baseOffset)
    {
        _isTraversingLink = true;
        _linkProgress = 0f;
        _linkInterruptPending = false;
        
        // Disable NavMeshAgent position updates
        _navAgent.updatePosition = false;
        
        var linkData = _navAgent.currentOffMeshLinkData;
        
        // Store link endpoints
        _linkStartPos = linkData.startPos;
        _linkEndPos = linkData.endPos;
        
        // Store initial state for various modes
        _linkStartHeight = transform.position.y;
        _linkStartRotation = transform.rotation;
        _smoothedPosition = transform.position;
        
        // Calculate duration based on distance and agent speed
        float distance = Vector3.Distance(_linkStartPos, _linkEndPos);
        float speed = Mathf.Max(_navAgent.speed * linkSpeedMultiplier, 0.1f);
        _linkDuration = distance / speed;
        _linkDuration = Mathf.Max(_linkDuration, minLinkDuration);
        
        if (logLinkTraversal)
        {
            Debug.Log($"[NPCAgent] Agent {AgentId} starting link traversal: " +
                      $"distance={distance:F2}m, duration={_linkDuration:F2}s, easing={linkMovementEasing}");
        }
        
        LinkTraversalStarted?.Invoke(this);
    }
    
    private Vector3 CalculateLinkPosition(float easedProgress, float baseOffset)
    {
        // Interpolate ground position
        Vector3 groundPos = Vector3.Lerp(_linkStartPos, _linkEndPos, easedProgress);
        
        // Calculate height based on mode
        float height;
        switch (linkHeightMode)
        {
            case LinkHeightMode.KeepStartHeight:
                height = _linkStartHeight;
                break;
                
            case LinkHeightMode.UseCurrentHeight:
                height = transform.position.y;
                break;
                
            case LinkHeightMode.InterpolateHeight:
                float startH = _linkStartPos.y + baseOffset;
                float endH = _linkEndPos.y + baseOffset;
                // Use same easing for height
                height = EasingFunctions.Lerp(startH, endH, _linkProgress, linkMovementEasing);
                break;
                
            case LinkHeightMode.FollowLinkEndpoints:
            default:
                height = groundPos.y + baseOffset;
                break;
        }
        
        return new Vector3(groundPos.x, height, groundPos.z);
    }
    
    private void HandleLinkRotation(float easedProgress)
    {
        switch (linkRotationMode)
        {
            case LinkRotationMode.None:
                break;
                
            case LinkRotationMode.KeepStartRotation:
                transform.rotation = _linkStartRotation;
                break;
                
            case LinkRotationMode.InstantLookAhead:
                Vector3 dir = (_linkEndPos - _linkStartPos).normalized;
                if (dir.sqrMagnitude > 0.001f)
                {
                    dir.y = 0;
                    if (dir.sqrMagnitude > 0.001f)
                    {
                        transform.rotation = Quaternion.LookRotation(dir);
                    }
                }
                break;
                
            case LinkRotationMode.SmoothLookAhead:
                Vector3 moveDir = (_linkEndPos - _linkStartPos).normalized;
                if (moveDir.sqrMagnitude > 0.001f)
                {
                    moveDir.y = 0;
                    if (moveDir.sqrMagnitude > 0.001f)
                    {
                        Quaternion targetRot = Quaternion.LookRotation(moveDir);
                        // Apply rotation easing
                        float rotProgress = EasingFunctions.Evaluate(linkRotationEasing, _linkProgress);
                        transform.rotation = Quaternion.Slerp(_linkStartRotation, targetRot, 
                            rotProgress * linkRotationSpeed * Time.deltaTime * 10f);
                    }
                }
                break;
        }
    }
    
    private bool ShouldInterruptLink()
    {
        return linkInterruptBehavior switch
        {
            LinkInterruptBehavior.CancelImmediately => true,
            LinkInterruptBehavior.WaitForSafePoint => _linkProgress >= linkSafePointThreshold,
            LinkInterruptBehavior.FinishLinkFirst => _linkProgress >= 1f,
            _ => false
        };
    }
    
    private void FinalizeLinkTraversal(float baseOffset)
    {
        // Snap to exact end position
        Vector3 finalPos = _linkEndPos;
        finalPos.y = linkHeightMode == LinkHeightMode.KeepStartHeight 
            ? _linkStartHeight 
            : _linkEndPos.y + baseOffset;
        transform.position = finalPos;
        
        // Sync final position
        _navAgent.nextPosition = transform.position;
        
        // Complete the link traversal
        _navAgent.CompleteOffMeshLink();
        
        CompleteOrCancelLinkTraversal(true);
    }
    
    private void CompleteOrCancelLinkTraversal(bool completed)
    {
        _navAgent.updatePosition = true;
        _isTraversingLink = false;
        _linkInterruptPending = false;
        
        if (logLinkTraversal)
        {
            Debug.Log($"[NPCAgent] Agent {AgentId} {(completed ? "completed" : "cancelled")} link traversal");
        }
        
        if (completed)
        {
            LinkTraversalCompleted?.Invoke(this);
        }
    }
    
    private void ProcessPendingDestination()
    {
        if (_pendingImmediateDestination.HasValue && !_isTraversingLink)
        {
            var dest = _pendingImmediateDestination.Value;
            _pendingImmediateDestination = null;
            SetDestinationInternal(dest);
        }
    }
    
    /// <summary>
    /// Reduced update - called periodically for Medium/Low LOD agents.
    /// </summary>
    internal void UpdateReduced()
    {
        if (!_initialized || !_navAgent) return;
        
        // Still need to handle link traversal even at lower LOD
        HandleOffMeshLinkTraversal();
        ProcessPendingDestination();
        
        UpdateStatus();
        CheckDestinationReached();
    }
    
    /// <summary>
    /// Minimal update - called rarely for Culled agents.
    /// </summary>
    internal void UpdateMinimal()
    {
        if (!_initialized || !_navAgent) return;
        
        // Handle link traversal
        HandleOffMeshLinkTraversal();
        ProcessPendingDestination();
        
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
        if (!_navAgent) return AgentStatus.Error;
        
        if (_navAgent.pathStatus == NavMeshPathStatus.PathInvalid)
        {
            return AgentStatus.Error;
        }
        
        if (_isTraversingLink)
        {
            return AgentStatus.Moving;
        }
        
        if (_hasDestination)
        {
            if (_navAgent.pathPending)
            {
                return AgentStatus.Waiting;
            }
            
            if (!_navAgent.pathPending && _navAgent.remainingDistance <= _navAgent.stoppingDistance)
            {
                return AgentStatus.Idle; // Will trigger CheckDestinationReached
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
        if (!_hasDestination) return;
        
        // Check if NavMesh destination reached
        if (!_navAgent.pathPending && _navAgent.remainingDistance <= _navAgent.stoppingDistance && !_isTraversingLink)
        {
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
        if (!_navAgent)
        {
            Debug.LogWarning($"[NPCAgent] Agent {AgentId} has no NavMeshAgent.");
            return false;
        }
        
        // Validate position on NavMesh
        if (!NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
        {
            Debug.LogWarning($"[NPCAgent] Agent {AgentId} could not find NavMesh position near {destination}");
            return false;
        }
        
        Vector3 validDest = hit.position;
        
        if (immediate)
        {
            return HandleImmediateDestination(validDest);
        }
        else
        {
            return HandleQueuedDestination(validDest);
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
        
        // If traversing a link, handle based on interrupt behavior
        if (_isTraversingLink)
        {
            if (linkInterruptBehavior == LinkInterruptBehavior.CancelImmediately)
            {
                // Cancel immediately and set destination
                CompleteOrCancelLinkTraversal(false);
                return SetDestinationInternal(destination);
            }
            else
            {
                // Queue the destination for after link completes
                _pendingImmediateDestination = destination;
                _linkInterruptPending = true;
                
                if (logQueueChanges)
                {
                    Debug.Log($"[NPCAgent] Agent {AgentId} immediate destination pending (on link)");
                }
                
                return true;
            }
        }
        
        return SetDestinationInternal(destination);
    }
    
    private bool HandleQueuedDestination(Vector3 destination)
    {
        // If no current destination, set it directly
        if (!_hasDestination && !_isTraversingLink)
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
        if (!_navAgent.isOnNavMesh)
        {
            Debug.LogWarning($"[NPCAgent] Agent {AgentId} is not on NavMesh. Position: {transform.position}");
            SetStatus(AgentStatus.Error);
            return false;
        }
        
        bool result = _navAgent.SetDestination(destination);
        
        if (result)
        {
            _hasDestination = true;
            _wasAtDestination = false;
            
            if (logStatusChanges)
            {
                Debug.Log($"[NPCAgent] Agent {AgentId} moving to {destination}");
            }
            
            DestinationStarted?.Invoke(this, destination);
        }
        else
        {
            Debug.LogWarning($"[NPCAgent] Agent {AgentId} failed to set destination to {destination}");
            SetStatus(AgentStatus.Error);
        }
        
        return result;
    }
    
    /// <summary>
    /// Stops current movement immediately and clears the queue.
    /// </summary>
    public void Stop()
    {
        if (!_navAgent) return;
        
        // Clean up link traversal
        if (_isTraversingLink)
        {
            CompleteOrCancelLinkTraversal(false);
        }
        
        // Clear pending
        _pendingImmediateDestination = null;
        
        // Clear queue
        if (_destinationQueue.Count > 0)
        {
            _destinationQueue.Clear();
            QueueChanged?.Invoke(this, 0);
        }
        
        _navAgent.ResetPath();
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
    /// Teleport agent to position (snapped to NavMesh). Clears queue.
    /// </summary>
    public bool Teleport(Vector3 worldPosition)
    {
        if (!_navAgent) return false;
        
        // Stop everything
        Stop();
        
        if (!NavMesh.SamplePosition(worldPosition, out NavMeshHit hit, 5f, NavMesh.AllAreas))
        {
            Debug.LogWarning($"[NPCAgent] Agent {AgentId} teleport failed - no NavMesh near {worldPosition}");
            return false;
        }
        
        _navAgent.Warp(hit.position);
        _smoothedPosition = hit.position;
        
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
    
    #region Public API - Configuration
    
    /// <summary>
    /// Sets whether to use Unity's automatic link traversal or manual traversal.
    /// TRUE = Unity handles it (recommended for flush platforms).
    /// FALSE = Manual traversal with custom easing/height control.
    /// </summary>
    public void SetAutoLinkTraversal(bool useAuto)
    {
        useAutoLinkTraversal = useAuto;
        if (_navAgent)
        {
            _navAgent.autoTraverseOffMeshLink = useAuto;
        }
    }
    
    /// <summary>
    /// Gets whether automatic link traversal is enabled.
    /// </summary>
    public bool UseAutoLinkTraversal => useAutoLinkTraversal;
    
    /// <summary>
    /// Sets the link traversal easing function (only used when manual traversal is enabled).
    /// </summary>
    public void SetLinkEasing(EasingType movementEasing, EasingType rotationEasing)
    {
        linkMovementEasing = movementEasing;
        linkRotationEasing = rotationEasing;
    }
    
    /// <summary>
    /// Sets the link height mode (only used when manual traversal is enabled).
    /// </summary>
    public void SetLinkHeightMode(LinkHeightMode mode)
    {
        linkHeightMode = mode;
    }
    
    /// <summary>
    /// Sets the link interrupt behavior.
    /// </summary>
    public void SetLinkInterruptBehavior(LinkInterruptBehavior behavior)
    {
        linkInterruptBehavior = behavior;
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
        
        // Draw current destination
        if (_hasDestination && _navAgent.hasPath)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(_navAgent.destination, 0.3f);
            Gizmos.DrawLine(transform.position, _navAgent.destination);
        }
        
        // Draw queued destinations
        if (_destinationQueue.Count > 0)
        {
            Gizmos.color = Color.blue;
            Vector3 prev = _hasDestination ? _navAgent.destination : transform.position;
            foreach (var dest in _destinationQueue)
            {
                Gizmos.DrawWireSphere(dest, 0.2f);
                Gizmos.DrawLine(prev, dest);
                prev = dest;
            }
        }
        
        // Draw link traversal info
        if (_isTraversingLink)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(_linkStartPos, _linkEndPos);
            Gizmos.DrawWireSphere(_linkStartPos, 0.15f);
            Gizmos.DrawWireSphere(_linkEndPos, 0.15f);
            
            // Draw progress indicator
            Vector3 progressPos = Vector3.Lerp(_linkStartPos, _linkEndPos, _linkProgress);
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(progressPos, 0.1f);
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
