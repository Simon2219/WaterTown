using System;
using Building;
using UnityEngine;

namespace Grid
{

/// Grid visualization component
/// Renders cell colors based on CellFlag states
/// Supports line coloring modes: Solid, Priority
///
[ExecuteAlways]
[DisallowMultipleComponent]
public class GridVisualizer : MonoBehaviour
{
    #region Types
    
    
    public enum LineColorMode
    {
        Solid,   // Use solidLineColor for all lines
        Priority // Use cell colors with neighbor bleeding
    }

    [Serializable]
    public struct FlagColor
    {
        public CellFlag flag;
        public Color color;
    }
    
    
    #endregion
    
    
    #region Events
    
    
    /// Fired when grid visibility changes
    public event Action<bool> OnGridVisibilityChanged;
    
    /// Fired when cell colors enabled state changes
    public event Action<bool> OnCellColorsEnabledChanged;
    
    
    #endregion
    
    
    #region Configuration
    
    
    [Header("References")]
    [SerializeField] private WorldGrid _grid;
    [SerializeField] private Material _material;
    
    [Header("Event Sources")]
    [Tooltip("Optional - subscribes to build mode events to auto-toggle cell colors")]
    [SerializeField] private BuildModeManager _buildModeManager;

    
    [Header("Display")]
    [SerializeField] private bool _showGrid = false;
    [SerializeField, Min(0.001f)] private float _lineThickness = 0.05f;
    [SerializeField, Range(0f, 1f)] private float _cellOpacity = 0.35f;
    [SerializeField, Range(0f, 1f)] private float _lineOpacity = 0.7f;

    
    [Header("Line Coloring")]
    [SerializeField] private LineColorMode _lineColorMode = LineColorMode.Solid;
    [SerializeField] private Color _solidLineColor = new(0f, 0f, 0f, 1f);
    [SerializeField, Range(0f, 1f)] private float _lineNeighborFade = 0f;
    [SerializeField, Range(0.1f, 3f)] private float _lineBlendFalloff = 1f;

    
    [Header("Cell Colors")]
    [SerializeField] private bool _enableCellColors = true;
    [SerializeField, Range(0f, 1f)] private float _neighborFade = 0.0f;

    [SerializeField]
    private FlagColor[] _flagColors =
    {
        new() { flag = CellFlag.Empty, color = new Color(0.12f, 0.34f, 0.55f, 1f) },
        new() { flag = CellFlag.Locked, color = new Color(0.80f, 0.25f, 0.25f, 1f) },
        new() { flag = CellFlag.Buildable, color = new Color(0.25f, 0.80f, 0.35f, 1f) },
        new() { flag = CellFlag.Occupied, color = new Color(0.25f, 0.50f, 0.90f, 1f) },
        new() { flag = CellFlag.OccupyPreview, color = new Color(0.95f, 0.75f, 0.20f, 1f) },
        new() { flag = CellFlag.ModuleBlocked, color = new Color(0.60f, 0.20f, 0.60f, 1f) },
    };

    
    [Header("Line Corners")]
    [SerializeField, Min(0f)] private float _cornerRadius = 0.05f;

    
    [Header("Depth")]
    [SerializeField] private float _yBias = 0.005f;
    [SerializeField] private bool _zTestAlways = false;

    
    #endregion
    
    
    #region Internals
    
    
    private Mesh _quadMesh;
    private Texture2D _colorMap;
    private Vector2Int _cachedSize;
    private Vector3 _cachedOrigin;

    private bool _gridDirty = true;
    private WorldGrid _subscribedGrid;

    private MeshFilter _mf;
    private MeshRenderer _mr;
    private MaterialPropertyBlock _mpb;

