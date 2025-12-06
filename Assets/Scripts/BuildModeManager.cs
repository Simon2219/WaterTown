using System.Collections.Generic;
using Grid;
using UnityEngine;
using UnityEngine.InputSystem;
using WaterTown.Building.UI;
using WaterTown.Platforms;
using WaterTown.Town;

namespace WaterTown.Building
{
    /// <summary>
    /// Manages build mode: ghost preview, rotation, placement validation, and final placement.
    /// Works with TownManager for grid validation and GameUIController for blueprint selection.
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

        [Header("Preview Settings")]
        [SerializeField] private int previewLevel = 0;
        [SerializeField] private Material previewValidMaterial;
        [SerializeField] private Material previewInvalidMaterial;
        [SerializeField] private LayerMask raycastLayers;

        [Header("Placement Rules")]
        [Tooltip("Global override: if false, platforms can be placed anywhere (ignores blueprint adjacency settings).")]
        [SerializeField] private bool enforceAdjacencyRequirements = false;
        [SerializeField] private float gridSnapSize = 1f;

        // Runtime state
        private PlatformBlueprint _selectedBlueprint;
        private GameObject _ghostPlatform;
        private GamePlatform _ghostPlatformComponent;
        private float _currentGhostRotation = 0f;
        private bool _isValidPlacement = false;
        private bool _lastValidityState = false; // Cache to avoid unnecessary material updates
        
        // Reusable lists for per-frame calculations (avoid allocations)
        private readonly List<Vector2Int> _tempCellList = new List<Vector2Int>();
        private readonly List<Vector3Int> _tempNeighborList = new List<Vector3Int>();
        private readonly List<Renderer> _ghostRenderers = new List<Renderer>();
        
        #endregion

        #region Unity Lifecycle
        
        // ---------- Unity Lifecycle ----------

        private void Awake()
        {
            if (!townManager) townManager = FindFirstObjectByType<TownManager>();
            if (!grid) grid = FindFirstObjectByType<WorldGrid>();
            if (!mainCamera) mainCamera = Camera.main;
        }

        private void OnEnable()
        {
            // Verify critical references before enabling
            if (townManager == null || grid == null || mainCamera == null)
            {
                Debug.LogError("[BuildModeManager] Missing critical references. Disabling component.", this);
                enabled = false;
                return;
            }

            if (gameUI != null)
                gameUI.OnBlueprintSelected += OnBlueprintSelected;

            if (placeAction != null && placeAction.action != null)
            {
                placeAction.action.Enable();
                placeAction.action.performed += OnPlacePerformed;
            }

            if (cancelAction != null && cancelAction.action != null)
            {
                cancelAction.action.Enable();
                cancelAction.action.performed += OnCancelPerformed;
            }

            if (rotateCWAction != null && rotateCWAction.action != null)
            {
                rotateCWAction.action.Enable();
                rotateCWAction.action.performed += OnRotateCWPerformed;
            }

            if (rotateCCWAction != null && rotateCCWAction.action != null)
            {
                rotateCCWAction.action.Enable();
                rotateCCWAction.action.performed += OnRotateCCWPerformed;
            }
        }

        private void OnDisable()
        {
            if (gameUI != null)
                gameUI.OnBlueprintSelected -= OnBlueprintSelected;

            if (placeAction != null && placeAction.action != null)
            {
                placeAction.action.performed -= OnPlacePerformed;
                placeAction.action.Disable();
            }

            if (cancelAction != null && cancelAction.action != null)
            {
                cancelAction.action.performed -= OnCancelPerformed;
                cancelAction.action.Disable();
            }

            if (rotateCWAction != null && rotateCWAction.action != null)
            {
                rotateCWAction.action.performed -= OnRotateCWPerformed;
                rotateCWAction.action.Disable();
            }

            if (rotateCCWAction != null && rotateCCWAction.action != null)
            {
                rotateCCWAction.action.performed -= OnRotateCCWPerformed;
                rotateCCWAction.action.Disable();
            }

            ClearGhost();
        }

