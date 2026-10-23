using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Managers.HotBars
{
    /// <summary>
    /// While the Fires UI override is ON, the
    /// Tool / Food / Ammo hotbars are Core HUD dock elements (FiresShell.RegisterHudElement, Core 0.2.522+, layer "hud"): Core places and scales
    /// them, layout mode drags / docks them, and the spot is saved per character + resolution. While registered, the bar's own placement
    /// (HotBarPlacement from [*Hotbar] Position) stands down. Override OFF, or the bar cleared = unregistered, and the config placement is back.
    /// Ids: vainv.hotbar.tool / .food / .ammo. All by reflection (exact Type.GetType): an older Core = inert, the config placement as before.
    /// </summary>
    internal static class HotbarHudDock
    {
        const string ShellType = "FiresCore.UI.Shell.FiresShell, FiresUnifiedCore";
        const string OptionsType = "FiresCore.UI.Shell.HudElementOptions, FiresUnifiedCore";

        static bool s_resolved;
        static PropertyInfo s_overrideOn;
        static MethodInfo s_register, s_unregister;
        static Type s_options;
        static readonly Dictionary<string, RectTransform> s_owned = new Dictionary<string, RectTransform>();
        static readonly Dictionary<string, bool> s_regOn = new Dictionary<string, bool>();   // The override mode each bar was registered in
        static readonly HashSet<string> s_dumped = new HashSet<string>();
        // Once per bar, the hierarchy the
        // fit walks (name, active, rect, children), so the next log names the slot structure instead of us guessing
        static void DumpHierarchyOnce(RectTransform root)
        {
            if (root == null || !s_dumped.Add(root.name)) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                void Walk(Transform t, int depth)
                {
                    if (depth > 4) return;
                    var rt = t as RectTransform;
                    sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeInHierarchy ? " [on] " : " [off] ")
                      .Append(rt != null ? $"{rt.rect.width:0}x{rt.rect.height:0} @({rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0})" : "").Append('\n');
                    foreach (Transform c in t) Walk(c, depth + 1);
                }
                Walk(root, 0);
                EquipmentPanel.PanelLog?.LogInfo($"[VAInventory] hotbars: hierarchy of {root.name}:\n{sb}");
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"hotbars: hierarchy dump failed: {e.Message}"); }
        }
        static readonly HashSet<string> s_placedOnce = new HashSet<string>();   // Bar instances the config placement has laid out once

        static bool Resolve()
        {
            if (!s_resolved)
            {
                s_resolved = true;
                var shell = Type.GetType(ShellType);
                s_overrideOn = shell?.GetProperty("OverrideOn", BindingFlags.Public | BindingFlags.Static);
                s_register = shell?.GetMethod("RegisterHudElement", BindingFlags.Public | BindingFlags.Static);
                s_unregister = shell?.GetMethod("UnregisterHudElement", BindingFlags.Public | BindingFlags.Static);
                // take the options type from RegisterHudElement's own 3rd parameter (RPGClasses' hard-coded name failed silently)
                var ps = s_register?.GetParameters();
                s_options = ps != null && ps.Length >= 3 ? ps[2].ParameterType : Type.GetType(OptionsType);
                EquipmentPanel.PanelLog?.LogDebug(s_register != null && s_options != null
                    ? "hotbars: Core HUD dock found; Tool / Food / Ammo bars are movable in layout mode while the Fires UI override is ON"
                    : "hotbars: no Core HUD dock (Core older than 0.2.522); the bars keep their config positions");
            }
            return s_register != null && s_unregister != null && s_options != null && s_overrideOn != null;
        }

        static bool OverrideOn() { try { return s_overrideOn != null && s_overrideOn.GetValue(null) is bool b && b; } catch { return false; } }

        /// <summary>Called from the bar's ApplyTransform. True = Core owns the bar's position (skip the config placement).</summary>
        internal static bool Manage(string barName, string label, RectTransform rect)
        {
            if (rect == null || !Resolve()) return false;
            FitToElements(rect);   // The rect Core outlines / drags / docks = the visible bar
            string id = IdOf(barName);
            { string other = BaseId(barName) + (OverrideOn() ? ".classic" : ""); if (s_owned.ContainsKey(other)) { EquipmentPanel.PanelLog?.LogInfo($"[VAInventory] hotbars: mode switch - {other} handed over to {id}"); ReleaseId(other); } }   // 1.0.299
            bool want = true;   // Always a Core HUD dock element, not only under the Fires UI override
            s_owned.TryGetValue(id, out var have);
            UpdateHitBounds(rect);   // 1.0.251
            // Options depend on the mode: SizeChanged = KitHotbars.Reflow (a SIZE resize that re-lays the KIT row) only makes sense with the
            // override ON; with it OFF there is no kit row, so Core's grip would only stretch the empty rect instead of moving the bar.
            // A mode flip re-registers the bar.
            bool on = OverrideOn();
            if (want && have == rect && s_regOn.TryGetValue(id, out var regOn) && regOn == on) return true;
            if (want && have == rect) { EquipmentPanel.PanelLog?.LogInfo($"[VAInventory] hotbars: {id} override {(on ? "ON" : "OFF")}: re-registering ({(on ? "size resize, kit row" : "scale resize")})"); Release(barName); have = null; }
            DumpHierarchyOnce(rect);
            if (want)
            {
                // Core takes the element's spot AT REGISTER as its default,
                // so the bar's own config placement must have laid it out first. The first call after the bar (re)appears lets the placement run
                // (returns false); the next one registers it right where the player always had it.
                // A bar that was
                // dock-registered once this session is Core's to place for good - a RE-CREATED rect (pouch emptied/refilled, hide/show)
                // re-registers at once and the old config placement never runs for it again (it put the bar back into VAInventory's stack)
                // NO first placement pass any more - the bar
                // registers at once and Core places it (saved spot, else Core's embedded default). VAInventory never sets a start spot.
                if (have != null) Release(barName);
                try
                {
                    var o = Activator.CreateInstance(s_options);
                    s_options.GetField("Owner")?.SetValue(o, "VAInventory");
                    s_options.GetField("Label")?.SetValue(o, label);
                    s_options.GetField("ShowInLayout")?.SetValue(o, true);   // listed + shown while the layout editor is open, even when hidden
                    { var fa = s_options.GetField("AlsoInInventoryLayout"); if (fa != null) fa.SetValue(o, true); else s_options.GetProperty("AlsoInInventoryLayout")?.SetValue(o, true); }   // draggable in the INVENTORY layout editor too (one HUD element, saved in dock_hud)
                    // A resize re-flows the bar's kit slots (cols x rows), slot size never under MinSize
                    if (on)   // Size resize only where a kit row re-lays; OFF = Core's scale resize (the player panel / chest path)
                    { string bn = barName; Action<Vector2> sc = want => KitHotbarsImpl.Reflow(bn, want);
                      var fs = s_options.GetField("SizeChanged"); if (fs != null) fs.SetValue(o, sc); else s_options.GetProperty("SizeChanged")?.SetValue(o, sc);
                      var mv = new Vector2(KitHotbarsImpl.MinCell, KitHotbarsImpl.MinCell);
                      // Reset drops the saved size and re-lays the bar at its default (Action, or Action<Vector2> tolerated)
                      { var fr = s_options.GetField("SizeReset"); var pr = fr == null ? s_options.GetProperty("SizeReset") : null; var tr = fr?.FieldType ?? pr?.PropertyType;
                        object d = tr == typeof(Action) ? (object)(Action)(() => KitHotbarsImpl.ResetFlow(bn)) : tr == typeof(Action<Vector2>) ? (Action<Vector2>)(_ => KitHotbarsImpl.ResetFlow(bn)) : null;
                        if (d != null) { if (fr != null) fr.SetValue(o, d); else pr.SetValue(o, d); } }
                      var fm = s_options.GetField("MinSize"); if (fm != null) fm.SetValue(o, mv); else s_options.GetProperty("MinSize")?.SetValue(o, mv); }
                    // The bar drags / resizes without the editor, by the
                    // same invisible 18 px strip above its top edge the panels use (reused: PanelsDock.LiveHandle)
                    {
                        var handle = PanelsDock.LiveHandle(id, rect);
                        var fh = s_options.GetField("DragHandle"); var fl = s_options.GetField("LiveEdit");
                        if (handle != null && fh != null && fl != null) { fh.SetValue(o, handle); fl.SetValue(o, true); }
                        else EquipmentPanel.PanelLog?.LogDebug($"hotbars: {id} no LiveEdit (Core older than 0.2.583)");
                    }
                    // Core switches an element it turned ON for the editor back OFF on close unless Visible() says it should show: the bar's own shown rule
                    {
                        Func<bool> raw = barName == ToolHotBar.barName ? (Func<bool>)ToolHotBar.IsEnabled : barName == FoodHotBar.barName ? (Func<bool>)FoodHotBar.IsEnabled
                            : barName == AmmoHotBar.barName ? (Func<bool>)AmmoHotBar.IsEnabled : null;
                        // Cached: IsEnabled walks the pouch slots with LINQ every call and Core polls Visible() every frame; re-read at most 4x/s
                        // (the bars' own refresh rate); O(1) between
                        Func<bool> vis = null;
                        if (raw != null) { bool last = false; float next = -1f; vis = () => { float now = Time.unscaledTime; if (now >= next) { next = now + 0.25f; try { last = raw(); } catch { last = false; } } return last; }; }
                        var fv = s_options.GetField("Visible");
                        if (vis != null && fv != null && fv.FieldType == typeof(Func<bool>)) fv.SetValue(o, vis);
                    }
                    // HitBounds (Core 0.2.603+) = everything the bar DRAWS (slots, key hints, counts, labels) + 6 px all round; Core's hit area,
                    // grips and dock edges use exactly it, so docked rows stack with clear padding (no row covers the key hints of the row above)
                    // and ANY part of the bar drags it
                    { var hb = UpdateHitBounds(rect); var fhb = s_options.GetField("HitBounds"); if (hb != null && fhb != null) fhb.SetValue(o, hb); }
                    s_register.Invoke(null, new object[] { id, rect, o });
                    s_owned[id] = rect; s_regOn[id] = on;
                    EquipmentPanel.PanelLog?.LogInfo($"[VAInventory] hotbars: {id} registered for override {(on ? "ON (size resize)" : "OFF (scale resize)")}, rect {rect.rect.width:0}x{rect.rect.height:0}");
                    if (s_everRegistered.Add(id)) EquipmentPanel.PanelLog?.LogInfo($"[VAInventory] hotbars: {id} placement owned by Core (VAInventory stands down)");
                    else { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] hotbars: {id} re-created bar re-registered with no VAInventory placement (Core restores its spot)"); return true; }   // the default stays the FIRST registration's spot
                    // No SetDefaultHere - Core's embedded default is the Reset spot, not the old config spot
                    EquipmentPanel.PanelLog?.LogInfo($"[VAInventory] hotbars: {id} registered with Core's HUD dock");
                    return true;
                }
                catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"hotbars: {id} HUD dock register failed: {(e.InnerException ?? e).Message}"); return false; }
            }
            if (have != null) Release(barName);   // override went OFF: the config placement takes over again
            return false;
        }

        // 1.0.168. The bar's root is a clone
        // of vanilla's HotKeyBar, whose slot elements are laid out from the root's pivot, so the root's own rect isn't where the bar draws.
        // Its rect is fitted to its active slot elements: only sizeDelta + pivot change (children are placed relative to the pivot point,
        // which stays put), so nothing on screen moves, and Core's outline, drag handle and dock edges cover exactly the visible bar.
        static readonly Dictionary<RectTransform, int> s_fitCount = new Dictionary<RectTransform, int>();

        // The HitBounds child ("VAInvHotbarHitBounds", no graphic) covers every visible graphic of the bar (text included) + Pad,
        // in the root's local space (anchors at the root's pivot = local origin). Re-measured on every Manage call (2x/s placement tick),
        // written only when it changed; logged once per bar per size.
        const float HitPad = 6f;
        const string HitName = "VAInvHotbarHitBounds";
        static readonly Dictionary<RectTransform, Rect> s_hitLast = new Dictionary<RectTransform, Rect>();
        static readonly Vector3[] s_hc = new Vector3[4];
        static RectTransform UpdateHitBounds(RectTransform root)
        {
            if (root == null) return null;
            var hb = root.Find(HitName) as RectTransform;
            if (hb == null)
            {
                hb = (RectTransform)new GameObject(HitName, typeof(RectTransform), typeof(UnityEngine.UI.LayoutElement)).transform;
                hb.SetParent(root, false); hb.GetComponent<UnityEngine.UI.LayoutElement>().ignoreLayout = true;
            }
            bool any = false; float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            // With the override OFF HitBounds = the fitted bar rect (the bar's real slots + margin, FitToElements): Core docks and places
            // its handles on HitBounds, and the drawn-graphic scan can see only a stray sliver graphic there. Override ON keeps the
            // drawn-graphic scan below, unchanged.
            if (!OverrideOn())
            {
                var rr = root.rect;
                x0 = rr.xMin; y0 = rr.yMin; x1 = rr.xMax; y1 = rr.yMax; any = rr.width > 1f && rr.height > 1f;
            }
            if (OverrideOn())
            foreach (var g in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
            {
                if (g == null || !g.isActiveAndEnabled || g.color.a < 0.02f || g.canvasRenderer.GetInheritedAlpha() < 0.02f) continue;
                var rt = g.rectTransform; if (rt.rect.width < 1f || rt.rect.height < 1f) continue;
                rt.GetWorldCorners(s_hc);
                for (int i = 0; i < 4; i++)
                {
                    Vector3 l = root.InverseTransformPoint(s_hc[i]);
                    if (!any) { x0 = x1 = l.x; y0 = y1 = l.y; any = true; } else { x0 = Mathf.Min(x0, l.x); x1 = Mathf.Max(x1, l.x); y0 = Mathf.Min(y0, l.y); y1 = Mathf.Max(y1, l.y); }
                }
            }
            if (!any) return hb;
            var want = new Rect(x0 - HitPad, y0 - HitPad, (x1 - x0) + 2f * HitPad, (y1 - y0) + 2f * HitPad);
            if (s_hitLast.TryGetValue(root, out var had) && Mathf.Abs(had.x - want.x) < 0.5f && Mathf.Abs(had.y - want.y) < 0.5f && Mathf.Abs(had.width - want.width) < 0.5f && Mathf.Abs(had.height - want.height) < 0.5f) return hb;
            s_hitLast[root] = want;
            hb.anchorMin = hb.anchorMax = root.pivot; hb.pivot = new Vector2(0.5f, 0.5f);
            hb.sizeDelta = want.size; hb.anchoredPosition = want.center;
            EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] hotbars: {root.name} HitBounds = drawn slots + key hints + {HitPad:0} px = {want.width:0}x{want.height:0}");
            return hb;
        }

        static void FitToElements(RectTransform root)
        {
            int n = 0; bool any = false; float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            var c = new Vector3[4];
            // Only what is DRAWN counts - visible graphics (active, enabled, inherited alpha > 0.02), never the vanilla elements
            // the kit row hides at alpha 0 or empty layout nodes (they would stretch the rect past the visible slots and off-centre docking).
            // The rect is the bar's REAL slots - the kit row while it is drawn (override ON), else vanilla's HotkeyBar elements
            // (m_elements[].m_go) - and the drawn-graphic scan below only runs when neither exists (a lone stray graphic would put Core's
            // handle strip and corner grip on a sliver). Reused: KitHotbarsImpl's row + vanilla's own element list.
            var parts = new List<RectTransform>();
            var kitRow = KitHotbarsImpl.RowRoot(root.name);
            if (kitRow != null) parts.Add(kitRow);
            else
            {
                var hb = root.GetComponent<HotkeyBar>();
                if (hb != null && HarmonyLib.Traverse.Create(hb).Field("m_elements").GetValue() is System.Collections.IEnumerable els)
                    foreach (var e in els)
                    {
                        var go = HarmonyLib.Traverse.Create(e).Field("m_go").GetValue() as GameObject;
                        if (go != null && go.activeInHierarchy && go.transform is RectTransform ert && ert.rect.width >= 8f) parts.Add(ert);
                    }
                // VAInventory's bars carry no vanilla m_elements, so the slots are every active descendant that holds vanilla's element prefab
                // child "icon" - the element roots, whatever builds them
                if (parts.Count == 0)
                    foreach (var t in root.GetComponentsInChildren<RectTransform>(false))
                        if (t != root && t.Find("icon") != null && t.rect.width >= 16f && t.rect.height >= 16f) parts.Add(t);   // every icon holder, nested ones included
            }
            foreach (var p in parts)
            {
                p.GetWorldCorners(c); n++;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 l = root.InverseTransformPoint(c[i]);
                    if (!any) { x0 = x1 = l.x; y0 = y1 = l.y; any = true; }
                    else { x0 = Mathf.Min(x0, l.x); x1 = Mathf.Max(x1, l.x); y0 = Mathf.Min(y0, l.y); y1 = Mathf.Max(y1, l.y); }
                }
            }
            if (parts.Count > 0) n += kitRow != null ? 1000 : 500;   // a parts fit (never equal to a graphic count)
            if (parts.Count == 0)
            foreach (var g in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
            {
                if (g == null || !g.isActiveAndEnabled || g.transform == root || g.canvasRenderer.GetInheritedAlpha() < 0.02f || g.color.a < 0.02f) continue;
                var rt = g.rectTransform;
                if (rt.rect.width < 1f || rt.rect.height < 1f) continue;
                n++;
                rt.GetWorldCorners(c);
                for (int i = 0; i < 4; i++)
                {
                    Vector3 l = root.InverseTransformPoint(c[i]);
                    if (!any) { x0 = x1 = l.x; y0 = y1 = l.y; any = true; }
                    else { x0 = Mathf.Min(x0, l.x); x1 = Mathf.Max(x1, l.x); y0 = Mathf.Min(y0, l.y); y1 = Mathf.Max(y1, l.y); }
                }
            }
            // Right after a re-create only one stray graphic may pass the drawn-graphic filter, which would put the rect (and Core's handle
            // strip above it) away from the slots. One graphic or fewer =
            // fit to the bar's active slot ELEMENTS (its direct children with a real size) instead.
            if (n <= 1 && parts.Count == 0)
            {
                int m = 0; bool any2 = false; float a0 = 0, b0 = 0, a1 = 0, b1 = 0;
                foreach (Transform ch in root)
                {
                    if (!ch.gameObject.activeSelf || !(ch is RectTransform crt) || crt.rect.width < 8f || crt.rect.height < 8f) continue;
                    m++;
                    crt.GetWorldCorners(c);
                    for (int i = 0; i < 4; i++)
                    {
                        Vector3 l = root.InverseTransformPoint(c[i]);
                        if (!any2) { a0 = a1 = l.x; b0 = b1 = l.y; any2 = true; }
                        else { a0 = Mathf.Min(a0, l.x); a1 = Mathf.Max(a1, l.x); b0 = Mathf.Min(b0, l.y); b1 = Mathf.Max(b1, l.y); }
                    }
                }
                if (m > 1) { x0 = a0; x1 = a1; y0 = b0; y1 = b1; n = 100 + m; any = true; }   // 100+ = an element fit (never equal to a graphic count)
            }
            if (!any) return;
            // Re-fit whenever the measured size changes, not only the count: every kit-row fit counts as n = 1000, so a count-only check
            // would keep the first fit (taken before the kit row was laid out) and the rect would never follow the real row.
            {
                float m0 = (parts.Count > 0 && kitRow == null) ? 14f : 0f;
                var probe = new Vector2(Mathf.Max(1f, x1 - x0 + 2f * m0), Mathf.Max(1f, y1 - y0 + 2f * m0));
                if (s_fitCount.TryGetValue(root, out int had) && had == n && Mathf.Abs(root.sizeDelta.x - probe.x) < 0.5f && Mathf.Abs(root.sizeDelta.y - probe.y) < 0.5f) return;
            }
            s_fitCount[root] = n;
            // local-space bounds relative to the pivot point: rect = [-pivot*size, (1-pivot)*size]
            // The dock rect gets a margin around the slots, so Core's corner grip (and
            // its outside zone) sits in that margin - off the last slot - and the handle strip above clears the key hints
            if (parts.Count > 0 && kitRow == null) { const float M = 14f;   /* Unified OFF only: the unified rect stays exactly as it was */ x0 -= M; y0 -= M; x1 += M; y1 += M; }
            var size = new Vector2(Mathf.Max(1f, x1 - x0), Mathf.Max(1f, y1 - y0));
            var pivot = new Vector2(-x0 / size.x, -y0 / size.y);
            Vector3 before = root.position;
            var kids = new List<KeyValuePair<Transform, Vector3>>();
            foreach (Transform child in root) kids.Add(new KeyValuePair<Transform, Vector3>(child, child.position));
            root.sizeDelta = size; root.pivot = pivot;
            root.position = before;   // the pivot point stays where it was
            foreach (var k in kids) if (k.Key != null) k.Key.position = k.Value;   // and every child exactly where it was (anchored children too)
            EquipmentPanel.PanelLog?.LogDebug($"hotbars: {root.name} rect fitted to {(n >= 1000 ? "its kit row" : n >= 500 ? (n - 500) + " slot element(s)" : n + " drawn graphic(s)")}: {size.x:0}x{size.y:0}");   // say what it counted
        }
        // The bar registers right after its config placement, so
        // its on-screen top-left now IS the config spot; DockService.SetDefault pins that as the default "Reset layout" returns to.
        // Screen px, y down from the top. Older Core (no SetDefault) = skipped.
        static System.Reflection.MethodInfo s_setDefault; static bool s_setDefaultLooked;

        static void SetDefaultHere(string id, RectTransform rect)
        {
            if (!s_setDefaultLooked)
            {
                s_setDefaultLooked = true;
                s_setDefault = Type.GetType("FiresCore.UI.Dock.DockService, FiresUnifiedCore")?.GetMethod("SetDefault", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Vector2) }, null);
            }
            if (s_setDefault == null) return;
            try
            {
                var c = new Vector3[4]; rect.GetWorldCorners(c);
                var canvas = rect.GetComponentInParent<Canvas>()?.rootCanvas;
                Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                Vector2 tl = RectTransformUtility.WorldToScreenPoint(cam, c[1]);
                var topLeft = new Vector2(tl.x, Screen.height - tl.y);
                s_setDefault.Invoke(null, new object[] { id, topLeft });
                EquipmentPanel.PanelLog?.LogDebug($"hotbars: {id} default = its config spot {topLeft.x:0},{topLeft.y:0} (screen, y down)");
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"hotbars: {id} SetDefault failed: {(e.InnerException ?? e).Message}"); }
        }
        /// <summary>True while Core's HUD dock owns this bar (its own drag handler then never moves it or writes the cfg).</summary>
        // The player's
        // per-element Hide in Core's layout (DockService.IsUserHidden) is applied here too - an owned bar's own CanvasGroup goes to alpha 0
        // and stops raycasts while hidden, and comes back when shown. Called with the 0.5 s bar pass.
        static System.Reflection.MethodInfo s_isUserHidden; static bool s_hiddenLooked;
        static readonly Dictionary<RectTransform, bool> s_userHidden = new Dictionary<RectTransform, bool>();

        static PropertyInfo s_hudEdit, s_invEdit; static bool s_editLooked;
        static bool EditActive()
        {
            if (!s_editLooked)
            {
                s_editLooked = true;
                var t = Type.GetType("FiresCore.UI.Dock.DockService, FiresUnifiedCore");
                s_hudEdit = t?.GetProperty("HudEditMode", BindingFlags.Public | BindingFlags.Static);
                s_invEdit = t?.GetProperty("InventoryEditMode", BindingFlags.Public | BindingFlags.Static);
            }
            try { return (s_hudEdit?.GetValue(null) is bool h && h) || (s_invEdit?.GetValue(null) is bool i && i); } catch { return false; }
        }

        internal static void ApplyUserHidden(RectTransform rect)
        {
            if (rect == null) return;
            string id = null;
            foreach (var kv in s_owned) if (kv.Value == rect) { id = kv.Key; break; }
            if (id == null) return;
            if (!s_hiddenLooked) { s_hiddenLooked = true; s_isUserHidden = Type.GetType("FiresCore.UI.Dock.DockService, FiresUnifiedCore")?.GetMethod("IsUserHidden", new[] { typeof(string) }); }
            bool hidden = false;
            try { hidden = s_isUserHidden != null && s_isUserHidden.Invoke(null, new object[] { id }) is bool b && b; } catch { }
            // The
            // editor's Hide applies ONLY while a layout edit is on (HUD or inventory layout mode); when the editor closes every bar is back to
            // its normal runtime behaviour (empty bars auto-hide, bars with items show)
            hidden = hidden && EditActive();
            s_userHidden.TryGetValue(rect, out bool was);
            if (hidden == was) return;
            s_userHidden[rect] = hidden;
            var cg = rect.GetComponent<CanvasGroup>() ?? rect.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = hidden ? 0f : 1f; cg.blocksRaycasts = !hidden;
            EquipmentPanel.PanelLog?.LogDebug($"hotbars: {id} {(hidden ? "hidden for the layout edit (editor Hide)" : "back to its runtime visibility")}");
        }

        // "owned" = this rect is registered OR its bar id was registered before this session (a re-created rect is still Core's)
        internal static bool Owns(RectTransform rect) => rect != null && (s_owned.ContainsValue(rect) || s_everRegistered.Contains(IdOf(rect.name)));
        internal static bool AnyEverRegistered => s_everRegistered.Count > 0;
        static readonly HashSet<string> s_everRegistered = new HashSet<string>();

        /// <summary>Re-fit a docked bar to its CURRENT slot elements (cheap: FitToElements returns at once when the count is unchanged).</summary>
        internal static void Refit(RectTransform rect)
        {
            if (rect == null || !s_owned.ContainsValue(rect)) return;
            try
            {
                FitToElements(rect); UpdateHitBounds(rect);
                // ("the drag handling isn't even in the grid square, you click on the tiny hint text"; log: only presses
                // whose top hit was the bar ROOT started a move): the slot graphics (bkg, icon, durability, amount) caught the press first. A HUD
                // hotbar's slots are display only (items are used by key; inventory drops go through the inventory grid), so none of them takes
                // raycasts: every press on the bar reaches the root and Core's free-body move. The root keeps its raycast Image.
                int off = 0;
                foreach (var g in rect.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                    if (g != null && g.transform != rect && g.raycastTarget) { g.raycastTarget = false; off++; }
                if (off > 0) EquipmentPanel.PanelLog?.LogDebug($"hotbars: {rect.name} {off} slot graphic(s) no longer catch presses (the whole bar drags)");
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"hotbars: refit {rect.name} failed: {e.Message}"); }
        }

        internal static void Release(string barName)
        {
            ReleaseId(BaseId(barName)); ReleaseId(BaseId(barName) + ".classic");   // whichever mode's id holds it
        }

        static void ReleaseId(string id)
        {
            s_regOn.Remove(id);
            if (!s_owned.Remove(id) || s_unregister == null) return;
            try { s_unregister.Invoke(null, new object[] { id }); } catch { }
            EquipmentPanel.PanelLog?.LogDebug($"hotbars: {id} unregistered");
        }

        // New ids, so the spots Core
        // saved under the old ones (some taken mid-screen before the bars registered after the config placement) never load again; each bar
        // registers at its [*Hotbar] Position config spot and Core takes THAT as its default.
        // Each mode keeps its OWN layout: the vanilla-look bar (OFF) and the kit row (ON) are different bars, and one shared id would
        // restore the same spot/scale onto both. ON keeps the old ids (the unified layout stays as saved); OFF uses ".classic" ids, so
        // arranging one mode never moves or resizes the other.
        static string IdOf(string barName) => BaseId(barName) + (OverrideOn() ? "" : ".classic");
        static string BaseId(string barName) => barName == ToolHotBar.barName ? "vainv.hotbar2.tool" : barName == FoodHotBar.barName ? "vainv.hotbar2.food" : barName == AmmoHotBar.barName ? "vainv.hotbar2.ammo" : "vainv.hotbar2." + barName;
    }
}