    // Shader property IDs
    private static readonly int PID_GridOrigin = Shader.PropertyToID("_GridOrigin");
    private static readonly int PID_CellSize = Shader.PropertyToID("_CellSize");
    private static readonly int PID_SizeXY = Shader.PropertyToID("_SizeXY");
    private static readonly int PID_LevelY = Shader.PropertyToID("_LevelY");
    private static readonly int PID_LineColor = Shader.PropertyToID("_LineColor");
    private static readonly int PID_LineOpacity = Shader.PropertyToID("_LineOpacity");
    private static readonly int PID_LineColorMode = Shader.PropertyToID("_LineColorMode");
    private static readonly int PID_LineNeighborFade = Shader.PropertyToID("_LineNeighborFade");
    private static readonly int PID_LineBlendFalloff = Shader.PropertyToID("_LineBlendFalloff");
    private static readonly int PID_LineWidth = Shader.PropertyToID("_LineWidth");
    private static readonly int PID_CellMap = Shader.PropertyToID("_CellMap");
    private static readonly int PID_EnableFill = Shader.PropertyToID("_EnableFill");
    private static readonly int PID_CellOpacity = Shader.PropertyToID("_CellOpacity");
    private static readonly int PID_Fade = Shader.PropertyToID("_NeighborFade");
    private static readonly int PID_CornerRadius = Shader.PropertyToID("_CornerRadius");
    private static readonly int PID_YBias = Shader.PropertyToID("_YBias");
    private static readonly int PID_ZTestMode = Shader.PropertyToID("_ZTestMode");

#if UNITY_EDITOR
    private bool _loggedNoMatThisValidate;
#endif
    
    
    #endregion
    
    
    #region Lifecycle
    
    
    private void OnEnable()
    {
        EnsureComponents();
        EnsureMaterialAssetOrRuntime();
        SubscribeToGridEvents();
        SubscribeToBuildModeEvents();
        RebuildAll();
        SyncRendererMaterial();
        ApplyParams(true);
        _gridDirty = true;
        
        // Sync with current build mode state
        SyncWithBuildModeState();
    }
    
    
    private void SyncWithBuildModeState()
    {
        if (_buildModeManager != null && _buildModeManager.IsInBuildMode)
        {
            ShowGrid = true;
            EnableCellColors = true;
        }
        else
        {
            ShowGrid = false;
            EnableCellColors = false;
        }
    }


    private void OnDisable()
    {
        UnsubscribeFromGridEvents();
        UnsubscribeFromBuildModeEvents();
    }


    private void Update()
    {
        // Check if grid reference changed
        if (_subscribedGrid != _grid)
        {
            UnsubscribeFromGridEvents();
            SubscribeToGridEvents();
            _gridDirty = true;
        }

        bool needsRebuild = false;

        if (_grid != null)
        {
            needsRebuild |= _cachedSize.x != _grid.sizeX || _cachedSize.y != _grid.sizeY;
            needsRebuild |= _cachedOrigin != _grid.worldOrigin;
        }

        if (needsRebuild) 
            RebuildAll();

        if (_grid && _enableCellColors && _gridDirty)
        {
            RecomputeColors();
            _gridDirty = false;
        }

        ApplyParams(true);
    }


    private void OnValidate()
    {
        EnsureComponents();
        RebuildAll();
        SyncRendererMaterial();
        ApplyParams(false);
        _gridDirty = true;
#if UNITY_EDITOR
        _loggedNoMatThisValidate = false;
#endif
    }
    
    
    #endregion
    
    
    #region Event Subscription
    
    
    private void SubscribeToGridEvents()
    {
        if (_grid == null) return;
        
        _grid.GridCellChanged += OnGridCellChanged;
        _grid.GridAreaChanged += OnGridAreaChanged;
        _subscribedGrid = _grid;
    }


    private void UnsubscribeFromGridEvents()
    {
        if (_subscribedGrid == null) return;
        
        _subscribedGrid.GridCellChanged -= OnGridCellChanged;
        _subscribedGrid.GridAreaChanged -= OnGridAreaChanged;
        _subscribedGrid = null;
    }


    private void OnGridCellChanged(Vector2Int cell)
    {
        _gridDirty = true;
    }


    private void OnGridAreaChanged(Vector2Int min, Vector2Int max)
    {
        _gridDirty = true;
    }
    
    
    private void SubscribeToBuildModeEvents()
    {
        if (_buildModeManager == null) return;
        
        _buildModeManager.OnBuildModeEntered += OnBuildModeEntered;
        _buildModeManager.OnBuildModeExited += OnBuildModeExited;
        _buildModeManager.OnPlacementCancelled += OnPlacementCancelled;
    }
    
    
    private void UnsubscribeFromBuildModeEvents()
    {
        if (_buildModeManager == null) return;
        
        _buildModeManager.OnBuildModeEntered -= OnBuildModeEntered;
        _buildModeManager.OnBuildModeExited -= OnBuildModeExited;
        _buildModeManager.OnPlacementCancelled -= OnPlacementCancelled;
    }
    
    
    private void OnBuildModeEntered()
    {
        ShowGrid = true;
        EnableCellColors = true;
    }
    
    
    private void OnBuildModeExited()
    {
        EnableCellColors = false;
        ShowGrid = false;
    }
    
    
    private void OnPlacementCancelled()
    {
        _gridDirty = true;
    }
    
    
    #endregion
    
    
    #region Public API - Properties
    
    
    /// Reference to the WorldGrid
    public WorldGrid Grid
    {
        get => _grid;
        set
        {
            if (_grid == value) return;
            UnsubscribeFromGridEvents();
            _grid = value;
            SubscribeToGridEvents();
            _gridDirty = true;
        }
    }
    