        private void Update()
        {
            if (_ghostPlatform != null)
            {
                UpdateGhostPosition();
                UpdateGhostValidity();
                UpdateGhostVisuals();
            }
        }
        
        #endregion

        #region Blueprint Selection
        
        // ---------- Blueprint Selection ----------

        private void OnBlueprintSelected(PlatformBlueprint blueprint)
        {
            if (blueprint == null)
            {
                ClearGhost();
                _selectedBlueprint = null;
                return;
            }

            _selectedBlueprint = blueprint;
            _currentGhostRotation = 0f;
            CreateGhost();
        }
        
        #endregion

        #region Ghost Management
        
        // ---------- Ghost Management ----------

        private void CreateGhost()
        {
            ClearGhost();

            if (_selectedBlueprint == null) return;

            // Instantiate preview prefab or runtime prefab
            GameObject prefab = _selectedBlueprint.PreviewPrefab != null 
                ? _selectedBlueprint.PreviewPrefab 
                : _selectedBlueprint.RuntimePrefab;

            if (prefab == null)
            {
                Debug.LogWarning("[BuildModeManager] No prefab assigned to blueprint.");
                return;
            }

            _ghostPlatform = Instantiate(prefab, Vector3.zero, Quaternion.identity);
            _ghostPlatform.name = "GHOST_" + _selectedBlueprint.DisplayName;

            // Get GamePlatform component
            _ghostPlatformComponent = _ghostPlatform.GetComponent<GamePlatform>();
            if (_ghostPlatformComponent == null)
            {
                Debug.LogError("[BuildModeManager] Ghost platform missing GamePlatform component!");
                Destroy(_ghostPlatform);
                _ghostPlatform = null;
                return;
            }

            // Collect all renderers for material swapping
            _ghostRenderers.Clear();
            _ghostRenderers.AddRange(_ghostPlatform.GetComponentsInChildren<Renderer>(true));

            // Disable NavMesh building on ghost
            var navSurface = _ghostPlatform.GetComponent<Unity.AI.Navigation.NavMeshSurface>();
            if (navSurface) navSurface.enabled = false;

            // Disable any colliders on ghost
            foreach (var col in _ghostPlatform.GetComponentsInChildren<Collider>(true))
                col.enabled = false;

            // Register ghost ONCE for railing preview (NOT for occupancy)
            // The ghost will update its position via transform, triggering TownManager's PoseChanged
            if (townManager != null && _ghostPlatformComponent != null)
                townManager.RegisterPreviewPlatform(_ghostPlatformComponent, previewLevel);

            // Force initial material update by resetting validity state
            _lastValidityState = true; // Force update on first frame
            _isValidPlacement = false; // Start as invalid

            UpdateGhostPosition();
        }

        private void ClearGhost()
        {
            if (_ghostPlatform != null)
            {
                // Unregister from TownManager's preview tracking
                if (_ghostPlatformComponent != null && townManager != null)
                    townManager.UnregisterPlatform(_ghostPlatformComponent);

                Destroy(_ghostPlatform);
                _ghostPlatform = null;
                _ghostPlatformComponent = null;
            }

            _ghostRenderers.Clear();
            _tempCellList.Clear();
        }
        
        #endregion

        #region Ghost Updates & Validation
        
        // ---------- Ghost Updates ----------

        private void UpdateGhostPosition()
        {
            if (_ghostPlatform == null || mainCamera == null || grid == null) return;

            // Get mouse position using new Input System
            Vector2 mousePosition = Mouse.current != null 
                ? Mouse.current.position.ReadValue() 
                : Vector2.zero;

            // Raycast from mouse to grid level
            Ray ray = mainCamera.ScreenPointToRay(mousePosition);
            Vector3Int levelRef = new Vector3Int(0, 0, previewLevel);

            if (grid.RaycastToCell(ray, levelRef, out Vector3Int hoveredCell, out Vector3 hitPoint))
            {
                // Snap to grid using WorldGrid for perfect alignment
                // Account for rotation when determining effective footprint
                Vector2Int baseFootprint = _ghostPlatformComponent != null 
                    ? _ghostPlatformComponent.Footprint 
                    : new Vector2Int(1, 1);
                
                // Determine if footprint is swapped due to 90° or 270° rotation
                int rotationSteps = Mathf.RoundToInt(_currentGhostRotation / 90f) & 3;
                bool isRotated90Or270 = (rotationSteps % 2) == 1;
                
                Vector2Int effectiveFootprint = isRotated90Or270 
                    ? new Vector2Int(baseFootprint.y, baseFootprint.x) // Swap width/height
                    : baseFootprint;
                
                Vector3 snappedPosition = grid.SnapToGridForPlatform(hitPoint, effectiveFootprint, previewLevel);

                // Apply rotation
                Quaternion rotation = Quaternion.Euler(0f, _currentGhostRotation, 0f);
                _ghostPlatform.transform.SetPositionAndRotation(snappedPosition, rotation);
            }
        }

