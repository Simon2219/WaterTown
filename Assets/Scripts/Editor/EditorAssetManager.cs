#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaterTown.Platforms;

namespace Editor
{
    public class EditorAssetManager : EditorWindow
    {
        [MenuItem("Tools/Editor Asset Manager")]
        public static void Open()
        {
            var w = GetWindow<EditorAssetManager>("Editor Asset Manager");
            w.minSize = new Vector2(620, 360);
            w.Show();
        }

        // -------- UI state --------
        private int _wCells = 4;                   // width (X), 1 m per cell
        private int _lCells = 4;                   // length (Z), 1 m per cell
        private float _inset = 0.05f;              // inward inset from edge (m)
        private float _railY = 0f;                 // local Y (0 if deck pivot is at top)
        private GameObject _straightPrefab;        // 2.0 m segment
        private GameObject _cornerPrefab;          // 1.0 m L piece (pivot at OUTER TIP)

        private const float STRAIGHT_LEN = 2.0f;
        private const float CORNER_LEN   = 1.0f;

        // ---- Platform Gizmos UI state ----
        private bool _gizmosVisible = GamePlatform.GizmoSettings.ShowGizmos;
        private bool _gizmosShowIndices = GamePlatform.GizmoSettings.ShowIndices;
        private float _gizmoSphereRadius = GamePlatform.GizmoSettings.SocketSphereRadius;
        private Color _colFree     = GamePlatform.GizmoSettings.ColorFree;
        private Color _colOccupied = GamePlatform.GizmoSettings.ColorOccupied;
        private Color _colLocked   = GamePlatform.GizmoSettings.ColorLocked;
        private Color _colDisabled = GamePlatform.GizmoSettings.ColorDisabled;

        // ---- Foldouts & scroll ----
        private bool _foldPlatformTools = true;
        private bool _foldPlatformSetup = true;   // renamed
        private bool _foldPlatformGizmos = true;
        private bool _foldNavMesh = true;
        private Vector2 _scroll;

        private GUIStyle _hdr;
        private void EnsureStyles()
        {
            if (_hdr == null) _hdr = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
        }
        private void H(string t) { EnsureStyles(); EditorGUILayout.LabelField(t, _hdr); }