    /// Material used for rendering
    public Material Material
    {
        get => _material;
        set
        {
            _material = value;
            SyncRendererMaterial();
        }
    }
    
    /// Show or hide the entire grid
    public bool ShowGrid
    {
        get => _showGrid;
        set
        {
            if (_showGrid == value) return;
            _showGrid = value;
            OnGridVisibilityChanged?.Invoke(_showGrid);
        }
    }
    
    /// Enable or disable cell color rendering
    public bool EnableCellColors
    {
        get => _enableCellColors;
        set
        {
            if (_enableCellColors == value) return;
            _enableCellColors = value;
            OnCellColorsEnabledChanged?.Invoke(_enableCellColors);
        }
    }
    
    /// Cell fill opacity (0 = transparent, 1 = opaque)
    public float CellOpacity
    {
        get => _cellOpacity;
        set => _cellOpacity = Mathf.Clamp01(value);
    }
    
    /// Line opacity (0 = transparent, 1 = opaque)
    public float LineOpacity
    {
        get => _lineOpacity;
        set => _lineOpacity = Mathf.Clamp01(value);
    }
    
    /// Line thickness in world units
    public float LineThickness
    {
        get => _lineThickness;
        set => _lineThickness = Mathf.Max(0.001f, value);
    }
    
    /// Line coloring mode (Solid or Priority)
    public LineColorMode LineMode
    {
        get => _lineColorMode;
        set => _lineColorMode = value;
    }
    
    /// Solid line color (used when LineMode = Solid)
    public Color SolidLineColor
    {
        get => _solidLineColor;
        set => _solidLineColor = value;
    }
    
    /// How far line colors bleed into neighbor cells (0-1)
    public float LineNeighborFade
    {
        get => _lineNeighborFade;
        set => _lineNeighborFade = Mathf.Clamp01(value);
    }
    
    /// Line color blend falloff strength
    public float LineBlendFalloff
    {
        get => _lineBlendFalloff;
        set => _lineBlendFalloff = Mathf.Clamp(value, 0.1f, 3f);
    }
    
    /// Cell neighbor fade amount (0-1)
    public float NeighborFade
    {
        get => _neighborFade;
        set => _neighborFade = Mathf.Clamp01(value);
    }
    
    /// Corner radius for line intersections
    public float CornerRadius
    {
        get => _cornerRadius;
        set => _cornerRadius = Mathf.Max(0f, value);
    }
    
    /// Y-axis rendering bias
    public float YBias
    {
        get => _yBias;
        set => _yBias = Mathf.Max(0f, value);
    }
    
    /// Always pass depth test (render on top)
    public bool ZTestAlways
    {
        get => _zTestAlways;
        set => _zTestAlways = value;
    }
    
    /// Flag colors array (readonly access - use SetFlagColor to modify)
    public FlagColor[] FlagColors => _flagColors;
    
    
    #endregion
    
    
    #region Public API - Methods
    
    
    /// Force rebuild of mesh and color map
    public void RebuildNow()
    {
        EnsureComponents();
        EnsureMaterialAssetOrRuntime();
        RebuildAll();
        SyncRendererMaterial();
        ApplyParams(true);
        _gridDirty = true;
    }


    /// Force MeshRenderer to use material asset
    /// Clears any prior per-renderer instance Unity may have created
    public void SyncRendererMaterial()
    {
        if (_mr == null) return;
        if (_material == null)
        {
            _mr.sharedMaterial = null;
            return;
        }

        _mr.sharedMaterial = _material;
    }
    
    
    /// Set the color for a specific flag
    public void SetFlagColor(CellFlag flag, Color color)
    {
        if (_flagColors == null) return;
        
        for (int i = 0; i < _flagColors.Length; i++)
        {
            if (_flagColors[i].flag == flag)
            {
                _flagColors[i].color = color;
                _gridDirty = true;
                return;
            }
        }
    }
    
    
    /// Get the color for a specific flag
    public Color GetFlagColor(CellFlag flag)
    {
        if (_flagColors == null) return Color.gray;
        
        foreach (var fc in _flagColors)
        {
            if (fc.flag == flag)
                return fc.color;
        }
        
        return Color.gray;
    }
    
    
    /// Mark the grid as dirty, forcing a color recompute on next update
    public void MarkDirty()
    {
        _gridDirty = true;
    }
    
    
    #endregion
    
    
    #region Color Computation
    
    
    /// Recomputes cell colors from grid flag data
    /// Stores color in RGB, priority in Alpha (0 = Empty, higher = more important)
    public void RecomputeColors()
    {
        if (_grid == null) return;
        EnsureColorMap();

        int w = Mathf.Max(1, _grid.sizeX);
        int h = Mathf.Max(1, _grid.sizeY);

        var colors = new Color[w * h];

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                var cell = new Vector2Int(x, y);
                var cd = _grid.GetCell(cell);
                var flags = cd?.Flags ?? CellFlag.Empty;

                Color cellColor = GetColorForFlags(flags);
                cellColor.a = GetPriorityForFlags(flags);
                colors[row + x] = cellColor;
            }
        }

