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

                if (_visualizer != null && _visualizer.Grid == _sceneGrid)
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

                if (_visualizer != null && _sceneGrid != null && _visualizer.Grid != _sceneGrid)
                {
                    if (GUILayout.Button("Link To Scene Grid", GUILayout.Width(160)))
                    {
                        Undo.RecordObject(_visualizer, "Link GridVisualizer to WorldGrid");
                        _visualizer.Grid = _sceneGrid;
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
                _visualizer.Material, typeof(Material), false);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_visualizer, "Assign Grid Material");
                _visualizer.Material = newMat;
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
                        _visualizer.Material = mat;
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
                bool showGrid = EditorGUILayout.Toggle("Show Grid", _visualizer.ShowGrid);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(_visualizer, "Toggle Grid Visibility");
                    _visualizer.ShowGrid = showGrid;
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
                    
                    bool enableCellColors = EditorGUILayout.Toggle("Enable Cell Colors", _visualizer.EnableCellColors);
                    float cellOpacity = EditorGUILayout.Slider(new GUIContent("Cell Opacity"), _visualizer.CellOpacity, 0f, 1f);
                    float neighborFade = EditorGUILayout.Slider(new GUIContent("Neighbor Fade"), _visualizer.NeighborFade, 0f, 1f);
                    
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_visualizer, "Edit Cell Settings");
                        _visualizer.EnableCellColors = enableCellColors;
                        _visualizer.CellOpacity = cellOpacity;
                        _visualizer.NeighborFade = neighborFade;
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
                    
                    var lineColorMode = (GridVisualizer.LineColorMode)EditorGUILayout.EnumPopup(
                        new GUIContent("Line Color Mode"), _visualizer.LineMode);
                    
                    var solidLineColor = EditorGUILayout.ColorField(
                        new GUIContent("Line Default Color", "Used for Empty cells and Solid mode"), 
                        _visualizer.SolidLineColor);
                    
                    float lineNeighborFade = _visualizer.LineNeighborFade;
                    float lineBlendFalloff = _visualizer.LineBlendFalloff;
                    
                    // Priority mode options
                    if (lineColorMode == GridVisualizer.LineColorMode.Priority)
                    {
                        EditorGUILayout.Space(3);
                        
                        lineNeighborFade = EditorGUILayout.Slider(
                            new GUIContent("Neighbor Fade", "How far cell colors bleed beyond adjacent lines (0 = none, 1 = full cell)"), 
                            _visualizer.LineNeighborFade, 0f, 1f);
                        
                        lineBlendFalloff = EditorGUILayout.Slider(
                            new GUIContent("Blend Falloff", "Color retention strength (higher = color stays stronger longer)"), 
                            _visualizer.LineBlendFalloff, 0.1f, 3f);
                    }
                    
                    EditorGUILayout.Space(3);
                    
                    float lineThickness = EditorGUILayout.Slider(
                        new GUIContent("Line Thickness (m)"), _visualizer.LineThickness, 0.001f, 0.5f);
                    
                    float lineOpacity = EditorGUILayout.Slider(
                        new GUIContent("Line Opacity"), _visualizer.LineOpacity, 0f, 1f);
                    
                    float cornerRadius = EditorGUILayout.Slider(
                        new GUIContent("Corner Radius (m)", "Rounds the intersections where grid lines meet"), 
                        _visualizer.CornerRadius, 0f, 0.25f);
                    
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_visualizer, "Edit Line Settings");
                        _visualizer.LineMode = lineColorMode;
                        _visualizer.SolidLineColor = solidLineColor;
                        _visualizer.LineNeighborFade = lineNeighborFade;
                        _visualizer.LineBlendFalloff = lineBlendFalloff;
                        _visualizer.LineThickness = lineThickness;
                        _visualizer.LineOpacity = lineOpacity;
                        _visualizer.CornerRadius = cornerRadius;
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
                    
                    float yBias = EditorGUILayout.Slider(new GUIContent("Y Bias (m)"), _visualizer.YBias, 0f, 0.05f);
                    bool zTestAlways = EditorGUILayout.Toggle(new GUIContent("ZTest Always (draw on top)"), _visualizer.ZTestAlways);
                    
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_visualizer, "Edit Depth Settings");
                        _visualizer.YBias = yBias;
                        _visualizer.ZTestAlways = zTestAlways;
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

                    var flagColors = _visualizer.FlagColors;
                    if (flagColors != null)
                    {
                        int removeIndex = -1;
                        for (int i = 0; i < flagColors.Length; i++)
                        {
                            var fc = flagColors[i];
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                var newFlag = (CellFlag)EditorGUILayout.EnumPopup(GUIContent.none, fc.flag, GUILayout.Width(120));
                                var newColor = EditorGUILayout.ColorField(fc.color);
                                
                                if (newFlag != fc.flag || newColor != fc.color)
                                {
                                    Undo.RecordObject(_visualizer, "Edit Flag Color");
                                    _visualizer.SetFlagColor(newFlag, newColor);
                                }
                                
                                if (GUILayout.Button("×", GUILayout.Width(22)))
                                    removeIndex = i;
                            }
                        }

                        if (removeIndex >= 0)
                        {
                            Undo.RecordObject(_visualizer, "Remove Flag Color");
                            var list = new System.Collections.Generic.List<GridVisualizer.FlagColor>(flagColors);
                            list.RemoveAt(removeIndex);
                            // Note: FlagColors is readonly, need SerializedObject for this
                            var so = new SerializedObject(_visualizer);
                            var prop = so.FindProperty("_flagColors");
                            prop.arraySize = list.Count;
                            for (int i = 0; i < list.Count; i++)
                            {
                                var elem = prop.GetArrayElementAtIndex(i);
                                elem.FindPropertyRelative("flag").intValue = (int)list[i].flag;
                                elem.FindPropertyRelative("color").colorValue = list[i].color;
                            }
                            so.ApplyModifiedProperties();
                        }
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("+ Add Flag Color", GUILayout.Width(120)))
                        {
                            Undo.RecordObject(_visualizer, "Add Flag Color");
                            var so = new SerializedObject(_visualizer);
                            var prop = so.FindProperty("_flagColors");
                            int idx = prop.arraySize;
                            prop.arraySize++;
                            var elem = prop.GetArrayElementAtIndex(idx);
                            elem.FindPropertyRelative("flag").intValue = (int)CellFlag.Empty;
                            elem.FindPropertyRelative("color").colorValue = new Color(0.5f, 0.5f, 0.5f, 1f);
                            so.ApplyModifiedProperties();
                        }
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        _visualizer.MarkDirty();
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
        vis.Material = mat;
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
