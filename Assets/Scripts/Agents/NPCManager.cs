using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using UnityEngine.AI;

namespace Agents
{
    /// <summary>
    /// Central manager for all NPC agents in the town.
    /// Creates and manages both GameObjects (for NavMeshAgent) and ECS Entities (for DOTS processing).
    /// Designed for 500+ agents with Burst-compiled parallel processing.
    /// </summary>
    [DisallowMultipleComponent]
    public class NPCManager : MonoBehaviour
    {
        #region Singleton
        
        private static NPCManager _instance;
        public static NPCManager Instance
        {
            get
            {
                if (!_instance)
                {
                    _instance = FindFirstObjectByType<NPCManager>();
                    if (!_instance)
                    {
                        Debug.LogError("[NPCManager] No NPCManager found in scene.");
                    }
                }
                return _instance;
            }
        }
        
        #endregion
        
        #region Configuration
        
        [Header("Agent Prefab Settings")]
        [Tooltip("If null, agents are created procedurally as capsules.")]
        [SerializeField] private GameObject agentPrefab;
        
        [Header("Default Agent Settings")]
        [Tooltip("Default movement speed for agents (units/second).")]
        [SerializeField] private float defaultSpeed = 3.5f;
        
        [Tooltip("Default angular speed for agents (degrees/second).")]
        [SerializeField] private float defaultAngularSpeed = 120f;
        
        [Tooltip("Default acceleration for agents.")]
        [SerializeField] private float defaultAcceleration = 8f;
        
        [Tooltip("Default stopping distance for agents.")]
        [SerializeField] private float defaultStoppingDistance = 0.1f;
        
        [Tooltip("Height offset for agents above NavMesh surface.")]
        [SerializeField] private float heightOffset = 0.05f;
        
        [Tooltip("Capsule radius for procedural agents.")]
        [SerializeField] private float agentRadius = 0.3f;
        
        [Tooltip("Capsule height for procedural agents.")]
        [SerializeField] private float agentHeight = 1.8f;
        
        [Header("Status Colors")]
        [SerializeField] private Color idleColor = new Color(0.3f, 0.7f, 0.3f, 1f);      // Green
        [SerializeField] private Color movingColor = new Color(0.3f, 0.5f, 0.9f, 1f);    // Blue
        [SerializeField] private Color selectedColor = new Color(1f, 0.9f, 0.2f, 1f);    // Yellow
        [SerializeField] private Color waitingColor = new Color(0.9f, 0.6f, 0.2f, 1f);   // Orange
        [SerializeField] private Color errorColor = new Color(0.9f, 0.2f, 0.2f, 1f);     // Red
        
        [Header("Selection Visual Settings")]
        [Tooltip("Scale multiplier when agent is selected.")]
        [SerializeField] private float selectedScaleMultiplier = 1.1f;
        
        #endregion
        
        #region Public Properties
        
        public float DefaultSpeed => defaultSpeed;
        public float DefaultAngularSpeed => defaultAngularSpeed;
        public float DefaultAcceleration => defaultAcceleration;
        public float DefaultStoppingDistance => defaultStoppingDistance;
        public float HeightOffset => heightOffset;
        public float AgentRadius => agentRadius;
        public float AgentHeight => agentHeight;
        public float SelectedScaleMultiplier => selectedScaleMultiplier;
        
        public Color IdleColor => idleColor;
        public Color MovingColor => movingColor;
        public Color SelectedColor => selectedColor;
        public Color WaitingColor => waitingColor;
        public Color ErrorColor => errorColor;
        
        /// <summary>
        /// Read-only access to all registered agents.
        /// </summary>
        public IReadOnlyCollection<NPCAgent> AllAgents => _agents;
        
        /// <summary>
        /// Current number of registered agents.
        /// </summary>
        public int AgentCount => _agents.Count;
        
        /// <summary>
        /// ECS World used for agent entities.
        /// </summary>
        public World AgentWorld => _world;
        
