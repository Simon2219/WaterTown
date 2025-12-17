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
    private bool _showVizDisplay = false;
    private bool _showVizColors = false;
    private bool _showVizTube   = false;
    private bool _showVizDepth  = false;

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
            EditorGUILayout.HelpBox("Assign a WorldGrid from the scene to edit its configuration.", MessageType.Info);
            return;
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Dimensions (cells)", EditorStyles.boldLabel);
            _sceneGrid.sizeX = EditorGUILayout.IntSlider(new GUIContent("Size X"), _sceneGrid.sizeX, 1, MaxCells);
            _sceneGrid.sizeY = EditorGUILayout.IntSlider(new GUIContent("Size Y"), _sceneGrid.sizeY, 1, MaxCells);
        

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Metrics (meters)", EditorStyles.boldLabel);

            EditorGUILayout.Space();
        

            if (GUILayout.Button(new GUIContent("Apply Settings"), GUILayout.Height(24)))
            {
                Undo.RecordObject(_sceneGrid, "Apply Grid Settings");
                _sceneGrid.EditorApplySettings();
                EditorUtility.SetDirty(_sceneGrid);
            
                if (_visualizer != null && _visualizer.grid == _sceneGrid)
                {
                    _visualizer.RebuildNow();
                    _visualizer.RecomputeAndApplyColorsForLevel();
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
                EditorGUILayout.HelpBox("Add or create a GridVisualizer to control how the grid is rendered.", MessageType.Info);
                return;
            }

            // Material asset, with hard sync to renderer on change
            EditorGUILayout.Space(5);
            EditorGUI.BeginChangeCheck();
            var newMat = (Material)EditorGUILayout.ObjectField(
                new GUIContent("Material (URP Grid)"),
                _visualizer.material, typeof(Material), false);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_visualizer, "Assign Grid Material");
                _visualizer.material = newMat;
                _visualizer.SyncRendererMaterial();  // << key line
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

            // Display
            _showVizDisplay = EditorGUILayout.Foldout(_showVizDisplay, "Display", true);
            if (_showVizDisplay)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();
                    _visualizer.showGrid = EditorGUILayout.Toggle("Show Grid", _visualizer.showGrid);


                    _visualizer.lineThickness = EditorGUILayout.Slider(new GUIContent("Line Thickness (m)"), _visualizer.lineThickness, 0.001f, 0.5f);
                    _visualizer.lineColor = EditorGUILayout.ColorField("Line Color", _visualizer.lineColor);
                    _visualizer.neighborFade = EditorGUILayout.Slider(new GUIContent("Neighbor Fade"), _visualizer.neighborFade, 0f, 1f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorUtility.SetDirty(_visualizer);
                        SceneView.RepaintAll();
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                }
            }

            // Tube
            _showVizTube = EditorGUILayout.Foldout(_showVizTube, "Tube Look", true);
            if (_showVizTube)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();
                    _visualizer.tubeLook = EditorGUILayout.Toggle(new GUIContent("Enable Tube Look"), _visualizer.tubeLook);
                    using (new EditorGUI.DisabledScope(!_visualizer.tubeLook))
                    {
                        _visualizer.tubeJoinSmooth   = EditorGUILayout.Slider(new GUIContent("Join Smooth (m)"), _visualizer.tubeJoinSmooth, 0f, 0.25f);
                        _visualizer.tubeLightStrength = EditorGUILayout.Slider(new GUIContent("Light Strength"), _visualizer.tubeLightStrength, 0f, 1f);
                        _visualizer.tubeRimStrength   = EditorGUILayout.Slider(new GUIContent("Rim Strength"),   _visualizer.tubeRimStrength, 0f, 1f);
                    }
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
                    _visualizer.yBias = EditorGUILayout.Slider(new GUIContent("Y Bias (m)"), _visualizer.yBias, 0f, 0.05f);
                    _visualizer.zTestAlways = EditorGUILayout.Toggle(new GUIContent("ZTest Always (draw on top)"), _visualizer.zTestAlways);
                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorUtility.SetDirty(_visualizer);
                        SceneView.RepaintAll();
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                }
            }

            // Colors
            _showVizColors = EditorGUILayout.Foldout(_showVizColors, "Colors", true);
            if (_showVizColors)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();

                    _visualizer.enableCellColors = EditorGUILayout.Toggle("Enable Cell Colors", _visualizer.enableCellColors);
                    _visualizer.defaultCellColor = EditorGUILayout.ColorField("Default Cell Color", _visualizer.defaultCellColor);
                    _visualizer.highlightColor   = EditorGUILayout.ColorField("Highlight Color", _visualizer.highlightColor);

                    EditorGUILayout.Space(5);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Recompute Grid Colors", GUILayout.Width(220)))
                        {
                            _visualizer.RecomputeAndApplyColorsForLevel();
                            EditorUtility.SetDirty(_visualizer);
                            SceneView.RepaintAll();
                            EditorApplication.QueuePlayerLoopUpdate();
                        }

                        if (GUILayout.Button("Fill With Default"))
                        {
                            _visualizer.FillAllCells(_visualizer.defaultCellColor);
                            _visualizer.ApplyColorMap();
                            EditorUtility.SetDirty(_visualizer);
                            SceneView.RepaintAll();
                            EditorApplication.QueuePlayerLoopUpdate();
                        }
                    }

                    EditorGUILayout.Space(5);

                    EditorGUILayout.LabelField("Per-Flag Colors", EditorStyles.boldLabel);

                    if (_visualizer.flagColors != null)
                    {
                        int removeIndex = -1;
                        for (int i = 0; i < _visualizer.flagColors.Length; i++)
                        {
                            var fc = _visualizer.flagColors[i];
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                fc.flag  = (WorldGrid.CellFlag)EditorGUILayout.EnumPopup(GUIContent.none, fc.flag, GUILayout.Width(120));
                                fc.color = EditorGUILayout.ColorField(fc.color);
                                if (GUILayout.Button("×", GUILayout.Width(22)))
                                    removeIndex = i;
                            }
                            _visualizer.flagColors[i] = fc;
                        }
                        
                        // Handle removal after iteration
                        if (removeIndex >= 0)
                        {
                            Undo.RecordObject(_visualizer, "Remove Flag Color");
                            var list = new System.Collections.Generic.List<GridVisualizer.FlagColor>(_visualizer.flagColors);
                            list.RemoveAt(removeIndex);
                            _visualizer.flagColors = list.ToArray();
                        }
                    }
                    
                    // Add new flag color button
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
                                flag = WorldGrid.CellFlag.Empty, 
                                color = new Color(0.5f, 0.5f, 0.5f, 0.35f) 
                            });
                            _visualizer.flagColors = list.ToArray();
                        }
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        _visualizer.RecomputeAndApplyColorsForLevel();
                        EditorUtility.SetDirty(_visualizer);
                        SceneView.RepaintAll();
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                }
            }
        }
    }

    // ---------- Helpers ----------

    private static void FrameGridBounds(WorldGrid grid)
    {
#if UNITY_EDITOR
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
#endif
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
    #if UNITY_EDITOR
        if (!vis) return null;

        var shader = Shader.Find("WaterCity/Grid/URPGrid");
        if (!shader)
        {
            EditorUtility.DisplayDialog("Shader Missing",
                "Shader 'WaterCity/Grid/URPGrid' not found.\n\nMake sure it exists at Assets/Shaders/URP_Grid.shader.", "OK");
            return null;
        }

        const string folder = "Assets/Materials";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder("Assets", "Materials");

        var path = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(folder, "GridVisualizer_Mat.mat"));
        var mat = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
        
        AssetDatabase.CreateAsset(mat, path);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(mat);

        Undo.RecordObject(vis, "Assign Grid Material");
        vis.material = mat;
        vis.SyncRendererMaterial(); // make renderer use it immediately
        vis.RebuildNow();
        SceneView.RepaintAll();
        EditorApplication.QueuePlayerLoopUpdate();
        return mat;
    #endif
    }
}
}
#endif
