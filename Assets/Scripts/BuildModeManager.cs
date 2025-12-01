using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.AI.Navigation;
using WaterTown.Platforms;
using WaterTown.Town;

namespace WaterTown.Building
{
    /// <summary>
    /// Handles: active blueprint selection, ghost preview, snapping, rotation, validation, and placement.
    /// IMPORTANT:
    /// - The ghost is a REAL GamePlatform instance.
    /// - On placement we REUSE the ghost object as the final placed platform
    ///   (so all socket / railing / connection state is preserved).
    /// - After placement we spawn a NEW ghost for multi-place.
    /// </summary>
    public class BuildModeManager : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private Camera mainCamera;
        [SerializeField] private TownManager town;
        [SerializeField] private WaterTown.Building.UI.GameUIController gameUI;

        [Header("Input (quick bindings)")]
        [Tooltip("Rotate clockwise (e.g., R). If null, uses Keyboard.current.rKey.")]
        [SerializeField] private InputActionReference rotateCW;
        [Tooltip("Rotate counter-clockwise (e.g., Q). If null, uses Keyboard.current.qKey.")]
        [SerializeField] private InputActionReference rotateCCW;
        [Tooltip("Place action (e.g., Left Mouse). If null, uses Mouse.current.leftButton.")]
        [SerializeField] private InputActionReference placeAction;
        [Tooltip("Cancel action (e.g., Escape).")]
        [SerializeField] private InputActionReference cancelAction;

        [Header("Ghost Visuals")]
        [Tooltip("Fallback when blueprint has no specific preview material for VALID placement.")]
        [SerializeField] private Material ghostValidMat;
        [Tooltip("Fallback when blueprint has no specific preview material for INVALID placement.")]
        [SerializeField] private Material ghostInvalidMat;
        [Tooltip("Optional extra tint alpha for all renderers (only used if material supports _Color).")]
        [Range(0f, 1f)] public float ghostAlpha = 0.6f;


        // --- runtime ---
        private PlatformBlueprint _activeBlueprint;

        /// <summary>
        /// Current ghost – this is a REAL runtime prefab with GamePlatform, PlatformModule, PlatformRailing, etc.
        /// On placement we REUSE this as the final placed platform.
        /// </summary>
        private GameObject _ghostGO;
        private GamePlatform _ghostPlatform;

        private readonly List<Renderer> _ghostRenderers = new();
        private readonly List<Material> _ghostMaterialInstances = new(); // Track material instances for cleanup
        private int _rotationSteps; // 0..3 (90° steps)
        private readonly List<Vector2Int> _tmpCells = new();

        private void OnEnable()
        {
            if (!mainCamera) mainCamera = Camera.main;
            if (!town || !town.Grid)
            {
                Debug.LogError("[BuildModeManager] Missing TownManager or WorldGrid reference.", this);
                enabled = false;
                return;
            }
            if (!gameUI)
            {
                Debug.LogError("[BuildModeManager] Missing GameUIController reference.", this);
                enabled = false;
                return;
            }

            gameUI.OnBlueprintSelected += OnBlueprintSelected;

            rotateCW?.action.Enable();
            rotateCCW?.action.Enable();
            placeAction?.action.Enable();
            cancelAction?.action.Enable();
        }

        private void OnDisable()
        {
            gameUI.OnBlueprintSelected -= OnBlueprintSelected;

            rotateCW?.action.Disable();
            rotateCCW?.action.Disable();
            placeAction?.action.Disable();
            cancelAction?.action.Disable();

            DestroyGhost();
        }

        private void OnBlueprintSelected(PlatformBlueprint bp)
        {
            _activeBlueprint = bp;
            _rotationSteps = 0;
            RebuildGhost();
        }

        private void Update()
        {
            if (_activeBlueprint == null || _ghostGO == null) return;

            HandleRotationInput();
            UpdateGhostAndMaybePlace();
            HandleCancel();
        }

        // ---------- Input ----------

        private void HandleRotationInput()
        {
            bool cw = rotateCW
                ? rotateCW.action.WasPressedThisFrame()
                : (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame);

            bool ccw = rotateCCW
                ? rotateCCW.action.WasPressedThisFrame()
                : (Keyboard.current != null && Keyboard.current.qKey.wasPressedThisFrame);

            if (!cw && !ccw) return;

            // Simple 90° step rotation: 0 -> 1 -> 2 -> 3 -> 0
            if (cw)  _rotationSteps = (_rotationSteps + 1) & 3; // 0..3
            if (ccw) _rotationSteps = (_rotationSteps + 3) & 3; // 0..3 (adding 3 = subtracting 1 mod 4)
        }

