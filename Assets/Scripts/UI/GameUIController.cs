using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using WaterTown.Platforms;

namespace WaterTown.Building.UI
{
    /// <summary>
    /// Central UI controller (lives on the root "UI" GameObject).
    /// Owns the Build toolbar: populates icons, handles selection, exposes Show/Hide/Toggle.
    /// </summary>
    public class GameUIController : MonoBehaviour
    {
        [Header("Build Bar (wiring)")]
        [SerializeField] private Animator barAnimator;
        [SerializeField] private CanvasGroup barCanvasGroup;
        [SerializeField] private string showTrigger = "Show";
        [SerializeField] private string hideTrigger = "Hide";

        [Header("Toolbar Content")]
        [SerializeField] private RectTransform content;
        [SerializeField] private GameObject platformIconPrefab;
        [SerializeField] private List<PlatformBlueprint> blueprints = new();

        [Header("Selection visuals")]
        [Tooltip("We try this child for the icon first. If not found, we fall back to root Image.")]
        [SerializeField] private string iconChildName = "Icon";

        [Tooltip("Child name used as selection overlay. If missing, we auto-create it.")]
        [SerializeField] private string selectedFrameChildName = "SelectedFrame";

        [Tooltip("Color for the auto-created selection overlay.")]
        [SerializeField] private Color selectedOverlayColor = new Color(1f, 1f, 1f, 0.18f);

        [Header("Behavior")]
        [Tooltip("If true, clears the current selection when the bar hides.")]
        [SerializeField] private bool clearSelectionOnHide = true;

        public event Action<PlatformBlueprint> OnBlueprintSelected;

        public bool IsBuildBarVisible { get; private set; }

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
                // Let clicks through immediately; visuals animate separately
                barCanvasGroup.interactable = true;
                barCanvasGroup.blocksRaycasts = true;
            }
        }

        public void HideBuildBar()
        {
            if (!IsBuildBarVisible) return;
            IsBuildBarVisible = false;

            if (barAnimator) barAnimator.SetTrigger(hideTrigger);

            // Gate clicks right away so gameplay can receive input during slide-out
            if (barCanvasGroup)
            {
                barCanvasGroup.interactable = false;
                barCanvasGroup.blocksRaycasts = false;
            }

            if (clearSelectionOnHide)
                ClearSelection();
        }

        public void ToggleBuildBar()
        {
            if (IsBuildBarVisible) HideBuildBar();
            else ShowBuildBar();
        }

        public void SetBlueprints(IEnumerable<PlatformBlueprint> list)
        {
            blueprints = new List<PlatformBlueprint>(list ?? Array.Empty<PlatformBlueprint>());
            PopulateToolbar();
        }

        public PlatformBlueprint GetSelectedBlueprint()
        {
            return (_selectedIndex >= 0 && _selectedIndex < blueprints.Count) ? blueprints[_selectedIndex] : null;
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
            if (!content || !platformIconPrefab) return;

            ClearChildren(content);
            _buttons.Clear();
            _selectedFrames.Clear();
            _selectedIndex = -1;

            for (int i = 0; i < blueprints.Count; i++)
            {
                var bp = blueprints[i];
                var go = Instantiate(platformIconPrefab, content);

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
            var bp = (index >= 0 && index < blueprints.Count) ? blueprints[index] : null;
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

        private static void ClearChildren(RectTransform parent)
        {
            if (!parent) return;
            for (int i = parent.childCount - 1; i >= 0; i--)
                GameObject.Destroy(parent.GetChild(i).gameObject);
        }
    }
}
