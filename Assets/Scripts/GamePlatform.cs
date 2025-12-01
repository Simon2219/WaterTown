using System.Collections;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace WaterTown.Platforms
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavMeshSurface))]
    public class GamePlatform : MonoBehaviour
    {
        // ---------- Global registry & events ----------
        public static event System.Action<GamePlatform> PlatformRegistered;
        public static event System.Action<GamePlatform> PlatformUnregistered;

        private static readonly HashSet<GamePlatform> _allPlatforms = new();
        public static IReadOnlyCollection<GamePlatform> AllPlatforms => _allPlatforms;

        /// <summary>Fired whenever this platform’s connection/railing state changes.</summary>
        public event System.Action<GamePlatform> ConnectionsChanged;

        /// <summary>Fired whenever this platform’s pose changes (position/rotation/scale).</summary>
        public event System.Action<GamePlatform> PoseChanged;

        // ---------- Footprint & NavMesh ----------
        [Header("Footprint (cells @ 1m)")]
        [SerializeField] private Vector2Int footprint = new Vector2Int(4, 4);
        public Vector2Int Footprint => footprint;

        private NavMeshSurface _navSurface;

        [Header("NavMesh Rebuild")]
        [SerializeField]
        [Tooltip("Delay before rebuilding this platform's NavMesh after changes.\n" +
                 "Lower = more responsive; higher = fewer rebuilds while moving/editing.")]
        private float rebuildDebounceSeconds = 0.1f;

        private Coroutine _pendingRebuild;

        internal NavMeshSurface NavSurface
        {
            get
            {
                if (!_navSurface)
                    _navSurface = GetComponent<NavMeshSurface>();
                return _navSurface;
            }
        }

        private Vector3 _lastPos;
        private Quaternion _lastRot;
        private Vector3 _lastScale;

        // ---------- Edge enum (for compatibility with PlatformModule) ----------

        public enum Edge { North, East, South, West }

        /// <summary>Length in whole meters along the given edge (number of segments).</summary>
        public int EdgeLengthMeters(Edge edge)
        {
            // X = width (north/south edges); Y = length (east/west edges)
            return (edge == Edge.North || edge == Edge.South) ? footprint.x : footprint.y;
        }

        // ---------- Sockets (grid-based, direction-agnostic) ----------

        public enum SocketStatus { Linkable, Occupied, Connected, Locked, Disabled }

        public enum SocketLocation { Edge, Corner }  // Corner kept for backward compat (we only create Edge sockets)

        [System.Serializable]
        public struct SocketInfo
        {
            [SerializeField, HideInInspector] private int index;
            [SerializeField, HideInInspector] private Vector3 localPos;
            [SerializeField, HideInInspector] private SocketLocation location;
            [SerializeField] private SocketStatus status;

            public int Index => index;
            public Vector3 LocalPos => localPos;
            public SocketStatus Status => status;
            public bool IsLinkable => status == SocketStatus.Linkable;
            public SocketLocation Location => location;

            internal void Initialize(int idx, Vector3 lp, SocketStatus defaultStatus)
            {
                index = idx;
                localPos = lp;
                location = SocketLocation.Edge; // we only create edge sockets in this setup
                status = defaultStatus;
            }

            public void SetStatus(SocketStatus s) => status = s;
        }

        [Header("Sockets (perimeter, 1m spacing)")]
        [SerializeField] private List<SocketInfo> sockets = new();
        private bool _socketsBuilt;

        /// <summary>Set of socket indices that are currently part of a connection.</summary>
        private readonly HashSet<int> _connectedSockets = new();

        public IReadOnlyList<SocketInfo> Sockets
        {
            get { if (!_socketsBuilt) BuildSockets(); return sockets; }
        }

        public int SocketCount
        {
            get { if (!_socketsBuilt) BuildSockets(); return sockets.Count; }
        }

        /// <summary>
        /// Build sockets along the perimeter of the footprint, in local space,
        /// one socket per 1m edge segment.
        /// Order (for compat with Edge/mark API):
        ///   0..(w-1)            : North edge
        ///   w..(2w-1)           : South edge
        ///   2w..(2w+l-1)        : East edge
        ///   2w+l..(2w+2l-1)     : West edge
        /// </summary>
        public void BuildSockets()
        {
            var prev = new Dictionary<Vector3, SocketStatus>();
            foreach (var s in sockets)
                prev[s.LocalPos] = s.Status;

            sockets.Clear();
            _socketsBuilt = false;

            int w = Mathf.Max(1, footprint.x);
            int l = Mathf.Max(1, footprint.y);
            float hx = w * 0.5f;
            float hz = l * 0.5f;

            int idx = 0;

            // North edge (local z ≈ +hz), segments along x
            for (int m = 0; m < w; m++)
            {
                float x = -hx + 0.5f + m;
                Vector3 lp = new Vector3(x, 0f, +hz);
                var si = new SocketInfo();
                si.Initialize(idx, lp, prev.TryGetValue(lp, out var old) ? old : SocketStatus.Linkable);
                sockets.Add(si);
                idx++;
            }

            // South edge (local z ≈ -hz)
            for (int m = 0; m < w; m++)
            {
                float x = -hx + 0.5f + m;
                Vector3 lp = new Vector3(x, 0f, -hz);
                var si = new SocketInfo();
                si.Initialize(idx, lp, prev.TryGetValue(lp, out var old) ? old : SocketStatus.Linkable);
                sockets.Add(si);
                idx++;
            }

            // East edge (local x ≈ +hx), along z
            for (int m = 0; m < l; m++)
            {
                float z = +hz - 0.5f - m;
                Vector3 lp = new Vector3(+hx, 0f, z);
                var si = new SocketInfo();
                si.Initialize(idx, lp, prev.TryGetValue(lp, out var old) ? old : SocketStatus.Linkable);
                sockets.Add(si);
                idx++;
            }

            // West edge (local x ≈ -hx)
            for (int m = 0; m < l; m++)
            {
                float z = +hz - 0.5f - m;
                Vector3 lp = new Vector3(-hx, 0f, z);
                var si = new SocketInfo();
                si.Initialize(idx, lp, prev.TryGetValue(lp, out var old) ? old : SocketStatus.Linkable);
                sockets.Add(si);
                idx++;
            }

            _socketsBuilt = true;
        }

        public SocketInfo GetSocket(int index)
        {
            if (!_socketsBuilt) BuildSockets();
            return sockets[index];
        }

        public Vector3 GetSocketWorldPosition(int index)
        {
            if (!_socketsBuilt) BuildSockets();
            return transform.TransformPoint(sockets[index].LocalPos);
        }

        public void SetSocketStatus(int index, SocketStatus status)
        {
            if (!_socketsBuilt) BuildSockets();
            var s = sockets[index];
            s.SetStatus(status);
            sockets[index] = s;
        }

        /// <summary>
        /// Compatibility helper for code that thinks in Edge+mark (PlatformModule, old tools).
        /// Uses the socket ordering defined in BuildSockets().
        /// </summary>
        public int GetSocketIndexByEdgeMark(Edge edge, int mark)
        {
            if (!_socketsBuilt) BuildSockets();

            int w = Mathf.Max(1, footprint.x);
            int l = Mathf.Max(1, footprint.y);

            switch (edge)
            {
                case Edge.North:
                    mark = Mathf.Clamp(mark, 0, w - 1);
                    return mark;

                case Edge.South:
                    mark = Mathf.Clamp(mark, 0, w - 1);
                    return w + mark;

                case Edge.East:
                    mark = Mathf.Clamp(mark, 0, l - 1);
                    return 2 * w + mark;

                case Edge.West:
                default:
                    mark = Mathf.Clamp(mark, 0, l - 1);
                    return 2 * w + l + mark;
            }
        }

        /// <summary>Return the single nearest socket index to a local position.</summary>
        public int FindNearestSocketIndexLocal(Vector3 localPos)
        {
            if (!_socketsBuilt) BuildSockets();
            int best = -1;
            float bestD = float.MaxValue;

            for (int i = 0; i < sockets.Count; i++)
            {
                float d = Vector3.SqrMagnitude(localPos - sockets[i].LocalPos);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// Finds up to maxCount nearest socket indices to localPos within maxDistance.
        /// </summary>
        public void FindNearestSocketIndicesLocal(
            Vector3 localPos,
            int maxCount,
            float maxDistance,
            List<int> result)
        {
            result.Clear();
            if (!_socketsBuilt) BuildSockets();
            if (maxCount <= 0 || sockets.Count == 0) return;

            float maxSqr = maxDistance * maxDistance;

            List<(int idx, float d)> tmp = new List<(int, float)>(sockets.Count);
            for (int i = 0; i < sockets.Count; i++)
            {
                float d = Vector3.SqrMagnitude(localPos - sockets[i].LocalPos);
                if (d <= maxSqr)
                    tmp.Add((i, d));
            }

            tmp.Sort((a, b) => a.d.CompareTo(b.d));
            for (int i = 0; i < tmp.Count && i < maxCount; i++)
                result.Add(tmp[i].idx);
        }

        /// <summary>
        /// Convenience: find nearest socket to a WORLD position.
        /// Just converts to local space and reuses FindNearestSocketIndexLocal.
        /// </summary>
        public int FindNearestSocketIndexWorld(Vector3 worldPos)
        {
            Vector3 local = transform.InverseTransformPoint(worldPos);
            return FindNearestSocketIndexLocal(local);
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
            if (!_socketsBuilt) BuildSockets();

            var list = new List<int>(socketIndices);
            var pm = moduleGo.GetComponent<PlatformModule>();
            bool blocks = pm ? pm.blocksLink : false;

            var reg = new ModuleReg { go = moduleGo, socketIndices = list.ToArray(), blocksLink = blocks };
            _moduleRegs[moduleGo] = reg;

            foreach (var sIdx in list)
            {
                if (!_socketToModules.TryGetValue(sIdx, out var l))
                {
                    l = new List<GameObject>();
                    _socketToModules[sIdx] = l;
                }
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

        // ---------- Railing registry ----------

        private readonly Dictionary<int, List<PlatformRailing>> _socketToRailings = new();

        /// <summary>Called by PlatformRailing to bind itself to given socket indices.</summary>
        public void RegisterRailing(PlatformRailing railing)
        {
            if (!railing) return;
            if (!_socketsBuilt) BuildSockets();

            var indices = railing.SocketIndices;
            if (indices == null || indices.Length == 0)
            {
                // fallback: bind to nearest socket
                int nearest = FindNearestSocketIndexLocal(transform.InverseTransformPoint(railing.transform.position));
                if (nearest >= 0)
                {
                    indices = new[] { nearest };
                    railing.SetSocketIndices(indices);
                }
                else
                    return;
            }

            foreach (int sIdx in indices)
            {
                if (!_socketToRailings.TryGetValue(sIdx, out var list))
                {
                    list = new List<PlatformRailing>();
                    _socketToRailings[sIdx] = list;
                }
                if (!list.Contains(railing)) list.Add(railing);
            }
        }

        /// <summary>Called by PlatformRailing when disabled/destroyed.</summary>
        public void UnregisterRailing(PlatformRailing railing)
        {
            if (!railing) return;

            foreach (var kv in _socketToRailings)
            {
                kv.Value.Remove(railing);
            }
        }

        /// <summary>True if the given socket index is currently part of a connection.</summary>
        internal bool IsSocketConnected(int socketIndex) => _connectedSockets.Contains(socketIndex);

        /// <summary>
        /// Compute visibility for a single PlatformRailing (rail or post)
        /// based purely on its socket indices and _connectedSockets.
        /// </summary>
        internal void UpdateRailingVisibility(PlatformRailing railing)
        {
            if (!railing) return;
            var indices = railing.SocketIndices ?? System.Array.Empty<int>();
            if (indices.Length == 0)
            {
                railing.SetHidden(false);
                return;
            }

            bool any = false;
            bool allConnected = true;
            foreach (int idx in indices)
            {
                any = true;
                if (!_connectedSockets.Contains(idx))
                {
                    allConnected = false;
                    break;
                }
            }

            bool hide = any && allConnected;
            railing.SetHidden(hide);
        }

        internal void RefreshAllRailingsVisibility()
        {
            var railings = GetComponentsInChildren<PlatformRailing>(true);
            foreach (var r in railings)
                UpdateRailingVisibility(r);
        }

        /// <summary>Recompute every socket’s status from current modules + connection state.</summary>
        public void RefreshSocketStatuses()
        {
            if (!_socketsBuilt) BuildSockets();

            for (int i = 0; i < sockets.Count; i++)
            {
                var s = sockets[i];

                if (s.Status == SocketStatus.Locked || s.Status == SocketStatus.Disabled)
                {
                    sockets[i] = s;
                    continue;
                }

                if (_connectedSockets.Contains(i))
                {
                    s.SetStatus(SocketStatus.Connected);
                    sockets[i] = s;
                    continue;
                }

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

        public void SetModuleHidden(GameObject moduleGo, bool hidden)
        {
            if (!moduleGo) return;

            var pm = moduleGo.GetComponent<PlatformModule>();
            if (pm != null) pm.SetHidden(hidden);
            else moduleGo.SetActive(!hidden);

            RefreshSocketStatuses();
            QueueRebuild();
        }

        /// <summary>
        /// Toggle modules & railings on these sockets and flip sockets to Connected/Linkable.
        /// </summary>
        public void ApplyConnectionVisuals(IEnumerable<int> socketIndices, bool connected)
        {
            var set = new HashSet<int>(socketIndices);
            var toToggleModules = new HashSet<GameObject>();
            var affectedRailings = new HashSet<PlatformRailing>();

            foreach (var sIdx in set)
            {
                if (connected) _connectedSockets.Add(sIdx);
                else _connectedSockets.Remove(sIdx);

                if (_socketToModules.TryGetValue(sIdx, out var mods))
                    foreach (var go in mods) toToggleModules.Add(go);

                if (_socketToRailings.TryGetValue(sIdx, out var rails))
                    foreach (var r in rails) affectedRailings.Add(r);
            }

            foreach (var go in toToggleModules)
                SetModuleHidden(go, connected);

            foreach (var r in affectedRailings)
                UpdateRailingVisibility(r);

            RefreshSocketStatuses();
            ConnectionsChanged?.Invoke(this);
        }

        /// <summary>Editor-only convenience to clear links and show all modules/railings.</summary>
        public void EditorResetAllConnections()
        {
            // IMPORTANT FIX:
            // We must restore EVERY module & railing (even ones that were hidden
            // and unregistered before) so that socket statuses and visuals are
            // in a consistent "no connections" baseline before recomputing links.

            // 1) Show all PlatformModules under this platform (active or inactive)
            var allModules = GetComponentsInChildren<PlatformModule>(true);
            foreach (var m in allModules)
            {
                if (!m) continue;
                m.SetHidden(false);
            }

            // 2) Clear connection bookkeeping
            _connectedSockets.Clear();

#if UNITY_EDITOR
            // 3) Destroy all NavMeshLink GameObjects under "Links" in the editor,
            //    using Undo so changes are revertible.
            var linksParent = transform.Find("Links");
            if (linksParent)
            {
                for (int i = linksParent.childCount - 1; i >= 0; i--)
                    UnityEditor.Undo.DestroyObjectImmediate(linksParent.GetChild(i).gameObject);
            }
#else
            // Runtime: just destroy children under "Links"
            var linksParent = transform.Find("Links");
            if (linksParent)
            {
                for (int i = linksParent.childCount - 1; i >= 0; i--)
                    Destroy(linksParent.GetChild(i).gameObject);
            }
#endif

            // 4) Recompute rail visibility and socket statuses in the clean state
            RefreshAllRailingsVisibility();
            RefreshSocketStatuses();
            QueueRebuild();
            ConnectionsChanged?.Invoke(this);
        }

        // ---------- Lifecycle ----------

        private void Awake()
        {
            if (!_navSurface) _navSurface = GetComponent<NavMeshSurface>();
            InitializePlatform();
        }

        private void OnEnable()
        {
            _lastPos = transform.position;
            _lastRot = transform.rotation;
            _lastScale = transform.localScale;

            RegisterSelf();
            InitializePlatform();
        }

        private void OnDisable()
        {
            UnregisterSelf();
        }

        private void LateUpdate()
        {
            if (transform.position != _lastPos ||
                transform.rotation != _lastRot ||
                transform.localScale != _lastScale)
            {
                _lastPos = transform.position;
                _lastRot = transform.rotation;
                _lastScale = transform.localScale;

                PoseChanged?.Invoke(this);
            }
        }

        private void InitializePlatform()
        {
            if (!_socketsBuilt) BuildSockets();
            EnsureChildrenModulesRegistered();
            EnsureChildrenRailingsRegistered();
            RefreshSocketStatuses();
        }

        private void RegisterSelf()
        {
            if (_allPlatforms.Add(this))
                PlatformRegistered?.Invoke(this);
        }

        private void UnregisterSelf()
        {
            if (_allPlatforms.Remove(this))
                PlatformUnregistered?.Invoke(this);
        }

        public void ForcePoseChanged()
        {
            _lastPos = transform.position;
            _lastRot = transform.rotation;
            _lastScale = transform.localScale;
            PoseChanged?.Invoke(this);
        }

        public void BuildLocalNavMesh()
        {
            if (NavSurface) NavSurface.BuildNavMesh();
        }

        public void QueueRebuild()
        {
            if (!NavSurface) return;
            if (_pendingRebuild != null)
                StopCoroutine(_pendingRebuild);
            _pendingRebuild = StartCoroutine(RebuildAfterDelay(rebuildDebounceSeconds));
        }

        private IEnumerator RebuildAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (NavSurface)
                NavSurface.BuildNavMesh();
            _pendingRebuild = null;
        }

        // ---------- Gizmos ----------

        public static class GizmoSettings
        {
            public static bool ShowGizmos = true;
            public static bool ShowIndices = true;
            public static float SocketSphereRadius = 0.06f;

            public static Color ColorFree = new(0.20f, 1.00f, 0.20f, 0.90f);
            public static Color ColorOccupied = new(1.00f, 0.60f, 0.20f, 0.90f);
            public static Color ColorLocked = new(0.95f, 0.25f, 0.25f, 0.90f);
            public static Color ColorDisabled = new(0.60f, 0.60f, 0.60f, 0.90f);
            public static Color ColorConnected = new(0.20f, 0.65f, 1.00f, 0.90f);

            public static bool ShowDirections = false;   // we don't need normal arrows anymore
            public static float DirectionLength = 0.28f;

            public static void SetVisibility(bool visible) => ShowGizmos = visible;
            public static void SetShowIndices(bool show) => ShowIndices = show;

            public static void SetColors(Color free, Color occupied, Color locked, Color disabled)
            {
                ColorFree = free;
                ColorOccupied = occupied;
                ColorLocked = locked;
                ColorDisabled = disabled;
            }

            public static void SetColorsAll(Color free, Color occupied, Color locked, Color disabled, Color connected)
            {
                SetColors(free, occupied, locked, disabled);
                ColorConnected = connected;
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!GizmoSettings.ShowGizmos) return;

            // Platform footprint outline
            Gizmos.color = new Color(0f, 0.6f, 1f, 0.25f);
            float hx = footprint.x * 0.5f;
            float hz = footprint.y * 0.5f;
            var p = transform.position; var r = transform.right; var f = transform.forward;
            Vector3 a = p + (-r * hx) + (-f * hz);
            Vector3 b = p + (r * hx) + (-f * hz);
            Vector3 c = p + (r * hx) + (f * hz);
            Vector3 d = p + (-r * hx) + (f * hz);
            Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c); Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);

            if (!_socketsBuilt) BuildSockets();

            int w = Mathf.Max(1, footprint.x);
            int l = Mathf.Max(1, footprint.y);

            for (int i = 0; i < sockets.Count; i++)
            {
                var s = sockets[i];
                Color col = s.Status switch
                {
                    SocketStatus.Linkable   => GizmoSettings.ColorFree,
                    SocketStatus.Occupied   => GizmoSettings.ColorOccupied,
                    SocketStatus.Connected  => GizmoSettings.ColorConnected,
                    SocketStatus.Locked     => GizmoSettings.ColorLocked,
                    SocketStatus.Disabled   => GizmoSettings.ColorDisabled,
                    _                       => Color.white
                };
                Gizmos.color = col;

                Vector3 wp = transform.TransformPoint(s.LocalPos);
                Gizmos.DrawSphere(wp, GizmoSettings.SocketSphereRadius);

                if (GizmoSettings.ShowIndices)
                {
                    // Reconstruct pseudo edge+mark only for labeling (for debug)
                    Edge edge;
                    int mark;
                    if (i < w)                { edge = Edge.North; mark = i; }
                    else if (i < 2 * w)       { edge = Edge.South; mark = i - w; }
                    else if (i < 2 * w + l)   { edge = Edge.East;  mark = i - 2 * w; }
                    else                      { edge = Edge.West;  mark = i - (2 * w + l); }

                    string label = $"#{i} [{edge}:{mark}] {s.Status}";
                    UnityEditor.Handles.Label(wp + Vector3.up * 0.05f, label);
                }
            }
        }
#endif

        // ---------- Link creation & adjacency (socket-position based) ----------

        /// <summary>
        /// Check if two platforms are adjacent on the same level by matching perimeter socket
        /// positions in world space. If yes, mark those sockets as connected and create a NavMeshLink.
        /// </summary>
        public static bool ConnectIfAdjacent(GamePlatform a, GamePlatform b)
        {
            if (!a || !b || a == b) return false;
            if (!a._socketsBuilt) a.BuildSockets();
            if (!b._socketsBuilt) b.BuildSockets();

            var pairs = new List<(int aIdx, int bIdx)>();
            float maxDist = 0.25f; // distance threshold for matching sockets

            var aSockets = a.Sockets;
            var bSockets = b.Sockets;

            for (int i = 0; i < aSockets.Count; i++)
            {
                Vector3 wa = a.GetSocketWorldPosition(i);
                for (int j = 0; j < bSockets.Count; j++)
                {
                    Vector3 wb = b.GetSocketWorldPosition(j);

                    // Require same approximate height (same deck level)
                    if (Mathf.Abs(wa.y - wb.y) > 0.1f) continue;

                    Vector3 diff = wb - wa;
                    diff.y = 0f;
                    if (diff.sqrMagnitude <= maxDist * maxDist)
                        pairs.Add((i, j));
                }
            }

            if (pairs.Count == 0)
                return false;

            var aIdxSet = new HashSet<int>();
            var bIdxSet = new HashSet<int>();
            Vector3 sumA = Vector3.zero, sumB = Vector3.zero;

            foreach (var p in pairs)
            {
                aIdxSet.Add(p.aIdx);
                bIdxSet.Add(p.bIdx);
                sumA += a.GetSocketWorldPosition(p.aIdx);
                sumB += b.GetSocketWorldPosition(p.bIdx);
            }

            a.ApplyConnectionVisuals(aIdxSet, true);
            b.ApplyConnectionVisuals(bIdxSet, true);

            a.QueueRebuild();
            b.QueueRebuild();

            Vector3 centerA = sumA / pairs.Count;
            Vector3 centerB = sumB / pairs.Count;
            CreateSimpleNavLink(a, centerA, centerB);

            return true;
        }

        private static void CreateSimpleNavLink(GamePlatform owner, Vector3 aPos, Vector3 bPos)
        {
            var parent = GetOrCreate(owner.transform, "Links");
            var go = new GameObject("Link_" + owner.name);
            go.transform.SetParent(parent, false);

            Vector3 center = 0.5f * (aPos + bPos);
            go.transform.position = center;

            var link = go.AddComponent<NavMeshLink>();
            link.startPoint = go.transform.InverseTransformPoint(aPos);
            link.endPoint   = go.transform.InverseTransformPoint(bPos);
            link.bidirectional = true;
            link.width = 0.6f;
            link.area = 0;
            link.agentTypeID = owner.NavSurface ? owner.NavSurface.agentTypeID : 0;
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

        public void EnsureChildrenRailingsRegistered()
        {
            var railings = GetComponentsInChildren<PlatformRailing>(true);
            foreach (var r in railings)
                r.EnsureRegistered();
        }
    }
}
