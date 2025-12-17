using System;
using System.Collections.Generic;
using UnityEngine;
using Pathfinding;

namespace Agents
{
    /// <summary>
    /// Navigation state for visual feedback.
    /// </summary>
    public enum NavigationState : byte
    {
        /// <summary>No destination, not moving.</summary>
        Idle = 0,
        /// <summary>Actively moving towards destination.</summary>
        Moving = 1,
        /// <summary>Calculating path or waiting for path.</summary>
        Calculating = 2,
        /// <summary>Navigation error (unreachable, path failed).</summary>
        Error = 3
    }

    /// <summary>
    /// Handles all navigation logic for an agent using A* Pathfinding Project.
    /// Manages destination queue, path failure detection, and movement control.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Seeker))]
    [RequireComponent(typeof(AIPath))]
    public class AgentNavigator : MonoBehaviour
    {
        #region Events
        
        /// <summary>Fired when navigation state changes.</summary>
        public event Action<NavigationState> StateChanged;
        
        /// <summary>Fired when current destination is reached.</summary>
        public event Action DestinationReached;
        
        /// <summary>Fired when all queued destinations are completed.</summary>
        public event Action AllDestinationsCompleted;
        
        /// <summary>Fired when a new destination starts being processed.</summary>
        public event Action<Vector3> DestinationStarted;
        
        /// <summary>Fired when destination queue changes. Args: newQueueCount</summary>
        public event Action<int> QueueChanged;
        
        /// <summary>Fired when navigation is stopped (manually or due to error).</summary>
        public event Action Stopped;
        
        /// <summary>Fired when a destination is unreachable. Args: targetPosition</summary>
        public event Action<Vector3> DestinationUnreachable;
        
        #endregion
        
        #region Public Properties
        
        /// <summary>Current navigation state.</summary>
        public NavigationState State => _state;
        
        /// <summary>Whether this navigator has a current destination.</summary>
        public bool HasDestination => _hasDestination;
        
        /// <summary>Whether this navigator has queued destinations.</summary>
        public bool HasQueuedDestinations => _destinationQueue.Count > 0;
        
        /// <summary>Number of destinations in queue (not including current).</summary>
        public int QueuedDestinationCount => _destinationQueue.Count;
        
        /// <summary>Whether the agent is actively moving.</summary>
        public bool IsMoving => _aiPath && !_aiPath.reachedDestination && _aiPath.hasPath;
        
        /// <summary>Current destination (if any).</summary>
        public Vector3? CurrentDestination => _hasDestination ? (Vector3?)_aiPath.destination : null;
        
        /// <summary>Reference to AIPath component.</summary>
        public AIPath AIPath => _aiPath;
        
        /// <summary>Reference to Seeker component.</summary>
        public Seeker Seeker => _seeker;
        
        /// <summary>Remaining distance to destination.</summary>
        public float RemainingDistance => _aiPath ? _aiPath.remainingDistance : 0f;
        
        /// <summary>Whether the agent has reached its destination.</summary>
        public bool ReachedDestination => _aiPath && _aiPath.reachedDestination;
        
        /// <summary>Whether an error state was detected (unreachable).</summary>
        public bool HasError => _unreachableDetected;
        
        #endregion
        
        #region Serialized Fields
        
        [Header("Destination Queue")]
        [Tooltip("Maximum number of destinations that can be queued. 0 = unlimited.")]
        [SerializeField] private int maxQueueSize = 10;
        
        [Tooltip("When queue is full, should new destinations replace the last one?")]
        [SerializeField] private bool replaceLastWhenFull = true;
        
        [Header("Path Failure Handling")]
        [Tooltip("Distance threshold to detect unreachable destination (meters).")]
        [SerializeField] private float unreachableDistanceThreshold = 0.5f;
        
        [Tooltip("Number of recently failed destinations to track.")]
        [SerializeField] private int maxFailedDestinationsTracked = 5;
        
        [Tooltip("Radius to consider two positions as the same destination.")]
        [SerializeField] private float failedDestinationMatchRadius = 0.5f;
        
        [Header("Debug")]
        [SerializeField] private bool logNavigation;
        
        #endregion
        
        #region Private State
        
        // A* Pathfinding components
        private AIPath _aiPath;
        private Seeker _seeker;
        
        // Navigation state
        private NavigationState _state = NavigationState.Idle;
        private bool _hasDestination;
        private bool _initialized;
        
        // Path failure tracking
        private Vector3 _currentTargetDestination;
        private bool _unreachableDetected;
        
        // Recently failed destinations
        private readonly List<Vector3> _failedDestinations = new();
        
