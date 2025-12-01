using System.Collections.Generic;
using UnityEngine;

namespace WaterTown.Platforms
{
    [DisallowMultipleComponent]
    public class PlatformModule : MonoBehaviour
    {
        [Header("Size (meters on 1m grid)")]
        [Min(1)] public int sizeAlongMeters = 2;   // occupies exactly sizeAlongMeters segments
        [Min(1)] public int sizeInwardMeters = 1;  // reserved for future (depth), not used for sockets

        [Header("Behavior")]
        public bool isCornerModule = false; // true: occupy nearest corner socket only (no-op with segment-only sockets)
        public bool blocksLink = false;     // true & active => socket becomes Occupied after Refresh

        public enum EdgeOverride { Auto, North, East, South, West }

        [Header("Attachment")]
        [Tooltip("Lock to a specific edge if pivot proximity would otherwise choose the wrong edge.")]
        public EdgeOverride attachEdge = EdgeOverride.Auto;

        [SerializeField, HideInInspector] private List<int> _boundSocketIndices = new List<int>();
        [SerializeField, HideInInspector] private bool _isHidden;

        private GamePlatform _platform;

        public IReadOnlyList<int> BoundSocketIndices => _boundSocketIndices;
        public bool IsHidden => _isHidden;

        private void Awake()
        {
            _platform = GetComponentInParent<GamePlatform>();
            if (!_platform)
                Debug.LogWarning($"[{nameof(PlatformModule)}] No GamePlatform parent for '{name}'.");
        }

        private void OnEnable()
        {
            if (IsEditingPrefab()) return;
            if (!_platform) { Awake(); if (!_platform) return; }
            RebindAndRegister();
            ApplyVisibilityImmediate();
            _platform.RefreshSocketStatuses();
        }

        private void OnDisable()
        {
            if (IsEditingPrefab()) return;
            if (_platform) { _platform.UnregisterModule(gameObject); _platform.RefreshSocketStatuses(); }
        }

        private void OnDestroy()
        {
            if (IsEditingPrefab()) return;
            if (_platform) { _platform.UnregisterModule(gameObject); _platform.RefreshSocketStatuses(); }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            sizeAlongMeters  = Mathf.Max(1, sizeAlongMeters);
            sizeInwardMeters = Mathf.Max(1, sizeInwardMeters);
            if (IsEditingPrefab()) return;

            if (_platform && isActiveAndEnabled)
            {
                _platform.UnregisterModule(gameObject);
                RebindAndRegister();
                ApplyVisibilityImmediate();
                _platform.RefreshSocketStatuses();
            }
        }
#endif

        public void EnsureRegistered()
        {
            if (IsEditingPrefab()) return;
            if (!_platform) _platform = GetComponentInParent<GamePlatform>();
            if (!_platform) return;

            _platform.UnregisterModule(gameObject);
            if (!enabled) enabled = true;

            RebindAndRegister();
            ApplyVisibilityImmediate();
            _platform.RefreshSocketStatuses();
        }

        // ---------- Binding ----------
        private void RebindAndRegister()
        {
            _boundSocketIndices = ComputeSocketIndices(_platform);
            if (_boundSocketIndices.Count > 0)
                _platform.RegisterModuleOnSockets(gameObject, occupiesSockets: true, _boundSocketIndices);
        }

