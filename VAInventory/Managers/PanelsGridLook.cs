using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.170 (Fire's hand test: "grid squares too dark on a dark background"; the approved look is [visual]'s layout_3panel_v4 mock). Under
    /// the Panels layout every carry-grid and chest-grid cell takes the mock's well: fill RGB 28/22/13 with a 1.5 px outline RGB 73/56/34
    /// (both sampled from layout_3panel_v4_1920.png), so the squares read on the dark plate. Vanilla rebuilds its elements, so this
    /// re-applies a few times a second (a colour compare per cell; writes only when a cell differs). Unified / Legacy are untouched.
    /// </summary>
    internal static class PanelsGridLook
    {
        static readonly Color Fill = new Color(28f / 255f, 22f / 255f, 13f / 255f, 1f);
        static readonly Color Edge = new Color(73f / 255f, 56f / 255f, 34f / 255f, 1f);
        static readonly Vector2 EdgeDistance = new Vector2(1.5f, -1.5f);
        const float KeySize = 13f;
        static readonly Color KeyColor = new Color(0.92f, 0.84f, 0.66f, 0.9f);
        static int s_frame;
        static bool s_logged;

        static bool s_wasOpen;
        static float s_nextRepaintLog;
        static float s_openedAt, s_probe0At = -1f, s_probe6At = -1f;

        internal static void Tick(InventoryGui gui)
        {
            if (gui == null) return;
            ProbeOpen(gui);   // 1.0.186: the probe is back (Debug only, [lead]: read the RENDERED state)
            // 1.0.181: no per-frame re-skin. The original look is set on the open frame (and the first second after it, while vanilla builds
            // the elements), then checked every 10th frame for NEW elements (a rebuild); a restore writes only when a slot differs, and with
            // KitLeaf on each slot Core's skin walk no longer repaints them.
            bool opening = Time.unscaledTime - s_openedAt < 1f;
            ++s_frame;
            if (!opening && (s_frame % 10) != 0) return;
            // 1.0.183 (Fire's final directive): NO slot restyling at all; every slot keeps its own pre-unified look (no palette, no outline,
            // no restore loop). Only the probe + the small layout tweaks below run.
            int n = 0, panelCells = 0;
            // proof that nothing fights the look: a restore AFTER the opening second means something repainted (or rebuilt) slots
            if (!opening && n + panelCells > 0 && Time.unscaledTime >= s_nextRepaintLog)
            {
                s_nextRepaintLog = Time.unscaledTime + 5f;
                EquipmentPanel.PanelLog?.LogDebug($"panels: {n} grid + {panelCells} panel slot(s) re-restored {Time.unscaledTime - s_openedAt:0.0} s after open (rebuilt or repainted)");
            }
            SmallQualityOnCharacterPanel();
            CoinPurseLook();   // 1.0.171
            if (!s_logged && n > 0)
            {
                s_logged = true;
                // point-of-use proof (team rule): what the main grid's first cell actually carries after the restyle
                var first = gui.m_playerGrid != null && gui.m_playerGrid.m_gridRoot != null && gui.m_playerGrid.m_gridRoot.childCount > 0
                    ? gui.m_playerGrid.m_gridRoot.GetChild(0).GetComponent<Image>() : null;
                EquipmentPanel.PanelLog?.LogDebug($"panels: original slots (1.0.181): {n} restore(s); main cell 0 sprite '{first?.sprite?.name}' colour {first?.color} kitLeaf {HasKitLeaf(first)} " +
                    $"edge {first?.GetComponent<Outline>()?.effectColor} canvas alpha {CanvasAlpha(first)}; {panelCells} character-panel cell edge(s)");
            }
        }

        /// <summary>1.0.179: every slot on the character panel gets the faint 1 px gold edge. 1.0.180 (Fire: "the player panel looks shit, the
        /// squares show in one shot and not the other"): and the SAME filled slot face as the grids, so it no longer depends on what is behind
        /// the outline-only kit pill. Slots = every InventoryElement root (equipment rail, Tools / Ammo / Food / Coin purse, Misc, backpacks)
        /// plus the panel's own "Cell_" squares. Returns the slots changed.</summary>
        static int OriginalCharacterPanelSlots()
        {
            var panel = UnifiedScreen.CharacterPanel;
            var model = Original();
            if (panel == null || model == null) return 0;
            int n = 0;
            foreach (var img in panel.GetComponentsInChildren<Image>(false))
            {
                bool slot = img.GetComponent<InventoryElement>() != null || img.name.StartsWith("Cell_", System.StringComparison.Ordinal);
                if (slot && Restore(img, model)) n++;
            }
            if (n > 0 && !s_panelLogged) { s_panelLogged = true; EquipmentPanel.PanelLog?.LogDebug($"panels: player panel slots = the original slot (sprite '{model.sprite?.name}', colour {model.color}) + 1 px faint gold, out of Core's skin walk: {n} slot(s)"); }
            return n;
        }
        static bool s_panelLogged;

        // ---- 1.0.181 (Fire: "we have perfectly working examples if we toggle off the unified UI - copy the originals that looked right and
        // change the colour") -----------------------------------------------------------------------------------------------------------------
        // The ORIGINAL slot = vanilla's grid element prefab (InventoryGrid.m_elementPrefab), exactly what renders with the unified UI off: its
        // sprite, image type / slicing, material and colour, untouched. Every grid cell and player-panel slot takes those, plus ONE change, a
        // 1 px faint kit-gold Outline. Each slot gets Core's KitLeaf marker so CraftingSkin.ApplyTo (it repainted every graphic under the grid
        // panel as Role.Raised: the near-black squares) skips it. A restore writes only when something changed a slot.
        /// <summary>1.0.182: restore every grid cell + player-panel slot to the original right now (called right after Core's skin pass, which
        /// on the first open ran after the open-time restore and repainted the character panel's slots).</summary>
        internal static void RestoreNow(InventoryGui gui, string why)
        {
            if (gui == null) return;
            int n = Apply(gui.m_playerGrid) + Apply(gui.m_containerGrid), p = OriginalCharacterPanelSlots();
            EquipmentPanel.PanelLog?.LogDebug($"panels: slots restored to the original {why}: {n} grid + {p} panel change(s)");
        }

        static Image s_original;
        static System.Type s_kitLeaf;
        static bool s_kitLeafLooked;

        static Image Original()
        {
            if (s_original != null) return s_original;
            var prefab = InventoryGui.instance != null && InventoryGui.instance.m_playerGrid != null ? InventoryGui.instance.m_playerGrid.m_elementPrefab : null;
            s_original = prefab != null ? prefab.GetComponent<Image>() : null;
            if (s_original != null) EquipmentPanel.PanelLog?.LogDebug($"panels: original slot look = vanilla's element prefab: sprite '{s_original.sprite?.name}' type {s_original.type} material '{s_original.material?.name}' colour {s_original.color}");
            return s_original;
        }

        static bool Restore(Image img, Image model)
        {
            bool changed = false;
            if (!s_kitLeafLooked) { s_kitLeafLooked = true; s_kitLeaf = System.Type.GetType("FiresCore.UI.Shell.Kit.KitLeaf, FiresUnifiedCore"); }
            if (s_kitLeaf != null && img.GetComponent(s_kitLeaf) == null) { img.gameObject.AddComponent(s_kitLeaf); changed = true; }
            if (img.sprite != model.sprite) { img.sprite = model.sprite; changed = true; }
            if (img.type != model.type) { img.type = model.type; changed = true; }
            if (img.material != model.material) { img.material = model.material; changed = true; }
            if (img.color != model.color) { img.color = model.color; changed = true; }
            if (VAInvPalette.EdgeOn(img)) changed = true;
            return changed;
        }

        static bool HasKitLeaf(Image img) => s_kitLeaf != null && img != null && img.GetComponent(s_kitLeaf) != null;

        // ---- 1.0.180 proof probe ([lead]: "find WHICH object is visible on top, at the moment of the shot") ----------------------------------
        // At open + 1 frame and open + 6 s: main grid cell 0's Image (sprite, colour, material) and EVERY visible graphic drawn after it that
        // covers its centre (render order = hierarchy order inside a canvas; a nested canvas with a higher sorting order draws later), plus
        // the same for the first Misc / backpack cell on the player panel. Debug lines "panels: probe".
        static void ProbeOpen(InventoryGui gui)
        {
            bool open = InventoryGui.IsVisible();
            if (open && !s_wasOpen) { s_openedAt = Time.unscaledTime; s_probe0At = Time.unscaledTime; s_probe6At = Time.unscaledTime + 6f; }
            s_wasOpen = open;
            if (!open) return;
            if (s_probe0At > 0f && Time.unscaledTime > s_probe0At) { s_probe0At = -1f; Probe(gui, "open+0"); }
            if (s_probe6At > 0f && Time.unscaledTime >= s_probe6At) { s_probe6At = -1f; Probe(gui, "open+6s"); }
        }

        static void Probe(InventoryGui gui, string when)
        {
            try
            {
                var cell = gui.m_playerGrid != null && gui.m_playerGrid.m_gridRoot != null && gui.m_playerGrid.m_gridRoot.childCount > 0
                    ? gui.m_playerGrid.m_gridRoot.GetChild(0).GetComponent<Image>() : null;
                ProbeOne(when, "main cell 0", cell);
                Image panelCell = null;
                var panel = UnifiedScreen.CharacterPanel;
                if (panel != null)
                    foreach (var img in panel.GetComponentsInChildren<Image>(false))
                        if (img.name.StartsWith("Cell_", System.StringComparison.Ordinal)) { panelCell = img; break; }
                ProbeOne(when, "panel cell", panelCell);
                // 1.0.186: an equipment-rail element (renders grey) next to the main grid cell (renders black): same prefab, so compare them
                InventoryElement rail = null;
                if (panel != null) foreach (var el in panel.GetComponentsInChildren<InventoryElement>(false)) { rail = el; break; }
                ProbeOne(when, "rail element", rail != null ? rail.GetComponent<Image>() : null);
                // 1.0.193: a BAG element (the last element on the panel; the bag sections come last) - the one that turned grey on open #3
                InventoryElement bag = null;
                if (panel != null) foreach (var el in panel.GetComponentsInChildren<InventoryElement>(false)) bag = el;
                ProbeOne(when, "bag element", bag != null ? bag.GetComponent<Image>() : null);
                // 1.0.194: the GREY squares themselves (R-1015P shot 194018 at 3440x1440): an empty Misc cell and an empty Explorer Bag cell
                ProbePoint(when, "grey Misc cell", new Vector2(1053f * Screen.width / 3440f, Screen.height - 654f * Screen.height / 1440f));
                ProbePoint(when, "grey bag cell", new Vector2(912f * Screen.width / 3440f, Screen.height - 1015f * Screen.height / 1440f));
                // the black box left of the crafting panel in R-1015I (3440 x 1440 shot: x 2620, y 450 from the top)
                // 1.0.188: the remaining tab ([lead]: x 2590-2675, y 455-535 from the top at 3440x1440): its centre
                ProbePoint(when, "black tab", new Vector2(2632f * Screen.width / 3440f, Screen.height - 495f * Screen.height / 1440f));
            }
            catch (System.Exception e) { EquipmentPanel.PanelLog?.LogDebug($"panels: probe {when} failed: {e.Message}"); }
        }

        static void ProbeOne(string when, string what, Image cell)
        {
            if (cell == null) { EquipmentPanel.PanelLog?.LogDebug($"panels: probe {when} {what}: none"); return; }
            var root = cell.canvas != null ? cell.canvas.rootCanvas : null;
            var c = new Vector3[4]; cell.rectTransform.GetWorldCorners(c);
            Vector3 centre = (c[0] + c[2]) * 0.5f;
            var sb = new System.Text.StringBuilder();
            sb.Append($"panels: probe {when} {what} '{Path(cell.transform, 3)}': sprite '{cell.sprite?.name}' colour {cell.color} rendered {cell.canvasRenderer.GetColor()} x alpha {cell.canvasRenderer.GetAlpha():0.00} selectable {SelectableInfo(cell)} mat '{cell.material?.name}' enabled {cell.enabled} active {cell.gameObject.activeInHierarchy} alpha {CanvasAlpha(cell)} canvas '{cell.canvas?.name}' order {cell.canvas?.sortingOrder}");
            if (root != null)
            {
                var all = root.GetComponentsInChildren<Graphic>(false);
                int at = System.Array.IndexOf(all, cell), over = 0;
                for (int i = 0; i < all.Length && over < 10; i++)
                {
                    var g = all[i];
                    if (g == cell || !g.enabled || g.color.a < 0.02f || CanvasAlpha(g) < 0.02f) continue;
                    var gc = new Vector3[4]; g.rectTransform.GetWorldCorners(gc);
                    if (centre.x < gc[0].x || centre.x > gc[2].x || centre.y < gc[0].y || centre.y > gc[2].y) continue;
                    bool later = g.canvas != cell.canvas ? (g.canvas != null && cell.canvas != null && g.canvas.sortingOrder > cell.canvas.sortingOrder) || (g.canvas != null && cell.canvas != null && g.canvas.sortingOrder == cell.canvas.sortingOrder && i > at) : i > at;
                    if (!later) continue;
                    over++;
                    var img = g as Image;
                    sb.Append($" | OVER {over}: '{Path(g.transform, 4)}' {g.GetType().Name} colour {g.color} sprite '{img?.sprite?.name}' canvas '{g.canvas?.name}' order {g.canvas?.sortingOrder}");
                }
                if (over == 0) sb.Append(" | nothing drawn over it");
            }
            EquipmentPanel.PanelLog?.LogDebug(sb.ToString());
        }

        static string SelectableInfo(Image img)
        {
            var s = img.GetComponent<Selectable>();
            if (s == null) return "none";
            var c = s.colors;
            return $"{s.GetType().Name} target={(s.targetGraphic == img)} transition {s.transition} normal {c.normalColor} mult {c.colorMultiplier:0.00} interactable {s.interactable}";
        }

        /// <summary>1.0.186: every visible graphic under the screen point, in draw order (last = on top).</summary>
        static void ProbePoint(string when, string what, Vector2 screen)
        {
            var sb = new System.Text.StringBuilder($"panels: probe {when} {what} at {screen.x:0},{screen.y:0}:");
            int n = 0;
            foreach (var canvas in Object.FindObjectsOfType<Canvas>())
            {
                if (!canvas.isRootCanvas || !canvas.isActiveAndEnabled) continue;
                Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                foreach (var g in canvas.GetComponentsInChildren<Graphic>(false))
                {
                    if (!g.enabled || g.color.a < 0.02f || CanvasAlpha(g) < 0.02f) continue;
                    if (!RectTransformUtility.RectangleContainsScreenPoint(g.rectTransform, screen, cam)) continue;
                    var r = g.rectTransform.rect;
                    if (r.width * r.height > 900f * 900f) continue;   // skip full-screen backdrops
                    n++;
                    var img = g as Image;
                    sb.Append($" | '{Path(g.transform, 5)}' {g.GetType().Name} colour {g.color} sprite '{img?.sprite?.name}' size {r.width:0}x{r.height:0} canvas '{canvas.name}' order {g.canvas?.sortingOrder}");
                }
            }
            if (n == 0) sb.Append(" nothing");
            EquipmentPanel.PanelLog?.LogDebug(sb.ToString());
        }

        static string Path(Transform t, int depth)
        {
            string p = t.name;
            for (var x = t.parent; x != null && depth-- > 1; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        static float CanvasAlpha(Component c)
        {
            float a = 1f;
            for (var t = c != null ? c.transform : null; t != null; t = t.parent) { var g = t.GetComponent<CanvasGroup>(); if (g != null) a *= g.alpha; }
            return a;
        }

        // [visual]: the backpack side column's blue quality '3' covered its icons; on the character panel the quality digit is small, top-left
        static void SmallQualityOnCharacterPanel()
        {
            var panel = UnifiedScreen.CharacterPanel;
            if (panel == null) return;
            foreach (var t in panel.GetComponentsInChildren<TMPro.TMP_Text>(false))
                if (t.name == "quality" && t.fontSize != QualitySize) { t.enableAutoSizing = false; t.fontSize = QualitySize; }
        }

        const float QualitySize = 13f;

        // 1.0.171 ([lead] call, the _v6 golden): the coin purse ALWAYS shows the coin icon - at ~40% with no count while empty, normal with the
        // count when filled (vanilla's own icon then) - and the currency-cycle "⇄" badge (EquipmentPanel's CurrencyCycle) sits small in the cell's
        // top-RIGHT corner (14 px) instead of over the coin at the top-left. Panels only.
        const string PurseGhost = "VAInventoryPurseGhost";
        static Sprite s_coin;

        static void CoinPurseLook()
        {
            var panel = UnifiedScreen.CharacterPanel;
            if (panel == null) return;
            foreach (var badge in panel.GetComponentsInChildren<RectTransform>(true))
            {
                if (badge.name != "CurrencyCycle") continue;
                if (badge.anchorMin != Vector2.one || badge.sizeDelta.x != 14f)
                {
                    badge.anchorMin = badge.anchorMax = badge.pivot = Vector2.one;
                    badge.anchoredPosition = new Vector2(-1f, -1f); badge.sizeDelta = new Vector2(14f, 14f);
                }
                var element = badge.parent != null ? badge.parent.GetComponent<InventoryElement>() : null;
                if (element == null) continue;
                bool empty = element.m_icon == null || !element.m_icon.enabled;
                var ghost = badge.parent.Find(PurseGhost);
                if (ghost == null && empty)
                {
                    if (s_coin == null) s_coin = ObjectDB.instance?.GetItemPrefab("Coins")?.GetComponent<ItemDrop>()?.m_itemData?.GetIcon();
                    if (s_coin == null) continue;
                    var go = new GameObject(PurseGhost, typeof(RectTransform), typeof(Image));
                    var rt = (RectTransform)go.transform; rt.SetParent(badge.parent, false); rt.SetSiblingIndex(0);
                    rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(8f, 8f); rt.offsetMax = new Vector2(-8f, -8f);
                    var img = go.GetComponent<Image>(); img.sprite = s_coin; img.preserveAspect = true; img.raycastTarget = false; img.color = new Color(1f, 1f, 1f, 0.4f);
                    ghost = rt;
                }
                if (ghost != null && ghost.gameObject.activeSelf != empty) ghost.gameObject.SetActive(empty);
            }
        }

        // 1.0.170 ([lead] for Fire, the v4 mock): each EMPTY equipment rail cell shows its slot's name in grey inside it ('Trinket', 'Back offhand',
        // 'Pet'...); a cell holding an item hides it. Panels only: leaving the layout hides every placeholder.
        const string PlaceholderName = "VAInventoryPlaceholder";
        static readonly Color PlaceholderColor = new Color(0.62f, 0.56f, 0.46f, 0.75f);

        internal static void Placeholders(bool on)
        {
            foreach (var kv in UnifiedScreen.RailCaptions)
            {
                var cell = kv.Key;
                if (cell == null) continue;
                var label = cell.Find(PlaceholderName);
                bool empty = on && !string.IsNullOrEmpty(kv.Value) && cell.gameObject.activeInHierarchy && !HasItemElement(cell);
                if (label == null)
                {
                    if (!empty) continue;
                    var go = new GameObject(PlaceholderName, typeof(RectTransform), typeof(TMPro.TextMeshProUGUI), typeof(LayoutElement));
                    go.GetComponent<LayoutElement>().ignoreLayout = true;
                    var rt = (RectTransform)go.transform; rt.SetParent(cell, false);
                    rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(3f, 3f); rt.offsetMax = new Vector2(-3f, -3f);
                    var t = go.GetComponent<TMPro.TextMeshProUGUI>();
                    var font = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponentInChildren<TMPro.TMP_Text>(true)?.font;
                    if (font != null) t.font = font;
                    t.text = kv.Value; t.fontSize = 11f; t.color = PlaceholderColor; t.alignment = TMPro.TextAlignmentOptions.Center; t.raycastTarget = false;
                    t.textWrappingMode = TMPro.TextWrappingModes.Normal;

                    label = rt;
                }
                if (label.gameObject.activeSelf != empty) label.gameObject.SetActive(empty);
                if (empty && label.GetSiblingIndex() != cell.childCount - 1) label.SetAsLastSibling();   // 1.0.171: over the empty element's own background (it hid the name)
            }
        }

        // a slot's item is drawn as a vanilla InventoryElement child of the cell (EquipmentPanel); an active one = the cell is filled
        static bool HasItemElement(RectTransform cell)
        {
            foreach (var e in cell.GetComponentsInChildren<InventoryElement>(false)) if (e != null && e.m_icon != null && e.m_icon.enabled) return true;
            return false;
        }

        // the first Misc cell's Image on the character panel (UnifiedScreen: RowMisc/Cells/Cell_0), cached per panel
        static Image s_model; static RectTransform s_modelPanel;
        static Image ModelCell()
        {
            var panel = UnifiedScreen.CharacterPanel;
            if (panel == null) return null;
            if (s_model != null && s_modelPanel == panel) return s_model;
            s_modelPanel = panel; s_model = null;
            foreach (var img in panel.GetComponentsInChildren<Image>(true))
                if (img.name.StartsWith("Cell_", System.StringComparison.Ordinal) && img.transform.parent != null && img.transform.parent.name == "Cells"
                    && img.transform.parent.parent != null && img.transform.parent.parent.name.IndexOf("Misc", System.StringComparison.OrdinalIgnoreCase) >= 0)
                { s_model = img; break; }
            EquipmentPanel.PanelLog?.LogDebug(s_model != null ? $"panels: grid cells copy the Misc cell's look (sprite '{s_model.sprite?.name}', colour {s_model.color})" : "panels: no Misc cell to copy; grid cells keep the v4 fill");
            return s_model;
        }

        static int Apply(InventoryGrid grid)
        {
            if (grid == null || grid.m_gridRoot == null || !grid.m_gridRoot.gameObject.activeInHierarchy) return 0;
            int changed = 0;
            // 1.0.181 (Fire): the ORIGINAL slot (vanilla's element prefab, as with the unified UI off) + 1 px faint gold, out of Core's skin walk.
            // Replaces 1.0.171's kit-pill copy and 1.0.179's palette sprite.
            var model = Original();
            if (model == null) return 0;
            foreach (Transform cell in grid.m_gridRoot)
            {
                var img = cell.GetComponent<Image>();
                if (img == null) continue;
                if (Restore(img, model)) changed++;
                // [visual]'s side-by-side: the 1-8 key numbers came out large and white over the cells; the mock has small warm digits in the corner
                var binding = cell.Find("binding")?.GetComponent<TMPro.TMP_Text>();
                if (binding != null && (binding.fontSize != KeySize || binding.color != KeyColor)) { binding.enableAutoSizing = false; binding.fontSize = KeySize; binding.color = KeyColor; }
            }
            return changed;
        }
    }
}
