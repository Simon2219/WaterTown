using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace WaterTown.Building.UI
{
    /// <summary>
    /// Manages input action maps for gameplay vs build mode.
    /// • Global: always on (ToggleBuildUI)
    /// • Player: gameplay actions – ON in gameplay, OFF in build mode
    /// • BuildMode: build actions – OFF in gameplay, ON in build mode
    /// • Camera: always on (camera movement in both modes)
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

        [Header("Global Actions")]
        [Tooltip("Name of the action in Global that toggles the build UI.")]
        [SerializeField] private string toggleBuildUIActionName = "ToggleBuildUI";

        [Header("References")]
        [SerializeField] private GameUIController gameUI;

        [Header("Events")]
        [Tooltip("Invoked when entering build mode.")]
        public UnityEvent OnBuildModeEntered = new UnityEvent();
        
        [Tooltip("Invoked when exiting build mode.")]
        public UnityEvent OnBuildModeExited = new UnityEvent();
        
        public bool IsInBuildMode { get; private set; }

        // runtime maps/actions
        private InputActionMap _mapGlobal;
        private InputActionMap _mapPlayer;
        private InputActionMap _mapBuildMode;
        private InputActionMap _mapCamera;
        private InputAction    _actToggleBuildUI;

        private void Awake()
        {
            if (!ValidateReferences())
            {
                enabled = false;
                return;
            }

            if (!InitializeInputMaps())
            {
                enabled = false;
                return;
            }
        }

        private bool ValidateReferences()
        {
            if (baseControls == null)
            {
                Debug.LogError("[PlayerInputController] baseControls InputActionAsset is not assigned.", this);
                return false;
            }
            
            if (gameUI == null)
            {
                Debug.LogError("[PlayerInputController] GameUIController reference is missing.", this);
                return false;
            }

            return true;
        }

        private bool InitializeInputMaps()
        {
            _mapGlobal    = baseControls.FindActionMap(globalMapName,    throwIfNotFound: false);
            _mapPlayer    = baseControls.FindActionMap(playerMapName,    throwIfNotFound: false);
            _mapBuildMode = baseControls.FindActionMap(buildModeMapName, throwIfNotFound: false);
            _mapCamera    = baseControls.FindActionMap(cameraMapName,    throwIfNotFound: false);

            // Validate all maps exist
            bool allValid = true;
            if (_mapGlobal == null)    { Debug.LogError($"[PlayerInputController] Action map '{globalMapName}' not found.", this);    allValid = false; }
            if (_mapPlayer == null)    { Debug.LogError($"[PlayerInputController] Action map '{playerMapName}' not found.", this);    allValid = false; }
            if (_mapBuildMode == null) { Debug.LogError($"[PlayerInputController] Action map '{buildModeMapName}' not found.", this); allValid = false; }
            if (_mapCamera == null)    { Debug.LogError($"[PlayerInputController] Action map '{cameraMapName}' not found.", this);    allValid = false; }

            if (!allValid) return false;

            // Find toggle action
            _actToggleBuildUI = _mapGlobal.FindAction(toggleBuildUIActionName, throwIfNotFound: false);
            if (_actToggleBuildUI == null)
            {
                Debug.LogError($"[PlayerInputController] Action '{toggleBuildUIActionName}' not found in '{globalMapName}' map.", this);
                return false;
            }

            return true;
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
                EnterBuildMode();
            }
            else
            {
                ExitBuildMode();
            }
        }

        private void EnterBuildMode()
        {
            _mapPlayer?.Disable();
            _mapBuildMode?.Enable();
            IsInBuildMode = true;
            OnBuildModeEntered?.Invoke();
        }

        private void ExitBuildMode()
        {
            _mapBuildMode?.Disable();
            _mapPlayer?.Enable();
            IsInBuildMode = false;
            OnBuildModeExited?.Invoke();
        }

        /// <summary>
        /// Public API to programmatically enter build mode.
        /// </summary>
        public void SetBuildMode(bool buildModeActive)
        {
            if (buildModeActive && !IsInBuildMode)
            {
                EnterBuildMode();
            }
            else if (!buildModeActive && IsInBuildMode)
            {
                ExitBuildMode();
            }
        }
    }
}
