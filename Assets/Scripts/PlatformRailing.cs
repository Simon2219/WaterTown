using UnityEngine;

namespace WaterTown.Platforms
{
    [DisallowMultipleComponent]
    public class PlatformRailing : MonoBehaviour
    {
        public enum RailingType { Post, Rail }

        [Header("Binding")]
        public RailingType type = RailingType.Rail;

        [Tooltip("Owning platform (auto-filled from parent).")]
        public GamePlatform platform;

        [Tooltip("Indices of sockets this piece is associated with on its platform.")]
        [SerializeField] private int[] socketIndices = System.Array.Empty<int>();

        private bool _registered;
        private bool _isHidden;

        public int[] SocketIndices => socketIndices;

        public void SetSocketIndices(int[] indices)
        {
            socketIndices = indices ?? System.Array.Empty<int>();
        }

        private void Awake()
        {
            if (!platform)
                platform = GetComponentInParent<GamePlatform>();
        }

        private void OnEnable()
        {
            EnsureRegistered();
        }

        private void OnDisable()
        {
            if (platform && _registered)
            {
                platform.UnregisterRailing(this);
                _registered = false;
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!platform)
                platform = GetComponentInParent<GamePlatform>();
        }
#endif

        /// <summary>Ensure this railing is known to its GamePlatform (for visibility updates).</summary>
        public void EnsureRegistered()
        {
            if (!platform)
                platform = GetComponentInParent<GamePlatform>();
            if (!platform) return;

            if (_registered)
                platform.UnregisterRailing(this);

            platform.RegisterRailing(this);
            _registered = true;
        }

        /// <summary>
        /// Hidden = GameObject inactive (NOT destroyed).
        /// This matches the previous behavior where railings disappear on connection
        /// and reappear when platforms separate.
        /// </summary>
        public void SetHidden(bool hidden)
        {
            if (_isHidden == hidden) return;
            
            _isHidden = hidden;
            gameObject.SetActive(!hidden);
            
            // Notify platform so it can update visible rail counters (for efficient post visibility)
            if (platform && type == RailingType.Rail)
                platform.OnRailVisibilityChanged(this, hidden);
        }

        public bool IsHidden => _isHidden;
        
        /// <summary>
        /// Updates this railing's visibility based on socket connection state.
        /// Rails: Hidden when ALL their socket indices are Connected.
        /// Posts: Hidden when ALL rails connected to the same sockets are hidden.
        /// </summary>
        public void UpdateVisibility()
        {
            if (!platform) return;
            
            var indices = socketIndices ?? System.Array.Empty<int>();
            if (indices.Length == 0)
            {
                SetHidden(false);
                return;
            }

            // For rails: hide if all sockets are connected
            if (type == RailingType.Rail)
            {
                bool allSocketsConnected = true;
                foreach (int socketIndex in indices)
                {
                    if (!platform.IsSocketConnected(socketIndex))
                    {
                        allSocketsConnected = false;
                        break;
                    }
                }
                SetHidden(allSocketsConnected && indices.Length > 0);
                return;
            }

            // For posts: hide if all rails on the same sockets are hidden
            if (type == RailingType.Post)
            {
                bool hasVisibleRail = platform.HasVisibleRailOnSockets(indices);
                SetHidden(!hasVisibleRail && indices.Length > 0);
            }
        }
    }
}