        private void UpdateGhostValidity()
        {
            if (_ghostPlatformComponent == null || townManager == null || grid == null)
            {
                _isValidPlacement = false;
                return;
            }

            // Compute which cells this ghost would occupy (reuse temp list)
            _tempCellList.Clear();
            townManager.ComputeCellsForPlatform(_ghostPlatformComponent, previewLevel, _tempCellList);

            if (_tempCellList.Count == 0)
            {
                _isValidPlacement = false;
                return;
            }

            // Check if area is free using TownManager (which checks WorldGrid's Occupied flags)
            // Ghost should NEVER have Occupied flag set, so it won't interfere
            bool areaFree = townManager.IsAreaFree(_tempCellList, previewLevel);

            // Check adjacency requirement only if globally enforced AND blueprint requires it
            bool meetsAdjacency = true;
            if (enforceAdjacencyRequirements && _selectedBlueprint != null && _selectedBlueprint.RequireEdgeAdjacency)
            {
                meetsAdjacency = CheckAdjacencyRequirement(_tempCellList, previewLevel);
            }

            _isValidPlacement = areaFree && meetsAdjacency;
        }

        private bool CheckAdjacencyRequirement(List<Vector2Int> cells, int level)
        {
            if (cells == null || cells.Count == 0) return false;

            // Count real placed platforms (ghost should never have Occupied flag set)
            int realPlatformCount = 0;
            foreach (var platform in GamePlatform.AllPlatforms)
            {
                if (platform != null && platform != _ghostPlatformComponent)
                    realPlatformCount++;
            }
            
            // First platform is always valid
            if (realPlatformCount == 0) return true;

            // Check if any cell is edge-adjacent to an occupied cell
            // (Ghost should never set Occupied flag, so it won't interfere)
            foreach (var cell in cells)
            {
                Vector3Int gridCell = new Vector3Int(cell.x, cell.y, level);
                grid.GetNeighbors4(gridCell, _tempNeighborList);

                foreach (var neighbor in _tempNeighborList)
                {
                    if (grid.CellHasAnyFlag(neighbor, WorldGrid.CellFlag.Occupied))
                        return true; // Found adjacent occupied cell
                }
            }

            return false; // No adjacent platforms found
        }

        private void UpdateGhostVisuals()
        {
            // Only update materials when validity state changes (performance optimization)
            if (_ghostRenderers.Count == 0) return;
            if (_isValidPlacement == _lastValidityState) return;
            _lastValidityState = _isValidPlacement;

            Material targetMaterial = _isValidPlacement ? previewValidMaterial : previewInvalidMaterial;

            // Use blueprint materials if available, otherwise use defaults
            if (_selectedBlueprint != null)
            {
                if (_isValidPlacement && _selectedBlueprint.PreviewValidMaterial != null)
                    targetMaterial = _selectedBlueprint.PreviewValidMaterial;
                else if (!_isValidPlacement && _selectedBlueprint.PreviewInvalidMaterial != null)
                    targetMaterial = _selectedBlueprint.PreviewInvalidMaterial;
            }

            if (targetMaterial != null)
            {
                foreach (var renderer in _ghostRenderers)
                {
                    if (renderer != null)
                    {
                        var materials = renderer.sharedMaterials;
                        for (int i = 0; i < materials.Length; i++)
                            materials[i] = targetMaterial;
                        renderer.sharedMaterials = materials;
                    }
                }
            }
        }
        
