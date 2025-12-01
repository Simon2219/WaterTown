using System.Collections.Generic;
using Grid;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using WaterTown.Platforms;

namespace WaterTown.Town
{
    [DisallowMultipleComponent]
    public class TownManager : MonoBehaviour
    {
        [Header("Grid")]
        [SerializeField] private WorldGrid grid;
        public WorldGrid Grid => grid;

        [Header("Default Level")]
        [Tooltip("Level index to use when auto-computing a platform's cells.")]
        [SerializeField] private int defaultLevel = 0;

        // --- Platform occupancy bookkeeping ---

        private class PlatformEntry
        {
            public GamePlatform platform;
            public List<Vector2Int> cells2D = new();
            public int level;
            public bool marksOccupied; // true = we write Occupied flags into the grid
        }

        // Platforms we know about → their occupied cells
        private readonly Dictionary<GamePlatform, PlatformEntry> _entries = new();

        // temp lists
        private static readonly List<GamePlatform> _tmpPlatforms = new();
        private readonly List<Vector2Int> _tmpCells2D = new();

        // ---------- Unity lifecycle ----------

        private void Awake()
        {
            // Only auto-find if nothing is wired in the inspector
            if (!grid)
                grid = FindFirstObjectByType<WorldGrid>();
        }

        private void OnEnable()
        {
            GamePlatform.PlatformRegistered   += OnPlatformRegistered;
            GamePlatform.PlatformUnregistered += OnPlatformUnregistered;
        }

        private void Start()
        {
            // Ensure all existing platforms in the scene are registered into the grid
            if (!grid)
                grid = FindFirstObjectByType<WorldGrid>();

            foreach (var gp in GamePlatform.AllPlatforms)
            {
                if (!gp) continue;
                if (!gp.isActiveAndEnabled) continue;

                _tmpCells2D.Clear();
                ComputeCellsForPlatform(gp, defaultLevel, _tmpCells2D);
                if (_tmpCells2D.Count > 0)
                {
                    // Registers occupancy AND triggers adjacency for all platforms
                    RegisterPlatform(gp, _tmpCells2D, defaultLevel, markOccupiedInGrid: true);
                }
            }
        }

        private void OnDisable()
        {
            GamePlatform.PlatformRegistered   -= OnPlatformRegistered;
            GamePlatform.PlatformUnregistered -= OnPlatformUnregistered;

            // Best-effort cleanup of pose subscriptions
            foreach (var kvp in _entries)
            {
                if (kvp.Key)
                    kvp.Key.PoseChanged -= OnPlatformPoseChanged;
            }
        }

        // ---------- GamePlatform events ----------

        private void OnPlatformRegistered(GamePlatform gp)
        {
            // We intentionally DO NOT auto-register into the grid here,
            // because BuildModeManager controls when a platform is
            // preview-only vs actually placed.
            // Scene platforms are registered once in Start().
        }

        private void OnPlatformUnregistered(GamePlatform gp)
        {
            if (!gp) return;
            UnregisterPlatform(gp);
        }

        /// <summary>
        /// Called when a platform reports its transform changed.
        /// Optional runtime support for moving platforms (and the ghost).
        /// </summary>
        private void OnPlatformPoseChanged(GamePlatform gp)
        {
            if (!grid || gp == null) return;
            if (!_entries.TryGetValue(gp, out var entry)) return;

            int level = entry.level;
            bool marksOccupied = entry.marksOccupied;

            // 1) Clear old occupancy for this platform
            if (marksOccupied)
            {
                foreach (var c2 in entry.cells2D)
                {
                    var cell = new Vector3Int(c2.x, c2.y, level);
                    grid.TryRemoveFlag(cell, WorldGrid.CellFlag.Occupied);
                }
            }

            // 2) Compute new footprint cells from current transform (rotation-aware)
            _tmpCells2D.Clear();
            ComputeCellsForPlatform(gp, level, _tmpCells2D);

            entry.cells2D.Clear();
            entry.cells2D.AddRange(_tmpCells2D);

            if (marksOccupied)
            {
                foreach (var c2 in entry.cells2D)
                {
                    var cell = new Vector3Int(c2.x, c2.y, level);
                    grid.TryAddFlag(cell, WorldGrid.CellFlag.Occupied, platformId: 0, payload: gp.GetInstanceID());
                }
            }

            // 3) Recompute adjacency for ALL platforms (global pass, same as SceneView tool)
            RecomputeAllAdjacency();
        }