        var arr = new Color32[w * h];
        for (int i = 0; i < arr.Length; i++) 
            arr[i] = colors[i];
        
        _colorMap.SetPixels32(arr);
        _colorMap.Apply(false, false);
    }


    /// Gets the color for a given flag combination
    /// Uses highest priority flag if multiple are set
    private Color GetColorForFlags(CellFlag flags)
    {
        if (_flagColors == null || _flagColors.Length == 0)
            return Color.gray;

        // Priority order: Locked > Occupied > OccupyPreview > ModuleBlocked > Buildable > Empty
        CellFlag[] priorityOrder =
        {
            CellFlag.Locked,
            CellFlag.Occupied,
            CellFlag.OccupyPreview,
            CellFlag.ModuleBlocked,
            CellFlag.Buildable,
            CellFlag.Empty
        };

        foreach (var priorityFlag in priorityOrder)
        {
            // For Empty, check if flags == Empty (no flags set)
            if (priorityFlag == CellFlag.Empty)
            {
                if (flags == CellFlag.Empty)
                    return GetFlagColor(CellFlag.Empty);
            }
            else if ((flags & priorityFlag) != 0)
            {
                return GetFlagColor(priorityFlag);
            }
        }

        return GetFlagColor(CellFlag.Empty);
    }


    /// Gets priority value for flags (0 = Empty, higher = more important)
    /// Used for line coloring in Priority mode
    ///
    private float GetPriorityForFlags(CellFlag flags)
    {
        // Priority order matches GetColorForFlags
        if ((flags & CellFlag.Locked) != 0) return 1.0f;
        if ((flags & CellFlag.Occupied) != 0) return 0.85f;
        if ((flags & CellFlag.OccupyPreview) != 0) return 0.7f;
        if ((flags & CellFlag.ModuleBlocked) != 0) return 0.55f;
        if ((flags & CellFlag.Buildable) != 0) return 0.4f;
        return 0f; // Empty
    }


    #endregion
    
    
    #region Internal Setup
    
    
    private void EnsureComponents()
    {
        if (!_mf) _mf = gameObject.GetComponent<MeshFilter>() ?? gameObject.AddComponent<MeshFilter>();
        if (!_mr) _mr = gameObject.GetComponent<MeshRenderer>() ?? gameObject.AddComponent<MeshRenderer>();
        if (_mpb == null) _mpb = new MaterialPropertyBlock();
    }


    private void EnsureMaterialAssetOrRuntime()
    {
        EnsureComponents();

        if (_material != null)
            return;

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            if (_mr) _mr.sharedMaterial = null;
            return;
        }
