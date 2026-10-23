using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// VAInventory owns the chest panel's LAYOUT under Panels; Core's CraftingSkin draws the look (kit pills, the gold rule under the
    /// name, the inset gold-edged well for any KitWell_*, Tags 8 px right of Place stacks). Outside in:
    ///   panel background | Pad1 | gold border (KitWell_ChestFrame) | Pad2 | name (header) | row: Take all . Place stacks . Tags |
    ///   the grid | "[weight icon] Container weight: X" (no box) | Pad2 | gold border | Pad1 | edge; the same padding left and right.
    /// The panel's size follows its content (the grid's DRAWN size and the button row's width), so nothing can hang off an edge; each
    /// element is moved by its own drawn bounds (whatever its anchors or parent), only when off by more than half a unit, at most 4x/s inside
    /// vanilla's UpdateContainer (no timer of ours). The top-left corner stays where the dock put it. Reused: CraftSections.Well / Bounds.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
    internal static class ChestSections
    {
        const float Pad1 = 6f, Pad2 = 12f, HeaderH = 44f, Gap = 10f, TagsW = 72f, ButtonGap = 8f, WeightH = 24f;
        // ("a little less padding between the top of the background and the header text, and a little padding
        // between the bottom of the tabs and the top of the grid, because right now they overlap"): the title sits TopPad (not Pad2) under
        // Pad1; the grid starts RowGridGap below the LOWEST drawn bottom of the button row (Take all / Place stacks / Core's Tags).
        const float TopPad = 4f, RowGridGap = 10f;
        static float s_ruleH = 14f;   // The divider's drawn height, measured each layout tick
        static float s_next;
        static string s_lastLog, s_weightSized;
        static bool s_hierLogged;
        static readonly Vector3[] s_c = new Vector3[4];

        static void Postfix(InventoryGui __instance)
        {
            if (!UnifiedScreen.PanelsMode || !FiresCore.UI.Shell.FiresShell.OverrideOn) return;   // Unified UI OFF (Core override) = vanilla chest, untouched
            var root = __instance.m_container;
            if (root == null || !root.gameObject.activeInHierarchy) return;
            WeightText(__instance);   // every call: vanilla rewrites the number each frame
            DiagTick(__instance, root);
            s_gui = __instance;
            if (!s_hooked) { s_hooked = true; Canvas.willRenderCanvases += SnapBeforeRender; }
            if (Time.unscaledTime < s_next) return;
            s_next = Time.unscaledTime + 0.25f;
            try { Layout(__instance, root); }
            catch (System.Exception e) { if (s_lastLog != "fail") { s_lastLog = "fail"; EquipmentPanel.PanelLog?.LogDebug("panels: chest layout failed: " + e.Message); } }
        }

        static void Layout(InventoryGui gui, RectTransform root)
        {
            var gridInv = gui.m_containerGrid;
            RectTransform grid = gridInv != null ? (gridInv.m_gridRoot != null ? gridInv.m_gridRoot : gridInv.transform as RectTransform) : null;
            var name = gui.m_containerName != null ? gui.m_containerName.rectTransform : null;
            var take = gui.m_takeAllButton != null ? (RectTransform)gui.m_takeAllButton.transform : null;
            var stack = gui.m_stackAllButton != null ? (RectTransform)gui.m_stackAllButton.transform : null;
            var weight = gui.m_containerWeight != null ? gui.m_containerWeight.transform.parent as RectTransform : null;
            if (grid == null || !grid.gameObject.activeInHierarchy) return;
            if (!s_hierLogged) { s_hierLogged = true; LogHierarchy(root); }

            // ContainerGrid/Root is STRETCHED across the panel, so its rect = the panel's width, and measuring it would feed every resize
            // into the next (the panel would grow the whole time it's open). The grid is measured by its CELLS (the union of Root's
            // active children), which never stretch.
            Rect g = CellsBounds(root, grid);
            Vector2 takeS = Size(root, take), stackS = Size(root, stack);
            float rowH = Mathf.Max(takeS.y, stackS.y, 30f);
            float rowW = takeS.x + ButtonGap + stackS.x + ButtonGap + TagsW;
            float inner = Mathf.Max(g.width, rowW, 260f);
            float w = inner + 2f * (Pad1 + Pad2);
            float h = Pad1 + TopPad + HeaderH + Gap + s_ruleH + Gap + rowH + RowGridGap + g.height + Gap + WeightH + Pad2 + Pad1;

            // size the panel, keeping its top-left where the dock put it
            root.GetWorldCorners(s_c); Vector3 topLeft = s_c[1];
            bool resized = false;
            if (Mathf.Abs(root.rect.width - w) > 0.5f) { root.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, w); resized = true; }
            if (Mathf.Abs(root.rect.height - h) > 0.5f) { root.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, h); resized = true; }
            if (resized) { root.GetWorldCorners(s_c); root.position += topLeft - s_c[1]; }
            var bkg = root.Find("Bkg") as RectTransform;
            if (bkg != null && (bkg.anchorMin != Vector2.zero || bkg.anchorMax != Vector2.one || bkg.offsetMin != Vector2.zero || bkg.offsetMax != Vector2.zero))
            { bkg.anchorMin = Vector2.zero; bkg.anchorMax = Vector2.one; bkg.offsetMin = bkg.offsetMax = Vector2.zero; }
            // Vanilla's container Darken (kit-skinned) is larger than the panel and offset, so it reads as a dark outer plate / a second
            // frame. Under Panels it covers exactly the panel, like Bkg, so it sits hidden behind Bkg instead of framing it.
            var darken = root.Find("Darken") as RectTransform;
            if (darken != null && (darken.anchorMin != Vector2.zero || darken.anchorMax != Vector2.one || darken.offsetMin != Vector2.zero || darken.offsetMax != Vector2.zero))
            { darken.anchorMin = Vector2.zero; darken.anchorMax = Vector2.one; darken.offsetMin = darken.offsetMax = Vector2.zero; }

            Rect r = root.rect;   // local space, after the resize
            float left = r.xMin + Pad1 + Pad2, top = r.yMax - Pad1 - TopPad, cx = r.center.x;

            // ONE frame, the panel's own: the inner gold well (KitWell_ChestFrame) drew a box offset inside the outer frame, so it is no
            // longer drawn; an already-built one is switched off.
            var oldFrame = root.Find("KitWell_ChestFrame"); if (oldFrame != null && oldFrame.gameObject.activeSelf) oldFrame.gameObject.SetActive(false);
            var oldWell = root.Find("KitWell_ChestGrid"); if (oldWell != null && oldWell.gameObject.activeSelf) oldWell.gameObject.SetActive(false);   // an older grid-sized well, switched off

            // header: the name, centred, filling the inner width
            if (name != null)
            {
                gui.m_containerName.alignment = TextAlignmentOptions.Center;   // the name is centred
                gui.m_containerName.enableWordWrapping = false;
                gui.m_containerName.enableAutoSizing = true; gui.m_containerName.fontSizeMax = Mathf.Max(gui.m_containerName.fontSizeMax, 30f); gui.m_containerName.fontSizeMin = 14f;
                SetSize(name, inner, HeaderH);
                Move(root, name, new Vector2(left, top));
            }
            float y = top - HeaderH - Gap;
            // Order: the title centred, padding, the decorative divider, padding, then the tabs and the grid, so the tabs never overlap the
            // divider. Core's divider (KitChestRule* under the title) is measured by its DRAWN bottom (line + centre diamond) and the row
            // starts Gap below it; the panel height reserves its height (s_ruleH, last tick)
            {
                Transform rule = null;
                if (name != null) foreach (Transform c in name) if (c.name.StartsWith("KitChestRule", System.StringComparison.Ordinal) && c.gameObject.activeInHierarchy) { rule = c; break; }
                if (rule is RectTransform rrt)
                {
                    Rect rd = Drawn(root, rrt, CraftSections.Bounds(root, rrt));
                    s_ruleH = Mathf.Max(0f, rd.height);
                    y = Mathf.Min(y, rd.yMin - Gap);
                }
                else s_ruleH = 0f;
            }

            // the button row at the left under the title: Take all . Place stacks . (Tags, placed by Core right of Place stacks)
            float x = left;
            if (take != null) { Move(root, take, new Vector2(x, y - (rowH - takeS.y) * 0.5f)); x += takeS.x + ButtonGap; }
            if (stack != null) Move(root, stack, new Vector2(x, y - (rowH - stackS.y) * 0.5f));
            y -= rowH;
            // The
            // row's LOWEST DRAWN bottom - every visible Graphic under Take all / Place stacks / any "Tag" object anywhere under the panel
            // outside the grid - then RowGridGap.
            float rowBottom = RowDrawnBottom(root, grid, take, stack, y);
            y = rowBottom - RowGridGap;

            // the grid, centred - by its CELLS:
            // Move() places the root's own drawn rect, but vanilla draws the cells 110 units inside the root, so centring the ROOT on the
            // cells' width would put the cells 110 too far right and the last columns past the mask. The cells' top-left lands on the spot.
            {
                Vector2 want = new Vector2(left, y);   // Left-aligned with the buttons, directly under them
                Rect now = CellsBounds(root, grid);   // re-measured: the panel resize above shifts root-local coordinates
                float drawnTop = Drawn(root, grid, now).yMax;   // The cells' DRAWN top
                Vector2 d = want - new Vector2(now.xMin, drawnTop);
                // checked every tick (4x/s): whoever moved the grid or the row since, it goes back - counted for [ChestDiag]
                if (d.sqrMagnitude >= 0.25f) { grid.position += root.TransformVector(new Vector3(d.x, d.y, 0f)); s_reapplied++; }
                s_rowBottom = rowBottom; s_gridTop = drawnTop + d.y;
            }
            y -= g.height + Gap;

            // the weight line at the bottom, centred, no box
            if (weight != null)
            {
                Boxless(weight, gui.m_containerWeight);
                Vector2 ws = Size(root, weight);
                Move(root, weight, new Vector2(cx - ws.x * 0.5f, y - (WeightH - ws.y) * 0.5f));
            }

            string line = $"chest layout: panel {w:0}x{h:0} (inner {inner:0}; grid {g.width:0}x{g.height:0}; row {rowW:0}); frame KitWell_ChestFrame; name / row / grid / weight placed";
            if (line != s_lastLog) { s_lastLog = line; EquipmentPanel.PanelLog?.LogDebug("panels: " + line); LogRects(gui, root); }
        }

        // Once per new layout, the DRAWN rects (panel space) of the panel, its Bkg,
        // the grid parent, the cells' union and every Mask / RectMask2D under the panel, so a clip shows which edge is short.
        static void LogRects(InventoryGui gui, RectTransform root)
        {
            var sb = new System.Text.StringBuilder("panels: chest rects (panel space) - panel " + R(root, root));
            var bkg = root.Find("Bkg") as RectTransform; if (bkg != null) sb.Append("; Bkg " + R(root, bkg));
            var gridRoot = gui.m_containerGrid != null ? gui.m_containerGrid.m_gridRoot : null;
            if (gridRoot != null)
            {
                sb.Append("; grid root " + R(root, gridRoot));
                bool any = false; float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
                for (int i = 0; i < gridRoot.childCount; i++)
                {
                    var c = gridRoot.GetChild(i) as RectTransform; if (c == null || !c.gameObject.activeSelf) continue;
                    c.GetWorldCorners(s_c);
                    Vector2 a = root.InverseTransformPoint(s_c[0]), b = root.InverseTransformPoint(s_c[2]);
                    if (!any) { x0 = a.x; y0 = a.y; x1 = b.x; y1 = b.y; any = true; } else { x0 = Mathf.Min(x0, a.x); y0 = Mathf.Min(y0, a.y); x1 = Mathf.Max(x1, b.x); y1 = Mathf.Max(y1, b.y); }
                }
                if (any) sb.Append($"; cells [{x0:0},{y0:0}..{x1:0},{y1:0}]");
            }
            foreach (var m in root.GetComponentsInChildren<UnityEngine.UI.Mask>(true)) sb.Append($"; Mask '{m.name}' {R(root, (RectTransform)m.transform)}");
            foreach (var m in root.GetComponentsInChildren<UnityEngine.UI.RectMask2D>(true)) sb.Append($"; RectMask2D '{m.name}' {R(root, m.rectTransform)}");
            EquipmentPanel.PanelLog?.LogInfo(sb.ToString());
        }

        static string R(RectTransform space, RectTransform r)
        {
            r.GetWorldCorners(s_c);
            Vector2 a = space.InverseTransformPoint(s_c[0]), b = space.InverseTransformPoint(s_c[2]);
            return $"[{a.x:0},{a.y:0}..{b.x:0},{b.y:0}]";
        }

        static Rect CellsBounds(RectTransform root, RectTransform gridRoot)
        {
            bool any = false; Rect u = default;
            foreach (Transform c in gridRoot)
            {
                if (!c.gameObject.activeSelf || !(c is RectTransform crt)) continue;
                var b = CraftSections.Bounds(root, crt);
                u = any ? Rect.MinMaxRect(Mathf.Min(u.xMin, b.xMin), Mathf.Min(u.yMin, b.yMin), Mathf.Max(u.xMax, b.xMax), Mathf.Max(u.yMax, b.yMax)) : b;
                any = true;
            }
            return any ? u : CraftSections.Bounds(root, gridRoot);
        }

        // ── The gap held in SCREEN pixels, right before the canvas draws ──
        // Something can move the grid between the 4x/s layout pass and the frame being drawn, so a gap that is right in panel space
        // can still be off on screen. Every frame in Canvas.willRenderCanvases (after every Update / LateUpdate, before the draw), the
        // gap is measured on screen (drawn Graphics, TMP glyphs, screen px) and the grid is moved by the screen difference. Who moved it
        // in between is logged once per chest opening ([ChestDiag] moved-by).
        static InventoryGui s_gui;
        static bool s_hooked, s_movedLogged;
        static Vector3 s_snapPos; static bool s_snapValid;
        static int s_snaps;

        static void SnapBeforeRender()
        {
            try
            {
                var gui = s_gui;
                if (gui == null || !UnifiedScreen.PanelsMode || !FiresCore.UI.Shell.FiresShell.OverrideOn) { s_snapValid = false; return; }
                var root = gui.m_container;
                var gridInv = gui.m_containerGrid;
                var grid = gridInv != null ? gridInv.m_gridRoot : null;
                if (root == null || grid == null || !root.gameObject.activeInHierarchy || !grid.gameObject.activeInHierarchy) { s_snapValid = false; s_movedLogged = false; return; }
                if (s_snapValid && (grid.position - s_snapPos).sqrMagnitude > 1e-6f && !s_movedLogged)
                {
                    s_movedLogged = true;
                    EquipmentPanel.PanelLog?.LogInfo($"[ChestDiag] moved-by: the grid root moved {(grid.position - s_snapPos).ToString("F2")} (world) between two pre-render snaps (frame {Time.frameCount}); grid owner {Owner(grid)}, Root anchors {grid.anchorMin}-{grid.anchorMax} pivot {grid.pivot} size {grid.rect.size}");
                }
                var take = gui.m_takeAllButton != null ? (RectTransform)gui.m_takeAllButton.transform : null;
                var stack = gui.m_stackAllButton != null ? (RectTransform)gui.m_stackAllButton.transform : null;
                float rowB = float.MaxValue, gridT = float.MinValue;
                foreach (var p in RowParts(root, grid, take, stack))
                    foreach (var gph in p.GetComponentsInChildren<Graphic>(false)) if (Visible(gph)) rowB = Mathf.Min(rowB, ScreenDrawn(gph).yMin);
                foreach (var gph in grid.GetComponentsInChildren<Graphic>(false)) if (Visible(gph)) gridT = Mathf.Max(gridT, ScreenDrawn(gph).yMax);
                if (rowB == float.MaxValue || gridT == float.MinValue) { s_snapValid = false; return; }
                Rect rs = ScreenOf(root);
                float pxPerUnit = root.rect.height > 1f ? rs.height / root.rect.height : 1f;
                float dy = (rowB - RowGridGap * pxPerUnit) - gridT;   // screen px the grid must move (negative = down)
                if (Mathf.Abs(dy) > 0.5f)
                {
                    var cv = root.GetComponentInParent<Canvas>(); Camera cam = cv != null && cv.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? cv.rootCanvas.worldCamera : null;
                    Vector2 p0 = new Vector2(rs.center.x, gridT);
                    if (RectTransformUtility.ScreenPointToWorldPointInRectangle(root, p0, cam, out Vector3 w0)
                        && RectTransformUtility.ScreenPointToWorldPointInRectangle(root, p0 + new Vector2(0f, dy), cam, out Vector3 w1))
                    { grid.position += w1 - w0; s_snaps++; }
                }
                s_snapPos = grid.position; s_snapValid = true;
            }
            catch (System.Exception e) { s_snapValid = false; if (s_lastLog != "snapfail") { s_lastLog = "snapfail"; EquipmentPanel.PanelLog?.LogDebug("panels: chest pre-render snap failed: " + e.Message); } }
        }

        // ── 1.0.310 drawn rects + [ChestDiag] ──
        static float s_rowBottom, s_gridTop; static int s_reapplied;
        static bool s_wasOpen; static int s_diagFrame = -1; static float s_diagTime = -1f;

        static bool Visible(Graphic g) => g != null && g.enabled && g.gameObject.activeInHierarchy && g.color.a > 0.01f && (g.canvasRenderer == null || g.canvasRenderer.GetAlpha() > 0.01f);

        static Rect GlyphOrRect(RectTransform space, Graphic gph)
        {
            if (gph is TMP_Text t && t.textInfo != null && t.textInfo.characterCount > 0)
            {   // text: its glyph bounds, not the (often oversized) box
                var tb = t.textBounds;
                Vector3 a = space.InverseTransformPoint(t.transform.TransformPoint(tb.min)), c = space.InverseTransformPoint(t.transform.TransformPoint(tb.max));
                return Rect.MinMaxRect(Mathf.Min(a.x, c.x), Mathf.Min(a.y, c.y), Mathf.Max(a.x, c.x), Mathf.Max(a.y, c.y));
            }
            return CraftSections.Bounds(space, gph.rectTransform);
        }

        // union of the visible Graphics under rt (root space); `fallback` when nothing draws
        static Rect Drawn(RectTransform root, RectTransform rt, Rect fallback)
        {
            bool any = false; Rect u = fallback;
            foreach (var gph in rt.GetComponentsInChildren<Graphic>(false))
            {
                if (!Visible(gph)) continue;
                var b = GlyphOrRect(root, gph);
                u = any ? Rect.MinMaxRect(Mathf.Min(u.xMin, b.xMin), Mathf.Min(u.yMin, b.yMin), Mathf.Max(u.xMax, b.xMax), Mathf.Max(u.yMax, b.yMax)) : b;
                any = true;
            }
            return u;
        }

        static IEnumerable<RectTransform> RowParts(RectTransform root, RectTransform grid, RectTransform take, RectTransform stack)
        {
            if (take != null && take.gameObject.activeInHierarchy) yield return take;
            if (stack != null && stack.gameObject.activeInHierarchy) yield return stack;
            foreach (var t in root.GetComponentsInChildren<RectTransform>(false))
                if (t != root && t.name.IndexOf("Tag", System.StringComparison.OrdinalIgnoreCase) >= 0 && (grid == null || !t.IsChildOf(grid))
                    && (t.parent == null || t.parent.name.IndexOf("Tag", System.StringComparison.OrdinalIgnoreCase) < 0)) yield return t;
        }

        static float RowDrawnBottom(RectTransform root, RectTransform grid, RectTransform take, RectTransform stack, float y)
        {
            foreach (var p in RowParts(root, grid, take, stack)) y = Mathf.Min(y, Drawn(root, p, CraftSections.Bounds(root, p)).yMin);
            return y;
        }

        // [ChestDiag]: one frame and 1 s after the chest opens, everything drawn over the chest's screen rect, with who made it
        static void DiagTick(InventoryGui gui, RectTransform root)
        {
            bool open = root.gameObject.activeInHierarchy;
            if (open && !s_wasOpen) { s_diagFrame = Time.frameCount + 1; s_diagTime = Time.unscaledTime + 1f; s_reapplied = 0; s_snaps = 0; s_movedLogged = false; }
            s_wasOpen = open;
            if (!open) return;
            if (s_diagFrame >= 0 && Time.frameCount >= s_diagFrame) { s_diagFrame = -1; Diag(gui, root, "+1 frame"); }
            if (s_diagTime >= 0f && Time.unscaledTime >= s_diagTime) { s_diagTime = -1f; Diag(gui, root, "+1 s"); }
        }

        static Rect ScreenRect(Transform t, Rect local)
        {
            var cv = t.GetComponentInParent<Canvas>();
            Camera cam = cv != null && cv.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? cv.rootCanvas.worldCamera : null;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, t.TransformPoint(local.min)), b = RectTransformUtility.WorldToScreenPoint(cam, t.TransformPoint(local.max));
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }
        static Rect ScreenOf(RectTransform rt) => ScreenRect(rt, rt.rect);
        static Rect ScreenDrawn(Graphic g) => ScreenRect(g.transform, GlyphOrRect(g.rectTransform, g));
        static string S(Rect r) => $"[{r.xMin:0},{r.yMin:0}..{r.xMax:0},{r.yMax:0}]";
        static string PathOf(Transform t) { var sb = new System.Text.StringBuilder(t.name); for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/"); return sb.ToString(); }
        static string Owner(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
                foreach (var mb in p.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue; string asm = mb.GetType().Assembly.GetName().Name;
                    if (!asm.StartsWith("UnityEngine") && !asm.StartsWith("Unity.") && asm != "assembly_valheim" && asm != "assembly_guiutils" && asm != "assembly_utils")
                        return asm + (p == t ? "" : "@" + p.name);
                }
            return "vanilla";
        }
        static string Cut(string s) => s == null ? "" : (s.Length > 20 ? s.Substring(0, 20) : s).Replace("\n", " ");

        static void Diag(InventoryGui gui, RectTransform root, string when)
        {
            var log = EquipmentPanel.PanelLog; if (log == null) return;
            try
            {
                Rect chest = ScreenOf(root);
                var grid = gui.m_containerGrid != null ? gui.m_containerGrid.m_gridRoot : null;
                var take = gui.m_takeAllButton != null ? (RectTransform)gui.m_takeAllButton.transform : null;
                var stack = gui.m_stackAllButton != null ? (RectTransform)gui.m_stackAllButton.transform : null;
                log.LogInfo($"[ChestDiag] {when}: chest {PathOf(root)} screen {S(chest)}; layout row drawn bottom {s_rowBottom:0} / grid drawn top {s_gridTop:0} (panel space) = gap {s_rowBottom - s_gridTop:0}; re-applied {s_reapplied}x, pre-render snaps {s_snaps}x since open");
                if (grid != null)
                {   // the row and the grid in SCREEN pixels, from the drawn Graphics
                    float rowB = float.MaxValue, gridT = float.MinValue;
                    foreach (var p in RowParts(root, grid, take, stack))
                        foreach (var gph in p.GetComponentsInChildren<Graphic>(false))
                            if (Visible(gph)) { var sr = ScreenDrawn(gph); rowB = Mathf.Min(rowB, sr.yMin); log.LogInfo($"[ChestDiag]   row part {PathOf(gph.transform)} {gph.GetType().Name} drawn {S(sr)}"); }
                    foreach (var gph in grid.GetComponentsInChildren<Graphic>(false)) if (Visible(gph)) gridT = Mathf.Max(gridT, ScreenDrawn(gph).yMax);
                    log.LogInfo($"[ChestDiag]   SCREEN: row bottom {rowB:0}, grid top {gridT:0}, gap {rowB - gridT:0} px (want >= {RowGridGap:0})");
                }
                for (var p = root.parent as RectTransform; p != null; p = p.parent as RectTransform)
                    log.LogInfo($"[ChestDiag]   ancestor {p.name} screen {S(ScreenOf(p))} owner {Owner(p)}");
                // every enabled Graphic on the chest's root canvas overlapping the chest (cell innards skipped)
                var cv = root.GetComponentInParent<Canvas>(); Transform top = cv != null ? cv.rootCanvas.transform : root;
                var paths = new HashSet<Transform>(); int n = 0;
                foreach (var gph in top.GetComponentsInChildren<Graphic>(false))
                {
                    if (!gph.enabled) continue;
                    Rect sr = ScreenDrawn(gph); if (!sr.Overlaps(chest)) continue;
                    if (grid != null && gph.transform.IsChildOf(grid) && gph.transform.parent != grid && gph.transform != grid) continue;
                    if (++n > 200) { log.LogInfo("[ChestDiag]   ... (200 cap)"); break; }
                    string what = gph is TMP_Text t ? "text '" + Cut(t.text) + "'" : gph is Text ut ? "text '" + Cut(ut.text) + "'" : gph is Image im ? "sprite " + (im.sprite != null ? im.sprite.name : "none") : gph.GetType().Name;
                    float a = gph.color.a * (gph.canvasRenderer != null ? gph.canvasRenderer.GetAlpha() : 1f);
                    log.LogInfo($"[ChestDiag]   G {PathOf(gph.transform)} {S(sr)} {what} a={a:0.00} sort={(gph.canvas != null ? gph.canvas.sortingOrder : 0)} by {Owner(gph.transform)}");
                    for (var p = gph.transform; p != null && p != top; p = p.parent) paths.Add(p);
                }
                foreach (var p in paths)
                {
                    foreach (var lg in p.GetComponents<LayoutGroup>()) log.LogInfo($"[ChestDiag]   L {lg.GetType().Name} on {PathOf(p)} enabled={lg.enabled}");
                    var csf = p.GetComponent<ContentSizeFitter>(); if (csf != null) log.LogInfo($"[ChestDiag]   L ContentSizeFitter on {PathOf(p)} enabled={csf.enabled} h={csf.horizontalFit} v={csf.verticalFit}");
                    var m2 = p.GetComponent<RectMask2D>(); if (m2 != null) log.LogInfo($"[ChestDiag]   L RectMask2D on {PathOf(p)} enabled={m2.enabled} screen {S(ScreenOf((RectTransform)p))}");
                }
            }
            catch (System.Exception e) { log.LogInfo("[ChestDiag] failed: " + e); }
        }

        static Vector2 Size(RectTransform root, RectTransform rt) => rt != null && rt.gameObject.activeInHierarchy ? CraftSections.Bounds(root, rt).size : Vector2.zero;

        static void SetSize(RectTransform rt, float w, float h)
        {
            if (Mathf.Abs(rt.rect.width - w) > 0.5f) rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, w);
            if (Mathf.Abs(rt.rect.height - h) > 0.5f) rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, h);
        }

        // moves rt so its DRAWN top-left (root-local) lands on want; works whatever its anchors / pivot / parent
        static void Move(RectTransform root, RectTransform rt, Vector2 want)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy) return;
            Rect b = CraftSections.Bounds(root, rt);
            Vector2 d = want - new Vector2(b.xMin, b.yMax);
            if (d.sqrMagnitude < 0.25f) return;
            rt.position += root.TransformVector(new Vector3(d.x, d.y, 0f));
        }

        // No box, just a small weight icon and the words: the weight group's own background images go; an image named *icon* stays,
        // made small; the text is left-aligned beside it.
        static void Boxless(RectTransform weight, TMP_Text text)
        {
            foreach (var img in weight.GetComponentsInChildren<Image>(true))
            {
                bool icon = img.name.IndexOf("icon", System.StringComparison.OrdinalIgnoreCase) >= 0 || (img.sprite != null && img.sprite.name.IndexOf("weight", System.StringComparison.OrdinalIgnoreCase) >= 0);
                if (!icon) { if (img.enabled) img.enabled = false; continue; }
                var irt = img.rectTransform;
                if (irt.rect.height > WeightH - 4f) irt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, WeightH - 6f);
                if (irt.rect.width > WeightH - 4f) irt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, WeightH - 6f);
                img.preserveAspect = true;
            }
            if (text != null)
            {
                text.enableWordWrapping = false; text.alignment = TextAlignmentOptions.MidlineLeft;
                // The full line shows
                text.overflowMode = TextOverflowModes.Overflow;
                if (text.rectTransform.rect.width < 200f) text.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 220f);
            }
        }

        static void WeightText(InventoryGui gui)
        {
            var t = gui.m_containerWeight;
            if (t == null) return;
            string s = t.text ?? "";
            if (!s.StartsWith("Container weight", System.StringComparison.Ordinal)) t.text = s = "Container weight: " + s;
            // 
            // the label's rect is held at least as wide as its text, left-aligned and overflowing, every call - a restyle after the 0.25 s
            // layout tick (or a right-aligned clip) showed only the end of the line
            if (t.alignment != TextAlignmentOptions.MidlineLeft) t.alignment = TextAlignmentOptions.MidlineLeft;
            if (t.overflowMode != TextOverflowModes.Overflow) t.overflowMode = TextOverflowModes.Overflow;
            if (t.enableWordWrapping) t.enableWordWrapping = false;
            if (s != s_weightSized) { s_weightSized = s; float need = t.GetPreferredValues(s).x + 6f; if (t.rectTransform.rect.width < need) t.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, need); }
        }

        static void LogHierarchy(RectTransform root)
        {
            var sb = new System.Text.StringBuilder("panels: chest hierarchy (once): ");
            foreach (Transform c in root)
            {
                sb.Append(c.name).Append(c.gameObject.activeSelf ? "" : "(off)").Append(" [");
                foreach (Transform cc in c) sb.Append(cc.name).Append(' ');
                sb.Append("] ");
            }
            EquipmentPanel.PanelLog?.LogDebug(sb.ToString());
        }
    }
}
