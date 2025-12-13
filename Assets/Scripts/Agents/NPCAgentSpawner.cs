using System;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

namespace Agents
{
    /// <summary>
    /// Handles agent spawning and selection/movement commands via InputActions.
    /// Assign InputAction references in the Inspector.
    /// </summary>
    [DisallowMultipleComponent]
    public class NPCAgentSpawner : MonoBehaviour
    {
        #region Configuration
        
        [Header("Input Actions")]
        [Tooltip("InputAction for spawning a new agent at mouse cursor position.")]
        [SerializeField] private InputActionReference spawnAgentAction;
        
        [Tooltip("InputAction for selecting an agent or issuing a move command.")]
        [SerializeField] private InputActionReference selectMoveAction;
        
        [Header("Spawn Position Settings")]
        [Tooltip("Optional spawn point transform. If set and 'Use Spawn Point' is true, agents spawn here.")]
        [SerializeField] private Transform spawnPoint;
        
        [Tooltip("Use spawn point instead of mouse cursor position.")]
        [SerializeField] private bool useSpawnPoint = false;
        
        [Tooltip("Layer mask for ground/platform detection when spawning.")]
        [SerializeField] private LayerMask groundLayerMask = ~0;
        
        [Tooltip("Maximum raycast distance for ground detection.")]
        [SerializeField] private float groundRaycastDistance = 1000f;
        
        [Header("Spawn Raycast Mode")]
        [Tooltip("How to determine spawn position from mouse click.")]
        [SerializeField] private SpawnRaycastMode raycastMode = SpawnRaycastMode.PhysicsRaycast;
        
        [Tooltip("Plane height for PlaneRaycast mode.")]
        [SerializeField] private float planeHeight = 0f;
        
        [Header("Selection Settings")]
        [Tooltip("Layer mask for agent selection raycasts.")]
        [SerializeField] private LayerMask agentLayerMask = ~0;
        
        [Tooltip("Maximum raycast distance for agent selection.")]
        [SerializeField] private float maxSelectionRaycastDistance = 1000f;
        
        [Header("References")]
        [Tooltip("Main camera for raycasting. If null, uses Camera.main.")]
        [SerializeField] private Camera mainCamera;
        
        [Header("Debug")]
        [SerializeField] private bool debugLogs = false;
        [SerializeField] private bool drawDebugRays = false;
        
        #endregion
        
        #region Enums
        
        public enum SpawnRaycastMode
        {
            [Tooltip("Physics raycast - detects actual collider surfaces (recommended).")]
            PhysicsRaycast,
            
            [Tooltip("Plane raycast - raycasts to a horizontal plane at specified height.")]
            PlaneRaycast,
            
            [Tooltip("NavMesh sample - finds nearest NavMesh point from plane intersection.")]
            NavMeshSample
        }
        
        #endregion
        
        #region Events
        
        /// <summary>Fired when an agent is spawned via this spawner.</summary>
        public event Action<NPCAgent> OnAgentSpawned;
        
        /// <summary>Fired when an agent is selected.</summary>
        public event Action<NPCAgent> OnAgentSelected;
        
        /// <summary>Fired when an agent is deselected.</summary>
        public event Action<NPCAgent> OnAgentDeselected;
        
        /// <summary>Fired when a move command is issued to the selected agent.</summary>
        public event Action<NPCAgent, Vector3> OnMoveCommandIssued;
        
        #endregion
        
        #region Public Properties
        
        /// <summary>Currently selected agent (if any).</summary>
        public NPCAgent SelectedAgent => _selectedAgent;
        
        #endregion
        
        #region Private State
        
        private NPCManager _manager;
        private NPCAgent _selectedAgent;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            if (!mainCamera)
            {
                mainCamera = Camera.main;
            }
            
            if (!mainCamera)
            {
                Debug.LogError("[NPCAgentSpawner] No camera assigned and Camera.main is null.");
            }
        }
        
        private void Start()
        {
            _manager = NPCManager.Instance;
            
            if (!_manager)
            {
                Debug.LogError("[NPCAgentSpawner] NPCManager not found in scene. Disabling spawner.");
                enabled = false;
            }
        }
        
        private void OnEnable()
        {
            if (spawnAgentAction?.action != null)
            {
                spawnAgentAction.action.performed += OnSpawnActionPerformed;
                spawnAgentAction.action.Enable();
            }
            
            if (selectMoveAction?.action != null)
            {
                selectMoveAction.action.performed += OnSelectMoveActionPerformed;
                selectMoveAction.action.Enable();
            }
        }
        
