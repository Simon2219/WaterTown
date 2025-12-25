using System.Collections.Generic;
using UnityEngine;

namespace Platforms
{
    [DisallowMultipleComponent]
    public class PlatformModule : MonoBehaviour
    {
        [Header("Size (meters on 1m grid)")]
        [Min(1)] public int sizeAlongMeters = 2;   // occupies exactly sizeAlongMeters segments
        [Min(1)] public int sizeInwardMeters = 1;  // reserved for future (depth), not used for sockets

        [Header("Behavior")]
        public bool blocksLink = false;     // true & active => socket becomes Occupied after Refresh

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
            if (_platform) { _platform.UnregisterModule(this); _platform.RefreshSocketStatuses(); }
        }

        private void OnDestroy()
        {
            if (IsEditingPrefab()) return;
            if (_platform) { _platform.UnregisterModule(this); _platform.RefreshSocketStatuses(); }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            sizeAlongMeters  = Mathf.Max(1, sizeAlongMeters);
            sizeInwardMeters = Mathf.Max(1, sizeInwardMeters);
            if (IsEditingPrefab()) return;

            if (_platform && isActiveAndEnabled)
            {
                _platform.UnregisterModule(this);
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

            _platform.UnregisterModule(this);
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
                _platform.RegisterModuleOnSockets(this, occupiesSockets: true, _boundSocketIndices);
        }

        /// Computes which socket indices this module should bind to
        /// Gets sizeAlongMeters nearest sockets from module's world position
        ///
        private List<int> ComputeSocketIndices(GamePlatform platform)
        {
            if (!platform) return new List<int>();
            
            // Get the nearest sockets based on module size
            // maxDistance of 1.5 * sizeAlongMeters covers the module's span with margin
            int socketCount = Mathf.Max(1, sizeAlongMeters);
            float maxDistance = socketCount * 1.5f;
            
            return platform.GetNearestSocketIndices(transform.position, socketCount, maxDistance);
        }
        

        // ---------- Visibility ----------
        public void Hide() => SetHidden(true);
        public void Show() => SetHidden(false);

        public void SetHidden(bool hidden)
        {
            _isHidden = hidden;
            ApplyVisibilityImmediate();
        }

        /// <summary>
        /// Hidden = GameObject inactive (NOT destroyed).
        /// This matches the previous behavior the system relied on.
        /// </summary>
        private void ApplyVisibilityImmediate()
        {
            bool shouldBeActive = !_isHidden;
            if (gameObject.activeSelf != shouldBeActive)
                gameObject.SetActive(shouldBeActive);
        }



        public void UpdateVisibility()
        {
            // Not sure how to handle visibility yet
            // Old implementation was hiding Modules on Connection
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

