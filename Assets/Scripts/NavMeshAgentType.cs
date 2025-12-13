using System;
using UnityEngine;

/// <summary>
/// Serializable wrapper for NavMesh Agent Type selection.
/// Provides a dropdown in the Inspector showing all available agent types.
/// </summary>
[Serializable]
public struct NavMeshAgentType
{
    [SerializeField] private int agentTypeID;
    
    public int AgentTypeID => agentTypeID;
    
    public NavMeshAgentType(int id)
    {
        agentTypeID = id;
    }
    
    public static implicit operator int(NavMeshAgentType agentType) => agentType.agentTypeID;
}
