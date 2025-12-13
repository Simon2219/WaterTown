using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Agents.Systems
{
    #region NavMesh Read System
    
    /// <summary>
    /// Reads NavMeshAgent state into ECS AgentData.
    /// Runs before status processing.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(AgentStatusUpdateSystem))]
    public partial class AgentNavMeshReadSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            Entities
                .WithoutBurst()
                .ForEach((Entity entity, ref AgentData agentData, in AgentManagedData managed) =>
                {
                    if (managed.NavMeshAgent == null) return;

                    var nav = managed.NavMeshAgent;
                    
                    agentData.CurrentPosition = nav.transform.position;
                    agentData.Velocity = nav.velocity;
                    agentData.RemainingDistance = nav.remainingDistance;
                    agentData.StoppingDistance = nav.stoppingDistance;
                    
                    agentData.PathStatus = nav.pathPending 
                        ? PathStatus.Pending 
                        : nav.pathStatus switch
                        {
                            UnityEngine.AI.NavMeshPathStatus.PathComplete => PathStatus.Complete,
                            UnityEngine.AI.NavMeshPathStatus.PathPartial => PathStatus.Partial,
                            UnityEngine.AI.NavMeshPathStatus.PathInvalid => PathStatus.Invalid,
                            _ => PathStatus.Invalid
                        };
                    
                    if (nav.hasPath)
                    {
                        agentData.TargetPosition = nav.destination;
                    }
                })
                .Run();
        }
    }
    
    #endregion

    #region Status Update System (Burst + Parallel)
    
    /// <summary>
    /// Burst-compiled system that updates agent status based on movement state.
    /// Runs on all agents in parallel using IJobEntity.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AgentStatusUpdateSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<AgentData>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.TempJob);
            
            new UpdateStatusJob
            {
                ECB = ecb.AsParallelWriter()
            }.ScheduleParallel();
            
            state.Dependency.Complete();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state) { }
    }

    /// <summary>
    /// Burst-compiled job for parallel status updates.
    /// </summary>
    [BurstCompile]
    public partial struct UpdateStatusJob : IJobEntity
    {
        public EntityCommandBuffer.ParallelWriter ECB;

        [BurstCompile]
        private void Execute(Entity entity, [ChunkIndexInQuery] int sortKey, ref AgentData agentData)
        {
            if (agentData.IsSelected) return;

            AgentStatus previousStatus = agentData.Status;
            AgentStatus newStatus;

            if (agentData.PathStatus == PathStatus.Invalid)
            {
                newStatus = AgentStatus.Error;
            }
            else if (agentData.HasDestination)
            {
                if (agentData.PathStatus == PathStatus.Pending)
                {
                    newStatus = AgentStatus.Waiting;
                }
                else if (agentData.RemainingDistance <= agentData.StoppingDistance)
                {
                    agentData.HasDestination = false;
                    newStatus = AgentStatus.Idle;
                }
                else if (math.lengthsq(agentData.Velocity) > 0.01f)
                {
                    newStatus = AgentStatus.Moving;
                }
                else
                {
                    newStatus = AgentStatus.Waiting;
                }
            }
            else
            {
                newStatus = AgentStatus.Idle;
            }

            if (newStatus != previousStatus)
            {
                agentData.Status = newStatus;
                ECB.AddComponent<AgentVisualsDirty>(sortKey, entity);
            }
        }
    }
    
    #endregion

    #region Sync & Visuals System
    
    /// <summary>
    /// Syncs ECS state to MonoBehaviour and updates visuals.
    /// Processes only agents marked with AgentVisualsDirty.
    /// </summary>
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class AgentSyncAndVisualsSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var ecbSingleton = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>();
            var ecb = ecbSingleton.CreateCommandBuffer(World.Unmanaged);
            var manager = NPCManager.Instance;

            // Sync all agents to MonoBehaviour (for events)
            Entities
                .WithoutBurst()
                .ForEach((in AgentData agentData, in AgentManagedData managed) =>
                {
                    if (managed.Agent != null)
                    {
                        managed.Agent.SyncFromECS(agentData);
                    }
                })
                .Run();

            // Update visuals only for dirty agents
            Entities
                .WithoutBurst()
                .WithAll<AgentVisualsDirty>()
                .ForEach((Entity entity, in AgentData agentData, in AgentManagedData managed, in AgentVisualConfig config) =>
                {
                    if (managed.Agent == null || manager == null) return;

                    // Update color
                    if (managed.Renderer != null && managed.Renderer.material != null)
                    {
                        Color color = agentData.Status switch
                        {
                            AgentStatus.Idle => manager.IdleColor,
                            AgentStatus.Moving => manager.MovingColor,
                            AgentStatus.Selected => manager.SelectedColor,
                            AgentStatus.Waiting => manager.WaitingColor,
                            AgentStatus.Error => manager.ErrorColor,
                            _ => manager.IdleColor
                        };
                        managed.Renderer.material.color = color;
                    }

                    // Update scale
                    var baseScale = new Vector3(config.BaseScaleX, config.BaseScaleY, config.BaseScaleZ);
                    managed.Agent.transform.localScale = agentData.IsSelected 
                        ? baseScale * config.SelectedScaleMultiplier 
                        : baseScale;

                    ecb.RemoveComponent<AgentVisualsDirty>(entity);
                })
                .Run();
        }
    }
    
    #endregion
}
