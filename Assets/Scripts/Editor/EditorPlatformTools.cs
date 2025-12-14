#if UNITY_EDITOR
using System.Collections.Generic;
using Platforms;
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
                socketSystem.BuildSockets();
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
        
        
        /// <summary>
        /// Refreshes all socket statuses in editor mode.
        /// </summary>
        public static void RefreshSocketStatuses(GamePlatform platform)
        {
            var socketSystem = GetSocketSystem(platform);
            socketSystem?.RefreshAllSocketStatuses();
        }
        
        
        /// <summary>
        /// Finds the nearest socket index to a local position in editor mode.
        /// </summary>
        public static int FindNearestSocketIndexLocal(GamePlatform platform, Vector3 localPos)
        {
            var socketSystem = GetSocketSystem(platform);
            return socketSystem?.FindNearestSocketIndexLocal(localPos) ?? -1;
        }
        
        
        /// <summary>
        /// Finds nearest socket indices to a local position in editor mode.
        /// </summary>
        public static void FindNearestSocketIndicesLocal(GamePlatform platform, Vector3 localPos, int maxCount, float maxDistance, List<int> result)
        {
            var socketSystem = GetSocketSystem(platform);
            socketSystem?.FindNearestSocketIndicesLocal(localPos, maxCount, maxDistance, result);
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
        /// Registers a railing with a platform in editor mode.
        /// </summary>
        public static void RegisterRailing(GamePlatform platform, PlatformRailing railing)
        {
            var railingSystem = GetRailingSystem(platform);
            railingSystem?.RegisterRailing(railing);
        }
        
        
        #endregion
    }
}
#endif
