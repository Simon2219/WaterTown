using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Platforms
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
        
        
        #endregion
        
        
        #region Initialization
        
        
        /// Called by GamePlatform to inject dependencies
        public void Initialize(GamePlatform platform, PlatformSocketSystem socketSystem)
        {
            _platform = platform;
            _socketSystem = socketSystem;
            EnsureChildrenRailingsRegistered();
        }
        
        
        #endregion
        
        
        #region Railing Registration
        
        
        /// Called by PlatformRailing to bind itself to given socket indices
        public void RegisterRailing(PlatformRailing railing)
        {
            // Cache commonly used refs (avoids repeated property / engine calls)
            var indices = railing.SocketIndices;

            if (indices == null || indices.Length == 0)
            {
                // Fallback: bind to nearest socket
                int nearest = _socketSystem.FindNearestSocketIndexLocal
                    (transform.InverseTransformPoint(railing.transform.position));
                
                if (nearest < 0) return;
                
                railing.SetSocketIndices(nearest);
            }

            // Array iteration (no enumerator, predictable)
            // ! -> Surpresses wrong null check warning, we do check above
            for (int i = 0, len = indices!.Length; i < len; i++)
            {
                int sIdx = indices[i];

                // Check Socket Assignment - Fallback
                if (!_socketToRailings.TryGetValue(sIdx, out List<PlatformRailing> list))
                {
                    list = new List<PlatformRailing>();
                    _socketToRailings.Add(sIdx, list);
                }

                // Duplicate Check
                if (!list.Contains(railing))
                    list.Add(railing);
            }
        }


        
        /// Called by PlatformRailing when disabled/destroyed
        public void UnregisterRailing(PlatformRailing railing)
        {
            foreach (KeyValuePair<int, List<PlatformRailing>> kv in _socketToRailings)
            {
                kv.Value.Remove(railing);
            }
        }
        
        
        #endregion
        
        
        #region Visibility Management


        public bool AllSocketsConnected(int[] socketIndices)
        {
            return _socketSystem.AllSocketsConnected(socketIndices);
        }

        
        
        /// Triggers visibility update on all railings
        /// IMPORTANT: Rails must update FIRST so counters are correct when Posts check visibility
        public void RefreshAllRailingsVisibility()
        {
            foreach (var platformRailing in _platform.PlatformRailings)
            {
                if (platformRailing)
                    platformRailing.UpdateVisibility();
            }
        }



        ///  IMPORTANT - THIS RUNS VERY OFTEN IF CALLED
        /// 
        private void EnsureChildrenRailingsRegistered()
        {
            foreach (PlatformRailing railing in _platform.PlatformRailings)
            {
                if (railing && !railing.IsRegistered)
                {
                    RegisterRailing(railing);
                }
            }
        }
        
        
        #endregion
    }
}

