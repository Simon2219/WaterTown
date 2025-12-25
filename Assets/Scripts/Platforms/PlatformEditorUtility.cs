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
        public void Initialize(GamePlatform platform, PlatformSocketSystem socketSystem)
        {
            _platform = platform;
            _socketSystem = socketSystem;
        }
        
        
        #endregion
        
        
#if UNITY_EDITOR
        
        
        /// Editor-only method to reset connections and clean up NavMesh links with Undo support
        /// This should NEVER be called during runtime - use ResetConnections() instead
        public void EditorResetAllConnections()
        {
            if (!_platform || !_socketSystem) return;
            
            _socketSystem.ResetConnections();
        }
        
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

            if (!_socketSystem) return;
            
            var sockets = _socketSystem.PlatformSockets;
            if (sockets is not { Count: > 0 }) return;

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
                    // Show socket index and status (simpler label without edge/mark)
                    string label = $"#{i} {s.Status}";
                    UnityEditor.Handles.Label(wp + Vector3.up * 0.05f, label);
                }
            }
        }

        public Vector2Int Editor_GetFootprint()
        {
            // Fallback to GetComponent (editor mode, or if called before SetDependencies)
            var gp = GetComponent<GamePlatform>();
            return gp ? gp.Footprint : Vector2Int.one;
        }
        
        
#endif
    }
}