        // Destination queue
        private readonly Queue<Vector3> _destinationQueue = new();
        
        #endregion
        
        #region Initialization
        
        /// <summary>
        /// Initialize the navigator. Must be called before use.
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            
            _aiPath = GetComponent<AIPath>();
            _seeker = GetComponent<Seeker>();
            
            if (!_aiPath || !_seeker)
            {
                Debug.LogError($"[AgentNavigator] Missing AIPath or Seeker component on {gameObject.name}!");
                return;
            }
            
            // Configure AIPath - start in idle state
            _aiPath.simulateMovement = true;
            _aiPath.canSearch = false;
            _aiPath.enableRotation = true;
            _aiPath.isStopped = true;
            _aiPath.destination = transform.position;
            
            _initialized = true;
            
            if (logNavigation)
            {
                Debug.Log($"[AgentNavigator] Initialized on {gameObject.name}");
            }
        }
        
        private void Awake()
        {
            _aiPath = GetComponent<AIPath>();
            _seeker = GetComponent<Seeker>();
        }
        
        #endregion
        
        #region Update (Called by owner)
        
        /// <summary>
        /// Update navigation state. Should be called regularly by the owning agent.
        /// </summary>
        public void UpdateNavigation()
        {
            if (!_initialized || !_aiPath) return;
            
            UpdateState();
            CheckDestinationReached();
        }
        
        private void UpdateState()
        {
            NavigationState newState = DetermineState();
            
            if (newState != _state)
            {
                NavigationState oldState = _state;
                _state = newState;
                StateChanged?.Invoke(_state);
                
                if (logNavigation)
                {
                    Debug.Log($"[AgentNavigator] {gameObject.name} state: {oldState} -> {newState}");
                }
            }
        }
        
        /// <summary>
        /// Determine navigation state based on AIPath properties.
        /// </summary>
        private NavigationState DetermineState()
        {
            if (!_aiPath) return NavigationState.Error;
            
            // Error state from unreachable detection
            if (_unreachableDetected)
            {
                return NavigationState.Error;
            }
            
            // No destination = Idle
            if (!_hasDestination)
            {
                return NavigationState.Idle;
            }
            
            // Path is being calculated
            if (_aiPath.pathPending)
            {
                return NavigationState.Calculating;
            }
            
            // Has valid path and actively moving
            if (_aiPath.hasPath && !_aiPath.reachedEndOfPath)
            {
                return NavigationState.Moving;
            }
            
            // Reached the actual destination
            if (_aiPath.reachedDestination)
            {
                return NavigationState.Idle;
            }
            
            // Reached end of path but NOT destination (unreachable - will be caught soon)
            if (_aiPath.reachedEndOfPath && !_aiPath.reachedDestination)
            {
                return NavigationState.Calculating;
            }
            
            return NavigationState.Calculating;
        }
        
        private void CheckDestinationReached()
        {
            if (!_aiPath || !_hasDestination) return;
            if (_unreachableDetected) return;
            
            // SUCCESS: Agent reached the actual destination
            if (_aiPath.reachedDestination)
            {
                if (logNavigation)
                {
                    Debug.Log($"[AgentNavigator] {gameObject.name} reached destination");
                }
                CompleteCurrentDestination();
                return;
            }
            
            // UNREACHABLE CHECK: Using AIPath's built-in properties
            if (_aiPath.reachedEndOfPath && !_aiPath.reachedDestination)
            {
                float endToDestDist = Vector3.Distance(_aiPath.endOfPath, _aiPath.destination);
                
                if (endToDestDist > unreachableDistanceThreshold)
                {
                    if (logNavigation)
                    {
                        Debug.LogWarning($"[AgentNavigator] {gameObject.name} UNREACHABLE - " +
                            $"EndOfPath: {_aiPath.endOfPath}, Destination: {_aiPath.destination}, " +
                            $"Gap: {endToDestDist:F2}m");
                    }
                    
                    _unreachableDetected = true;
                    GiveUpOnDestination();
                }
            }
        }
        
        #endregion
        
        #region Destination Completion
        
        private void CompleteCurrentDestination()
        {
            if (logNavigation)
            {
                Debug.Log($"[AgentNavigator] {gameObject.name} completed destination, queue remaining: {_destinationQueue.Count}");
            }
            
            _hasDestination = false;
            _unreachableDetected = false;
            
            // Stop AIPath auto-recalculation
            if (_aiPath)
            {
                _aiPath.canSearch = false;
                _aiPath.SetPath(null);
            }
            
            DestinationReached?.Invoke();
            
            // Process next queued destination
            if (_destinationQueue.Count > 0)
            {
                ProcessNextQueuedDestination();
            }
            else
            {
                AllDestinationsCompleted?.Invoke();
            }
        }
        
