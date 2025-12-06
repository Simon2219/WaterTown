#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using WaterTown.Platforms;
using WaterTown.Town;

namespace Editor
{
    [InitializeOnLoad]
    public static class PlatformSceneLinkTester
    {
        static PlatformSceneLinkTester()
        {
            EditorApplication.update += OnUpdate;
            ObjectChangeEvents.changesPublished += OnChanges;
        }

        static void OnChanges(ref ObjectChangeEventStream stream)
        {
            // just force re-evaluation next update
        }

        static void OnUpdate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            var all = Object.FindObjectsByType<GamePlatform>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (all == null || all.Length == 0) return;

            // Ensure registration is always valid in Scene View
            foreach (var p in all)
            {
                p.EnsureChildrenModulesRegistered();
                p.EnsureChildrenRailingsRegistered();
            }

            // Reset everything first so rails reappear when platforms separate
            foreach (var p in all)
                p.EditorResetAllConnections();

            // Try to use TownManager's grid-based adjacency checking if available
            var townManager = Object.FindFirstObjectByType<TownManager>();
            
            if (townManager != null)
            {
                // Use grid-based adjacency checking
                for (int i = 0; i < all.Length; i++)
                {
                    var a = all[i];
                    for (int j = i + 1; j < all.Length; j++)
                    {
                        var b = all[j];
                        townManager.ConnectPlatformsIfAdjacent(a, b);
                    }
                }
            }
            else
            {
                // Fallback to old distance-based method (deprecated)
                #pragma warning disable 0618
                for (int i = 0; i < all.Length; i++)
                {
                    var a = all[i];
                    for (int j = i + 1; j < all.Length; j++)
                    {
                        var b = all[j];
                        GamePlatform.ConnectIfAdjacent(a, b);
                    }
                }
                #pragma warning restore 0618
            }
        }
    }
}
#endif
