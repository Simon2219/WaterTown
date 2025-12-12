using System;
using System.Collections.Generic;
using Interfaces;
using UnityEngine;

namespace Platforms
{
    /// <summary>
    /// Handles IPickupable implementation for platforms
    /// Manages pickup state, visual feedback, materials, and colliders
    /// </summary>
    [DisallowMultipleComponent]
    public class PickupHandler : MonoBehaviour, IPickupable
    {
        #region Events
        
        
        /// Fired when this platform is picked up
        public event Action<GamePlatform> PickedUp;
        
        /// Fired when this platform is placed
        public event Action<GamePlatform> Placed;
        
        
        #endregion
        
        
        
        
        
        #region Dependencies
        
        
        private GamePlatform _platform;
        private PlatformManager _platformManager;
        
        
        #endregion
        
        
        
        
        #region IPickupable Properties
        
        
        public bool IsPickedUp { get; set; }
        public bool CanBePlaced => ValidatePlacement();
        public Transform Transform => transform;
        public GameObject GameObject => gameObject;
        
        
        #endregion
        
        
        
        
        #region State
        
        
        private Vector3 _originalPosition;
        private Quaternion _originalRotation;
        private bool _isNewObject;
        private Material[] _originalMaterials;
        private readonly List<Renderer> _allRenderers = new List<Renderer>();
        
        
        
        
        [Header("Pickup Materials (Optional - will auto-create if not assigned)")]
        [SerializeField] private Material pickupValidMaterial;
        [SerializeField] private Material pickupInvalidMaterial;
        
        // Auto-generated materials for testing
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int Color1 = Shader.PropertyToID("_Color");
        private static readonly int Surface = Shader.PropertyToID("_Surface");
        private static readonly int Blend = Shader.PropertyToID("_Blend");
        private static readonly int AlphaClip = Shader.PropertyToID("_AlphaClip");
        private static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
        private static readonly int ZWrite = Shader.PropertyToID("_ZWrite");

        #endregion
        
        
        
        
        #region Initialization
        
        
        /// Called by GamePlatform to inject dependencies
        public void SetDependencies(GamePlatform platform, PlatformManager platformManager)
        {
            _platform = platform;
            _platformManager = platformManager;
        }
        
        
        
        #endregion
        
        
        
        
        #region IPickupable Implementation
        
        
        public void OnPickedUp(bool isNewObject)
        {
            IsPickedUp = true;
            _isNewObject = isNewObject;
            
            // Sync state with GamePlatform
            if (_platform)
                _platform.IsPickedUp = true;
            
            // Store original transform for cancellation
            _originalPosition = transform.position;
            _originalRotation = transform.rotation;
            
            // Disable colliders so we can raycast through the platform
            if (_platform)
            {
                foreach (var col in _platform.CachedColliders)
                {
                    if (col) col.enabled = false;
                }
            }
            
            // Cache renderers and store original materials
            if (_allRenderers.Count == 0)
            {
                _allRenderers.AddRange(GetComponentsInChildren<Renderer>(true));
            }
            
            if (_allRenderers.Count > 0 && _allRenderers[0])
            {
                _originalMaterials = _allRenderers[0].sharedMaterials;
            }
            
            // Fire pickup event for existing platforms (not for new spawned ones)
            if (!isNewObject)
            {
                PickedUp?.Invoke(_platform);
            }
        }


        public void OnPlaced()
        {
            // Restore colliders
            if (_platform)
            {
                foreach (var col in _platform.CachedColliders)
                {
                    if (col) col.enabled = true;
                }
            }
            
            // Restore original materials
            RestoreOriginalMaterials();
            
            // Compute cells for placement
            if (!_platformManager)
            {
                Debug.LogError($"[PlatformPickupHandler] Cannot place platform '{name}' - PlatformManager not initialized!");
                return;
            }
            
            List<Vector2Int> cells = _platformManager.GetCellsForPlatform(_platform);
            if (_platform)
                _platform.occupiedCells = cells;
            
            // Set IsPickedUp to false before firing event
            IsPickedUp = false;
            if (_platform)
                _platform.IsPickedUp = false;
            
            // Fire event for managers to register platform and trigger adjacency
            Placed?.Invoke(_platform);
        }