        #endregion

        #region Input Handlers
        
        // ---------- Input Handlers ----------

        private void OnPlacePerformed(InputAction.CallbackContext context)
        {
            if (_ghostPlatform == null) return; // Silently ignore if no ghost
            
            if (!_isValidPlacement)
            {
                Debug.Log("[BuildModeManager] Cannot place: Invalid placement (check area free and adjacency).");
                return;
            }

            PlacePlatform();
        }

        private void OnCancelPerformed(InputAction.CallbackContext context)
        {
            // Clear selection
            if (gameUI != null)
                gameUI.ClearSelection();

            ClearGhost();
            _selectedBlueprint = null;
        }

        private void OnRotateCWPerformed(InputAction.CallbackContext context)
        {
            if (_ghostPlatform == null) return;

            float rotationStep = _selectedBlueprint != null && _selectedBlueprint.RotStep == PlatformBlueprint.RotationStep.Deg45 
                ? 45f 
                : 90f;

            _currentGhostRotation += rotationStep;
            if (_currentGhostRotation >= 360f) _currentGhostRotation -= 360f;

            _ghostPlatform.transform.rotation = Quaternion.Euler(0f, _currentGhostRotation, 0f);
        }

        private void OnRotateCCWPerformed(InputAction.CallbackContext context)
        {
            if (_ghostPlatform == null) return;

            float rotationStep = _selectedBlueprint != null && _selectedBlueprint.RotStep == PlatformBlueprint.RotationStep.Deg45 
                ? 45f 
                : 90f;

            _currentGhostRotation -= rotationStep;
            if (_currentGhostRotation < 0f) _currentGhostRotation += 360f;

            _ghostPlatform.transform.rotation = Quaternion.Euler(0f, _currentGhostRotation, 0f);
        }
        
        #endregion

        #region Platform Placement
        
        // ---------- Placement ----------

        private void PlacePlatform()
        {
            if (_selectedBlueprint == null || _selectedBlueprint.RuntimePrefab == null)
            {
                Debug.LogWarning("[BuildModeManager] Cannot place: no blueprint or runtime prefab.");
                return;
            }

            // Compute cells for final placement (from current ghost position)
            _tempCellList.Clear();
            townManager.ComputeCellsForPlatform(_ghostPlatformComponent, previewLevel, _tempCellList);
            
            if (_tempCellList.Count == 0)
            {
                Debug.LogWarning("[BuildModeManager] Cannot place: ghost has no valid cells.");
                return;
            }

            // Instantiate the runtime prefab at ghost's current position
            GameObject placedPlatform = Instantiate(
                _selectedBlueprint.RuntimePrefab,
                _ghostPlatform.transform.position,
                _ghostPlatform.transform.rotation
            );

            placedPlatform.name = _selectedBlueprint.DisplayName;

            // Get GamePlatform component and register it as permanent
            var platformComponent = placedPlatform.GetComponent<GamePlatform>();
            if (platformComponent != null && townManager != null)
            {
                // Register as permanent platform (marks cells as Occupied in WorldGrid)
                townManager.RegisterPlatform(platformComponent, _tempCellList, previewLevel, markOccupiedInGrid: true);
                
                Debug.Log($"[BuildModeManager] Placed {_selectedBlueprint.DisplayName} at {placedPlatform.transform.position}");
            }
            else
            {
                Debug.LogWarning("[BuildModeManager] Placed platform has no GamePlatform component!");
            }

            // Ghost stays active for continued placement (will update validation automatically next frame)
        }
        
        #endregion

        #region Public API
        
        // ---------- Public API ----------

        public bool IsInBuildMode => _ghostPlatform != null;
        public bool IsValidPlacement => _isValidPlacement;
        public PlatformBlueprint SelectedBlueprint => _selectedBlueprint;

        public void SetPreviewLevel(int level)
        {
            previewLevel = Mathf.Max(0, level);
        }
        
        #endregion
    }
}
