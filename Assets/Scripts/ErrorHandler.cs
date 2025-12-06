using System;
using UnityEngine;

namespace WaterTown.Core
{
    /// <summary>
    /// Static helper class for error handling and logging in WaterTown systems.
    /// Provides consistent error messages and context for Unity components.
    /// 
    /// Uses standard .NET exceptions with enhanced Unity-specific logging.
    /// </summary>
    public static class ErrorHandler
    {
        /// <summary>
        /// Logs an exception with proper Unity context and disables the component.
        /// Call this in Awake/OnEnable catch blocks when a component can't function.
        /// </summary>
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
        
        /// <summary>
        /// Creates an InvalidOperationException for a missing dependency.
        /// Use this when a required component/manager is not found in the scene.
        /// </summary>
        public static InvalidOperationException MissingDependency(Type dependencyType, Component source)
        {
            return new InvalidOperationException(
                $"Required dependency '{dependencyType.Name}' not found in scene. " +
                $"Ensure '{dependencyType.Name}' exists in the scene before '{source?.GetType().Name}'."
            );
        }
        
        /// <summary>
        /// Creates an InvalidOperationException for a missing dependency (by name).
        /// Use this when the dependency is not a Type (e.g., "Camera.main", "InputActionAsset").
        /// </summary>
        public static InvalidOperationException MissingDependency(string dependencyName, Component source)
        {
            return new InvalidOperationException(
                $"Required dependency '{dependencyName}' not found. " +
                $"Check '{source?.GetType().Name}' configuration in the inspector."
            );
        }
        
        /// <summary>
        /// Creates an InvalidOperationException for invalid configuration.
        /// Use this when inspector settings are invalid or action maps/actions are missing.
        /// </summary>
        public static InvalidOperationException InvalidConfiguration(string message, Component source)
        {
            return new InvalidOperationException(
                $"Invalid configuration in '{source?.GetType().Name}': {message}"
            );
        }
        
        /// <summary>
        /// Creates an InvalidOperationException for initialization failures.
        /// Use this when a system fails to initialize properly.
        /// </summary>
        public static InvalidOperationException InitializationFailed(string systemName, Component source, Exception innerException = null)
        {
            return new InvalidOperationException(
                $"Failed to initialize {systemName} in '{source?.GetType().Name}'.",
                innerException
            );
        }
    }
}
