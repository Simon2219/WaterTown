using System;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

namespace Agents
{
    /// <summary>
    /// Handles agent spawning and selection/movement commands via InputActions.
    /// 
    /// Flow:
    /// 1. Click anywhere → raycast
    /// 2. If raycast hits an Agent → select it
    /// 3. If agent is already selected AND click on ground → move agent there
    /// </summary>
    [DisallowMultipleComponent]
    public class NPCAgentSpawner : MonoBehaviour
    {
        #region Configuration
        
        [Header("Input Actions")]
        [Tooltip("InputAction for spawning a new agent.")]
        [SerializeField] private InputActionReference spawnAgentAction;
        
        [Tooltip("InputAction for selecting/moving agents (typically left mouse click).")]
        [SerializeField] private InputActionReference selectMoveAction;
        
        [Header("Spawn Settings")]
        [Tooltip("Optional fixed spawn point. If set and enabled, agents spawn here.")]
        [SerializeField] private Transform spawnPoint;
        [SerializeField] private bool useSpawnPoint = false;
        
        [Header("Ground Detection")]
        [Tooltip("Plane height for ground detection (Y coordinate of your NavMesh/platforms).")]
        [SerializeField] private float groundPlaneHeight = 0f;
        
        [Tooltip("Search radius for finding NavMesh from click point.")]
        [SerializeField] private float navMeshSearchRadius = 5f;
        
        [Header("Raycasting")]
        [Tooltip("Maximum raycast distance.")]
        [SerializeField] private float maxRaycastDistance = 500f;
        
        [Header("References")]
        [SerializeField] private Camera mainCamera;
        
        [Header("Debug")]
        [SerializeField] private bool debugLogs = true;
        [SerializeField] private bool drawDebugVisuals = true;
        
        #endregion
        
        #region Events
        
        public event Action<NPCAgent> OnAgentSpawned;
        public event Action<NPCAgent> OnAgentSelected;
        public event Action<NPCAgent> OnAgentDeselected;
        public event Action<NPCAgent, Vector3> OnMoveCommandIssued;
        
        #endregion
        
        #region Properties
        
        public NPCAgent SelectedAgent => _selectedAgent;
        
        #endregion
        
        #region Private
        
        private NPCManager _manager;
        private NPCAgent _selectedAgent;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            if (!mainCamera) mainCamera = Camera.main;
        }
        
        private void Start()
        {
            _manager = NPCManager.Instance;
            if (!_manager)
            {
                Debug.LogError("[NPCAgentSpawner] NPCManager not found!");
                enabled = false;
            }
        }
        
        private void OnEnable()
        {
            if (spawnAgentAction?.action != null)
            {
                spawnAgentAction.action.performed += OnSpawnInput;
                spawnAgentAction.action.Enable();
                if (debugLogs) Debug.Log($"[Spawner] Registered SPAWN: {spawnAgentAction.action.name}");
            }
            
            if (selectMoveAction?.action != null)
            {
                selectMoveAction.action.performed += OnSelectMoveInput;
                selectMoveAction.action.Enable();
                if (debugLogs) Debug.Log($"[Spawner] Registered SELECT/MOVE: {selectMoveAction.action.name}");
            }
        }
        
        private void OnDisable()
        {
            if (spawnAgentAction?.action != null)
                spawnAgentAction.action.performed -= OnSpawnInput;
            
            if (selectMoveAction?.action != null)
                selectMoveAction.action.performed -= OnSelectMoveInput;
        }
        
        #endregion
        
        #region Input Handlers
        
        private void OnSpawnInput(InputAction.CallbackContext ctx)
        {
            if (debugLogs) Debug.Log("[Spawner] ▶ SPAWN INPUT");
            DoSpawn();
        }
        
        private void OnSelectMoveInput(InputAction.CallbackContext ctx)
        {
            if (debugLogs) Debug.Log("[Spawner] ▶ SELECT/MOVE INPUT");
            DoSelectOrMove();
        }
        
        #endregion
        
        #region Core Logic
        
        /// <summary>
        /// Spawn an agent at mouse position (or spawn point if configured).
        /// </summary>
        private void DoSpawn()
        {
            if (!_manager) return;
            
            Vector3 pos;
            if (useSpawnPoint && spawnPoint)
            {
                pos = spawnPoint.position;
            }
            else if (!GetGroundPosition(out pos))
            {
                if (debugLogs) Debug.LogWarning("[Spawner] Could not find ground position for spawn");
                return;
            }
            
            var agent = _manager.SpawnAgent(pos);
            if (agent)
            {
                if (debugLogs) Debug.Log($"[Spawner] ✓ Spawned agent #{agent.AgentId}");
                OnAgentSpawned?.Invoke(agent);
            }
        }
        