        private void HandleCancel()
        {
            bool cancel = cancelAction
                ? cancelAction.action.WasPressedThisFrame()
                : (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame);

            if (!cancel) return;

            _activeBlueprint = null;
            DestroyGhost();
        }

        // ---------- Ghost handling ----------

        private void RebuildGhost()
        {
            DestroyGhost();
            if (_activeBlueprint == null) return;

            // Prefer dedicated preview prefab, fall back to runtime prefab
            GameObject prefabForGhost =
                _activeBlueprint.PreviewPrefab != null
                    ? _activeBlueprint.PreviewPrefab
                    : _activeBlueprint.RuntimePrefab;

            if (!prefabForGhost)
            {
                Debug.LogWarning($"[BuildModeManager] Blueprint '{_activeBlueprint.name}' has no prefab assigned.", _activeBlueprint);
                return;
            }

            // Important: we need a REAL GamePlatform here, otherwise sockets/railings won't react.
            if (!prefabForGhost.GetComponentInChildren<GamePlatform>(true))
            {
                if (_activeBlueprint.RuntimePrefab != null &&
                    _activeBlueprint.RuntimePrefab.GetComponentInChildren<GamePlatform>(true))
                {
                    prefabForGhost = _activeBlueprint.RuntimePrefab;
                }
                else
                {
                    Debug.LogError($"[BuildModeManager] Prefab for '{_activeBlueprint.DisplayName}' has no GamePlatform component.", _activeBlueprint);
                }
            }

            _ghostGO = Instantiate(prefabForGhost);
            _ghostGO.name = $"{_activeBlueprint.DisplayName}_Ghost";

            _ghostPlatform = _ghostGO.GetComponent<GamePlatform>();
            if (!_ghostPlatform)
            {
                Debug.LogError($"[BuildModeManager] Ghost for '{_activeBlueprint.DisplayName}' has no GamePlatform; cannot preview connections.", _ghostGO);
            }

            _ghostRenderers.Clear();
            _ghostGO.GetComponentsInChildren(true, _ghostRenderers);

            // Make sure the ghost participates in adjacency but does NOT mark grid cells as occupied.
            if (town && town.Grid && _ghostPlatform)
            {
                town.RegisterPreviewPlatform(_ghostPlatform, level: 0);
            }

            // initial state until validity computed
            ApplyGhostMaterial(isValid: false);
        }

        private void DestroyGhost()
        {
            if (_ghostPlatform && town && town.Grid)
            {
                // Remove preview occupancy & adjacency
                town.UnregisterPlatform(_ghostPlatform);
            }

            // Clean up material instances
            foreach (var matInstance in _ghostMaterialInstances)
            {
                if (matInstance) Destroy(matInstance);
            }
            _ghostMaterialInstances.Clear();

            if (_ghostGO)
            {
                Destroy(_ghostGO);
            }

            _ghostGO = null;
            _ghostPlatform = null;
            _ghostRenderers.Clear();
        }

        private void UpdateGhostAndMaybePlace()
        {
            if (!town || !town.Grid) return;
            if (_ghostGO == null || _ghostPlatform == null) return;

            var grid = town.Grid;

            // Ray to grid level 0 (pure plane math; ignores meshes so the ghost can't interfere)
            Vector2 mousePos =
                Mouse.current != null
                    ? Mouse.current.position.ReadValue()
                    : (Vector2)Input.mousePosition;

            var ray = mainCamera.ScreenPointToRay(mousePos);
            Vector3Int levelRef = new Vector3Int(0, 0, 0);

            if (!grid.RaycastToCell(ray, levelRef, out var _ /*cell*/, out var hit))
                return;

            // Stable center-aligned snapping (rotation-aware via _rotationSteps)
            ComputePlacement(hit, out var worldCenter, out var worldRot);

            // position/rotate ghost
            _ghostGO.transform.SetPositionAndRotation(worldCenter, worldRot);

            // Compute footprint cells using the SAME logic as TownManager uses for occupancy
            _tmpCells.Clear();
            town.ComputeCellsForPlatform(_ghostPlatform, level: 0, _tmpCells);

            bool isValid = _tmpCells.Count > 0 
                && town.IsAreaFree(_tmpCells, level: 0)
                && ValidatePlacementRules(_tmpCells, level: 0);

            ApplyGhostMaterial(isValid);

            // confirm place
            bool place =
                placeAction
                    ? placeAction.action.WasPressedThisFrame()
                    : (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame);

            if (place && isValid)
            {
                PlaceNow(worldCenter, worldRot, _tmpCells);
            }
        }

