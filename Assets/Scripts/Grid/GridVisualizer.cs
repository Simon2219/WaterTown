using System;
using UnityEngine;

namespace Grid
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class GridVisualizer : MonoBehaviour
    {
        [Header("References")]
        public WorldGrid grid;                 // target logical grid
        public Material material;              // material asset to use

        [Header("Display")]
        [Min(0)] public int level = 0;
        public bool showGrid = true;
        [Min(0.001f)] public float lineThickness = 0.05f;
        public Color lineColor = new(0f, 0f, 0f, 0.7f);
        [Range(0f, 1f)] public float neighborFade = 0.0f;

        [Header("Cell Colors")]
        public bool enableCellColors = true;
        public bool autoSyncColorsFromGrid = true;
        public Color defaultCellColor = new(0.12f, 0.34f, 0.55f, 0.25f);
        public Color highlightColor   = new(1f, 1f, 0f, 0.65f);

        [Serializable]
        public struct FlagColor { public WorldGrid.CellFlag flag; public Color color; }
        public FlagColor[] flagColors = new[]
        {
            new FlagColor{ flag = WorldGrid.CellFlag.Locked,    color = new Color(0.80f, 0.25f, 0.25f, 0.35f) },
            new FlagColor{ flag = WorldGrid.CellFlag.Buildable, color = new Color(0.25f, 0.80f, 0.35f, 0.35f) },
            new FlagColor{ flag = WorldGrid.CellFlag.Occupied,  color = new Color(0.25f, 0.50f, 0.90f, 0.35f) },
        };

        [Header("Tube Look")]
        public bool tubeLook = true;
        [Min(0f)] public float tubeJoinSmooth = 0.05f;
        [Range(0f,1f)] public float tubeLightStrength = 0.35f;
        [Range(0f,1f)] public float tubeRimStrength   = 0.15f;

        [Header("Depth")]
        public float yBias = 0.005f;
        public bool zTestAlways = false;

        // internals
        private Mesh _quadMesh;
        private Texture2D _colorMap;
        private Vector2Int _cachedSize;
        private int _cachedCellSize;
        private Vector3 _cachedOrigin;
        private int _cachedLevels;

        private int _lastGridVersion = -1;
        private int _lastLevel = -1;

        private MeshFilter _mf;
        private MeshRenderer _mr;
        private MaterialPropertyBlock _mpb;    // << per-renderer override

        // shader property IDs
        private static readonly int PID_GridOrigin  = Shader.PropertyToID("_GridOrigin");
        private static readonly int PID_CellSize    = Shader.PropertyToID("_CellSize");
        private static readonly int PID_SizeXY      = Shader.PropertyToID("_SizeXY");
        private static readonly int PID_LevelY      = Shader.PropertyToID("_LevelY");
        private static readonly int PID_LineColor   = Shader.PropertyToID("_LineColor");
        private static readonly int PID_LineWidth   = Shader.PropertyToID("_LineWidth");
        private static readonly int PID_CellMap     = Shader.PropertyToID("_CellMap");
        private static readonly int PID_EnableFill  = Shader.PropertyToID("_EnableFill");
        private static readonly int PID_Fade        = Shader.PropertyToID("_NeighborFade");
        private static readonly int PID_TubeLook    = Shader.PropertyToID("_TubeLook");
        private static readonly int PID_JoinSmooth  = Shader.PropertyToID("_JoinSmooth");
        private static readonly int PID_TubeLight   = Shader.PropertyToID("_TubeLightStrength");
        private static readonly int PID_TubeRim     = Shader.PropertyToID("_TubeRimStrength");
        private static readonly int PID_YBias       = Shader.PropertyToID("_YBias");
        private static readonly int PID_ZTestMode   = Shader.PropertyToID("_ZTestMode");

#if UNITY_EDITOR
        private bool _loggedNoMatThisValidate;
#endif

        private void EnsureComponents()
        {
            if (!_mf) _mf = gameObject.GetComponent<MeshFilter>() ?? gameObject.AddComponent<MeshFilter>();
            if (!_mr) _mr = gameObject.GetComponent<MeshRenderer>() ?? gameObject.AddComponent<MeshRenderer>();
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
        }

        private void OnEnable()
        {
            EnsureComponents();
            EnsureMaterialAssetOrRuntime();
            RebuildAll(force:true);
            SyncRendererMaterial();        // << force renderer slot to our asset
            ApplyParams(true);
            _lastGridVersion = -1;
            _lastLevel = -1;
        }

        private void Update()
        {
            bool needsRebuild = false;

            if (grid != null)
            {
                needsRebuild |= _cachedSize.x != grid.sizeX || _cachedSize.y != grid.sizeY;
                needsRebuild |= _cachedCellSize != grid.cellSize;
                needsRebuild |= _cachedOrigin != grid.worldOrigin;
                needsRebuild |= _cachedLevels != grid.levels;
            }

            if (needsRebuild) RebuildAll(true);

            if (grid && autoSyncColorsFromGrid && (_lastGridVersion != grid.Version || _lastLevel != level))
            {
                RecomputeAndApplyColorsForLevel();
                _lastGridVersion = grid.Version;
                _lastLevel = level;
            }

            ApplyParams(true);
        }

        private void OnValidate()
        {
            EnsureComponents();
            RebuildAll(false);
            SyncRendererMaterial();        // keep renderer slot synced in Edit Mode
            ApplyParams(false);
#if UNITY_EDITOR
            _loggedNoMatThisValidate = false;
#endif
        }

        public void RebuildNow()
        {
            EnsureComponents();
            EnsureMaterialAssetOrRuntime();
            RebuildAll(true);
            SyncRendererMaterial();
            ApplyParams(true);
            _lastGridVersion = -1;
        }

        /// <summary>
        /// Force MeshRenderer to use 'material' asset (clears any prior per-renderer instance Unity may have created).
        /// </summary>
        public void SyncRendererMaterial()
        {
            if (_mr == null) return;
            if (material == null) { _mr.sharedMaterial = null; return; }

            // If renderer has an instantiated material (created by .material at some point),
            // nuke it by reassigning sharedMaterial explicitly.
            _mr.sharedMaterial = material;
        }

        // ----- Color Map API -----
        public void SetCellColor(Vector3Int cell, Color color)
        {
            if (!grid || !grid.CellInBounds(cell)) return;
            EnsureColorMap();
            _colorMap.SetPixel(cell.x, cell.y, color);
        }

        public void FillAllCells(Color color)
        {
            EnsureColorMap();
            var data = _colorMap.GetPixels32();
            var c32 = (Color32)color;
            for (int i = 0; i < data.Length; i++) data[i] = c32;
            _colorMap.SetPixels32(data);
        }

        public void ApplyColorMap()
        {
            if (_colorMap) _colorMap.Apply(false, false);
        }

        public void RecomputeAndApplyColorsForLevel()
        {
            if (grid == null) return;
            EnsureColorMap();

            int z = Mathf.Clamp(level, 0, Mathf.Max(0, grid.levels - 1));
            int w = Mathf.Max(1, grid.sizeX);
            int h = Mathf.Max(1, grid.sizeY);

            var fcs = flagColors;
            var colors = new Color[w * h];

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    var cell = new Vector3Int(x, y, z);
                    grid.TryGetCell(cell, out var cd);

                    var flags = cd.flags;
                    if (flags == WorldGrid.CellFlag.Empty)
                    {
                        colors[row + x] = defaultCellColor;
                        continue;
                    }

                    Color accum = Color.clear;
                    int count = 0;
                    for (int i = 0; i < fcs.Length; i++)
                    {
                        var fc = fcs[i];
                        if (fc.flag == WorldGrid.CellFlag.Empty) continue;
                        if ((flags & fc.flag) != 0) { accum += fc.color; count++; }
                    }
                    colors[row + x] = (count == 0) ? defaultCellColor : (accum / count);
                }
            }

            var arr = new Color32[w * h];
            for (int i = 0; i < arr.Length; i++) arr[i] = (Color32)colors[i];
            _colorMap.SetPixels32(arr);
            _colorMap.Apply(false, false);
        }

        // ----- internals -----
        private void EnsureMaterialAssetOrRuntime()
        {
            EnsureComponents();

            if (material != null)
                return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                if (_mr) _mr.sharedMaterial = null; // no auto asset in edit mode
                return;
            }