        private void ProcessNextQueuedDestination()
        {
            while (_destinationQueue.Count > 0)
            {
                Vector3 nextDest = _destinationQueue.Dequeue();
                
                if (IsFailedDestination(nextDest))
                {
                    if (logNavigation)
                    {
                        Debug.Log($"[AgentNavigator] {gameObject.name} skipping failed destination: {nextDest}");
                    }
                    QueueChanged?.Invoke(_destinationQueue.Count);
                    continue;
                }
                
                if (logNavigation)
                {
                    Debug.Log($"[AgentNavigator] {gameObject.name} starting next destination: {nextDest}. Remaining: {_destinationQueue.Count}");
                }
                
                QueueChanged?.Invoke(_destinationQueue.Count);
                SetDestinationInternal(nextDest);
                return;
            }
            
            if (logNavigation)
            {
                Debug.Log($"[AgentNavigator] {gameObject.name} queue exhausted");
            }
            
            AllDestinationsCompleted?.Invoke();
        }
        
        private void GiveUpOnDestination()
        {
            Vector3 unreachableTarget = _currentTargetDestination;
            
            AddFailedDestination(unreachableTarget);
            
            if (logNavigation)
            {
                Debug.LogWarning($"[AgentNavigator] {gameObject.name} GIVING UP on: {unreachableTarget}. " +
                    $"Failed list: {_failedDestinations.Count}");
            }
            
            _hasDestination = false;
            
            // Remove failed destinations from queue
            RemoveFailedDestinationsFromQueue();
            
            // Clear remaining queue
            if (_destinationQueue.Count > 0)
            {
                _destinationQueue.Clear();
                QueueChanged?.Invoke(0);
            }
            
            // Stop AIPath completely
            if (_aiPath)
            {
                _aiPath.canSearch = false;
                _aiPath.isStopped = true;
                _aiPath.SetPath(null);
                _aiPath.destination = transform.position;
            }
            
            DestinationUnreachable?.Invoke(unreachableTarget);
            Stopped?.Invoke();
        }
        
        #endregion
        
        #region Public API - Movement
        
