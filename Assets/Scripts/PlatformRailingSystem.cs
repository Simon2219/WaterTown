using System.Collections.Generic;
using UnityEngine;

namespace WaterTown.Platforms
{
    /// <summary>
    /// Manages railing registration, visibility tracking, and updates for a platform
    /// Listens to socket changes and updates railing visibility accordingly
    /// </summary>
    [DisallowMultipleComponent]
    public class PlatformRailingSystem : MonoBehaviour
    {
        #region Dependencies
        
        
        private GamePlatform _platform;
        private PlatformSocketSystem _socketSystem;
        
        
        #endregion
        
        
        
        
        #region Railing Data
        
        
        // Railing registry - maps socket index to railings attached to that socket
        private readonly Dictionary<int, List<PlatformRailing>> _socketToRailings = new();
        
        // Cached list of railings (populated by GamePlatform at initialization)
        private List<PlatformRailing> _cachedRailings;
        
        
        #endregion
        
        
        
        
        #region Initialization
        
        
        /// Called by GamePlatform to inject dependencies
        public void SetDependencies(GamePlatform platform, PlatformSocketSystem socketSystem)
        {
            _platform = platform;
            _socketSystem = socketSystem;
            
            // Note: Event subscriptions are handled by GamePlatform
            // SocketsChanged -> GamePlatform.OnSocketsChanged -> ConnectionsChanged
            // Railing visibility is triggered via RefreshAllRailingsVisibility facade
        }
        
        
        /// Sets the cached railings list (provided by GamePlatform)
        public void SetCachedRailings(List<PlatformRailing> railings)
        {
            _cachedRailings = railings;
        }
        
        
        #endregion
        
        
        
        
        #region Railing Registration
        
        
        /// Called by PlatformRailing to bind itself to given socket indices
        public void RegisterRailing(PlatformRailing railing)
        {
            if (!railing || !_socketSystem) return;

            var indices = railing.SocketIndices;
            if (indices == null || indices.Length == 0)
            {
                // Fallback: bind to nearest socket
                int nearest = _socketSystem.FindNearestSocketIndexLocal(
                    transform.InverseTransformPoint(railing.transform.position));
                if (nearest >= 0)
                {
                    indices = new[] { nearest };
                    railing.SetSocketIndices(indices);
                }
                else
                    return;
            }

            foreach (int sIdx in indices)
            {
                if (!_socketToRailings.TryGetValue(sIdx, out var list))
                {
                    list = new List<PlatformRailing>();
                    _socketToRailings[sIdx] = list;
                }
                if (!list.Contains(railing)) list.Add(railing);
            }
        }


        /// Called by PlatformRailing when disabled/destroyed
        public void UnregisterRailing(PlatformRailing railing)
        {
            if (!railing) return;

            foreach (var kv in _socketToRailings)
            {
                kv.Value.Remove(railing);
            }
        }
        
        
        #endregion
        
        
        
        
        #region Visibility Management
        
        
        /// Returns true if any of the given sockets has at least one visible rail
        /// Directly checks Rail visibility state instead of relying on counters
        public bool HasVisibleRailOnSockets(int[] socketIndices)
        {
            if (socketIndices == null || socketIndices.Length == 0) return false;
            
            foreach (int socketIndex in socketIndices)
            {
                if (_socketToRailings.TryGetValue(socketIndex, out var railings))
                {
                    foreach (var railing in railings)
                    {
                        // Check if this is a visible Rail (not Post)
                        if (railing && railing.type == PlatformRailing.RailingType.Rail && !railing.IsHidden)
                            return true;
                    }
                }
            }
            return false;
        }


        /// Triggers visibility update on all railings
        /// IMPORTANT: Rails must update FIRST so counters are correct when Posts check visibility
        public void RefreshAllRailingsVisibility()
        {
            if (_cachedRailings == null) return;
            
            // First pass: update all Rails (they update the visibility counters via SetHidden)
            foreach (var r in _cachedRailings)
            {
                if (r && r.type == PlatformRailing.RailingType.Rail)
                    r.UpdateVisibility();
            }
            
            // Second pass: update all Posts (they use HasVisibleRailOnSockets which reads counters)
            foreach (var r in _cachedRailings)
            {
                if (r && r.type == PlatformRailing.RailingType.Post)
                    r.UpdateVisibility();
            }
        }


        public void EnsureChildrenRailingsRegistered()
        {
            if (_cachedRailings == null) return;
            
            foreach (var r in _cachedRailings)
            {
                if (r) r.EnsureRegistered();
            }
        }
        
        
        #endregion
    }
}

