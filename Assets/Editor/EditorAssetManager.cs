#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
// TODO: A* Pathfinding - Remove Unity NavMesh imports when no longer needed
// using Unity.AI.Navigation;
// using UnityEngine.AI;
using Platforms;

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
        private int _wCells = 4;
        private int _lCells = 4;
        private float _inset = 0.05f;
        private float _railY = 0f;

        private GameObject _postPrefab;
        private GameObject _railPrefab;

        // ---- A* Pathfinding Floor Geometry Settings ----
        // Creates an invisible box collider larger than the platform
        // so A* RecastGraph generates navmesh up to platform edges
        private const string NavMeshGeometryLayerName = "Platform";
        private const string NavMeshGeometryChildName = "_NavMeshFloor";
        private bool _generateNavMeshFloor = true;
        private float _navMeshFloorThickness = 0.1f;
        private float _navMeshFloorYOffset = -0.05f;
        private float _navMeshFloorExtraExtension = 0.5f; // Extends past platform edge

        private enum RailForwardAxis
        {
            PlusZ,
            MinusZ,
            PlusX,
            MinusX
        }

        [SerializeField] private RailForwardAxis _railForwardAxis = RailForwardAxis.PlusZ;

        // ---- Platform Gizmos UI state ----
        private bool _gizmosVisible = PlatformEditorUtility.GizmoSettings.ShowGizmos;
        private bool _gizmosShowIndices = PlatformEditorUtility.GizmoSettings.ShowIndices;
        private float _gizmoSphereRadius = PlatformEditorUtility.GizmoSettings.SocketSphereRadius;
        private Color _colFree = PlatformEditorUtility.GizmoSettings.ColorFree;
        private Color _colOccupied = PlatformEditorUtility.GizmoSettings.ColorOccupied;
        private Color _colLocked = PlatformEditorUtility.GizmoSettings.ColorLocked;
        private Color _colDisabled = PlatformEditorUtility.GizmoSettings.ColorDisabled;

        // ---- Foldouts & scroll ----
        private bool _foldPlatformTools = true;
        private bool _foldPlatformSetup = true;
        private bool _foldPlatformGizmos = true;
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
                            EditorGUILayout.HelpBox(
                                "Open a prefab (double-click in Project). " +
                                "Button enables only if the prefab root name contains 'Platform'.",
                                MessageType.Info);
                        }
                    }

                    EditorGUILayout.Space(6);

                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        _wCells = Mathf.Max(1, EditorGUILayout.IntField("Width  (cells, X)", _wCells));
                        _lCells = Mathf.Max(1, EditorGUILayout.IntField("Length (cells, Z)", _lCells));
                        _railY = EditorGUILayout.FloatField("Railing Y (local)", _railY);
                        _inset = Mathf.Max(0f, EditorGUILayout.FloatField("Inset inward from edge (m)", _inset));

                        EditorGUILayout.Space(4);
                        _postPrefab = (GameObject)EditorGUILayout.ObjectField("Post Prefab", _postPrefab, typeof(GameObject), false);
                        _railPrefab = (GameObject)EditorGUILayout.ObjectField("Rail Prefab (1 m)", _railPrefab, typeof(GameObject), false);

                        EditorGUILayout.Space(4);
                        _railForwardAxis = (RailForwardAxis)EditorGUILayout.EnumPopup("Rail Local Length Axis", _railForwardAxis);

                        // ---- A* NavMesh Floor Geometry ----
                        EditorGUILayout.Space(8);
                        EditorGUILayout.LabelField("A* NavMesh Floor Geometry", EditorStyles.boldLabel);
                        
                        _generateNavMeshFloor = EditorGUILayout.Toggle("Generate NavMesh Floor", _generateNavMeshFloor);
                        
                        using (new EditorGUI.DisabledScope(!_generateNavMeshFloor))
                        {
                            _navMeshFloorThickness = Mathf.Max(0.01f, 
                                EditorGUILayout.FloatField("Floor Thickness", _navMeshFloorThickness));
                            _navMeshFloorYOffset = EditorGUILayout.FloatField("Floor Y Offset", _navMeshFloorYOffset);
                            _navMeshFloorExtraExtension = Mathf.Max(0f,
                                EditorGUILayout.FloatField("Edge Extension (m)", _navMeshFloorExtraExtension));
                        }
                        
                        EditorGUILayout.HelpBox(
                            "Creates an invisible Box Collider larger than the platform.\n" +
                            "This ensures A* RecastGraph generates navmesh up to the platform edges.\n" +
                            $"Layer: '{NavMeshGeometryLayerName}' (must be in A* walkable layers)",
                            MessageType.Info);

                        bool canRun = inPrefabMode
                                      && root != null
                                      && root.name.ToLowerInvariant().Contains("platform")
                                      && _postPrefab != null
                                      && _railPrefab != null;

                        using (new EditorGUI.DisabledScope(!canRun))
                        {
                            if (GUILayout.Button("Run Platform Setup (GamePlatform + Posts & Rails)"))
                            {
                                Undo.RegisterFullObjectHierarchyUndo(root.gameObject, "Platform Setup");

                                var gp = root.GetComponent<GamePlatform>();
                                if (!gp) gp = Undo.AddComponent<GamePlatform>(root.gameObject);

                                var so = new SerializedObject(gp);
                                so.FindProperty("footprintSize").vector2IntValue = new Vector2Int(_wCells, _lCells);
                                so.ApplyModifiedProperties();

                                EditorPlatformTools.BuildSockets(gp);
                                // Note: RefreshSocketStatuses is NOT called here - it requires runtime dependencies
                                // (WorldGrid, PlatformManager) that don't exist in editor prefab mode.
                                // Socket statuses will be calculated at runtime when the platform is placed.

                                SpawnRailingsAndRegister(root, gp, _wCells, _lCells, _railY, _inset,
                                    _postPrefab, _railPrefab, _railForwardAxis);

                                // Generate A* NavMesh floor geometry
                                if (_generateNavMeshFloor)
                                {
                                    GenerateNavMeshFloorGeometry(root, _wCells, _lCells,
                                        _navMeshFloorThickness, _navMeshFloorYOffset, _navMeshFloorExtraExtension);
                                }

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
                                "• Assign Post and Rail (1 m) prefabs\n\n" +
                                "This will:\n" +
                                "• Ensure the prefab has a GamePlatform component\n" +
                                "• Build perimeter sockets\n" +
                                "• Spawn posts & rails and bind them to socket indices",
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
                        _colFree = EditorGUILayout.ColorField("Color • Free", _colFree);
                        _colOccupied = EditorGUILayout.ColorField("Color • Occupied", _colOccupied);
                        _colLocked = EditorGUILayout.ColorField("Color • Locked", _colLocked);
                        _colDisabled = EditorGUILayout.ColorField("Color • Disabled", _colDisabled);

                        EditorGUILayout.Space(4);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("Apply Gizmo Settings"))
                            {
                                PlatformEditorUtility.GizmoSettings.SetVisibility(_gizmosVisible);
                                PlatformEditorUtility.GizmoSettings.SetShowIndices(_gizmosShowIndices);
                                PlatformEditorUtility.GizmoSettings.SocketSphereRadius = _gizmoSphereRadius;
                                PlatformEditorUtility.GizmoSettings.SetColors(_colFree, _colOccupied, _colLocked, _colDisabled);
                                SceneView.RepaintAll();
                            }

                            if (GUILayout.Button("Reset Gizmo Defaults"))
                            {
                                _gizmosVisible = true;
                                _gizmosShowIndices = true;
                                _gizmoSphereRadius = 0.06f;
                                _colFree = new Color(0.20f, 1.00f, 0.20f, 0.90f);
                                _colOccupied = new Color(1.00f, 0.60f, 0.20f, 0.90f);
                                _colLocked = new Color(0.95f, 0.25f, 0.25f, 0.90f);
                                _colDisabled = new Color(0.60f, 0.60f, 0.60f, 0.90f);

                                PlatformEditorUtility.GizmoSettings.SetVisibility(_gizmosVisible);
                                PlatformEditorUtility.GizmoSettings.SetShowIndices(_gizmosShowIndices);
                                PlatformEditorUtility.GizmoSettings.SocketSphereRadius = _gizmoSphereRadius;
                                PlatformEditorUtility.GizmoSettings.SetColors(_colFree, _colOccupied, _colLocked, _colDisabled);
                                SceneView.RepaintAll();
                            }
                        }

                        EditorGUILayout.HelpBox(
                            "Shows socket index and status. Colors are driven by status.",
                            MessageType.Info);
                    }

                    EditorGUILayout.Space(6);
                    EditorGUI.indentLevel--;
                }

                // NOTE: Nav Mesh submenu removed - Unity NavMesh no longer used
                // A* Pathfinding Project will have its own graph management tools

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndScrollView();
        }

        // -------- Core placement (posts + rails) --------
        private static void SpawnRailingsAndRegister(
            Transform platformRoot,
            GamePlatform gp,
            int wCells,
            int lCells,
            float yLocal,
            float inset,
            GameObject postPrefab,
            GameObject railPrefab,
            RailForwardAxis railForwardAxis)
        {
            var railingsParent = GetOrCreate(platformRoot, "Railings");
            ClearChildren(railingsParent);

            float hx = wCells * 0.5f;
            float hz = lCells * 0.5f;

            EditorPlatformTools.BuildSockets(gp);
            // Note: No RefreshSocketStatuses - requires runtime dependencies

            // Determine prefab forward axis (local length axis of the rail)
            Vector3 localForward = railForwardAxis switch
            {
                RailForwardAxis.PlusZ => Vector3.forward,
                RailForwardAxis.MinusZ => Vector3.back,
                RailForwardAxis.PlusX => Vector3.right,
                RailForwardAxis.MinusX => Vector3.left,
                _ => Vector3.forward
            };

            Quaternion RailRotationForLocalPos(Vector3 socketLocal, float halfX, float halfZ)
            {
                const float EPS = 0.001f;
                Vector3 edgeDirLocal;

                if (Mathf.Abs(socketLocal.z - halfZ) < EPS) // north
                    edgeDirLocal = Vector3.right;
                else if (Mathf.Abs(socketLocal.z + halfZ) < EPS) // south
                    edgeDirLocal = Vector3.right;
                else if (Mathf.Abs(socketLocal.x - halfX) < EPS) // east
                    edgeDirLocal = Vector3.back;
                else // west
                    edgeDirLocal = Vector3.back;

                return Quaternion.FromToRotation(localForward, edgeDirLocal);
            }

            Vector3 RailInsetOffset(Vector3 socketLocal, float halfX, float halfZ, float insetVal)
            {
                const float EPS = 0.001f;

                if (Mathf.Abs(socketLocal.z - halfZ) < EPS)      // north edge: inward = -Z
                    return new Vector3(0f, 0f, -insetVal);
                if (Mathf.Abs(socketLocal.z + halfZ) < EPS)      // south: inward = +Z
                    return new Vector3(0f, 0f, insetVal);
                if (Mathf.Abs(socketLocal.x - halfX) < EPS)      // east: inward = -X
                    return new Vector3(-insetVal, 0f, 0f);
                // west: inward = +X
                return new Vector3(insetVal, 0f, 0f);
            }

            PlatformRailing CreateRailingComponent(
                GameObject go,
                GamePlatform platform,
                PlatformRailing.RailingType type,
                int[] socketIndices)
            {
                var pr = go.GetComponent<PlatformRailing>();
                if (!pr) pr = Undo.AddComponent<PlatformRailing>(go);

                pr.type = type;
                pr._platform = platform;
                pr.SetSocketIndices(socketIndices);

                pr.EnsureRegistered();
                return pr;
            }

            // ---- RAILS: one per socket ----
            var sockets = EditorPlatformTools.GetSockets(gp);
            if (sockets == null || sockets.Count == 0)
            {
                Debug.LogError($"[EditorAssetManager] No sockets found on platform '{gp.name}'. Make sure BuildSockets was called.");
                return;
            }
            
            for (int sIdx = 0; sIdx < sockets.Count; sIdx++)
            {
                var s = sockets[sIdx];
                Vector3 sockLocal = s.LocalPos;

                var go = (GameObject)PrefabUtility.InstantiatePrefab(railPrefab, railingsParent);
                Undo.RegisterCreatedObjectUndo(go, "Instantiate Rail");

                go.name = $"Rail_{sIdx}";

                var t = go.transform;
                t.localPosition = new Vector3(sockLocal.x, yLocal, sockLocal.z) +
                                  RailInsetOffset(sockLocal, hx, hz, inset);
                t.localRotation = RailRotationForLocalPos(sockLocal, hx, hz);

                // Only register as module if the prefab has a PlatformModule component
                var pm = go.GetComponent<PlatformModule>();
                if (pm) EditorPlatformTools.RegisterModuleOnSockets(gp, pm, occupiesSockets: true, new[] { sIdx });

                CreateRailingComponent(go, gp, PlatformRailing.RailingType.Rail, new[] { sIdx });
            }

            // ---- POSTS: corner + intermediate posts; bind to nearest 1–2 sockets ----
            
            // North edge posts: z=+hz, x=-hx..+hx
            for (int i = 0; i <= wCells; i++)
            {
                float x = -hx + i;
                float z = +hz;

                bool isWestCorner = (i == 0);
                bool isEastCorner = (i == wCells);

                if (isWestCorner) { x += inset; z -= inset; }
                else if (isEastCorner) { x -= inset; z -= inset; }
                else { z -= inset; }

                var go = (GameObject)PrefabUtility.InstantiatePrefab(postPrefab, railingsParent);
                Undo.RegisterCreatedObjectUndo(go, "Instantiate Post (North)");
                go.name = $"Post_N_{i}";

                var t = go.transform;
                t.localPosition = new Vector3(x, yLocal, z);

                // Bind to up to 2 nearest sockets
                var sockets = EditorPlatformTools.GetNearestSocketIndices(gp, t.localPosition, 2, 1.5f);
                CreateRailingComponent(go, gp, PlatformRailing.RailingType.Post, sockets.ToArray());
            }

            // South edge posts: z=-hz
            for (int i = 0; i <= wCells; i++)
            {
                float x = -hx + i;
                float z = -hz;

                bool isWestCorner = (i == 0);
                bool isEastCorner = (i == wCells);

                if (isWestCorner) { x += inset; z += inset; }
                else if (isEastCorner) { x -= inset; z += inset; }
                else { z += inset; }

                var go = (GameObject)PrefabUtility.InstantiatePrefab(postPrefab, railingsParent);
                Undo.RegisterCreatedObjectUndo(go, "Instantiate Post (South)");
                go.name = $"Post_S_{i}";

                var t = go.transform;
                t.localPosition = new Vector3(x, yLocal, z);

                var sockets = EditorPlatformTools.GetNearestSocketIndices(gp, t.localPosition, 2, 1.5f);
                CreateRailingComponent(go, gp, PlatformRailing.RailingType.Post, sockets.ToArray());
            }

            // East edge intermediate posts (skip corners)
            for (int i = 1; i < lCells; i++)
            {
                float x = +hx;
                float z = +hz - i;

                x -= inset;

                var go = (GameObject)PrefabUtility.InstantiatePrefab(postPrefab, railingsParent);
                Undo.RegisterCreatedObjectUndo(go, "Instantiate Post (East)");
                go.name = $"Post_E_{i}";

                var t = go.transform;
                t.localPosition = new Vector3(x, yLocal, z);

                var sockets = EditorPlatformTools.GetNearestSocketIndices(gp, t.localPosition, 2, 1.5f);
                CreateRailingComponent(go, gp, PlatformRailing.RailingType.Post, sockets.ToArray());
            }

            // West edge intermediate posts (skip corners)
            for (int i = 1; i < lCells; i++)
            {
                float x = -hx;
                float z = +hz - i;

                x += inset;

                var go = (GameObject)PrefabUtility.InstantiatePrefab(postPrefab, railingsParent);
                Undo.RegisterCreatedObjectUndo(go, "Instantiate Post (West)");
                go.name = $"Post_W_{i}";

                var t = go.transform;
                t.localPosition = new Vector3(x, yLocal, z);

                var sockets = EditorPlatformTools.GetNearestSocketIndices(gp, t.localPosition, 2, 1.5f);
                CreateRailingComponent(go, gp, PlatformRailing.RailingType.Post, sockets.ToArray());
            }

            EditorGUIUtility.PingObject(railingsParent);
            Debug.Log($"[EditorAssetManager] Platform setup finished on '{platformRoot.name}' ({wCells}×{lCells}), inset={inset:F2} m.");
        }

        // -------- A* NavMesh Floor Geometry --------
        
        /// <summary>
        /// Creates an invisible box collider child that extends past the platform edges.
        /// This ensures A* RecastGraph generates navmesh up to the platform edges.
        /// </summary>
        private static void GenerateNavMeshFloorGeometry(
            Transform platformRoot,
            int widthCells,
            int lengthCells,
            float thickness,
            float yOffset,
            float extraExtension)
        {
            // Remove existing floor geometry
            var existing = platformRoot.Find(NavMeshGeometryChildName);
            if (existing != null)
            {
                Undo.DestroyObjectImmediate(existing.gameObject);
            }
            
            // Create new floor geometry child
            var floorGo = new GameObject(NavMeshGeometryChildName);
            Undo.RegisterCreatedObjectUndo(floorGo, "Create NavMesh Floor");
            
            floorGo.transform.SetParent(platformRoot, false);
            floorGo.transform.localPosition = new Vector3(0f, yOffset, 0f);
            floorGo.transform.localRotation = Quaternion.identity;
            floorGo.transform.localScale = Vector3.one;
            
            // Set layer
            int layer = LayerMask.NameToLayer(NavMeshGeometryLayerName);
            if (layer >= 0)
            {
                floorGo.layer = layer;
            }
            else
            {
                Debug.LogWarning($"[EditorAssetManager] Layer '{NavMeshGeometryLayerName}' not found. " +
                                 "NavMesh floor will use Default layer. Create the layer and assign manually.");
            }
            
            // Calculate size with extension
            float width = widthCells + (extraExtension * 2f);
            float length = lengthCells + (extraExtension * 2f);
            
            // Add box collider
            var boxCollider = Undo.AddComponent<BoxCollider>(floorGo);
            boxCollider.size = new Vector3(width, thickness, length);
            boxCollider.center = Vector3.zero;
            boxCollider.isTrigger = false; // Must be solid for A* to detect
            
            // Make it invisible (no renderer needed - A* only needs the collider)
            // The collider is what A* RecastGraph scans
            
            Debug.Log($"[EditorAssetManager] Created NavMesh floor geometry: {width}x{length}m " +
                      $"(platform: {widthCells}x{lengthCells}m, extension: {extraExtension}m)");
        }

        // -------- Utilities --------
        private static (bool inPrefabMode, Transform root) GetActivePrefabRoot()
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
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
    }
}
#endif
