using System;
using System.Collections.Generic;
using Grid;
using UnityEngine;
using UnityEngine.InputSystem;
using WaterTown.Building.UI;
using WaterTown.Interfaces;
using WaterTown.Platforms;

namespace WaterTown.Building
{
    ///
    /// Manages build mode: spawning/moving pickupable objects, validating placement, and handling placement/cancellation
    /// Unified system for both building NEW platforms and moving EXISTING ones using IPickupable interface
    ///
    [DisallowMultipleComponent]
    public class BuildModeManager : MonoBehaviour
    {
        #region Configuration & Dependencies

        [Header("References")]
        [SerializeField] private TownManager townManager;
        [SerializeField] private PlatformManager platformManager;
        [SerializeField] private WorldGrid grid;
        [SerializeField] private GameUIController gameUI;
        [SerializeField] private Camera mainCamera;

        [Header("Input Actions")]
        [SerializeField] private InputActionReference placeAction;
        [SerializeField] private InputActionReference cancelAction;
        [SerializeField] private InputActionReference rotateCWAction;
        [SerializeField] private InputActionReference rotateCCWAction;

        [Header("Placement Settings")]
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
            try
            {
                FindDependencies();
            }
            catch (Exception ex)
            {
                ErrorHandler.LogAndDisable(ex, this);
            }
        }
        
        /// <summary>
        /// Finds and validates all required dependencies.
        /// Throws InvalidOperationException if any critical dependency is missing.
        /// </summary>
        private void FindDependencies()
        {
            if (!townManager)
            {
                townManager = FindFirstObjectByType<TownManager>();
                if (!townManager)
                {
                    throw ErrorHandler.MissingDependency(typeof(TownManager), this);
                }
            }
            
            if (!grid)
            {
                grid = FindFirstObjectByType<WorldGrid>();
                if (!grid)
                {
                    throw ErrorHandler.MissingDependency(typeof(WorldGrid), this);
                }
            }
            
            if (!mainCamera)
            {
                mainCamera = Camera.main;
                if (!mainCamera)
                {
                    throw ErrorHandler.MissingDependency("Camera.main", this);
                }
            }
        }

        private void OnEnable()
        {

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
            
            // Trigger adjacency update for preview
            if (_currentPickup is GamePlatform && platformManager != null)
            {
                platformManager.TriggerAdjacencyUpdate();
            }
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
            Vector3Int levelRef = new Vector3Int(0, 0, 0);

            if (grid.RaycastToCell(ray, out Vector2Int hoveredCell, out Vector3 hitPoint))
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
                Vector3 snappedPosition = grid.SnapToGridForPlatform(hitPoint, effectiveFootprint);
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
                Debug.LogWarning("[BuildModeManager] Cannot place platform at current position.");
                return;
            }
            
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
            
            _currentPickup.OnPlacementCancelled();
            _currentPickup = null;
            _selectedBlueprint = null;
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
