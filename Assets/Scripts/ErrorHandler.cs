using System;
using UnityEngine;


/// Static helper class for error handling and logging in WaterTown systems.
/// Provides consistent error messages and context for Unity components.
/// 
/// Uses standard .NET exceptions with enhanced Unity-specific logging.
public static class ErrorHandler
{
    
    /// Logs an exception with proper Unity context and disables the component.
    /// Call this in Awake/OnEnable catch blocks when a component can't function.
    public static void LogAndDisable(Exception exception, MonoBehaviour component)
    {
        if (component != null)
        {
            Debug.LogError($"[{component.GetType().Name}] {exception.Message}", component);
            component.enabled = false;
        }
        else
        {
            Debug.LogError($"[ErrorHandler] {exception.Message}");
        }
    }
    

    /// Creates an MissingReferenceException for a missing dependency.
    /// Use this when a required component/manager is not found in the scene.
    public static MissingReferenceException MissingDependency(Type dependencyType, Component source)
    {
        return new MissingReferenceException(
            $"Required dependency '{dependencyType.Name}' not found in scene. " +
            $"Ensure '{dependencyType.Name}' exists in the scene before '{source?.GetType().Name}'."
        );
    }
    
    /// <summary>
    /// Creates an MissingReferenceException for a missing dependency (by name).
    /// Use this when the dependency is not a Type (e.g., "Camera.main", "InputActionAsset").
    /// </summary>
    public static MissingReferenceException MissingDependency(string dependencyName, Component source)
    {
        return new MissingReferenceException(
            $"Required dependency '{dependencyName}' not found. " +
            $"Check '{source?.GetType().Name}' configuration in the inspector."
        );
    }
    
    /// <summary>
    /// Creates an MissingReferenceException for invalid configuration.
    /// Use this when inspector settings are invalid or action maps/actions are missing.
    /// </summary>
    public static MissingReferenceException InvalidConfiguration(string message, Component source)
    {
        return new MissingReferenceException(
            $"Invalid configuration in '{source?.GetType().Name}': {message}"
        );
    }
    
    /// <summary>
    /// Creates an MissingReferenceException for initialization failures.
    /// Use this when a system fails to initialize properly.
    /// </summary>
    public static MissingReferenceException InitializationFailed(string systemName, Component source, Exception innerException = null)
    {
        return new MissingReferenceException(
            $"Failed to initialize {systemName} in '{source?.GetType().Name}'.",
            innerException
        );
    }


    /// 
    ///
    public static ArgumentOutOfRangeException ArgumentOutOfRange(string parameterName, object actualValue, string message)
    {
        return new ArgumentOutOfRangeException(parameterName, actualValue, message);
    }



    public static void LogWarning(Component source, string message)
    {
        Debug.LogWarning($"[{source.GetType().Name}] - {message}");
    }
}