        /// <summary>
        /// Main click handler:
        /// 1. Raycast to find what was clicked
        /// 2. If clicked on agent → select it
        /// 3. Else if have selected agent → move it to click position
        /// </summary>
        private void DoSelectOrMove()
        {
            if (!mainCamera) return;
            
            Ray ray = mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            
            if (drawDebugVisuals)
            {
                Debug.DrawRay(ray.origin, ray.direction * maxRaycastDistance, Color.cyan, 2f);
            }
            
            // Step 1: Check if we clicked on an agent
            // Use RaycastAll to check ALL objects the ray passes through
            RaycastHit[] hits = Physics.RaycastAll(ray, maxRaycastDistance);
            
            if (debugLogs) Debug.Log($"[Spawner] Raycast hit {hits.Length} objects");
            
            // Check each hit for an NPCAgent
            foreach (var hit in hits)
            {
                var agent = hit.collider.GetComponent<NPCAgent>();
                if (agent == null)
                    agent = hit.collider.GetComponentInParent<NPCAgent>();
                
                if (agent != null)
                {
                    // Found an agent - select it
                    if (debugLogs) Debug.Log($"[Spawner] ★ Clicked on agent #{agent.AgentId}");
                    SelectAgent(agent);
                    return;
                }
            }
            
            // Step 2: Didn't click on an agent
            // If we have a selected agent, try to move it
            if (_selectedAgent != null)
            {
                if (GetGroundPosition(out Vector3 destination))
                {
                    if (debugLogs) Debug.Log($"[Spawner] → Moving agent #{_selectedAgent.AgentId} to {destination}");
                    
                    if (drawDebugVisuals)
                    {
                        Debug.DrawRay(destination, Vector3.up * 3f, Color.blue, 3f);
                    }
                    
                    MoveSelectedAgent(destination);
                }
                else
                {
                    if (debugLogs) Debug.Log("[Spawner] Could not find ground position for move");
                }
            }
            else
            {
                if (debugLogs) Debug.Log("[Spawner] No agent selected - click on one first");
            }
        }
        
        #endregion
        
        #region Selection
        
        public void SelectAgent(NPCAgent agent)
        {
            // Deselect previous
            if (_selectedAgent != null && _selectedAgent != agent)
            {
                _selectedAgent.Deselect();
                OnAgentDeselected?.Invoke(_selectedAgent);
                if (debugLogs) Debug.Log($"[Spawner] Deselected agent #{_selectedAgent.AgentId}");
            }
            
            // Select new
            _selectedAgent = agent;
            if (_selectedAgent != null)
            {
                _selectedAgent.Select();
                OnAgentSelected?.Invoke(_selectedAgent);
                if (debugLogs) Debug.Log($"[Spawner] ★ Selected agent #{_selectedAgent.AgentId}");
            }
        }
        
        public void DeselectCurrent()
        {
            if (_selectedAgent != null)
            {
                _selectedAgent.Deselect();
                OnAgentDeselected?.Invoke(_selectedAgent);
                if (debugLogs) Debug.Log($"[Spawner] Deselected agent #{_selectedAgent.AgentId}");
                _selectedAgent = null;
            }
        }
        
        #endregion
        
        #region Movement
        
        private void MoveSelectedAgent(Vector3 destination)
        {
            if (_selectedAgent == null) return;
            
            bool success = _selectedAgent.SetDestination(destination);
            
            if (success)
            {
                if (debugLogs) Debug.Log($"[Spawner] ✓ Move command sent");
                OnMoveCommandIssued?.Invoke(_selectedAgent, destination);
            }
            else
            {
                if (debugLogs) Debug.LogWarning("[Spawner] ✗ Move command failed");
            }
        }
        
        #endregion
        
        #region Ground Position
        
        /// <summary>
        /// Get world position from mouse cursor by raycasting to ground plane,
        /// then snapping to nearest NavMesh point.
        /// </summary>
        private bool GetGroundPosition(out Vector3 position)
        {
            position = Vector3.zero;
            if (!mainCamera) return false;
            
            Vector2 mousePos = Mouse.current.position.ReadValue();
            Ray ray = mainCamera.ScreenPointToRay(mousePos);
            
            // Raycast to horizontal plane at ground height
            var plane = new Plane(Vector3.up, new Vector3(0, groundPlaneHeight, 0));
            
            if (!plane.Raycast(ray, out float distance))
            {
                return false;
            }
            
            Vector3 planeHit = ray.GetPoint(distance);
            
            if (drawDebugVisuals)
            {
                Debug.DrawRay(planeHit, Vector3.up * 2f, Color.yellow, 1f);
            }
            
            // Find nearest NavMesh point
            if (NavMesh.SamplePosition(planeHit, out NavMeshHit navHit, navMeshSearchRadius, NavMesh.AllAreas))
            {
                position = navHit.position;
                
                if (drawDebugVisuals)
                {
                    Debug.DrawLine(planeHit, position, Color.magenta, 1f);
                }
                
                return true;
            }
            
            if (debugLogs)
            {
                Debug.LogWarning($"[Spawner] No NavMesh within {navMeshSearchRadius}m of {planeHit}");
            }
            
            return false;
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Spawn an agent at a specific position.
        /// </summary>
        public NPCAgent SpawnAt(Vector3 position)
        {
            if (!_manager) return null;
            var agent = _manager.SpawnAgent(position);
            if (agent) OnAgentSpawned?.Invoke(agent);
            return agent;
        }
        
        /// <summary>
        /// Issue move command to selected agent.
        /// </summary>
        public void MoveSelectedTo(Vector3 destination)
        {
            MoveSelectedAgent(destination);
        }
        
        #endregion
    }
}