        private List<int> ComputeSocketIndices(GamePlatform platform)
        {
            var socketIndices = new List<int>();
            if (!platform) return socketIndices;

            // Corner modules no longer have dedicated corner sockets; this path will simply do nothing.
            if (isCornerModule)
            {
                int nearestCornerIndex = FindNearestCornerSocketIndex(platform);
                if (nearestCornerIndex >= 0) socketIndices.Add(nearestCornerIndex);
                return socketIndices;
            }

            // Choose edge (override or nearest)
            Vector3 localPosition = platform.transform.InverseTransformPoint(transform.position);
            float halfWidth = platform.Footprint.x * 0.5f;
            float halfLength = platform.Footprint.y * 0.5f;

            float distanceToNorth = Mathf.Abs(localPosition.z - (+halfLength));
            float distanceToSouth = Mathf.Abs(localPosition.z - (-halfLength));
            float distanceToEast = Mathf.Abs(localPosition.x - (+halfWidth));
            float distanceToWest = Mathf.Abs(localPosition.x - (-halfWidth));

            GamePlatform.Edge selectedEdge;
            bool edgeUsesXAxis;
            float baseCoordinate;
            int edgeLengthInSegments;

            if (attachEdge != EdgeOverride.Auto)
            {
                switch (attachEdge)
                {
                    case EdgeOverride.North: 
                        selectedEdge = GamePlatform.Edge.North; 
                        edgeUsesXAxis = true;  
                        baseCoordinate = -halfWidth; 
                        edgeLengthInSegments = platform.Footprint.x; 
                        break;
                    case EdgeOverride.South: 
                        selectedEdge = GamePlatform.Edge.South; 
                        edgeUsesXAxis = true;  
                        baseCoordinate = -halfWidth; 
                        edgeLengthInSegments = platform.Footprint.x; 
                        break;
                    case EdgeOverride.East:  
                        selectedEdge = GamePlatform.Edge.East;  
                        edgeUsesXAxis = false; 
                        baseCoordinate = -halfLength; 
                        edgeLengthInSegments = platform.Footprint.y; 
                        break;
                    default:                 
                        selectedEdge = GamePlatform.Edge.West;  
                        edgeUsesXAxis = false; 
                        baseCoordinate = -halfLength; 
                        edgeLengthInSegments = platform.Footprint.y; 
                        break;
                }
            }
            else
            {
                // Find nearest edge by distance
                if (distanceToNorth <= distanceToSouth && distanceToNorth <= distanceToEast && distanceToNorth <= distanceToWest) 
                { 
                    selectedEdge = GamePlatform.Edge.North; 
                    edgeUsesXAxis = true;  
                    baseCoordinate = -halfWidth; 
                    edgeLengthInSegments = platform.Footprint.x; 
                }
                else if (distanceToSouth <= distanceToNorth && distanceToSouth <= distanceToEast && distanceToSouth <= distanceToWest) 
                { 
                    selectedEdge = GamePlatform.Edge.South; 
                    edgeUsesXAxis = true;  
                    baseCoordinate = -halfWidth; 
                    edgeLengthInSegments = platform.Footprint.x; 
                }
                else if (distanceToEast <= distanceToWest)                         
                { 
                    selectedEdge = GamePlatform.Edge.East;  
                    edgeUsesXAxis = false; 
                    baseCoordinate = -halfLength; 
                    edgeLengthInSegments = platform.Footprint.y; 
                }
                else                                       
                { 
                    selectedEdge = GamePlatform.Edge.West;  
                    edgeUsesXAxis = false; 
                    baseCoordinate = -halfLength; 
                    edgeLengthInSegments = platform.Footprint.y; 
                }
            }

            float coordinateOnEdge = edgeUsesXAxis ? localPosition.x : localPosition.z;
            int totalEdgeSegments = platform.EdgeLengthMeters(selectedEdge); // same as edgeLengthInSegments

            // We want exactly sizeAlongMeters contiguous segment indices 0..totalEdgeSegments-1
            float normalizedPosition = coordinateOnEdge - baseCoordinate;  // 0..totalEdgeSegments
            int centerSegmentIndex = Mathf.Clamp(Mathf.RoundToInt(normalizedPosition - 0.5f), 0, Mathf.Max(0, totalEdgeSegments - 1));

            int moduleSizeInSegments = Mathf.Max(1, sizeAlongMeters);
            int startSegmentIndex = Mathf.Clamp(centerSegmentIndex - moduleSizeInSegments / 2, 0, Mathf.Max(0, totalEdgeSegments - moduleSizeInSegments));
            int endSegmentIndex = startSegmentIndex + (moduleSizeInSegments - 1); // inclusive

            for (int segmentIndex = startSegmentIndex; segmentIndex <= endSegmentIndex; segmentIndex++)
            {
                int socketIndex = platform.GetSocketIndexByEdgeMark(selectedEdge, segmentIndex);
                if (!socketIndices.Contains(socketIndex)) socketIndices.Add(socketIndex);
            }

            return socketIndices;
        }