        private void OnDisable()
        {
            if (spawnAgentAction?.action != null)
            {
                spawnAgentAction.action.performed -= OnSpawnActionPerformed;
            }
            
            if (selectMoveAction?.action != null)
            {
                selectMoveAction.action.performed -= OnSelectMoveActionPerformed;
            }
        }
        
        #endregion
        
        #region Input Handlers
        
        private void OnSpawnActionPerformed(InputAction.CallbackContext ctx)
        {
            SpawnAgent();
        }
        
        private void OnSelectMoveActionPerformed(InputAction.CallbackContext ctx)
        {
            HandleSelectOrMove();
        }
        
        #endregion
        
        #region Spawning
        
        /// <summary>
        /// Spawns an agent at the configured location (mouse cursor or spawn point).
        /// </summary>
        public void SpawnAgent()
        {
            if (!_manager) return;
            
            Vector3 spawnPosition;
            
            if (useSpawnPoint && spawnPoint)
            {
                spawnPosition = spawnPoint.position;
            }
            else
            {
                if (!TryGetSpawnPosition(out spawnPosition))
                {
                    if (debugLogs)
                        Debug.Log("[NPCAgentSpawner] Failed to find valid spawn position from mouse cursor.");
                    return;
                }
            }
            
            var agent = _manager.SpawnAgent(spawnPosition);
            
            if (agent)
            {
                if (debugLogs)
                    Debug.Log($"[NPCAgentSpawner] Spawned agent at {spawnPosition}");
                
                OnAgentSpawned?.Invoke(agent);
            }
        }
        
        /// <summary>
        /// Spawns an agent at a specific world position.
        /// </summary>
        public NPCAgent SpawnAgentAt(Vector3 worldPosition)
        {
            if (!_manager) return null;
            
            var agent = _manager.SpawnAgent(worldPosition);
            
            if (agent)
            {
                OnAgentSpawned?.Invoke(agent);
            }
            
            return agent;
        }
        
        #endregion
        
        #region Selection & Movement
        
        /// <summary>
        /// Handles the combined select/move action.
        /// - If clicking on an agent: select it
        /// - If an agent is selected and clicking elsewhere: issue move command
        /// </summary>
        private void HandleSelectOrMove()
        {
            if (!mainCamera) return;
            
            Vector2 mousePosition = GetMouseScreenPosition();
            Ray ray = mainCamera.ScreenPointToRay(mousePosition);
            
            // First, check if we're clicking on an agent (physics raycast for colliders)
            if (Physics.Raycast(ray, out RaycastHit agentHit, maxSelectionRaycastDistance, agentLayerMask))
            {
                var clickedAgent = agentHit.collider.GetComponent<NPCAgent>();
                if (!clickedAgent)
                {
                    clickedAgent = agentHit.collider.GetComponentInParent<NPCAgent>();
                }
                
                if (clickedAgent)
                {
                    SelectAgent(clickedAgent);
                    return;
                }
            }
            
            // Not clicking on an agent
            if (_selectedAgent)
            {
                // We have a selected agent - try to issue move command
                if (TryGetSpawnPosition(out Vector3 destination))
                {
                    // Sample NavMesh at the destination
                    if (NavMesh.SamplePosition(destination, out NavMeshHit navHit, 5f, NavMesh.AllAreas))
                    {
                        IssueMoveCommand(navHit.position);
                    }
                    else
                    {
                        if (debugLogs)
                            Debug.Log("[NPCAgentSpawner] Clicked position is not on NavMesh.");
                    }
                }
            }
            else
            {
                if (debugLogs)
                    Debug.Log("[NPCAgentSpawner] Clicked empty space with no agent selected.");
            }
        }
        
        /// <summary>
        /// Selects the specified agent, deselecting any previously selected agent.
        /// </summary>
        public void SelectAgent(NPCAgent agent)
        {
            if (agent == _selectedAgent) return;
            
            // Deselect previous
            if (_selectedAgent)
            {
                _selectedAgent.Deselect();
                OnAgentDeselected?.Invoke(_selectedAgent);
            }
            
            // Select new
            _selectedAgent = agent;
            
            if (_selectedAgent)
            {
                _selectedAgent.Select();
                OnAgentSelected?.Invoke(_selectedAgent);
                
                if (debugLogs)
                    Debug.Log($"[NPCAgentSpawner] Selected agent {_selectedAgent.AgentId}");
            }
        }
        
        /// <summary>
        /// Deselects the currently selected agent.
        /// </summary>
        public void DeselectAgent()
        {
            if (_selectedAgent)
            {
                _selectedAgent.Deselect();
                OnAgentDeselected?.Invoke(_selectedAgent);
                
                if (debugLogs)
                    Debug.Log($"[NPCAgentSpawner] Deselected agent {_selectedAgent.AgentId}");
                
                _selectedAgent = null;
            }
        }
        