        /// <summary>
        /// Stable grid snapping: snap the platform's CENTER to the nearest grid cell center,
        /// then TownManager.ComputeCellsForPlatform uses the transform+rotation to get footprint.
        /// </summary>
        private void ComputePlacement(
            Vector3 hitWorldOnGridPlane,
            out Vector3 worldCenter,
            out Quaternion worldRot)
        {
            var grid = town.Grid;

            float cellSize = grid.cellSize;
            Vector3 origin = grid.worldOrigin;

            // Convert world to continuous grid coordinates, then snap center to nearest cell center
            float continuousColumn = (hitWorldOnGridPlane.x - origin.x) / cellSize;   // continuous column
            float continuousRow = (hitWorldOnGridPlane.z - origin.z) / cellSize;   // continuous row

            int snappedColumn = Mathf.RoundToInt(continuousColumn);
            int snappedRow = Mathf.RoundToInt(continuousRow);

            // World center from snapped grid coordinates
            float worldX = origin.x + snappedColumn * cellSize;
            float worldZ = origin.z + snappedRow * cellSize;
            float worldY = grid.GetLevelWorldY(0);
            worldCenter = new Vector3(worldX, worldY, worldZ);

            // Rotation (90° steps only)
            worldRot = Quaternion.Euler(0f, _rotationSteps * 90f, 0f);
        }

        /// <summary>
        /// PLACE the platform:
        /// - Reuses the current ghost as the final placed platform
        /// - Registers it with the TownManager (now marking cells as Occupied)
        /// - Keeps all socket / connection / railing state as-is (global adjacency will recompute)
        /// - Spawns a NEW ghost for multi-place.
        /// </summary>
        private void PlaceNow(Vector3 worldCenter, Quaternion worldRot, List<Vector2Int> cells)
        {
            if (_activeBlueprint == null)
            {
                Debug.LogWarning("[BuildModeManager] Cannot place: no active blueprint.", this);
                return;
            }
            if (_ghostGO == null || _ghostPlatform == null)
            {
                Debug.LogWarning("[BuildModeManager] Cannot place: ghost is missing or has no GamePlatform.", this);
                return;
            }

            // Reuse ghost as final placed platform
            var placedGameObject = _ghostGO;
            var placedPlatform = _ghostPlatform;

            // Detach ghost from BuildModeManager control
            _ghostGO = null;
            _ghostPlatform = null;
            _ghostRenderers.Clear();

            placedGameObject.name = _activeBlueprint.DisplayName;
            placedGameObject.transform.SetPositionAndRotation(worldCenter, worldRot);

            // Register this platform in the grid / TownManager as a REAL placed platform
            town.RegisterPlatform(placedPlatform, cells, level: 0, markOccupiedInGrid: true);

            // Let the platform build its local NavMesh if needed
            placedPlatform.QueueRebuild();

            // For multi-place: create a NEW ghost at the next frame using the same blueprint.
            RebuildGhost();
        }

