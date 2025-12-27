#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Platforms;

namespace Editor
{
    /// <summary>
    /// Editor-only utility that maintains platform connections and railing visibility in Scene View.
    /// Runs outside of play mode to provide visual feedback during level design.
    /// </summary>
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

            // Ensure registration is always valid in Scene View (use editor tools for proper editor-mode access)
            foreach (var p in all)
            {
                EditorPlatformTools.EnsureChildrenModulesRegistered(p);
                EditorPlatformTools.EnsureChildrenRailingsRegistered(p);
            }
            
            // Reset everything first so rails reappear when platforms separate
            foreach (var p in all)
                p.GetComponent<PlatformEditorUtility>().EditorResetAllConnections();

            // Try to use PlatformManager's grid-based adjacency checking if available
            var platformManager = Object.FindFirstObjectByType<PlatformManager>();
            
            if (platformManager != null)
            {
                // Use grid-based adjacency checking
                for (int i = 0; i < all.Length; i++)
                {
                    var a = all[i];
                    for (int j = i + 1; j < all.Length; j++)
                    {
                        var b = all[j];
                        ConnectPlatformsIfAdjacent(a, b);
                    }
                }
            }
        }
        
        ///
        /// Public method for checking if two platforms are adjacent and connecting them
        /// Used by editor tools (SceneLinkTester)
        /// Each platform updates its own sockets - connections are determined automatically
        ///
        private static void ConnectPlatformsIfAdjacent(GamePlatform platformA, GamePlatform platformB)
        {
            if (!platformA || !platformB) return;

            var platformManager = Object.FindFirstObjectByType<PlatformManager>();
            
            // Ensure platforms are registered with their grid cells
            if (platformManager.AllPlatforms.Contains(platformA))
            {
                List<Vector2Int> cells = platformManager.GetCellsForPlatform(platformA);
                if (cells.Count > 0)
                {
                    platformA.occupiedCells = cells;
                    platformManager.RegisterPlatform(platformA);
                }
                else return;
            }

            if (!platformManager.AllPlatforms.Contains(platformB))
            {
                List<Vector2Int> cells = platformManager.GetCellsForPlatform(platformB);
                if (cells.Count > 0)
                {
                    platformB.occupiedCells = cells;
                    platformManager.RegisterPlatform(platformB);
                }
                else return;
            }

            // Each platform updates its own sockets
            platformA.RefreshSocketStatuses();
            platformB.RefreshSocketStatuses();
        }
    }
}
#endif
