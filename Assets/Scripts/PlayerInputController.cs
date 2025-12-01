using UnityEngine;
using UnityEngine.InputSystem;

namespace WaterTown.Building.UI
{
    /// <summary>
    /// Lives on the Player object.
    /// Manages which action maps are enabled for gameplay vs build mode:
    /// • Global: always on (ToggleBuildUI)
    /// • Player: gameplay actions (Select/Context/OpenMenu) – ON in gameplay, OFF in build mode
    /// • BuildMode: rotate/place/cancel – OFF in gameplay, ON in build mode
    /// • Camera: always on (camera movement in both modes)
    ///
    /// Also routes Global/ToggleBuildUI to GameUIController.
    /// </summary>
    public class PlayerInputController : MonoBehaviour
    {
        [Header("Input Asset")]
        [Tooltip("Your baseControls InputActionAsset.")]
        [SerializeField] private InputActionAsset baseControls;

        [Header("Map Names")]
        [SerializeField] private string globalMapName   = "Global";
        [SerializeField] private string playerMapName   = "Player";
        [SerializeField] private string buildModeMapName = "BuildMode";
        [SerializeField] private string cameraMapName   = "Camera";
        [SerializeField] private string uiMapName       = "UI";   // used by EventSystem, not toggled here

        [Header("Global Actions")]
        [Tooltip("Name of the action in Global that toggles the build UI (e.g., ToggleBuildUI).")]
        [SerializeField] private string toggleBuildUIActionName = "ToggleBuildUI";

        [Header("Central UI")]
        [SerializeField] private GameUIController gameUI;

        // runtime maps/actions
        private InputActionMap _mapGlobal;
        private InputActionMap _mapPlayer;
        private InputActionMap _mapBuildMode;
        private InputActionMap _mapCamera;
        private InputAction    _actToggleBuildUI;

        private void Awake()
        {
            if (baseControls == null)
            {
                Debug.LogError("[PlayerInputController] baseControls is not assigned.", this);
                enabled = false;
                return;
            }
            if (gameUI == null)
            {
                Debug.LogError("[PlayerInputController] GameUIController reference is missing.", this);
                enabled = false;
                return;
            }

            _mapGlobal    = baseControls.FindActionMap(globalMapName,    throwIfNotFound: false);
            _mapPlayer    = baseControls.FindActionMap(playerMapName,    throwIfNotFound: false);
            _mapBuildMode = baseControls.FindActionMap(buildModeMapName, throwIfNotFound: false);
            _mapCamera    = baseControls.FindActionMap(cameraMapName,    throwIfNotFound: false);

            if (_mapGlobal == null)    { Debug.LogError($"[PlayerInputController] Global map '{globalMapName}' not found.", this);    enabled = false; return; }
            if (_mapPlayer == null)    { Debug.LogError($"[PlayerInputController] Player map '{playerMapName}' not found.", this);    enabled = false; return; }
            if (_mapBuildMode == null) { Debug.LogError($"[PlayerInputController] BuildMode map '{buildModeMapName}' not found.", this); enabled = false; return; }
            if (_mapCamera == null)    { Debug.LogError($"[PlayerInputController] Camera map '{cameraMapName}' not found.", this);    enabled = false; return; }

            _actToggleBuildUI = _mapGlobal.FindAction(toggleBuildUIActionName, throwIfNotFound: false);
            if (_actToggleBuildUI == null)
            {
                Debug.LogError($"[PlayerInputController] Action '{toggleBuildUIActionName}' not found in Global map.", this);
                enabled = false;
                return;
            }
        }

        private void OnEnable()
        {
            if (baseControls == null) return; // Safety check (Awake should have disabled component)

            baseControls.Enable();

            // Always-on maps
            _mapGlobal?.Enable();   // ToggleBuildUI, etc.
            _mapCamera?.Enable();   // camera controls

            // Start in "gameplay" mode:
            _mapPlayer?.Enable();        // Select/Context/OpenMenu
            _mapBuildMode?.Disable();    // rotate/place/cancel inactive

            if (_actToggleBuildUI != null)
                _actToggleBuildUI.performed += OnToggleBuildUI;
        }

        private void OnDisable()
        {
            if (_actToggleBuildUI != null)
                _actToggleBuildUI.performed -= OnToggleBuildUI;
        }

        private void OnToggleBuildUI(InputAction.CallbackContext ctx)
        {
            if (gameUI == null || _mapPlayer == null || _mapBuildMode == null) return;

            bool enteringBuildMode = !gameUI.IsBuildBarVisible;

            gameUI.ToggleBuildBar();

            if (enteringBuildMode)
            {
                // Gameplay → BuildMode
                _mapPlayer.Disable();      // E no longer triggers OpenMenu
                _mapBuildMode.Enable();    // E now triggers RotateCW
            }
            else
            {
                // BuildMode → Gameplay
                _mapBuildMode.Disable();   // E no longer triggers RotateCW
                _mapPlayer.Enable();       // E is back to OpenMenu
            }
        }
    }
}