        /// <summary>
        /// Validates placement rules from the active blueprint:
        /// - requireEdgeAdjacency: platform must share an edge with an existing platform
        /// - disallowCornerAdjacency: corner-only (diagonal) contact is insufficient
        /// </summary>
        private bool ValidatePlacementRules(List<Vector2Int> cells, int level)
        {
            if (_activeBlueprint == null) return true; // No rules = always valid
            if (!_activeBlueprint.RequireEdgeAdjacency) return true; // Rule not enabled

            // Check if any existing platform is adjacent
            // Only check platforms that are actually placed (registered with TownManager)
            var existingPlatforms = GamePlatform.AllPlatforms;
            int placedPlatformCount = 0;
            
            // Check if ghost platform is adjacent to any existing platform
            bool hasEdgeAdjacency = false;
            bool hasCornerOnlyAdjacency = false;

            foreach (var existingPlatform in existingPlatforms)
            {
                if (!existingPlatform || existingPlatform == _ghostPlatform) continue;
                if (!existingPlatform.isActiveAndEnabled) continue;
                
                // Check if this platform is actually placed (not just a preview)
                // We can check if it has cells registered in TownManager
                var tmpCheckCells = new List<Vector2Int>();
                town.ComputeCellsForPlatform(existingPlatform, level, tmpCheckCells);
                if (tmpCheckCells.Count == 0) continue; // Skip platforms with no footprint
                
                // Check if platform is actually occupying cells (not just preview)
                bool isPlaced = false;
                foreach (var cell in tmpCheckCells)
                {
                    var gridCell = new Vector3Int(cell.x, cell.y, level);
                    if (town.Grid.CellHasAnyFlag(gridCell, Grid.WorldGrid.CellFlag.Occupied))
                    {
                        isPlaced = true;
                        break;
                    }
                }
                if (!isPlaced) continue; // Skip preview/ghost platforms
                
                placedPlatformCount++;

                // Get cells of existing platform
                var existingCells = new List<Vector2Int>();
                town.ComputeCellsForPlatform(existingPlatform, level, existingCells);

                // Check for edge adjacency (sharing a side)
                foreach (var ghostCell in cells)
                {
                    foreach (var existingCell in existingCells)
                    {
                        // Check if cells share an edge (not just a corner)
                        int deltaX = Mathf.Abs(ghostCell.x - existingCell.x);
                        int deltaY = Mathf.Abs(ghostCell.y - existingCell.y);

                        if (deltaX == 1 && deltaY == 0) // Share horizontal edge
                        {
                            hasEdgeAdjacency = true;
                        }
                        else if (deltaX == 0 && deltaY == 1) // Share vertical edge
                        {
                            hasEdgeAdjacency = true;
                        }
                        else if (deltaX == 1 && deltaY == 1) // Diagonal (corner only)
                        {
                            hasCornerOnlyAdjacency = true;
                        }
                    }
                }

                // Break early if we found edge adjacency
                if (hasEdgeAdjacency) break;
            }

            // First platform placement is always allowed
            if (placedPlatformCount == 0)
                return true;

            // Apply rules
            if (_activeBlueprint.RequireEdgeAdjacency && !hasEdgeAdjacency)
                return false;

            if (_activeBlueprint.DisallowCornerAdjacency && hasCornerOnlyAdjacency && !hasEdgeAdjacency)
                return false;

            return true;
        }