        /// <summary>
        /// Entity Manager for the agent world.
        /// </summary>
        public EntityManager EntityManager => _entityManager;
        
        #endregion
        
        #region Events
        
        /// <summary>
        /// Fired when an agent is spawned and registered.
        /// </summary>
        public event Action<NPCAgent> AgentSpawned;
        
        /// <summary>
        /// Fired when an agent is destroyed and unregistered.
        /// </summary>
        public event Action<NPCAgent> AgentDestroyed;
        
        #endregion
        
        #region Private State
        
        private readonly HashSet<NPCAgent> _agents = new();
        
        // ECS
        private World _world;
        private EntityManager _entityManager;
        private EntityArchetype _agentArchetype;
        
        // Shared material for procedural agents
        private Material _sharedAgentMaterial;
        
        private static int _nextAgentId = 0;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning("[NPCManager] Multiple NPCManager instances detected. Destroying duplicate.");
                Destroy(gameObject);
                return;
            }
            _instance = this;
            
            // Create shared material for procedural agents
            _sharedAgentMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"))
            {
                color = idleColor
            };
            
            // Initialize ECS
            InitializeECS();
        }
        
        private void OnDestroy()
        {
            if (_sharedAgentMaterial)
            {
                Destroy(_sharedAgentMaterial);
            }
            
            if (_instance == this)
            {
                _instance = null;
            }
        }
        
        #endregion
        
        #region ECS Initialization
        
        /// <summary>
        /// Initialize ECS world and archetypes for agents.
        /// </summary>
        private void InitializeECS()
        {
            // Use the default world
            _world = World.DefaultGameObjectInjectionWorld;
            
            if (_world == null)
            {
                Debug.LogError("[NPCManager] No default ECS World found. Ensure Entities package is set up correctly.");
                return;
            }
            
            _entityManager = _world.EntityManager;
            
            // Create archetype for agents
            _agentArchetype = _entityManager.CreateArchetype(
                typeof(AgentData),
                typeof(AgentManagedData),
                typeof(AgentVisualConfig)
            );
            
            Debug.Log("[NPCManager] ECS initialized successfully.");
        }
        
        /// <summary>
        /// Creates an ECS entity for an agent with all required components.
        /// </summary>
        private Entity CreateAgentEntity(NPCAgent agent, NavMeshAgent navAgent, Renderer renderer, Vector3 scale)
        {
            if (_world == null || !_world.IsCreated)
            {
                Debug.LogError("[NPCManager] Cannot create entity - ECS World not available.");
                return Entity.Null;
            }
            
            Entity entity = _entityManager.CreateEntity(_agentArchetype);
            
            // Set AgentData
            int agentId = _nextAgentId++;
            _entityManager.SetComponentData(entity, new AgentData
            {
                AgentId = agentId,
                Status = AgentStatus.Idle,
                StatusBeforeSelection = AgentStatus.Idle,
                IsSelected = false,
                HasDestination = false,
                CurrentPosition = agent.transform.position,
                TargetPosition = agent.transform.position,
                Velocity = float3Zero,
                RemainingDistance = 0f,
                StoppingDistance = defaultStoppingDistance,
                PathStatus = PathStatus.Complete
            });
            
            // Set managed data (references to GameObjects)
            _entityManager.SetComponentData(entity, new AgentManagedData
            {
                Agent = agent,
                NavMeshAgent = navAgent,
                Renderer = renderer
            });
            
            // Set visual config
            _entityManager.SetComponentData(entity, new AgentVisualConfig
            {
                SelectedScaleMultiplier = selectedScaleMultiplier,
                BaseScaleX = scale.x,
                BaseScaleY = scale.y,
                BaseScaleZ = scale.z
            });
            
            return entity;
        }
        
        // Helper for float3 zero (avoid Unity.Mathematics dependency in this file's namespace)
        private static readonly Unity.Mathematics.float3 float3Zero = Unity.Mathematics.float3.zero;
        
        #endregion
        
        #region Agent Factory
        
        /// <summary>
        /// Spawns a new agent at the specified position on the NavMesh.
        /// Creates both a GameObject (for NavMeshAgent) and an ECS Entity (for DOTS processing).
        /// </summary>
        /// <param name="worldPosition">Target world position (will be sampled to NavMesh).</param>
        /// <returns>The spawned agent, or null if spawn failed.</returns>
        public NPCAgent SpawnAgent(Vector3 worldPosition)
        {
            // Sample NavMesh to find valid position
            if (!NavMesh.SamplePosition(worldPosition, out NavMeshHit hit, 10f, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[NPCManager] Failed to find NavMesh position near {worldPosition}");
                return null;
            }
            
            Vector3 spawnPos = hit.position + Vector3.up * heightOffset;
            
            GameObject agentGo;
            
            if (agentPrefab)
            {
                agentGo = Instantiate(agentPrefab, spawnPos, Quaternion.identity);
            }
            else
            {
                agentGo = CreateProceduralAgent(spawnPos);
            }
            
            // Ensure NPCAgent component exists
            var npcAgent = agentGo.GetComponent<NPCAgent>();
            if (!npcAgent)
            {
                npcAgent = agentGo.AddComponent<NPCAgent>();
            }
            
            // Ensure NavMeshAgent exists and configure it
            var navAgent = agentGo.GetComponent<NavMeshAgent>();
            if (!navAgent)
            {
                navAgent = agentGo.AddComponent<NavMeshAgent>();
            }
            
            ConfigureNavMeshAgent(navAgent);
            
            // Get renderer for visuals
            var agentRenderer = agentGo.GetComponent<Renderer>();
            
            // Create ECS entity
            Vector3 scale = agentGo.transform.localScale;
            Entity entity = CreateAgentEntity(npcAgent, navAgent, agentRenderer, scale);
            
            // Initialize the NPC agent with entity reference
            npcAgent.Initialize(this, navAgent, entity, _nextAgentId - 1);
            
            return npcAgent;
        }
        
        /// <summary>
        /// Spawns an agent at a specific spawn point transform.
        /// </summary>
        public NPCAgent SpawnAgentAtPoint(Transform spawnPoint)
        {
            if (!spawnPoint)
            {
                Debug.LogWarning("[NPCManager] Spawn point is null.");
                return null;
            }
            
            return SpawnAgent(spawnPoint.position);
        }
        
        /// <summary>
        /// Creates a procedural capsule agent.
        /// </summary>
        private GameObject CreateProceduralAgent(Vector3 position)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = $"NPC_Agent_{_agents.Count}";
            go.transform.position = position;
            
            // Scale capsule to match agent dimensions
            // Default capsule is 2 units tall, 1 unit diameter
            float scaleY = agentHeight / 2f;
            float scaleXZ = agentRadius * 2f;
            go.transform.localScale = new Vector3(scaleXZ, scaleY, scaleXZ);
            
            // Apply material - create instance for color changes
            var renderer = go.GetComponent<Renderer>();
            if (renderer)
            {
                renderer.material = new Material(_sharedAgentMaterial);
            }
            
            return go;
        }
        
        /// <summary>
        /// Configures a NavMeshAgent with default settings.
        /// </summary>
        private void ConfigureNavMeshAgent(NavMeshAgent navAgent)
        {
            navAgent.speed = defaultSpeed;
            navAgent.angularSpeed = defaultAngularSpeed;
            navAgent.acceleration = defaultAcceleration;
            navAgent.stoppingDistance = defaultStoppingDistance;
            navAgent.radius = agentRadius;
            navAgent.height = agentHeight;
            navAgent.baseOffset = heightOffset;
            
            // Allow traversal of all NavMesh areas (including links)
            navAgent.areaMask = NavMesh.AllAreas;
            navAgent.autoTraverseOffMeshLink = true;
        }
        
        #endregion
        
        #region Agent Registration
        
        /// <summary>
        /// Registers an agent with the manager. Called automatically by NPCAgent.
        /// </summary>
        internal void RegisterAgent(NPCAgent agent)
        {
            if (!agent) return;
            
            if (_agents.Add(agent))
            {
                AgentSpawned?.Invoke(agent);
            }
        }
        
        /// <summary>
        /// Unregisters an agent from the manager. Called automatically by NPCAgent.
        /// </summary>
        internal void UnregisterAgent(NPCAgent agent)
        {
            if (!agent) return;
            
            if (_agents.Remove(agent))
            {
                // Destroy ECS entity
                if (agent.LinkedEntity != Entity.Null && _world != null && _world.IsCreated)
                {
                    if (_entityManager.Exists(agent.LinkedEntity))
                    {
                        _entityManager.DestroyEntity(agent.LinkedEntity);
                    }
                }
                
                AgentDestroyed?.Invoke(agent);
            }
        }
        
        #endregion
        
        #region ECS Data Access
        
        /// <summary>
        /// Updates ECS AgentData when agent state changes from MonoBehaviour side.
        /// Called by NPCAgent when SetDestination, Select, etc. are called.
        /// </summary>
        internal void UpdateAgentData(Entity entity, AgentData data)
        {
            if (entity == Entity.Null || _world == null || !_world.IsCreated) return;
            if (!_entityManager.Exists(entity)) return;
            
            _entityManager.SetComponentData(entity, data);
        }
        
        /// <summary>
        /// Gets current AgentData for an entity.
        /// </summary>
        internal AgentData GetAgentData(Entity entity)
        {
            if (entity == Entity.Null || _world == null || !_world.IsCreated)
                return default;
            if (!_entityManager.Exists(entity))
                return default;
            
            return _entityManager.GetComponentData<AgentData>(entity);
        }
        
        /// <summary>
        /// Marks an agent's visuals as dirty (needs update).
        /// </summary>
        internal void MarkVisualsDirty(Entity entity)
        {
            if (entity == Entity.Null || _world == null || !_world.IsCreated) return;
            if (!_entityManager.Exists(entity)) return;
            
            if (!_entityManager.HasComponent<AgentVisualsDirty>(entity))
            {
                _entityManager.AddComponent<AgentVisualsDirty>(entity);
            }
        }
        
        #endregion
        
        #region Utility
        
        /// <summary>
        /// Gets the appropriate color for an agent status.
        /// </summary>
        public Color GetColorForStatus(AgentStatus status)
        {
            return status switch
            {
                AgentStatus.Idle => idleColor,
                AgentStatus.Moving => movingColor,
                AgentStatus.Selected => selectedColor,
                AgentStatus.Waiting => waitingColor,
                AgentStatus.Error => errorColor,
                _ => idleColor
            };
        }
        
        /// <summary>
        /// Gets the appropriate color for an agent status (NPCAgent enum version).
        /// </summary>
        public Color GetColorForStatus(NPCAgent.AgentStatus status)
        {
            return status switch
            {
                NPCAgent.AgentStatus.Idle => idleColor,
                NPCAgent.AgentStatus.Moving => movingColor,
                NPCAgent.AgentStatus.Selected => selectedColor,
                NPCAgent.AgentStatus.Waiting => waitingColor,
                NPCAgent.AgentStatus.Error => errorColor,
                _ => idleColor
            };
        }
        
        /// <summary>
        /// Destroys all agents. Useful for cleanup/reset.
        /// </summary>
        public void DestroyAllAgents()
        {
            // Copy to avoid modification during iteration
            var agentsCopy = new List<NPCAgent>(_agents);
            foreach (var agent in agentsCopy)
            {
                if (agent)
                {
                    Destroy(agent.gameObject);
                }
            }
            
            _agents.Clear();
        }
        
        #endregion
    }
}
