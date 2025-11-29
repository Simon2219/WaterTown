// Assets/Scripts/Editor/PlatformSceneLinkTester.cs
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WaterTown.Platforms;

namespace Editor
{
    [InitializeOnLoad]
    public static class PlatformSceneLinkTester
    {
        static readonly HashSet<GamePlatform> _seen = new();
        static Vector3 _lastSnap = Vector3.zero;

        static PlatformSceneLinkTester()
        {
            EditorApplication.update += OnUpdate;
            ObjectChangeEvents.changesPublished += OnChanges;
        }

        static void OnChanges(ref ObjectChangeEventStream stream)
        {
            // Any change -> force scan next update
            _seen.Clear();
        }

        static void OnUpdate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            var all = Object.FindObjectsByType<GamePlatform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (all == null || all.Length == 0) return;

            // Always ensure registration is up to date
            foreach (var p in all) p.EnsureChildrenModulesRegistered();

            // Reset everything first so rails reappear when platforms separate
            foreach (var p in all) p.EditorResetAllConnections();

            // Try pairwise connections for currently touching platforms (corners already excluded in ConnectIfAdjacent)
            for (int i = 0; i < all.Length; i++)
            {
                var a = all[i];
                for (int j = i + 1; j < all.Length; j++)
                {
                    var b = all[j];
                    GamePlatform.ConnectIfAdjacent(a, b);
                }
            }
        }
    }
}
#endif