        public void OnPlacementCancelled()
        {
            IsPickedUp = false;
            if (_platform)
                _platform.IsPickedUp = false;
            
            if (_isNewObject)
            {
                // New object - destroy it
                Destroy(gameObject);
            }
            else
            {
                // Existing object - restore original position
                transform.position = _originalPosition;
                transform.rotation = _originalRotation;
                
                // Re-enable colliders
                if (_platform)
                {
                    foreach (var col in _platform.CachedColliders)
                    {
                        if (col) col.enabled = true;
                    }
                }
                
                // Restore original materials
                RestoreOriginalMaterials();
                
                // Compute cells and fire placement event to re-register at original position
                if (!_platformManager)
                {
                    Debug.LogError($"[PlatformPickupHandler] Cannot cancel placement of platform '{name}' - PlatformManager not initialized!");
                    return;
                }
                
                List<Vector2Int> cells = _platformManager.GetCellsForPlatform(_platform);
                if (_platform)
                    _platform.occupiedCells = cells;
                
                Placed?.Invoke(_platform);
            }
        }


        public void UpdateValidityVisuals()
        {
            bool isValid = CanBePlaced;
            Material previewMaterial = isValid 
                ? (pickupValidMaterial ? pickupValidMaterial : GetAutoMaterial(isValid: true))
                : (pickupInvalidMaterial ? pickupInvalidMaterial : GetAutoMaterial(isValid: false));
            

            foreach (var modelRenderer in _allRenderers)
            {
                Material[] materials = modelRenderer.sharedMaterials;
                
                for (int i = 0; i < materials.Length; i++)
                    materials[i] = previewMaterial;
                
                modelRenderer.sharedMaterials = materials;
            }
        }
        
        
        #endregion
        
        
        
        
        #region Validation
        
        
        private bool ValidatePlacement()
        {
            /*if (!IsPickedUp) return true;
            
            if (_platformManager == null || _platform == null)
                return false;*/
            
            List<Vector2Int> cells = _platformManager.GetCellsForPlatform(_platform);
            
            
            return cells.Count != 0 && _platformManager.IsAreaEmpty(cells);
        }
        
        
        #endregion
        
        
        
        
        #region Material Management
        
        
        private void RestoreOriginalMaterials()
        {
            if (_originalMaterials is { Length: > 0 })
            {
                foreach (var modelRenderer in _allRenderers)
                {
                    if (modelRenderer)
                        modelRenderer.sharedMaterials = _originalMaterials;
                }
            }
        }


        private static Material GetAutoMaterial(bool isValid)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (!shader) shader = Shader.Find("Standard");
                
            Material autoGenMaterial = new Material(shader);

            if (isValid)
            {
                autoGenMaterial.name = "AutoGen_Placement_Valid";
                
                autoGenMaterial.SetColor(BaseColor, new Color(0f, 1f, 0f, 0.6f));
                autoGenMaterial.SetColor(Color1, new Color(0f, 1f, 0f, 0.6f));
                
                autoGenMaterial.SetFloat(Surface, 1);
                autoGenMaterial.SetFloat(Blend, 0);
                autoGenMaterial.SetFloat(AlphaClip, 0);
                autoGenMaterial.SetFloat(SrcBlend, (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                autoGenMaterial.SetFloat(DstBlend, (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                autoGenMaterial.SetFloat(ZWrite, 0);
                
            }
            else
            {
                autoGenMaterial.name = "AutoGen_Placement_Invalid";
                
                autoGenMaterial.SetColor(BaseColor, new Color(1f, 0f, 0f, 0.6f));
                autoGenMaterial.SetColor(Color1, new Color(1f, 0f, 0f, 0.6f));
                
                autoGenMaterial.SetFloat(Surface, 1);
                autoGenMaterial.SetFloat(Blend, 0);
                autoGenMaterial.SetFloat(AlphaClip, 0);
                autoGenMaterial.SetFloat(SrcBlend, (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                autoGenMaterial.SetFloat(DstBlend, (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                autoGenMaterial.SetFloat(ZWrite, 0);
                
            }
            
            autoGenMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                
            autoGenMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            autoGenMaterial.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            
            return autoGenMaterial;
        }
        
        
        #endregion
    }
}

