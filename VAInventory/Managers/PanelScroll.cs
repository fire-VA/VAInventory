using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // Fire 10-07 (1.0.67): the Unified character panel never runs off the screen. When its content is taller than the
    // room under its top edge, the lower block (Misc + the bag sections) moves into a wheel-only ScrollRect sized so the
    // panel ends at the screen's bottom margin; the rails, preview and pouch row stay fixed above it. Built at runtime
    // around the existing cells, so EquipmentPanel's cell mapping, Button clicks and vanilla's drag item are untouched.
    internal static class PanelScroll
    {
        const float BottomMargin = 8f;
        // Fire 10-08 (1.0.71): "add a tiny bit of padding below the lowest grid squares on our player panel". The layout's
        // bottom padding (UnifiedScreen.PanelPadTopBottom) sits under the frame's border art, so the last row ran into the
        // frame. 1.0.72 (Fire, hand test 4: "too much padding … about the same relief as the top padding"): the frame's bottom
        // border is thicker than its top, and 20 left ~21 px against the top's ~15 px; [UI] PanelBottomGap (default 14) matches
        // them. Only as much as the room allows: the Explorer Bag fills the panel to within a few units, and the gap must never
        // make it scroll.
        static int BottomGap => ConfigManager.Instance?.configPanelBottomGap?.Value ?? 14;
        const float MinViewport = 140f;
        const float ScrollbarWidth = 6f;
        const float ScrollbarGap = 4f;
        const string ScrollName = "LowerScroll";
        const string BagSectionPrefix = "BagSection_";

        static RectTransform scrollRoot;
        static RectTransform movedMisc;
        static Transform miscParent;
        static int miscIndex;
        static readonly Vector3[] corners = new Vector3[4];
        // The panel's own size fitter is held off while Fit sets the frame height itself, and given back on Unwrap.
        static ContentSizeFitter heldFitter;
        static ContentSizeFitter.FitMode heldMode;

        // 1.0.89 (Fire: the second bag section bled off the bottom): a bag section built after the fit (a second bag equipped,
        // a section rebuilt) landed on the panel below the scroll view, past the frame. A section outside the scroll view while it
        // scrolls, or a section count that differs from the last fit, asks for a new fit, which wraps it in.
        static int fittedSections = -1;
        const float ViewportBottomInset = 24f;

        internal static bool NeedsRefit(RectTransform panel)
        {
            if (panel == null || !panel.gameObject.activeInHierarchy) return false;
            int loose = 0, inside = 0;
            foreach (Transform t in panel)
                if (t.gameObject.activeSelf && t.name.StartsWith(BagSectionPrefix)) loose++;
            var content = scrollRoot != null ? scrollRoot.GetComponent<WheelOnlyScrollRect>()?.content : null;
            if (content != null)
                foreach (Transform t in content)
                    if (t.gameObject.activeSelf && t.name.StartsWith(BagSectionPrefix)) inside++;
            bool refit = (scrollRoot != null && loose > 0) || loose + inside != fittedSections;
            if (refit)
                EquipmentPanel.PanelLog?.LogDebug($"panel fit: refit (bag sections {loose} outside + {inside} in the scroll view, last fit {fittedSections})");
            return refit;
        }

        // Puts Misc back in its column and the bag sections back on the panel, and drops the scroll view.
        internal static void Unwrap()
        {
            if (heldFitter != null) heldFitter.verticalFit = heldMode;
            heldFitter = null;
            if (movedMisc != null && miscParent != null)
            {
                movedMisc.SetParent(miscParent, false);
                movedMisc.SetSiblingIndex(Mathf.Min(miscIndex, miscParent.childCount - 1));
            }
            movedMisc = null;
            miscParent = null;
            if (scrollRoot == null) return;
            var content = scrollRoot.GetComponent<WheelOnlyScrollRect>()?.content;
            var panel = scrollRoot.parent;
            if (content != null && panel != null)
                foreach (var child in content.Cast<Transform>().ToList())
                    child.SetParent(panel, false);
            // Deactivated first: Destroy lands at end of frame, and this frame's layout rebuild must not count it.
            scrollRoot.gameObject.SetActive(false);
            Object.Destroy(scrollRoot.gameObject);
            scrollRoot = null;
        }

        // Sizes the panel to its content, or clamps it to the screen with the lower block scrolling.
        // Returns the evidence line; the caller logs it once per open (1.0.67 round: the frame was measured, not the content,
        // and the frame was never resized, so the panel kept running off the screen).
        internal static string Fit(RectTransform panel, RectTransform misc)
        {
            if (panel == null) return "panel fit: skipped (no panel)";
            Unwrap();
            var canvas = panel.GetComponentInParent<Canvas>()?.rootCanvas;
            if (canvas == null) return "panel fit: skipped (no canvas)";
            if (!panel.gameObject.activeInHierarchy) return "panel fit: skipped (panel inactive)";
            var canvasRect = (RectTransform)canvas.transform;

            var fitter = panel.GetComponent<ContentSizeFitter>();
            if (fitter != null)
            {
                heldFitter = fitter;
                heldMode = fitter.verticalFit;
                fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
            }
            var layout = panel.GetComponent<VerticalLayoutGroup>();
            if (layout != null) layout.padding.bottom = UnifiedScreen.PanelPadTopBottom;   // the base; the last fit's gap comes off
            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
            float content = PreferredHeight(panel);
            panel.GetWorldCorners(corners);
            float top = canvasRect.InverseTransformPoint(corners[1]).y;
            float bottom = canvasRect.InverseTransformPoint(corners[0]).y;
            float unit = panel.rect.height > 0f ? (top - bottom) / panel.rect.height : 1f;   // canvas units per panel unit (the Crafting fit scale)
            float screen = (top - (canvasRect.rect.yMin + BottomMargin)) / Mathf.Max(0.01f, unit);
            int gap = layout != null ? Mathf.Clamp(Mathf.FloorToInt(screen - content), 0, BottomGap) : 0;
            if (gap > 0)
            {
                layout.padding.bottom += gap;
                LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
                content = PreferredHeight(panel);
            }
            float viewport = 0f;
            WheelOnlyScrollRect scrollRect = null;

            // Fire 10-07 on 1.0.68: "Why is Misc pushed down so far?" Wrapping Misc took it out of the centre column (under the
            // pouch row) and put it below the rails, a dead band beside Shoulder/Pet/Bypass. Misc stays in its column; only the
            // bag sections scroll. Misc scrolls only when there is no bag section to take the overflow.
            var lower = new List<RectTransform>();
            lower.AddRange(panel.Cast<Transform>().Where(t => t.gameObject.activeSelf && t.name.StartsWith(BagSectionPrefix)).Cast<RectTransform>());
            fittedSections = lower.Count;
            if (lower.Count == 0 && misc != null && misc.gameObject.activeInHierarchy) lower.Add(misc);

            bool scroll = content > screen + 0.5f && lower.Count > 0;
            if (scroll)
            {
                scrollRect = Wrap(panel, lower);
                var le = scrollRoot.GetComponent<LayoutElement>();
                LayoutRebuilder.ForceRebuildLayoutImmediate(scrollRect.content);
                float lowerHeight = LayoutUtility.GetPreferredHeight(scrollRect.content);
                le.minWidth = le.preferredWidth = LayoutUtility.GetPreferredWidth(scrollRect.content) + ScrollbarWidth + ScrollbarGap;
                le.minHeight = le.preferredHeight = lowerHeight;
                LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
                // Misc left its column, so the fixed part may have shrunk: measure once with the full block, then cut it.
                viewport = Mathf.Clamp(lowerHeight - (PreferredHeight(panel) - screen), Mathf.Min(MinViewport, lowerHeight), lowerHeight);
                // 1.0.90 (round 33: the next bag section's header and cell tops drawn on the frame's bottom edge): the viewport
                // ended where the frame's art ends, so the clip line sat on the border. It now ends ViewportBottomInset above it.
                float cut = Mathf.Min(ViewportBottomInset, viewport - Mathf.Min(MinViewport, lowerHeight));
                viewport -= cut;
                if (layout != null) layout.padding.bottom += Mathf.RoundToInt(cut);   // the frame keeps its height; the room goes below the view
                le.minHeight = le.preferredHeight = viewport;
                LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
            }
            // The frame: hugs the content, or ends at the screen's bottom margin while the lower block scrolls.
            float frame = PreferredHeight(panel);
            if (scroll) frame = Mathf.Min(frame, screen);
            SetHeightKeepTop(panel, frame);
            LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
            if (scrollRect != null) scrollRect.verticalNormalizedPosition = 1f;
            // 1.0.90: where every bag section actually lives (round 33 asked for the parent path).
            foreach (var s in panel.GetComponentsInChildren<RectTransform>(true))
                if (s.name.StartsWith(BagSectionPrefix))
                    EquipmentPanel.PanelLog?.LogDebug($"panel fit: {s.name} under {(s.parent != null ? s.parent.name : "-")}{(s.parent != null && s.parent.parent != null ? " < " + s.parent.parent.name : "")}, active {s.gameObject.activeInHierarchy}");
            return $"panel fit: content={content:0} room={screen:0} canvasH={canvasRect.rect.height:0} scroll={(scroll ? "on" : "off")} "
                + $"viewport={viewport:0} frame={panel.rect.height:0} bottomGap={gap}";
        }

        // The layout's preferred height, or the rect when the panel has no layout to ask.
        static float PreferredHeight(RectTransform panel)
        {
            float preferred = LayoutUtility.GetPreferredHeight(panel);
            return preferred > 0f ? preferred : panel.rect.height;
        }

        // Resizes the panel without moving its top edge, whatever its pivot.
        static void SetHeightKeepTop(RectTransform panel, float height)
        {
            var parent = panel.parent;
            panel.GetWorldCorners(corners);
            Vector3 before = parent != null ? parent.InverseTransformPoint(corners[1]) : corners[1];
            panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            panel.GetWorldCorners(corners);
            Vector3 after = parent != null ? parent.InverseTransformPoint(corners[1]) : corners[1];
            panel.anchoredPosition += new Vector2(0f, before.y - after.y);
        }

        static WheelOnlyScrollRect Wrap(RectTransform panel, List<RectTransform> lower)
        {
            var first = lower[0];
            int insertAt = first.parent == panel ? first.GetSiblingIndex() : AncestorUnder(panel, first).GetSiblingIndex() + 1;

            scrollRoot = NewRect(panel, ScrollName);
            scrollRoot.SetSiblingIndex(insertAt);
            scrollRoot.gameObject.AddComponent<LayoutElement>();

            var viewport = NewRect(scrollRoot, "Viewport");
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = new Vector2(-(ScrollbarWidth + ScrollbarGap), 0f);
            var catcher = viewport.gameObject.AddComponent<Image>();   // the wheel works over the gaps between cells too
            catcher.color = Color.clear;
            catcher.raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();

            var content = NewRect(viewport, "Content");
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = Vector2.zero;
            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.spacing = panel.GetComponent<VerticalLayoutGroup>()?.spacing ?? 10f;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = vlg.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            foreach (var rect in lower)
            {
                if (rect.parent != panel)
                {
                    movedMisc = rect;
                    miscParent = rect.parent;
                    miscIndex = rect.GetSiblingIndex();
                }
                rect.SetParent(content, false);
            }

            var bar = NewRect(scrollRoot, "Scrollbar");
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = Vector2.one;
            bar.pivot = new Vector2(1f, 0.5f);
            bar.sizeDelta = new Vector2(ScrollbarWidth, 0f);
            var track = bar.gameObject.AddComponent<Image>();
            track.color = new Color(0.10f, 0.08f, 0.06f, 0.6f);
            var area = NewRect(bar, "Sliding Area");
            area.anchorMin = Vector2.zero;
            area.anchorMax = Vector2.one;
            area.sizeDelta = Vector2.zero;
            var handle = NewRect(area, "Handle");
            handle.anchorMin = Vector2.zero;
            handle.anchorMax = Vector2.one;
            handle.sizeDelta = Vector2.zero;
            var handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = new Color(0.93f, 0.78f, 0.37f, 0.85f);
            var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handleImage;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            var sr = scrollRoot.gameObject.AddComponent<WheelOnlyScrollRect>();
            sr.viewport = viewport;
            sr.content = content;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.inertia = false;
            sr.scrollSensitivity = 30f;
            sr.verticalScrollbar = scrollbar;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            return sr;
        }

        static Transform AncestorUnder(Transform panel, Transform child)
        {
            var t = child;
            while (t.parent != null && t.parent != panel) t = t.parent;
            return t;
        }

        static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }
    }

    // Scrolls by the mouse wheel and the scrollbar only: a left press or drag on a cell is the cell's (its Button and
    // vanilla's drag item), never the content's. While an item is on the cursor, hovering near the top or bottom edge
    // of the viewport scrolls toward it.
    internal sealed class WheelOnlyScrollRect : ScrollRect
    {
        const float EdgeBand = 40f;
        const float EdgeSpeed = 420f;   // panel units per second at the edge
        static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> DragItem =
            AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_dragItem");

        public override void OnInitializePotentialDrag(PointerEventData eventData) { }
        public override void OnBeginDrag(PointerEventData eventData) { }
        public override void OnDrag(PointerEventData eventData) { }
        public override void OnEndDrag(PointerEventData eventData) { }

        // Fire 10-08 (1.0.73): scrolled bag cells drew over Misc and the equipment rails. Vanilla's InventoryElement has a
        // faint "dropFocus" Image with maskable off, so the viewport's RectMask2D never clipped it. Every graphic in the
        // content is made maskable; checked again whenever the content's graphic count changes (grids rebuild cells).
        int maskedCount = -1;
        float nextMaskCheck;

        void MakeContentMaskable()
        {
            if (content == null || Time.unscaledTime < nextMaskCheck) return;
            nextMaskCheck = Time.unscaledTime + 0.5f;
            var graphics = content.GetComponentsInChildren<MaskableGraphic>(true);
            if (graphics.Length == maskedCount) return;
            maskedCount = graphics.Length;
            int fixedCount = 0;
            foreach (var g in graphics)
                if (!g.maskable) { g.maskable = true; fixedCount++; }
            if (fixedCount > 0) EquipmentPanel.PanelLog?.LogDebug($"panel scroll: {fixedCount} of {graphics.Length} graphic(s) made maskable");
        }

        // 1.0.91 (round 34: the second bag section's header and a row of cells drew past the frame although they sit under
        // Content < Viewport, so the RectMask2D did not clip them): a hard guard that does not rely on the mask. Every part of a
        // bag section (cells, header, info, rule, rail) that is not entirely inside the viewport is hidden through its own
        // CanvasGroup (alpha 0, no raycasts) and shown again as it scrolls in. The mask state and any nested Canvas under the
        // content are logged once.
        readonly List<RectTransform> parts = new List<RectTransform>();
        readonly Vector3[] vpCorners = new Vector3[4], partCorners = new Vector3[4];
        int partsFrom = -1;
        bool maskLogged, boundsLogged;
        readonly Vector3[] panelCorners = new Vector3[4];

        void HideOutsideViewport()
        {
            if (viewport == null || content == null) return;
            if (content.hierarchyCount != partsFrom)
            {
                partsFrom = content.hierarchyCount;
                parts.Clear();
                foreach (Transform section in content)
                    foreach (Transform part in section)
                        if (part is RectTransform rt) parts.Add(rt);
                if (!maskLogged)
                {
                    maskLogged = true;
                    var mask = viewport.GetComponent<RectMask2D>();
                    var canvases = content.GetComponentsInChildren<Canvas>(true);
                    EquipmentPanel.PanelLog?.LogDebug($"panel scroll: viewport RectMask2D {(mask != null ? (mask.enabled ? "on" : "off") : "missing")}, nested Canvas under content {canvases.Length}{(canvases.Length > 0 ? " (" + string.Join(", ", canvases.Select(c => c.name + (c.overrideSorting ? " overrideSorting" : ""))) + ")" : "")}, guarded parts {parts.Count}");
                }
            }
            viewport.GetWorldCorners(vpCorners);
            float top = vpCorners[1].y, bottom = vpCorners[0].y;
            // 1.0.92 (Fire's phone shot on 1.0.91: cells still below the frame): the viewport itself can reach past the panel's
            // frame, so a part must also lie inside the panel (the scroll view's parent), less its bottom padding.
            if (transform.parent is RectTransform panelRect)
            {
                panelRect.GetWorldCorners(panelCorners);
                float pad = UnifiedScreen.PanelPadTopBottom * panelRect.lossyScale.y;
                bottom = Mathf.Max(bottom, panelCorners[0].y + pad);
                top = Mathf.Min(top, panelCorners[1].y);
                if (!boundsLogged)
                {
                    boundsLogged = true;
                    EquipmentPanel.PanelLog?.LogDebug($"panel scroll: viewport bottom {vpCorners[0].y:0}, panel bottom {panelCorners[0].y:0} (+pad {pad:0}), clip bottom {bottom:0}");
                }
            }
            foreach (var part in parts)
            {
                if (part == null) continue;
                part.GetWorldCorners(partCorners);
                bool inside = partCorners[0].y >= bottom - 0.5f && partCorners[1].y <= top + 0.5f;
                var group = part.GetComponent<CanvasGroup>();
                if (group == null) { group = part.gameObject.AddComponent<CanvasGroup>(); group.alpha = -1f; }   // forces the first write below
                float alpha = inside ? 1f : 0f;
                if (group.alpha != alpha) { group.alpha = alpha; group.blocksRaycasts = inside; group.interactable = inside; }
            }
        }

        // 1.0.93 (round 36: cells peeked below the frame for a moment after the panel opened, clean later): LateUpdate runs
        // BEFORE the canvas lays the sections out, so on the open frame and after every refit the guard read stale positions
        // and a freshly added CanvasGroup started visible. The guard now also runs in Canvas.willRenderCanvases, after layout and
        // right before drawing, every frame; new parts start hidden until measured.
        protected override void OnEnable()
        {
            base.OnEnable();
            Canvas.willRenderCanvases += GuardBeforeRender;
        }

        protected override void OnDisable()
        {
            Canvas.willRenderCanvases -= GuardBeforeRender;
            base.OnDisable();
        }

        void GuardBeforeRender()
        {
            if (this == null || !isActiveAndEnabled) return;
            HideOutsideViewport();
        }

        protected override void LateUpdate()
        {
            base.LateUpdate();
            MakeContentMaskable();
            HideOutsideViewport();
            var gui = InventoryGui.instance;
            if (gui == null || viewport == null || content == null || DragItem(gui) == null) return;
            float overflow = content.rect.height - viewport.rect.height;
            if (overflow <= 0f) return;
            var canvas = viewport.GetComponentInParent<Canvas>()?.rootCanvas;
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, Input.mousePosition, cam, out var p)) return;
            var r = viewport.rect;
            if (p.x < r.xMin || p.x > r.xMax || p.y < r.yMin - EdgeBand || p.y > r.yMax + EdgeBand) return;
            float dir = p.y > r.yMax - EdgeBand ? 1f : p.y < r.yMin + EdgeBand ? -1f : 0f;
            if (dir == 0f) return;
            verticalNormalizedPosition = Mathf.Clamp01(verticalNormalizedPosition + dir * EdgeSpeed * Time.unscaledDeltaTime / overflow);
        }
    }
}
