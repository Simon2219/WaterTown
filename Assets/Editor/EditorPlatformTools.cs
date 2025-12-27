#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using Grid;
using Platforms;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    /// Editor-only utility class for platform operations during prefab editing
    /// Provides direct access to platform sub-components when runtime dependency injection hasn't occurred
    /// Use this class from EditorAssetManager and other editor tools instead of going through
    /// GamePlatform facade methods, which require runtime initialization
    ///
    public static class EditorPlatformTools
    {
        #region Component Access
        
        
        /// Gets the PlatformSocketSystem component directly from a GamePlatform
        /// Use this in editor when the runtime dependency injection hasn't occurred
        ///
        public static PlatformSocketSystem GetSocketSystem(GamePlatform platform)
        {
            if (!platform) return null;
            platform.TryGetComponent(out PlatformSocketSystem socketSystem);
            return socketSystem;
        }
        
        
        /// Gets the PlatformRailingSystem component directly from a GamePlatform
        /// Use this in editor when the runtime dependency injection hasn't occurred
        ///
        public static PlatformRailingSystem GetRailingSystem(GamePlatform platform)
        {
            if (!platform) return null;
            platform.TryGetComponent(out PlatformRailingSystem railingSystem);
            return railingSystem;
        }


        /// Gets the PlatformEditorUtility component directly from a GamePlatform
        ///
        public static PlatformEditorUtility GetEditorUtility(GamePlatform platform)
        {
            if (!platform) return null;
            platform.TryGetComponent(out PlatformEditorUtility editorUtility);
            return editorUtility;
        }
        
        
        #endregion
        
        
        
        
        #region Socket Operations
        
        
        /// Builds sockets for a platform in editor mode
        /// Sets WorldGrid reference if available (scene mode)
        ///
        public static void BuildSockets(GamePlatform platform)
        {
            var socketSystem = GetSocketSystem(platform);
            if (!socketSystem) return;
            
            SetWorldGridReference(socketSystem);
            socketSystem.ReBuildSockets(platform.Footprint);
        }
        
        
        /// Sets the WorldGrid reference on a socket system via reflection
        /// Required for cell-to-socket mapping to work in editor
        ///
        private static void SetWorldGridReference(PlatformSocketSystem socketSystem)
        {
            var worldGrid = Object.FindFirstObjectByType<WorldGrid>();
            if (!worldGrid) return;
            
            var field = typeof(PlatformSocketSystem).GetField("_worldGrid", BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(socketSystem, worldGrid);
        }
        
        
        /// Gets the socket list from a platform in editor mode
        ///
        public static IReadOnlyDictionary<int,PlatformSocketSystem.SocketData> GetSockets(GamePlatform platform)
        {
            var socketSystem = GetSocketSystem(platform);
            return socketSystem?.PlatformSockets;
        }
        
        
        /// Gets the socket count from a platform in editor mode
        ///
        public static int GetSocketCount(GamePlatform platform)
        {
            var socketSystem = GetSocketSystem(platform);
            return socketSystem?.SocketCount ?? 0;
        }
        
        
        /// Finds nearest socket indices to a local position in editor mode
        /// Uses simple linear search - no WorldGrid required
        ///
        public static void FindNearestSocketIndicesLocal(GamePlatform platform, Vector3 localPos, int maxCount, float maxDistance, List<int> result)
        {
            result.Clear();
            var socketSystem = GetSocketSystem(platform);
            if (socketSystem == null || socketSystem.SocketCount == 0) return;
            
            Vector3 worldPos = ((Component)platform).transform.TransformPoint(localPos);
            float maxDistSqr = maxDistance * maxDistance;
            
            // Simple linear search - collect all within distance, sorted by distance
            var candidates = new List<(int idx, float distSqr)>();
            var sockets = socketSystem.PlatformSockets;
            
            for (int i = 0; i < sockets.Count; i++)
            {
                float distSqr = Vector3.SqrMagnitude(worldPos - sockets[i].WorldPos);
                if (distSqr <= maxDistSqr)
                    candidates.Add((i, distSqr));
            }
            
            candidates.Sort((a, b) => a.distSqr.CompareTo(b.distSqr));
            
            for (int i = 0; i < candidates.Count && i < maxCount; i++)
                result.Add(candidates[i].idx);
        }
        
        
        #endregion
        
        
        
        
        #region Railing Operations
        
        
        /// Ensures all child railings are registered with a platform in editor mode
        ///
        public static void EnsureChildrenRailingsRegistered(GamePlatform platform)
        {
            if (!platform) return;
            
            var railings = platform.GetComponentsInChildren<PlatformRailing>(true);
            foreach (var r in railings)
            {
                r._railingSystem = platform.GetComponent<PlatformRailingSystem>();
                if (r) r.EnsureRegistered();
            }
        }
        
        
        /// Ensures all child modules are registered with a platform in editor mode
        ///
        public static void EnsureChildrenModulesRegistered(GamePlatform platform)
        {
            if (!platform) return;
            
            var modules = platform.GetComponentsInChildren<PlatformModule>(true);
            foreach (var m in modules)
            {
                if (m) m.EnsureRegistered();
            }
        }
        
        
        #endregion
    }
}
#endif
