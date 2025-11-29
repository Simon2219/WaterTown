using System.Collections;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace WaterTown.Platforms
{
    [DisallowMultipleComponent]
    public class GamePlatform : MonoBehaviour
    {
        // ---------- Footprint & NavMesh ----------
        [Header("Footprint (cells @ 1m)")]
        [SerializeField] private Vector2Int footprint = new Vector2Int(4, 4);
        public Vector2Int Footprint => footprint;

        [Header("NavMesh (assign in Inspector)")]
        [SerializeField] private NavMeshSurface navSurface;
        [SerializeField] private float rebuildDebounceSeconds = 0.1f;
        private Coroutine _pendingRebuild;

        // --- Pose change notifications ---
        public event System.Action<GamePlatform> PoseChanged;

        private Vector3 _lastPos;
        private Quaternion _lastRot;
        private Vector3 _lastScale;
        
        private void OnEnable()
        {
            _lastPos = transform.position;
            _lastRot = transform.rotation;
            _lastScale = transform.localScale;
        }

        
        private void Reset()
        {
            if (!navSurface) navSurface = GetComponent<NavMeshSurface>();
        }

        private void Awake()
        {
            if (!navSurface) navSurface = GetComponent<NavMeshSurface>();
            if (!_socketsBuilt) BuildSockets();
            EnsureChildrenModulesRegistered();
            RefreshSocketStatuses();
        }
        
        private void LateUpdate()
        {
            // 90° steps in your game → rotations can snap; compare directly is ok
            if (transform.position != _lastPos ||
                transform.rotation != _lastRot ||
                transform.localScale != _lastScale)
            {
                _lastPos = transform.position;
                _lastRot = transform.rotation;
                _lastScale = transform.localScale;

                // notify listeners (e.g., TownManager) that adjacency should be recomputed
                PoseChanged?.Invoke(this);
            }
        }

        /// <summary>Manually trigger a pose change notification (e.g., after programmatic teleports).</summary>
        public void ForcePoseChanged()
        {
            _lastPos = transform.position;
            _lastRot = transform.rotation;
            _lastScale = transform.localScale;
            PoseChanged?.Invoke(this);
        }
        
        public void BuildLocalNavMesh()
        {
            if (navSurface) navSurface.BuildNavMesh();
        }

        public void QueueRebuild()
        {
            if (!navSurface) return;
            if (_pendingRebuild != null) StopCoroutine(_pendingRebuild);
            _pendingRebuild = StartCoroutine(RebuildAfterDelay(rebuildDebounceSeconds));
        }

        private IEnumerator RebuildAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (navSurface) navSurface.BuildNavMesh();
            _pendingRebuild = null;
        }

        // ---------- Legacy (compat) ----------
        // Some of your other scripts still reference LinkChannel. Re-introduce a minimal, unused version.
        [System.Flags]
        [System.Obsolete("LinkChannel is legacy and no longer used for logic. Blocks come from PlatformModule.blocksLink.")]
        public enum LinkChannel { None = 0, Walk = 1 << 0, All = ~0 }

        [Header("Legacy (unused, kept for backward compatibility)")]
        [SerializeField, Tooltip("LEGACY: not used anymore; kept so older editor data & code compile.")]
        [System.Obsolete("DefaultSocketChannels is legacy and unused.")]
        private LinkChannel defaultSocketChannels = LinkChannel.Walk;

        [System.Obsolete("DefaultSocketChannels is legacy and unused.")]
        public LinkChannel DefaultSocketChannels => defaultSocketChannels;

        // ---------- Edges / Helpers ----------
        public enum Edge { North, East, South, West }
        public static Edge Opposite(Edge e) => e switch
        {
            Edge.North => Edge.South,
            Edge.South => Edge.North,
            Edge.East  => Edge.West,
            Edge.West  => Edge.East,
            _ => Edge.North
        };

        public int EdgeLengthMeters(Edge edge) =>
            (edge == Edge.North || edge == Edge.South) ? footprint.x : footprint.y;

        public (Vector3 center, Vector3 outward) GetEdgeInfo(Edge edge)
        {
            float hx = footprint.x * 0.5f;
            float hz = footprint.y * 0.5f;

            Vector3 localCenter = edge switch
            {
                Edge.North => new Vector3(0f, 0f, +hz),
                Edge.South => new Vector3(0f, 0f, -hz),
                Edge.East  => new Vector3(+hx, 0f, 0f),
                Edge.West  => new Vector3(-hx, 0f, 0f),
                _ => Vector3.zero
            };
            Vector3 localOut = edge switch
            {
                Edge.North => Vector3.forward,
                Edge.South => Vector3.back,
                Edge.East  => Vector3.right,
                Edge.West  => Vector3.left,
                _ => Vector3.forward
            };

            return (transform.TransformPoint(localCenter), transform.TransformDirection(localOut));
        }

        // ---------- Sockets ----------
        public enum SocketLocation { Corner, Edge }
        public enum SocketStatus   { Linkable, Occupied, Connected, Locked, Disabled }

        [System.Serializable]
        public struct SocketInfo
        {
            [SerializeField, HideInInspector] private int index;
            [SerializeField, HideInInspector] private SocketLocation location;
            [SerializeField, HideInInspector] private Edge edge;
            [SerializeField, HideInInspector] private int edgeMark; // 0..Len (0 & Len are corners)

            [SerializeField] private SocketStatus status;   // editable for designer (Locked/Disabled)
            [SerializeField, HideInInspector] private Vector3 localPos;
            [SerializeField, HideInInspector] private Quaternion localRot;

            public int Index => index;
            public SocketLocation Location => location;
            public Edge EdgeLocal => edge;
            public int EdgeMark => edgeMark;
            public SocketStatus Status => status;
            public bool IsLinkable => status == SocketStatus.Linkable;
            public Vector3 LocalPos => localPos;
            public Quaternion LocalRot => localRot;

            public void SetStatus(SocketStatus s) => status = s;

            internal void Initialize(
                int idx, SocketLocation loc, Edge e, int mark,
                Vector3 lp, Quaternion lr, SocketStatus defaultStatus)
            {
                index = idx; location = loc; edge = e; edgeMark = mark;
                localPos = lp; localRot = lr; status = defaultStatus;
            }
        }

        [Header("Sockets (NE = #0, clockwise)")]
        [SerializeField] private List<SocketInfo> sockets = new List<SocketInfo>();
        private readonly Dictionary<(Edge edge, int mark), int> _edgeMarkToIndex = new();
        private bool _socketsBuilt;

        // Sockets currently part of an active connection (to restore after Refresh)
        private readonly HashSet<int> _connectedSockets = new();

        public IReadOnlyList<SocketInfo> Sockets { get { if (!_socketsBuilt) BuildSockets(); return sockets; } }
        public int SocketCount { get { if (!_socketsBuilt) BuildSockets(); return sockets.Count; } }

        public void BuildSockets()
        {
            // Preserve statuses keyed by (edge,mark)
            var prev = new Dictionary<(Edge e, int mark), SocketStatus>(64);
            for (int i = 0; i < sockets.Count; i++)
            {
                var s = sockets[i];
                prev[(s.EdgeLocal, s.EdgeMark)] = s.Status;
            }

            sockets.Clear();
            _edgeMarkToIndex.Clear();
            _socketsBuilt = false;

            int w = Mathf.Max(1, footprint.x); // meters along X (North/South edges)
            int l = Mathf.Max(1, footprint.y); // meters along Z (East/West  edges)
            float hx = w * 0.5f;
            float hz = l * 0.5f;

            Quaternion Rot(Vector3 outward) => Quaternion.LookRotation(outward, Vector3.up);

            int Add(SocketLocation loc, Edge e, int mark, Vector3 lp, Quaternion lr)
            {
                int idx = sockets.Count;
                var si = new SocketInfo();
                var initial = prev.TryGetValue((e, mark), out var saved) ? saved : SocketStatus.Linkable;
                si.Initialize(idx, loc, e, mark, lp, lr, initial);
                sockets.Add(si);
                _edgeMarkToIndex[(e, mark)] = idx;
                return idx;
            }

            // Outward vectors
            Vector3 outN = Vector3.forward;
            Vector3 outE = Vector3.right;
            Vector3 outS = Vector3.back;
            Vector3 outW = Vector3.left;

            // Local corner positions
            Vector3 NE = new Vector3(+hx, 0f, +hz);
            Vector3 SE = new Vector3(+hx, 0f, -hz);
            Vector3 SW = new Vector3(-hx, 0f, -hz);
            Vector3 NW = new Vector3(-hx, 0f, +hz);

            // Helper: add corner with alias for the adjacent edge
            void AddCorner(Vector3 lp, Vector3 outwardAvg, Edge primaryEdge, int primaryMark, (Edge e, int mark) alias)
            {
                int idx = Add(SocketLocation.Corner, primaryEdge, primaryMark, lp, Rot(outwardAvg.normalized));
                _edgeMarkToIndex[alias] = idx; // corner also addressable from the other edge
            }

            // ===== CLOCKWISE PERIMETER (NE → E → SE → S → SW → W → NW → N) =====

            // NE corner (primary: North,0) alias (East,l)
            AddCorner(NE, outN + outE, Edge.North, 0, (Edge.East, l));

            // East interior 1..l-1
            for (int k = 1; k <= l - 1; k++)
            {
                float z = +hz - k;
                Vector3 lp = new Vector3(+hx, 0f, z);
                Add(SocketLocation.Edge, Edge.East, k, lp, Rot(outE));
            }

            // SE corner (primary: South,0) alias (East,0)
            AddCorner(SE, outS + outE, Edge.South, 0, (Edge.East, 0));

            // South interior 1..w-1
            for (int k = 1; k <= w - 1; k++)
            {
                float x = +hx - k;
                Vector3 lp = new Vector3(x, 0f, -hz);
                Add(SocketLocation.Edge, Edge.South, k, lp, Rot(outS));
            }

            // SW corner (primary: South,w) alias (West,l)
            AddCorner(SW, outS + outW, Edge.South, w, (Edge.West, l));

            // West interior 1..l-1
            for (int k = 1; k <= l - 1; k++)
            {
                float z = -hz + k;
                Vector3 lp = new Vector3(-hx, 0f, z);
                Add(SocketLocation.Edge, Edge.West, k, lp, Rot(outW));
            }

            // NW corner (primary: North,w) alias (West,0)
            AddCorner(NW, outN + outW, Edge.North, w, (Edge.West, 0));

            // North interior 1..w-1
            for (int k = 1; k <= w - 1; k++)
            {
                float x = -hx + k;
                Vector3 lp = new Vector3(x, 0f, +hz);
                Add(SocketLocation.Edge, Edge.North, k, lp, Rot(outN));
            }

            _socketsBuilt = true;
        }

        public SocketInfo GetSocket(int index) { if (!_socketsBuilt) BuildSockets(); return sockets[index]; }
        public Vector3 GetSocketWorldPosition(int index) => transform.TransformPoint(GetSocket(index).LocalPos);
        public Quaternion GetSocketWorldRotation(int index) => transform.rotation * GetSocket(index).LocalRot;

        public bool TryGetSocketIndexByEdgeMark(Edge edge, int mark, out int index)
        {
            if (!_socketsBuilt || sockets == null || sockets.Count == 0)
                BuildSockets();

            int len = EdgeLengthMeters(edge);
            int clamped = Mathf.Clamp(mark, 0, len);

            if (_edgeMarkToIndex.TryGetValue((edge, clamped), out index))
                return true;

            BuildSockets();
            if (_edgeMarkToIndex.TryGetValue((edge, clamped), out index))
                return true;

            if (len == 1)
            {
                if (_edgeMarkToIndex.TryGetValue((edge, 0), out index)) return true;
                if (_edgeMarkToIndex.TryGetValue((edge, len), out index)) return true;
            }

            index = -1;
            return false;
        }

        public int GetSocketIndexByEdgeMark(Edge edge, int mark)
        {
            if (TryGetSocketIndexByEdgeMark(edge, mark, out var idx))
                return idx;

#if UNITY_EDITOR
            var keys = new System.Text.StringBuilder();
            keys.Append('[');
            bool first = true;
            foreach (var kv in _edgeMarkToIndex)
            {
                if (kv.Key.edge != edge) continue;
                if (!first) keys.Append(", ");
                first = false;
                keys.Append(kv.Key.mark);
            }
            keys.Append(']');
            Debug.LogError($"[GamePlatform] Socket lookup failed for ({edge}, {mark}). Available marks for {edge}: {keys}.", this);
#endif
            throw new KeyNotFoundException($"Socket ({edge},{mark}) not found.");
        }

        public void SetSocketStatus(int index, SocketStatus status)
        {
            if (!_socketsBuilt) BuildSockets();
            var s = sockets[index]; s.SetStatus(status); sockets[index] = s;
        }

        // ---------- Module registry ----------
        [System.Serializable]
        public struct ModuleReg
        {
            public GameObject go;
            public int[] socketIndices;
            public bool blocksLink;
        }

        private readonly Dictionary<GameObject, ModuleReg> _moduleRegs = new();
        private readonly Dictionary<int, List<GameObject>> _socketToModules = new();

        public void RegisterModuleOnSockets(GameObject moduleGo, bool occupiesSockets, IEnumerable<int> socketIndices)
        {
            if (!moduleGo) return;

            var list = new List<int>(socketIndices);
            var pm   = moduleGo.GetComponent<PlatformModule>();
            bool blocks = pm ? pm.blocksLink : false;

            var reg = new ModuleReg { go = moduleGo, socketIndices = list.ToArray(), blocksLink = blocks };
            _moduleRegs[moduleGo] = reg;

            foreach (var sIdx in list)
            {
                if (!_socketToModules.TryGetValue(sIdx, out var l)) { l = new List<GameObject>(); _socketToModules[sIdx] = l; }
                if (!l.Contains(moduleGo)) l.Add(moduleGo);
            }
        }

        public void UnregisterModule(GameObject moduleGo)
        {
            if (!moduleGo) return;
            if (!_moduleRegs.TryGetValue(moduleGo, out var reg)) return;

            if (reg.socketIndices != null)
            {
                foreach (var sIdx in reg.socketIndices)
                {
                    if (_socketToModules.TryGetValue(sIdx, out var list))
                    {
                        list.Remove(moduleGo);
                        if (list.Count == 0) _socketToModules.Remove(sIdx);
                    }
                }
            }
            _moduleRegs.Remove(moduleGo);
        }

        /// <summary>Recompute every socket’s status from current modules + connection state.</summary>
        public void RefreshSocketStatuses()
        {
            if (!_socketsBuilt) BuildSockets();

            for (int i = 0; i < sockets.Count; i++)
            {
                var s = sockets[i];

                // Designer overrides remain authoritative:
                if (s.Status == SocketStatus.Locked || s.Status == SocketStatus.Disabled)
                {
                    sockets[i] = s;
                    continue;
                }

                // If this socket is part of an active connection:
                if (_connectedSockets.Contains(i))
                {
                    s.SetStatus(SocketStatus.Connected);
                    sockets[i] = s;
                    continue;
                }

                // Else, derive from modules:
                bool blocked = false;
                if (_socketToModules.TryGetValue(i, out var mods))
                {
                    foreach (var go in mods)
                    {
                        if (!go) continue;
                        if (!go.activeInHierarchy) continue;
                        if (!_moduleRegs.TryGetValue(go, out var reg)) continue;
                        if (reg.blocksLink) { blocked = true; break; }
                    }
                }

                s.SetStatus(blocked ? SocketStatus.Occupied : SocketStatus.Linkable);
                sockets[i] = s;
            }
        }

        /// <summary>Hide/Show a module, then refresh statuses and navmesh.</summary>
        public void SetModuleHidden(GameObject moduleGo, bool hidden)
        {
            if (!moduleGo) return;

            var pm = moduleGo.GetComponent<PlatformModule>();
            if (pm != null) pm.SetHidden(hidden);
            else moduleGo.SetActive(!hidden);

            RefreshSocketStatuses();
            QueueRebuild();
        }

        /// <summary>Toggle modules on these sockets and flip sockets to Connected/Linkable.</summary>
        public void ApplyConnectionVisuals(IEnumerable<int> socketIndices, bool connected)
        {
            var set = new HashSet<int>(socketIndices);
            var toToggle = new HashSet<GameObject>();

            foreach (var sIdx in set)
            {
                if (connected) _connectedSockets.Add(sIdx);
                else _connectedSockets.Remove(sIdx);

                if (_socketToModules.TryGetValue(sIdx, out var mods))
                    foreach (var go in mods) toToggle.Add(go);
            }

            foreach (var go in toToggle)
                SetModuleHidden(go, connected);

            RefreshSocketStatuses();
        }

        /// <summary>Editor-only convenience to clear links and show all modules.</summary>
        public void EditorResetAllConnections()
        {
            var toShow = new HashSet<GameObject>();
            foreach (var kv in _socketToModules)
                foreach (var go in kv.Value)
                    if (go) toShow.Add(go);

            foreach (var go in toShow)
                SetModuleHidden(go, false);

            _connectedSockets.Clear();

#if UNITY_EDITOR
            var linksParent = transform.Find("Links");
            if (linksParent)
            {
                for (int i = linksParent.childCount - 1; i >= 0; i--)
                    UnityEditor.Undo.DestroyObjectImmediate(linksParent.GetChild(i).gameObject);
            }
#else
            var linksParent = transform.Find("Links");
            if (linksParent)
            {
                for (int i = linksParent.childCount - 1; i >= 0; i--)
                    Destroy(linksParent.GetChild(i).gameObject);
            }
#endif
            RefreshSocketStatuses();
            QueueRebuild();
        }

        // ---------- Gizmos ----------
        public static class GizmoSettings
        {
            public static bool ShowGizmos = true;
            public static bool ShowIndices = true;
            public static float SocketSphereRadius = 0.06f;

            public static Color ColorFree     = new Color(0.20f, 1.00f, 0.20f, 0.90f); // Linkable
            public static Color ColorOccupied = new Color(1.00f, 0.60f, 0.20f, 0.90f);
            public static Color ColorLocked   = new Color(0.95f, 0.25f, 0.25f, 0.90f);
            public static Color ColorDisabled = new Color(0.60f, 0.60f, 0.60f, 0.90f);
            public static Color ColorConnected = new Color(0.20f, 0.65f, 1.00f, 0.90f);

            public static bool  ShowDirections  = true;
            public static float DirectionLength = 0.28f;

            public static void SetVisibility(bool visible) => ShowGizmos = visible;
            public static void SetShowIndices(bool show) => ShowIndices = show;
            public static void SetColors(Color free, Color occupied, Color locked, Color disabled)
            { ColorFree = free; ColorOccupied = occupied; ColorLocked = locked; ColorDisabled = disabled; }
            public static void SetColorsAll(Color free, Color occupied, Color locked, Color disabled, Color connected)
            { SetColors(free, occupied, locked, disabled); ColorConnected = connected; }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!GizmoSettings.ShowGizmos) return;

            // Footprint
            Gizmos.color = new Color(0f, 0.6f, 1f, 0.25f);
            float hx = footprint.x * 0.5f;
            float hz = footprint.y * 0.5f;
            var p = transform.position; var r = transform.right; var f = transform.forward;
            Vector3 a = p + (-r * hx) + (-f * hz);
            Vector3 b = p + ( r * hx) + (-f * hz);
            Vector3 c = p + ( r * hx) + ( f * hz);
            Vector3 d = p + (-r * hx) + ( f * hz);
            Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c); Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);

            if (!_socketsBuilt) BuildSockets();

            for (int i = 0; i < sockets.Count; i++)
            {
                var s = sockets[i];
                Color col = s.Status switch
                {
                    SocketStatus.Linkable  => GizmoSettings.ColorFree,
                    SocketStatus.Occupied  => GizmoSettings.ColorOccupied,
                    SocketStatus.Connected => GizmoSettings.ColorConnected,
                    SocketStatus.Locked    => GizmoSettings.ColorLocked,
                    SocketStatus.Disabled  => GizmoSettings.ColorDisabled,
                    _ => Color.white
                };
                Gizmos.color = col;

                Vector3 wp = transform.TransformPoint(s.LocalPos);
                Gizmos.DrawSphere(wp, GizmoSettings.SocketSphereRadius);

                if (GizmoSettings.ShowDirections)
                {
                    Vector3 dir = (transform.rotation * s.LocalRot) * Vector3.forward;
                    Gizmos.DrawLine(wp, wp + dir.normalized * GizmoSettings.DirectionLength);
                }

#if UNITY_EDITOR
                if (GizmoSettings.ShowIndices)
                {
                    string label = $"#{i} [{s.EdgeLocal}:{s.EdgeMark}] {s.Status}";
                    UnityEditor.Handles.Label(wp + Vector3.up * 0.05f, label);
                }
#endif
            }
        }