        private void OnGUI()
        {
            EnsureStyles();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // ===== Platform Tools (parent foldout) =====
            _foldPlatformTools = EditorGUILayout.Foldout(_foldPlatformTools, "Platform Tools", true);
            if (_foldPlatformTools)
            {
                EditorGUI.indentLevel++;

                // -------- Submenu: Platform Setup --------
                _foldPlatformSetup = EditorGUILayout.Foldout(_foldPlatformSetup, "Platform Setup", true);
                if (_foldPlatformSetup)
                {
                    EditorGUI.indentLevel++;
                    H("Platform Setup");

                    var (inPrefabMode, root) = GetActivePrefabRoot();
                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        EditorGUILayout.LabelField("Prefab Mode:", inPrefabMode ? "YES" : "NO");
                        if (inPrefabMode && root != null)
                        {
                            EditorGUILayout.LabelField("Active Prefab Root:", root.name);
                            EditorGUILayout.ObjectField("Root Object", root, typeof(Transform), true);
                        }
                        else
                        {
                            EditorGUILayout.HelpBox("Open a prefab (double-click in Project). Button enables only if the prefab root name contains 'Platform'.", MessageType.Info);
                        }
                    }

                    EditorGUILayout.Space(6);

                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        _wCells = Mathf.Max(1, EditorGUILayout.IntField("Width  (cells, X)", _wCells));
                        _lCells = Mathf.Max(1, EditorGUILayout.IntField("Length (cells, Z)", _lCells));
                        _railY  = EditorGUILayout.FloatField("Railing Y (local)", _railY);
                        _inset  = Mathf.Max(0f, EditorGUILayout.FloatField("Inset inward from edge (m)", _inset));

                        EditorGUILayout.Space(4);
                        _straightPrefab = (GameObject)EditorGUILayout.ObjectField("Straight Prefab (2 m)", _straightPrefab, typeof(GameObject), false);
                        _cornerPrefab   = (GameObject)EditorGUILayout.ObjectField("Corner Prefab (1 m, pivot at OUTER TIP)", _cornerPrefab, typeof(GameObject), false);

                        bool canRun = inPrefabMode
                                      && root != null
                                      && root.name.ToLowerInvariant().Contains("platform")
                                      && _straightPrefab != null
                                      && _cornerPrefab   != null;

                        using (new EditorGUI.DisabledScope(!canRun))
                        {
                            if (GUILayout.Button("Run Platform Setup (Attach GamePlatform + Spawn Railings)"))
                            {
                                Undo.RegisterFullObjectHierarchyUndo(root.gameObject, "Platform Setup");

                                // Ensure GamePlatform exists, set footprint, build sockets
                                var gp = root.GetComponent<GamePlatform>();
                                if (!gp) gp = Undo.AddComponent<GamePlatform>(root.gameObject);

                                var so = new SerializedObject(gp);
                                so.FindProperty("footprint").vector2IntValue = new Vector2Int(_wCells, _lCells);
                                so.ApplyModifiedProperties();
                                gp.BuildSockets();

                                // Spawn railings & register them by nearest sockets (position-based)
                                SpawnRailingsAndRegisterByPosition(root, gp, _wCells, _lCells, _railY, _inset, _straightPrefab, _cornerPrefab);

                                EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
                                EditorGUIUtility.PingObject(root);
                            }
                        }

                        if (!canRun)
                        {
                            EditorGUILayout.HelpBox(
                                "Requirements:\n" +
                                "• Open a Prefab (Prefab Mode)\n" +
                                "• Prefab root name contains 'Platform'\n" +
                                "• Assign Straight (2 m) and Corner (1 m) prefabs\n\n" +
                                "This will:\n" +
                                "• Ensure the prefab has a GamePlatform component\n" +
                                "• Build perimeter sockets at 1 m spacing (clockwise from NE)\n" +
                                "• Spawn railings and register them to the nearest socket(s)",
                                MessageType.Warning);
                        }
                    }

                    EditorGUILayout.Space(6);
                    EditorGUI.indentLevel--;
                }

                // -------- Submenu: Platform Gizmos --------
                _foldPlatformGizmos = EditorGUILayout.Foldout(_foldPlatformGizmos, "Platform Gizmos", true);
                if (_foldPlatformGizmos)
                {
                    EditorGUI.indentLevel++;
                    H("Platform Gizmos");

                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        _gizmosVisible = EditorGUILayout.Toggle("Show Gizmos", _gizmosVisible);
                        _gizmosShowIndices = EditorGUILayout.Toggle("Show Indices", _gizmosShowIndices);
                        _gizmoSphereRadius = Mathf.Max(0.0f, EditorGUILayout.FloatField("Socket Sphere Radius", _gizmoSphereRadius));

                        EditorGUILayout.Space(4);
                        _colFree     = EditorGUILayout.ColorField("Color • Free",     _colFree);
                        _colOccupied = EditorGUILayout.ColorField("Color • Occupied", _colOccupied);
                        _colLocked   = EditorGUILayout.ColorField("Color • Locked",   _colLocked);
                        _colDisabled = EditorGUILayout.ColorField("Color • Disabled", _colDisabled);

                        EditorGUILayout.Space(4);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("Apply Gizmo Settings"))
                            {
                                GamePlatform.GizmoSettings.SetVisibility(_gizmosVisible);
                                GamePlatform.GizmoSettings.SetShowIndices(_gizmosShowIndices);
                                GamePlatform.GizmoSettings.SocketSphereRadius = _gizmoSphereRadius;
                                GamePlatform.GizmoSettings.SetColors(_colFree, _colOccupied, _colLocked, _colDisabled);
                                SceneView.RepaintAll();
                            }

