using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;

namespace Platforms
{
    
[DisallowMultipleComponent]
public class PlatformRailing : MonoBehaviour
{
    #region Configuration
    
    
    public enum RailingType { Post, Rail }

    [Header("Binding")]
    public RailingType type;

    [Tooltip("Owning platform (auto-filled from parent)")]
    public GamePlatform _platform;
    public PlatformRailingSystem _railingSystem ;
    
    
    [Tooltip("Indices of sockets this piece is associated with on its platform")]
    [SerializeField] private int[] socketIndices = System.Array.Empty<int>();

    public bool IsRegistered { get; private set; }
    
    public bool IsVisible { get; private set; }

    public int[] SocketIndices => socketIndices;

    
    #endregion
    
    
    #region Lifecycle


    private void Awake()
    {
        if (!_platform)
            _platform = GetComponentInParent<GamePlatform>();
        
        if(!_railingSystem)
            _railingSystem = GetComponentInParent<PlatformRailingSystem>();
    }

    
    
    private void OnEnable()
    {
        EnsureRegistered();
    }

    
    
    private void OnDisable()
    {
        if (IsRegistered)
        {
            _railingSystem.UnregisterRailing(this);
            IsRegistered = false;
        }
    }
    
    
    #endregion
    
    
    #region Railing Functions
    
    
    public void SetSocketIndices(int[] indices)
    {
        socketIndices = indices ?? System.Array.Empty<int>();
    }

    
    public void SetSocketIndices(int index)
    {
        socketIndices = new int[1] { index };
    }
    
    
    public void SetSocketIndices(List<int> indices)
    {
        socketIndices = indices.Count > 0 ? indices.ToArray() : System.Array.Empty<int>();
    }



    /// Ensure this railing is known to its GamePlatform (for visibility updates)
    /// 
    public void EnsureRegistered()
    {
        if (IsRegistered)
            _railingSystem.UnregisterRailing(this);
        
        
        _railingSystem.RegisterRailing(this);
        IsRegistered = true;
    }

    
    

    /// Set Railing Visibility
    /// Hidden = GameObject inactive (NOT destroyed).
    /// Railings disappear on Platform Connection (dependend on bound Socket Status)
    /// 
    private void SetVisibility(bool isVisible)
    {
        if (IsVisible == isVisible) return;
        
        if (isVisible)
        {
            IsVisible = true;
            gameObject.SetActive(true);
        }
        else
        {
            IsVisible = false;
            gameObject.SetActive(false);
        }
    }
    


    /// Updates Visibility - based on Socket Status
    /// Rails: Hidden when their bound Socket Status is Connected.
    /// Posts: Hidden when both sockets to each side are Connected (no Rails, no post between them)
    /// 
    public void UpdateVisibility()
    {
        var indices = socketIndices ?? Array.Empty<int>();
        if (indices.Length == 0)
        {
            SetVisibility(true);
            return;
        }

        bool railingSocketsConnected = _railingSystem.AllSocketsConnected(indices);

        //Invert - if all connected, call with false
        SetVisibility(!railingSocketsConnected);
    }
    
    
    #endregion
    
    
    
#if UNITY_EDITOR
    private void OnValidate()
    {
        if (!_platform)
            _platform = GetComponentInParent<GamePlatform>();
    }
#endif
}
}
