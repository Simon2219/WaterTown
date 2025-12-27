using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Platforms
{
    [DisallowMultipleComponent]
    public class PlatformModule : MonoBehaviour
    {
        #region Configuration 
        
        
        [Header("Size Along (Cells)")] [Min(1)]
        public int moduleSize = 2;

        
        [SerializeField, HideInInspector] private List<int> _boundSocketIndices = new();
        public IReadOnlyList<int> BoundSocketIndices => _boundSocketIndices;
        
        
        [SerializeField, HideInInspector] private bool _isVisible;
        public bool IsVisible => _isVisible;
        
        
        private GamePlatform _platform;

        
        #endregion
        

        #region Lifecycle
        
        
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
            UpdateVisibility();
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
        
        
        
        public void EnsureRegistered()
        {
            if (IsEditingPrefab()) return;
            if (!_platform) _platform = GetComponentInParent<GamePlatform>();
            if (!_platform) return;

            _platform.UnregisterModule(this);
            if (!enabled) enabled = true;

            RebindAndRegister();
            UpdateVisibility();
            _platform.RefreshSocketStatuses();
        }
        
        
        
#if UNITY_EDITOR
        private void OnValidate()
        {
            moduleSize  = Mathf.Max(1, moduleSize);

            if (IsEditingPrefab()) return;

            if (_platform && isActiveAndEnabled)
            {
                _platform.UnregisterModule(this);
                RebindAndRegister();
                UpdateVisibility();
                _platform.RefreshSocketStatuses();
            }
        }
#endif
        
        
        #endregion
        

        // ---------- Binding ----------
        private void RebindAndRegister()
        {
            _boundSocketIndices = ComputeSocketIndices(_platform);
            if (_boundSocketIndices.Count > 0)
                _platform.RegisterModule(this, _boundSocketIndices);
        }

        private List<int> ComputeSocketIndices(GamePlatform platform)
        {
            if (!platform) return new List<int>();
            
            // Use the module's size to find that many nearest sockets
            // maxDistance covers the module size plus some buffer for edge cases
            float maxDistance = (moduleSize + 1) * Grid.WorldGrid.CellSize;
            
            return platform.FindNearestSocketIndices(transform.position, moduleSize, maxDistance);
        }
        

        // ---------- Visibility ----------
        public void Hide() => SetVisibility(false);
        public void Show() => SetVisibility(true);



        public void UpdateVisibility()
        {
            SetVisibility(_isVisible);
        }


        private void SetVisibility(bool visibility)
        {
            bool shouldBeActive = _isVisible;
            if (gameObject.activeSelf != shouldBeActive)
                gameObject.SetActive(shouldBeActive);
            
            _isVisible = visibility;
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