        /// <summary>
        /// Sets the navigation destination.
        /// </summary>
        /// <param name="destination">Target position.</param>
        /// <param name="immediate">If true, cancels current movement and clears queue.</param>
        /// <returns>True if destination was accepted.</returns>
        public bool SetDestination(Vector3 destination, bool immediate = false)
        {
            if (!_initialized || !_aiPath)
            {
                Debug.LogWarning($"[AgentNavigator] {gameObject.name} not initialized.");
                return false;
            }
            
            if (IsFailedDestination(destination))
            {
                if (logNavigation)
                {
                    Debug.LogWarning($"[AgentNavigator] {gameObject.name} rejecting failed destination: {destination}");
                }
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
            if (_destinationQueue.Count > 0)
            {
                _destinationQueue.Clear();
                QueueChanged?.Invoke(0);
            }
            
            return SetDestinationInternal(destination);
        }
        
        private bool HandleQueuedDestination(Vector3 destination)
        {
            if (!_hasDestination)
            {
                return SetDestinationInternal(destination);
            }
            
            if (maxQueueSize > 0 && _destinationQueue.Count >= maxQueueSize)
            {
                if (replaceLastWhenFull)
                {
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
                }
                else
                {
                    Debug.LogWarning($"[AgentNavigator] {gameObject.name} queue full ({maxQueueSize})");
                    return false;
                }
            }
            
            _destinationQueue.Enqueue(destination);
            
            if (logNavigation)
            {
                Debug.Log($"[AgentNavigator] {gameObject.name} queued destination. Size: {_destinationQueue.Count}");
            }
            
            QueueChanged?.Invoke(_destinationQueue.Count);
            return true;
        }
        
        private bool SetDestinationInternal(Vector3 destination)
        {
            if (!_aiPath) return false;
            
            _currentTargetDestination = destination;
            _unreachableDetected = false;
            
            _aiPath.canSearch = true;
            _aiPath.isStopped = false;
            _aiPath.destination = destination;
            _aiPath.SearchPath();
            
            _hasDestination = true;
            
            if (logNavigation)
            {
                Debug.Log($"[AgentNavigator] {gameObject.name} moving to {destination}");
            }
            
            DestinationStarted?.Invoke(destination);
            return true;
        }
        
        /// <summary>
        /// Stops current movement immediately and clears the queue.
        /// </summary>
        public void Stop()
        {
            if (_destinationQueue.Count > 0)
            {
                _destinationQueue.Clear();
                QueueChanged?.Invoke(0);
            }
            
            if (_aiPath)
            {
                _aiPath.canSearch = false;
                _aiPath.isStopped = true;
                _aiPath.SetPath(null);
                _aiPath.destination = transform.position;
            }
            
            _hasDestination = false;
            _unreachableDetected = false;
            
            if (logNavigation)
            {
                Debug.Log($"[AgentNavigator] {gameObject.name} stopped");
            }
            
            Stopped?.Invoke();
        }
        
        /// <summary>
        /// Clears the destination queue without stopping current movement.
        /// </summary>
        public void ClearQueue()
        {
            if (_destinationQueue.Count > 0)
            {
                _destinationQueue.Clear();
                QueueChanged?.Invoke(0);
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
        /// Peeks at the next queued destination.
        /// </summary>
        public Vector3? PeekNextDestination()
        {
            return _destinationQueue.Count > 0 ? _destinationQueue.Peek() : null;
        }
        
        /// <summary>
        /// Teleport to position. Clears queue.
        /// </summary>
        public void Teleport(Vector3 worldPosition)
        {
            Stop();
            
            if (_aiPath)
            {
                _aiPath.Teleport(worldPosition);
            }
            else
            {
                transform.position = worldPosition;
            }
            
            if (logNavigation)
            {
                Debug.Log($"[AgentNavigator] {gameObject.name} teleported to {worldPosition}");
            }
        }
        
        #endregion
        
        #region Failed Destination Tracking
        
        private void AddFailedDestination(Vector3 destination)
        {
            if (IsFailedDestination(destination)) return;
            
            if (_failedDestinations.Count >= maxFailedDestinationsTracked)
            {
                _failedDestinations.RemoveAt(0);
            }
            _failedDestinations.Add(destination);
        }
        
        private bool IsFailedDestination(Vector3 destination)
        {
            for (int i = 0; i < _failedDestinations.Count; i++)
            {
                if (Vector3.Distance(_failedDestinations[i], destination) <= failedDestinationMatchRadius)
                {
                    return true;
                }
            }
            return false;
        }
        
        private void RemoveFailedDestinationsFromQueue()
        {
            if (_destinationQueue.Count == 0 || _failedDestinations.Count == 0) return;
            
            var destinations = new List<Vector3>(_destinationQueue);
            destinations.RemoveAll(d => IsFailedDestination(d));
            
            _destinationQueue.Clear();
            foreach (var d in destinations)
            {
                _destinationQueue.Enqueue(d);
            }
        }
        
        /// <summary>
        /// Clear the failed destinations list.
        /// </summary>
        public void ClearFailedDestinations()
        {
            _failedDestinations.Clear();
            if (logNavigation)
            {
                Debug.Log($"[AgentNavigator] {gameObject.name} cleared failed destinations");
            }
        }
        
        #endregion
        
        #region Configuration
        
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
        
        /// <summary>
        /// Set the repath rate (how often paths are recalculated).
        /// </summary>
        public void SetRepathRate(float rate)
        {
            if (_aiPath)
            {
                _aiPath.repathRate = rate;
            }
        }
        
        #endregion
        
        #region Debug
        
        private void OnDrawGizmosSelected()
        {
            if (!_aiPath) return;
            
            // Draw current destination
            if (_hasDestination)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(_aiPath.destination, 0.3f);
                Gizmos.DrawLine(transform.position, _aiPath.destination);
            }
            
            // Draw path
            if (_seeker)
            {
                var path = _seeker.GetCurrentPath();
                if (path?.vectorPath != null && path.vectorPath.Count > 0)
                {
                    Gizmos.color = Color.yellow;
                    for (int i = 0; i < path.vectorPath.Count - 1; i++)
                    {
                        Gizmos.DrawLine(path.vectorPath[i], path.vectorPath[i + 1]);
                    }
                }
            }
            
            // Draw queue
            if (_destinationQueue.Count > 0)
            {
                Gizmos.color = Color.blue;
                Vector3 prev = _hasDestination ? _aiPath.destination : transform.position;
                foreach (var dest in _destinationQueue)
                {
                    Gizmos.DrawWireSphere(dest, 0.2f);
                    Gizmos.DrawLine(prev, dest);
                    prev = dest;
                }
            }
            
            // Draw failed destinations
            Gizmos.color = Color.red;
            foreach (var failed in _failedDestinations)
            {
                Gizmos.DrawWireSphere(failed, 0.15f);
            }
        }
        
        #endregion
    }
}

