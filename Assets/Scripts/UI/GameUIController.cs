using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Platforms;




/// <summary>
/// Central UI controller for the build toolbar.
/// Manages blueprint selection, UI visibility, and toolbar population.
/// </summary>
public class GameUIController : MonoBehaviour
{
    [Header("Build Bar")]
    [SerializeField] private Animator barAnimator;
    [SerializeField] private CanvasGroup barCanvasGroup;
    [SerializeField] private string showTrigger = "Show";
    [SerializeField] private string hideTrigger = "Hide";

    [Header("Toolbar Content")]
    [SerializeField] private RectTransform toolbarContent;
    [SerializeField] private GameObject platformIconPrefab;
    [SerializeField] private List<PlatformBlueprint> availableBlueprints = new();

    [Header("Selection Visuals")]
    [Tooltip("Child name for icon image. Falls back to root Image if not found.")]
    [SerializeField] private string iconChildName = "Icon";

    [Tooltip("Child name for selection overlay. Auto-created if missing.")]
    [SerializeField] private string selectedFrameChildName = "SelectedFrame";

    [Tooltip("Color for auto-created selection overlay.")]
    [SerializeField] private Color selectedOverlayColor = new Color(1f, 1f, 1f, 0.18f);

    [Header("Behavior")]
    [Tooltip("Clear selection when build bar is hidden.")]
    [SerializeField] private bool clearSelectionOnHide = true;

    [Header("Events")]
    [Tooltip("Invoked when build bar is shown.")]
    public UnityEvent OnBuildBarShown = new UnityEvent();
    
    [Tooltip("Invoked when build bar is hidden.")]
    public UnityEvent OnBuildBarHidden = new UnityEvent();

    // C# event for BuildModeManager (internal, performance-critical)
    public event Action<PlatformBlueprint> OnBlueprintSelected;

    public bool IsBuildBarVisible { get; private set; }
    public PlatformBlueprint SelectedBlueprint => GetSelectedBlueprint();

    // runtime caches
    private readonly List<Button> _buttons = new();
    private readonly List<GameObject> _selectedFrames = new();
    private int _selectedIndex = -1;

    private void Awake()
    {
        // Start hidden/gated
        IsBuildBarVisible = false;
        if (barCanvasGroup != null)
        {
            barCanvasGroup.alpha = 0f;
            barCanvasGroup.interactable = false;
            barCanvasGroup.blocksRaycasts = false;
        }

        PopulateToolbar();
    }

    public void ShowBuildBar()
    {
        if (IsBuildBarVisible) return;
        IsBuildBarVisible = true;

        if (barAnimator) barAnimator.SetTrigger(showTrigger);

        if (barCanvasGroup)
        {
            barCanvasGroup.interactable = true;
            barCanvasGroup.blocksRaycasts = true;
        }

        OnBuildBarShown?.Invoke();
    }

    public void HideBuildBar()
    {
        if (!IsBuildBarVisible) return;
        IsBuildBarVisible = false;

        if (barAnimator) barAnimator.SetTrigger(hideTrigger);

        if (barCanvasGroup)
        {
            barCanvasGroup.interactable = false;
            barCanvasGroup.blocksRaycasts = false;
        }

        if (clearSelectionOnHide)
            ClearSelection();

        OnBuildBarHidden?.Invoke();
    }

    public void ToggleBuildBar()
    {
        if (IsBuildBarVisible) HideBuildBar();
        else ShowBuildBar();
    }

    public void SetBlueprints(IEnumerable<PlatformBlueprint> blueprintList)
    {
        availableBlueprints = new List<PlatformBlueprint>(blueprintList ?? Array.Empty<PlatformBlueprint>());
        PopulateToolbar();
    }

    public PlatformBlueprint GetSelectedBlueprint()
    {
        return (_selectedIndex >= 0 && _selectedIndex < availableBlueprints.Count) ? availableBlueprints[_selectedIndex] : null;
    }

