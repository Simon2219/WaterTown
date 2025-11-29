using System.Collections.Generic;
using UnityEngine;
using WaterTown.Platforms;

namespace WaterTown.Town
{
    /// <summary>
    /// Holds city state for placements and acts as the lightweight runtime planner:
    /// - Tracks which cells are occupied by which GamePlatform
    /// - Listens to platform PoseChanged and recomputes links vs. neighbors (debounced)
    /// - Recomputes on load
    /// </summary>
    public class TownManager : MonoBehaviour
    {
        [SerializeField] private Grid.WorldGrid worldGrid;

        // cell -> platform
        private readonly Dictionary<Vector2Int, GamePlatform> cellToPlatform = new();
        // platform -> cells
        private readonly Dictionary<GamePlatform, HashSet<Vector2Int>> platformToCells = new();

        // runtime planner: pending platforms to reevaluate this frame (debounced)
        private readonly HashSet<GamePlatform> _pending = new();
        [SerializeField, Range(0f, 0.25f)] private float debounceSeconds = 0.05f;
        private float _nextFlushTime;

        public Grid.WorldGrid Grid => worldGrid;
        private static readonly HashSet<GamePlatform> _tmpNeighbors = new();
        private void Awake()
        {
            if (!worldGrid)
                Debug.LogError("[TownManager] Missing WorldGrid reference.", this);
        }

        private void Start()
        {
            // On load, find any preplaced platforms in the scene and register them.
            var all = FindObjectsOfType<GamePlatform>(includeInactive: false);
            foreach (var gp in all)
            {
                // If not already registered with cells (because they weren’t placed via BuildMode),
                // try to infer a best-effort area from their footprint and transform center.
                if (!platformToCells.ContainsKey(gp))
                {
                    var guess = GuessCellsFromTransform(gp);
                    RegisterPlatform(gp, guess);
                }
            }

            // Initial full recompute once everything is registered.
            RecomputeAllLinks();
        }

        private void Update()
        {
            if (_pending.Count == 0) return;
            if (Time.time < _nextFlushTime) return;

            // Flush the batch
            var list = ListFromSet(_pending);
            _pending.Clear();

            foreach (var gp in list)
                RecomputeLinksForPlatformAndNeighbors(gp);
        }

        // ------------ Public API unchanged for BuildModeManager ------------

        public bool IsAreaFree(List<Vector2Int> cells)
        {
            for (int i = 0; i < cells.Count; i++)
                if (cellToPlatform.ContainsKey(cells[i]))
                    return false;
            return true;
        }

        public void RegisterPlatform(GamePlatform gp, List<Vector2Int> cells)
        {
            if (!gp) return;
            if (!platformToCells.TryGetValue(gp, out var set))
            {
                set = new HashSet<Vector2Int>();
                platformToCells[gp] = set;
            }
            foreach (var c in cells)
            {
                cellToPlatform[c] = gp;
                set.Add(c);
            }

            // Subscribe to pose changes so we can relink while dragging/moving/rotating
            gp.PoseChanged += OnPlatformPoseChanged;

            // Attempt initial adjacency connections right after placement
            RecomputeConnectionsAround(gp);
        }

        public void UnregisterPlatform(GamePlatform gp)
        {
            if (!gp) return;

            // Unhook pose-change listener
            gp.PoseChanged -= OnPlatformPoseChanged;

            if (!platformToCells.TryGetValue(gp, out var set)) return;

            foreach (var c in set)
                cellToPlatform.Remove(c);

            platformToCells.Remove(gp);
        }


        public GamePlatform GetPlatformAtCell(Vector2Int cell)
        {
            return cellToPlatform.TryGetValue(cell, out var gp) ? gp : null;
        }

        public bool TryGetCells(GamePlatform gp, out HashSet<Vector2Int> cells)
        {
            return platformToCells.TryGetValue(gp, out cells);
        }

        // ------------ Runtime planner internals ------------
        

        private void QueueRecompute(GamePlatform gp)
        {
            if (!gp) return;
            _pending.Add(gp);
            _nextFlushTime = Time.time + debounceSeconds;
        }

        /// <summary>Full recompute for all registered platforms (used on load).</summary>
        private void RecomputeAllLinks()
        {
            // Reset connections on all, then connect where adjacent
            foreach (var gp in platformToCells.Keys)
                gp.EditorResetAllConnections();

            var list = ListFromSet(platformToCells.Keys);
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                var a = list[i];
                var neigh = CollectNeighborPlatforms(a);
                foreach (var b in neigh)
                {
                    // To avoid double-work, connect only in a deterministic order
                    if (a.GetInstanceID() < b.GetInstanceID())
                        GamePlatform.ConnectIfAdjacent(a, b);
                }
            }
        }

        private void OnPlatformPoseChanged(GamePlatform moved)
        {
            // When a platform moves/rotates/scales, recompute connections with its immediate neighbors.
            RecomputeConnectionsAround(moved);
        }

