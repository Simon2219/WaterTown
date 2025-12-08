using System;
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
    private PlayerInput playerInputComponent; // Reference for Input Actions
    private Vector2 currentMoveInput;
    
    private float verticalVelocitySmoothing; // Used by SmoothDamp for vertical movement
    private Vector3 horizontalVelocitySmoothing; // Used by SmoothDamp for horizontal movement

    
    
    
    
    
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
        playerInputComponent = GetComponent<PlayerInput>();

        if (moveAction == null)
        {
            throw ErrorHandler.MissingDependency("Move Action (InputActionReference)", this);
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
        currentMoveInput = context.ReadValue<Vector2>();
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
        Vector3 relativeMovement = (camForward * currentMoveInput.y + camRight * currentMoveInput.x) * effectiveSpeed;
        
        // Determine the current horizontal position (ignoring vertical).
        Vector3 currentHorizontalPosition = new Vector3(currentPos.x, 0f, currentPos.z);
        Vector3 targetHorizontalPosition = currentHorizontalPosition + relativeMovement * Time.deltaTime;
        
        // Smoothly damp towards the target position.
        Vector3 smoothedHorizontalPosition = Vector3.SmoothDamp(currentHorizontalPosition, targetHorizontalPosition, ref horizontalVelocitySmoothing, movementSmoothTime);
        
        // Update the Focus Object's position (preserving the original Y value).
        transform.position = new Vector3(smoothedHorizontalPosition.x, currentPos.y, smoothedHorizontalPosition.z);
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
            float targetHeight = hit.point.y + heightOffset;
            float smoothedHeight = Mathf.SmoothDamp(currentPos.y, targetHeight, ref verticalVelocitySmoothing, heightSmoothTime);
            transform.position = new Vector3(currentPos.x, smoothedHeight, currentPos.z);
        }
    }
}
