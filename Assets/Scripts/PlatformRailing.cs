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
        }

        public bool IsHidden => _isHidden;
    }
}
