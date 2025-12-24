using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Platforms
{
    /// <summary>
    /// Handles pickup visuals and physics for platforms.
    /// Listens to GamePlatform events and manages colliders, materials, and position restoration.
    /// </summary>
    [DisallowMultipleComponent]
    public class PickupHandler : MonoBehaviour
    {
        #region Configuration & State
        
        
        private GamePlatform _platform;
        
        
        private Vector3 _originalPosition;
        private Quaternion _originalRotation;
        private Material[] _originalMaterials;
        private readonly List<Renderer> _allRenderers = new();
        
        
        [Header("Pickup Materials (Optional - will auto-create if not assigned)")]
        [SerializeField] private Material pickupValidMaterial;
        [SerializeField] private Material pickupInvalidMaterial;
        
        // Shader property IDs for auto-generated materials
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
        
        
        /// Called by GamePlatform to inject dependencies and subscribe to events
        public void Initialize(GamePlatform platform)
        {
            _platform = platform;
            
            // Subscribe to GamePlatform events
            _platform.PickedUp += OnPickedUp;
            _platform.Placed += OnPlaced;
            _platform.PlacementCancelled += OnPlacementCancelled;

            GenAutoMaterial(true);
            GenAutoMaterial(false);
        }
        
        
        private void OnDestroy()
        {
            if (_platform)
            {
                _platform.PickedUp -= OnPickedUp;
                _platform.Placed -= OnPlaced;
                _platform.PlacementCancelled -= OnPlacementCancelled;
            }
        }
        
        
        #endregion
        
        
        #region Event Handlers
        
        
        /// Called when platform is picked up (existing platforms only)
        private void OnPickedUp(GamePlatform platform)
        {
            // Store original transform for cancellation
            _originalPosition = transform.position;
            _originalRotation = transform.rotation;
            
            // Disable colliders so we can raycast through the platform
            DisableColliders();
            
            // Cache renderers and store original materials
            CacheRenderersAndMaterials();
        }
        
        
        /// Called when platform is placed successfully
        private void OnPlaced(GamePlatform platform)
        {
            // Restore colliders
            EnableColliders();
            
            // Restore original materials
            RestoreOriginalMaterials();
        }
        
        
        /// Called when placement is cancelled
        private void OnPlacementCancelled(GamePlatform platform)
        {
            if (_platform.IsNewObject)
            {
                // New object - destroy it
                Destroy(gameObject);
            }
            else
            {
                // Existing object - restore original position
                // Colliders/materials restored by subsequent Placed event from GamePlatform)
                transform.position = _originalPosition;
                transform.rotation = _originalRotation;

                _platform.Place();
            }
        }
        
        
        #endregion
        
        
        #region Visuals & Colliders
        
        
        /// Updates visual feedback during pickup - called each frame while picked up
        public void UpdateValidityVisuals()
        {
            bool isValid = _platform.CanBePlaced;
            Material previewMaterial = GetAutoMaterial(isValid);
            
            foreach (var modelRenderer in _allRenderers)
            {
                Material[] materials = modelRenderer.sharedMaterials;
                for (int i = 0; i < modelRenderer.sharedMaterials.Length; i++)
                    materials[i] = previewMaterial;
                
                modelRenderer.sharedMaterials = materials;
            }
        }
        
        
        
        private void CacheRenderersAndMaterials()
        {
            if (_allRenderers.Count == 0)
                _allRenderers.AddRange(GetComponentsInChildren<Renderer>(true));
            
            if (_allRenderers.Count > 0 && _allRenderers[0])
                _originalMaterials = _allRenderers[0].sharedMaterials;
        }
        
        
        private void RestoreOriginalMaterials()
        {
            if (_originalMaterials is not { Length: > 0 }) return;

            foreach (var modelRenderer in _allRenderers.Where(modelRenderer => modelRenderer))
            {
                modelRenderer.sharedMaterials = _originalMaterials;
            }
        }


        private Material GetAutoMaterial(bool isValid)
        {
            return isValid ? pickupValidMaterial : pickupInvalidMaterial;
        }
        
        
        
        private void GenAutoMaterial(bool isValid)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (!shader) shader = Shader.Find("Standard");
                
            Material autoGenMaterial = new Material(shader);

            if (isValid)
            {
                autoGenMaterial.name = "AutoGen_Placement_Valid";
                autoGenMaterial.SetColor(BaseColor, new Color(0f, 1f, 0f, 0.6f));
                autoGenMaterial.SetColor(Color1, new Color(0f, 1f, 0f, 0.6f));
            }
            else
            {
                autoGenMaterial.name = "AutoGen_Placement_Invalid";
                autoGenMaterial.SetColor(BaseColor, new Color(1f, 0f, 0f, 0.6f));
                autoGenMaterial.SetColor(Color1, new Color(1f, 0f, 0f, 0.6f));
            }
            
            autoGenMaterial.SetFloat(Surface, 1);
            autoGenMaterial.SetFloat(Blend, 0);
            autoGenMaterial.SetFloat(AlphaClip, 0);
            autoGenMaterial.SetFloat(SrcBlend, (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            autoGenMaterial.SetFloat(DstBlend, (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            autoGenMaterial.SetFloat(ZWrite, 0);
            
            autoGenMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            autoGenMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            autoGenMaterial.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            
            if(isValid) pickupValidMaterial = autoGenMaterial;
            else pickupInvalidMaterial = autoGenMaterial;
        }
        
        
        private void DisableColliders()
        {
            foreach (Collider col in _platform.PlatformColliders)
            {
                col.enabled = false;
            }
        }
        
        
        private void EnableColliders()
        {
            foreach (Collider col in _platform.PlatformColliders)
            {
                col.enabled = true;
            }
        }
        
        
        #endregion
    }
}
