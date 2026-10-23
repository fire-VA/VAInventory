using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// Under Panels, crafting's header is a button row then the CRAFTING title, and the tab row moves
    /// down to clear it (InventoryTabs.PanelsDrop). Everything BELOW the tabs (recipe list, description, requirements, Craft button, the tab
    /// views) moves down by the same amount, and the panel's background layers grow downward to hold it, so nothing starts under the
    /// tabs. Crafting's own rect is not resized (vanilla's; Core measures what draws). Leaving Panels puts every moved child back.
    /// Moved = each direct child of m_crafting except the tab row's parent, the background / skin layers, our own Panels header pieces and
    /// vanilla's hidden title / repair panel.
    /// </summary>
    internal static class PanelsCraftShift
    {
        static readonly HashSet<string> Keep = new HashSet<string> { "Bkg", "VAInventorySkinUnderlay", "VAInventorySkinInlay", "topic", "RepairSimple", "RepairButton", "TitlePanel" };   // 1.0.217: TitlePanel stays above the tab row (Fire: a line across the tabs)
        static readonly HashSet<string> Grow = new HashSet<string> { "Bkg", "VAInventorySkinUnderlay", "VAInventorySkinInlay" };
        static readonly Dictionary<RectTransform, Vector2> s_moved = new Dictionary<RectTransform, Vector2>();   // child -> its original anchoredPosition
        static readonly Dictionary<RectTransform, Vector2> s_grown = new Dictionary<RectTransform, Vector2>();   // layer -> its original offsetMin
        static RectTransform s_crafting;
        static float s_applied;

        // The gap under the tab row is DOUBLED to 16 px,
        // recipe list and description column alike (both are measured)
        const float TabGap = 16f;

        static bool s_zeroLogged, s_wasVis, s_hiddenLogged, s_openPending;   // 1.0.255/256/257
        static int s_openN;
        static int s_openFrame = -100;
        static float s_base;   // The header drop the current move was built from (PanelsDrop)

        internal static void Tick(InventoryGui gui, bool on)
        {
            var crafting = gui != null ? gui.m_crafting : null;
            DarkenOff(crafting, on);   // 1.0.187
            // An inventory OPEN (on false -> true; crafting itself stays active while the inventory is hidden, so 255 never re-armed)
            if (on && !s_wasVis) { s_openFrame = Time.frameCount; s_openN++; s_openPending = true; }
            s_wasVis = on;
            if (on) HeaderCheck(gui, crafting);   // 1.0.253 (replaces 1.0.244's TitleLineOffTabs)
            float drop = on && crafting != null ? InventoryTabs.PanelsDrop : 0f;
            // 
            // InventoryTabs.PanelsDrop reads 0 for the first frame(s) of an open (the tab row not measured yet), so this Tick saw a
            // "changed" drop, Restore()d the content to its undropped spot and re-applied a frame later. While Panels is ON, a momentary
            // 0 is NOT a layout change: the last drop is kept. Only leaving Panels (on = false) restores.
            // 1.0.256 (R-1020UI4, ' session: "crafting drop change 11 -> 0" logged on every inventory CLOSE, then "0 -> 11" one frame
            // AFTER every reopen = the first visible frame drew the content undropped): 'on' is Panels AND the inventory visible, so closing
            // the inventory counted as leaving Panels and restored the content. While Panels mode itself is on, a closed inventory keeps the
            // drop as it is - the content is already in place on the first frame of the next open. Only leaving Panels mode restores.
            if (!on && UnifiedScreen.PanelsMode && crafting != null && s_base > 0.5f && ReferenceEquals(crafting, s_crafting)) return;
            if (on && crafting != null && drop <= 0.5f && s_base > 0.5f && ReferenceEquals(crafting, s_crafting))
            {
                if (!s_zeroLogged) { s_zeroLogged = true; EquipmentPanel.PanelLog?.LogDebug($"panels: crafting drop read 0 while Panels is on (tab row not measured yet); kept the drop of {s_base:0} px - no restore / re-apply"); }
                drop = s_base;
            }
            if (!ReferenceEquals(crafting, s_crafting)) { s_moved.Clear(); s_grown.Clear(); s_crafting = crafting; s_applied = 0f; s_base = 0f; }
            if (crafting == null) return;
            if (Mathf.Approximately(drop, s_base))
            {
                // Re-measure 2 frames after the open and after every UpdateRecipeList (the rows / layout settle after Apply)
                if (on && drop > 0.5f && s_recheckFrame > 0 && Time.frameCount >= s_recheckFrame) { s_recheckFrame = 0; Recheck(gui, crafting, s_recheckWhy); }
                if (on && drop > 0.5f) { HoldDrop(); PinGap(gui, crafting); }   // 1.0.254 + 1.0.259
                return;
            }
            EquipmentPanel.PanelLog?.LogDebug($"panels: crafting drop change {s_base:0} -> {drop:0} at frame {Time.frameCount} (+{Time.frameCount - s_openFrame} since open): restore + re-apply");   // 1.0.255
            Restore();
            s_applied = drop > 0.5f ? Apply(gui, crafting, drop) : 0f;
            if (drop > 0.5f) PinGap(gui, crafting);   // The full gap in the SAME frame as the first apply
            s_base = drop;
            if (drop > 0.5f) RequestRecheck("2 frames after the open", 2);
        }

        // 1.0.254 (R-1020UI2: the OVERLAP CHECK alternated FAIL/PASS on EVERY open - RecipeList / Decription at y 101 (undropped) for the
        // first frames, then 128 once the 2-frame re-check moved them; something resets their anchoredPosition on each open): every frame
        // while dropped, each moved child is held at its original spot minus the applied drop (and each grown layer's offsetMin), so the
        // content is at its final y on the first visible frame. Cheap: ~10 Vector2 compares; logged the first time it has to correct.
        // 
        // the content's RECT tops (RecipeList, Decription - not the drawn graphics, which a re-list / the FATI tab changes) are pinned to
        // exactly the tab row's bottom + TabGap every frame the crafting is up, in the same frame as the first apply. One correction moves
        // every dropped child together (MoveBy, s_applied follows), so HoldDrop and PinGap agree.
        static readonly string[] PinNames = { "RecipeList", "Decription" };
        static float s_expectedTop = float.NaN;   // crafting-local y of the content tops (for the proof line)
        static string s_pinLog;
        static bool TabBottom(InventoryGui gui, RectTransform crafting, out float bottom)
        {
            bottom = float.MaxValue;
            var tabRow = gui.m_tabCraft != null ? gui.m_tabCraft.transform.parent as RectTransform : null;
            if (tabRow == null) return false;
            foreach (Transform t in tabRow)
            {
                if (!t.gameObject.activeInHierarchy || t.GetComponent<Button>() == null) continue;
                ((RectTransform)t).GetWorldCorners(s_c4);
                bottom = Mathf.Min(bottom, crafting.InverseTransformPoint(s_c4[0]).y);
            }
            return bottom != float.MaxValue;
        }
        static bool ContentTop(RectTransform crafting, out float top)
        {
            top = float.MinValue;
            foreach (var n in PinNames)
            {
                var rt = crafting.Find(n) as RectTransform;
                if (rt == null || !rt.gameObject.activeSelf) continue;
                rt.GetWorldCorners(s_c4);
                top = Mathf.Max(top, crafting.InverseTransformPoint(s_c4[1]).y);
            }
            return top != float.MinValue;
        }
        static void PinGap(InventoryGui gui, RectTransform crafting)
        {
            if (crafting == null || s_moved.Count == 0 || !TabBottom(gui, crafting, out float tabBottom)) return;
            s_expectedTop = tabBottom - TabGap;
            if (!ContentTop(crafting, out float top)) return;
            float delta = top - s_expectedTop;   // > 0: content too high
            if (Mathf.Abs(delta) <= 0.5f) return;
            MoveBy(delta); s_applied += delta;
            string log = $"panels: crafting content pinned to {TabGap:0} px under the tabs (moved {delta:0.#} px; tab bottom {tabBottom:0}, content top now {s_expectedTop:0}; total drop {s_applied:0})";
            if (log != s_pinLog) { s_pinLog = log; EquipmentPanel.PanelLog?.LogDebug(log); }
        }

        static bool s_holdLogged;
        static void HoldDrop()
        {
            int fixedN = 0;
            foreach (var kv in s_moved)
            {
                if (kv.Key == null) continue;
                var want = kv.Value - new Vector2(0f, s_applied);
                if ((kv.Key.anchoredPosition - want).sqrMagnitude > 0.25f) { kv.Key.anchoredPosition = want; fixedN++; }
            }
            foreach (var kv in s_grown)
            {
                if (kv.Key == null) continue;
                var want = kv.Value - new Vector2(0f, s_applied);
                if ((kv.Key.offsetMin - want).sqrMagnitude > 0.25f) { kv.Key.offsetMin = want; fixedN++; }
            }
            if (fixedN > 0 && !s_holdLogged) { s_holdLogged = true; EquipmentPanel.PanelLog?.LogDebug($"panels: crafting content was reset to its undropped spot ({fixedN} part(s)); held at the drop of {s_applied:0} px every frame from now on (frame {Time.frameCount}, +{Time.frameCount - s_openFrame} since open)"); }
        }

        static int s_recheckFrame; static string s_recheckWhy;
        internal static void RequestRecheck(string why, int frames) { s_recheckFrame = Time.frameCount + frames; s_recheckWhy = why; }

        static void Recheck(InventoryGui gui, RectTransform crafting, string why)
        {
            Canvas.ForceUpdateCanvases();
            float over = Measure(gui, crafting, out string topName, out float tabY, out float topY);
            float extra = !float.IsNaN(over) && over > -TabGap ? over + TabGap : 0f;
            if (extra > 0.01f) { MoveBy(extra); s_applied += extra; Canvas.ForceUpdateCanvases(); }
            float after = extra > 0.01f ? Measure(gui, crafting, out _, out _, out _) : over;
            EquipmentPanel.PanelLog?.LogDebug($"panels: crafting re-check ({why}): tab bottom {tabY:0}, top content '{topName ?? "none"}' at {topY:0}, overlap before {(float.IsNaN(over) ? "n/a" : over.ToString("0.0"))}, moved {extra:0} more (total drop {s_applied:0}); overlap after = {(float.IsNaN(after) ? "n/a" : after.ToString("0.0"))} (must be <= -{TabGap:0}; gap {(float.IsNaN(over) ? "n/a" : (-over).ToString("0"))} -> {(float.IsNaN(after) ? "n/a" : (-after).ToString("0"))} px)");
        }

        [HarmonyLib.HarmonyPatch(typeof(InventoryGui), "UpdateRecipeList")]
        static class RecheckAfterRecipeList
        {
            static void Postfix() { if (UnifiedScreen.PanelsMode && s_base > 0.5f) RequestRecheck("after UpdateRecipeList", 1); }
        }

        // 1.0.216 (the 1.0.210 / 1.0.214 overlap check NEVER RAN - it waited for a later tick that never came; "crafting content
        // moved down 19" x4 and not one check line): deterministic, inside Apply. Move by the header drop, force the canvases, MEASURE what
        // is drawn - the tab row's lowest visible graphic vs the highest visible (mask-clipped) graphic of everything moved - and move again
        // by the overlap + 8 px in the SAME call; re-measure and log both every apply. Returns the total drop.
        static float Measure(InventoryGui gui, RectTransform crafting, out string topName, out float tabBottomY, out float contentTopY)
        {
            topName = null; tabBottomY = 0f; contentTopY = 0f;
            var tabRow = gui.m_tabCraft != null ? gui.m_tabCraft.transform.parent as RectTransform : null;
            if (tabRow == null || s_moved.Count == 0) return float.NaN;
            float tabBottom = float.MaxValue, contentTop = float.MinValue;
            var c4 = new Vector3[4];
            foreach (var g in tabRow.GetComponentsInChildren<Graphic>(false))
            {
                if (!g.isActiveAndEnabled || g.canvasRenderer.GetInheritedAlpha() < 0.02f) continue;
                g.rectTransform.GetWorldCorners(c4); tabBottom = Mathf.Min(tabBottom, c4[0].y);
            }
            foreach (var rt in s_moved.Keys)
            {
                if (rt == null || !rt.gameObject.activeInHierarchy) continue;
                foreach (var g in rt.GetComponentsInChildren<Graphic>(false))
                {
                    if (!g.isActiveAndEnabled || g.color.a < 0.02f || g.canvasRenderer.GetInheritedAlpha() < 0.02f) continue;
                    var mask = g.GetComponentInParent<RectMask2D>();
                    g.rectTransform.GetWorldCorners(c4);
                    float top = c4[1].y;
                    if (mask != null) { var m4 = new Vector3[4]; mask.rectTransform.GetWorldCorners(m4); top = Mathf.Min(top, m4[1].y); }   // clipped by its mask
                    if (top > contentTop) { contentTop = top; topName = rt.name + "/" + g.name; }
                }
            }
            // Every moved
            // ScrollRect's VIEWPORT top counts too - the visible list starts there whether its rows exist yet or not
            foreach (var rt in s_moved.Keys)
            {
                if (rt == null || !rt.gameObject.activeInHierarchy) continue;
                foreach (var sr in rt.GetComponentsInChildren<ScrollRect>(false))
                {
                    var vp = sr.viewport != null ? sr.viewport : sr.transform as RectTransform;
                    if (vp == null) continue;
                    vp.GetWorldCorners(c4);
                    if (c4[1].y > contentTop) { contentTop = c4[1].y; topName = rt.name + "/" + vp.name + " (viewport)"; }
                }
            }
            if (tabBottom == float.MaxValue || contentTop == float.MinValue) return float.NaN;
            float scale = Mathf.Max(1e-5f, crafting.lossyScale.y);
            var local = crafting.InverseTransformPoint(new Vector3(0f, tabBottom, 0f)); tabBottomY = local.y;
            local = crafting.InverseTransformPoint(new Vector3(0f, contentTop, 0f)); contentTopY = local.y;
            return (contentTop - tabBottom) / scale;   // > 0: content drawn above the tabs' bottom (overlap)
        }

        static void MoveBy(float dy)
        {
            foreach (var rt in s_moved.Keys) if (rt != null) rt.anchoredPosition = rt.anchoredPosition - new Vector2(0f, dy);
            foreach (var rt in s_grown.Keys) if (rt != null) rt.offsetMin = rt.offsetMin - new Vector2(0f, dy);
        }

        static float Apply(InventoryGui gui, RectTransform crafting, float drop)
        {
            Transform tabRow = gui.m_tabCraft != null ? gui.m_tabCraft.transform.parent : null;
            var names = new List<string>();
            foreach (Transform child in crafting)
            {
                var rt = child as RectTransform;
                if (rt == null) continue;
                if (Grow.Contains(rt.name)) { s_grown[rt] = rt.offsetMin; rt.offsetMin = rt.offsetMin - new Vector2(0f, drop); continue; }
                if (rt.name == Braid)   // Hidden, not moved (it drew across the tab row)
                {
                    s_braid = rt.GetComponent<CanvasGroup>() ?? rt.gameObject.AddComponent<CanvasGroup>();
                    s_braid.alpha = 0f; s_braid.blocksRaycasts = false;
                    names.Add(rt.name + " (hidden)");
                    continue;
                }
                if (Keep.Contains(rt.name) || rt.name.StartsWith("VAInventoryPanels", System.StringComparison.Ordinal) || rt.name.StartsWith("VAInventoryGrip", System.StringComparison.Ordinal)) continue;
                if (tabRow != null && (ReferenceEquals(rt, tabRow) || tabRow.IsChildOf(rt))) continue;
                if (gui.m_tabCraft != null && gui.m_tabCraft.transform.IsChildOf(rt)) continue;
                s_moved[rt] = rt.anchoredPosition;
                rt.anchoredPosition = rt.anchoredPosition - new Vector2(0f, drop);
                names.Add(rt.name);
            }
            Canvas.ForceUpdateCanvases();
            float over = Measure(gui, crafting, out string topName, out float tabY, out float topY);
            float extra = !float.IsNaN(over) && over > -TabGap ? over + TabGap : 0f;
            if (extra > 0.01f) { MoveBy(extra); Canvas.ForceUpdateCanvases(); }
            float after = extra > 0.01f ? Measure(gui, crafting, out _, out _, out _) : over;
            EquipmentPanel.PanelLog?.LogDebug($"panels: crafting drop = {drop + extra:0} (header {drop:0} + measured {extra:0}; tab bottom {tabY:0}, top content '{topName ?? "none"}' at {topY:0}, overlap before {(float.IsNaN(over) ? "n/a" : over.ToString("0.0"))}); overlap after = {(float.IsNaN(after) ? "n/a" : after.ToString("0.0"))} (must be <= -{TabGap:0}; gap {(float.IsNaN(over) ? "n/a" : (-over).ToString("0"))} -> {(float.IsNaN(after) ? "n/a" : (-after).ToString("0"))} px); moved {names.Count}: {string.Join(", ", names.ToArray())}; background grown {s_grown.Count} layer(s)");
            FixTabRow(gui, crafting);
            return drop + extra;
        }

        // Every tab label fills its tab and is centred, never
        // clipped; any THIN graphic (<= 6 px tall, wider than 40) outside the tab row that crosses the row's rect is switched off (logged by
        // name, back on Restore)
        static readonly List<Graphic> s_lineOff = new List<Graphic>();
        // 2 dumps per session, each Graphic overlapping the row's
        // rect with its path, y range relative to the row (0 = row bottom), sprite, image type, colour, nearest RectMask2D and sibling index
        static int s_tabDumps;
        static void DumpTabRow(RectTransform tabRow, RectTransform crafting)
        {
            if (s_tabDumps >= 2) return;
            s_tabDumps++;
            var r4 = new Vector3[4]; tabRow.GetWorldCorners(r4);
            float y0 = r4[0].y, y1 = r4[1].y, x0 = r4[0].x, x1 = r4[2].x, sc = Mathf.Max(1e-5f, crafting.lossyScale.y);
            var sb = new System.Text.StringBuilder($"panels: crafting tab row DUMP #{s_tabDumps} (row h {(y1 - y0) / sc:0}): ");
            var g4 = new Vector3[4];
            foreach (var g in crafting.GetComponentsInChildren<Graphic>(false))
            {
                if (g == null || !g.isActiveAndEnabled) continue;
                g.rectTransform.GetWorldCorners(g4);
                if (g4[1].y < y0 || g4[0].y > y1 || g4[2].x < x0 || g4[0].x > x1) continue;
                var img = g as Image; var mask = g.GetComponentInParent<RectMask2D>();
                string path = g.name; for (var p = g.transform.parent; p != null && p != crafting; p = p.parent) path = p.name + "/" + path;
                sb.Append($"[{path} y {(g4[0].y - y0) / sc:0}..{(g4[1].y - y0) / sc:0} {g.GetType().Name}{(img != null ? $" sprite {(img.sprite != null ? img.sprite.name : "none")} {img.type}" : "")} col {g.color.r:0.00},{g.color.g:0.00},{g.color.b:0.00},{g.color.a:0.00} a{g.canvasRenderer.GetInheritedAlpha():0.00}{(mask != null ? " mask " + mask.name : "")} sib {g.transform.GetSiblingIndex()}] ");
            }
            EquipmentPanel.PanelLog?.LogDebug(sb.ToString());
        }

        // 1.0.244 (crafting_tab_line_through: a horizontal line through Craft / Upgrade / FATI; the 233/240 checks ran at Apply,
        // before the tabs sat in their final row): the culprit is OUR title hairline (PanelsChrome "VAInventoryPanelsTitleLine_NoKitSkin",
        // 1 px, crafting width - 48, a fixed 79 px under crafting's top, added AFTER the tab row so it draws over it) - the Panels tab row
        // sits ~80 px under the top. Every frame (cheap: two corner reads) the hairline is kept 5 px ABOVE the tab row's drawn top.
        const string TitleLine = "VAInventoryPanelsTitleLine_NoKitSkin";
        static RectTransform s_titleLine; static string s_titleLineLog;
        static readonly Vector3[] s_c4 = new Vector3[4];
        // 1.0.253 (fati_crafting_overlap_a/b: the CRAFTING title struck through, a line through the tabs, the FATI category row
        // over the tabs, the icon bar on the title): twice a second while crafting shows under Panels -
        //  (1) our title hairline is OFF;
        //  (2) any THIN graphic (<= 6 px tall, > 40 wide) crossing the tab row or the title, that isn't a tab/title itself, is switched off
        //      and named (1.0.233's pass ran once at Apply, before the row settled);
        //  (3) the TEAM RULE automated overlap check: the header's top-level elements (crafting's direct children that draw in the top
        //      band, by their drawn bounds) are tested pairwise; every overlapping pair is logged by name + px once per change.
        static float s_nextHeader; static string s_overlapLog, s_thinLog;
        static readonly List<string> s_thinOff = new List<string>();
        static void HeaderCheck(InventoryGui gui, RectTransform crafting)
        {
            if (!crafting.gameObject.activeInHierarchy) return;
            // A frame where crafting is not DRAWN (a parent CanvasGroup at alpha 0 - e.g. 1.0.243's first-open seeding hide) is not
            // checked: nothing on screen can overlap (logged once per hide)
            float alpha = 1f; foreach (var cg in crafting.GetComponentsInParent<CanvasGroup>()) alpha *= cg.alpha;
            if (alpha < 0.02f) { if (!s_hiddenLogged) { s_hiddenLogged = true; EquipmentPanel.PanelLog?.LogDebug($"panels: crafting header check skipped at frame {Time.frameCount}: crafting is not drawn (parent alpha 0)"); } return; }
            s_hiddenLogged = false;
            int since = Time.frameCount - s_openFrame;   // The first 4 frames of every open are checked EVERY frame (proof the content is in place before it draws)
            if (since > 3 && Time.unscaledTime < s_nextHeader) return;
            s_nextHeader = Time.unscaledTime + 0.5f;
            if (s_titleLine == null || s_titleLine.parent != crafting) s_titleLine = crafting.Find(TitleLine) as RectTransform;
            if (s_titleLine != null && s_titleLine.gameObject.activeSelf) { s_titleLine.gameObject.SetActive(false); EquipmentPanel.PanelLog?.LogDebug("panels: crafting title hairline OFF (it struck through the title / tabs)"); }
            var tabRow = gui.m_tabCraft != null ? gui.m_tabCraft.transform.parent as RectTransform : null;
            float sc = Mathf.Max(1e-5f, crafting.lossyScale.y);
            // the header band: from crafting's top down to the tab row's bottom + 60 (the FATI category row sits there)
            crafting.GetWorldCorners(s_c4); float top = s_c4[1].y;
            float bandBottom = top - 260f * sc;
            if (tabRow != null) { tabRow.GetWorldCorners(s_c4); bandBottom = Mathf.Min(bandBottom, s_c4[0].y - 60f * sc); }
            // per direct child: the drawn bounds inside the band
            var boxes = new List<(string name, Rect r)>();
            var thin = new List<string>();
            foreach (Transform child in crafting)
            {
                if (!child.gameObject.activeInHierarchy || Grow.Contains(child.name) || child.name == "Darken") continue;
                bool any = false; float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
                foreach (var g in child.GetComponentsInChildren<Graphic>(false))
                {
                    if (g == null || !g.isActiveAndEnabled || g.color.a < 0.02f || g.canvasRenderer.GetInheritedAlpha() < 0.02f) continue;
                    g.rectTransform.GetWorldCorners(s_c4);
                    if (s_c4[0].y > top || s_c4[1].y < bandBottom) continue;
                    float h = (s_c4[1].y - s_c4[0].y) / sc, w = (s_c4[2].x - s_c4[0].x) / sc;
                    if (h <= 6f && w > 40f && !(g is TMPro.TMP_Text))
                    {
                        g.enabled = false; string n = child.name + "/" + g.name; thin.Add(n); s_lineOff.Add(g);
                        continue;
                    }
                    if (!any) { x0 = s_c4[0].x; y0 = s_c4[0].y; x1 = s_c4[2].x; y1 = s_c4[1].y; any = true; }
                    else { x0 = Mathf.Min(x0, s_c4[0].x); y0 = Mathf.Min(y0, s_c4[0].y); x1 = Mathf.Max(x1, s_c4[2].x); y1 = Mathf.Max(y1, s_c4[1].y); }
                }
                if (any) boxes.Add((child.name, Rect.MinMaxRect(x0, y0, x1, y1)));
            }
            if (thin.Count > 0) { string t = string.Join(", ", thin.ToArray()); if (t != s_thinLog) { s_thinLog = t; EquipmentPanel.PanelLog?.LogDebug($"panels: crafting header thin lines switched off: {t}"); } }
            var over = new List<string>();
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var a = boxes[i].r; var b = boxes[j].r;
                    float ox = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin), oy = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
                    if (ox > 1f * sc && oy > 1f * sc) over.Add($"{boxes[i].name} x {boxes[j].name} ({ox / sc:0}x{oy / sc:0} px)");
                }
            string log = over.Count == 0 ? "none" : string.Join("; ", over.ToArray());
            // 1.0.257 (TEAM RULE: a check that compared nothing FAILs; "no line" never stands for PASS): fewer than 2 drawn header elements =
            // FAIL "compared nothing"; the FIRST drawn check of every inventory open is logged unconditionally as "open #N"
            bool nothing = boxes.Count < 2;
            if (nothing) log = "compared nothing (" + boxes.Count + " element(s) drawn in the header band)";
            // The gap must be IDENTICAL on every open - the content rect top vs the pinned expected (tab bottom - TabGap)
            string gap = "";
            if (!float.IsNaN(s_expectedTop) && ContentTop(crafting, out float ctop))
            {
                float off = (s_expectedTop - ctop) / 1f;
                gap = Mathf.Abs(off) <= 1f ? $"; gap {TabGap:0} px OK" : $"; GAP WRONG: content top {ctop:0} vs expected {s_expectedTop:0} ({off:0.#} px)";
                if (Mathf.Abs(off) > 1f && !nothing) { nothing = true; log = gap.Substring(2); }
            }
            bool forced = s_openPending;
            if (forced || log != s_overlapLog)
            {
                s_overlapLog = log; s_openPending = false;
                string list = string.Join(", ", boxes.ConvertAll(bx => $"{bx.name} y {(top - bx.r.yMax) / sc:0}..{(top - bx.r.yMin) / sc:0}").ToArray());
                EquipmentPanel.PanelLog?.LogDebug($"panels: crafting header OVERLAP CHECK{(forced ? " open #" + s_openN : "")} (frame {Time.frameCount}, +{since} since open, drawn alpha {alpha:0.##}): {(nothing ? "FAIL " + log : over.Count == 0 ? "PASS, no overlaps" + gap : "FAIL " + over.Count + ": " + log)} | elements (px from crafting top): {list}");
            }
        }

        static void TitleLineOffTabs(InventoryGui gui, RectTransform crafting)
        {
            if (s_titleLine == null || s_titleLine.parent != crafting) s_titleLine = crafting.Find(TitleLine) as RectTransform;
            var tabRow = gui.m_tabCraft != null ? gui.m_tabCraft.transform.parent as RectTransform : null;
            if (s_titleLine == null || tabRow == null || !s_titleLine.gameObject.activeInHierarchy) return;
            float top = float.MinValue, bottom = float.MaxValue;
            foreach (Transform tab in tabRow)
            {
                if (!tab.gameObject.activeInHierarchy || tab.GetComponent<Button>() == null) continue;
                ((RectTransform)tab).GetWorldCorners(s_c4);
                top = Mathf.Max(top, crafting.InverseTransformPoint(s_c4[1]).y); bottom = Mathf.Min(bottom, crafting.InverseTransformPoint(s_c4[0]).y);
            }
            if (top == float.MinValue) return;
            s_titleLine.GetWorldCorners(s_c4);
            float lineY = crafting.InverseTransformPoint(s_c4[1]).y;
            if (lineY < bottom - 0.5f || lineY > top + 4.5f) return;   // clear of the tabs
            float dy = top + 5f - lineY;
            s_titleLine.anchoredPosition += new Vector2(0f, dy);
            string log = $"panels: crafting title hairline was ON the tab row (line y {lineY:0}, tabs {bottom:0}..{top:0}); moved up {dy:0} to 5 px above the tabs";
            if (log != s_titleLineLog) { s_titleLineLog = log; EquipmentPanel.PanelLog?.LogDebug(log); }
        }

        static void FixTabRow(InventoryGui gui, RectTransform crafting)
        {
            var tabRow = gui.m_tabCraft != null ? gui.m_tabCraft.transform.parent as RectTransform : null;
            if (tabRow == null) return;
            int labels = 0;
            foreach (var tx in tabRow.GetComponentsInChildren<TMPro.TMP_Text>(false))
            {
                var rt = tx.rectTransform; var tab = rt.parent as RectTransform;
                if (tab == null || tab == tabRow) continue;
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero; rt.pivot = new Vector2(0.5f, 0.5f);
                tx.alignment = TMPro.TextAlignmentOptions.Center; tx.overflowMode = TMPro.TextOverflowModes.Ellipsis; tx.margin = Vector4.zero;
                // 1.0.240 (R-1019A shot: the LOWER half of 'Craft' / 'Upgrade' / 'FATI' cut off by a crisp horizontal edge; 233's thin-line
                // check found none): the label draws LAST in its tab (above any kit pill / frame / underline image) and is never clipped by a mask
                rt.SetAsLastSibling();
                tx.maskable = false;
                labels++;
            }
            DumpTabRow(tabRow, crafting);
            Canvas.ForceUpdateCanvases();
            var r4 = new Vector3[4]; var g4 = new Vector3[4];
            float y0 = float.MaxValue, y1 = float.MinValue, x0 = float.MaxValue, x1 = float.MinValue;
            foreach (Transform tab in tabRow) { var t = tab as RectTransform; if (t == null || !t.gameObject.activeInHierarchy) continue; t.GetWorldCorners(r4); y0 = Mathf.Min(y0, r4[0].y); y1 = Mathf.Max(y1, r4[1].y); x0 = Mathf.Min(x0, r4[0].x); x1 = Mathf.Max(x1, r4[2].x); }
            var off = new List<string>();
            float scale = Mathf.Max(1e-5f, crafting.lossyScale.y);
            if (y0 < y1)
                foreach (var g in crafting.GetComponentsInChildren<Graphic>(false))
                {
                    if (g == null || !g.isActiveAndEnabled || g.transform.IsChildOf(tabRow) || g.canvasRenderer.GetInheritedAlpha() < 0.02f) continue;
                    g.rectTransform.GetWorldCorners(g4);
                    float h = (g4[1].y - g4[0].y) / scale, w = (g4[2].x - g4[0].x) / scale;
                    if (h > 6f || w < 40f) continue;
                    if (g4[1].y < y0 || g4[0].y > y1 || g4[2].x < x0 || g4[0].x > x1) continue;
                    g.enabled = false; s_lineOff.Add(g); off.Add(g.transform.parent != null ? g.transform.parent.name + "/" + g.name : g.name);
                }
            EquipmentPanel.PanelLog?.LogDebug($"panels: crafting tab row: {labels} label(s) fitted + centred; lines crossing the row switched off: {(off.Count == 0 ? "none" : string.Join(", ", off.ToArray()))}");
        }

        // 1.0.187 (R-1015J probe: the BLACK BOX left of the crafting panel was Crafting/Darken, a 670x750 near-black kit pill, wider than and
        // offset from Crafting/Bkg 590x681; with crafting flush right it poked out on the left). Under Panels the kit Bkg is the panel, so
        // vanilla's Darken shade is switched off; leaving Panels switches it back on.
        static GameObject s_darken;
        static bool s_darkenHidden, s_darkenLogged;

        static void DarkenOff(RectTransform crafting, bool on)
        {
            if (crafting == null) return;
            if (s_darken == null || s_darken.transform.parent != crafting) { var t = crafting.Find("Darken"); s_darken = t != null ? t.gameObject : null; s_darkenHidden = false; }
            VanillaRepairOff(crafting, on);   // 1.0.192
            if (s_darken == null) return;
            if (on && s_darken.activeSelf)
            {
                s_darken.SetActive(false); s_darkenHidden = true;
                if (!s_darkenLogged) { s_darkenLogged = true; EquipmentPanel.PanelLog?.LogDebug("panels: crafting's vanilla 'Darken' shade off under Panels (it poked out left of the panel as a black box)"); }
            }
            else if (!on && s_darkenHidden) { s_darken.SetActive(true); s_darkenHidden = false; }
        }

        // 1.0.192 (R-1015M probe: the remaining BLACK TAB left of crafting = 'Crafting/RepairButton', vanilla's 64x64 repair button with its
        // half-black inner Image, kit-skinned dark and left outside the panel's left edge). Under Panels, PanelsChrome draws its own repair
        // button inside crafting's top-left and forwards presses to vanilla's (m_repairButton.onClick), so vanilla's is made invisible and
        // click-through (CanvasGroup; never deactivated, vanilla keeps driving its state). Leaving Panels shows it again.
        static CanvasGroup s_repairGroup;
        static bool s_repairAdded, s_repairLogged;

        static void VanillaRepairOff(RectTransform crafting, bool on)
        {
            var gui = InventoryGui.instance;
            var repair = gui != null && gui.m_repairButton != null ? gui.m_repairButton.gameObject : null;
            if (repair == null) return;
            if (s_repairGroup == null || s_repairGroup.gameObject != repair)
            {
                s_repairGroup = repair.GetComponent<CanvasGroup>();
                s_repairAdded = s_repairGroup == null;
                if (s_repairAdded) s_repairGroup = repair.AddComponent<CanvasGroup>();
            }
            if (on)
            {
                if (s_repairGroup.alpha != 0f || s_repairGroup.blocksRaycasts)
                {
                    s_repairGroup.alpha = 0f; s_repairGroup.blocksRaycasts = false;
                    if (!s_repairLogged) { s_repairLogged = true; EquipmentPanel.PanelLog?.LogDebug($"panels: vanilla '{repair.name}' hidden under Panels (it drew as a black tab left of crafting; PanelsChrome's repair mirror is the button)"); }
                }
            }
            else if (s_repairGroup.alpha != 1f || !s_repairGroup.blocksRaycasts) { s_repairGroup.alpha = 1f; s_repairGroup.blocksRaycasts = true; }
        }

        static void Restore()
        {
            foreach (var kv in s_moved) if (kv.Key != null) kv.Key.anchoredPosition = kv.Value;
            foreach (var kv in s_grown) if (kv.Key != null) kv.Key.offsetMin = kv.Value;
            s_moved.Clear(); s_grown.Clear();
            foreach (var g in s_lineOff) if (g != null) g.enabled = true;   // 1.0.233
            s_lineOff.Clear();
            if (s_braid != null) { s_braid.alpha = 1f; s_braid = null; }   // Vanilla's divider back when the layout is left
        }

        // Vanilla's title divider
        // BraidLineHorisontalMedium, moved down with the content, lands across the tab row. Under Panels the kit header draws its own divider:
        // hidden (CanvasGroup alpha 0), back on Restore.
        const string Braid = "BraidLineHorisontalMedium";
        static CanvasGroup s_braid;
    }
}
