#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

[CustomPropertyDrawer(typeof(NavMeshAgentType))]
public class NavMeshAgentTypeDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);
        
        var agentTypeIDProperty = property.FindPropertyRelative("agentTypeID");
        
        // Get all agent type names and IDs
        int count = NavMesh.GetSettingsCount();
        string[] agentTypeNames = new string[count];
        int[] agentTypeIDs = new int[count];
        
        int currentIndex = 0;
        int currentID = agentTypeIDProperty.intValue;
        
        for (int i = 0; i < count; i++)
        {
            var settings = NavMesh.GetSettingsByIndex(i);
            agentTypeIDs[i] = settings.agentTypeID;
            agentTypeNames[i] = NavMesh.GetSettingsNameFromID(settings.agentTypeID);
            
            if (settings.agentTypeID == currentID)
            {
                currentIndex = i;
            }
        }
        
        // Show dropdown
        EditorGUI.BeginChangeCheck();
        int newIndex = EditorGUI.Popup(position, label.text, currentIndex, agentTypeNames);
        
        if (EditorGUI.EndChangeCheck())
        {
            agentTypeIDProperty.intValue = agentTypeIDs[newIndex];
        }
        
        EditorGUI.EndProperty();
    }
}
#endif
