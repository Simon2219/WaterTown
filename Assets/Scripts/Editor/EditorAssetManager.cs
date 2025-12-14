#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
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

        // ---- NavMesh Floor Geometry settings ----
        private const string NavMeshGeometryLayerName = "NavMeshGeometry";
        private const string NavMeshGeometryChildName = "NavMeshGeometry";
        
        private bool _generateNavMeshFloor = true;
        private float _navMeshAgentRadius = 0.3f;
        private float _navMeshFloorThickness = 0.1f;
        private float _navMeshFloorYOffset = 0f;
        private float _navMeshFloorExtraExtension = 0f; // Additional extension beyond agent radius if needed

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

                        EditorGUILayout.Space(8);
                        EditorGUILayout.LabelField("NavMesh Floor Geometry", EditorStyles.boldLabel);
                        
                        _generateNavMeshFloor = EditorGUILayout.Toggle("Generate NavMesh Floor", _generateNavMeshFloor);
                        
                        using (new EditorGUI.DisabledScope(!_generateNavMeshFloor))
                        {
                            EditorGUI.indentLevel++;
                            _navMeshAgentRadius = Mathf.Max(0.01f, EditorGUILayout.FloatField(
                                new GUIContent("Agent Radius", "NavMesh agent radius. Floor extends by this amount past platform edge."),
                                _navMeshAgentRadius));
                            _navMeshFloorExtraExtension = Mathf.Max(0f, EditorGUILayout.FloatField(
                                new GUIContent("Extra Extension", "Additional extension beyond agent radius (usually 0)."),
                                _navMeshFloorExtraExtension));
                            _navMeshFloorThickness = Mathf.Max(0.01f, EditorGUILayout.FloatField(
                                new GUIContent("Floor Thickness", "Height of the floor collider (thin is fine)."),
                                _navMeshFloorThickness));
                            _navMeshFloorYOffset = EditorGUILayout.FloatField(
                                new GUIContent("Floor Y Offset", "Y position offset from platform origin (usually 0 for floor level)."),
                                _navMeshFloorYOffset);
                            EditorGUI.indentLevel--;
                        }
                        
                        // Check if NavMeshGeometry layer exists
                        int navMeshLayer = LayerMask.NameToLayer(NavMeshGeometryLayerName);
                        if (_generateNavMeshFloor && navMeshLayer == -1)
                        {
                            EditorGUILayout.HelpBox(
                                $"Layer '{NavMeshGeometryLayerName}' not found!\n" +
                                "Please create this layer in Edit > Project Settings > Tags and Layers.",
                                MessageType.Error);
                        }

                        bool canRun = inPrefabMode
                                      && root != null
                                      && root.name.ToLowerInvariant().Contains("platform")
                                      && _postPrefab != null
                                      && _railPrefab != null
                                      && (!_generateNavMeshFloor || navMeshLayer != -1);

                        using (new EditorGUI.DisabledScope(!canRun))
                        {
                            if (GUILayout.Button("Run Platform Setup (GamePlatform + NavMeshSurface + Posts & Rails)"))
                            {
                                Undo.RegisterFullObjectHierarchyUndo(root.gameObject, "Platform Setup");

                                var gp = root.GetComponent<GamePlatform>();
                                if (!gp) gp = Undo.AddComponent<GamePlatform>(root.gameObject);

                                var so = new SerializedObject(gp);
                                so.FindProperty("footprintSize").vector2IntValue = new Vector2Int(_wCells, _lCells);
                                so.ApplyModifiedProperties();

                                gp.BuildSockets();
                                gp.RefreshSocketStatuses();

                                // Generate NavMesh floor geometry (before NavMeshSurface setup)
                                if (_generateNavMeshFloor)
                                {
                                    GenerateNavMeshFloorGeometry(root, _wCells, _lCells, 
                                        _navMeshAgentRadius, _navMeshFloorExtraExtension,
                                        _navMeshFloorThickness, _navMeshFloorYOffset);
                                }

                                EnsureNavMeshSurface(root, _generateNavMeshFloor);

                                SpawnRailingsAndRegister(root, gp, _wCells, _lCells, _railY, _inset,
                                    _postPrefab, _railPrefab, _railForwardAxis);

                                BuildAndSaveNavMeshForPrefab(gp, root);

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
                                "• Assign Post and Rail (1 m) prefabs\n" +
                                "• If NavMesh Floor enabled: '" + NavMeshGeometryLayerName + "' layer must exist\n\n" +
                                "This will:\n" +
                                "• Ensure the prefab has a GamePlatform component\n" +
                                "• Build perimeter sockets\n" +
                                "• Generate NavMesh floor geometry (extends past edge for proper NavMesh coverage)\n" +
                                "• Ensure a NavMeshSurface on the prefab root\n" +
                                "• Spawn posts & rails and bind them to socket indices\n" +
                                "• Build and save a per-platform NavMeshData asset",
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
                        
                        // Check if this platform has NavMesh floor geometry
                        bool hasNavMeshFloor = transform && transform.Find(NavMeshGeometryChildName);
                        
                        if (prefabMode && gp)
                        {
                            EditorGUILayout.LabelField("NavMesh Floor:", hasNavMeshFloor ? "Present" : "Not found");
                        }

                        using (new EditorGUI.DisabledScope(!(prefabMode && gp)))
                        {
                            if (GUILayout.Button("Rebuild NavMesh (Active Prefab)"))
                            {
                                Undo.RegisterFullObjectHierarchyUndo(transform.gameObject, "Rebuild NavMesh (Prefab)");
                                EnsureNavMeshSurface(transform, hasNavMeshFloor);
                                BuildAndSaveNavMeshForPrefab(gp, transform);
                                EditorSceneManager.MarkSceneDirty(transform.gameObject.scene);
                            }
                        }

                        if (GUILayout.Button("Rebuild NavMesh for ALL GamePlatforms in Open Scenes"))
                        {
                            RebuildAllPlatformsInOpenScenes();
                        }

                        EditorGUILayout.HelpBox(
                            "Use the first button while editing a prefab.\n" +
                            "Use the second to batch-rebuild every GamePlatform in all open scenes.\n\n" +
                            "Note: If NavMesh Floor geometry exists, NavMesh will be built from that layer only.",
                            MessageType.Info);
                    }

                    EditorGUI.indentLevel--;
                }

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

            gp.BuildSockets();
            gp.RefreshSocketStatuses();

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
                pr.platform = platform;
                pr.SetSocketIndices(socketIndices);

                pr.EnsureRegistered();
                return pr;
            }

            // ---- RAILS: one per socket ----
            var sockets = gp.Sockets;
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
                if (pm) gp.RegisterModuleOnSockets(pm, occupiesSockets: true, new[] { sIdx });

                CreateRailingComponent(go, gp, PlatformRailing.RailingType.Rail, new[] { sIdx });
            }

            // ---- POSTS: corner + intermediate posts; bind to nearest 1–2 sockets ----
            List<int> tmpSockets = new();

            // Corner + edge post positions (same layout as old code, but we compute neighbors via nearest sockets)
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
                tmpSockets.Clear();
                gp.FindNearestSocketIndicesLocal(t.localPosition, maxCount: 2, maxDistance: 1.5f, result: tmpSockets);
                CreateRailingComponent(go, gp, PlatformRailing.RailingType.Post, tmpSockets.ToArray());
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

                tmpSockets.Clear();
                gp.FindNearestSocketIndicesLocal(t.localPosition, maxCount: 2, maxDistance: 1.5f, result: tmpSockets);
                CreateRailingComponent(go, gp, PlatformRailing.RailingType.Post, tmpSockets.ToArray());
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

                tmpSockets.Clear();
                gp.FindNearestSocketIndicesLocal(t.localPosition, maxCount: 2, maxDistance: 1.5f, result: tmpSockets);
                CreateRailingComponent(go, gp, PlatformRailing.RailingType.Post, tmpSockets.ToArray());
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

                tmpSockets.Clear();
                gp.FindNearestSocketIndicesLocal(t.localPosition, maxCount: 2, maxDistance: 1.5f, result: tmpSockets);
                CreateRailingComponent(go, gp, PlatformRailing.RailingType.Post, tmpSockets.ToArray());
            }

            EditorGUIUtility.PingObject(railingsParent);
            Debug.Log($"[EditorAssetManager] Platform setup finished on '{platformRoot.name}' ({wCells}×{lCells}), inset={inset:F2} m.");
        }

        // -------- NavMesh helpers --------

        /// <summary>
        /// Generates a floor collider that extends past the platform edge for proper NavMesh coverage.
        /// This allows adjacent platform NavMeshes to merge without needing NavMeshLinks.
        /// </summary>
        private static void GenerateNavMeshFloorGeometry(
            Transform platformRoot,
            int widthCells,
            int lengthCells,
            float agentRadius,
            float extraExtension,
            float floorThickness,
            float yOffset)
        {
            if (!platformRoot) return;
            
            int layer = LayerMask.NameToLayer(NavMeshGeometryLayerName);
            if (layer == -1)
            {
                Debug.LogError($"[EditorAssetManager] Layer '{NavMeshGeometryLayerName}' not found. Cannot generate NavMesh floor geometry.");
                return;
            }
            
            // Remove existing NavMesh geometry child if present
            var existingChild = platformRoot.Find(NavMeshGeometryChildName);
            if (existingChild)
            {
                Undo.DestroyObjectImmediate(existingChild.gameObject);
            }
            
            // Create new NavMesh geometry GameObject
            var navMeshGo = new GameObject(NavMeshGeometryChildName);
            Undo.RegisterCreatedObjectUndo(navMeshGo, "Create NavMesh Floor Geometry");
            
            navMeshGo.transform.SetParent(platformRoot, false);
            navMeshGo.transform.localPosition = new Vector3(0f, yOffset, 0f);
            navMeshGo.transform.localRotation = Quaternion.identity;
            navMeshGo.transform.localScale = Vector3.one;
            navMeshGo.layer = layer;
            
            // Calculate floor size: platform footprint + extension on all sides
            // Extension = agentRadius + extraExtension
            // This ensures NavMesh extends to visual edge after erosion
            float totalExtension = agentRadius + extraExtension;
            float floorWidth = widthCells + (totalExtension * 2f);
            float floorLength = lengthCells + (totalExtension * 2f);
            
            // Add BoxCollider
            var collider = Undo.AddComponent<BoxCollider>(navMeshGo);
            collider.size = new Vector3(floorWidth, floorThickness, floorLength);
            collider.center = new Vector3(0f, -floorThickness * 0.5f, 0f); // Top of collider at Y=0
            collider.isTrigger = false; // NavMesh needs solid colliders
            
            Debug.Log($"[EditorAssetManager] Generated NavMesh floor geometry: " +
                      $"{floorWidth:F2}x{floorLength:F2}m (platform: {widthCells}x{lengthCells}m, extension: {totalExtension:F2}m per side)");
        }

        private static void EnsureNavMeshSurface(Transform root, bool useNavMeshFloorLayer = false)
        {
            if (!root) return;

            var surface = root.GetComponent<NavMeshSurface>();
            if (!surface)
            {
                surface = Undo.AddComponent<NavMeshSurface>(root.gameObject);
            }

            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            
            // Configure layer mask based on whether we're using NavMesh floor geometry
            if (useNavMeshFloorLayer)
            {
                int navMeshLayer = LayerMask.NameToLayer(NavMeshGeometryLayerName);
                if (navMeshLayer != -1)
                {
                    // Only collect from NavMeshGeometry layer
                    surface.layerMask = 1 << navMeshLayer;
                    Debug.Log($"[EditorAssetManager] NavMeshSurface configured to use '{NavMeshGeometryLayerName}' layer only.");
                }
                else
                {
                    Debug.LogWarning($"[EditorAssetManager] Layer '{NavMeshGeometryLayerName}' not found. Using default layer mask.");
                }
            }
            // else: keep existing/default layer mask
            
            // Agent Type can be configured in the NavMeshSurface Inspector
            // Must match the agent type used by your NPCs
        }

        private static void BuildAndSaveNavMeshForPrefab(GamePlatform gp, Transform root)
        {
            if (!gp || !root) return;

            var surface = gp.GetComponent<NavMeshSurface>();
            if (!surface)
            {
                surface = Undo.AddComponent<NavMeshSurface>(root.gameObject);
                surface.collectObjects = CollectObjects.Children;
            }

            surface.BuildNavMesh();

            var data = surface.navMeshData;
            if (data == null) return;

            string existingPath = AssetDatabase.GetAssetPath(data);
            if (!string.IsNullOrEmpty(existingPath))
            {
                Debug.Log($"[EditorAssetManager] Rebuilt existing NavMeshData at {existingPath}", surface);
                return;
            }

            string prefabPath = AssetDatabase.GetAssetPath(root.gameObject);
            string folder = string.IsNullOrEmpty(prefabPath)
                ? "Assets"
                : Path.GetDirectoryName(prefabPath);

            string fileName = $"{root.name}_NavMesh.asset";
            string targetPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(folder, fileName));

            AssetDatabase.CreateAsset(data, targetPath);
            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(surface);
            Debug.Log($"[EditorAssetManager] Saved NavMeshData asset: {targetPath}", surface);
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

        private void RebuildAllPlatformsInOpenScenes()
        {
            int total = 0;
            int withNavMeshFloor = 0;
            
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
                        
                        // Check if this platform has NavMesh floor geometry
                        bool hasNavMeshFloor = p.transform.Find(NavMeshGeometryChildName);
                        if (hasNavMeshFloor)
                        {
                            withNavMeshFloor++;
                            // Ensure surface is configured to use NavMesh floor layer
                            EnsureNavMeshSurface(p.transform, useNavMeshFloorLayer: true);
                        }
                        
                        surface.BuildNavMesh();
                        total++;
                    }
                }

                EditorSceneManager.MarkSceneDirty(scene);
            }
            Debug.Log($"[EditorAssetManager] Rebuilt NavMesh on {total} GamePlatform(s) across open scenes ({withNavMeshFloor} with NavMesh floor geometry).");
        }
    }
}
#endif
