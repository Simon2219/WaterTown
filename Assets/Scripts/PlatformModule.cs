using System.Collections.Generic;
using UnityEngine;

namespace WaterTown.Platforms
{
    [DisallowMultipleComponent]
    public class PlatformModule : MonoBehaviour
    {
        [Header("Size (meters on 1m grid)")]
        [Min(1)] public int sizeAlongMeters = 2;   // occupies exactly sizeAlongMeters marks
        [Min(1)] public int sizeInwardMeters = 1;  // reserved for future (depth), not used for sockets

        [Header("Behavior")]
        public bool isCornerModule = false; // true: occupy nearest corner socket only
        public bool blocksLink = false;     // true & active => socket becomes Occupied after Refresh

        public enum EdgeOverride { Auto, North, East, South, West }

        [Header("Attachment")]
        [Tooltip("Lock to a specific edge if pivot proximity would otherwise choose the wrong edge.")]
        public EdgeOverride attachEdge = EdgeOverride.Auto;

        [SerializeField, HideInInspector] private List<int> _boundSocketIndices = new List<int>();
        [SerializeField, HideInInspector] private bool _isHidden;

        private GamePlatform _platform;

        public IReadOnlyList<int> BoundSocketIndices => _boundSocketIndices;
        public bool IsHidden => _isHidden;

        private void Awake()
        {
            _platform = GetComponentInParent<GamePlatform>();
            if (!_platform)
                Debug.LogWarning($"[{nameof(PlatformModule)}] No GamePlatform parent for '{name}'.");
        }

        private void OnEnable()
        {
            if (IsEditingPrefab()) return;
            if (!_platform) { Awake(); if (!_platform) return; }
            RebindAndRegister();
            ApplyVisibilityImmediate();
            _platform.RefreshSocketStatuses();
        }

        private void OnDisable()
        {
            if (IsEditingPrefab()) return;
            if (_platform) { _platform.UnregisterModule(gameObject); _platform.RefreshSocketStatuses(); }
        }

        private void OnDestroy()
        {
            if (IsEditingPrefab()) return;
            if (_platform) { _platform.UnregisterModule(gameObject); _platform.RefreshSocketStatuses(); }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            sizeAlongMeters  = Mathf.Max(1, sizeAlongMeters);
            sizeInwardMeters = Mathf.Max(1, sizeInwardMeters);
            if (IsEditingPrefab()) return;

            if (_platform && isActiveAndEnabled)
            {
                _platform.UnregisterModule(gameObject);
                RebindAndRegister();
                ApplyVisibilityImmediate();
                _platform.RefreshSocketStatuses();
            }
        }
#endif

        public void EnsureRegistered()
        {
            if (IsEditingPrefab()) return;
            if (!_platform) _platform = GetComponentInParent<GamePlatform>();
            if (!_platform) return;

            _platform.UnregisterModule(gameObject);
            if (!enabled) enabled = true;

            RebindAndRegister();
            ApplyVisibilityImmediate();
            _platform.RefreshSocketStatuses();
        }

        // ---------- Binding ----------
        private void RebindAndRegister()
        {
            _boundSocketIndices = ComputeSocketIndices(_platform);
            if (_boundSocketIndices.Count > 0)
                _platform.RegisterModuleOnSockets(gameObject, occupiesSockets: true, _boundSocketIndices);
        }

        private List<int> ComputeSocketIndices(GamePlatform gp)
        {
            var result = new List<int>();
            if (!gp) return result;

            if (isCornerModule)
            {
                int nearest = NearestCornerIndex(gp);
                if (nearest >= 0) result.Add(nearest);
                return result;
            }

            // Choose edge (override or nearest)
            Vector3 lp = gp.transform.InverseTransformPoint(transform.position);
            float hx = gp.Footprint.x * 0.5f;
            float hz = gp.Footprint.y * 0.5f;

            float dN = Mathf.Abs(lp.z - (+hz));
            float dS = Mathf.Abs(lp.z - (-hz));
            float dE = Mathf.Abs(lp.x - (+hx));
            float dW = Mathf.Abs(lp.x - (-hx));

            GamePlatform.Edge edge; bool edgeIsX; float baseCoord; int len;

            if (attachEdge != EdgeOverride.Auto)
            {
                switch (attachEdge)
                {
                    case EdgeOverride.North: edge = GamePlatform.Edge.North; edgeIsX = true;  baseCoord = -hx; len = gp.Footprint.x; break;
                    case EdgeOverride.South: edge = GamePlatform.Edge.South; edgeIsX = true;  baseCoord = -hx; len = gp.Footprint.x; break;
                    case EdgeOverride.East:  edge = GamePlatform.Edge.East;  edgeIsX = false; baseCoord = -hz; len = gp.Footprint.y; break;
                    default:                 edge = GamePlatform.Edge.West;  edgeIsX = false; baseCoord = -hz; len = gp.Footprint.y; break;
                }
            }
            else
            {
                if      (dN <= dS && dN <= dE && dN <= dW) { edge = GamePlatform.Edge.North; edgeIsX = true;  baseCoord = -hx; len = gp.Footprint.x; }
                else if (dS <= dN && dS <= dE && dS <= dW) { edge = GamePlatform.Edge.South; edgeIsX = true;  baseCoord = -hx; len = gp.Footprint.x; }
                else if (dE <= dW)                         { edge = GamePlatform.Edge.East;  edgeIsX = false; baseCoord = -hz; len = gp.Footprint.y; }
                else                                       { edge = GamePlatform.Edge.West;  edgeIsX = false; baseCoord = -hz; len = gp.Footprint.y; }
            }

            float coord = edgeIsX ? lp.x : lp.z;
            int kCenter = Mathf.RoundToInt(coord - baseCoord);

            int L = Mathf.Max(1, sizeAlongMeters);

            // Ensure exactly L marks within [0 .. len-1] (corners are 0 and len, skip them for mids)
            int start = Mathf.Clamp(kCenter - (L / 2), 0, Mathf.Max(0, len - L));
            int end   = start + (L - 1); // inclusive

            for (int m = start; m <= end; m++)
            {
                int idx = gp.GetSocketIndexByEdgeMark(edge, m);
                if (!result.Contains(idx)) result.Add(idx);
            }

            return result;
        }

        private int NearestCornerIndex(GamePlatform gp)
        {
            int best = -1; float bestD = float.MaxValue;
            var sockets = gp.Sockets;
            Vector3 myPos = transform.position;
            for (int i = 0; i < sockets.Count; i++)
            {
                var s = sockets[i];
                if (s.Location != GamePlatform.SocketLocation.Corner) continue;
                float d = Vector3.Distance(myPos, gp.GetSocketWorldPosition(i));
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // ---------- Visibility ----------
        public void Hide() => SetHidden(true);
        public void Show() => SetHidden(false);

        public void SetHidden(bool hidden)
        {
            _isHidden = hidden;
            ApplyVisibilityImmediate();
        }

        private void ApplyVisibilityImmediate()
        {
            bool shouldBeActive = !_isHidden;
            if (gameObject.activeSelf != shouldBeActive)
                gameObject.SetActive(shouldBeActive);
        }

        private static bool IsEditingPrefab()
        {
#if UNITY_EDITOR
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null;
#else
            return false;
#endif
        }
    }
}