#endif

        // ---------- Link creation & adjacency ----------
        public static bool ConnectIfAdjacent(GamePlatform a, GamePlatform b)
        {
            if (!a || !b || a == b) return false;

            if (!TouchingAlongOneEdge(a, b, out Edge aEdge, out Edge bEdge,
                                      out int aStart, out int aEnd,
                                      out int bStart, out int bEnd))
                return false;

            var aMarks = new List<int>();
            var bMarks = new List<int>();

            int lenA = a.EdgeLengthMeters(aEdge);
            int lenB = b.EdgeLengthMeters(bEdge);
            int span = Mathf.Min(aEnd - aStart + 1, bEnd - bStart + 1);

            for (int i = 0; i < span; i++)
            {
                int am = aStart + i;
                int bm = bStart + i;

                // skip corners
                if (am <= 0 || am >= lenA) continue;
                if (bm <= 0 || bm >= lenB) continue;

                int aIdx = a.GetSocketIndexByEdgeMark(aEdge, am);
                int bIdx = b.GetSocketIndexByEdgeMark(bEdge, bm);

                if (a.GetSocket(aIdx).IsLinkable && b.GetSocket(bIdx).IsLinkable)
                {
                    aMarks.Add(am);
                    bMarks.Add(bm);
                }
            }

            if (aMarks.Count == 0) return false;

            (int rs, int re) = LongestContiguousRun(aMarks);

            var aSock = new List<int>();
            var bSock = new List<int>();
            for (int m = aMarks[rs]; m <= aMarks[re]; m++)
            {
                aSock.Add(a.GetSocketIndexByEdgeMark(aEdge, m));
                bSock.Add(b.GetSocketIndexByEdgeMark(bEdge, m));
            }

            a.ApplyConnectionVisuals(aSock, true);
            b.ApplyConnectionVisuals(bSock, true);

            a.QueueRebuild();
            b.QueueRebuild();

            CreateLinkAcrossRun(a, aEdge, aMarks[rs], aMarks[re], b, bEdge);
            return true;
        }

        private static (int startIdx, int endIdx) LongestContiguousRun(List<int> sortedMarks)
        {
            if (sortedMarks.Count == 0) return (0, -1);
            int bestS = 0, bestE = 0;
            int curS = 0, curE = 0;
            for (int i = 1; i < sortedMarks.Count; i++)
            {
                if (sortedMarks[i] == sortedMarks[i - 1] + 1)
                {
                    curE = i;
                }
                else
                {
                    if (curE - curS > bestE - bestS) { bestS = curS; bestE = curE; }
                    curS = curE = i;
                }
            }
            if (curE - curS > bestE - bestS) { bestS = curS; bestE = curE; }
            return (bestS, bestE);
        }

        private static bool TouchingAlongOneEdge(
            GamePlatform a, GamePlatform b,
            out Edge aEdge, out Edge bEdge,
            out int aStartMark, out int aEndMark,
            out int bStartMark, out int bEndMark)
        {
            const float EPS = 0.02f;

            aEdge = bEdge = Edge.North;
            aStartMark = aEndMark = bStartMark = bEndMark = 0;

            Rect aRect = WorldRect(a);
            Rect bRect = WorldRect(b);

            // A north edge to B south edge
            if (Mathf.Abs(aRect.yMax - bRect.yMin) <= EPS &&
                Overlap1D(aRect.xMin, aRect.xMax, bRect.xMin, bRect.xMax, out float x0, out float x1))
            {
                aEdge = Edge.North; bEdge = Edge.South;
                ToMarksMapped(a, aEdge, x0, x1, out aStartMark, out aEndMark);
                ToMarksMapped(b, bEdge, x0, x1, out bStartMark, out bEndMark);
                return true;
            }

            // A south edge to B north edge
            if (Mathf.Abs(aRect.yMin - bRect.yMax) <= EPS &&
                Overlap1D(aRect.xMin, aRect.xMax, bRect.xMin, bRect.xMax, out x0, out x1))
            {
                aEdge = Edge.South; bEdge = Edge.North;
                ToMarksMapped(a, aEdge, x0, x1, out aStartMark, out aEndMark);
                ToMarksMapped(b, bEdge, x0, x1, out bStartMark, out bEndMark);
                return true;
            }

            // A east edge to B west edge
            if (Mathf.Abs(aRect.xMax - bRect.xMin) <= EPS &&
                Overlap1D(aRect.yMin, aRect.yMax, bRect.yMin, bRect.yMax, out float z0, out float z1))
            {
                aEdge = Edge.East; bEdge = Edge.West;
                ToMarksMapped(a, aEdge, z0, z1, out aStartMark, out aEndMark);
                ToMarksMapped(b, bEdge, z0, z1, out bStartMark, out bEndMark);
                return true;
            }

            // A west edge to B east edge
            if (Mathf.Abs(aRect.xMin - bRect.xMax) <= EPS &&
                Overlap1D(aRect.yMin, aRect.yMax, bRect.yMin, bRect.yMax, out z0, out z1))
            {
                aEdge = Edge.West; bEdge = Edge.East;
                ToMarksMapped(a, aEdge, z0, z1, out aStartMark, out aEndMark);
                ToMarksMapped(b, bEdge, z0, z1, out bStartMark, out bEndMark);
                return true;
            }

            return false;

            // --- helpers ---
            static Rect WorldRect(GamePlatform gp)
            {
                var pos = gp.transform.position;
                int w = gp.footprint.x, l = gp.footprint.y;

                int rotSteps = Mathf.RoundToInt((gp.transform.eulerAngles.y % 360f) / 90f) & 3;
                bool swapped = (rotSteps % 2) == 1;
                float hx = (swapped ? l : w) * 0.5f;
                float hz = (swapped ? w : l) * 0.5f;

                return new Rect(pos.x - hx, pos.z - hz, hx * 2f, hz * 2f);
            }

            static bool Overlap1D(float a0, float a1, float b0, float b1, out float o0, out float o1)
            {
                o0 = Mathf.Max(a0, b0);
                o1 = Mathf.Min(a1, b1);
                return o1 - o0 > 0.0001f;
            }

            static void ToMarksMapped(GamePlatform gp, Edge e, float w0, float w1, out int m0, out int m1)
            {
                int len = gp.EdgeLengthMeters(e);
                float c = (e == Edge.North || e == Edge.South) ? gp.transform.position.x : gp.transform.position.z;
                float half = len * 0.5f;

                int Map(float worldCoord)
                {
                    float t = worldCoord - (c - half); // 0..len
                    return Mathf.Clamp(Mathf.RoundToInt(t), 0, len);
                }

                m0 = Map(w0);
                m1 = Map(w1);
                if (m1 < m0) { int tmp = m0; m0 = m1; m1 = tmp; }
            }
        }

        private static void CreateLinkAcrossRun(GamePlatform a, Edge aEdge, int aMarkStart, int aMarkEnd, GamePlatform b, Edge bEdge)
        {
            Vector3 A0 = a.WorldPointOnEdgeMarkCenter(aEdge, aMarkStart);
            Vector3 A1 = a.WorldPointOnEdgeMarkCenter(aEdge, aMarkEnd);
            Vector3 B0 = b.WorldPointOnEdgeMarkCenter(bEdge, aMarkStart);
            Vector3 B1 = b.WorldPointOnEdgeMarkCenter(bEdge, aMarkEnd);

            var (_, aOut) = a.GetEdgeInfo(aEdge);
            var (_, bOut) = b.GetEdgeInfo(bEdge);
            const float INSET = 0.02f;
            A0 -= aOut.normalized * INSET; A1 -= aOut.normalized * INSET;
            B0 -= bOut.normalized * INSET; B1 -= bOut.normalized * INSET;

            var owner = GetOrCreate(a.transform, "Links");
            var go = new GameObject($"Link_{a.name}_{aEdge}_{aMarkStart}-{aMarkEnd}_to_{b.name}");
            go.transform.SetParent(owner, false);

            Vector3 startWorld = 0.5f * (A0 + B0);
            Vector3 endWorld   = 0.5f * (A1 + B1);
            Vector3 center = 0.5f * (startWorld + endWorld);
            go.transform.position = center;

            var link = go.AddComponent<NavMeshLink>();
            link.startPoint = go.transform.InverseTransformPoint(startWorld);
            link.endPoint   = go.transform.InverseTransformPoint(endWorld);
            link.bidirectional = true;
            link.width = 0.6f;
            link.area = 0;
            link.agentTypeID = a.navSurface ? a.navSurface.agentTypeID : 0;
        }

        private Vector3 WorldPointOnEdgeMarkCenter(Edge e, int mark)
        {
            float hx = footprint.x * 0.5f;
            float hz = footprint.y * 0.5f;

            Vector3 lp;
            if (e == Edge.North)      lp = new Vector3(-hx + mark, 0f, +hz);
            else if (e == Edge.South) lp = new Vector3(+hx - mark, 0f, -hz);
            else if (e == Edge.East)  lp = new Vector3(+hx, 0f, +hz - mark);
            else                      lp = new Vector3(-hx, 0f, -hz + mark);

            return transform.TransformPoint(lp);
        }

        private static Transform GetOrCreate(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (!t)
            {
                var go = new GameObject(name);
                t = go.transform;
                t.SetParent(parent, false);
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }
            return t;
        }

        public void EnsureChildrenModulesRegistered()
        {
            var modules = GetComponentsInChildren<PlatformModule>(true);
            foreach (var m in modules) m.EnsureRegistered();
        }

        // ---------- COMPAT HELPERS (for your existing calls) ----------

        /// <summary>
        /// Find nearest socket to a LOCAL position (platform space).
        /// </summary>
        public int FindNearestSocketIndexLocal(Vector3 localPos)
        {
            if (!_socketsBuilt) BuildSockets();
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < sockets.Count; i++)
            {
                float d = Vector3.SqrMagnitude(localPos - sockets[i].LocalPos);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// Register a module by a set of LOCAL positions. Each position is snapped to the nearest edge mark.
        /// Useful for old code that passed meter-mark centers as local positions.
        /// </summary>
        public void RegisterModuleByLocalPositions(GameObject moduleGo, bool occupiesSockets, IEnumerable<Vector3> localPositions)
        {
            if (!_socketsBuilt) BuildSockets();
            if (moduleGo == null || localPositions == null) return;

            var idxSet = new HashSet<int>();
            float hx = footprint.x * 0.5f;
            float hz = footprint.y * 0.5f;

            foreach (var lp in localPositions)
            {
                // Decide nearest edge by distance to each side line
                float dN = Mathf.Abs(lp.z - (+hz));
                float dS = Mathf.Abs(lp.z - (-hz));
                float dE = Mathf.Abs(lp.x - (+hx));
                float dW = Mathf.Abs(lp.x - (-hx));

                Edge edge;
                bool edgeIsX;
                float baseCoord;
                int len;

                if      (dN <= dS && dN <= dE && dN <= dW) { edge = Edge.North; edgeIsX = true;  baseCoord = -hx; len = footprint.x; }
                else if (dS <= dN && dS <= dE && dS <= dW) { edge = Edge.South; edgeIsX = true;  baseCoord = -hx; len = footprint.x; }
                else if (dE <= dW)                         { edge = Edge.East;  edgeIsX = false; baseCoord = -hz; len = footprint.y; }
                else                                       { edge = Edge.West;  edgeIsX = false; baseCoord = -hz; len = footprint.y; }

                float coord = edgeIsX ? lp.x : lp.z;
                int mark = Mathf.Clamp(Mathf.RoundToInt(coord - baseCoord), 0, len); // include corners

                if (TryGetSocketIndexByEdgeMark(edge, mark, out int sIdx))
                    idxSet.Add(sIdx);
            }

            if (idxSet.Count > 0)
                RegisterModuleOnSockets(moduleGo, occupiesSockets, idxSet);
        }
    }
}
