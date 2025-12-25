#if UNITY_EDITOR
using System.Collections.Generic;
using Platforms;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    /// <summary>
    /// Editor-only utility class for platform operations during prefab editing.
    /// Provides direct access to platform sub-components when runtime dependency injection hasn't occurred.
    /// 
    /// Use this class from EditorAssetManager and other editor tools instead of going through
    /// GamePlatform facade methods, which require runtime initialization.
    /// </summary>
    public static class EditorPlatformTools
    {
        #region Component Access
        
        
        /// <summary>
        /// Gets the PlatformSocketSystem component directly from a GamePlatform.
        /// Use this in editor when the runtime dependency injection hasn't occurred.
        /// </summary>
        public static PlatformSocketSystem GetSocketSystem(GamePlatform platform)
        {
            if (!platform) return null;
            platform.TryGetComponent(out PlatformSocketSystem socketSystem);
            return socketSystem;
        }
        
        
        /// <summary>
        /// Gets the PlatformRailingSystem component directly from a GamePlatform.
        /// Use this in editor when the runtime dependency injection hasn't occurred.
        /// </summary>
        public static PlatformRailingSystem GetRailingSystem(GamePlatform platform)
        {
            if (!platform) return null;
            platform.TryGetComponent(out PlatformRailingSystem railingSystem);
            return railingSystem;
        }



        public static PlatformEditorUtility GetEditorUtility(GamePlatform platform)
        {
            if (!platform) return null;
            platform.TryGetComponent(out PlatformEditorUtility editorUtility);
            return editorUtility;
        }
        #endregion
        
        
        
        
        #region Socket Operations
        
        
        /// <summary>
        /// Builds sockets for a platform in editor mode.
        /// </summary>
        public static void BuildSockets(GamePlatform platform)
        {
            var socketSystem = GetSocketSystem(platform);
            if (socketSystem)
            {
                socketSystem.ReBuildSockets(platform.Footprint);
            }
        }
        
        
        /// <summary>
        /// Gets the socket list from a platform in editor mode.
        /// </summary>
        public static IReadOnlyList<PlatformSocketSystem.SocketData> GetSockets(GamePlatform platform)
        {
            var socketSystem = GetSocketSystem(platform);
            return socketSystem?.PlatformSockets;
        }
        
        
        /// <summary>
        /// Gets the socket count from a platform in editor mode.
        /// </summary>
        public static int GetSocketCount(GamePlatform platform)
        {
            var socketSystem = GetSocketSystem(platform);
            return socketSystem?.SocketCount ?? 0;
        }
        
        
        // Note: RefreshSocketStatuses is intentionally NOT provided here.
        // It requires runtime dependencies (WorldGrid, PlatformManager) that don't exist
        // in editor prefab mode. Socket statuses are calculated at runtime.
        
        
        
        /// Finds nearest socket indices to a local position in editor mode
        ///
        public static void GetNearestSocketIndicesLocal(GamePlatform platform, Vector3 localPos, int maxCount, float maxDistance, List<int> result)
        {
            var socketSystem = GetSocketSystem(platform);
            socketSystem?.GetNearestSocketIndicesLocal(localPos, maxCount, maxDistance, result);
        }
        
        
        /// Gets the next socket index in clockwise direction (with wrap-around)
        ///
        public static int GetNextSocketIndex(GamePlatform platform, int socketIndex)
        {
            var socketSystem = GetSocketSystem(platform);
            return socketSystem?.GetNextSocketIndex(socketIndex) ?? -1;
        }
        
        
        /// Gets the previous socket index in counter-clockwise direction (with wrap-around)
        ///
        public static int GetPreviousSocketIndex(GamePlatform platform, int socketIndex)
        {
            var socketSystem = GetSocketSystem(platform);
            return socketSystem?.GetPreviousSocketIndex(socketIndex) ?? -1;
        }
        
        
        #endregion
        
        
        
        
        #region Module Operations
        
        
        /// <summary>
        /// Registers a module on sockets in editor mode.
        /// </summary>
        public static void RegisterModuleOnSockets(GamePlatform platform, PlatformModule module, bool occupiesSockets, IEnumerable<int> socketIndices)
        {
            var socketSystem = GetSocketSystem(platform);
            socketSystem?.RegisterModuleOnSockets(module, occupiesSockets, socketIndices);
        }
        
        
        #endregion
        
        
        
        
        #region Railing Operations
        
        
        /// <summary>
        /// Ensures all child railings are registered with a platform in editor mode.
        /// </summary>
        public static void EnsureChildrenRailingsRegistered(GamePlatform platform)
        {
            if (!platform) return;
            
            var railings = platform.GetComponentsInChildren<PlatformRailing>(true);
            foreach (var r in railings)
            {
                if (r) r.EnsureRegistered();
            }
        }
        
        
        /// <summary>
        /// Ensures all child modules are registered with a platform in editor mode.
        /// </summary>
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