                            if (GUILayout.Button("Reset Gizmo Defaults"))
                            {
                                _gizmosVisible = true;
                                _gizmosShowIndices = true;
                                _gizmoSphereRadius = 0.06f;
                                _colFree     = new Color(0.20f, 1.00f, 0.20f, 0.90f);
                                _colOccupied = new Color(1.00f, 0.60f, 0.20f, 0.90f);
                                _colLocked   = new Color(0.95f, 0.25f, 0.25f, 0.90f);
                                _colDisabled = new Color(0.60f, 0.60f, 0.60f, 0.90f);

                                GamePlatform.GizmoSettings.SetVisibility(_gizmosVisible);
                                GamePlatform.GizmoSettings.SetShowIndices(_gizmosShowIndices);
                                GamePlatform.GizmoSettings.SocketSphereRadius = _gizmoSphereRadius;
                                GamePlatform.GizmoSettings.SetColors(_colFree, _colOccupied, _colLocked, _colDisabled);
                                SceneView.RepaintAll();
                            }
                        }

                        EditorGUILayout.HelpBox(
                            "Shows socket index, perimeter mark, and bound module names. Colors are driven by status.",
                            MessageType.Info);
                    }

                    EditorGUILayout.Space(6);
                    EditorGUI.indentLevel--;
                }

                // -------- Submenu: Nav Mesh --------
                _foldNavMesh = EditorGUILayout.Foldout(_foldNavMesh, "Nav Mesh", true);
                if (_foldNavMesh)
                {
                    EditorGUI.indentLevel++;
                    H("Nav Mesh");

                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        var (prefabMode, transform) = GetActivePrefabRoot();
                        GamePlatform gp = null;
                        if (prefabMode && transform) gp = transform.GetComponent<GamePlatform>();

                        using (new EditorGUI.DisabledScope(!(prefabMode && gp)))
                        {
                            if (GUILayout.Button("Rebuild NavMesh (Active Prefab)"))
                            {
                                Undo.RegisterFullObjectHierarchyUndo(transform.gameObject, "Rebuild NavMesh (Prefab)");
                                gp.BuildLocalNavMesh();
                                EditorSceneManager.MarkSceneDirty(transform.gameObject.scene);
                                Debug.Log($"[EditorAssetManager] Rebuilt NavMesh for prefab '{transform.name}'.");
                            }
                        }

                        if (GUILayout.Button("Rebuild NavMesh for ALL GamePlatforms in Open Scenes"))
                        {
                            RebuildAllPlatformsInOpenScenes();
                        }

                        EditorGUILayout.HelpBox(
                            "Use the first button while editing a prefab.\n" +
                            "Use the second to batch-rebuild every GamePlatform in all open scenes.",
                            MessageType.Info);
                    }

                    EditorGUI.indentLevel--;
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndScrollView();
        }

        // -------- Core placement (position-based registration) --------
        private static void SpawnRailingsAndRegisterByPosition(
            Transform platformRoot, GamePlatform gp,
            int wCells, int lCells, float yLocal, float inset,
            GameObject straightPrefab, GameObject cornerPrefab)
        {
            var railings = GetOrCreate(platformRoot, "Railings");
            ClearChildren(railings);

            float hx = wCells * 0.5f; // X half
            float hz = lCells * 0.5f; // Z half

            var straightAxes = ClassifyAxes(straightPrefab);
            var cornerAxes   = ClassifyAxes(cornerPrefab);

            // ----- Corners -----
            const float YAW_NE = 270f;
            const float YAW_SE = 0f;
            const float YAW_SW = 90f;
            const float YAW_NW = 180f;

            // Helper to place & register one corner (bind to its single nearest socket)
            void Corner(Vector3 baseCorner, Vector3 inwardX, Vector3 inwardZ, string label, float yaw)
            {
                Vector3 pos = baseCorner + inwardX * inset + inwardZ * inset;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(cornerPrefab, railings);
                go.name = label;
                Undo.RegisterCreatedObjectUndo(go, "Instantiate Corner");
                go.transform.localPosition = pos;

                var rot = ComputeCornerLegAlignment(cornerAxes, inwardX, inwardZ);
                go.transform.localRotation = ApplyFinalYaw(rot, yaw);

                // Register to nearest socket
                int idx = gp.FindNearestSocketIndexLocal(pos);
                gp.RegisterModuleOnSockets(go, true, new[] { idx });
            }

            // NE / SE / SW / NW
            Corner(new Vector3(+hx, yLocal, +hz), Vector3.left,  Vector3.back,    "Corner_NE", YAW_NE);
            Corner(new Vector3(+hx, yLocal, -hz), Vector3.left,  Vector3.forward, "Corner_SE", YAW_SE);
            Corner(new Vector3(-hx, yLocal, -hz), Vector3.right, Vector3.forward, "Corner_SW", YAW_SW);
            Corner(new Vector3(-hx, yLocal, +hz), Vector3.right, Vector3.back,    "Corner_NW", YAW_NW);

            // ----- Middles (2 m), centered between corners on each edge) -----
            float usableX = wCells - 2f * CORNER_LEN; // N/S edges
            float usableZ = lCells - 2f * CORNER_LEN; // E/W edges
            int countX = Mathf.Max(0, Mathf.RoundToInt(usableX / STRAIGHT_LEN));
            int countZ = Mathf.Max(0, Mathf.RoundToInt(usableZ / STRAIGHT_LEN));

            // Each straight will register to 3 nearest sockets (≈ -1, 0, +1 marks)
            List<Vector3> centers = new();

            void Straights(Vector3 center, Vector3 tangent, Vector3 inward, int count, float spacing, string tag)
            {
                if (count <= 0) return;

                float run = count * spacing;
                float start = -run * 0.5f + spacing * 0.5f;

                for (int i = 0; i < count; i++)
                {
                    float along = start + i * spacing;
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(straightPrefab, railings);
                    go.name = $"Mid_{tag}_{i}";
                    Undo.RegisterCreatedObjectUndo(go, "Instantiate Straight");

                    Vector3 localCenter = center + tangent * along + inward * Mathf.Abs(inset);
                    go.transform.localPosition = localCenter;
                    go.transform.localRotation = ComputeStraightAlignment(straightAxes, tangent, inward);

                    // gather ~3 anchor points: center, +/- 1 m along tangent (local)
                    centers.Clear();
                    centers.Add(localCenter);
                    centers.Add(localCenter + tangent * 1f);
                    centers.Add(localCenter - tangent * 1f);

                    gp.RegisterModuleByLocalPositions(go, true, centers);
                }
            }

            Straights(new Vector3(0f, yLocal, +hz), Vector3.right,  Vector3.back,    countX, STRAIGHT_LEN, "N");
            Straights(new Vector3(0f, yLocal, -hz), Vector3.right,  Vector3.forward, countX, STRAIGHT_LEN, "S");
            Straights(new Vector3(+hx, yLocal, 0f), Vector3.forward,Vector3.left,    countZ, STRAIGHT_LEN, "E");
            Straights(new Vector3(-hx, yLocal, 0f), Vector3.forward,Vector3.right,   countZ, STRAIGHT_LEN, "W");

            EditorGUIUtility.PingObject(railings);
            Debug.Log($"[EditorAssetManager] Platform setup finished on '{platformRoot.name}' ({wCells}×{lCells}), inset={inset:F2} m.");
        }

        // -------- Orientation logic --------
        private struct AxisInfo
        {
            public Vector3 lengthAxis;
            public Vector3 thicknessAxis;
        }

        private static AxisInfo ClassifyAxes(GameObject prefab)
        {
            var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
            if (filters == null || filters.Length == 0)
                return new AxisInfo { lengthAxis = Vector3.right, thicknessAxis = Vector3.forward };

            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool init = false;
            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (!mesh) continue;
                var b = mesh.bounds;
                if (!init) { bounds = b; init = true; }
                else { bounds.Encapsulate(b); }
            }

            float ex = Mathf.Abs(bounds.size.x);
            float ez = Mathf.Abs(bounds.size.z);

            if (ex >= ez)
                return new AxisInfo { lengthAxis = Vector3.right, thicknessAxis = Vector3.forward };
            else
                return new AxisInfo { lengthAxis = Vector3.forward, thicknessAxis = Vector3.right };
        }

        private static Quaternion ComputeStraightAlignment(AxisInfo axes, Vector3 tangent, Vector3 inward)
        {
            float best = float.NegativeInfinity;
            Quaternion bestRot = Quaternion.identity;
            float[] yaws = { 0f, 90f, 180f, 270f };

            foreach (var yaw in yaws)
            {
                var rot = Quaternion.Euler(0f, yaw, 0f);
                var len = rot * axes.lengthAxis;    len.y = 0f; if (len != Vector3.zero) len.Normalize();
                var thk = rot * axes.thicknessAxis; thk.y = 0f; if (thk != Vector3.zero) thk.Normalize();

                float s = Mathf.Abs(Vector3.Dot(len, tangent));
                float o = Mathf.Abs(Vector3.Dot(thk, inward));
                float score = s * s + o * o;
                if (score > best) { best = score; bestRot = rot; }
            }
            return bestRot;
        }

        private static Quaternion ComputeCornerLegAlignment(AxisInfo axes, Vector3 inwardX, Vector3 inwardZ)
        {
            float best = float.NegativeInfinity;
            Quaternion bestRot = Quaternion.identity;
            float[] yaws = { 0f, 90f, 180f, 270f };

            Vector3 a1 = axes.lengthAxis;
            Vector3 a2 = (a1 == Vector3.right) ? Vector3.forward : Vector3.right;

            foreach (var yaw in yaws)
            {
                var rot = Quaternion.Euler(0f, yaw, 0f);
                Vector3 v1 = rot * a1; v1.y = 0f; if (v1 != Vector3.zero) v1.Normalize();
                Vector3 v2 = rot * a2; v2.y = 0f; if (v2 != Vector3.zero) v2.Normalize();

                float s1 = Mathf.Abs(Vector3.Dot(v1, inwardX)) + Mathf.Abs(Vector3.Dot(v2, inwardZ));
                float s2 = Mathf.Abs(Vector3.Dot(v1, inwardZ)) + Mathf.Abs(Vector3.Dot(v2, inwardX));
                float orthPenalty = Mathf.Abs(Vector3.Dot(v1, v2));
                float score = Mathf.Max(s1, s2) - orthPenalty * 0.25f;

                if (score > best) { best = score; bestRot = rot; }
            }
            return bestRot;
        }

        private static Quaternion ApplyFinalYaw(Quaternion current, float yawDeg)
        {
            yawDeg %= 360f; if (yawDeg < 0f) yawDeg += 360f;
            Vector3 f = current * Vector3.forward; f.y = 0f; if (f != Vector3.zero) f.Normalize();
            float currentYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            float delta = yawDeg - currentYaw;
            return Quaternion.Euler(0f, delta, 0f) * current;
        }

        // -------- Utilities --------
        private static (bool inPrefabMode, Transform root) GetActivePrefabRoot()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null)
                return (true, stage.prefabContentsRoot.transform);
            return (false, null);
        }

        private static Transform GetOrCreate(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (!t)
            {
                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Create Child");
                t = go.transform;
                t.SetParent(parent, false);
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }
            return t;
        }

        private static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                Undo.DestroyObjectImmediate(t.GetChild(i).gameObject);
        }

        private void RebuildAllPlatformsInOpenScenes()
        {
            int total = 0;
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    var platforms = root.GetComponentsInChildren<GamePlatform>(true);
                    foreach (var p in platforms)
                    {
                        var surface = p.GetComponent<NavMeshSurface>();
                        if (!surface) continue;
                        Undo.RegisterFullObjectHierarchyUndo(p.gameObject, "Rebuild All NavMeshes");
                        surface.BuildNavMesh();
                        total++;
                    }
                }

                EditorSceneManager.MarkSceneDirty(scene);
            }
            Debug.Log($"[EditorAssetManager] Rebuilt NavMesh on {total} GamePlatform(s) across open scenes.");
        }
    }
}
#endif
