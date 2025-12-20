using System;
using UnityEngine;

namespace Grid
{

/// Grid visualization component
/// Renders cell colors based on CellFlag states
/// Supports line coloring modes: Solid, Blend, Priority
///
[ExecuteAlways]
[DisallowMultipleComponent]
public class GridVisualizer : MonoBehaviour
{
    #region Configuration
    
    
    [Header("References")]
    public WorldGrid grid;
    public Material material;

    
    [Header("Display")]
    public bool showGrid = true;
    [Min(0.001f)] public float lineThickness = 0.05f;
    [Range(0f, 1f)] public float cellOpacity = 0.35f;
    [Range(0f, 1f)] public float lineOpacity = 0.7f;

    
    [Header("Line Coloring")]
    public LineColorMode lineColorMode = LineColorMode.Solid;
    public Color solidLineColor = new(0f, 0f, 0f, 1f);

    public enum LineColorMode
    {
        Solid,    // Use solidLineColor for all lines
        Blend,    // Blend colors from adjacent cells
        Priority  // Use higher priority cell's color
    }

    
    [Header("Cell Colors")]
    public bool enableCellColors = true;
    [Range(0f, 1f)] public float neighborFade = 0.0f;

    [Serializable]
    public struct FlagColor
    {
        public CellFlag flag;
        public Color color;
    }

    public FlagColor[] flagColors =
    {
        new() { flag = CellFlag.Empty, color = new Color(0.12f, 0.34f, 0.55f, 1f) },
        new() { flag = CellFlag.Locked, color = new Color(0.80f, 0.25f, 0.25f, 1f) },
        new() { flag = CellFlag.Buildable, color = new Color(0.25f, 0.80f, 0.35f, 1f) },
        new() { flag = CellFlag.Occupied, color = new Color(0.25f, 0.50f, 0.90f, 1f) },
        new() { flag = CellFlag.OccupyPreview, color = new Color(0.95f, 0.75f, 0.20f, 1f) },
        new() { flag = CellFlag.ModuleBlocked, color = new Color(0.60f, 0.20f, 0.60f, 1f) },
    };

    
    [Header("Line Corners")]
    [Min(0f)] public float cornerRadius = 0.05f;

    
    [Header("Depth")]
    public float yBias = 0.005f;
    public bool zTestAlways = false;

    
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
        RebuildAll();
        SyncRendererMaterial();
        ApplyParams(true);
        _gridDirty = true;
    }


    private void OnDisable()
    {
        UnsubscribeFromGridEvents();
    }


    private void Update()
    {
        // Check if grid reference changed
        if (_subscribedGrid != grid)
        {
            UnsubscribeFromGridEvents();
            SubscribeToGridEvents();
            _gridDirty = true;
        }

        bool needsRebuild = false;

        if (grid != null)
        {
            needsRebuild |= _cachedSize.x != grid.sizeX || _cachedSize.y != grid.sizeY;
            needsRebuild |= _cachedOrigin != grid.worldOrigin;
        }

        if (needsRebuild) 
            RebuildAll();

        if (grid && enableCellColors && _gridDirty)
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
        if (grid == null) return;
        
        grid.GridCellChanged += OnGridCellChanged;
        grid.GridAreaChanged += OnGridAreaChanged;
        _subscribedGrid = grid;
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
    
    
    #endregion
    
    
    #region Public API
    
    
    /// Force rebuild of mesh and color map
    ///
    public void RebuildNow()
    {
        EnsureComponents();
        EnsureMaterialAssetOrRuntime();
        RebuildAll();
        SyncRendererMaterial();
        ApplyParams(true);
        _gridDirty = true;
    }


    /// Force MeshRenderer to use 'material' asset
    /// Clears any prior per-renderer instance Unity may have created
    ///
    public void SyncRendererMaterial()
    {
        if (_mr == null) return;
        if (material == null)
        {
            _mr.sharedMaterial = null;
            return;
        }

        _mr.sharedMaterial = material;
    }
    
    
    #endregion
    
    
    #region Color Computation
    
    
    /// Recomputes cell colors from grid flag data
    /// Stores color in RGB, priority in Alpha (0 = Empty, higher = more important)
    ///
    public void RecomputeColors()
    {
        if (grid == null) return;
        EnsureColorMap();

        int w = Mathf.Max(1, grid.sizeX);
        int h = Mathf.Max(1, grid.sizeY);

        var colors = new Color[w * h];

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                var cell = new Vector2Int(x, y);
                var cd = grid.GetCell(cell);
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
    ///
    private Color GetColorForFlags(CellFlag flags)
    {
        if (flagColors == null || flagColors.Length == 0)
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


    /// Finds the configured color for a specific flag
    ///
    private Color GetFlagColor(CellFlag flag)
    {
        foreach (var fc in flagColors)
        {
            if (fc.flag == flag)
                return fc.color;
        }
        
        return Color.gray;
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


    /// Gets the base line color (without opacity - that's separate)
    ///
    private Color GetBaseLineColor()
    {
        return solidLineColor;
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

        if (material != null)
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
        material = runtimeMat;
    }


    private void RebuildAll()
    {
        if (grid)
        {
            _cachedSize = new Vector2Int(Mathf.Max(1, grid.sizeX), Mathf.Max(1, grid.sizeY));
            _cachedOrigin = grid.worldOrigin;
        }
        else
        {
            _cachedSize = new Vector2Int(1, 1);
            _cachedOrigin = transform.position;
        }

        BuildQuadMeshLocal(_cachedSize.x * WorldGrid.CellSize, _cachedSize.y * WorldGrid.CellSize);

        if (grid) 
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
            _mr.enabled = showGrid && material != null;

#if UNITY_EDITOR
        if (!Application.isPlaying && material == null) return;
#endif
        if (Application.isPlaying && material == null)
        {
            EnsureMaterialAssetOrRuntime();
            if (material == null) return;
            SyncRendererMaterial();
        }

        var sizeX = Mathf.Max(1, _cachedSize.x);
        var sizeY = Mathf.Max(1, _cachedSize.y);
        var cellSz = WorldGrid.CellSize;
        var origin = grid ? grid.worldOrigin : transform.position;

        _mpb.Clear();
        _mpb.SetVector(PID_GridOrigin, origin);
        _mpb.SetFloat(PID_CellSize, cellSz);
        _mpb.SetVector(PID_SizeXY, new Vector4(sizeX, sizeY, 0, 0));
        _mpb.SetFloat(PID_LevelY, 0);
        _mpb.SetColor(PID_LineColor, GetBaseLineColor());
        _mpb.SetFloat(PID_LineOpacity, Mathf.Clamp01(lineOpacity));
        _mpb.SetFloat(PID_LineColorMode, (float)lineColorMode);
        _mpb.SetFloat(PID_LineWidth, Mathf.Max(0.001f, lineThickness));
        _mpb.SetFloat(PID_EnableFill, enableCellColors ? 1f : 0f);
        _mpb.SetFloat(PID_CellOpacity, Mathf.Clamp01(cellOpacity));
        _mpb.SetFloat(PID_Fade, Mathf.Clamp01(neighborFade));
        _mpb.SetFloat(PID_CornerRadius, Mathf.Max(0f, cornerRadius));

        _mpb.SetFloat(PID_YBias, Mathf.Max(0f, yBias));
        _mpb.SetFloat(PID_ZTestMode, zTestAlways ? 8f : 4f);

        EnsureColorMap();
        _mpb.SetTexture(PID_CellMap, _colorMap);

        _mr.SetPropertyBlock(_mpb);
    }
    
    
    #endregion
}
}
