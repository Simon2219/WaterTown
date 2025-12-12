using UnityEngine;

namespace Platforms
{
    /// <summary>
    /// Editor-only utilities for GamePlatform
    /// Contains gizmos, editor reset methods, and visualization settings
    /// </summary>
    [DisallowMultipleComponent]
    public class PlatformEditorUtility : MonoBehaviour
    {
        #region Dependencies
        
        
        private GamePlatform _platform;
        private PlatformSocketSystem _socketSystem;
        
        
        #endregion
        
        
        
        
        #region Gizmo Settings
        
        
        public static class GizmoSettings
        {
            public static bool ShowGizmos = true;
            public static bool ShowIndices = true;
            public static float SocketSphereRadius = 0.06f;

            public static Color ColorFree = new(0.20f, 1.00f, 0.20f, 0.90f);
            public static Color ColorOccupied = new(1.00f, 0.60f, 0.20f, 0.90f);
            public static Color ColorLocked = new(0.95f, 0.25f, 0.25f, 0.90f);
            public static Color ColorDisabled = new(0.60f, 0.60f, 0.60f, 0.90f);
            public static Color ColorConnected = new(0.20f, 0.65f, 1.00f, 0.90f);

            public static bool ShowDirections = false;
            public static float DirectionLength = 0.28f;

            public static void SetVisibility(bool visible) => ShowGizmos = visible;
            public static void SetShowIndices(bool show) => ShowIndices = show;

            public static void SetColors(Color free, Color occupied, Color locked, Color disabled)
            {
                ColorFree = free;
                ColorOccupied = occupied;
                ColorLocked = locked;
                ColorDisabled = disabled;
            }

            public static void SetColorsAll(Color free, Color occupied, Color locked, Color disabled, Color connected)
            {
                SetColors(free, occupied, locked, disabled);
                ColorConnected = connected;
            }
        }
        
        
        #endregion
        
        
        
        
        #region Initialization
        
        
        /// Called by GamePlatform to inject dependencies
        public void SetDependencies(GamePlatform platform, PlatformSocketSystem socketSystem)
        {
            _platform = platform;
            _socketSystem = socketSystem;
        }
        
        
        #endregion
        
        
        
        
#if UNITY_EDITOR
        
        #region Editor Methods
        
        
        /// Editor-only method to reset connections and clean up NavMesh links with Undo support
        /// This should NEVER be called during runtime - use ResetConnections() instead
        public void EditorResetAllConnections()
        {
            if (!_platform || !_socketSystem) return;
            
            _socketSystem.ResetConnections();

            // Destroy all NavMeshLink GameObjects under "Links" in the editor with Undo
            var linksParent = _platform.LinksParentTransform ?? transform.Find("Links");
            if (linksParent)
            {
                for (int i = linksParent.childCount - 1; i >= 0; i--)
                    UnityEditor.Undo.DestroyObjectImmediate(linksParent.GetChild(i).gameObject);
            }
            
            // Only queue rebuild if we're active (skip during shutdown)
            if (gameObject.activeInHierarchy)
                _platform.QueueRebuild();
        }
        
        
        #endregion
        
        
        
        
        #region Gizmos
        
        
        private void OnDrawGizmosSelected()
        {
            if (!GizmoSettings.ShowGizmos) return;
            if (!_platform) _platform = GetComponent<GamePlatform>();
            if (!_socketSystem) _socketSystem = GetComponent<PlatformSocketSystem>();
            if (!_platform) return;

            var footprintSize = _platform.Footprint;

            // Platform footprint outline
            Gizmos.color = new Color(0f, 0.6f, 1f, 0.25f);
            float hx = footprintSize.x * 0.5f;
            float hz = footprintSize.y * 0.5f;
            var p = transform.position; var r = transform.right; var f = transform.forward;
            Vector3 a = p + (-r * hx) + (-f * hz);
            Vector3 b = p + (r * hx) + (-f * hz);
            Vector3 c = p + (r * hx) + (f * hz);
            Vector3 d = p + (-r * hx) + (f * hz);
            Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c); Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);

            if (_socketSystem == null) return;
            
            var sockets = _socketSystem.Sockets;
            if (sockets == null || sockets.Count == 0) return;

            int footprintWidth = Mathf.Max(1, footprintSize.x);
            int footprintLength = Mathf.Max(1, footprintSize.y);

            for (int i = 0; i < sockets.Count; i++)
            {
                var s = sockets[i];
                Color col = s.Status switch
                {
                    PlatformSocketSystem.SocketStatus.Linkable   => GizmoSettings.ColorFree,
                    PlatformSocketSystem.SocketStatus.Occupied   => GizmoSettings.ColorOccupied,
                    PlatformSocketSystem.SocketStatus.Connected  => GizmoSettings.ColorConnected,
                    PlatformSocketSystem.SocketStatus.Locked     => GizmoSettings.ColorLocked,
                    PlatformSocketSystem.SocketStatus.Disabled   => GizmoSettings.ColorDisabled,
                    _                                            => Color.white
                };
                Gizmos.color = col;

                Vector3 wp = transform.TransformPoint(s.LocalPos);
                Gizmos.DrawSphere(wp, GizmoSettings.SocketSphereRadius);

                if (GizmoSettings.ShowIndices)
                {
                    // Reconstruct pseudo edge+mark only for labeling (for debug)
                    PlatformSocketSystem.Edge edge;
                    int mark;
                    if (i < footprintWidth)                { edge = PlatformSocketSystem.Edge.North; mark = i; }
                    else if (i < 2 * footprintWidth)       { edge = PlatformSocketSystem.Edge.South; mark = i - footprintWidth; }
                    else if (i < 2 * footprintWidth + footprintLength)   { edge = PlatformSocketSystem.Edge.East;  mark = i - 2 * footprintWidth; }
                    else                      { edge = PlatformSocketSystem.Edge.West;  mark = i - (2 * footprintWidth + footprintLength); }

                    string label = $"#{i} [{edge}:{mark}] {s.Status}";
                    UnityEditor.Handles.Label(wp + Vector3.up * 0.05f, label);
                }
            }
        }
        
        
        #endregion
        
        
#endif
    }
}

