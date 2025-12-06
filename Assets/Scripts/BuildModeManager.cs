using System.Collections.Generic;
using Grid;
using UnityEngine;
using UnityEngine.InputSystem;
using WaterTown.Building.UI;
using WaterTown.Interfaces;
using WaterTown.Platforms;
using WaterTown.Town;

namespace WaterTown.Building
{
    /// <summary>
    /// Manages build mode: spawning/moving pickupable objects, validating placement, and handling placement/cancellation.
    /// Unified system for both building NEW platforms and moving EXISTING ones using IPickupable interface.
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildModeManager : MonoBehaviour
    {
        #region Configuration & Dependencies
        
        [Header("References")]
        [SerializeField] private TownManager townManager;
        [SerializeField] private WorldGrid grid;
        [SerializeField] private GameUIController gameUI;
        [SerializeField] private Camera mainCamera;

        [Header("Input Actions")]
        [SerializeField] private InputActionReference placeAction;
        [SerializeField] private InputActionReference cancelAction;
        [SerializeField] private InputActionReference rotateCWAction;
        [SerializeField] private InputActionReference rotateCCWAction;

        [Header("Placement Settings")]
        [SerializeField] private int placementLevel = 0;
        [SerializeField] private LayerMask raycastLayers;
        [SerializeField] private float rotationStep = 90f;

        // Runtime state - unified pickup system
        private PlatformBlueprint _selectedBlueprint;
        private IPickupable _currentPickup;
        private float _currentRotation = 0f;
        
        // Reusable lists (avoid allocations)
        private readonly List<Vector2Int> _tempCellList = new List<Vector2Int>();
        
        #endregion

        #region Unity Lifecycle
        
        private void Awake()
        {
            if (!townManager) townManager = FindFirstObjectByType<TownManager>();
            if (!grid) grid = FindFirstObjectByType<WorldGrid>();
            if (!mainCamera) mainCamera = Camera.main;
        }

        private void OnEnable()
        {
            if (townManager == null || grid == null || mainCamera == null)
            {
                Debug.LogError("[BuildModeManager] Missing critical references. Disabling component.", this);
                enabled = false;
                return;
            }

            if (gameUI != null)
                gameUI.OnBlueprintSelected += OnBlueprintSelected;

            if (placeAction?.action != null)
            {
                placeAction.action.Enable();
                placeAction.action.performed += OnPlacePerformed;
            }

            if (cancelAction?.action != null)
            {
                cancelAction.action.Enable();
                cancelAction.action.performed += OnCancelPerformed;
            }

            if (rotateCWAction?.action != null)
            {
                rotateCWAction.action.Enable();
                rotateCWAction.action.performed += OnRotateCWPerformed;
            }

            if (rotateCCWAction?.action != null)
            {
                rotateCCWAction.action.Enable();
                rotateCCWAction.action.performed += OnRotateCCWPerformed;
            }
        }

        private void OnDisable()
        {
            if (gameUI != null)
                gameUI.OnBlueprintSelected -= OnBlueprintSelected;

            if (placeAction?.action != null)
            {
                placeAction.action.performed -= OnPlacePerformed;
                placeAction.action.Disable();
            }

            if (cancelAction?.action != null)
            {
                cancelAction.action.performed -= OnCancelPerformed;
                cancelAction.action.Disable();
            }

            if (rotateCWAction?.action != null)
            {
                rotateCWAction.action.performed -= OnRotateCWPerformed;
                rotateCWAction.action.Disable();
            }

            if (rotateCCWAction?.action != null)
            {
                rotateCCWAction.action.performed -= OnRotateCCWPerformed;
                rotateCCWAction.action.Disable();
            }

            if (_currentPickup != null)
            {
                CancelPlacement();
            }
        }

        private void Update()
        {
            if (_currentPickup == null) return;

            UpdatePickupPosition();

            bool isValid = _currentPickup.CanBePlaced;
            _currentPickup.UpdateValidityVisuals(isValid);
            
            // Trigger railing preview update each frame while placing
            // This ensures railings show/hide based on adjacency to other platforms
            townManager.TriggerAdjacencyUpdate();
        }
        
        #endregion

        #region Blueprint Selection
        
        private void OnBlueprintSelected(PlatformBlueprint blueprint)
        {
            if (blueprint == null)
            {
                Debug.LogWarning("[BuildModeManager] Received null blueprint.");
                return;
            }

            _selectedBlueprint = blueprint;
            _currentRotation = 0f;

            SpawnPlatformForPlacement(blueprint);
        }
        
        #endregion

        #region Pickup/Placement Logic
        
        /// <summary>
        /// Spawns a new platform for placement (build mode).
        /// </summary>
        private void SpawnPlatformForPlacement(PlatformBlueprint blueprint)
        {
            if (blueprint.RuntimePrefab == null)
            {
                Debug.LogWarning($"[BuildModeManager] Blueprint '{blueprint.DisplayName}' has no runtime prefab.");
                return;
            }

            // Cancel any existing pickup
            if (_currentPickup != null)
            {
                CancelPlacement();
            }

            // Instantiate at origin (will be moved to mouse in Update)
            GameObject spawnedPlatform = Instantiate(blueprint.RuntimePrefab, Vector3.zero, Quaternion.identity);
            spawnedPlatform.name = $"{blueprint.DisplayName} (Placing)";

            // Get IPickupable component
            var pickupable = spawnedPlatform.GetComponent<IPickupable>();
            if (pickupable == null)
            {
                Debug.LogError($"[BuildModeManager] Spawned platform '{spawnedPlatform.name}' doesn't have IPickupable component!");
                Destroy(spawnedPlatform);
                return;
            }

            _currentPickup = pickupable;
            _currentPickup.OnPickedUp(isNewObject: true);

            Debug.Log($"[BuildModeManager] Spawned '{blueprint.DisplayName}' for placement.");
        }

        /// <summary>
        /// Picks up an existing platform for moving/rotating.
        /// </summary>
        public void PickupExistingPlatform(IPickupable pickupable)
        {
            if (pickupable == null) return;

            if (_currentPickup != null)
            {
                CancelPlacement();
            }

            _currentPickup = pickupable;
            _currentPickup.OnPickedUp(isNewObject: false);
            _currentRotation = pickupable.Transform.eulerAngles.y;

            Debug.Log($"[BuildModeManager] Picked up existing platform '{pickupable.GameObject.name}' for moving.");
        }

        /// <summary>
        /// Updates the pickup's position to follow the mouse on the grid.
        /// </summary>
        private void UpdatePickupPosition()
        {
            if (_currentPickup == null || mainCamera == null) return;

            // Use new Input System for mouse position
            Vector2 mousePosition = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            Ray ray = mainCamera.ScreenPointToRay(mousePosition);
            Vector3Int levelRef = new Vector3Int(0, 0, placementLevel);

            if (grid.RaycastToCell(ray, levelRef, out Vector3Int hoveredCell, out Vector3 hitPoint))
            {
                // Get footprint
                Vector2Int footprint = Vector2Int.one;
                if (_currentPickup is GamePlatform platform)
                {
                    footprint = platform.Footprint;
                }

                // Account for rotation
                int rotationSteps = Mathf.RoundToInt(_currentRotation / 90f) & 3;
                bool isRotated90Or270 = (rotationSteps % 2) == 1;
                Vector2Int effectiveFootprint = isRotated90Or270
                    ? new Vector2Int(footprint.y, footprint.x)
                    : footprint;

                // Snap to grid
                Vector3 snappedPosition = grid.SnapToGridForPlatform(hitPoint, effectiveFootprint, placementLevel);
                Quaternion rotation = Quaternion.Euler(0f, _currentRotation, 0f);

                _currentPickup.Transform.SetPositionAndRotation(snappedPosition, rotation);
            }
        }

        /// <summary>
        /// Confirms placement of the current pickup.
        /// </summary>
        private void PlacePickup()
        {
            if (_currentPickup == null) return;

            if (!_currentPickup.CanBePlaced)
            {
                Debug.LogWarning("[BuildModeManager] Cannot place: position is invalid.");
                return;
            }

            Debug.Log($"[BuildModeManager] Placed '{_currentPickup.GameObject.name}' at {_currentPickup.Transform.position}");
            
            _currentPickup.OnPlaced();
            _currentPickup = null;
            _selectedBlueprint = null;
        }

        /// <summary>
        /// Cancels placement of the current pickup.
        /// </summary>
        private void CancelPlacement()
        {
            if (_currentPickup == null) return;

            Debug.Log($"[BuildModeManager] Cancelled placement of '{_currentPickup.GameObject.name}'");
            
            _currentPickup.OnPlacementCancelled();
            _currentPickup = null;
            _selectedBlueprint = null;
            
            // Force adjacency update to restore railings on existing platforms
            // This ensures any connections created during preview are cleared
            townManager.TriggerAdjacencyUpdate();
        }
        
        #endregion

        #region Input Handlers
        
        private void OnPlacePerformed(InputAction.CallbackContext context)
        {
            if (_currentPickup != null)
            {
                PlacePickup();
            }
        }

        private void OnCancelPerformed(InputAction.CallbackContext context)
        {
            if (_currentPickup != null)
            {
                CancelPlacement();
            }
            
            // Also clear UI selection
            if (gameUI != null)
                gameUI.ClearSelection();
        }

        private void OnRotateCWPerformed(InputAction.CallbackContext context)
        {
            if (_currentPickup == null) return;

            _currentRotation += rotationStep;
            if (_currentRotation >= 360f) _currentRotation -= 360f;

            _currentPickup.Transform.rotation = Quaternion.Euler(0f, _currentRotation, 0f);
        }

        private void OnRotateCCWPerformed(InputAction.CallbackContext context)
        {
            if (_currentPickup == null) return;

            _currentRotation -= rotationStep;
            if (_currentRotation < 0f) _currentRotation += 360f;

            _currentPickup.Transform.rotation = Quaternion.Euler(0f, _currentRotation, 0f);
        }
        
        #endregion

        #region Public API
        
        public bool HasActivePickup => _currentPickup != null;
        public IPickupable CurrentPickup => _currentPickup;
        
        #endregion
    }
}