    public void ClearSelection()
    {
        if (_selectedIndex >= 0 && _selectedIndex < _selectedFrames.Count)
        {
            var prev = _selectedFrames[_selectedIndex];
            if (prev) prev.SetActive(false);
        }
        _selectedIndex = -1;
    }

    // ---------- internal ----------

    private void PopulateToolbar()
    {
        if (!toolbarContent || !platformIconPrefab) return;

        ClearToolbarChildren();
        _buttons.Clear();
        _selectedFrames.Clear();
        _selectedIndex = -1;

        for (int i = 0; i < availableBlueprints.Count; i++)
        {
            var bp = availableBlueprints[i];
            var go = Instantiate(platformIconPrefab, toolbarContent);

            // Naming rule: Blueprint.DisplayName if present, else keep prefab's name
            var desiredName = (bp && !string.IsNullOrWhiteSpace(bp.DisplayName))
                ? bp.DisplayName
                : platformIconPrefab.name;
            go.name = desiredName;

            // Find icon Image (prefer child named iconChildName, else root Image)
            var iconImg = FindIconImage(go.transform, iconChildName);
            if (iconImg && bp) iconImg.sprite = bp.Icon;

            // Find Button (root or child)
            var btn = go.GetComponent<Button>() ?? go.GetComponentInChildren<Button>(true);
            if (btn != null)
            {
                int idx = i;
                btn.onClick.AddListener(() => OnIconClicked(idx));
                _buttons.Add(btn);
            }
            else
            {
                Debug.LogWarning($"[GameUIController] No Button found on item '{go.name}'.");
                _buttons.Add(null);
            }

            // Ensure a SelectedFrame exists (find or create) and start hidden
            var selFrame = FindOrCreateSelectedFrame(go.transform, selectedFrameChildName, selectedOverlayColor);
            selFrame.SetActive(false);
            _selectedFrames.Add(selFrame);
        }
    }

    private void OnIconClicked(int index)
    {
        // Turn off previous
        if (_selectedIndex >= 0 && _selectedIndex < _selectedFrames.Count)
        {
            var prev = _selectedFrames[_selectedIndex];
            if (prev) prev.SetActive(false);
        }

        _selectedIndex = index;

        // Turn on current
        var curr = (_selectedIndex >= 0 && _selectedIndex < _selectedFrames.Count)
            ? _selectedFrames[_selectedIndex]
            : null;
        if (curr) curr.SetActive(true);

        // Fire event
        var bp = (index >= 0 && index < availableBlueprints.Count) ? availableBlueprints[index] : null;
        OnBlueprintSelected?.Invoke(bp);
    }

    private static Image FindIconImage(Transform root, string childName)
    {
        if (!root) return null;

        if (!string.IsNullOrEmpty(childName))
        {
            var t = root.Find(childName);
            if (t)
            {
                var img = t.GetComponent<Image>();
                if (img) return img;
            }
        }

        var rootImg = root.GetComponent<Image>();
        if (rootImg) return rootImg;

        return root.GetComponentInChildren<Image>(true);
    }

    private static GameObject FindOrCreateSelectedFrame(Transform root, string frameName, Color overlayColor)
    {
        if (!root) return null;

        if (!string.IsNullOrEmpty(frameName))
        {
            var t = root.Find(frameName);
            if (t) return t.gameObject;
        }

        // Create a simple full-rect overlay
        var go = new GameObject(string.IsNullOrEmpty(frameName) ? "SelectedFrame" : frameName, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(root, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);

        var img = go.GetComponent<Image>();
        img.color = overlayColor;
        img.raycastTarget = false; // don't block clicks

        go.transform.SetAsLastSibling();
        return go;
    }

    private void ClearToolbarChildren()
    {
        if (!toolbarContent) return;
        for (int i = toolbarContent.childCount - 1; i >= 0; i--)
            Destroy(toolbarContent.GetChild(i).gameObject);
    }
}
