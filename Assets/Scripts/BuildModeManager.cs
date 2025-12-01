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
        [SerializeField] private bool requireAdjacency = true;
        [SerializeField] private float gridSnapSize = 1f;

        // Runtime state
        private PlatformBlueprint _selectedBlueprint;
        private GameObject _ghostPlatform;
        private GamePlatform _ghostPlatformComponent;
        private float _currentRotation = 0f;
        private Vector3 _currentPlacementPosition;
        private bool _isValidPlacement = false;
        private List<Vector2Int> _ghostCells = new List<Vector2Int>();
        private List<Renderer> _ghostRenderers = new List<Renderer>();
        
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
            _currentRotation = 0f;
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

            UpdateGhostPosition();
        }

        private void ClearGhost()
        {
            if (_ghostPlatform != null)
            {
                // Unregister from TownManager if registered
                if (_ghostPlatformComponent != null && townManager != null)
                    townManager.UnregisterPlatform(_ghostPlatformComponent);

                Destroy(_ghostPlatform);
                _ghostPlatform = null;
                _ghostPlatformComponent = null;
            }

            _ghostRenderers.Clear();
            _ghostCells.Clear();
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
                // Snap to grid
                Vector3 snappedPosition = grid.GetCellCenter(hoveredCell);
                _currentPlacementPosition = snappedPosition;

                // Apply rotation
                Quaternion rotation = Quaternion.Euler(0f, _currentRotation, 0f);
                _ghostPlatform.transform.SetPositionAndRotation(_currentPlacementPosition, rotation);
            }
        }

        private void UpdateGhostValidity()
        {
            if (_ghostPlatformComponent == null || townManager == null || grid == null)
            {
                _isValidPlacement = false;
                return;
            }

            // Compute which cells this ghost would occupy
            _ghostCells.Clear();
            townManager.ComputeCellsForPlatform(_ghostPlatformComponent, previewLevel, _ghostCells);

            if (_ghostCells.Count == 0)
            {
                _isValidPlacement = false;
                return;
            }

            // Check if area is free
            bool areaFree = townManager.IsAreaFree(_ghostCells, previewLevel);

            // Check adjacency requirement if enabled
            bool meetsAdjacency = true;
            if (requireAdjacency && _selectedBlueprint != null && _selectedBlueprint.RequireEdgeAdjacency)
            {
                meetsAdjacency = CheckAdjacencyRequirement(_ghostCells, previewLevel);
            }

            _isValidPlacement = areaFree && meetsAdjacency;

            // Register ghost as preview platform:
            // - Does NOT mark cells as Occupied (players can still place here)
            // - DOES participate in adjacency calculations (enables railing preview)
            // This allows players to see how railings will look when platform is placed
            if (townManager != null && _ghostPlatformComponent != null)
                townManager.RegisterPreviewPlatform(_ghostPlatformComponent, previewLevel);
        }

        private bool CheckAdjacencyRequirement(List<Vector2Int> cells, int level)
        {
            if (cells == null || cells.Count == 0) return false;

            // If there are no existing platforms, first platform is always valid
            if (GamePlatform.AllPlatforms.Count == 0) return true;

            // Check if any cell is adjacent (shares an edge) with an occupied cell
            foreach (var cell in cells)
            {
                Vector3Int gridCell = new Vector3Int(cell.x, cell.y, level);
                var neighbors = new List<Vector3Int>();
                grid.GetNeighbors4(gridCell, neighbors);

                foreach (var neighbor in neighbors)
                {
                    if (grid.CellHasAnyFlag(neighbor, WorldGrid.CellFlag.Occupied))
                        return true; // Found an adjacent occupied cell
                }
            }

            return false; // No adjacent platforms found
        }

        private void UpdateGhostVisuals()
        {
            if (_ghostRenderers.Count == 0) return;

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
            if (_ghostPlatform == null || !_isValidPlacement) return;

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

            _currentRotation += rotationStep;
            if (_currentRotation >= 360f) _currentRotation -= 360f;

            _ghostPlatform.transform.rotation = Quaternion.Euler(0f, _currentRotation, 0f);
        }

        private void OnRotateCCWPerformed(InputAction.CallbackContext context)
        {
            if (_ghostPlatform == null) return;

            float rotationStep = _selectedBlueprint != null && _selectedBlueprint.RotStep == PlatformBlueprint.RotationStep.Deg45 
                ? 45f 
                : 90f;

            _currentRotation -= rotationStep;
            if (_currentRotation < 0f) _currentRotation += 360f;

            _ghostPlatform.transform.rotation = Quaternion.Euler(0f, _currentRotation, 0f);
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

            // Instantiate the runtime prefab
            GameObject placed = Instantiate(
                _selectedBlueprint.RuntimePrefab,
                _currentPlacementPosition,
                Quaternion.Euler(0f, _currentRotation, 0f)
            );

            placed.name = _selectedBlueprint.DisplayName;

            // Get GamePlatform component and register it
            var platformComponent = placed.GetComponent<GamePlatform>();
            if (platformComponent != null && townManager != null)
            {
                // Register as permanent platform (marks cells as Occupied)
                // TownManager will handle NavMesh building via event subscription
                townManager.RegisterPlatform(platformComponent, _ghostCells, previewLevel, markOccupiedInGrid: true);
            }
            else
            {
                Debug.LogWarning("[BuildModeManager] Placed platform has no GamePlatform component!");
            }

            Debug.Log($"[BuildModeManager] Placed {_selectedBlueprint.DisplayName} at {_currentPlacementPosition}");

            // Keep ghost active for continued placement
            // (User can cancel with Cancel action or select different blueprint)
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
