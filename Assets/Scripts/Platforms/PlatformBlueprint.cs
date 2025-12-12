using UnityEngine;
using Platforms;

/// <summary>
/// Authoritative data for a Platform type:
/// - Footprint (used by GamePlatform & placement)
/// - Placement rules (adjacency, levels, blockers)
/// - UI metadata (name, icon, category)
/// - Prefab references (runtime + optional lightweight preview)
///
/// The runtime prefab should contain a GamePlatform component
/// and will be instantiated on successful placement.
/// </summary>
[CreateAssetMenu(
    fileName = "NewPlatformBlueprint",
    menuName = "Scriptable Objects/Platform Blueprint",
    order = 10)]
public class PlatformBlueprint : ScriptableObject
{
    // ---------- Identity & UI ----------
    [Header("Identity & UI")]
    [SerializeField] private string id = "platform.id";
    [SerializeField] private string displayName = "Platform";
    [SerializeField] private Sprite icon;
    [SerializeField] private string category = "Default";
    [TextArea] [SerializeField] private string description;

    // ---------- Prefabs ----------
    [Header("Prefabs")]
    [Tooltip("Runtime prefab to instantiate on placement. Must have GamePlatform.")]
    [SerializeField] private GameObject runtimePrefab;

    [Tooltip("Optional lightweight prefab for the ghost/preview. If null, runtime prefab + preview materials are used.")]
    [SerializeField] private GameObject previewPrefab;

    // ---------- Preview Visuals ----------
    [Header("Preview Materials (optional)")]
    [Tooltip("Applied to the preview when placement is valid.")]
    [SerializeField] private Material previewValidMaterial;
    [Tooltip("Applied to the preview when placement is invalid/blocked.")]
    [SerializeField] private Material previewInvalidMaterial;

    // ---------- Core Platform Data ----------
    [Header("Footprint")]
    [Tooltip("Footprint in whole grid cells (meters). This is the authoritative size.")]
    [Min(1)] [SerializeField] private Vector2Int footprint = new Vector2Int(4, 4);

    // ---------- Placement Rules (grid-only) ----------
    public enum PivotMode   { Center, CornerNE, CornerSE, CornerSW, CornerNW }
    public enum RotationStep { Deg90 = 90, Deg45 = 45 }

    [Header("Placement Rules (Grid)")]
    [SerializeField] private PivotMode pivot = PivotMode.Center;
    [SerializeField] private RotationStep rotationStep = RotationStep.Deg90;

    [Tooltip("If true, a new platform must touch (share an edge with) an existing platform.")]
    [SerializeField] private bool requireEdgeAdjacency = true;

    [Tooltip("If true, corner-only (diagonal) contact is not sufficient for adjacency.")]
    [SerializeField] private bool disallowCornerAdjacency = true;

    // ---------- Economy (optional) ----------
    [Header("Economy (optional)")]
    [Min(0)] [SerializeField] private int cost = 0;
    [SerializeField] private string costCurrency = "Credits";

    // ---------- Public API (read-only) ----------
    public string Id          => id;
    public string DisplayName => displayName;
    public Sprite Icon        => icon;
    public string Category    => category;
    public string Description => description;

    public GameObject RuntimePrefab => runtimePrefab;
    public GameObject PreviewPrefab => previewPrefab;

    public Material PreviewValidMaterial   => previewValidMaterial;
    public Material PreviewInvalidMaterial => previewInvalidMaterial;

    public Vector2Int Footprint => new Vector2Int(Mathf.Max(1, footprint.x), Mathf.Max(1, footprint.y));

    public PivotMode    Pivot   => pivot;
    public RotationStep RotStep => rotationStep;

    public bool RequireEdgeAdjacency    => requireEdgeAdjacency;
    public bool DisallowCornerAdjacency => disallowCornerAdjacency;

    public int    Cost         => cost;
    public string CostCurrency => costCurrency;

#if UNITY_EDITOR
    private void OnValidate()
    {
        // Clamp footprint to >= 1
        footprint = new Vector2Int(Mathf.Max(1, footprint.x), Mathf.Max(1, footprint.y));

        // Basic prefab sanity: must contain GamePlatform
        if (runtimePrefab && !runtimePrefab.GetComponent<GamePlatform>())
        {
            Debug.LogWarning($"[PlatformBlueprint] '{name}' runtimePrefab '{runtimePrefab.name}' has no GamePlatform component.", this);
        }

        // Warn if previewPrefab is very heavy (has NavMesh, etc.) — optional nudge
        if (previewPrefab && previewPrefab.GetComponentInChildren<Unity.AI.Navigation.NavMeshSurface>())
        {
            Debug.LogWarning($"[PlatformBlueprint] '{name}' previewPrefab '{previewPrefab.name}' contains NavMeshSurface. Consider a lighter preview.", this);
        }
    }
#endif
}