        // ---------- Public API ----------

        /// <summary>
        /// True if all given 2D cells (at level) are inside the grid and not Occupied.
        /// Used by BuildModeManager as placement validation.
        /// </summary>
        public bool IsAreaFree(List<Vector2Int> cells, int level = 0)
        {
            if (!grid) return false;
            foreach (var c in cells)
            {
                var cell = new Vector3Int(c.x, c.y, level);
                if (!grid.CellInBounds(cell)) return false;
                if (grid.CellHasAnyFlag(cell, WorldGrid.CellFlag.Occupied))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Register a platform at a set of grid cells on a given level.
        /// markOccupiedInGrid=false is used for the GHOST (preview): it still participates
        /// in adjacency, but does NOT reserve cells in the WorldGrid.
        /// </summary>
        public void RegisterPlatform(
            GamePlatform gp,
            List<Vector2Int> cells,
            int level = 0,
            bool markOccupiedInGrid = true)
        {
            if (!gp || cells == null || cells.Count == 0 || !grid) return;

            // Remove old occupancy if any
            if (_entries.TryGetValue(gp, out var oldEntry))
            {
                if (oldEntry.marksOccupied)
                {
                    foreach (var c2 in oldEntry.cells2D)
                    {
                        var cell = new Vector3Int(c2.x, c2.y, oldEntry.level);
                        grid.TryRemoveFlag(cell, WorldGrid.CellFlag.Occupied);
                    }
                }
            }

            // Ensure we have an entry and are listening to pose changes
            if (!_entries.TryGetValue(gp, out var entry))
            {
                entry = new PlatformEntry { platform = gp };
                _entries[gp] = entry;
                gp.PoseChanged += OnPlatformPoseChanged;
            }

            entry.level         = level;
            entry.marksOccupied = markOccupiedInGrid;
            entry.cells2D.Clear();
            entry.cells2D.AddRange(cells);

            if (markOccupiedInGrid)
            {
                foreach (var c in cells)
                {
                    var cell = new Vector3Int(c.x, c.y, level);
                    grid.TryAddFlag(cell, WorldGrid.CellFlag.Occupied, platformId: 0, payload: gp.GetInstanceID());
                }
            }

            // Recompute adjacency for all platforms
            RecomputeAllAdjacency();
        }

        /// <summary>
        /// Helper for preview: register platform using its current transform footprint,
        /// but WITHOUT marking cells as Occupied in the grid.
        /// The ghost still participates in adjacency (railing preview).
        /// </summary>
        public void RegisterPreviewPlatform(GamePlatform gp, int level = 0)
        {
            if (!gp || !grid) return;

            _tmpCells2D.Clear();
            ComputeCellsForPlatform(gp, level, _tmpCells2D);
            if (_tmpCells2D.Count == 0) return;

            RegisterPlatform(gp, _tmpCells2D, level, markOccupiedInGrid: false);
        }

        /// <summary>
        /// Removes platform occupancy from the grid and clears its connections.
        /// </summary>
        public void UnregisterPlatform(GamePlatform gp)
        {
            if (!gp || !grid) return;
            if (!_entries.TryGetValue(gp, out var entry)) return;

            gp.PoseChanged -= OnPlatformPoseChanged;

            if (entry.marksOccupied)
            {
                foreach (var c2 in entry.cells2D)
                {
                    var cell = new Vector3Int(c2.x, c2.y, entry.level);
                    grid.TryRemoveFlag(cell, WorldGrid.CellFlag.Occupied);
                }
            }

            _entries.Remove(gp);

            // Clear connections on this platform
            gp.EditorResetAllConnections();

            // Recompute adjacency for all remaining platforms
            RecomputeAllAdjacency();
        }

        // ---------- Core adjacency logic (GLOBAL) ----------

        /// <summary>
        /// Recomputes connections for ALL platforms (runtime),
        /// using the exact same algorithm as the editor SceneView tester:
        /// - Reset all connections/modules/railings
        /// - Try pairwise GamePlatform.ConnectIfAdjacent
        /// </summary>
        private void RecomputeAllAdjacency()
        {
            if (!grid) return;

            _tmpPlatforms.Clear();
            foreach (var gp in GamePlatform.AllPlatforms)
            {
                if (!gp) continue;
                if (!gp.isActiveAndEnabled) continue;
                _tmpPlatforms.Add(gp);
            }

            // Ensure registration is always valid
            foreach (var p in _tmpPlatforms)
            {
                p.EnsureChildrenModulesRegistered();
                p.EnsureChildrenRailingsRegistered();
            }

            // Reset everything first so rails reappear when platforms separate
            foreach (var p in _tmpPlatforms)
                p.EditorResetAllConnections();

            // Try pairwise connections for currently touching platforms
            int count = _tmpPlatforms.Count;
            for (int i = 0; i < count; i++)
            {
                var a = _tmpPlatforms[i];
                for (int j = i + 1; j < count; j++)
                {
                    var b = _tmpPlatforms[j];
                    GamePlatform.ConnectIfAdjacent(a, b);
                }
            }
        }

        // ---------- Helpers ----------

        /// <summary>
        /// Compute which 2D grid cells a platform covers on a given level,
        /// assuming its footprint is aligned to the 1x1 world grid AND
        /// that rotation is in 90° steps (0, 90, 180, 270).
        /// This is the single source of truth for runtime footprint.
        /// </summary>
        public void ComputeCellsForPlatform(GamePlatform gp, int level, List<Vector2Int> into)
        {
            into.Clear();
            if (!gp || !grid) return;

            // Use platform's world position to find a "center" cell on the desired level
            Vector3 worldCenter = gp.transform.position;
            var centerCell = grid.WorldToCellOnLevel(worldCenter, new Vector3Int(0, 0, level));
            int cx = centerCell.x;
            int cy = centerCell.y;

            int w = Mathf.Max(1, gp.Footprint.x);
            int h = Mathf.Max(1, gp.Footprint.y);

            // Determine rotation in 90° steps (0..3)
            float yaw = gp.transform.eulerAngles.y;
            int steps = Mathf.RoundToInt(yaw / 90f) & 3;
            bool swap = (steps % 2) == 1; // 90 or 270

            int fw = swap ? h : w; // width in cells after rotation
            int fh = swap ? w : h; // height in cells after rotation

            int startX = cx - fw / 2;
            int startY = cy - fh / 2;

            for (int y = 0; y < fh; y++)
            {
                for (int x = 0; x < fw; x++)
                {
                    int gx = startX + x;
                    int gy = startY + y;
                    var cell = new Vector3Int(gx, gy, level);
                    if (!grid.CellInBounds(cell))
                        continue;

                    into.Add(new Vector2Int(gx, gy));
                }
            }
        }

        private static Transform GetOrCreateChild(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (!t)
            {
                var go = new GameObject(name);
                t = go.transform;
                t.SetParent(parent, false);
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale    = Vector3.one;
            }
            return t;
        }

        private void CreateNavLinkBetween(GamePlatform a, int socketA, GamePlatform b, int socketB, Vector3 center)
        {
            Vector3 posA = a.GetSocketWorldPosition(socketA);
            Vector3 posB = b.GetSocketWorldPosition(socketB);

            var owner = GetOrCreateChild(a.transform, "Links");
            var go    = new GameObject($"Link_{a.name}_s{socketA}_to_{b.name}_s{socketB}");
            go.transform.SetParent(owner, false);
            go.transform.position = center;

            var link = go.AddComponent<NavMeshLink>();
            link.startPoint    = go.transform.InverseTransformPoint(posA);
            link.endPoint      = go.transform.InverseTransformPoint(posB);
            link.bidirectional = true;
            link.width         = 0.6f;
            link.area          = 0;
            link.agentTypeID   = a.NavSurface ? a.NavSurface.agentTypeID : 0;

            a.QueueRebuild();
            b.QueueRebuild();
        }
    }
}
