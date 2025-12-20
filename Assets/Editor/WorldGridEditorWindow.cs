#if UNITY_EDITOR
using System.IO;
using Grid;
using UnityEditor;
using UnityEngine;

namespace Editor
{

public class WorldGridEditorWindow : EditorWindow
{
    private const string MenuPath = "Tools/World Grid Editor";

    private WorldGrid _sceneGrid;
    private GridVisualizer _visualizer;

    private bool _showSettings = true;
    private bool _showVisualizer = false;
    private bool _showVizCells = false;
    private bool _showVizLines = false;
    private bool _showVizColors = false;
    private bool _showVizDepth = false;

    private Vector2 _scroll;

    private const int MaxCells = 250000;

    
    [MenuItem(MenuPath)]
    public static void ShowWindow()
    {
        var win = GetWindow<WorldGridEditorWindow>("World Grid");
        win.minSize = new Vector2(460, 360);
        win.Show();
    }

    
    private void OnEnable()
    {
        if (_sceneGrid == null) _sceneGrid = FindWorldGrid();
        if (_visualizer == null) _visualizer = FindVisualizer();
    }

    
    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.Space();

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            _sceneGrid = (WorldGrid)EditorGUILayout.ObjectField(
                new GUIContent("Scene WorldGrid"),
                _sceneGrid, typeof(WorldGrid), true);
        }

        EditorGUILayout.Space();

        _showSettings = EditorGUILayout.Foldout(_showSettings, "Grid Settings", true);
        if (_showSettings)
            DrawSettingsGUI();

        using (new EditorGUI.DisabledScope(!_sceneGrid))
        {
            if (GUILayout.Button(new GUIContent("Frame Grid Scene")))
                FrameGridBounds(_sceneGrid);
        }

        EditorGUILayout.Space();
        EditorGUILayout.Space();

        _showVisualizer = EditorGUILayout.Foldout(_showVisualizer, "Grid Visualizer", true);
        if (_showVisualizer)
            DrawVisualizerGUI();