#endif
        var shader = Shader.Find("WaterCity/Grid/URPGrid");
        if (!shader)
        {
#if UNITY_EDITOR
            if (!_loggedNoMatThisValidate)
            {
                Debug.LogWarning("GridVisualizer: shader 'WaterCity/Grid/URPGrid' not found");
                _loggedNoMatThisValidate = true;
            }
#endif
            return;
        }

        var runtimeMat = new Material(shader)
        {
            name = "GridVisualizer_RuntimeMat",
            hideFlags = HideFlags.HideAndDontSave
        };
        _material = runtimeMat;
    }


    private void RebuildAll()
    {
        if (_grid)
        {
            _cachedSize = new Vector2Int(Mathf.Max(1, _grid.sizeX), Mathf.Max(1, _grid.sizeY));
            _cachedOrigin = _grid.worldOrigin;
        }
        else
        {
            _cachedSize = new Vector2Int(1, 1);
            _cachedOrigin = transform.position;
        }

        BuildQuadMeshLocal(_cachedSize.x * WorldGrid.CellSize, _cachedSize.y * WorldGrid.CellSize);

        if (_grid) 
            transform.position = new Vector3(_cachedOrigin.x, 0f, _cachedOrigin.z);

        EnsureColorMap(initialFill: true);
        _gridDirty = true;
    }


    private void BuildQuadMeshLocal(float widthMeters, float heightMeters)
    {
        EnsureComponents();

        if (_quadMesh == null)
        {
            _quadMesh = new Mesh { name = "GridVisualizer_Quad" };
            _quadMesh.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            _quadMesh.MarkDynamic();
        }

        var w = Mathf.Max(0.0001f, widthMeters);
        var h = Mathf.Max(0.0001f, heightMeters);

        Vector3 bl = new(0f, 0f, 0f);
        Vector3 br = new(w, 0f, 0f);
        Vector3 tr = new(w, 0f, h);
        Vector3 tl = new(0f, 0f, h);

        var verts = new[] { bl, br, tr, tl };
        var uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        var tris = new[] { 0, 1, 2, 0, 2, 3 };
        var normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };

        _quadMesh.Clear();
        _quadMesh.vertices = verts;
        _quadMesh.uv = uv;
        _quadMesh.triangles = tris;
        _quadMesh.normals = normals;
        _quadMesh.RecalculateBounds();

        _mf.sharedMesh = _quadMesh;
        SyncRendererMaterial();
    }


    private void EnsureColorMap(bool initialFill = false)
    {
        int w = Mathf.Max(1, _cachedSize.x);
        int h = Mathf.Max(1, _cachedSize.y);

        if (_colorMap != null && _colorMap.width == w && _colorMap.height == h)
            return;

        if (_colorMap != null)
        {
            if (Application.isPlaying) Destroy(_colorMap);
            else DestroyImmediate(_colorMap);
        }

        _colorMap = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "GridVisualizer_ColorMap"
        };

        if (initialFill)
        {
            var arr = new Color32[w * h];
            Color emptyColor = GetFlagColor(CellFlag.Empty);
            emptyColor.a = 0f; // Priority 0 = Empty (line coloring uses solid color)
            var c32 = (Color32)emptyColor;
            
            for (int i = 0; i < arr.Length; i++) 
                arr[i] = c32;
            
            _colorMap.SetPixels32(arr);
            _colorMap.Apply(false, false);
        }
    }


    private void ApplyParams(bool allowRendererToggle)
    {
        EnsureComponents();
        SyncRendererMaterial();

        if (allowRendererToggle)
            _mr.enabled = _showGrid && _material != null;

#if UNITY_EDITOR
        if (!Application.isPlaying && _material == null) return;
#endif
        if (Application.isPlaying && _material == null)
        {
            EnsureMaterialAssetOrRuntime();
            if (_material == null) return;
            SyncRendererMaterial();
        }

        var sizeX = Mathf.Max(1, _cachedSize.x);
        var sizeY = Mathf.Max(1, _cachedSize.y);
        var cellSz = WorldGrid.CellSize;
        var origin = _grid ? _grid.worldOrigin : transform.position;

        _mpb.Clear();
        _mpb.SetVector(PID_GridOrigin, origin);
        _mpb.SetFloat(PID_CellSize, cellSz);
        _mpb.SetVector(PID_SizeXY, new Vector4(sizeX, sizeY, 0, 0));
        _mpb.SetFloat(PID_LevelY, 0);
        _mpb.SetColor(PID_LineColor, _solidLineColor);
        _mpb.SetFloat(PID_LineOpacity, _lineOpacity);
        _mpb.SetFloat(PID_LineColorMode, (float)_lineColorMode);
        _mpb.SetFloat(PID_LineNeighborFade, _lineNeighborFade);
        _mpb.SetFloat(PID_LineBlendFalloff, _lineBlendFalloff);
        _mpb.SetFloat(PID_LineWidth, _lineThickness);
        _mpb.SetFloat(PID_EnableFill, _enableCellColors ? 1f : 0f);
        _mpb.SetFloat(PID_CellOpacity, _cellOpacity);
        _mpb.SetFloat(PID_Fade, _neighborFade);
        _mpb.SetFloat(PID_CornerRadius, _cornerRadius);

        _mpb.SetFloat(PID_YBias, _yBias);
        _mpb.SetFloat(PID_ZTestMode, _zTestAlways ? 8f : 4f);

        EnsureColorMap();
        _mpb.SetTexture(PID_CellMap, _colorMap);

        _mr.SetPropertyBlock(_mpb);
    }
    
    
    #endregion
}
}
