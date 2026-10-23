using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.161 (Fire's Panels asks, 10-15, via [lead]). Panels layout only; Unified / Legacy never see any of it. NOTHING VANILLA IS MOVED:
    /// the vanilla pieces are hidden (CanvasGroup alpha 0, no raycasts) and OUR mirrors stand in for them, invoking the original
    /// Button.onClick / setting the original Toggle.isOn, so vanilla's and every other mod's listeners still run. Leaving Panels restores
    /// everything (the CanvasGroups go, the mirrors hide).
    ///   1 armour + weight   the grid's right-edge tabs -> two display slots beside the preview's head (armour left, weight right)
    ///   2 repair            crafting's left-edge repair button -> m_crafting/VAInventoryPanelsRepair inside the panel (glow mirrored)
    ///   3 header            the player's name over the character panel (vanilla's m_playerName hidden)
    ///   4 info row          the info panel's buttons + PvP toggle -> m_crafting/VAInventoryPanelsInfoRow; the info panel then hides
    ///                       (PanelsDock gives vainv.info Visible = !InfoMoved, so crafting re-docks through it)
    ///   5 trash             a trashcan right of the preview's feet: click it while dragging an item -> "Destroy <item> xN?" -> the
    ///                       stack is dropped as a real ItemDrop and its ZDO destroyed through the owner path (networked, like removedrops)
    /// [saddles]' CraftingSkin styles everything under m_crafting (sprite / colour / material only).
    /// </summary>
    internal static class PanelsChrome
    {
        const string RepairName = "VAInventoryPanelsRepair", RowName = "VAInventoryPanelsInfoRow", HeaderName = "VAInventoryPanelsHeader";
        const string ArmorName = "VAInventoryPanelsArmor", WeightName = "VAInventoryPanelsWeight", TrashName = "VAInventoryPanelsTrash";
        // 1.0.170 [classes-ui] (Fire's hand test 2 vs layout_3panel_v4_1920): crafting gets a real title row; "Title" in the name = Core's
        // CraftingSkin colours it Gold (Heading); "NoKitSkin" opts the hairline out of the skin.
        const string TitleName = "VAInventoryPanelsTitle", TitleLineName = "VAInventoryPanelsTitleLine_NoKitSkin";
        static readonly Color MockGold = new Color(0.941f, 0.706f, 0.298f), MockMuted = new Color(0.702f, 0.631f, 0.506f), MockText = new Color(0.937f, 0.886f, 0.769f);
        static readonly Color MockHairline = new Color(0.29f, 0.22f, 0.14f);
        const float Slot = 48f, Small = 40f, InfoIcon = 30f, TopRow = 32f, TopPad = 8f, TitleH = 30f;   // 1.0.172: Fire's order = button row, then title, then tabs   // 1.0.171: the info icons sit in crafting's footer at 30   // 1.0.167: the stat boxes a little smaller (Fire's shot), the info icons larger

        static readonly List<CanvasGroup> s_hidden = new List<CanvasGroup>();
        static readonly List<GameObject> s_ours = new List<GameObject>();
        static TMP_Text s_armorText, s_weightText, s_header, s_title;
        static Image s_repairGlow, s_repairIcon; static Button s_repair;
        static System.Reflection.FieldInfo s_vanillaGlow;   // 1.0.214
        static Toggle s_pvpMirror;
        static bool s_active;
        internal static bool InfoMoved { get; private set; }

        internal static void Tick(InventoryGui gui, RectTransform character)
        {
            bool want = UnifiedScreen.PanelsMode && gui != null && character != null && InventoryGui.IsVisible();
            if (!want) { if (s_active && !UnifiedScreen.PanelsMode) Restore(); return; }
            if (!s_active) Build(gui, character);
            Mirror(gui);
            // 1.0.225 (Fire, r167_vanilla_info_panel: vanilla's info panel - texts, trophies, the variant squares, PvP - showed at the top
            // centre): hidden once at build, something put its CanvasGroup back; it is now held hidden every frame while Panels shows
            if (InfoMoved && gui.m_info != null)
            {
                var cg = gui.m_info.GetComponent<CanvasGroup>() ?? gui.m_info.gameObject.AddComponent<CanvasGroup>();
                if (cg.alpha != 0f || cg.blocksRaycasts || cg.interactable)
                {
                    cg.alpha = 0f; cg.blocksRaycasts = false; cg.interactable = false;
                    if (!s_infoReLogged) { s_infoReLogged = true; EquipmentPanel.PanelLog?.LogDebug("panels: vanilla info panel was shown again by something else; hidden again (held every frame under Panels)"); }
                }
            }
        }

        static void Build(InventoryGui gui, RectTransform character)
        {
            s_active = true;
            var font = gui.m_armor != null ? gui.m_armor.font : null;
            var cellSprite = gui.m_playerGrid?.m_elementPrefab?.GetComponent<Image>()?.sprite;
            var preview = FindDeep(character, "PreviewMount") as RectTransform;
            var anchor = preview != null ? preview : character;

            // 1 armour + weight
            if (gui.m_armor != null && gui.m_weight != null)
            {
                // 1.0.170 [classes-ui] (mock v4): plain labels in the GRID panel's footer, "Armor 73.75" bottom-left, "Weight 142 / 300"
                // bottom-right of InventoryGui.m_player; the two stat boxes are gone.
                // 1.0.171 (night shot: drawn over the grid's middle row): under the GRID itself (m_playerGrid), not m_player's bottom
                // 1.0.180 (Fire 10-15, shot 0886e386: still drawn over the grid's 6th row; the grid's own transform is a scroll view taller than its
                // cells): OFF the grid entirely. In the PLAYER panel, either side of the character's head: Armor LEFT, Weight RIGHT.
                s_armorText = MakeFooter(ArmorName, anchor, true, font);
                s_weightText = MakeFooter(WeightName, anchor, false, font);
                s_playerVitals = MakeVitals(s_weightText, "PlayerVital");   // 1.0.313
                if (s_playerVitals != null) foreach (var v in s_playerVitals) if (v != null) s_ours.Add(v.gameObject);
                s_statsGrid = gui.m_playerGrid != null ? gui.m_playerGrid.m_gridRoot : null;
                EquipmentPanel.PanelLog?.LogDebug($"panels: armour / weight beside the head on '{anchor.name}' (player panel), not on the grid");
                Hide(TabOf(gui.m_armor)); Hide(TabOf(gui.m_weight));
            }

            // 3 header
            {
                // 1.0.170 [classes-ui] (mock v4): the name centred at the TOP of the preview plate, full size, Gold serif, no box
                var go = new GameObject(HeaderName, typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
                go.GetComponent<LayoutElement>().ignoreLayout = true;
                var prt = (RectTransform)go.transform; prt.SetParent(character, false); prt.SetAsLastSibling();   // 1.0.171: the panel's top band ([seasons]: +48 px top padding), above the preview
                prt.anchorMin = new Vector2(0f, 1f); prt.anchorMax = new Vector2(1f, 1f); prt.pivot = new Vector2(0.5f, 1f);
                prt.sizeDelta = new Vector2(-12f, 40f); prt.anchoredPosition = new Vector2(0f, -6f);
                s_header = go.GetComponent<TextMeshProUGUI>();
                var nameFont = gui.m_playerName != null ? gui.m_playerName.font : font;
                if (nameFont != null) s_header.font = nameFont;
                s_header.fontSize = 28f; s_header.fontStyle = FontStyles.Bold; s_header.alignment = TextAlignmentOptions.Center; s_header.color = MockGold;
                s_header.enableWordWrapping = false; s_header.overflowMode = TextOverflowModes.Ellipsis; s_header.raycastTarget = false;
                s_ours.Add(go);
                if (gui.m_playerName != null) Hide(gui.m_playerName.gameObject);
            }

            // 2 repair, inside crafting's top-left corner
            if (gui.m_repairButton != null && gui.m_crafting != null)
            {
                var b = MakeButton(RepairName, gui.m_crafting, VanillaRepairIcon(gui), cellSprite, () =>
                {
                    // 1.0.172 (Fire: "the repair button still broken"): one proof line per press; vanilla only repairs at a station in range
                    Debug.Log($"[VAInventory] panels: repair pressed -> vanilla OnRepairPressed (interactable {gui.m_repairButton.interactable})");
                    gui.m_repairButton.onClick.Invoke();
                });
                var rt = (RectTransform)b.transform; rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f); rt.sizeDelta = new Vector2(TopRow, TopRow); rt.anchoredPosition = new Vector2(12f, -TopPad);
                s_repairIcon = b.transform.Find("Icon")?.GetComponent<Image>();
                EquipmentPanel.PanelLog?.LogDebug($"panels: repair mirror icon {(s_repairIcon != null && s_repairIcon.sprite != null ? s_repairIcon.sprite.name : "none")}");
                var glow = new GameObject("Glow", typeof(RectTransform), typeof(Image));
                glow.transform.SetParent(rt, false); Stretch((RectTransform)glow.transform, -4f);
                s_repairGlow = glow.GetComponent<Image>();
                // 1.0.171 (night shot: a white square): vanilla's glow sprite stretched over our button read as a white box; a soft gold tint instead
                s_repairGlow.sprite = null; s_repairGlow.color = new Color(0.94f, 0.71f, 0.30f, 0.35f);
                s_repairGlow.raycastTarget = false; glow.transform.SetAsFirstSibling();
                s_repair = b;
                if (gui.m_repairPanel != null) Hide(gui.m_repairPanel.gameObject);
            }

            // 4 info row along crafting's bottom edge, then the info panel hides
            if (gui.m_info != null && gui.m_crafting != null)
            {
                var row = new GameObject(RowName, typeof(RectTransform), typeof(HorizontalLayoutGroup));
                var rrt = (RectTransform)row.transform; rrt.SetParent(gui.m_crafting, false);
                // 1.0.170 [classes-ui] (Fire: the info buttons as a real header row INSIDE crafting, with padding): the right side of the
                // title row, top-right corner inset 12; the repair button holds the top-left. No plate of its own (the panel is the plate).
                // 1.0.171 (night shot: 5 x 40 px icons covered the CRAFTING title at this panel width): crafting's bottom-left FOOTER
                // 1.0.172 (Fire, R-NIGHT1: "in the header with the buttons and then the header text under them"): the TOP row, after repair
                rrt.anchorMin = rrt.anchorMax = new Vector2(0f, 1f); rrt.pivot = new Vector2(0f, 1f);
                rrt.sizeDelta = new Vector2(0f, TopRow); rrt.anchoredPosition = new Vector2(12f + TopRow + 8f, -TopPad);
                var fit = row.AddComponent<ContentSizeFitter>(); fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                var h = row.GetComponent<HorizontalLayoutGroup>(); h.padding = new RectOffset(0, 0, 0, 0); h.spacing = 8f; h.childAlignment = TextAnchor.MiddleLeft; h.childControlWidth = false; h.childControlHeight = false; h.childForceExpandWidth = false;
                s_ours.Add(row);
                int n = 0;
                foreach (var btn in gui.m_info.GetComponentsInChildren<Button>(true))
                {
                    if (!btn.gameObject.activeSelf) continue;
                    var src = btn;
                    var m = MakeButton("Btn_" + btn.name, rrt, IconOf(btn), cellSprite, () =>
                    {
                        if (src == null || !src.interactable) return;
                        if (OpenOurs(src)) return;   // 1.0.211 (Fire): Skills / Trophies open OUR windows; vanilla only without our mods
                        src.onClick.Invoke();
                    });
                    ((RectTransform)m.transform).sizeDelta = new Vector2(TopRow, TopRow); n++;
                }
                if (gui.m_pvp != null)
                {
                    var t = MakeButton("Tgl_PvP", rrt, IconOf(gui.m_pvp), cellSprite, () => { if (gui.m_pvp.interactable) gui.m_pvp.isOn = !gui.m_pvp.isOn; });
                    ((RectTransform)t.transform).sizeDelta = new Vector2(TopRow, TopRow);
                    var check = new GameObject("Check", typeof(RectTransform), typeof(Image));
                    check.transform.SetParent(t.transform, false); Stretch((RectTransform)check.transform, 6f);
                    var ci = check.GetComponent<Image>(); ci.color = new Color(0.9f, 0.3f, 0.2f, 0.6f); ci.raycastTarget = false;
                    s_pvpCheck = ci; n++;
                }
                Hide(gui.m_info.gameObject);
                InfoMoved = true;
                BuildCraftingTitle(gui, font);
                EquipmentPanel.PanelLog?.LogDebug($"panels: info panel's {n} button(s) mirrored onto crafting; info panel hidden");
            }

            // 5 trash (1.0.162, Fire's render fire_trashcan_ref.png): a regular equipment-style CELL, same size and face as the panel's
            // slot cells, with a trash-can icon, in the preview's bottom-right corner (right of the feet, just above the Coin purse row).
            // A drop target the vanilla way: click it while carrying an item (InventoryGui's drag) -> confirm -> networked destroy.
            {
                Vector2 cellSize = SlotCellSize(character);
                var b = MakeButton(TrashName, anchor, null, cellSprite, OnTrash);
                var rt = (RectTransform)b.transform; rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
                rt.sizeDelta = cellSize; rt.anchoredPosition = new Vector2(-4f, 4f);
                // 1.0.284 (Fire 10-21 V5, shot 20261021_0040/16: "the trashcan shows a white box"): an Image with no sprite draws WHITE. With
                // Unified UI off there is no pouch-cell sprite, so the cell face is the kit's dark slot colour instead; and the icon (borrowed
                // from the start screen) is only used while it is still alive - a destroyed one falls back to the red ×.
                var cellImg = b.GetComponent<Image>();
                if (cellImg != null && cellImg.sprite == null) cellImg.color = new Color(0.227f, 0.165f, 0.071f, 1f);   // FiresTokens.SlotFace #3A2A12
                // 1.0.290 (Fire 10-21 non-unified round, shots 20261021_0120_nonunified/3,4: "trashcan is still white; when clicked it faintly
                // shows"): the cell HAS a sprite but keeps MakeButton's white colour - with Unified ON Core's kit skin tints it like every slot,
                // with it OFF nothing does, so the white face covered the icon. TrashLook gives it the Tools pouch cell's look (UnifiedScreen.
                // ApplyLook, what every slot face uses) on each enable, ONLY while the override is OFF - Unified is left exactly as it is.
                b.gameObject.AddComponent<TrashLook>();
                var icon = TrashSprite();
                if (icon != null && (icon.texture == null)) icon = null;
                if (icon != null)
                {
                    var ic = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                    ic.transform.SetParent(rt, false); Stretch((RectTransform)ic.transform, cellSize.x * 0.18f);
                    var ii = ic.GetComponent<Image>(); ii.sprite = icon; ii.preserveAspect = true; ii.raycastTarget = false;
                }
                else
                {
                    var lbl = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                    lbl.transform.SetParent(rt, false); Stretch((RectTransform)lbl.transform, 0f);
                    var t = lbl.GetComponent<TextMeshProUGUI>(); if (font != null) t.font = font;
                    t.text = "×"; t.fontSize = cellSize.y * 0.55f; t.alignment = TextAlignmentOptions.Center; t.color = new Color(0.9f, 0.35f, 0.3f); t.raycastTarget = false;   // Latin-1
                }
                EquipmentPanel.PanelLog?.LogDebug($"panels: trash cell {cellSize.x:0}x{cellSize.y:0}, icon {(icon != null ? icon.name : "none (× fallback)")}");
            }            EquipmentPanel.PanelLog?.LogDebug("panels: chrome built (armour/weight slots, header, repair, info row, trash)");
        }

        static Image s_pvpCheck;
        static int s_recipeCount = -1;

        // ── 1.0.313 (Fire 10-22: "the players should get a health stam eitr display like companions have under the weight"): ONE
        // implementation for both panels - three clones of the Weight label stacked under it (22 px apart), "HP" red, "Stamina" yellow,
        // "Eitr" purple, label muted like Armor / Weight; a pool with no max (e.g. 0 eitr) shows nothing. CompanionPanel uses the same two. ──
        internal static TMP_Text[] MakeVitals(TMP_Text weightText, string namePrefix)
        {
            if (weightText == null) return null;
            var vitals = new TMP_Text[3];
            for (int i = 0; i < 3; i++)
            {
                var go = UnityEngine.Object.Instantiate(weightText.gameObject, weightText.transform.parent, false);
                go.name = namePrefix + i;
                var rt = (RectTransform)go.transform;
                rt.anchoredPosition = ((RectTransform)weightText.transform).anchoredPosition + new Vector2(0f, -22f * (i + 1));
                vitals[i] = go.GetComponent<TMP_Text>(); vitals[i].text = "";
            }
            return vitals;
        }

        internal static void SetVitals(TMP_Text[] vitals, float hp, float maxHp, float sta, float maxSta, float eitr, float maxEitr)
        {
            if (vitals == null || vitals.Length < 3) return;
            Vital(vitals[0], "HP", "#E5533F", hp, maxHp); Vital(vitals[1], "Stamina", "#E8C547", sta, maxSta); Vital(vitals[2], "Eitr", "#B07CFF", eitr, maxEitr);
        }

        static void Vital(TMP_Text t, string label, string hex, float v, float max)
        {
            if (t == null) return;
            string s = max > 0f ? $"<color=#9C8F78>{label}</color>  <color={hex}>{v:0}</color>" : "";
            if (t.text != s) t.text = s;
        }

        static TMP_Text[] s_playerVitals;

        static void Mirror(InventoryGui gui)
        {
            if (s_armorText != null && gui.m_armor != null) SetFooter(s_armorText, "Armor", gui.m_armor.text);
            if (s_weightText != null && gui.m_weight != null) SetFooter(s_weightText, "Weight", gui.m_weight.text);
            // 1.0.313: the local player's HP / Stamina / Eitr under the player panel's Weight (current values, as the companion panel shows)
            var me = Player.m_localPlayer;
            if (s_playerVitals != null && me != null) SetVitals(s_playerVitals, me.GetHealth(), me.GetMaxHealth(), me.GetStamina(), me.GetMaxStamina(), me.GetEitr(), me.GetMaxEitr());
            CheckStatsOffGrid();   // 1.0.180
            if (s_header != null)
            {
                string name = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : (gui.m_playerName != null ? gui.m_playerName.text : "");
                if (s_header.text != name) s_header.text = name;
            }
            if (s_repair != null && gui.m_repairButton != null)
            {
                bool can = gui.m_repairButton.interactable;
                s_repair.interactable = can;
                if (s_repairIcon != null) s_repairIcon.color = can ? Color.white : new Color(0.55f, 0.52f, 0.47f, 0.45f);   // greyed, never white, with no station
                // 1.0.214 (Fire: the hammer was ALWAYS highlighted): the gold glow mirrors vanilla's own m_repairButtonGlow (on only when
                // something is repairable at this station) AND the button being interactable; never a fixed tint
                if (s_repairGlow != null)
                {
                    s_vanillaGlow ??= AccessTools.Field(typeof(InventoryGui), "m_repairButtonGlow");
                    var vg = s_vanillaGlow?.GetValue(gui) as Component;
                    bool glow = can && vg != null && vg.gameObject.activeInHierarchy && (!(vg is Behaviour b) || b.enabled);
                    if (s_repairGlow.enabled != glow) { s_repairGlow.enabled = glow; EquipmentPanel.PanelLog?.LogDebug($"panels: repair glow {(glow ? "ON (something to repair)" : "off")}"); }
                }
                var face = s_repair.GetComponent<Image>();   // the button face never sits in a 'selected' state
                if (face != null && s_repair.transition == Selectable.Transition.ColorTint) { var cb = s_repair.colors; if (cb.selectedColor != cb.normalColor) { cb.selectedColor = cb.normalColor; s_repair.colors = cb; } }
            }
            // 1.0.233 (Fire: wrench ALWAYS gold): removed the line that copied vanilla glow Image.enabled here every frame - that Image stays enabled while its object is inactive, so it overrode the glow rule above
            if (s_pvpCheck != null && gui.m_pvp != null) s_pvpCheck.enabled = gui.m_pvp.isOn;
            // 1.0.171 (night shot: "x20" wrapped onto a second line): recipe names stay on one line, ellipsis when too long; flags only
            var list = gui.m_recipeListRoot;
            if (list != null && list.childCount != s_recipeCount)
            {
                s_recipeCount = list.childCount;
                foreach (var tx in list.GetComponentsInChildren<TMP_Text>(true)) { tx.enableWordWrapping = false; tx.overflowMode = TextOverflowModes.Ellipsis; }
            }
        }

        // ── trash ──
        static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> DragItem = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_dragItem");
        static readonly AccessTools.FieldRef<InventoryGui, Inventory> DragInv = AccessTools.FieldRefAccess<InventoryGui, Inventory>("m_dragInventory");
        static readonly AccessTools.FieldRef<InventoryGui, int> DragAmount = AccessTools.FieldRefAccess<InventoryGui, int>("m_dragAmount");
        static readonly System.Reflection.MethodInfo SetupDrag = AccessTools.Method(typeof(InventoryGui), "SetupDragItem");

        static void OnTrash()
        {
            var gui = InventoryGui.instance; var me = Player.m_localPlayer;
            if (gui == null || me == null) return;
            var item = DragItem(gui); var inv = DragInv(gui); int amount = Mathf.Max(1, DragAmount(gui));
            if (item == null || inv == null) { EquipmentPanel.PanelLog?.LogDebug("trash: clicked with nothing dragged"); return; }
            amount = Mathf.Min(amount, item.m_stack);
            string label = Localization.instance != null ? Localization.instance.Localize(item.m_shared.m_name) : item.m_shared.m_name;
            // 1.0.212 (Fire: the Destroy dialog drew UNDER our panel, and Yes / No couldn't be clicked with the stack still on the cursor):
            // (b) the drag is HELD (cleared, as vanilla does before its own confirm) while the dialog is up; No puts it back on the cursor;
            // (a) vanilla's UnifiedPopup gets its own canvas sorted above every Panels canvas while it is shown
            try { SetupDrag?.Invoke(gui, new object[] { null, null, 1 }); } catch { }
            UnifiedPopup.Push(new YesNoPopup("Destroy", $"Destroy {label} x{amount}?", () =>
            {
                UnifiedPopup.Pop();
                Destroy(gui, me, inv, item, amount, label);
            }, () =>
            {
                UnifiedPopup.Pop();
                try { if (inv.ContainsItem(item)) SetupDrag?.Invoke(gui, new object[] { item, inv, amount }); } catch { }
                EquipmentPanel.PanelLog?.LogDebug($"trash: No: {label} x{amount} back on the cursor");
            }, false));
            int order = RaisePopup();
            EquipmentPanel.PanelLog?.LogDebug($"trash: confirm for {label} x{amount}: drag held (cursor clear), vanilla Destroy dialog raised to sorting order {order}");
        }

        // vanilla's UnifiedPopup above every Panels canvas: its own override-sorting Canvas (+ a raycaster so Yes / No take clicks)
        const int PopupOrder = 32000;
        static int RaisePopup()
        {
            var popup = UnityEngine.Object.FindObjectOfType<UnifiedPopup>();
            if (popup == null) return -1;
            var go = popup.gameObject;
            var canvas = go.GetComponent<Canvas>() ?? go.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = PopupOrder;
            if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();
            return canvas.sortingOrder;
        }

        static void Destroy(InventoryGui gui, Player me, Inventory inv, ItemDrop.ItemData item, int amount, string label)
        {
            try
            {
                if (!inv.ContainsItem(item)) { EquipmentPanel.PanelLog?.LogWarning($"trash: {label} is no longer in that inventory; nothing destroyed"); return; }
                if (item.m_equipped) me.UnequipItem(item, false);
                var clone = item.Clone(); clone.m_stack = amount;
                if (amount >= item.m_stack) inv.RemoveItem(item); else inv.RemoveItem(item, amount);
                try { SetupDrag?.Invoke(gui, new object[] { null, null, 1 }); } catch { }
                // the same end as dropping it and running removedrops: a real ItemDrop whose ZDO is destroyed through its owner (us)
                var drop = clone.m_dropPrefab != null ? ItemDrop.DropItem(clone, amount, me.transform.position + Vector3.up, Quaternion.identity) : null;
                var nview = drop != null ? drop.GetComponent<ZNetView>() : null;
                if (nview != null && nview.IsValid()) { nview.ClaimOwnership(); nview.Destroy(); }
                Debug.Log($"[VAInventory] trash: destroyed {label} x{amount}");
            }
            catch (Exception e) { Debug.LogWarning($"[VAInventory] trash: failed for {label}: {e.Message}"); }
        }

        // the size of the panel's own slot cells (the first rail cell), so the trash matches them exactly
        static Vector2 SlotCellSize(RectTransform character)
        {
            var rail = FindDeep(character, "RailLeft") ?? FindDeep(character, "RailRight");
            if (rail != null)
                foreach (var img in rail.GetComponentsInChildren<Image>(true))
                {
                    var r = ((RectTransform)img.transform).rect;
                    if (img.transform != rail && r.width >= 30f && r.width <= 120f && Mathf.Abs(r.width - r.height) < 4f) return r.size;
                }
            float s = InventoryGui.instance?.m_playerGrid != null ? InventoryGui.instance.m_playerGrid.m_elementSpace - 4f : 64f;
            return new Vector2(s, s);
        }

        // a trash-can icon from what the game has loaded (vanilla and mods ship one under a few names); null = the × fallback
        static Sprite s_trash; static bool s_trashLooked;
        // 1.0.163 (Fire, via [lead]: "vanilla already has a trash-can sprite, the one on the world-select screen's DELETE button"): the icon
        // on FejdStartup's world / character delete button, captured while the start scene is up (FejdStartupDeleteIcon below) and held
        // so it outlives the scene. Fallbacks: a loaded sprite of the same name, then the old name search.
        internal static Sprite s_menuTrash;
        internal static string s_menuTrashName;

        static Sprite TrashSprite()
        {
            if (s_menuTrash != null && s_menuTrash.texture != null) return s_menuTrash;   // 1.0.284: a start-screen sprite can be gone after the scene unloads
            if (s_trashLooked) return s_trash;
            s_trashLooked = true;
            foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sp == null) continue;
                if (s_menuTrashName != null && sp.name == s_menuTrashName) { s_trash = sp; return s_trash; }
            }
            return s_trash;   // 1.0.166: no name search (it found a rubble pile); the cell shows × instead
        }

        // ── helpers ──
        static TMP_Text MakeSlot(string name, RectTransform parent, Vector2 anchor, Vector2 offset, Sprite cell, Sprite icon, TMP_FontAsset font)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor; rt.pivot = new Vector2(0f, 1f); rt.sizeDelta = new Vector2(Slot, Slot); rt.anchoredPosition = offset;
            var img = go.GetComponent<Image>(); img.sprite = cell; img.type = Image.Type.Sliced; img.raycastTarget = false;
            if (icon != null)
            {
                var ic = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                ic.transform.SetParent(rt, false); var irt = (RectTransform)ic.transform; irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.62f); irt.sizeDelta = new Vector2(26f, 26f);
                var ii = ic.GetComponent<Image>(); ii.sprite = icon; ii.preserveAspect = true; ii.raycastTarget = false;
            }
            var tx = new GameObject("Value", typeof(RectTransform), typeof(TextMeshProUGUI));
            tx.transform.SetParent(rt, false); var trt = (RectTransform)tx.transform; trt.anchorMin = new Vector2(0f, 0f); trt.anchorMax = new Vector2(1f, 0.4f); trt.offsetMin = trt.offsetMax = Vector2.zero;
            var t = tx.GetComponent<TextMeshProUGUI>(); if (font != null) t.font = font; t.fontSize = 15f; t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false; t.enableWordWrapping = false;
            s_ours.Add(go);
            return t;
        }

        static Button MakeButton(string name, Transform parent, Sprite icon, Sprite cell, Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>(); img.sprite = cell; img.type = Image.Type.Sliced;
            var b = go.GetComponent<Button>(); b.targetGraphic = img; b.onClick.AddListener(() => { try { onClick(); } catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"panels: {name} click threw {e.Message}"); } });
            if (icon != null)
            {
                var ic = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                ic.transform.SetParent(go.transform, false); Stretch((RectTransform)ic.transform, 5f);
                var ii = ic.GetComponent<Image>(); ii.sprite = icon; ii.preserveAspect = true; ii.raycastTarget = false;
            }
            s_ours.Add(go);
            return b;
        }

        // 1.0.170 [classes-ui]: crafting's title row (mock v4): "CRAFTING" centred, tracked, Gold serif; a hairline under it. Vanilla's
        // "topic" text (same spot) hides. The repair button (left) and the info row (right) share the row.
        static void BuildCraftingTitle(InventoryGui gui, TMP_FontAsset fallback)
        {
            var crafting = gui.m_crafting;
            if (crafting == null) return;
            var topic = crafting.Find("topic")?.GetComponent<TMP_Text>();
            var go = new GameObject(TitleName, typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var rt = (RectTransform)go.transform; rt.SetParent(crafting, false);
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(-24f, TitleH); rt.anchoredPosition = new Vector2(0f, -(TopPad + TopRow + 6f));   // 1.0.172: under the button row
            s_title = go.GetComponent<TextMeshProUGUI>();
            var f = topic != null ? topic.font : fallback;
            if (f != null) s_title.font = f;
            s_title.text = "Crafting"; s_title.fontStyle = FontStyles.UpperCase | FontStyles.Bold; s_title.characterSpacing = 8f;
            s_title.fontSize = 24f; s_title.alignment = TextAlignmentOptions.Center; s_title.color = MockGold;
            s_title.enableWordWrapping = false; s_title.raycastTarget = false;
            s_ours.Add(go);
            var line = new GameObject(TitleLineName, typeof(RectTransform), typeof(Image));
            var lrt = (RectTransform)line.transform; lrt.SetParent(crafting, false);
            lrt.anchorMin = new Vector2(0f, 1f); lrt.anchorMax = new Vector2(1f, 1f); lrt.pivot = new Vector2(0.5f, 1f);
            lrt.sizeDelta = new Vector2(-48f, 1f); lrt.anchoredPosition = new Vector2(0f, -(TopPad + TopRow + 6f + TitleH + 3f));
            var li = line.GetComponent<Image>(); li.color = MockHairline; li.raycastTarget = false;
            s_ours.Add(line);
            if (topic != null) Hide(topic.gameObject);
            EquipmentPanel.PanelLog?.LogDebug($"panels: crafting title row built (title + hairline; vanilla topic {(topic != null ? "hidden" : "not found")})");
        }

        static TMP_Text s_footerArmor, s_footerWeight;

        // a footer label in the grid panel: bottom-left (armour) or bottom-right (weight), inset 16 / 10
        static TMP_Text MakeFooter(string name, RectTransform parent, bool left, TMP_FontAsset font)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var rt = (RectTransform)go.transform; rt.SetParent(parent, false); rt.SetAsLastSibling();
            float x = left ? 0f : 1f;
            // 1.0.180: the top corners of the character preview, level with the head (the name band sits above the preview)
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(x, 1f);
            rt.sizeDelta = new Vector2(150f, 26f); rt.anchoredPosition = new Vector2(left ? 6f : -6f, -HeadDrop);
            var t = go.GetComponent<TextMeshProUGUI>(); if (font != null) t.font = font;
            t.fontSize = 16f; t.alignment = left ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.MidlineRight; t.color = MockText;
            t.enableWordWrapping = false; t.raycastTarget = false; t.richText = true;
            s_ours.Add(go);
            if (left) s_footerArmor = t; else s_footerWeight = t;
            return t;
        }

        // "Armor  73.75" / "Weight  142 / 300": the label muted, the value as vanilla wrote it (its own colour tags kept), "/" spaced
        const float HeadDrop = 26f;   // 1.0.180: below the preview's top edge, beside the head
        static Transform s_statsGrid;
        static float s_nextStatsCheck;

        /// <summary>1.0.180: Armor / Weight must never overlap the inventory grid's cells (Fire's 10-15 shot). Checked twice a second; a hit is
        /// logged as a warning with both rects, once per layout.</summary>
        static void CheckStatsOffGrid()
        {
            if (Time.unscaledTime < s_nextStatsCheck || s_statsGrid == null) return;
            s_nextStatsCheck = Time.unscaledTime + 0.5f;
            Rect grid = WorldRect(s_statsGrid as RectTransform);
            foreach (var t in new[] { s_armorText, s_weightText })
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                Rect r = WorldRect(t.rectTransform);
                bool hit = r.Overlaps(grid);
                string key = t.name + hit + r.position;
                if (key == s_lastStatsKey(t)) continue;
                s_statsKeys[t] = key;
                if (hit) EquipmentPanel.PanelLog?.LogWarning($"panels: {t.name} OVERLAPS the inventory grid: label {r} grid {grid}");
                else EquipmentPanel.PanelLog?.LogDebug($"panels: {t.name} clear of the grid: label {r} grid {grid}");
            }
        }
        static readonly System.Collections.Generic.Dictionary<TMP_Text, string> s_statsKeys = new System.Collections.Generic.Dictionary<TMP_Text, string>();
        static string s_lastStatsKey(TMP_Text t) => s_statsKeys.TryGetValue(t, out var k) ? k : null;

        static Rect WorldRect(RectTransform rt)
        {
            if (rt == null) return new Rect();
            var c = new Vector3[4]; rt.GetWorldCorners(c);
            return Rect.MinMaxRect(c[0].x, c[0].y, c[2].x, c[2].y);
        }

        static void SetFooter(TMP_Text t, string label, string value)
        {
            // 1.0.316 (Fire 10-22 shot weight_color_tag_broken: "Weight 536</ color> / 504"): vanilla wraps an over-max weight in
            // <color=red>..</color>; spacing EVERY "/" also split the closing tag. Only the separator slash is spaced now, never "</".
            string v = (value ?? "").Replace("</", "\u0001").Replace("/", " / ").Replace("  /  ", " / ").Replace("\u0001", "</");
            string want = "<color=#B3A181>" + label + "</color>  " + v;
            if (t.text != want) t.text = want;
        }

        static void Stretch(RectTransform rt, float inset) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset); }

        // the tab a stat text sits on (its parent Image), else the text itself
        static GameObject TabOf(TMP_Text t) => t.transform.parent != null && t.transform.parent.GetComponent<Image>() != null ? t.transform.parent.gameObject : t.gameObject;

        // a vanilla control's icon: the first child Image with a sprite that isn't the control's own background
        // 1.0.210 (Fire: "the top-left button in crafting is NOT the vanilla repair sprite"; the log said 'repair mirror icon
        // FiresKit_roundPill_6_False'): Core's skin had already replaced the vanilla button's icon when it was read. A kit sprite read there
        // falls back to vanilla's own loaded repair sprite, by name.
        // 1.0.211 (Fire: the crafting icon row's TROPHY and SKILLS buttons must open OUR UI): Skills -> RPGClasses' Classes window on its
        // Skills tab (ClassesPanel.Instance.Open(ClassesTab.Skills), the same call its own OnOpenSkills patch makes); Trophies -> the Fires hub's
        // Achievements module (FiresShell.Open("fires.achievements")). Each falls back to vanilla's button when its mod / the hub isn't there.
        static bool OpenOurs(Button src)
        {
            var gui = InventoryGui.instance;
            string n = src.name.ToLowerInvariant();
            bool skills = gui != null && n.Contains("skill");
            bool trophies = gui != null && n.Contains("troph");
            try
            {
                // 1.0.213 ([wishbone]): ClassesPanel.Open(tab) is RPGClasses' public call; it routes to the Fires hub when hosted
                // ("rpgclasses.classes" / "skills" | "achievements") and to its own window otherwise. Trophies live there as Achievements.
                if (skills || trophies)
                {
                    var panelType = Type.GetType("FiresRPGClasses.UI.ClassesPanel, FiresRPGClasses");
                    var tabType = Type.GetType("FiresRPGClasses.UI.ClassesTab, FiresRPGClasses");
                    var panel = panelType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    var open = tabType != null ? panelType?.GetMethod("Open", new[] { tabType }) : null;
                    string tab = skills ? "Skills" : "Achievements";
                    if (panel != null && open != null && Enum.IsDefined(tabType, tab))
                    {
                        open.Invoke(panel, new[] { Enum.Parse(tabType, tab) });
                        EquipmentPanel.PanelLog?.LogDebug($"panels: {(skills ? "Skills" : "Trophies")} button -> RPGClasses ClassesPanel.Open({tab})");
                        return true;
                    }
                }
                if (trophies)
                {
                    var shell = Type.GetType("FiresCore.UI.Shell.FiresShell, FiresUnifiedCore");
                    bool on = shell?.GetProperty("OverrideOn", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is bool b && b;
                    var open = shell?.GetMethod("Open", new[] { typeof(string), typeof(string) });
                    if (on && open != null)
                    {
                        open.Invoke(null, new object[] { "fires.achievements", null });
                        EquipmentPanel.PanelLog?.LogDebug("panels: Trophies button -> Fires hub, fires.achievements");
                        return true;
                    }
                }
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"panels: {src.name} -> our window failed ({(e.InnerException ?? e).Message}); vanilla"); }
            if (skills || trophies) EquipmentPanel.PanelLog?.LogDebug($"panels: {src.name} -> vanilla (our {(skills ? "Classes window" : "hub")} not available)");
            return false;
        }

        static Sprite s_vanillaRepair;
        static Sprite VanillaRepairIcon(InventoryGui gui)
        {
            var read = IconOf(gui.m_repairButton);
            if (read != null && !read.name.StartsWith("FiresKit", StringComparison.Ordinal)) return read;
            if (s_vanillaRepair == null)
                foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sp == null) continue;
                    if (string.Equals(sp.name, "repair", StringComparison.OrdinalIgnoreCase)) { s_vanillaRepair = sp; break; }
                    if (s_vanillaRepair == null && sp.name.IndexOf("repair", StringComparison.OrdinalIgnoreCase) >= 0 && !sp.name.StartsWith("FiresKit", StringComparison.Ordinal)) s_vanillaRepair = sp;
                }
            EquipmentPanel.PanelLog?.LogDebug($"panels: repair icon: read '{(read != null ? read.name : "none")}' -> vanilla '{(s_vanillaRepair != null ? s_vanillaRepair.name : "NOT FOUND")}'");
            return s_vanillaRepair ?? read;
        }

        static Sprite IconOf(Component c)
        {
            if (c == null) return null;
            var root = c is TMP_Text t ? TabOf(t).transform : c.transform;
            var own = root.GetComponent<Image>();
            // 1.0.170 [classes-ui] (Fire: info icons not rendering): Tools\VAInventoryUI\inventory_screen.txt shows each info button as
            // Background + Icon (Texts, Skills) or Background + Image (Trophies, Achievements), and the PvP toggle as Background + Checked.
            // The first child sprite was the BACKGROUND. Named children first, then any non-background child, then the control's own image.
            foreach (string want in new[] { "Icon", "Image", "Checked" })
            {
                var named = root.Find(want)?.GetComponent<Image>();
                if (named != null && named.sprite != null) return named.sprite;
            }
            foreach (var img in root.GetComponentsInChildren<Image>(true))
                if (img != own && img.sprite != null && img.name.IndexOf("Background", StringComparison.OrdinalIgnoreCase) < 0 && img.name.IndexOf("Glow", StringComparison.OrdinalIgnoreCase) < 0) return img.sprite;
            return own != null ? own.sprite : null;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t == null) return null;
            if (t.name == name) return t;
            foreach (Transform c in t) { var f = FindDeep(c, name); if (f != null) return f; }
            return null;
        }

        static bool s_infoReLogged;   // 1.0.225
        static void Hide(GameObject go)
        {
            if (go == null) return;
            var cg = go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();
            cg.alpha = 0f; cg.blocksRaycasts = false; cg.interactable = false;
            s_hidden.Add(cg);
        }

        /// <summary>1.0.170: the old screen's mirrors died with it; the next screen builds its own (no CanvasGroup restore: those objects are gone).</summary>
        internal static void ResetForNewGui()
        {
            s_hidden.Clear(); s_ours.Clear();
            s_armorText = s_weightText = s_header = s_title = null; s_playerVitals = null; s_repair = null; s_repairGlow = null; s_repairIcon = null; s_pvpCheck = null;
            s_footerArmor = s_footerWeight = null; s_recipeCount = -1;
            s_active = false; InfoMoved = false;
        }

        static void Restore()
        {
            foreach (var cg in s_hidden) if (cg != null) { cg.alpha = 1f; cg.blocksRaycasts = true; cg.interactable = true; }
            s_hidden.Clear();
            foreach (var go in s_ours) if (go != null) UnityEngine.Object.Destroy(go);
            s_ours.Clear();
            s_armorText = s_weightText = s_header = s_title = null; s_playerVitals = null; s_repair = null; s_repairGlow = null; s_repairIcon = null; s_pvpCheck = null;
            s_footerArmor = s_footerWeight = null; s_recipeCount = -1;
            s_active = false; InfoMoved = false;
            EquipmentPanel.PanelLog?.LogDebug("panels: chrome removed (left the Panels layout); vanilla pieces shown again");
        }
    }

    // 1.0.163: grabs the trash-can icon from the start screen's delete button (world remove, else character remove) when FejdStartup wakes.
    [HarmonyPatch(typeof(FejdStartup), "Awake")]
    internal static class FejdStartupDeleteIcon
    {
        [HarmonyPostfix]
        private static void Postfix(FejdStartup __instance)
        {
            try
            {
                foreach (var b in new[] { __instance.m_worldRemove, __instance.m_csRemoveButton })
                {
                    if (b == null) continue;
                    var own = b.targetGraphic as Image ?? b.GetComponent<Image>();
                    foreach (var img in b.GetComponentsInChildren<Image>(true))
                    {
                        if (img == own || img.sprite == null) continue;
                        PanelsChrome.s_menuTrash = img.sprite; PanelsChrome.s_menuTrashName = img.sprite.name;
                        EquipmentPanel.PanelLog?.LogDebug($"panels: trash icon taken from the start screen's '{b.name}': sprite '{img.sprite.name}'");
                        return;
                    }
                }
                // 1.0.166 (Fire's hand test: no child icon on the button, so the old name search picked 'Morkhalla_rubble_trashpile_icon'): the button's
                // OWN image is the trash can
                foreach (var b in new[] { __instance.m_worldRemove, __instance.m_csRemoveButton })
                {
                    var own = b != null ? (b.targetGraphic as Image ?? b.GetComponent<Image>()) : null;
                    if (own == null || own.sprite == null) continue;
                    PanelsChrome.s_menuTrash = own.sprite; PanelsChrome.s_menuTrashName = own.sprite.name;
                    EquipmentPanel.PanelLog?.LogDebug($"panels: trash icon taken from the start screen's '{b.name}' (its own image): sprite '{own.sprite.name}'");
                    return;
                }
                EquipmentPanel.PanelLog?.LogDebug("panels: the start screen's delete buttons have no image at all; the trash cell shows its × fallback");
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning("panels: trash icon capture failed: " + e.Message); }
        }
    }}