        EditorGUILayout.EndScrollView();
    }

    
    private void DrawSettingsGUI()
    {
        if (!_sceneGrid)
        {
            EditorGUILayout.HelpBox("Assign a WorldGrid from the scene to edit its configuration", MessageType.Info);
            return;
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Dimensions (cells)", EditorStyles.boldLabel);
            _sceneGrid.sizeX = EditorGUILayout.IntSlider(new GUIContent("Size X"), _sceneGrid.sizeX, 1, MaxCells);
            _sceneGrid.sizeY = EditorGUILayout.IntSlider(new GUIContent("Size Y"), _sceneGrid.sizeY, 1, MaxCells);

            EditorGUILayout.Space();

            if (GUILayout.Button(new GUIContent("Apply Settings"), GUILayout.Height(24)))
            {
                Undo.RecordObject(_sceneGrid, "Apply Grid Settings");
                _sceneGrid.EditorApplySettings();
                EditorUtility.SetDirty(_sceneGrid);

                if (_visualizer != null && _visualizer.grid == _sceneGrid)
                {
                    _visualizer.RebuildNow();
                    _visualizer.SyncRendererMaterial();
                    EditorUtility.SetDirty(_visualizer);
                }

                SceneView.RepaintAll();
                EditorApplication.QueuePlayerLoopUpdate();
            }

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(_sceneGrid);
                SceneView.RepaintAll();
                EditorApplication.QueuePlayerLoopUpdate();
            }
        }
    }

    
    private void DrawVisualizerGUI()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            _visualizer = (GridVisualizer)EditorGUILayout.ObjectField(
                new GUIContent("Visualizer"),
                _visualizer, typeof(GridVisualizer), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Find In Scene", GUILayout.Width(120)))
                    _visualizer = FindVisualizer();

                using (new EditorGUI.DisabledScope(_visualizer != null))
                {
                    if (GUILayout.Button("Create Visualizer", GUILayout.Width(140)))
                        _visualizer = CreateVisualizer();
                }

                GUILayout.FlexibleSpace();

                if (_visualizer != null && _sceneGrid != null && _visualizer.grid != _sceneGrid)
                {
                    if (GUILayout.Button("Link To Scene Grid", GUILayout.Width(160)))
                    {
                        Undo.RecordObject(_visualizer, "Link GridVisualizer to WorldGrid");
                        _visualizer.grid = _sceneGrid;
                        _visualizer.RebuildNow();
                        _visualizer.SyncRendererMaterial();
                        EditorUtility.SetDirty(_visualizer);
                        SceneView.RepaintAll();
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                }
            }

            if (!_visualizer)
            {
                EditorGUILayout.HelpBox("Add or create a GridVisualizer to control how the grid is rendered", MessageType.Info);
                return;
            }

            // Material
            EditorGUILayout.Space(5);
            EditorGUI.BeginChangeCheck();
            var newMat = (Material)EditorGUILayout.ObjectField(
                new GUIContent("Material (URP Grid)"),
                _visualizer.material, typeof(Material), false);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_visualizer, "Assign Grid Material");
                _visualizer.material = newMat;
                _visualizer.SyncRendererMaterial();
                _visualizer.RebuildNow();
                EditorUtility.SetDirty(_visualizer);
                SceneView.RepaintAll();
                EditorApplication.QueuePlayerLoopUpdate();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Create Material", GUILayout.Width(160)))
                {
                    var mat = CreateAndAssignGridMaterial(_visualizer);
                    if (mat != null)
                    {
                        _visualizer.material = mat;
                        _visualizer.SyncRendererMaterial();
                        _visualizer.RebuildNow();
                        EditorUtility.SetDirty(_visualizer);
                        SceneView.RepaintAll();
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                }
            }

            EditorGUILayout.Space(5);

            // Show Grid toggle at top
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                _visualizer.showGrid = EditorGUILayout.Toggle("Show Grid", _visualizer.showGrid);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorUtility.SetDirty(_visualizer);
                    SceneView.RepaintAll();
                    EditorApplication.QueuePlayerLoopUpdate();
                }
            }

            EditorGUILayout.Space(3);

            // Cells
            _showVizCells = EditorGUILayout.Foldout(_showVizCells, "Cells", true);
            if (_showVizCells)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();
                    
                    _visualizer.enableCellColors = EditorGUILayout.Toggle("Enable Cell Colors", _visualizer.enableCellColors);
                    
                    _visualizer.cellOpacity = EditorGUILayout.Slider(
                        new GUIContent("Cell Opacity"), 
                        _visualizer.cellOpacity, 0f, 1f);
                    
                    _visualizer.neighborFade = EditorGUILayout.Slider(
                        new GUIContent("Neighbor Fade"), 
                        _visualizer.neighborFade, 0f, 1f);
                    
                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorUtility.SetDirty(_visualizer);
                        SceneView.RepaintAll();
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                }
            }

            // Lines
            _showVizLines = EditorGUILayout.Foldout(_showVizLines, "Lines", true);
            if (_showVizLines)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();
                    
                    _visualizer.lineColorMode = (GridVisualizer.LineColorMode)EditorGUILayout.EnumPopup(
                        new GUIContent("Line Color Mode"), 
                        _visualizer.lineColorMode);
                    
                    _visualizer.solidLineColor = EditorGUILayout.ColorField(
                        new GUIContent("Line Default Color", "Used for Empty cells and Solid mode"), 
                        _visualizer.solidLineColor);
                    
                    // Priority mode options
                    if (_visualizer.lineColorMode == GridVisualizer.LineColorMode.Priority)
                    {
                        EditorGUILayout.Space(3);
                        
                        _visualizer.lineNeighborFade = EditorGUILayout.Slider(
                            new GUIContent("Neighbor Fade", "How far cell colors bleed beyond adjacent lines (0 = none, 1 = full cell)"), 
                            _visualizer.lineNeighborFade, 0f, 1f);
                        
                        _visualizer.lineBlendFalloff = EditorGUILayout.Slider(
                            new GUIContent("Blend Falloff", "How aggressively the color fades (lower = more gradual, higher = sharper)"), 
                            _visualizer.lineBlendFalloff, 0.1f, 3f);
                        
                        _visualizer.linePriorityOverride = EditorGUILayout.Toggle(
                            new GUIContent("Priority Override", "On: highest priority wins. Off: blend colors together"), 
                            _visualizer.linePriorityOverride);
                    }
                    
                    EditorGUILayout.Space(3);
                    
                    _visualizer.lineThickness = EditorGUILayout.Slider(
                        new GUIContent("Line Thickness (m)"), 
                        _visualizer.lineThickness, 0.001f, 0.5f);
                    
                    _visualizer.lineOpacity = EditorGUILayout.Slider(
                        new GUIContent("Line Opacity"), 
                        _visualizer.lineOpacity, 0f, 1f);
                    
                    _visualizer.cornerRadius = EditorGUILayout.Slider(
                        new GUIContent("Corner Radius (m)", "Rounds the intersections where grid lines meet"), 
                        _visualizer.cornerRadius, 0f, 0.25f);
                    
                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorUtility.SetDirty(_visualizer);
                        SceneView.RepaintAll();
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                }
            }

            // Depth
            _showVizDepth = EditorGUILayout.Foldout(_showVizDepth, "Depth / Overlay", true);
            if (_showVizDepth)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();
                    
                    _visualizer.yBias = EditorGUILayout.Slider(
                        new GUIContent("Y Bias (m)"), 
                        _visualizer.yBias, 0f, 0.05f);
                    
                    _visualizer.zTestAlways = EditorGUILayout.Toggle(
                        new GUIContent("ZTest Always (draw on top)"), 
                        _visualizer.zTestAlways);
                    
                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorUtility.SetDirty(_visualizer);
                        SceneView.RepaintAll();
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                }
            }

            // Colors
            _showVizColors = EditorGUILayout.Foldout(_showVizColors, "Flag Colors", true);
            if (_showVizColors)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();

                    EditorGUILayout.LabelField("Per-Flag Colors", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox("Each cell displays the color of its highest priority flag", MessageType.None);

                    EditorGUILayout.Space(3);

                    if (_visualizer.flagColors != null)
                    {
                        int removeIndex = -1;
                        for (int i = 0; i < _visualizer.flagColors.Length; i++)
                        {
                            var fc = _visualizer.flagColors[i];
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                fc.flag = (CellFlag)EditorGUILayout.EnumPopup(GUIContent.none, fc.flag, GUILayout.Width(120));
                                fc.color = EditorGUILayout.ColorField(fc.color);
                                if (GUILayout.Button("×", GUILayout.Width(22)))
                                    removeIndex = i;
                            }
                            _visualizer.flagColors[i] = fc;
                        }

                        if (removeIndex >= 0)
                        {
                            Undo.RecordObject(_visualizer, "Remove Flag Color");
                            var list = new System.Collections.Generic.List<GridVisualizer.FlagColor>(_visualizer.flagColors);
                            list.RemoveAt(removeIndex);
                            _visualizer.flagColors = list.ToArray();
                        }
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("+ Add Flag Color", GUILayout.Width(120)))
                        {
                            Undo.RecordObject(_visualizer, "Add Flag Color");
                            var list = _visualizer.flagColors != null
                                ? new System.Collections.Generic.List<GridVisualizer.FlagColor>(_visualizer.flagColors)
                                : new System.Collections.Generic.List<GridVisualizer.FlagColor>();
                            list.Add(new GridVisualizer.FlagColor
                            {
                                flag = CellFlag.Empty,
                                color = new Color(0.5f, 0.5f, 0.5f, 1f)
                            });
                            _visualizer.flagColors = list.ToArray();
                        }
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorUtility.SetDirty(_visualizer);
                        SceneView.RepaintAll();
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                }
            }
        }
    }

    
    #region Helpers
    
    
    private static void FrameGridBounds(WorldGrid grid)
    {
        if (grid == null) return;

        float w = Mathf.Max(1f, grid.sizeX * WorldGrid.CellSize);
        float h = Mathf.Max(1f, grid.sizeY * WorldGrid.CellSize);
        var center = grid.worldOrigin + new Vector3(w * 0.5f, 0f, h * 0.5f);

        var sv = SceneView.lastActiveSceneView;
        if (sv == null && SceneView.sceneViews.Count > 0)
            sv = (SceneView)SceneView.sceneViews[0];
        if (sv == null) return;

        sv.Focus();
        sv.in2DMode = false;
        sv.orthographic = true;
        sv.pivot = center + Vector3.up * 0.01f;
        sv.size = Mathf.Max(w, h) * 0.6f;
        sv.rotation = Quaternion.Euler(90f, 0f, 0f);
        sv.Repaint();
    }

    
    private static WorldGrid FindWorldGrid()
    {
        return FindFirstObjectByType<WorldGrid>(FindObjectsInactive.Exclude);
    }

    
    private static GridVisualizer FindVisualizer()
    {
        return FindFirstObjectByType<GridVisualizer>(FindObjectsInactive.Exclude);
    }

    
    private static GridVisualizer CreateVisualizer()
    {
        var go = new GameObject("Grid Visualizer");
        Undo.RegisterCreatedObjectUndo(go, "Create Grid Visualizer");
        var vis = go.AddComponent<GridVisualizer>();
        return vis;
    }

    
    private static Material CreateAndAssignGridMaterial(GridVisualizer vis)
    {
        if (!vis) return null;

        var shader = Shader.Find("WaterCity/Grid/URPGrid");
        if (!shader)
        {
            EditorUtility.DisplayDialog("Shader Missing",
                "Shader 'WaterCity/Grid/URPGrid' not found\n\nMake sure it exists at Assets/Shaders/URP_Grid.shader", "OK");
            return null;
        }

        const string folder = "Assets/Materials";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder("Assets", "Materials");

        var path = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(folder, "GridVisualizer_Mat.mat"));
        var mat = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };

        AssetDatabase.CreateAsset(mat, path);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(mat);

        Undo.RecordObject(vis, "Assign Grid Material");
        vis.material = mat;
        vis.SyncRendererMaterial();
        vis.RebuildNow();
        SceneView.RepaintAll();
        EditorApplication.QueuePlayerLoopUpdate();
        return mat;
    }
    
    
    #endregion
}
}
#endif