        private int FindNearestCornerSocketIndex(GamePlatform platform)
        {
            // With new segment-only sockets, there are no true corner sockets.
            // This remains for backward compatibility; it will usually return -1.
            int bestSocketIndex = -1; 
            float bestDistance = float.MaxValue;
            var sockets = platform.Sockets;
            Vector3 moduleWorldPosition = transform.position;
            
            for (int socketIndex = 0; socketIndex < sockets.Count; socketIndex++)
            {
                var socket = sockets[socketIndex];
                if (socket.Location != GamePlatform.SocketLocation.Corner) continue;
                float distance = Vector3.Distance(moduleWorldPosition, platform.GetSocketWorldPosition(socketIndex));
                if (distance < bestDistance) 
                { 
                    bestDistance = distance; 
                    bestSocketIndex = socketIndex; 
                }
            }
            return bestSocketIndex;
        }

        // ---------- Visibility ----------
        public void Hide() => SetHidden(true);
        public void Show() => SetHidden(false);

        public void SetHidden(bool hidden)
        {
            _isHidden = hidden;
            ApplyVisibilityImmediate();
        }

        /// <summary>
        /// Hidden = GameObject inactive (NOT destroyed).
        /// This matches the previous behavior the system relied on.
        /// </summary>
        private void ApplyVisibilityImmediate()
        {
            bool shouldBeActive = !_isHidden;
            if (gameObject.activeSelf != shouldBeActive)
                gameObject.SetActive(shouldBeActive);
        }

        private static bool IsEditingPrefab()
        {
#if UNITY_EDITOR
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null;
#else
            return false;
#endif
        }
    }
}

            // With new segment-only sockets, there are no true corner sockets.
            // This remains for backward compatibility; it will usually return -1.
            int bestSocketIndex = -1; 
            float bestDistance = float.MaxValue;
            var sockets = platform.Sockets;
            Vector3 moduleWorldPosition = transform.position;
            
            for (int socketIndex = 0; socketIndex < sockets.Count; socketIndex++)
            {
                var socket = sockets[socketIndex];
                if (socket.Location != GamePlatform.SocketLocation.Corner) continue;
                float distance = Vector3.Distance(moduleWorldPosition, platform.GetSocketWorldPosition(socketIndex));
                if (distance < bestDistance) 
                { 
                    bestDistance = distance; 
                    bestSocketIndex = socketIndex; 
                }
            }
            return bestSocketIndex;
        }

        // ---------- Visibility ----------
        public void Hide() => SetHidden(true);
        public void Show() => SetHidden(false);

        public void SetHidden(bool hidden)
        {
            _isHidden = hidden;
            ApplyVisibilityImmediate();
        }

        /// <summary>
        /// Hidden = GameObject inactive (NOT destroyed).
        /// This matches the previous behavior the system relied on.
        /// </summary>
        private void ApplyVisibilityImmediate()
        {
            bool shouldBeActive = !_isHidden;
            if (gameObject.activeSelf != shouldBeActive)
                gameObject.SetActive(shouldBeActive);
        }

        private static bool IsEditingPrefab()
        {
#if UNITY_EDITOR
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null;
#else
            return false;
#endif
        }
    }
}

            // With new segment-only sockets, there are no true corner sockets.
            // This remains for backward compatibility; it will usually return -1.
            int bestSocketIndex = -1; 
            float bestDistance = float.MaxValue;
            var sockets = platform.Sockets;
            Vector3 moduleWorldPosition = transform.position;
            
            for (int socketIndex = 0; socketIndex < sockets.Count; socketIndex++)
            {
                var socket = sockets[socketIndex];
                if (socket.Location != GamePlatform.SocketLocation.Corner) continue;
                float distance = Vector3.Distance(moduleWorldPosition, platform.GetSocketWorldPosition(socketIndex));
                if (distance < bestDistance) 
                { 
                    bestDistance = distance; 
                    bestSocketIndex = socketIndex; 
                }
            }
            return bestSocketIndex;
        }

        // ---------- Visibility ----------
        public void Hide() => SetHidden(true);
        public void Show() => SetHidden(false);

        public void SetHidden(bool hidden)
        {
            _isHidden = hidden;
            ApplyVisibilityImmediate();
        }

        /// <summary>
        /// Hidden = GameObject inactive (NOT destroyed).
        /// This matches the previous behavior the system relied on.
        /// </summary>
        private void ApplyVisibilityImmediate()
        {
            bool shouldBeActive = !_isHidden;
            if (gameObject.activeSelf != shouldBeActive)
                gameObject.SetActive(shouldBeActive);
        }

        private static bool IsEditingPrefab()
        {
#if UNITY_EDITOR
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null;
#else
            return false;
#endif
        }
    }
}
