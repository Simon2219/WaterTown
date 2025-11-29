using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using WaterTown.Platforms;
using WaterTown.Town;

namespace WaterTown.Building
{
    /// <summary>
    /// Handles: active blueprint selection, ghost preview, snapping, rotation, validation, and placement.
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
        [SerializeField] private Material ghostValidMat;
        [SerializeField] private Material ghostInvalidMat;
        [Tooltip("Optional extra tint alpha for all renderers.")]
        [Range(0f, 1f)] public float ghostAlpha = 0.6f;

        // --- runtime ---
        private PlatformBlueprint _activeBlueprint;
        private GameObject _ghostGO;
        private List<Renderer> _ghostRenderers = new();
        private int _rotationSteps; // 0..3 (90° steps)
        private readonly List<Vector2Int> _tmpCells = new();

        private void OnEnable()
        {
            if (!mainCamera) mainCamera = Camera.main;
            if (!town || !town.Grid)
            {
                Debug.LogError("[BuildModeManager] Missing TownManager or WorldGrid reference.", this);
                enabled = false; return;
            }
            if (!gameUI)
            {
                Debug.LogError("[BuildModeManager] Missing GameUIController reference.", this);
                enabled = false; return;
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

        private void HandleRotationInput()
        {
            bool cw = rotateCW ? rotateCW.action.WasPressedThisFrame()
                               : (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame);
            bool ccw = rotateCCW ? rotateCCW.action.WasPressedThisFrame()
                                 : (Keyboard.current != null && Keyboard.current.qKey.wasPressedThisFrame);

            if (!cw && !ccw) return;

            int step = Mathf.Max(1, (int)_activeBlueprint.RotStep);
            if (cw)  _rotationSteps = (_rotationSteps + (step / 90)) & 3; // 90° steps → 0..3
            if (ccw) _rotationSteps = (_rotationSteps + 4 - (step / 90)) & 3;

            _lastValid = null; // force refresh
        }

        private void HandleCancel()
        {
            bool cancel = cancelAction ? cancelAction.action.WasPressedThisFrame()
                                       : (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame);

            if (cancel)
            {
                _activeBlueprint = null;
                DestroyGhost();
            }
        }

        private void RebuildGhost()
        {
            DestroyGhost();
            if (_activeBlueprint == null || !_activeBlueprint.RuntimePrefab) return;

            _ghostGO = Instantiate(_activeBlueprint.RuntimePrefab);
            _ghostGO.name = $"{_activeBlueprint.DisplayName}_Ghost";

            _ghostRenderers.Clear();
            _ghostGO.GetComponentsInChildren(true, _ghostRenderers);
            ApplyGhostMaterial(isValid: false); // initial state until validity computed
        }

        private void DestroyGhost()
        {
            if (_ghostGO) Destroy(_ghostGO);
            _ghostGO = null;
            _ghostRenderers.Clear();
        }

        private struct PlacementCache
        {
            public Vector3 worldCenter;
            public Quaternion worldRot;
            public List<Vector2Int> cells;
            public bool valid;
        }
        private PlacementCache? _lastValid; // cache to avoid flicker

        private void UpdateGhostAndMaybePlace()
        {
            var grid = town.Grid;

            // Ray to grid level 0 (pure plane math; ignores meshes so the ghost can't interfere)
            Vector3Int levelRef = new Vector3Int(0, 0, 0);
            var ray = mainCamera.ScreenPointToRay(Mouse.current != null ? Mouse.current.position.ReadValue()
                                                                        : (Vector2)Input.mousePosition);
            if (!grid.RaycastToCell(ray, levelRef, out var _ /*cell*/, out var hit))
                return;

            // Stable center-aligned snapping (rotation-aware)
            ComputePlacement(hit, out var worldCenter, out var worldRot, out var cells, out bool isValid);

            // position/rotate ghost
            _ghostGO.transform.SetPositionAndRotation(worldCenter, worldRot);
            ApplyGhostMaterial(isValid);

            // confirm place
            bool place =
                (placeAction ? placeAction.action.WasPressedThisFrame()
                             : (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame));
            if (place && isValid)
            {
                PlaceNow(worldCenter, worldRot, cells);
                // keep ghost alive for multi-place
            }
        }

        /// <summary>
        /// Stable grid snapping: snap the platform's CENTER to the nearest grid cell center,
        /// then derive the start cell from center and (rotated) footprint.
        /// This avoids the "nearest-corner" flip that caused jitter.
        /// </summary>
        private void ComputePlacement(
            Vector3 hitWorldOnGridPlane,
            out Vector3 worldCenter, out Quaternion worldRot,
            out List<Vector2Int> cellsOut, out bool isValid)
        {
            var grid = town.Grid;

            // Rotated footprint
            int w = _activeBlueprint.Footprint.x;
            int h = _activeBlueprint.Footprint.y;
            int steps = _rotationSteps & 3;         // 0..3
            bool swap = (steps % 2) == 1;           // 90 or 270
            int fw = swap ? h : w;                  // width in cells after rotation
            int fh = swap ? w : h;                  // height in cells after rotation

            float cs = grid.cellSize;
            Vector3 origin = grid.worldOrigin;

            // Convert world to continuous grid coords (u,v), then snap center to nearest cell center
            float u = (hitWorldOnGridPlane.x - origin.x) / cs;   // continuous column
            float v = (hitWorldOnGridPlane.z - origin.z) / cs;   // continuous row

            // Snap platform CENTER to nearest cell center
            int cx = Mathf.RoundToInt(u);
            int cy = Mathf.RoundToInt(v);

            // From center cell -> derive start (lower-left) cell
            int startX = cx - (fw / 2);
            int startY = cy - (fh / 2);

            // Fill occupied cells (footprint rectangle)
            _tmpCells.Clear();
            for (int y = 0; y < fh; y++)
                for (int x = 0; x < fw; x++)
                    _tmpCells.Add(new Vector2Int(startX + x, startY + y));

            // Bounds + occupancy checks
            isValid = true;
            for (int i = 0; i < _tmpCells.Count; i++)
            {
                var c = _tmpCells[i];
                var c3 = new Vector3Int(c.x, c.y, 0);
                if (!grid.CellInBounds(c3)) { isValid = false; break; }
            }
            if (isValid)
                isValid = town.IsAreaFree(_tmpCells);

            // World center from snapped (cx,cy)
            float cxWorld = origin.x + cx * cs;
            float czWorld = origin.z + cy * cs;
            float cyWorld = grid.GetLevelWorldY(0);
            worldCenter = new Vector3(cxWorld, cyWorld, czWorld);

            // Rotation
            worldRot = Quaternion.Euler(0f, steps * 90f, 0f);

            // out
            cellsOut = new List<Vector2Int>(_tmpCells);
        }

        private void PlaceNow(Vector3 worldCenter, Quaternion worldRot, List<Vector2Int> cells)
        {
            // Instantiate the real prefab
            var go = Instantiate(_activeBlueprint.RuntimePrefab, worldCenter, worldRot);
            go.name = _activeBlueprint.DisplayName;

            var gp = go.GetComponent<GamePlatform>();
            if (!gp)
            {
                Debug.LogWarning("[BuildModeManager] Placed prefab has no GamePlatform; add it to your prefab.");
            }

            // Bookkeeping
            town.RegisterPlatform(gp, cells);

            // Optional: invoke navmesh build on the new platform
            gp?.BuildLocalNavMesh();
        }

        private void ApplyGhostMaterial(bool isValid)
        {
            var mat = isValid ? ghostValidMat : ghostInvalidMat;
            if (!mat) return;

            if (_ghostRenderers.Count == 0)
                _ghostGO.GetComponentsInChildren(true, _ghostRenderers);

            for (int i = 0; i < _ghostRenderers.Count; i++)
            {
                var r = _ghostRenderers[i];
                if (!r) continue;

                var mats = r.sharedMaterials;
                for (int m = 0; m < mats.Length; m++) mats[m] = mat;
                r.sharedMaterials = mats;
            }
        }
    }
}