        private void ApplyGhostMaterial(bool isValid)
        {
            if (!_ghostGO) return;

            // Prefer blueprint-defined preview materials, fallback to generic ones
            Material baseMat =
                isValid
                    ? ((_activeBlueprint != null && _activeBlueprint.PreviewValidMaterial != null)
                        ? _activeBlueprint.PreviewValidMaterial
                        : ghostValidMat)
                    : ((_activeBlueprint != null && _activeBlueprint.PreviewInvalidMaterial != null)
                        ? _activeBlueprint.PreviewInvalidMaterial
                        : ghostInvalidMat);

            if (!baseMat) return;

            if (_ghostRenderers.Count == 0)
                _ghostGO.GetComponentsInChildren(true, _ghostRenderers);

            for (int rendererIndex = 0; rendererIndex < _ghostRenderers.Count; rendererIndex++)
            {
                var renderer = _ghostRenderers[rendererIndex];
                if (!renderer) continue;

                // Get current materials (creates instances automatically)
                var instanceMats = renderer.materials;
                
                // Replace each material slot with an instance of the preview material
                for (int materialSlotIndex = 0; materialSlotIndex < instanceMats.Length; materialSlotIndex++)
                {
                    // Destroy old instance if it exists
                    if (instanceMats[materialSlotIndex] != null)
                    {
                        // Check if it's an instance we created (not the original shared material)
                        bool isOurInstance = _ghostMaterialInstances.Contains(instanceMats[materialSlotIndex]);
                        if (!isOurInstance)
                        {
                            // Destroy the auto-created instance Unity made
                            if (Application.isPlaying)
                                Destroy(instanceMats[materialSlotIndex]);
                            else
                                DestroyImmediate(instanceMats[materialSlotIndex]);
                        }
                    }

                    // Create new instance
                    var newInstance = new Material(baseMat);
                    _ghostMaterialInstances.Add(newInstance);

                    // Apply alpha tweak if needed
                    if (ghostAlpha < 0.999f && newInstance.HasProperty("_Color"))
                    {
                        var materialColor = newInstance.color;
                        materialColor.a = ghostAlpha;
                        newInstance.color = materialColor;
                    }

                    instanceMats[materialSlotIndex] = newInstance;
                }
                renderer.materials = instanceMats;
            }
        }
    }
}

                    if (town.Grid.CellHasAnyFlag(gridCell, Grid.WorldGrid.CellFlag.Occupied))
                    {
                        isPlaced = true;
                        break;
                    }
                }
                if (!isPlaced) continue; // Skip preview/ghost platforms
                
                placedPlatformCount++;

                // Get cells of existing platform
                var existingCells = new List<Vector2Int>();
                town.ComputeCellsForPlatform(existingPlatform, level, existingCells);

                // Check for edge adjacency (sharing a side)
                foreach (var ghostCell in cells)
                {
                    foreach (var existingCell in existingCells)
                    {
                        // Check if cells share an edge (not just a corner)
                        int deltaX = Mathf.Abs(ghostCell.x - existingCell.x);
                        int deltaY = Mathf.Abs(ghostCell.y - existingCell.y);

                        if (deltaX == 1 && deltaY == 0) // Share horizontal edge
                        {
                            hasEdgeAdjacency = true;
                        }
                        else if (deltaX == 0 && deltaY == 1) // Share vertical edge
                        {
                            hasEdgeAdjacency = true;
                        }
                        else if (deltaX == 1 && deltaY == 1) // Diagonal (corner only)
                        {
                            hasCornerOnlyAdjacency = true;
                        }
                    }
                }

                // Break early if we found edge adjacency
                if (hasEdgeAdjacency) break;
            }

            // First platform placement is always allowed
            if (placedPlatformCount == 0)
                return true;

            // Apply rules
            if (_activeBlueprint.RequireEdgeAdjacency && !hasEdgeAdjacency)
                return false;

            if (_activeBlueprint.DisallowCornerAdjacency && hasCornerOnlyAdjacency && !hasEdgeAdjacency)
                return false;

            return true;
        }

        private void ApplyGhostMaterial(bool isValid)
        {
            if (!_ghostGO) return;

            // Prefer blueprint-defined preview materials, fallback to generic ones
            Material baseMat =
                isValid
                    ? ((_activeBlueprint != null && _activeBlueprint.PreviewValidMaterial != null)
                        ? _activeBlueprint.PreviewValidMaterial
                        : ghostValidMat)
                    : ((_activeBlueprint != null && _activeBlueprint.PreviewInvalidMaterial != null)
                        ? _activeBlueprint.PreviewInvalidMaterial
                        : ghostInvalidMat);

            if (!baseMat) return;

            if (_ghostRenderers.Count == 0)
                _ghostGO.GetComponentsInChildren(true, _ghostRenderers);

            for (int rendererIndex = 0; rendererIndex < _ghostRenderers.Count; rendererIndex++)
            {
                var renderer = _ghostRenderers[rendererIndex];
                if (!renderer) continue;

                // Get current materials (creates instances automatically)
                var instanceMats = renderer.materials;
                
                // Replace each material slot with an instance of the preview material
                for (int materialSlotIndex = 0; materialSlotIndex < instanceMats.Length; materialSlotIndex++)
                {
                    // Destroy old instance if it exists
                    if (instanceMats[materialSlotIndex] != null)
                    {
                        // Check if it's an instance we created (not the original shared material)
                        bool isOurInstance = _ghostMaterialInstances.Contains(instanceMats[materialSlotIndex]);
                        if (!isOurInstance)
                        {
                            // Destroy the auto-created instance Unity made
                            if (Application.isPlaying)
                                Destroy(instanceMats[materialSlotIndex]);
                            else
                                DestroyImmediate(instanceMats[materialSlotIndex]);
                        }
                    }

                    // Create new instance
                    var newInstance = new Material(baseMat);
                    _ghostMaterialInstances.Add(newInstance);

                    // Apply alpha tweak if needed
                    if (ghostAlpha < 0.999f && newInstance.HasProperty("_Color"))
                    {
                        var materialColor = newInstance.color;
                        materialColor.a = ghostAlpha;
                        newInstance.color = materialColor;
                    }

                    instanceMats[materialSlotIndex] = newInstance;
                }
                renderer.materials = instanceMats;
            }
        }
    }
}

                    if (town.Grid.CellHasAnyFlag(gridCell, Grid.WorldGrid.CellFlag.Occupied))
                    {
                        isPlaced = true;
                        break;
                    }
                }
                if (!isPlaced) continue; // Skip preview/ghost platforms
                
                placedPlatformCount++;

                // Get cells of existing platform
                var existingCells = new List<Vector2Int>();
                town.ComputeCellsForPlatform(existingPlatform, level, existingCells);

                // Check for edge adjacency (sharing a side)
                foreach (var ghostCell in cells)
                {
                    foreach (var existingCell in existingCells)
                    {
                        // Check if cells share an edge (not just a corner)
                        int deltaX = Mathf.Abs(ghostCell.x - existingCell.x);
                        int deltaY = Mathf.Abs(ghostCell.y - existingCell.y);

                        if (deltaX == 1 && deltaY == 0) // Share horizontal edge
                        {
                            hasEdgeAdjacency = true;
                        }
                        else if (deltaX == 0 && deltaY == 1) // Share vertical edge
                        {
                            hasEdgeAdjacency = true;
                        }
                        else if (deltaX == 1 && deltaY == 1) // Diagonal (corner only)
                        {
                            hasCornerOnlyAdjacency = true;
                        }
                    }
                }

                // Break early if we found edge adjacency
                if (hasEdgeAdjacency) break;
            }

            // First platform placement is always allowed
            if (placedPlatformCount == 0)
                return true;

            // Apply rules
            if (_activeBlueprint.RequireEdgeAdjacency && !hasEdgeAdjacency)
                return false;

            if (_activeBlueprint.DisallowCornerAdjacency && hasCornerOnlyAdjacency && !hasEdgeAdjacency)
                return false;

            return true;
        }

        private void ApplyGhostMaterial(bool isValid)
        {
            if (!_ghostGO) return;

            // Prefer blueprint-defined preview materials, fallback to generic ones
            Material baseMat =
                isValid
                    ? ((_activeBlueprint != null && _activeBlueprint.PreviewValidMaterial != null)
                        ? _activeBlueprint.PreviewValidMaterial
                        : ghostValidMat)
                    : ((_activeBlueprint != null && _activeBlueprint.PreviewInvalidMaterial != null)
                        ? _activeBlueprint.PreviewInvalidMaterial
                        : ghostInvalidMat);

            if (!baseMat) return;

            if (_ghostRenderers.Count == 0)
                _ghostGO.GetComponentsInChildren(true, _ghostRenderers);

            for (int rendererIndex = 0; rendererIndex < _ghostRenderers.Count; rendererIndex++)
            {
                var renderer = _ghostRenderers[rendererIndex];
                if (!renderer) continue;

                // Get current materials (creates instances automatically)
                var instanceMats = renderer.materials;
                
                // Replace each material slot with an instance of the preview material
                for (int materialSlotIndex = 0; materialSlotIndex < instanceMats.Length; materialSlotIndex++)
                {
                    // Destroy old instance if it exists
                    if (instanceMats[materialSlotIndex] != null)
                    {
                        // Check if it's an instance we created (not the original shared material)
                        bool isOurInstance = _ghostMaterialInstances.Contains(instanceMats[materialSlotIndex]);
                        if (!isOurInstance)
                        {
                            // Destroy the auto-created instance Unity made
                            if (Application.isPlaying)
                                Destroy(instanceMats[materialSlotIndex]);
                            else
                                DestroyImmediate(instanceMats[materialSlotIndex]);
                        }
                    }

                    // Create new instance
                    var newInstance = new Material(baseMat);
                    _ghostMaterialInstances.Add(newInstance);

                    // Apply alpha tweak if needed
                    if (ghostAlpha < 0.999f && newInstance.HasProperty("_Color"))
                    {
                        var materialColor = newInstance.color;
                        materialColor.a = ghostAlpha;
                        newInstance.color = materialColor;
                    }

                    instanceMats[materialSlotIndex] = newInstance;
                }
                renderer.materials = instanceMats;
            }
        }
    }
}
