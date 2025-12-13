using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Agents
{
    /// <summary>
    /// Central manager for all NPC agents.
    /// Handles spawning, registration, and performance optimization via LOD and culling.
    /// Designed for 500+ agents with batched updates.
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
        
        #region Configuration - Agent Settings
        
        [Header("Agent Prefab Settings")]
        [Tooltip("If null, agents are created procedurally as capsules.")]
        [SerializeField] private GameObject agentPrefab;
        
        [Header("Default Agent Settings")]
        [SerializeField] private float defaultSpeed = 3.5f;
        [SerializeField] private float defaultAngularSpeed = 120f;
        [SerializeField] private float defaultAcceleration = 8f;
        [SerializeField] private float defaultStoppingDistance = 0.1f;
        [SerializeField] private float heightOffset = 0.05f;
        [SerializeField] private float agentRadius = 0.3f;
        [SerializeField] private float agentHeight = 1.8f;
        
        [Header("Status Colors")]
        [SerializeField] private Color idleColor = new Color(0.3f, 0.7f, 0.3f, 1f);
        [SerializeField] private Color movingColor = new Color(0.3f, 0.5f, 0.9f, 1f);
        [SerializeField] private Color selectedColor = new Color(1f, 0.9f, 0.2f, 1f);
        [SerializeField] private Color waitingColor = new Color(0.9f, 0.6f, 0.2f, 1f);
        [SerializeField] private Color errorColor = new Color(0.9f, 0.2f, 0.2f, 1f);
        
        [Header("Selection Settings")]
        [SerializeField] private float selectedScaleMultiplier = 1.1f;
        
        #endregion
        
        #region Configuration - LOD & Performance
        
        [Header("LOD Settings")]
        [Tooltip("Distance for High LOD (full updates every frame).")]
        [SerializeField] private float lodHighDistance = 20f;
        
        [Tooltip("Distance for Medium LOD (updates every 3 frames).")]
        [SerializeField] private float lodMediumDistance = 50f;
        
        [Tooltip("Distance for Low LOD (updates every 5 frames).")]
        [SerializeField] private float lodLowDistance = 100f;
        
        [Tooltip("Beyond this distance, visuals are disabled.")]
        [SerializeField] private float cullDistance = 150f;
        
        [Tooltip("How often to recalculate LOD levels (seconds).")]
        [SerializeField] private float lodUpdateInterval = 0.5f;
        
        [Header("Performance Settings")]
        [Tooltip("Maximum agents to process per frame for LOD updates.")]
        [SerializeField] private int maxLODUpdatesPerFrame = 50;
        
        [Tooltip("Enable LOD system.")]
        [SerializeField] private bool enableLOD = true;
        
        [Tooltip("Enable visual culling at distance.")]
        [SerializeField] private bool enableCulling = true;
        
        [Header("Debug")]
        [SerializeField] private bool showPerformanceStats;
        
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
        
        /// <summary>All registered agents.</summary>
        public IReadOnlyList<NPCAgent> AllAgents => _agentsList;
        
        /// <summary>Number of agents.</summary>
        public int AgentCount => _agentsList.Count;
        
        /// <summary>Camera used for LOD calculations.</summary>
        public Camera LODCamera => _lodCamera;
        
        #endregion
        
        #region Performance Stats (Public)
        
        public int AgentsHighLOD { get; private set; }
        public int AgentsMediumLOD { get; private set; }
        public int AgentsLowLOD { get; private set; }
        public int AgentsCulled { get; private set; }
        public int AgentsUpdatedThisFrame { get; private set; }
        
        #endregion
        
        #region Events
        
        /// <summary>Fired when agent is spawned.</summary>
        public event Action<NPCAgent> AgentSpawned;
        
        /// <summary>Fired when agent is destroyed.</summary>
        public event Action<NPCAgent> AgentDestroyed;
        
        #endregion
        
        #region Private State
        
        // Agent storage
        private readonly HashSet<NPCAgent> _agentsSet = new();
        private readonly List<NPCAgent> _agentsList = new();
        
        // LOD buckets
        private readonly List<NPCAgent> _highLODAgents = new();
        private readonly List<NPCAgent> _mediumLODAgents = new();
        private readonly List<NPCAgent> _lowLODAgents = new();
        private readonly List<NPCAgent> _culledAgents = new();
        
        // LOD update tracking
        private Camera _lodCamera;
        private float _lastLODUpdateTime;
        private int _lodUpdateIndex;
        
        // Shared material
        private Material _sharedAgentMaterial;
        
        // Frame tracking
        private int _frameCount;
        
        // ID generation
        private static int _nextAgentId;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning("[NPCManager] Multiple NPCManager instances. Destroying duplicate.");
                Destroy(gameObject);
                return;
            }
            _instance = this;
            
            _sharedAgentMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"))
            {
                color = idleColor
            };
            
            _lodCamera = Camera.main;
        }
        
        private void Update()
        {
            _frameCount++;
            
            // Update LOD periodically
            if (enableLOD && Time.time - _lastLODUpdateTime >= lodUpdateInterval)
            {
                UpdateLODLevels();
                _lastLODUpdateTime = Time.time;
            }
            
            // Process agents by LOD tier
            ProcessAgentUpdates();
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
        
        #region LOD System
        
        /// <summary>
        /// Recalculates LOD levels for agents based on camera distance.
        /// Processes in batches to distribute load.
        /// </summary>
        private void UpdateLODLevels()
        {
            if (!_lodCamera)
            {
                _lodCamera = Camera.main;
                if (!_lodCamera) return;
            }
            
            Vector3 cameraPos = _lodCamera.transform.position;
            int agentCount = _agentsList.Count;
            
            if (agentCount == 0) return;
            
            // Process a batch of agents
            int processed = 0;
            int startIndex = _lodUpdateIndex;
            
            while (processed < maxLODUpdatesPerFrame && processed < agentCount)
            {
                int index = (_lodUpdateIndex + processed) % agentCount;
                var agent = _agentsList[index];
                
                if (agent)
                {
                    UpdateAgentLOD(agent, cameraPos);
                }
                
                processed++;
            }
            
            _lodUpdateIndex = (startIndex + processed) % Mathf.Max(1, agentCount);
            
            // Update stats
            UpdateLODStats();
        }
        
        private void UpdateAgentLOD(NPCAgent agent, Vector3 cameraPos)
        {
            float distance = Vector3.Distance(agent.transform.position, cameraPos);
            agent.CameraDistance = distance;
            
            AgentLODLevel newLevel;
            
            if (!enableLOD)
            {
                newLevel = AgentLODLevel.High;
            }
            else if (distance <= lodHighDistance)
            {
                newLevel = AgentLODLevel.High;
            }
            else if (distance <= lodMediumDistance)
            {
                newLevel = AgentLODLevel.Medium;
            }
            else if (distance <= lodLowDistance)
            {
                newLevel = AgentLODLevel.Low;
            }
            else if (enableCulling && distance > cullDistance)
            {
                newLevel = AgentLODLevel.Culled;
            }
            else
            {
                newLevel = AgentLODLevel.Low;
            }
            
            // Move between buckets if LOD changed
            if (agent.LODLevel != newLevel)
            {
                RemoveFromLODBucket(agent);
                agent.SetLODLevel(newLevel);
                AddToLODBucket(agent);
            }
        }
        
        private void AddToLODBucket(NPCAgent agent)
        {
            switch (agent.LODLevel)
            {
                case AgentLODLevel.High:
                    _highLODAgents.Add(agent);
                    break;
                case AgentLODLevel.Medium:
                    _mediumLODAgents.Add(agent);
                    break;
                case AgentLODLevel.Low:
                    _lowLODAgents.Add(agent);
                    break;
                case AgentLODLevel.Culled:
                    _culledAgents.Add(agent);
                    break;
            }
        }
        
        private void RemoveFromLODBucket(NPCAgent agent)
        {
            switch (agent.LODLevel)
            {
                case AgentLODLevel.High:
                    _highLODAgents.Remove(agent);
                    break;
                case AgentLODLevel.Medium:
                    _mediumLODAgents.Remove(agent);
                    break;
                case AgentLODLevel.Low:
                    _lowLODAgents.Remove(agent);
                    break;
                case AgentLODLevel.Culled:
                    _culledAgents.Remove(agent);
                    break;
            }
        }
        
        private void UpdateLODStats()
        {
            AgentsHighLOD = _highLODAgents.Count;
            AgentsMediumLOD = _mediumLODAgents.Count;
            AgentsLowLOD = _lowLODAgents.Count;
            AgentsCulled = _culledAgents.Count;
        }
        
        #endregion
        
        #region Agent Updates
        
        /// <summary>
        /// Process agent updates based on LOD tier.
        /// </summary>
        private void ProcessAgentUpdates()
        {
            int updatedCount = 0;
            
            // High LOD - update every frame
            for (int i = 0; i < _highLODAgents.Count; i++)
            {
                var agent = _highLODAgents[i];
                if (agent)
                {
                    agent.UpdateFull();
                    updatedCount++;
                }
            }
            
            // Medium LOD - update every 3 frames (staggered)
            for (int i = 0; i < _mediumLODAgents.Count; i++)
            {
                var agent = _mediumLODAgents[i];
                if (agent && agent.ShouldUpdateThisFrame(_frameCount))
                {
                    agent.UpdateReduced();
                    updatedCount++;
                }
            }
            
            // Low LOD - update every 5 frames (staggered)
            for (int i = 0; i < _lowLODAgents.Count; i++)
            {
                var agent = _lowLODAgents[i];
                if (agent && agent.ShouldUpdateThisFrame(_frameCount))
                {
                    agent.UpdateReduced();
                    updatedCount++;
                }
            }
            
            // Culled - minimal updates every 15 frames
            for (int i = 0; i < _culledAgents.Count; i++)
            {
                var agent = _culledAgents[i];
                if (agent && agent.ShouldUpdateThisFrame(_frameCount))
                {
                    agent.UpdateMinimal();
                    updatedCount++;
                }
            }
            
            AgentsUpdatedThisFrame = updatedCount;
        }
        
        #endregion
        
        #region Agent Factory
        
        /// <summary>
        /// Spawn an agent at the specified NavMesh position.
        /// </summary>
        public NPCAgent SpawnAgent(Vector3 worldPosition)
        {
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
            
            var npcAgent = agentGo.GetComponent<NPCAgent>();
            if (!npcAgent)
            {
                npcAgent = agentGo.AddComponent<NPCAgent>();
            }
            
            var navAgent = agentGo.GetComponent<NavMeshAgent>();
            if (!navAgent)
            {
                navAgent = agentGo.AddComponent<NavMeshAgent>();
            }
            
            ConfigureNavMeshAgent(navAgent);
            
            int agentId = _nextAgentId++;
            npcAgent.Initialize(this, agentId);
            
            return npcAgent;
        }
        
        /// <summary>
        /// Spawn an agent at a transform's position.
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
        
        private GameObject CreateProceduralAgent(Vector3 position)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = $"NPC_Agent_{_agentsList.Count}";
            go.transform.position = position;
            
            float scaleY = agentHeight / 2f;
            float scaleXZ = agentRadius * 2f;
            go.transform.localScale = new Vector3(scaleXZ, scaleY, scaleXZ);
            
            var renderer = go.GetComponent<Renderer>();
            if (renderer)
            {
                renderer.material = new Material(_sharedAgentMaterial);
            }
            
            return go;
        }
        
        private void ConfigureNavMeshAgent(NavMeshAgent navAgent)
        {
            navAgent.speed = defaultSpeed;
            navAgent.angularSpeed = defaultAngularSpeed;
            navAgent.acceleration = defaultAcceleration;
            navAgent.stoppingDistance = defaultStoppingDistance;
            navAgent.radius = agentRadius;
            navAgent.height = agentHeight;
            navAgent.baseOffset = heightOffset;
            navAgent.areaMask = NavMesh.AllAreas;
            navAgent.autoTraverseOffMeshLink = true;
        }
        
        #endregion
        
        #region Agent Registration
        
        /// <summary>
        /// Register an agent with the manager.
        /// </summary>
        internal void RegisterAgent(NPCAgent agent)
        {
            if (!agent) return;
            
            if (_agentsSet.Add(agent))
            {
                _agentsList.Add(agent);
                _highLODAgents.Add(agent); // Start in high LOD
                AgentSpawned?.Invoke(agent);
            }
        }
        
        /// <summary>
        /// Unregister an agent from the manager.
        /// </summary>
        internal void UnregisterAgent(NPCAgent agent)
        {
            if (!agent) return;
            
            if (_agentsSet.Remove(agent))
            {
                _agentsList.Remove(agent);
                RemoveFromLODBucket(agent);
                AgentDestroyed?.Invoke(agent);
            }
        }
        
        #endregion
        
        #region Utility
        
        /// <summary>
        /// Get color for a status.
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
        /// Set the camera used for LOD calculations.
        /// </summary>
        public void SetLODCamera(Camera camera)
        {
            _lodCamera = camera;
        }
        
        /// <summary>
        /// Force immediate LOD update for all agents.
        /// </summary>
        public void ForceUpdateAllLOD()
        {
            if (!_lodCamera) return;
            
            Vector3 cameraPos = _lodCamera.transform.position;
            foreach (var agent in _agentsList)
            {
                if (agent)
                {
                    UpdateAgentLOD(agent, cameraPos);
                }
            }
            UpdateLODStats();
        }
        
        /// <summary>
        /// Enable/disable all agent visuals (for pause screens, etc.).
        /// </summary>
        public void SetAllVisualsEnabled(bool enabled)
        {
            foreach (var agent in _agentsList)
            {
                if (agent)
                {
                    agent.SetVisualsEnabled(enabled);
                }
            }
        }
        
        /// <summary>
        /// Destroy all agents.
        /// </summary>
        public void DestroyAllAgents()
        {
            var agentsCopy = new List<NPCAgent>(_agentsList);
            foreach (var agent in agentsCopy)
            {
                if (agent)
                {
                    Destroy(agent.gameObject);
                }
            }
            
            _agentsList.Clear();
            _agentsSet.Clear();
            _highLODAgents.Clear();
            _mediumLODAgents.Clear();
            _lowLODAgents.Clear();
            _culledAgents.Clear();
        }
        
        /// <summary>
        /// Get agent by ID.
        /// </summary>
        public NPCAgent GetAgentById(int agentId)
        {
            foreach (var agent in _agentsList)
            {
                if (agent && agent.AgentId == agentId)
                {
                    return agent;
                }
            }
            return null;
        }
        
        /// <summary>
        /// Get all agents within a radius.
        /// </summary>
        public List<NPCAgent> GetAgentsInRadius(Vector3 center, float radius)
        {
            var result = new List<NPCAgent>();
            float radiusSqr = radius * radius;
            
            foreach (var agent in _agentsList)
            {
                if (agent)
                {
                    float distSqr = (agent.transform.position - center).sqrMagnitude;
                    if (distSqr <= radiusSqr)
                    {
                        result.Add(agent);
                    }
                }
            }
            
            return result;
        }
        
        #endregion
        
        #region Debug GUI
        
        private void OnGUI()
        {
            if (!showPerformanceStats) return;
            
            GUILayout.BeginArea(new Rect(10, 10, 250, 200));
            GUILayout.BeginVertical("box");
            
            GUILayout.Label($"<b>NPC Performance Stats</b>");
            GUILayout.Label($"Total Agents: {AgentCount}");
            GUILayout.Label($"Updated This Frame: {AgentsUpdatedThisFrame}");
            GUILayout.Space(5);
            GUILayout.Label($"High LOD: {AgentsHighLOD}");
            GUILayout.Label($"Medium LOD: {AgentsMediumLOD}");
            GUILayout.Label($"Low LOD: {AgentsLowLOD}");
            GUILayout.Label($"Culled: {AgentsCulled}");
            
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
        
        #endregion
    }
}
