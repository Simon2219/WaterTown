using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;
using UnityEngine.Serialization;

[RequireComponent(typeof(PlayerInput))]
public class CameraMovementController : MonoBehaviour
{
    //────────────────────────────────────────────
    // Input Actions
    //────────────────────────────────────────────
    [Header("Input Actions")]
    
    [Tooltip("Horizontal movement input (WASD/Arrow keys).")]
    public InputActionReference moveAction;

    
    
    //────────────────────────────────────────────
    // Cinemachine Camera Reference
    //────────────────────────────────────────────
    [Header("Cinemachine")]
    [Tooltip("Assign the Cinemachine camera used to determine movement direction and zoom.")]
    public CinemachineCamera cinemachineCamera;
    
    
    
    //────────────────────────────────────────────
    // Horizontal Movement Settings
    //────────────────────────────────────────────
    [Header("Horizontal Movement")]
    
    [Tooltip("Base movement speed. (Units per second)")]
    public float moveSpeed = 10f;
    
    [Tooltip("Smooth horizontal movement. Time (in seconds)")]
    public float movementSmoothTime = 0.1f;
    
    [Tooltip("Scales movement speed based on zoom distance.")]
    public float moveSpeedZoomScale = 0.1f;

    
    
    //────────────────────────────────────────────
    // Vertical Adjustment Settings
    //────────────────────────────────────────────
    [Header("Focus Object Vertical Decollision")]
    
    [Tooltip("Additional height above ground/building surface.")]
    public float heightOffset = 2f;
    
    [Tooltip("Smooth vertical adjustments. Time (in seconds)")]
    public float heightSmoothTime = 0.2f;
    
    [Tooltip("Height above Focus Object from which to start downward raycast.")]
    [SerializeField] private float raycastOriginHeight = 50f;
    
    [Tooltip("Maximum distance for the downward raycast.")]
    [SerializeField] private float raycastDistance = 100f;
    
    [Tooltip("Layer mask for ground/buildings to follow.")]
    public LayerMask collisionLayers;
    
    

    //────────────────────────────────────────────
    // Private Variables
    //────────────────────────────────────────────
    private PlayerInput playerInput; //Reference for Input Actions
    private Vector2 moveInput;
    
    private float verticalVelocity; // used by SmoothDamp
    private Vector3 horizontalVelocity; // used by SmoothDamp

    
    
    
    
    
    private void Awake()
    {
        playerInput = GetComponent<PlayerInput>();

        if (moveAction == null)
        {
            Debug.LogError("No Move Action assigned. RETURN");
            return;
        }
        
        // If no Cinemachine camera has been assigned, try to find one among the children.
        if (cinemachineCamera == null)
        {
            CinemachineCamera vcam = GetComponentInChildren<CinemachineCamera>();
            if (vcam != null)
            {
                cinemachineCamera = vcam;
            }
            else
            {
                Debug.LogWarning("No Cinemachine camera assigned or found in children. RETURN");
                return;
            }
        }
    }

    
    
    private void OnEnable()
    {
        moveAction.action.Enable();
        moveAction.action.performed += OnMove;
        moveAction.action.canceled += OnMove;
    }

    
    
    private void OnDisable()
    {
        moveAction.action.performed -= OnMove;
        moveAction.action.canceled -= OnMove;
        moveAction.action.Disable();
    }

    
    
    private void Update()
    {
        ProcessHorizontalMovement();
        
        ProcessVerticalAdjustment();
    }

    
    
    private void OnMove(InputAction.CallbackContext context)
    {
        moveInput = context.ReadValue<Vector2>();
    }
    
    
    
        /// <summary>
        /// Processes horizontal movement relative to the assigned Cinemachine camera's orientation.
        /// The effective move speed scales based on the zoom distance.
        /// </summary>
        private void ProcessHorizontalMovement()
        {
            // Safety check - return if camera is not available
            if (cinemachineCamera == null) return;

            // Cache the current transform position.
            Vector3 currentPos = transform.position;

            // Calculate effective move speed based on the zoom distance.
            float zoomDistance = Vector3.Distance(currentPos, cinemachineCamera.transform.position);
            float effectiveSpeed = moveSpeed * (1f + zoomDistance * moveSpeedZoomScale);
            
            // Get the camera's forward and right vectors projected onto the horizontal plane.
            Vector3 camForward = Vector3.ProjectOnPlane(cinemachineCamera.transform.forward, Vector3.up).normalized;
            Vector3 camRight = Vector3.ProjectOnPlane(cinemachineCamera.transform.right, Vector3.up).normalized;

        // Calculate the desired movement direction relative to the camera.
        Vector3 relativeMovement = (camForward * moveInput.y + camRight * moveInput.x) * effectiveSpeed;
        
        // Determine the current horizontal position (ignoring vertical).
        Vector3 currentCameraPos = new Vector3(currentPos.x, 0f, currentPos.z);
        Vector3 targetCameraPos = currentCameraPos + relativeMovement * Time.deltaTime;
        
        // Smoothly damp towards the target position.
        Vector3 newCameraPos = Vector3.SmoothDamp(currentCameraPos, targetCameraPos, ref horizontalVelocity, movementSmoothTime);
        
        // Update the Focus Object's position (preserving the original Y value).
        transform.position = new Vector3(newCameraPos.x, currentPos.y, newCameraPos.z);
    }


    /// <summary>
    /// Adjusts the pivot's vertical position to remain above ground/buildings.
    /// </summary>
    private void ProcessVerticalAdjustment()
    {
        // Cache the current transform position.
        Vector3 currentPos = transform.position;
        Vector3 rayOrigin = currentPos + Vector3.up * raycastOriginHeight;
    
        // Cast a ray downward from the adjusted ray origin.
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, raycastDistance, collisionLayers))
        {
            float targetY = hit.point.y + heightOffset;
            float newY = Mathf.SmoothDamp(currentPos.y, targetY, ref verticalVelocity, heightSmoothTime);
            transform.position = new Vector3(currentPos.x, newY, currentPos.z);
        }
    }
}