#endif
            // Play Mode: create a runtime instance so it renders even without an assigned asset.
            var shader = Shader.Find("WaterCity/Grid/URPGrid");
            if (!shader)
            {
#if UNITY_EDITOR
                if (!_loggedNoMatThisValidate)
                {
                    Debug.LogWarning("GridVisualizer: shader 'WaterCity/Grid/URPGrid' not found. Create it at Assets/Shaders/URP_Grid.shader, or assign a material.");
                    _loggedNoMatThisValidate = true;
                }
#endif
                return;
            }
            var runtimeMat = new Material(shader) { name = "GridVisualizer_RuntimeMat" };
            runtimeMat.hideFlags = HideFlags.HideAndDontSave;
            material = runtimeMat;
        }

        private void RebuildAll(bool force)
        {
            if (grid)
            {
                _cachedSize     = new Vector2Int(Mathf.Max(1, grid.sizeX), Mathf.Max(1, grid.sizeY));
                _cachedCellSize = Mathf.Max(1, grid.cellSize);
                _cachedOrigin   = grid.worldOrigin;
                _cachedLevels   = Mathf.Max(1, grid.levels);
            }
            else
            {
                _cachedSize     = new Vector2Int(1, 1);
                _cachedCellSize = 1;
                _cachedOrigin   = transform.position;
                _cachedLevels   = 1;
            }

            BuildQuadMeshLocal(_cachedSize.x * _cachedCellSize, _cachedSize.y * _cachedCellSize);

            if (grid) transform.position = new Vector3(_cachedOrigin.x, 0f, _cachedOrigin.z);

            EnsureColorMap(initialFill:true);
            _lastGridVersion = -1;
        }

        private void BuildQuadMeshLocal(float widthMeters, float heightMeters)
        {
            EnsureComponents();

            if (_quadMesh == null)
            {
                _quadMesh = new Mesh { name = "GridVisualizer_Quad" };
#if UNITY_EDITOR
                _quadMesh.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
#else
                _quadMesh.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
#endif
                _quadMesh.MarkDynamic();
            }

            var w = Mathf.Max(0.0001f, widthMeters);
            var h = Mathf.Max(0.0001f, heightMeters);

            Vector3 bl = new Vector3(0f, 0f, 0f);
            Vector3 br = new Vector3(w,  0f, 0f);
            Vector3 tr = new Vector3(w,  0f, h);
            Vector3 tl = new Vector3(0f, 0f, h);

            var verts   = new[] { bl, br, tr, tl };
            var uv      = new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
            var tris    = new[] { 0,1,2, 0,2,3 };
            var normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };

            _quadMesh.Clear();
            _quadMesh.vertices  = verts;
            _quadMesh.uv        = uv;
            _quadMesh.triangles = tris;
            _quadMesh.normals   = normals;
            _quadMesh.RecalculateBounds();

            _mf.sharedMesh = _quadMesh;

            // ensure renderer slot points to our chosen asset
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
                wrapMode   = TextureWrapMode.Clamp,
                name       = "GridVisualizer_ColorMap"
            };

            if (initialFill)
            {
                var arr = new Color32[w * h];
                var c32 = (Color32)defaultCellColor;
                for (int i = 0; i < arr.Length; i++) arr[i] = c32;
                _colorMap.SetPixels32(arr);
                _colorMap.Apply(false, false);
            }
        }

        private void ApplyParams(bool allowRendererToggle)
        {
            EnsureComponents();

            // ensure renderer uses the 'material' asset
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

            var sizeX   = Mathf.Max(1, _cachedSize.x);
            var sizeY   = Mathf.Max(1, _cachedSize.y);
            var cellSz  = Mathf.Max(1, _cachedCellSize);
            var origin  = grid ? grid.worldOrigin : transform.position;
            var levelY  = grid ? grid.GetLevelWorldY(Mathf.Clamp(level, 0, Mathf.Max(grid.levels - 1, 0))) : 0f;

            // Write everything through MPB (safe regardless of shared/instanced material).
            _mpb.Clear();
            _mpb.SetVector(PID_GridOrigin, origin);
            _mpb.SetFloat (PID_CellSize,  cellSz);
            _mpb.SetVector(PID_SizeXY,    new Vector4(sizeX, sizeY, 0, 0));
            _mpb.SetFloat (PID_LevelY,    levelY);
            _mpb.SetColor (PID_LineColor, lineColor);
            _mpb.SetFloat (PID_LineWidth, Mathf.Max(0.001f, lineThickness));
            _mpb.SetFloat (PID_EnableFill, enableCellColors ? 1f : 0f);
            _mpb.SetFloat (PID_Fade,       Mathf.Clamp01(neighborFade));

            _mpb.SetFloat(PID_TubeLook,   tubeLook ? 1f : 0f);
            _mpb.SetFloat(PID_JoinSmooth, Mathf.Max(0f, tubeJoinSmooth));
            _mpb.SetFloat(PID_TubeLight,  Mathf.Clamp01(tubeLightStrength));
            _mpb.SetFloat(PID_TubeRim,    Mathf.Clamp01(tubeRimStrength));

            _mpb.SetFloat(PID_YBias,      Mathf.Max(0f, yBias));
            _mpb.SetFloat(PID_ZTestMode,  zTestAlways ? 8f : 4f); // 8=Always, 4=LEqual

            EnsureColorMap();
            _mpb.SetTexture(PID_CellMap, _colorMap);

            _mr.SetPropertyBlock(_mpb);
        }
    }
}