        private void RecomputeConnectionsAround(GamePlatform moved)
        {
            if (!moved) return;

            // 1) Reset connections/hidden modules on the moved platform
            moved.EditorResetAllConnections();

            // 2) Collect immediate neighbors (4-neighborhood around each occupied cell)
            _tmpNeighbors.Clear();
            if (TryGetCells(moved, out var movedCells))
            {
                foreach (var c in movedCells)
                {
                    TryAddNeighbor(new Vector2Int(c.x + 1, c.y), moved);
                    TryAddNeighbor(new Vector2Int(c.x - 1, c.y), moved);
                    TryAddNeighbor(new Vector2Int(c.x, c.y + 1), moved);
                    TryAddNeighbor(new Vector2Int(c.x, c.y - 1), moved);
                }
            }

            // Reset neighbors too so their modules/links are in a clean state
            foreach (var n in _tmpNeighbors)
                n.EditorResetAllConnections();

            // 3) Reconnect moved <-> each neighbor (minimal span + hide railings handled inside)
            foreach (var n in _tmpNeighbors)
                GamePlatform.ConnectIfAdjacent(moved, n);

            // 4) Optional: also reconnect neighbor pairs among themselves to keep chains valid while sliding
            var arr = new List<GamePlatform>(_tmpNeighbors);
            for (int i = 0; i < arr.Count; i++)
            for (int j = i + 1; j < arr.Count; j++)
                GamePlatform.ConnectIfAdjacent(arr[i], arr[j]);
        }

        private void TryAddNeighbor(Vector2Int cell, GamePlatform exclude)
        {
            var gp = GetPlatformAtCell(cell);
            if (gp && gp != exclude)
                _tmpNeighbors.Add(gp);
        }
        
        /// <summary>Recompute links only for one platform and the platforms that touch it.</summary>
        private void RecomputeLinksForPlatformAndNeighbors(GamePlatform gp)
        {
            if (!gp) return;

            var neighbors = CollectNeighborPlatforms(gp);

            // First, clear connections on gp (show railings, delete links, rebuild gp navmesh)
            gp.EditorResetAllConnections();

            // Then clear connections on each neighbor that involve their touching edges.
            // Simpler (and robust): reset neighbors fully too; we'll reconnect immediately after.
            foreach (var n in neighbors)
                n.EditorResetAllConnections();

            // Now reconnect gp<->neighbor pairs if they are adjacent & linkable.
            foreach (var n in neighbors)
                GamePlatform.ConnectIfAdjacent(gp, n);
        }

        /// <summary>Collect platforms that touch any cell edge of gp (4-neighborhood based on the grid map).</summary>
        private HashSet<GamePlatform> CollectNeighborPlatforms(GamePlatform gp)
        {
            var result = new HashSet<GamePlatform>();
            if (!platformToCells.TryGetValue(gp, out var myCells)) return result;

            // For each occupied cell, check 4 neighbors in the grid dictionary.
            foreach (var c in myCells)
            {
                TryAddNeighbor(new Vector2Int(c.x + 1, c.y), gp, result);
                TryAddNeighbor(new Vector2Int(c.x - 1, c.y), gp, result);
                TryAddNeighbor(new Vector2Int(c.x, c.y + 1), gp, result);
                TryAddNeighbor(new Vector2Int(c.x, c.y - 1), gp, result);
            }
            return result;

            void TryAddNeighbor(Vector2Int cell, GamePlatform self, HashSet<GamePlatform> into)
            {
                if (cellToPlatform.TryGetValue(cell, out var other) && other && other != self)
                    into.Add(other);
            }
        }

        // If platforms existed already in the scene (not placed through BuildMode),
        // make a best-effort guess of their occupied cells from transform & footprint.
        private List<Vector2Int> GuessCellsFromTransform(GamePlatform gp)
        {
            var list = new List<Vector2Int>(gp.Footprint.x * gp.Footprint.y);

            // We align its center to nearest cell center and fill footprint, respecting 90° rotation.
            Vector3 p = gp.transform.position;
            int rotSteps = Mathf.RoundToInt((gp.transform.eulerAngles.y % 360f) / 90f) & 3;
            bool swapped = (rotSteps % 2) == 1;
            int fw = swapped ? gp.Footprint.y : gp.Footprint.x;
            int fh = swapped ? gp.Footprint.x : gp.Footprint.y;

            // Compute the "start cell" (lower-left of the footprint rectangle)
            float cs = worldGrid ? worldGrid.cellSize : 1f;
            Vector3 o = worldGrid ? worldGrid.worldOrigin : Vector3.zero;

            // nearest cell center
            int cx = Mathf.RoundToInt((p.x - o.x) / cs);
            int cy = Mathf.RoundToInt((p.z - o.z) / cs);

            int startX = cx - fw / 2;
            int startY = cy - fh / 2;

            for (int y = 0; y < fh; y++)
                for (int x = 0; x < fw; x++)
                    list.Add(new Vector2Int(startX + x, startY + y));

            return list;
        }

        private static List<T> ListFromSet<T>(IEnumerable<T> s)
        {
            var l = new List<T>();
            foreach (var x in s) l.Add(x);
            return l;
        }
    }
}