        /// <summary>
        /// Issues a move command to the selected agent.
        /// </summary>
        public void IssueMoveCommand(Vector3 destination)
        {
            if (!_selectedAgent) return;
            
            bool success = _selectedAgent.SetDestination(destination);
            
            if (success)
            {
                if (debugLogs)
                    Debug.Log($"[NPCAgentSpawner] Issued move command to agent {_selectedAgent.AgentId} -> {destination}");
                
                OnMoveCommandIssued?.Invoke(_selectedAgent, destination);
            }
        }
        
        #endregion
        
        #region Raycast Helpers
        
        /// <summary>
        /// Gets mouse screen position using new Input System.
        /// </summary>
        private Vector2 GetMouseScreenPosition()
        {
            return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
        }
        
        /// <summary>
        /// Gets spawn position based on configured raycast mode.
        /// </summary>
        private bool TryGetSpawnPosition(out Vector3 worldPosition)
        {
            worldPosition = Vector3.zero;
            
            if (!mainCamera) return false;
            
            Vector2 mousePosition = GetMouseScreenPosition();
            Ray ray = mainCamera.ScreenPointToRay(mousePosition);
            
            if (drawDebugRays)
            {
                Debug.DrawRay(ray.origin, ray.direction * groundRaycastDistance, Color.yellow, 2f);
            }
            
            switch (raycastMode)
            {
                case SpawnRaycastMode.PhysicsRaycast:
                    return TryPhysicsRaycast(ray, out worldPosition);
                    
                case SpawnRaycastMode.PlaneRaycast:
                    return TryPlaneRaycast(ray, out worldPosition);
                    
                case SpawnRaycastMode.NavMeshSample:
                    return TryNavMeshSample(ray, out worldPosition);
                    
                default:
                    return TryPhysicsRaycast(ray, out worldPosition);
            }
        }
        
        /// <summary>
        /// Physics raycast - hits actual collider surfaces.
        /// </summary>
        private bool TryPhysicsRaycast(Ray ray, out Vector3 worldPosition)
        {
            worldPosition = Vector3.zero;
            
            if (Physics.Raycast(ray, out RaycastHit hit, groundRaycastDistance, groundLayerMask))
            {
                worldPosition = hit.point;
                
                if (debugLogs)
                    Debug.Log($"[NPCAgentSpawner] Physics raycast hit '{hit.collider.name}' at {hit.point}");
                
                if (drawDebugRays)
                {
                    Debug.DrawLine(ray.origin, hit.point, Color.green, 2f);
                }
                
                return true;
            }
            
            if (debugLogs)
                Debug.Log("[NPCAgentSpawner] Physics raycast missed - no collider hit.");
            
            return false;
        }
        
        /// <summary>
        /// Plane raycast - hits horizontal plane at specified height.
        /// </summary>
        private bool TryPlaneRaycast(Ray ray, out Vector3 worldPosition)
        {
            worldPosition = Vector3.zero;
            
            var groundPlane = new Plane(Vector3.up, new Vector3(0, planeHeight, 0));
            
            if (groundPlane.Raycast(ray, out float distance))
            {
                worldPosition = ray.GetPoint(distance);
                
                if (debugLogs)
                    Debug.Log($"[NPCAgentSpawner] Plane raycast hit at {worldPosition}");
                
                if (drawDebugRays)
                {
                    Debug.DrawLine(ray.origin, worldPosition, Color.blue, 2f);
                }
                
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// NavMesh sample - plane raycast then find nearest NavMesh point.
        /// </summary>
        private bool TryNavMeshSample(Ray ray, out Vector3 worldPosition)
        {
            worldPosition = Vector3.zero;
            
            // First get plane intersection
            if (!TryPlaneRaycast(ray, out Vector3 planeHit))
            {
                return false;
            }
            
            // Then sample NavMesh
            if (NavMesh.SamplePosition(planeHit, out NavMeshHit navHit, 10f, NavMesh.AllAreas))
            {
                worldPosition = navHit.position;
                
                if (debugLogs)
                    Debug.Log($"[NPCAgentSpawner] NavMesh sample found at {worldPosition}");
                
                if (drawDebugRays)
                {
                    Debug.DrawLine(planeHit, worldPosition, Color.magenta, 2f);
                }
                
                return true;
            }
            
            if (debugLogs)
                Debug.Log("[NPCAgentSpawner] NavMesh sample failed - no NavMesh nearby.");
            
            return false;
        }
        
        #endregion
    }
}
