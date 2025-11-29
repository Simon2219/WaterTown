// Assets/Scripts/Grid/GridSettings.cs
using UnityEngine;

namespace Grid
{
    [CreateAssetMenu(fileName = "GridSettings", menuName = "Scriptable Objects/Grid Settings", order = 10)]
    public class GridSettings : ScriptableObject
    {
        [Header("Grid Dimensions (cells)")]
        [Min(1)] public int sizeX = 128;          // columns (X)
        [Min(1)] public int sizeY = 128;          // rows (Z)
        [Min(1)] public int levels = 1;           // decks (0..levels-1)

        [Header("Metrics")]
        [Tooltip("Cell edge length in meters (1 => 1×1 m).")]
        [Min(1)] public int cellSize = 1;
        [Tooltip("Vertical spacing between decks in meters.")]
        [Min(1)] public int levelStep = 10;

        [Header("Origin")]
        [Tooltip("World-space origin of cell (0,0,0) lower-left corner.")]
        public Vector3 worldOrigin = Vector3.zero;

#if UNITY_EDITOR
        private void OnValidate()
        {
            sizeX     = Mathf.Max(1, sizeX);
            sizeY     = Mathf.Max(1, sizeY);
            levels    = Mathf.Max(1, levels);
            cellSize  = Mathf.Max(1, cellSize);
            levelStep = Mathf.Max(1, levelStep);
        }
#endif
    }
}
