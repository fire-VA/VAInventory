using System.Linq;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // Puts the unified inventory's own views on the LIVE vanilla screen.
    //
    // NOTHING VANILLA IS EVER MOVED OR RE-PARENTED. Vanilla's show animation drives Player, Info and Crafting's
    // positions every frame, and mods resolve those panels by string path - FATI uses
    // InventoryGui.instance.m_inventoryRoot.Find("Crafting") plus "Crafting/RecipeList" and "Crafting/Decription".
    // So our content goes INSIDE vanilla's panels and rides wherever vanilla puts them, the way
    // AzuExtendedPlayerInventory and ExtraSlots do it (Tools\project_vainventory_unified_screen.md, REWRITE PLAN).
    //
    // The prefab is the layout authority: each view is authored as a child of the vanilla panel it rides on, and
    // the bundle ships it under Mounts/<Panel> with that local rect. Mounting is SetParent(panel, false) and
    // nothing else.
    internal static class UnifiedScreen
    {
        internal const string BundleName = "vainventoryui";
        // The ASSET name in the bundle, which is the stripped copy the bundler writes - not the authored
        // prefab, which keeps the vanilla panel copies and their art for editing.
        const string PrefabName = "VAInventoryScreen_Runtime";
        const string MountsName = "Mounts";
        const string PlayerPanelName = "Player";
        const string CraftingPanelName = "Crafting";
        const string CharacterPanelPath = MountsName + "/" + PlayerPanelName + "/CharacterPanel";
        const string PreviewMountName = "PreviewMount";
        const string RailLeftName = "RailLeft";
        const string RailRightName = "RailRight";
        const string RowPrefix = "Row";
        const string CellsName = "Cells";
        const string CellTemplateName = "CellTemplate";
        const string CellPrefix = "Cell_";
        // The calm plate between the backdrop and the layout (FiresVAInventoryUIAdjust.EnsureBacking): it keeps its own
        // baked colours and never takes the slot face.
        const string BackingName = "Backing";

        // Crafting's RepairSimple/RepairButton reach this far left of its own rect.
        const float CraftingLeftOverhang = 80f;
        const float FitGutter = 16f;
        const float MinFitScale = 0.5f;

        static AssetBundle bundle;
        static GameObject prefab;
        static GameObject instance;
        static bool loadFailed;
        static RectTransform characterPanel;
        internal static RectTransform CharacterPanel => characterPanel;   // 1.0.101: PanelLayout
        static Vector2 authoredPosition;
        static readonly List<GameObject> mounted = [];

        // Slot -> the prefab cell its element should live in. Rebuilt whenever the panel is rebuilt, since
        // the active slot set is config driven. Empty in Legacy, which is what makes EquipmentPanel fall
        // back to its own positioning: one renderer with a placement strategy, not two renderers.
        // Keyed by the slot's ID + grid index, not the Slot object: equip/unequip re-news every Slot (Slots.InitializeSlots), so an
        // object key missed every lookup and the panel went blank until the inventory was reopened (Fire 10-06). IDs alone repeat
        // (empty, food, ammo cells), the index makes the key unique.
        static readonly Dictionary<string, RectTransform> cells = [];
        internal static readonly Dictionary<RectTransform, string> RailCaptions = [];   // 1.0.170: equipment rail cell -> its slot name

        static string CellKey(Slots.Slot slot) => slot.ID + "@" + slot.Index;

        // Slots were re-created while the Unified panel is up (equip/unequip, a bag adding slots): the rows are cloned to
        // the new slot counts and the map re-keyed now, not at the next open.
        public static void RebuildCells()
        {
            if (!Enabled || characterPanel == null) return;
            BuildCellMap();
            FitToScreen();
        }

        // The panel ends under its last row; taller than the screen, its lower block scrolls (PanelScroll).
        // Only REQUESTED here: the pass runs from Tick (InventoryGui.Update) once the panel is live and the Crafting scale is
        // settled, so it measures built rows on an active canvas. In 1.0.67 it ran inside Show/rebuild, and Tick skipped it
        // for a moved panel. Its line goes through UnityEngine.Debug: the "VAInventory.Panel" source is not a family name to
        // Core's QuietOtherMods, which drops its Debug lines.
        static bool screenFitPending;
        static bool fitLogPending;

        static void FitToScreen() => screenFitPending = true;

        static void RunScreenFit()
        {
            screenFitPending = false;
            if (characterPanel == null) return;
            KeepOnScreen();
            string line = PanelScroll.Fit(characterPanel, FindDeep(characterPanel, RowPrefix + "Misc") as RectTransform);
            if (!fitLogPending) return;
            fitLogPending = false;
            Debug.Log("[VAInventory] " + line);
        }

        // One cell -> item dump per open (EquipmentPanel.DumpCells), at the first panel refresh after Apply.
        static bool cellDumpPending;
        internal static bool ConsumeCellDump()
        {
            if (!cellDumpPending) return false;
            cellDumpPending = false;
            return true;
        }

        internal static bool Enabled => ConfigManager.Instance?.configUiLayout?.Value is "Unified" or "Panels";   // 1.0.157: Panels builds on Unified
        internal static bool PanelsMode => ConfigManager.Instance?.configUiLayout?.Value == "Panels";

        // 1.0.133 (perf audit): Tick ran 6 Find + GetComponent per frame while the inventory is open; the three layer Images are
        // cached and found again only when one is gone or no longer a child of the current panel.
        private static Image s_bkgImg, s_backingImg, s_underlayImg;
        private static Image PanelImage(ref Image cache, string child)
        {
            if (cache == null || cache.transform.parent != characterPanel) cache = characterPanel.Find(child)?.GetComponent<Image>();
            return cache;
        }

        internal static void Reset()
        {
            foreach (var view in mounted)
                if (view != null) Object.Destroy(view);
            mounted.Clear();
            PanelScroll.Unwrap();
            characterPanel = null;
            if (instance != null) Object.Destroy(instance);
            instance = null;
            cells.Clear();
            runtimeViews.Clear();
            PouchPopup.Unregister();
            CharacterPreview.Teardown();
            StatsPanel.Reset();
            BackpackTab.Reset();
            InventoryTabs.Reset();
        }

        // The preview camera renders every frame it is enabled, so it is switched off with the screen
        // rather than left running behind a closed inventory.
        internal static void Hide() => CharacterPreview.Hide();

        /// <summary>
        /// The prefab cell a slot's element belongs in, or false in Legacy / for slots with no cell (the
        /// currency vault is never drawn). Tool/ammo/food cells live in the pouch popups, backpack storage in the bag section.
        /// EquipmentPanel parents into this instead of positioning by Slot.Position.
        /// </summary>
        internal static bool TryGetCell(Slots.Slot slot, out RectTransform cell)
        {
            cell = null;
            if (!Enabled || slot == null) return false;
            return cells.TryGetValue(CellKey(slot), out cell) && cell != null;
        }

        /// <summary>
        /// Stretches an element to fill its cell. The cell owns the geometry, so the element carries none -
        /// which is what lets the layout be re-arranged in Unity without touching code.
        /// </summary>
        internal static void FillCell(RectTransform element)
        {
            element.anchorMin = Vector2.zero;
            element.anchorMax = Vector2.one;
            element.pivot = new Vector2(0.5f, 0.5f);
            element.anchoredPosition = Vector2.zero;
            element.sizeDelta = Vector2.zero;
            element.localScale = Vector3.one;
        }

        // ── Captions (Fire 10-07): a strip above the cell, outside the icon square, short names.
        internal const float CaptionStrip = 16f;

        // Pouch cells carry no caption (Fire 10-07 on 1.0.68: the per-slot "Ammo"/"Food" labels sat over the popup's title, and
        // they only repeated it).
        internal static string CaptionFor(Slots.Slot slot) => slot.IsCoinPurseSlot || IsPouchSlot(slot) ? "" : slot.ID switch
        {
            Slots.teleportBypassSlotID => "Bypass",
            Slots.backpackSlotID => "Backpack",
            Slots.shoulderSlotID => "Shoulder",
            _ => slot.Name
        };

        static bool IsPouchSlot(Slots.Slot slot) =>
            slot != null && (slot.IsFoodSlot || slot.IsAmmoSlot || Slots.toolHotbarSlots.Contains(slot));

        internal static void PlaceCaption(RectTransform rect, TMP_Text text, Slots.Slot slot)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(16f, CaptionStrip);   // a little wider than the cell: "Front offhand" style names
            rect.anchoredPosition = new Vector2(0f, 1f);
            text.text = CaptionFor(slot);
            text.enableWordWrapping = false;
            text.fontSizeMin = 9f;
            text.fontSizeMax = 12f;
            text.margin = Vector4.zero;
            text.verticalAlignment = VerticalAlignmentOptions.Bottom;
        }

        internal static void Apply()
        {
            if (loadFailed) return;
            var gui = InventoryGui.instance;
            var root = gui != null ? gui.m_inventoryRoot : null;
            if (root == null) return;
            if (!EnsurePrefab()) return;

            var player = root.Find(PlayerPanelName) as RectTransform;
            var crafting = root.Find(CraftingPanelName) as RectTransform;

            if (instance == null)
            {
                instance = Object.Instantiate(prefab, root);
                instance.SetActive(false);
                instance.name = "VAInventoryScreen";   // stable name in the live hierarchy and in dumpui
                StripEditorOnly(instance);
                instance.transform.SetAsFirstSibling();
                characterPanel = instance.transform.Find(CharacterPanelPath) as RectTransform;
                StripCellChrome();
                // Bind BEFORE mounting: binding walks our own root, and a mounted view is no longer under it.
                BindChrome(instance.transform, player);
                MountOnVanillaPanels(root);
                MakePanelDraggable();
            }

            var ui = (RectTransform)instance.transform;

            // TABS ARE NOT GATED ON THE LAYOUT. They live inside vanilla's Crafting panel, which exists in
            // both layouts, and their whole point is reaching another mod's screen from the inventory -
            // which has nothing to do with which arrangement the player prefers. Gating them meant every
            // registered tab was invisible on the DEFAULT setting.
            MountCraftingExtras(ui, crafting);
            StatsPanel.RegisterTab();
            BackpackTab.RegisterTab();
            InventoryTabs.Sync(crafting != null ? crafting.Find("CraftingExtras") as RectTransform : null);

            foreach (var view in mounted)
                if (view != null) view.SetActive(Enabled);

            if (!Enabled)
            {
                LeaveUnified();
                return;
            }

            fitPending = true;
            fitLogPending = true;
            lastCraftingX = float.NaN;
            cellDumpPending = true;
            PlacePanel();
            PanelsLayout.Attach(root as RectTransform);   // 1.0.157: inert unless [UI] Layout = Panels
            Compact();
            ApplyPanelOpacity();
            BuildCellMap();
            SetLegacyPanelVisible(false);

            // Layout groups do not rebuild themselves for anything cloned after the prefab was authored. One
            // rebuild from the panel root sizes the nested groups inner-first, so the panel's own size fitter
            // sees the rows as they are now; per-group rebuilds ran parent-first and measured stale children.
            FitToScreen();

            Debug.Log($"[VAInventory] unified applied: layout={ConfigManager.Instance?.configUiLayout?.Value} "
                + $"mounts={mounted.Count} cells={cells.Count} "
                + $"legacyPanel={(EquipmentPanel.equipmentBackground != null ? EquipmentPanel.equipmentBackground.gameObject.activeSelf.ToString() : "<null>")} "
                + $"panel={(characterPanel != null ? $"{characterPanel.rect.width:0}x{characterPanel.rect.height:0}" : "<none>")} "
                + $"canvasWidth={((RectTransform)root).rect.width:0}");
            CharacterPreview.Bind(characterPanel != null ? characterPanel.GetComponentsInChildren<RawImage>(true)
                .FirstOrDefault(image => image.name == PreviewMountName) : null);
        }

        // Fire 10-07 (1.0.69): with Misc back under the pouch row, the Explorer Bag panel measured 1080 units for 1042 of room.
        // The prefab's 30-unit top/bottom padding and the centre column's 16-unit gaps are trimmed here, at runtime, so the
        // bag fits without scrolling and no bundle rebuild is needed. Idempotent: Min() against the authored values.
        internal const int PanelPadTopBottom = 18;
        const float CentreColumnSpacing = 10f;

        static void Compact()
        {
            if (characterPanel == null) return;
            var panelLayout = characterPanel.GetComponent<VerticalLayoutGroup>();
            if (panelLayout != null)
            {
                panelLayout.padding.top = Mathf.Min(panelLayout.padding.top, PanelPadTopBottom);
                panelLayout.padding.bottom = Mathf.Min(panelLayout.padding.bottom, PanelPadTopBottom);
            }
            var centre = FindDeep(characterPanel, "ColumnCentre")?.GetComponent<VerticalLayoutGroup>();
            if (centre != null) centre.spacing = Mathf.Min(centre.spacing, CentreColumnSpacing);
            // 1.0.70: round 7's Explorer Bag still measured 1038 for 1033 of room (scroll on, its last row clipped). The gap
            // between the columns and the bag section gives up 6 more (the bag header and gap below give 6), so it fits whole;
            // the character preview keeps its size (a fixed render, it would squash).
            if (panelLayout != null) panelLayout.spacing = Mathf.Min(panelLayout.spacing, PanelSpacing);
            if (PanelsMode) CompactForPanels(panelLayout);
        }

        // 1.0.158 (Fire, layout v2: "tighten the player panel's internal gaps so it fits 1080p without scrolling", measured 1074 for a
        // ~1008 cap): in Panels only, every vertical gap inside the panel shrinks to PanelsGap and the outer padding to PanelsPad. Cell
        // sizes, the preview and the rails are untouched (gaps only). Logs the panel height before and after.
        const float PanelsGap = 3f;
        const int PanelsPad = 8;
        internal const int PanelsHeaderBand = 48;   // 1.0.171
        static float lastPanelsHeight = -1f;

        static void CompactForPanels(VerticalLayoutGroup panelLayout)
        {
            float before = characterPanel.rect.height;
            if (panelLayout != null) { panelLayout.padding.top = Mathf.Min(panelLayout.padding.top, PanelsPad); panelLayout.padding.bottom = Mathf.Min(panelLayout.padding.bottom, PanelsPad); }
            // 1.0.171 (Fire's night shot: 'Vafire' over the character's head): a 48 px band at the panel's top for the name header ([classes-ui]
            // anchors it to the panel's top); everything else, the preview included, starts below it
            if (panelLayout != null) panelLayout.padding.top = PanelsPad + PanelsHeaderBand;
            int n = 0;
            foreach (var v in characterPanel.GetComponentsInChildren<VerticalLayoutGroup>(true))
            {
                if (v.spacing > PanelsGap) { v.spacing = PanelsGap; n++; }
                if (v != panelLayout && (v.padding.top > PanelsPad || v.padding.bottom > PanelsPad)) { v.padding.top = Mathf.Min(v.padding.top, PanelsPad); v.padding.bottom = Mathf.Min(v.padding.bottom, PanelsPad); n++; }
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(characterPanel);
            float after = characterPanel.rect.height;
            if (!Mathf.Approximately(after, lastPanelsHeight))
                EquipmentPanel.PanelLog?.LogDebug($"panels: character panel gaps tightened ({n} group(s)): height {before:0} -> {after:0}");
            lastPanelsHeight = after;
        }

        const float PanelSpacing = 4f;

        // Fire 10-07 (1.0.69): "too see-through, grid squares wash into the background". The backdrop's art is translucent
        // parchment, so a solid plate goes under it at [UI] PanelOpacity, inset inside the frame's border.
        const string UnderlayName = "OpaqueUnderlay";
        const float UnderlayInset = 6f;
        static readonly Color UnderlayColor = new(0.10f, 0.08f, 0.06f, 1f);   // 1.0.183: back to the pre-unified colour (Fire)
        // 1.0.75 (Fire 10-08, [classes-ui] mockup A): the player grid's own underlay, warmer than the right panel's.
        internal static readonly Color PlayerGridUnderlay = new(0.30f, 0.22f, 0.14f, 1f);   // 1.0.77: lighter (was 0.17/0.12/0.08)

        internal static void ApplyPanelOpacity()
        {
            if (characterPanel == null) return;
            float alpha = Mathf.Clamp01(ConfigManager.Instance?.configPanelOpacity?.Value ?? 0.92f);
            var plate = characterPanel.Find(UnderlayName) as RectTransform;
            if (plate == null)
            {
                plate = NewRect(characterPanel, UnderlayName);
                plate.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                plate.anchorMin = Vector2.zero;
                plate.anchorMax = Vector2.one;
                plate.offsetMin = new Vector2(UnderlayInset, UnderlayInset);
                plate.offsetMax = new Vector2(-UnderlayInset, -UnderlayInset);
                plate.gameObject.AddComponent<Image>().raycastTarget = false;
            }
            plate.SetAsFirstSibling();   // under Bkg, which stays the drag handle
            plate.GetComponent<Image>().color = new Color(UnderlayColor.r, UnderlayColor.g, UnderlayColor.b, alpha);
        }

        // Every view under Mounts/<Panel> goes into the live panel of that name with its authored local rect.
        static void MountOnVanillaPanels(Transform root)
        {
            var mounts = instance.transform.Find(MountsName);
            if (mounts == null)
            {
                Debug.LogError($"[VAInventory] '{BundleName}' has no {MountsName}: the character panel cannot be shown. Rebuild the bundle.");
                return;
            }
            var placed = new List<string>();
            foreach (var holder in mounts.Cast<Transform>().ToList())
            {
                var panel = root.Find(holder.name);
                if (panel == null)
                {
                    Debug.LogWarning($"[VAInventory] no vanilla panel '{holder.name}' to mount {holder.childCount} view(s) on.");
                    continue;
                }
                foreach (var view in holder.Cast<Transform>().ToList())
                {
                    view.SetParent(panel, false);
                    mounted.Add(view.gameObject);
                    placed.Add(holder.name + "/" + view.name);
                }
            }
            Debug.Log($"[VAInventory] mounted onto vanilla panels: {(placed.Count > 0 ? string.Join(", ", placed) : "<none>")}");
        }

        // A view mounted beside the carry grid shares the row with Crafting. On a narrow canvas (a high GUI
        // Scale) it shrinks uniformly to stay clear of Crafting instead of drawing over it.
        //
        // Vanilla's show clip slides Crafting in from off-screen (x 630 -> -40 over 0.5 s), so the room is only
        // real once Crafting has stopped moving: measured from InventoryGui.Update, the first frame its x holds.
        static bool fitPending;
        static float lastCraftingX;

        internal static void Tick()
        {
            if (!Enabled || characterPanel == null) return;
            if (cellRebuildPending)
            {
                cellRebuildPending = false;
                RebuildCells();
                EquipmentPanel.MarkDirty();
                EquipmentPanel.PanelLog?.LogDebug("[VAInventory] cell map rebuilt: a refresh left slots without a cell (a bag's storage appeared)");
            }
            // 1.0.157: in Panels the row is placed and scaled by PanelsLayout (LateUpdate), not by the Crafting fit
            if (fitPending) { if (PanelsMode) { fitPending = false; screenFitPending = true; } else ScaleToCrafting(); }
            // The screen fit waits for the scale (it measures in scaled units), then runs on every open and after any rebuild.
            if (!fitPending && !screenFitPending && characterPanel.gameObject.activeInHierarchy && PanelScroll.NeedsRefit(characterPanel)) screenFitPending = true;
            if (!fitPending && screenFitPending && characterPanel.gameObject.activeInHierarchy) RunScreenFit();
            // 1.0.72: the player grid's inset plate, refitted when its cells move (PlayerGridPlate).
            if (characterPanel.gameObject.activeInHierarchy)
            {
                // 1.0.81 (Fire: "a DARK background with a LIGHTER INLAY that the grid lays onto"): the grid copies the panel's
                // three layers live: the dark underlay, the Bkg art over it, and the lighter Backing inlay under the cells.
                PlayerGridPlate.Tick(PanelImage(ref s_bkgImg, "Bkg"),
                    PanelImage(ref s_backingImg, BackingName),
                    PanelImage(ref s_underlayImg, UnderlayName), GoldEdge, CellEdgeAlpha);
                // 1.0.136 (Fire's approved mockup): the chest grid gets the same three layers (ContainerGridPlate).
                ContainerGridPlate.Tick(PanelImage(ref s_bkgImg, "Bkg"),
                    PanelImage(ref s_backingImg, BackingName),
                    PanelImage(ref s_underlayImg, UnderlayName), GoldEdge, CellEdgeAlpha);
                ApplyGridCellFace();
                PanelLayout.TickLayout();   // 1.0.101: companions re-dock when the panel moves
                ApplySuppression();   // 1.0.105: VAInventoryApi.SuppressPlayerPanel (cheap: a flag compare)
                // 1.0.82 (Fire: the crafting panel shares the player panel's layout and colour scheme): same three layers, live.
                if (PanelsMode) { if (!panelSkinRemoved) { panelSkinRemoved = true; PanelSkin.RemoveAll(); EquipmentPanel.PanelLog?.LogDebug("panels: VAInventory PanelSkin off - Core CraftingSkin owns the crafting panel (UI audit)"); } }   // 1.0.224
                else PanelSkin.Apply(InventoryGui.instance != null ? InventoryGui.instance.m_crafting : null,
                    PanelImage(ref s_bkgImg, "Bkg"),
                    PanelImage(ref s_underlayImg, UnderlayName),
                    PanelImage(ref s_backingImg, BackingName));
            }
        }

        // 1.0.88 (Fire: "the slightly darker grid squares ... on the player panel too"): every cell face on the character panel
        // (equipment, Misc, pouches, bag sections: the images BindChrome gave the element sprite) takes the face the main grid's
        // vanilla cells draw (PlayerGridPlate.GridCellFace). Re-applied when that face or the panel's hierarchy changes.
        static Color lastAppliedFace = new Color(-1f, 0f, 0f, 0f);
        static int lastHierarchyCount = -1;

        static bool panelSkinRemoved;   // 1.0.224
        static void ApplyGridCellFace()
        {
            if (characterPanel == null || PlayerGridPlate.GridCellFace == null) return;   // 1.0.183: runs as before 1.0.181 (part of the pre-unified look)
            var face = PlayerGridPlate.GridCellFace.Value;
            int hierarchy = characterPanel.hierarchyCount;
            if (face == lastAppliedFace && hierarchy == lastHierarchyCount) return;
            var elementSprite = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponent<Image>()?.sprite;
            if (elementSprite == null) return;
            var panelBkg = PanelImage(ref s_bkgImg, "Bkg");
            int n = 0;
            foreach (var image in characterPanel.GetComponentsInChildren<Image>(true))
            {
                if (image == panelBkg || image.sprite != elementSprite) continue;
                if (image.color != face) image.color = face;
                n++;
            }
            if (face != lastAppliedFace)
                EquipmentPanel.PanelLog?.LogDebug($"panel cells: {n} cell face(s) take the main grid's cell face {face}");
            lastAppliedFace = face;
            lastHierarchyCount = hierarchy;
        }

        static void ScaleToCrafting()
        {
            if (DockOwnsPanel) return;   // 1.0.219 ([generator]): Core's dock owns this panel's position + localScale under Panels
            var root = InventoryGui.instance != null ? InventoryGui.instance.m_inventoryRoot as RectTransform : null;
            var crafting = root != null ? root.Find(CraftingPanelName) as RectTransform : null;
            if (crafting == null) { fitPending = false; return; }
            float x = crafting.anchoredPosition.x;
            if (!Mathf.Approximately(x, lastCraftingX))
            {
                lastCraftingX = x;
                return;
            }
            fitPending = false;
            screenFitPending = true;

            // 1.0.100 (Fire: "the ability to SCALE the player panel"): the config scale is the panel's size; the Crafting fit
            // below can only shrink it further.
            float user = UserScale;
            characterPanel.localScale = new Vector3(user, user, 1f);
            // A panel the player has placed stays at the chosen size where they put it (the screen fit still runs).
            if (PanelOffset != Vector2.zero) { FitToScreen(); LogUserScale(user, user); return; }
            float panelLeft = CornerX(root, characterPanel, BottomLeftCorner);
            float width = CornerX(root, characterPanel, TopRightCorner) - panelLeft;
            float room = CornerX(root, crafting, BottomLeftCorner) - CraftingLeftOverhang - FitGutter - panelLeft;
            float scale = width > 0f && room < width ? Mathf.Max(MinFitScale * user, user * room / width) : user;
            characterPanel.localScale = new Vector3(scale, scale, 1f);
            FitToScreen();
            LogUserScale(user, scale);
            if (scale < user)
                Debug.Log($"[VAInventory] character panel scaled to {scale:0.00} to clear Crafting: {room:0} of {width:0} units "
                    + $"free on a {root.rect.width:0}-unit canvas" + (room / width < MinFitScale ? $" (floor {MinFitScale}, it overlaps)" : ""));
        }

        // ── Dragging the panel (Fire, 2026-09-27: move the whole equipment + preview panel "the same way we could to
        // re position our old equipment panel"). The backdrop is the handle; cells, the preview and the rows keep
        // their own mouse input on top of it. The offset is saved, relative to the prefab's spot.
        // 1.0.100: the player panel's size from the config, [UI] PlayerPanelScale (0.6 to 1.2). A change re-runs the fit live.
        internal static bool DockOwnsPanel => PanelsMode && PanelsDock.Registered;   // 1.0.219
        internal static float UserScale => Mathf.Clamp(ConfigManager.Instance?.configPlayerPanelScale?.Value ?? 1f, 0.6f, 1.2f);
        static float lastLoggedScale = float.NaN;

        static void LogUserScale(float user, float applied)
        {
            PanelLayout.Report(applied);   // 1.0.101: other mods follow the applied scale
            if (Mathf.Approximately(applied, lastLoggedScale)) return;
            lastLoggedScale = applied;
            EquipmentPanel.PanelLog?.LogDebug($"player panel scale: config {user:0.00}, applied {applied:0.00}");
        }

        // 1.0.171 (Fire / [visual]: the player panel ran off the bottom, the second backpack cut off): under Panels, Core's dock moves the panel
        // after the open-time fit, so PanelsLayout asks for a re-fit whenever the panel's bottom is below the screen; PanelScroll then caps it
        // and the bag sections scroll inside.
        internal static void RequestScreenFit() { screenFitPending = true; fitLogPending = true; }

        // 1.0.174: EquipmentPanel asks for this when a refresh leaves slots without a cell (a new bag's storage mid-drag); run next Tick
        static bool cellRebuildPending;
        internal static void RequestCellRebuild() => cellRebuildPending = true;

        internal static void RequestRescale()
        {
            if (characterPanel == null) return;
            if (PanelsMode && PanelsDock.Registered) return;   // 1.0.169: Core's dock owns the panel's scale under Panels
            characterPanel.localScale = new Vector3(UserScale, UserScale, 1f);
            fitPending = true;
            lastCraftingX = float.NaN;
        }

        // 1.0.105: the player panel hidden/shown by VAInventoryApi's owners, via a CanvasGroup on the panel.
        static bool appliedSuppressed;

        internal static void ApplySuppression()
        {
            if (characterPanel == null) return;
            bool want = VAInventoryApi.PlayerPanelSuppressed;
            var group = characterPanel.GetComponent<CanvasGroup>();
            if (want == appliedSuppressed && (group == null || (group.alpha < 0.5f) == want)) return;
            if (group == null) group = characterPanel.gameObject.AddComponent<CanvasGroup>();
            group.alpha = want ? 0f : 1f;
            group.interactable = !want;
            group.blocksRaycasts = !want;
            appliedSuppressed = want;
            // 1.0.106 (Fire's NPC-book shot: the whole middle column overlapped the book): the character panel carries the
            // preview, equipment grid, extra slots (Tools/Ammo/Food/coin, Misc) and the bag sections; any Legacy-layout
            // equipment / bag panels under the vanilla Player panel and the open pouch popups go with it. Only the main
            // inventory grid and Crafting stay.
            int extra = 0;
            var playerRoot = InventoryGui.instance != null ? InventoryGui.instance.m_player : null;
            if (playerRoot != null)
                foreach (Transform child in playerRoot)
                    if (child.name == "VAInventoryEquipmentPanel" || child.name.StartsWith("VAInventoryBackpackPanel_", System.StringComparison.Ordinal))
                    {
                        var g = child.GetComponent<CanvasGroup>() ?? child.gameObject.AddComponent<CanvasGroup>();
                        g.alpha = want ? 0f : 1f; g.interactable = !want; g.blocksRaycasts = !want;
                        extra++;
                    }
            if (want) Slots.ClosePouches();
            EquipmentPanel.PanelLog?.LogDebug($"player panel: {(want ? "hidden (suppressed)" : "shown")}: {characterPanel.name} (preview, equipment, extra slots, bag sections, {characterPanel.childCount} children) + {extra} legacy panel(s)");
        }

        static Vector2 PanelOffset =>ConfigManager.Instance?.configCharacterPanelOffset?.Value ?? Vector2.zero;

        static void MakePanelDraggable()
        {
            if (characterPanel == null) return;
            authoredPosition = characterPanel.anchoredPosition;
            var handle = PanelImage(ref s_bkgImg, "Bkg");
            if (handle == null) return;
            handle.raycastTarget = true;
            if (handle.GetComponent<CharacterPanelDrag>() == null) handle.gameObject.AddComponent<CharacterPanelDrag>();
            AddResizeGrip();
        }

        // 1.0.102 (Fire: "there is no drag handle to scale the player panel in the bottom right"): a grip in the panel's
        // bottom-right corner. Dragging it scales the whole panel live; letting go saves [UI] PlayerPanelScale, which re-runs
        // the fit, and PanelLayout tells the companion panel. Double-click puts it back to 1.
        const string ResizeGripName = "VAInventoryResizeGrip";

        static void AddResizeGrip()
        {
            // 1.0.272 (Fire 10-20: the player panel resize corner did nothing; [generator]/[lead]: one implementation): under Panels Core's dock
            // grip (FiresDockGrip3) owns resize; this grip sat on top of it and took his presses. Panels = no VAInventory grip (any old one goes).
            if (characterPanel != null && PanelsMode && PanelsDock.Available) { RemoveResizeGrip("Panels with Core's dock: its grip owns resize"); return; }   // Fire: VAInventory doesn't need Core; without it this grip stays
            if (characterPanel == null || characterPanel.Find(ResizeGripName) != null) return;
            var go = new GameObject(ResizeGripName, typeof(RectTransform), typeof(Image), typeof(LayoutElement), typeof(CharacterPanelResize));
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var rt = (RectTransform)go.transform;
            rt.SetParent(characterPanel, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
            rt.anchoredPosition = new Vector2(-4f, 4f);
            rt.sizeDelta = new Vector2(18f, 18f);
            var image = go.GetComponent<Image>();
            // 1.0.104 (Fire: the corner showed a plain square): a drawn three-line grip sprite, white-tinted so its gold shows.
            image.sprite = GripSprite.Get(GoldEdge);
            image.color = Color.white;
            image.raycastTarget = true;
            rt.sizeDelta = new Vector2(22f, 22f);
            rt.SetAsLastSibling();
            EquipmentPanel.PanelLog?.LogDebug($"player panel: resize grip added at the bottom-right of {characterPanel.name}, sprite {image.sprite?.name ?? "none"}");
        }

        internal static void RemoveResizeGrip(string why)
        {
            var g = characterPanel != null ? characterPanel.Find(ResizeGripName) : null;
            if (g == null) return;
            g.gameObject.SetActive(false); Object.Destroy(g.gameObject);
            EquipmentPanel.PanelLog?.LogDebug($"player panel: VAInventory resize grip removed ({why})");
        }

        static float resizeStartScale, resizeStartDistance;

        internal static void BeginResize(PointerEventData e)
        {
            if (DockOwnsPanel) return;   // 1.0.219 ([generator]): Core's dock owns this panel's position + localScale under Panels
            resizeStartScale = characterPanel != null ? characterPanel.localScale.x : 1f;
            resizeStartDistance = Mathf.Max(1f, DistanceFromTopLeft(e));
        }

        internal static void Resize(PointerEventData e)
        {
            if (DockOwnsPanel) return;   // 1.0.219 ([generator]): Core's dock owns this panel's position + localScale under Panels
            if (characterPanel == null) return;
            float s = Mathf.Clamp(resizeStartScale * DistanceFromTopLeft(e) / resizeStartDistance, 0.6f, 1.2f);
            characterPanel.localScale = new Vector3(s, s, 1f);
            PanelLayout.Report(s);
        }

        internal static void EndResize()
        {
            if (DockOwnsPanel) return;   // 1.0.219 ([generator]): Core's dock owns this panel's position + localScale under Panels
            var config = ConfigManager.Instance;
            if (characterPanel == null || config?.configPlayerPanelScale == null) return;
            float s = Mathf.Round(characterPanel.localScale.x * 100f) / 100f;
            config.configPlayerPanelScale.Value = s;   // SettingChanged re-runs the fit
            config.NotifyLocalConfigSave();
            EquipmentPanel.PanelLog?.LogDebug($"player panel: resized by the grip to {s:0.00} (saved to [UI] PlayerPanelScale)");
        }

        internal static void ResetScale()
        {
            var config = ConfigManager.Instance;
            if (config?.configPlayerPanelScale == null) return;
            config.configPlayerPanelScale.Value = 1f;
            config.NotifyLocalConfigSave();
        }

        // Pointer distance from the panel's top-left corner, in screen pixels: the corner that stays put while the grip moves.
        static float DistanceFromTopLeft(PointerEventData e)
        {
            var corners = new Vector3[4];
            characterPanel.GetWorldCorners(corners);
            Vector2 topLeft = RectTransformUtility.WorldToScreenPoint(e.pressEventCamera, corners[1]);
            return Vector2.Distance(topLeft, e.position);
        }

        static void PlacePanel()
        {
            if (PanelsMode && PanelsDock.Registered) return;   // 1.0.169 (Fire: 'dragging immediately fucks everything up'): Core's dock owns it
            if (characterPanel != null) characterPanel.anchoredPosition = authoredPosition + PanelOffset;
            KeepOnScreen();
        }

        // 1.0.176 ([lead] R-NIGHT3 Unified at 1024x768: 'Cell_0 is OFF SCREEN at (-457,823)', panel fit 'room=2559 canvasH=1440'): a saved
        // [UI] CharacterPanelOffset of (-1710, 553) put the panel far left and above the screen, and nothing brought it back (the screen fit
        // only sizes it). The panel is now kept on the canvas: left / right edge inside, top under the top edge (the fit caps the bottom).
        // Only the drawn spot moves; the saved offset is left as it is (no config writes).
        static string lastKeepLine;
        static void KeepOnScreen()
        {
            if (characterPanel == null || (PanelsMode && PanelsDock.Registered)) return;
            var canvas = characterPanel.GetComponentInParent<Canvas>()?.rootCanvas;
            if (canvas == null) return;
            var canvasRect = (RectTransform)canvas.transform;
            var c = new Vector3[4];
            characterPanel.GetWorldCorners(c);
            Vector2 min = canvasRect.InverseTransformPoint(c[0]), max = canvasRect.InverseTransformPoint(c[2]);
            Rect area = canvasRect.rect;
            float dx = min.x < area.xMin ? area.xMin - min.x : max.x > area.xMax ? Mathf.Max(area.xMax - max.x, area.xMin - min.x) : 0f;
            float dy = max.y > area.yMax ? area.yMax - max.y : min.y < area.yMin && max.y - min.y <= area.height ? area.yMin - min.y : 0f;
            if (Mathf.Abs(dx) < 0.5f && Mathf.Abs(dy) < 0.5f) return;
            // 1.0.177 ([lead] R-NIGHT7 Unified A/B: 'moved 1012,-512' left the panel ON the grid - 'topmost hit at (78,727) is
            // CharacterPanel/Top/ColumnCentre/PreviewMount, not Player/PlayerGrid'): an off-screen saved spot goes back to the authored spot
            // (the layout's own place beside the grid) first; only what is still off the canvas then is nudged in from the edge.
            string how = "edge clamp";
            if (PanelOffset != Vector2.zero)
            {
                characterPanel.anchoredPosition = authoredPosition;
                characterPanel.GetWorldCorners(c);
                min = canvasRect.InverseTransformPoint(c[0]); max = canvasRect.InverseTransformPoint(c[2]);
                dx = min.x < area.xMin ? area.xMin - min.x : max.x > area.xMax ? Mathf.Max(area.xMax - max.x, area.xMin - min.x) : 0f;
                dy = max.y > area.yMax ? area.yMax - max.y : min.y < area.yMin && max.y - min.y <= area.height ? area.yMin - min.y : 0f;
                how = "authored spot";
            }
            if (Mathf.Abs(dx) >= 0.5f || Mathf.Abs(dy) >= 0.5f) characterPanel.position += canvasRect.TransformVector(new Vector3(dx, dy, 0f));
            string line = $"[VAInventory] player panel kept on screen ({how}): then moved {dx:0},{dy:0} canvas units (saved offset {PanelOffset} left as it is; canvas {area.width:0}x{area.height:0})";
            if (line != lastKeepLine) { lastKeepLine = line; Debug.Log(line); }
        }

        internal static void MovePanel(PointerEventData eventData)
        {
            if (DockOwnsPanel) return;   // 1.0.219 ([generator]): Core's dock owns this panel's position + localScale under Panels
            var parent = characterPanel != null ? characterPanel.parent as RectTransform : null;
            if (parent == null) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, eventData.position, eventData.pressEventCamera, out var now)) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, eventData.position - eventData.delta, eventData.pressEventCamera, out var before)) return;
            characterPanel.localScale = new Vector3(UserScale, UserScale, 1f);
            characterPanel.anchoredPosition += now - before;
        }

        internal static void SavePanelOffset()
        {
            var config = ConfigManager.Instance;
            if (characterPanel == null || config?.configCharacterPanelOffset == null) return;
            config.configCharacterPanelOffset.Value = characterPanel.anchoredPosition - authoredPosition;
            config.NotifyLocalConfigSave();
            FitToScreen();
        }

        internal static void ResetPanelOffset()
        {
            var config = ConfigManager.Instance;
            if (config?.configCharacterPanelOffset == null) return;
            config.configCharacterPanelOffset.Value = Vector2.zero;
            config.NotifyLocalConfigSave();
            PlacePanel();
            fitPending = true;
            lastCraftingX = float.NaN;
        }

        const int BottomLeftCorner = 0;
        const int TopRightCorner = 2;
        static readonly Vector3[] corners = new Vector3[4];

        // GetWorldCorners order: bottom-left, top-left, top-right, bottom-right.
        static float CornerX(RectTransform root, RectTransform rect, int corner)
        {
            rect.GetWorldCorners(corners);
            return root.InverseTransformPoint(corners[corner]).x;
        }

        // Legacy after Unified: our views are already hidden by the caller; give the slots back to the legacy
        // backdrop. Nothing vanilla was changed, so there is nothing of vanilla's to put back.
        static void LeaveUnified()
        {
            PlayerGridPlate.Remove();
            ContainerGridPlate.Remove();
            PanelSkin.RemoveAll();
            if (cells.Count == 0) return;
            cells.Clear();
            PanelScroll.Unwrap();
            DropRuntimeViews();
            CharacterPreview.Teardown();
            SetLegacyPanelVisible(true);
            EquipmentPanel.RefreshSlotsAndPanel();
        }

        // EquipmentPanel's own panel is the legacy layout's visual. In Unified the slots live in prefab
        // cells instead, so leaving it on is what drew both layouts at once.
        static void SetLegacyPanelVisible(bool visible)
        {
            if (EquipmentPanel.equipmentBackground != null)
                EquipmentPanel.equipmentBackground.gameObject.SetActive(visible);
        }

        // Assigns every active slot a cell. Equipment slots split evenly over the two rails in index order, and
        // rail cells with no slot are hidden so the rails hold only real slots. Tools, food, ammo and the misc
        // slots fill their rows, whose cells are cloned from a template to match the live counts (all config
        // driven, so a fixed cell count would either waste or truncate); an empty row is hidden.
        //
        // The panel sizes itself to this content (prefab layout groups plus a size fitter), so a wrapped row or a
        // hidden one moves everything below it and the backdrop follows.
        static readonly BepInEx.Logging.ManualLogSource BackpackLog = BepInEx.Logging.Logger.CreateLogSource("VAInventory.Panel");

        static void BuildCellMap()
        {
            cells.Clear();
            if (characterPanel == null) return;
            PanelScroll.Unwrap();   // Misc back in its column before the rows are rebuilt

            var railLeft = FindDeep(characterPanel, RailLeftName);
            var railRight = FindDeep(characterPanel, RailRightName);
            if (railLeft == null || railRight == null) return;

            // Fire 10-07 (approved panel_backpack mockup): rails 6 / 6 in a fixed order. Left = armour + trinket + ring,
            // right = the RPGClasses bar slots, Torch, Backpack, Pet. Inactive slots drop out (no gaps, no balancing).
            // Layout only: every slot keeps its grid position, so no saved item moves.
            var equipment = Slots.AllSlots()
                .Where(s => s != null && s.IsActive && !s.IsCurrencyVaultSlot && !s.IsBackpackStorageSlot
                            && !s.IsFoodSlot && !s.IsAmmoSlot && !s.IsMiscSlot && !s.IsCoinPurseSlot
                            && !IsJewelrySlot(s)   // 1.0.110: in the Jewelry popup; the rail shows its face instead
                            && s.ID != Slots.emptySlotID && s.Index >= Slots.InventorySizePlayer)
                .OrderBy(s => s.Index)
                .ToList();
            // Fire 10-07 (rails 7 / 7): Shoulder ends the left rail, TeleportBypass the right; the coin purse is the 4th
            // cell of the pouch row (BuildPouches).
            var left = TakeInOrder(equipment, LeftRailOrder);
            var right = TakeInOrder(equipment, RightRailOrder);
            // Any other active equipment slot stays visible: appended after the fixed order, left then right, alternating.
            for (int i = 0; i < equipment.Count; i++)
                (i % 2 == 0 ? left : right).Add(equipment[i]);
            FillRail(railLeft, left);
            FillRail(railRight, right);
            BackpackLog.LogDebug($"[VAInventory] rails: left {left.Count} [{string.Join(",", left.Select(s => s.ID))}], "
                + $"right {right.Count} [{string.Join(",", right.Select(s => s.ID))}]");

            // Pouches (Fire 10-07): the Tools / Ammo / Food rows are hidden; their real slots live in pouch popups.
            AssignRow("Tools", []);
            AssignRow("Food", []);
            AssignRow("Ammo", []);
            BuildPouches();
            AssignRow("Misc", Slots.AllSlots().Where(s => s != null && s.IsActive && s.IsMiscSlot).OrderBy(s => s.Index).ToList());
            BuildBagSections();
            // 1.0.111 (R-1012J: empty pouch popups + 141 ElemGo NREs after the inventory-open rebuild): the cells above are new and
            // the old slot elements died with the old ones; EquipmentPanel re-creates them into these cells on its next update.
            EquipmentPanel.RequestElementRebuild();
        }

        static readonly string[] LeftRailOrder =
            [Slots.helmetSlotID, Slots.chestSlotID, Slots.legsSlotID, Slots.utilitySlotID, Slots.trinketSlotID,
             Slots.shoulderSlotID];   // 1.0.110: Ring's place holds the Jewelry face (BuildPouches), right after Trinket
        static readonly string[] RightRailOrder =
            [Slots.frontWeaponSlotID, Slots.frontOffhandSlotID, Slots.backWeaponSlotID, Slots.backOffhandSlotID,
             Slots.torchSlotID, Slots.backpackSlotID, Slots.petSlotID, Slots.teleportBypassSlotID];

        // Removes the slots with these IDs from the pool and returns them in this order.
        static List<Slots.Slot> TakeInOrder(List<Slots.Slot> pool, string[] order)
        {
            var taken = new List<Slots.Slot>();
            foreach (var id in order)
            {
                var slot = pool.FirstOrDefault(s => s.ID == id);
                if (slot == null) continue;
                pool.Remove(slot);
                taken.Add(slot);
            }
            return taken;
        }

        // A rail with fewer prefab cells than slots grows: extra cells are cloned from its first cell (already stripped
        // by StripCellChrome), so nothing is dropped. Unused cells are hidden.
        // 1.0.143 ([seasons], Fire 10-14: the belt and capes were invisible): the old Jewelry face is only DEACTIVATED by
        // DropRuntimeViews (Destroy lands at end of frame), so it was still a rail child here; it took the 6th place - the
        // Shoulder slot was mapped onto it - and the real Shoulder cell, now 7th, was switched off. Only the rail's own slot
        // cells count now (runtime views and pouch faces are skipped).
        static bool IsSlotCell(Transform t) => t != null && !runtimeViews.Contains(t.gameObject) && !t.name.StartsWith("Pouch_", System.StringComparison.Ordinal);

        static void FillRail(Transform rail, List<Slots.Slot> slots)
        {
            var own = new List<RectTransform>();
            for (int i = 0; i < rail.childCount; i++) if (IsSlotCell(rail.GetChild(i))) own.Add((RectTransform)rail.GetChild(i));
            if (own.Count > 0)
                for (int i = own.Count; i < slots.Count; i++)
                {
                    var made = Object.Instantiate(own[0].gameObject, rail);
                    made.name = CellPrefix + i;
                    own.Add((RectTransform)made.transform);
                }
            for (int i = 0; i < own.Count; i++)
            {
                var cell = own[i];
                bool used = i < slots.Count;
                cell.gameObject.SetActive(used);
                if (used) cells[CellKey(slots[i])] = cell;
                if (used) RailCaptions[cell] = CaptionFor(slots[i]); else RailCaptions.Remove(cell);   // 1.0.170: Panels' in-cell placeholders
            }
            // Room for each cell's caption strip above it (PlaceCaption), so no caption reaches the cell above.
            var vlg = rail.GetComponent<VerticalLayoutGroup>();
            if (vlg != null)
            {
                vlg.spacing = Mathf.Max(vlg.spacing, CaptionStrip + 4f);
                vlg.padding.top = Mathf.Max(vlg.padding.top, (int)CaptionStrip + 2);
            }
            var grid = rail.GetComponent<GridLayoutGroup>();
            if (grid != null)
            {
                grid.spacing = new Vector2(grid.spacing.x, Mathf.Max(grid.spacing.y, CaptionStrip + 4f));
                grid.padding.top = Mathf.Max(grid.padding.top, (int)CaptionStrip + 2);
            }
            if (slots.Count > own.Count)
                Debug.LogWarning($"[VAInventory] {slots.Count - own.Count} slot(s) have no cell on {rail.name}: it has no cell to clone.");
            BackpackLog.LogDebug($"[VAInventory] rail {rail.name}: {slots.Count} slot cell(s) shown [{string.Join(",", slots.Select(s => s.ID))}], {rail.childCount - own.Count} other child(ren) skipped");
        }

        // ── Pouches (Fire 10-07, Tools\VAINVENTORY_POUCHES_PLAN.md). UI only: the same Slots and grid positions; the pouch
        // popups hold the cells those slots map to, so EquipmentPanel builds, drags and tooltips them as before.
        static readonly List<GameObject> runtimeViews = [];
        static readonly string[] PouchGroups = ["tools", "ammo", "food"];

        internal static List<Slots.Slot> PouchSlots(string group) => group switch
        {
            "tools" => Slots.toolHotbarSlots.Where(s => s != null && s.IsActive).ToList(),
            "ammo" => Slots.GetAmmoSlots().ToList(),
            "food" => Slots.GetFoodSlots().ToList(),
            // 1.0.110: Necklace, Left ring, Right ring (the popup's cell order).
            "jewelry" => new[] { Slots.necklaceSlotID, Slots.ringSlotID, Slots.ringRightSlotID }
                .Select(id => Slots.AllSlots().FirstOrDefault(s => s != null && s.ID == id && s.IsActive)).Where(s => s != null).ToList(),
            _ => []
        };

        internal static bool IsJewelrySlot(Slots.Slot s) =>
            s != null && (s.ID == Slots.necklaceSlotID || s.ID == Slots.ringSlotID || s.ID == Slots.ringRightSlotID);

        static void DropRuntimeViews()
        {
            // Deactivated first: Destroy lands at end of frame, and this frame's layout rebuild must not count them.
            foreach (var go in runtimeViews)
                if (go != null) { go.SetActive(false); Object.Destroy(go); }
            runtimeViews.Clear();
            PouchPopup.Unregister();
        }

        static TMP_FontAsset PanelFont() =>
            characterPanel.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.font != null)?.font
            ?? InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.transform.Find("amount")?.GetComponent<TMP_Text>()?.font;

        // Cell size and pitch of a prefab row's GridLayoutGroup (the vanilla square: 64 at 70).
        static void RowGrid(string row, out Vector2 cellSize, out float spacing)
        {
            var grid = FindDeep(characterPanel, RowPrefix + row)?.Find(CellsName)?.GetComponent<GridLayoutGroup>();
            cellSize = grid != null ? grid.cellSize : new Vector2(64f, 64f);
            spacing = grid != null ? grid.spacing.x : 6f;
        }

        static TextMeshProUGUI NewText(Transform parent, string name, string text, float size, Color color, TextAlignmentOptions align, TMP_FontAsset font)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            return tmp;
        }

        static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static readonly Color Gold = new(0.93f, 0.78f, 0.37f, 1f);
        static readonly Color GoldEdge = new(0.55f, 0.42f, 0.20f, 1f);   // 1.0.183: back (Fire)
        const float CellEdgeAlpha = 0.7f;   // 1.0.183: back (Fire)

        static void BuildPouches()
        {
            Slots.ClearCachedItems();   // 1.0.81: the pouch counts read live, never a stale grid-position cache
            DropRuntimeViews();
            var toolsRow = FindDeep(characterPanel, RowPrefix + "Tools");
            var template = toolsRow?.Find(CellsName)?.Find(CellTemplateName);
            if (toolsRow == null || template == null)
            {
                Debug.LogWarning("[VAInventory] no RowTools/Cells/CellTemplate in the character panel: pouches not built; rebuild the vainventoryui bundle.");
                return;
            }
            RowGrid("Tools", out var cellSize, out var spacing);
            var font = PanelFont();
            var face = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponent<Image>();
            var faceTint = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponent<Selectable>();

            // One centred row where the Tools row was (Fire 10-07): Tools, Ammo, Food pouches + the coin purse, normal-size
            // cells at the row pitch, so four fill the centre column exactly like the Misc row.
            var row = NewRect(toolsRow.parent, "RowPouches");
            row.SetSiblingIndex(toolsRow.GetSiblingIndex() + 1);
            runtimeViews.Add(row.gameObject);
            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.UpperCenter;
            hlg.spacing = spacing;
            var order = new List<string>();
            hlg.childControlWidth = hlg.childControlHeight = true;
            hlg.childForceExpandWidth = hlg.childForceExpandHeight = false;

            var counts = new List<string>();
            foreach (var group in PouchGroups)
            {
                var slots = PouchSlots(group);
                counts.Add($"{group} {slots.Count(s => s.Item != null)}/{slots.Count}");
                if (slots.Count == 0) continue;
                string title = char.ToUpperInvariant(group[0]) + group.Substring(1);
                order.Add(group);

                var holder = PouchHolder(row, "Pouch_" + title, title, cellSize, font);
                var cell = PouchFace(holder, group, cellSize, face, faceTint, font);
                BuildPouchPopup(group, title + " pouch", slots, cell, template, cellSize, spacing, font);
            }

            // 1.0.110 (Fire 10-12, choice (a)): the Jewelry pouch's face sits in the LEFT RAIL where the Ring slot was, captioned
            // "Jewelry" like a rail cell; its popup holds the Necklace, Left ring and Right ring cells (real slots, real grid
            // positions, so nothing saved moves).
            var jewelry = PouchSlots("jewelry");
            counts.Add($"jewelry {jewelry.Count(s => s.Item != null)}/{jewelry.Count}");
            var railLeft = FindDeep(characterPanel, RailLeftName);
            if (jewelry.Count > 0 && railLeft != null)
            {
                var jHolder = NewRect(railLeft, "Pouch_Jewelry");
                runtimeViews.Add(jHolder.gameObject);
                var jLayout = jHolder.gameObject.AddComponent<LayoutElement>();
                jLayout.minWidth = jLayout.preferredWidth = cellSize.x;
                jLayout.minHeight = jLayout.preferredHeight = cellSize.y;
                var trinket = Slots.AllSlots().FirstOrDefault(s => s != null && s.ID == Slots.trinketSlotID && s.IsActive);
                int at = trinket != null && cells.TryGetValue(CellKey(trinket), out var tCell) && tCell != null && tCell.parent == railLeft
                    ? tCell.GetSiblingIndex() + 1 : railLeft.childCount - 1;
                jHolder.SetSiblingIndex(Mathf.Clamp(at, 0, railLeft.childCount - 1));
                var jCell = PouchFace(jHolder, "jewelry", cellSize, face, faceTint, font);
                jCell.anchorMin = Vector2.zero; jCell.anchorMax = Vector2.one;
                jCell.offsetMin = jCell.offsetMax = Vector2.zero;
                var caption = NewText(jHolder, "Caption", "Jewelry", 12f, Color.white, TextAlignmentOptions.Center, font);
                var capRect = (RectTransform)caption.transform;
                capRect.anchorMin = new Vector2(0f, 1f); capRect.anchorMax = new Vector2(1f, 1f);
                capRect.pivot = new Vector2(0.5f, 0f);
                capRect.sizeDelta = new Vector2(16f, CaptionStrip);
                capRect.anchoredPosition = new Vector2(0f, 1f);
                caption.verticalAlignment = VerticalAlignmentOptions.Bottom;
                order.Add("jewelry(rail)");
                BuildPouchPopup("jewelry", "Jewelry", jewelry, jCell, template, cellSize, spacing, font);
            }

            // The coin purse: its REAL slot cell as the 4th cell, labelled like a pouch. EquipmentPanel fills it as any
            // mapped cell (currency-cycle badge, right-click CurrencyPouchPopup), so nothing else changes.
            var purse = Slots.AllSlots().FirstOrDefault(s => s != null && s.IsActive && s.IsCoinPurseSlot);
            if (purse != null)
            {
                var holder = PouchHolder(row, "Pouch_CoinPurse", "Coin purse", cellSize, font);
                var clone = Object.Instantiate(template.gameObject, holder);
                clone.name = CellPrefix + 0;
                clone.SetActive(true);
                var le = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
                le.ignoreLayout = false;
                le.minWidth = le.preferredWidth = cellSize.x;
                le.minHeight = le.preferredHeight = cellSize.y;
                cells[CellKey(purse)] = (RectTransform)clone.transform;
                order.Add(purse.ID);
            }
            PouchPopup.Reapply();
            BackpackLog.LogDebug($"[VAInventory] pouches: {string.Join(" ", counts)}");
            BackpackLog.LogDebug($"[VAInventory] pouch row: {string.Join(",", order)}");
        }

        // A pouch's clickable face (1.0.110: lifted out of BuildPouches unchanged so the rail's Jewelry face is the same thing).
        static RectTransform PouchFace(Transform parent, string group, Vector2 cellSize, Image face, Selectable faceTint, TMP_FontAsset font)
        {
            var cell = NewRect(parent, "PouchCell");
            var cellLayout = cell.gameObject.AddComponent<LayoutElement>();
            cellLayout.preferredWidth = cellSize.x;
            cellLayout.preferredHeight = cellSize.y;
            var bg = cell.gameObject.AddComponent<Image>();
            if (face != null) { bg.sprite = face.sprite; bg.type = Image.Type.Sliced; }
            bg.color = faceTint != null ? faceTint.colors.normalColor : new Color(0.19f, 0.16f, 0.12f, 0.9f);
            bg.raycastTarget = true;
            var edge = cell.gameObject.AddComponent<Outline>();
            edge.effectColor = GoldEdge;
            edge.effectDistance = new Vector2(1.5f, -1.5f);
            var icon = NewRect(cell, "icon");
            icon.anchorMin = Vector2.zero; icon.anchorMax = Vector2.one;
            icon.offsetMin = new Vector2(10f, 10f); icon.offsetMax = new Vector2(-10f, -10f);
            var iconImage = icon.gameObject.AddComponent<Image>();
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;
            var badge = NewText(cell, "badge", "", 12f, Gold, TextAlignmentOptions.TopRight, font);
            var badgeRect = (RectTransform)badge.transform;
            badgeRect.anchorMin = Vector2.zero; badgeRect.anchorMax = Vector2.one;
            badgeRect.offsetMin = new Vector2(2f, 2f); badgeRect.offsetMax = new Vector2(-4f, -2f);
            cell.gameObject.AddComponent<PouchCell>().Init(group, iconImage, badge);
            return cell;
        }

        // A pouch's popup: the group's real slot cells, cloned from the row template exactly as AssignRow does (1.0.110: lifted
        // out of BuildPouches unchanged; the title is passed in).
        static void BuildPouchPopup(string group, string titleText, List<Slots.Slot> slots, RectTransform anchor, Transform template,
            Vector2 cellSize, float spacing, TMP_FontAsset font)
        {
            var popup = NewRect(characterPanel, "PouchPopup_" + group);
            runtimeViews.Add(popup.gameObject);
            popup.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            popup.anchorMin = popup.anchorMax = new Vector2(0.5f, 0.5f);
            popup.pivot = new Vector2(0.5f, 0f);
            var pImg = popup.gameObject.AddComponent<Image>();
            pImg.color = new Color(0.13f, 0.10f, 0.07f, 0.98f);
            pImg.raycastTarget = true;
            var pEdge = popup.gameObject.AddComponent<Outline>();
            pEdge.effectColor = GoldEdge;
            pEdge.effectDistance = new Vector2(2f, -2f);
            var pv = popup.gameObject.AddComponent<VerticalLayoutGroup>();
            pv.padding = new RectOffset(10, 10, 6, 10);
            pv.spacing = 4f;
            pv.childAlignment = TextAnchor.UpperCenter;
            pv.childControlWidth = pv.childControlHeight = true;
            pv.childForceExpandWidth = pv.childForceExpandHeight = false;
            var fit = popup.gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var pTitle = NewText(popup, "title", titleText, 15f, Gold, TextAlignmentOptions.Center, font);
            pTitle.gameObject.AddComponent<LayoutElement>().preferredHeight = 20f;
            var popupCells = NewRect(popup, CellsName);
            var ph = popupCells.gameObject.AddComponent<HorizontalLayoutGroup>();
            ph.spacing = spacing;
            ph.childControlWidth = ph.childControlHeight = true;
            ph.childForceExpandWidth = ph.childForceExpandHeight = false;
            for (int i = 0; i < slots.Count; i++)
            {
                var clone = Object.Instantiate(template.gameObject, popupCells);
                clone.name = CellPrefix + i;
                clone.SetActive(true);
                var le = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
                le.ignoreLayout = false;
                le.minWidth = le.preferredWidth = cellSize.x;
                le.minHeight = le.preferredHeight = cellSize.y;
                cells[CellKey(slots[i])] = (RectTransform)clone.transform;
            }
            popup.gameObject.SetActive(false);
            PouchPopup.Register(group, popup, anchor);
        }

        // A pouch-row column: the always-on label over one normal-size cell.
        static RectTransform PouchHolder(Transform row, string name, string title, Vector2 cellSize, TMP_FontAsset font)
        {
            var holder = NewRect(row, name);
            var vlg = holder.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.spacing = 2f;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = vlg.childForceExpandHeight = false;
            var label = NewText(holder, "Label", title, 14f, Color.white, TextAlignmentOptions.Center, font);
            label.enableAutoSizing = true;   // "Coin purse" must fit one cell width
            label.fontSizeMin = 10f;
            label.fontSizeMax = 14f;
            var labelLayout = label.gameObject.AddComponent<LayoutElement>();
            labelLayout.preferredHeight = 18f;
            labelLayout.preferredWidth = cellSize.x;
            return holder;
        }

        // ── The worn bag(s) inside the panel (Fire 10-07, panel_backpack mockup): below Misc, a header "Backpack: <name>"
        // with "Quality N · C × R" on the right, the bag's storage grid (its IsBackpackStorageSlot slots, mapped to cells so
        // EquipmentPanel fills them as any other), and an upgraded bag's special-slot rail at the right edge. Replaces the
        // floating bag window in Unified. Two bags = two sections. The panel's VerticalLayoutGroup + size fitter grow the
        // backdrop with them.
        const float BagHeaderHeight = 18f;
        const float BagSectionGap = 2f;

        static void BuildBagSections()
        {
            var template = FindDeep(characterPanel, RowPrefix + "Misc")?.Find(CellsName)?.Find(CellTemplateName)
                           ?? FindDeep(characterPanel, RowPrefix + "Tools")?.Find(CellsName)?.Find(CellTemplateName);
            var bagSlots = Slots.AllSlots().Where(s => s != null && s.IsActive && s.IsBackpackStorageSlot).ToList();
            if (!BackpackBridge.IsAvailable || !BackpackBridge.SlotStorageActive || bagSlots.Count == 0 || template == null)
            {
                BackpackLog.LogDebug($"[VAInventory] bag section: none (available={BackpackBridge.IsAvailable} slots={bagSlots.Count})");
                return;
            }
            RowGrid("Misc", out var cellSize, out var spacing);
            float pitch = cellSize.x + spacing;
            var font = PanelFont();
            var bags = BackpackBridge.GetEquippedBackpacks(Player.m_localPlayer);
            var elementPrefab = InventoryGui.instance?.m_playerGrid?.m_elementPrefab;

            foreach (var byBag in bagSlots.GroupBy(s => Slots.GetBackpackPrefabFromSlot(s) ?? "Unknown"))
            {
                var slots = byBag.OrderBy(s => s.GridPosition.y).ThenBy(s => s.GridPosition.x).ToList();
                var ys = slots.Select(s => s.GridPosition.y).Distinct().OrderBy(y => y).ToList();
                var xs = slots.Select(s => s.GridPosition.x).Distinct().OrderBy(x => x).ToList();
                int rows = ys.Count, cols = xs.Count;
                var bag = bags.FirstOrDefault(b => b != null && b.m_dropPrefab != null && b.m_dropPrefab.name == byBag.Key);
                string name = bag != null ? Localization.instance.Localize(bag.m_shared.m_name) : slots[0].Name;
                int quality = bag?.m_quality ?? 1;
                var railBag = quality >= 2 ? BagSpecialRail.FindBag(byBag.Key) : null;
                var kinds = BagSpecialRail.KindsFor(railBag);

                float gridW = cols * pitch - spacing, gridH = rows * pitch - spacing;
                float railW = kinds.Length > 0 ? BagSpecialRail.RailGap * 3f + cellSize.x : 0f;
                float width = gridW + railW;
                float height = BagSectionGap + BagHeaderHeight + Mathf.Max(gridH, kinds.Length * pitch - spacing);

                var section = NewRect(characterPanel, "BagSection_" + byBag.Key);
                runtimeViews.Add(section.gameObject);
                section.sizeDelta = new Vector2(width, height);
                var layout = section.gameObject.AddComponent<LayoutElement>();
                layout.minWidth = layout.preferredWidth = width;
                layout.minHeight = layout.preferredHeight = height;

                var rule = NewRect(section, "rule");
                rule.anchorMin = new Vector2(0f, 1f); rule.anchorMax = new Vector2(1f, 1f); rule.pivot = new Vector2(0.5f, 1f);
                rule.sizeDelta = new Vector2(0f, 1f); rule.anchoredPosition = new Vector2(0f, -BagSectionGap / 2f);
                var ruleImage = rule.gameObject.AddComponent<Image>();
                ruleImage.color = new Color(GoldEdge.r, GoldEdge.g, GoldEdge.b, 0.6f);
                ruleImage.raycastTarget = false;

                var header = NewText(section, "header", "Backpack: " + name, 16f, Gold, TextAlignmentOptions.MidlineLeft, font);
                PlaceTop((RectTransform)header.transform, BagSectionGap, BagHeaderHeight);
                var info = NewText(section, "info", $"Quality {quality} · {cols} × {rows}", 12f, new Color(0.85f, 0.80f, 0.70f, 1f),
                    TextAlignmentOptions.MidlineRight, font);
                PlaceTop((RectTransform)info.transform, BagSectionGap, BagHeaderHeight);

                float top = BagSectionGap + BagHeaderHeight;
                foreach (var slot in slots)
                {
                    int r = ys.IndexOf(slot.GridPosition.y), c = xs.IndexOf(slot.GridPosition.x);
                    var clone = Object.Instantiate(template.gameObject, section);
                    clone.name = CellPrefix + (r * cols + c);
                    clone.SetActive(true);
                    var le = clone.GetComponent<LayoutElement>();
                    if (le != null) le.ignoreLayout = true;
                    var rt = (RectTransform)clone.transform;
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                    rt.sizeDelta = cellSize;
                    rt.anchoredPosition = new Vector2(c * pitch, -(top + r * pitch));
                    cells[CellKey(slot)] = rt;
                }

                if (kinds.Length > 0 && elementPrefab != null)
                {
                    var host = NewRect(section, "RailHost");
                    host.anchorMin = host.anchorMax = host.pivot = new Vector2(1f, 1f);
                    host.sizeDelta = new Vector2(cellSize.x, kinds.Length * pitch);
                    host.anchoredPosition = new Vector2(0f, -top);
                    BagSpecialRail.Build(host, railBag, kinds, elementPrefab, Vector2.zero, pitch);
                }
                BackpackLog.LogDebug($"[VAInventory] bag section: {name} q{quality} {cols}x{rows} rail={(kinds.Length > 0 ? string.Join(",", kinds) : "none")}");
            }
        }

        static void PlaceTop(RectTransform rect, float top, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(0f, -(top + height));
            rect.offsetMax = new Vector2(0f, -top);
        }

        static void AssignRow(string row, List<Slots.Slot> slots)
        {
            var rowRoot = FindDeep(characterPanel, RowPrefix + row);
            var container = rowRoot?.Find(CellsName);
            var template = container?.Find(CellTemplateName);
            if (container == null || template == null)
            {
                if (slots.Count > 0)
                    Debug.LogWarning($"[VAInventory] {slots.Count} {row} slot(s) have no row in the character panel; rebuild the vainventoryui bundle.");
                return;
            }
            rowRoot.gameObject.SetActive(slots.Count > 0);

            // Drop last run's clones; counts change when the player edits config or equips a bag. Deactivated
            // first: Destroy lands at end of frame, and the layout rebuilt this frame must not count them.
            for (int i = container.childCount - 1; i >= 0; i--)
            {
                var child = container.GetChild(i);
                if (child == template) continue;
                child.gameObject.SetActive(false);
                Object.Destroy(child.gameObject);
            }

            for (int i = 0; i < slots.Count; i++)
            {
                var clone = Object.Instantiate(template.gameObject, container);
                clone.name = CellPrefix + i;
                clone.SetActive(true);
                cells[CellKey(slots[i])] = (RectTransform)clone.transform;
            }
        }

        // The prefab's cells are clones of vanilla's inventory element, so they carry its placeholder overlays
        // (amount, quality, durability text). Each cell is only a container for the live element instantiated
        // into it, which brings its own face and overlays; left in, the placeholders drew under every slot.
        /// <summary>
        /// 1.0.196 (Fire: the companion gets "an IDENTICAL panel setup"): a second CharacterPanel built from the SAME prefab asset by the
        /// SAME steps the player panel takes (StripEditorOnly, the cell-chrome strip, BindChrome), under <paramref name="parent"/>. Inactive
        /// until the caller fills it. Null when the bundle or the vanilla Player panel is missing.
        /// </summary>
        /// <summary>1.0.199 (companions get every player slot): each player-panel slot cell as (Core companion section, index in it, the
        /// cell's path under the character panel), so a clone of the panel can find the same cell. Section = the Slots ID, except
        /// "Tools" (tool hotbar), "Misc" (Misc*), "BackpackStorage" (BackpackStorage_*).</summary>
        internal static List<(string section, int index, string path, string caption)> SectionCells()
        {
            var list = new List<(string, int, string, string)>();
            if (characterPanel == null) return list;
            var counts = new Dictionary<string, int>();
            foreach (var s in Slots.AllSlots())
            {
                if (s == null || !s.IsActive || s.IsCurrencyVaultSlot) continue;
                string section = Slots.toolHotbarSlots.Contains(s) ? "Tools" : s.IsMiscSlot ? "Misc" : s.IsBackpackStorageSlot ? "BackpackStorage" : s.ID;
                counts.TryGetValue(section, out int i); counts[section] = i + 1;
                if (!cells.TryGetValue(CellKey(s), out var cell) || cell == null || !cell.IsChildOf(characterPanel)) continue;
                string path = cell.name;
                for (var t = cell.parent; t != null && t != characterPanel; t = t.parent) path = t.name + "/" + path;
                RailCaptions.TryGetValue(cell, out var caption);   // 1.0.200: the rail label (Helmet, Chest, ...) - it lived on the player's element
                list.Add((section, i, path, caption));
            }
            return list;
        }

        internal static RectTransform BuildCharacterPanelCopy(Transform parent, string name)
        {
            if (!EnsurePrefab()) return null;
            var src = prefab.transform.Find(CharacterPanelPath);
            var root = InventoryGui.instance != null ? InventoryGui.instance.m_inventoryRoot : null;
            var vanillaPlayer = root != null ? root.Find(PlayerPanelName) as RectTransform : null;
            if (src == null || vanillaPlayer == null) return null;
            var copy = Object.Instantiate(src.gameObject, parent, false);
            copy.SetActive(false);
            copy.name = name;
            StripEditorOnly(copy);
            foreach (var cell in copy.GetComponentsInChildren<RectTransform>(true).Where(t => t.name == CellTemplateName || t.name.StartsWith(CellPrefix)).ToList())
            {
                for (int i = cell.childCount - 1; i >= 0; i--) Object.Destroy(cell.GetChild(i).gameObject);
                var face = cell.GetComponent<Image>();
                if (face != null) face.enabled = true;   // a companion cell shows its own face (Core's Slot pass styles it)
            }
            BindChrome(copy.transform, vanillaPlayer);
            EquipmentPanel.PanelLog?.LogDebug($"unified: character panel copy '{name}' built from {PrefabName}/{CharacterPanelPath}");
            return (RectTransform)copy.transform;
        }

        internal const string PreviewMount = PreviewMountName;
        internal const string LeftRail = RailLeftName, RightRail = RailRightName, CellPrefixName = CellPrefix;

        static void StripCellChrome()
        {
            if (characterPanel == null) return;
            foreach (var cell in characterPanel.GetComponentsInChildren<RectTransform>(true)
                         .Where(t => t.name == CellTemplateName || t.name.StartsWith(CellPrefix)).ToList())
            {
                for (int i = cell.childCount - 1; i >= 0; i--)
                    Object.Destroy(cell.GetChild(i).gameObject);
                var face = cell.GetComponent<Image>();
                if (face != null) face.enabled = false;
            }
        }

        /// <summary>
        /// 1.0.187 (R-1015J probe: on the FIRST open the empty Misc / bag cells drew NOTHING - "Cell_0 ... item_background ... enabled False"):
        /// StripCellChrome turns every cell's own face off because the live element instantiated into it brings its own; but an EMPTY cell
        /// gets no element until a later rebuild, so on the first open it had no face at all. Under Panels each cell's face is on exactly
        /// while it holds no InventoryElement (Core's skin then styles it as a Slot); a cell with an element keeps its face off (one face per
        /// slot). Returns how many faces changed.
        /// </summary>
        internal static int SyncEmptyCellFaces()
        {
            if (characterPanel == null || !PanelsMode) return 0;
            int n = 0;
            foreach (var cell in characterPanel.GetComponentsInChildren<RectTransform>(true))
            {
                if (!cell.name.StartsWith(CellPrefix, System.StringComparison.Ordinal)) continue;
                var face = cell.GetComponent<Image>();
                if (face == null) continue;
                bool empty = cell.GetComponentInChildren<InventoryElement>(true) == null;
                if (!face.enabled) { face.enabled = true; n++; }   // 1.0.214: the Cell_ face is THE square (the element face inside is transparent); was "on only while empty"
            }
            return n;
        }

        /// <summary>
        /// 1.0.195 ([lead] for Fire: Misc / bag squares GREY on later opens): Core's Slot pass treats a Cell_ that holds an InventoryElement
        /// as a FRAME and leaves its face vanilla (grey item_background). Under Panels every Cell_ face in the character panel takes the SAME
        /// look Core gave the main grid's cells - copied from main grid cell 0 (sprite, type, colour, material) with a neutral (white) tint -
        /// so whatever draws in a cell is brown. Runs right after each Core skin pass. Returns the faces changed.
        /// Reused from: Core CraftingSkin Role.Slot (its output on the main grid is the reference; nothing is invented here).
        /// </summary>
        internal static int StyleCellFramesLikeGrid() => StyleCellFramesLikeGrid(characterPanel);

        /// <summary>1.0.200: the same for any panel (the companion panel clone).</summary>
        internal static int StyleCellFramesLikeGrid(RectTransform panel)
        {
            if (panel == null || !PanelsMode) return 0;
            return ApplySlotLook(panel);   // 1.0.210: the ONLY slot-face writer (the old copy below is dead code, kept for history)
            var grid = InventoryGui.instance != null ? InventoryGui.instance.m_playerGrid : null;
            var model = grid != null && grid.m_gridRoot != null && grid.m_gridRoot.childCount > 0 ? grid.m_gridRoot.GetChild(0).GetComponent<Image>() : null;
            if (model == null || model.sprite == null || model.sprite.name.StartsWith("item_background", System.StringComparison.Ordinal)) return 0;   // Core hasn't styled the grid yet
            int n = 0;
            foreach (var cell in panel.GetComponentsInChildren<RectTransform>(true))
            {
                if (!cell.name.StartsWith(CellPrefix, System.StringComparison.Ordinal)) continue;
                var face = cell.GetComponent<Image>();
                if (face == null) continue;
                bool changed = false;
                if (face.sprite != model.sprite) { face.sprite = model.sprite; face.type = model.type; changed = true; }
                if (face.color != model.color) { face.color = model.color; changed = true; }
                if (face.material != model.material) { face.material = model.material; changed = true; }
                var sel = cell.GetComponent<Selectable>();
                if (sel != null && sel.colors.normalColor != Color.white) { var cb = sel.colors; cb.normalColor = Color.white; sel.colors = cb; changed = true; }
                if (CopyOutline(cell, grid.m_gridRoot.GetChild(0))) changed = true;   // 1.0.200: the grid cell's KitSlotOutline too (same look, not just the fill)
                if (changed) n++;
            }
            return n;
        }

        /// <summary>1.0.197 ([lead]: Misc / bag squares still GREY on open #3 with 1.0.195): every InventoryElement on the character panel
        /// copies the MAIN grid's element 0 as rendered: its face Image (sprite, type, colour, material), its Selectable ColorBlock and its
        /// CanvasRenderer colour. Idempotent; returns how many changed. Logs both rendered colours once per open.</summary>
        internal static int StyleElementsLikeGrid(bool log) => StyleElementsLikeGrid(characterPanel, log);

        /// <summary>1.0.203: the same for any root (the companion storage grid; its vanilla elements were see-through).</summary>
        internal static int StyleElementsLikeGrid(RectTransform panel, bool log)
        {
            if (panel == null || !PanelsMode) return 0;
            { int c = log ? ForceSlotLook(panel) : ApplySlotLook(panel); if (log) EquipmentPanel.PanelLog?.LogDebug($"panels: slot look = the Tools pouch cell (Core ApplySlotLook) under {panel.name}: {c} changed"); return c; }   // 1.0.210: the ONLY writer
            var grid = InventoryGui.instance != null ? InventoryGui.instance.m_playerGrid : null;
            var modelEl = EmptyGridCell(grid);   // 1.0.201 ([lead]: copy an EMPTY cell, never cell 0 with its item)
            var model = modelEl != null ? modelEl.GetComponent<Image>() : null;
            if (model == null || model.sprite == null || model.sprite.name.StartsWith("item_background", System.StringComparison.Ordinal)) return 0;   // Core hasn't styled the grid yet
            var modelSel = modelEl.GetComponent<Selectable>();
            Color modelCr = model.canvasRenderer.GetColor();
            int n = 0; string sample = null, bagSample = null;
            foreach (var el in panel.GetComponentsInChildren<InventoryElement>(true))
            {
                var face = el.GetComponent<Image>();
                if (face == null) continue;
                bool changed = false;
                if (face.sprite != model.sprite) { face.sprite = model.sprite; face.type = model.type; changed = true; }
                if (face.color != model.color) { face.color = model.color; changed = true; }
                if (face.material != model.material) { face.material = model.material; changed = true; }
                var sel = el.GetComponent<Selectable>();
                if (sel != null && modelSel != null && !sel.colors.Equals(modelSel.colors)) { sel.colors = modelSel.colors; changed = true; }
                if (face.canvasRenderer.GetColor() != modelCr) { face.canvasRenderer.SetColor(modelCr); changed = true; }
                // 1.0.201 (R-1015T: 1.0.200 copied cell 0's ITEM children - icon, equiped - onto every slot): ONLY the empty-slot look,
                // KitSlotOutline + dropFocus; icon / equiped / queued / amount / quality / durability / binding / noteleport / food stay vanilla's
                foreach (var g in el.GetComponentsInChildren<Graphic>(true))
                {
                    if (g == face || (g.name != "KitSlotOutline" && g.name != "dropFocus")) continue;
                    var mt = modelEl.transform.Find(g.name);
                    var mg = mt != null ? mt.GetComponent<Graphic>() : null;
                    if (mg == null || g is TMP_Text) continue;
                    if (g.enabled != mg.enabled) { g.enabled = mg.enabled; changed = true; }
                    if (g.gameObject.activeSelf != mg.gameObject.activeSelf) { g.gameObject.SetActive(mg.gameObject.activeSelf); changed = true; }
                    if (g.color != mg.color) { g.color = mg.color; changed = true; }
                    if (g.material != mg.material) { g.material = mg.material; changed = true; }
                    if (g is Image gi && mg is Image mi && gi.sprite != mi.sprite) { gi.sprite = mi.sprite; gi.type = mi.type; changed = true; }
                    if (g.canvasRenderer.GetColor() != mg.canvasRenderer.GetColor()) { g.canvasRenderer.SetColor(mg.canvasRenderer.GetColor()); changed = true; }
                }
                if (changed) n++;
                if (log && sample == null) sample = $"{el.name} face {face.color} x cr {face.canvasRenderer.GetColor()} x a {face.canvasRenderer.GetInheritedAlpha():0.00}";
                if (log && bagSample == null && InBag(el.transform)) bagSample = Children(el.transform);
            }
            if (log) EquipmentPanel.PanelLog?.LogDebug($"panels: element children: empty main cell [{Children(modelEl.transform)}] vs bag cell [{bagSample ?? "none"}]");
            if (log) EquipmentPanel.PanelLog?.LogDebug($"panels: element look copy: {n} changed; main cell 0 face {model.color} x cr {modelCr} x a {model.canvasRenderer.GetInheritedAlpha():0.00}; panel {sample ?? "none"}");
            return n;
        }

        // ── 1.0.208 (Fire: "all grid squares should look like our Jewelry / Tools / Ammo / Food squares: the DARK square bordered by the
        // LIGHTER gold colour"): ONE slot look everywhere, read LIVE from the Tools pouch cell (its face Image + its gold Outline) and laid on
        // every slot face: main grid, chest, Misc, bags, rails, Coin purse, the companion's cells, the hotbars. Empty-slot look only: icons,
        // amounts, equipped / queued markers stay vanilla's.
        internal struct SlotLookData { public Sprite Sprite; public Image.Type Type; public Color Color; public Material Material; public Color Edge; public Vector2 EdgeDistance; }
        static SlotLookData s_look; static bool s_lookOk; static float s_lookAt = -10f; static bool s_lookLogged;

        internal static bool PouchLook(out SlotLookData look)
        {
            if (Time.unscaledTime - s_lookAt > 2f || !s_lookOk)
            {
                s_lookAt = Time.unscaledTime;
                var holder = characterPanel != null ? FindDeep(characterPanel, "Pouch_Tools") : null;
                var cell = holder != null ? FindDeep(holder, "PouchCell") : null;
                var img = cell != null ? cell.GetComponent<Image>() : null;
                var edge = cell != null ? cell.GetComponent<Outline>() : null;
                s_lookOk = img != null && img.sprite != null;
                if (s_lookOk)
                {
                    s_look = new SlotLookData { Sprite = img.sprite, Type = img.type, Color = img.color, Material = img.material,
                                                Edge = edge != null ? edge.effectColor : GoldEdge, EdgeDistance = edge != null ? edge.effectDistance : new Vector2(1.5f, -1.5f) };
                    if (!s_lookLogged) { s_lookLogged = true; EquipmentPanel.PanelLog?.LogDebug($"panels: slot look = the Tools pouch cell: sprite '{img.sprite.name}' {img.type}, face {img.color}, edge {s_look.Edge} @ {s_look.EdgeDistance}"); }
                }
            }
            look = s_look;
            return s_lookOk;
        }

        // one face: the pouch look; returns true when something changed
        // 1.0.224 (UI audit, [generator]/[lead]: ONE slot-look API): Core 0.2.572 FiresCore.UI.Shell.FiresSkin.ApplySlotLook(Image, SlotState)
        // is THE writer of a slot face; VAInventory calls it (by reflection: older Core = the pouch look below, as before). Counted for the
        // READY proof: "N faces via FiresSkin.ApplySlotLook".
        static System.Reflection.MethodInfo s_coreSlotLook; static object s_coreNormal; static bool s_coreLooked;
        static readonly HashSet<int> s_coreFaces = new HashSet<int>();
        static float s_nextCoreLog;
        static bool CoreSlotLook(Image face)
        {
            if (!s_coreLooked)
            {
                s_coreLooked = true;
                var t = System.Type.GetType("FiresCore.UI.Shell.FiresSkin, FiresUnifiedCore");
                var stateT = System.Type.GetType("FiresCore.UI.Shell.FiresSkin+SlotState, FiresUnifiedCore");
                if (t != null && stateT != null)
                {
                    s_coreSlotLook = t.GetMethod("ApplySlotLook", new[] { typeof(Image), stateT });
                    try { s_coreNormal = System.Enum.Parse(stateT, "Normal"); } catch { s_coreSlotLook = null; }
                }
                EquipmentPanel.PanelLog?.LogDebug(s_coreSlotLook != null ? "panels: slot faces via Core FiresSkin.ApplySlotLook" : "panels: no Core FiresSkin.ApplySlotLook (Core < 0.2.572); VAInventory's pouch look");
            }
            if (s_coreSlotLook == null) return false;
            try { s_coreSlotLook.Invoke(null, new[] { face, s_coreNormal }); } catch (System.Exception e) { EquipmentPanel.PanelLog?.LogWarning("panels: FiresSkin.ApplySlotLook failed: " + (e.InnerException ?? e).Message); s_coreSlotLook = null; return false; }
            if (s_coreFaces.Add(face.GetInstanceID()) && Time.unscaledTime >= s_nextCoreLog)
            {
                s_nextCoreLog = Time.unscaledTime + 5f;
                EquipmentPanel.PanelLog?.LogDebug($"panels: {s_coreFaces.Count} faces via FiresSkin.ApplySlotLook");
            }
            return true;
        }

        // 1.0.229 ([lead]/Fire TARGET_slot_style: a visible GAP of dark plate between the gold borders on every grid): the square is drawn on an
        // INSET child face (2 px in on each side, behind the icon), and the slot's own Image becomes a transparent click area - ~4 px of plate
        // between neighbours everywhere, with NO layout change (panel sizes, pitches and fits stay exactly as they are)
        const string InsetName = "VAInvSlotFace";
        const float InsetPx = 1f;   // +2 px between neighbours: gap/cell ~0.10 -> ~0.13 (TARGET_slot_style)
        internal static bool ApplyLook(Image face, in SlotLookData look)
        {
            if (face == null) return false;
            return ApplyLookDirect(face, look);   // 1.0.230 ([lead]/Fire: the "gap" was background - no inset; the slot Image itself carries the look)
#pragma warning disable CS0162
            if (face.name == InsetName) return ApplyLookDirect(face, look);
            var inner = face.transform.Find(InsetName)?.GetComponent<Image>();
            bool made = false;
            if (inner == null)
            {
                var go = new GameObject(InsetName, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                go.GetComponent<LayoutElement>().ignoreLayout = true;
                var rt = (RectTransform)go.transform; rt.SetParent(face.transform, false);
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(InsetPx, InsetPx); rt.offsetMax = new Vector2(-InsetPx, -InsetPx);
                inner = go.GetComponent<Image>(); inner.raycastTarget = false;
                made = true;
            }
            if (inner.transform.GetSiblingIndex() != 0) inner.transform.SetAsFirstSibling();   // behind the icon / counts / markers
            bool changed = ClearOuter(face) | made;
            return ApplyLookDirect(inner, look) | changed;
#pragma warning restore CS0162
        }

        // the slot's own Image under an inset face: transparent (keeps its raycast = the click area), no border, never repainted by Core
        static bool ClearOuter(Image face)
        {
            bool changed = false;
            var c = face.color; if (c.a != 0f) { c.a = 0f; face.color = c; changed = true; }
            var o = face.GetComponent<Outline>(); if (o != null && o.enabled) { o.enabled = false; changed = true; }
            var sel = face.GetComponent<Selectable>();
            if (sel != null && sel.colors.normalColor != Color.white) { var cb = sel.colors; cb.normalColor = Color.white; sel.colors = cb; changed = true; }
            if (!s_leafLooked) { s_leafLooked = true; s_leaf = System.Type.GetType("FiresCore.UI.Shell.Kit.KitLeaf, FiresUnifiedCore"); }
            if (s_leaf != null && face.GetComponent(s_leaf) == null) { face.gameObject.AddComponent(s_leaf); changed = true; }
            var kit = face.transform.Find("KitSlotOutline"); if (kit != null && kit.gameObject.activeSelf) { kit.gameObject.SetActive(false); changed = true; }
            if (changed) NoteWrite(face);
            return changed;
        }

        static bool ApplyLookDirect(Image face, in SlotLookData look)
        {
            if (CoreSlotLook(face)) { NoteWrite(face); return false; }   // 1.0.224: Core is the slot-face writer (it is idempotent; "changed" is Core's)
            bool changed = false;
            if (face.sprite != look.Sprite) { face.sprite = look.Sprite; changed = true; }
            if (face.type != look.Type) { face.type = look.Type; changed = true; }
            if (face.color != look.Color) { face.color = look.Color; changed = true; }
            if (face.material != look.Material) { face.material = look.Material; changed = true; }
            if (face.canvasRenderer.GetColor() != Color.white) { face.canvasRenderer.SetColor(Color.white); changed = true; }
            var sel = face.GetComponent<Selectable>();
            if (sel != null && sel.colors.normalColor != Color.white) { var cb = sel.colors; cb.normalColor = Color.white; sel.colors = cb; changed = true; }
            var o = face.GetComponent<Outline>() ?? face.gameObject.AddComponent<Outline>();
            if (o.effectColor != look.Edge) { o.effectColor = look.Edge; changed = true; }
            if (o.effectDistance != look.EdgeDistance) { o.effectDistance = look.EdgeDistance; changed = true; }
            if (!o.enabled) { o.enabled = true; changed = true; }
            // 1.0.210 ([lead]: ONE writer): Core's CraftingSkin skips a graphic carrying KitLeaf, so its Role.Slot pass never repaints a slot
            // face again; the panels keep Core's plates (skin still runs for them), the slots have exactly one writer: this
            if (!s_leafLooked) { s_leafLooked = true; s_leaf = System.Type.GetType("FiresCore.UI.Shell.Kit.KitLeaf, FiresUnifiedCore"); }
            if (s_leaf != null && face.GetComponent(s_leaf) == null) { face.gameObject.AddComponent(s_leaf); changed = true; }
            if (changed) NoteWrite(face);
            var kit = face.transform.Find("KitSlotOutline");   // Core's slot outline: the pouch's gold Outline replaces it (one border)
            if (kit != null && kit.gameObject.activeSelf) { kit.gameObject.SetActive(false); changed = true; }
            var drop = face.transform.Find("dropFocus")?.GetComponent<Image>();
            if (drop != null && drop.color.a > 0.001f && !drop.color.Equals(new Color(1f, 1f, 1f, 0.5f))) { }   // vanilla drives dropFocus during drags; left alone
            return changed;
        }

        static System.Type s_leaf; static bool s_leafLooked;

        // ── 1.0.210 PROOF ([lead]: "log the count of writers that touched cell 0 of Misc in one open (must be 1)") ──
        // The watched face = Misc cell 0's visible face (its element's Image when it holds one, else the cell's own). ApplyLook notes its own
        // writes; every frame the face's look (sprite, colour, material, type, rendered colour) is compared with what ApplyLook last left:
        // any other difference = a FOREIGN writer. Logged once per open, 6 s in.
        static Image s_watch; static string s_watchSig; static int s_ownWrites, s_foreign; static float s_watchFrom; static bool s_watchLogged;
        static readonly List<string> s_foreignWhat = new List<string>();

        // 1.0.214 ([lead]): every graphic of a panel's Misc cell 0 (the Cell_ itself and everything under it), draw order, what it renders
        // 1.0.227 ([lead]/Fire: the EMPTY rail slots still render GREY): the first empty rail cell's every graphic (draw order, on/off,
        // colour, rendered colour, alpha, sprite, outline), so the grey layer is named, not guessed
        internal static void DumpEmptyRails(string when) =>
            EquipmentPanel.PanelLog?.LogDebug($"panels: EMPTY rail cell STACKS ({when}): left [{EmptyRailStack(RailLeftName)}] right [{EmptyRailStack(RailRightName)}]");

        static string EmptyRailStack(string railName)
        {
            var rail = characterPanel != null ? FindDeep(characterPanel, railName) : null;
            if (rail == null) return "no rail";
            foreach (var t in rail.GetComponentsInChildren<RectTransform>(false))
            {
                if (!t.name.StartsWith(CellPrefix, System.StringComparison.Ordinal)) continue;
                bool filled = false;
                foreach (var e in t.GetComponentsInChildren<InventoryElement>(false)) if (e.m_icon != null && e.m_icon.enabled) { filled = true; break; }
                if (filled) continue;
                var parts = new List<string>();
                foreach (var g in t.GetComponentsInChildren<Graphic>(true))
                {
                    var o = g.GetComponent<Outline>();
                    parts.Add($"{g.name}#{g.transform.GetSiblingIndex()} {(g.isActiveAndEnabled ? "ON" : "off")} {g.color} cr {g.canvasRenderer.GetColor()} a {g.canvasRenderer.GetInheritedAlpha():0.00} {(g is Image i && i.sprite != null ? i.sprite.name : "")}{(o != null && o.enabled ? " +outline" : "")}");
                }
                return t.name + ": " + string.Join(" ; ", parts.ToArray());
            }
            return "no empty cell";
        }

        static string Stack(Transform panel)
        {
            if (panel == null) return "no panel";
            var row = FindDeep(panel, RowPrefix + "Misc");
            Transform cell = null;
            if (row != null) foreach (var t in row.GetComponentsInChildren<RectTransform>(false)) if (t.name.StartsWith(CellPrefix, System.StringComparison.Ordinal)) { cell = t; break; }
            if (cell == null) return "no Misc cell";
            var parts = new List<string>();
            foreach (var g in cell.GetComponentsInChildren<Graphic>(true))
            {
                var o = g.GetComponent<Outline>();
                parts.Add($"{g.name}#{g.transform.GetSiblingIndex()} {(g.isActiveAndEnabled ? "ON" : "off")} {g.color} cr {g.canvasRenderer.GetColor()} a {g.canvasRenderer.GetInheritedAlpha():0.00} {(g is Image i && i.sprite != null ? i.sprite.name : "")}{(o != null && o.enabled ? " +outline" : "")}");
            }
            return string.Join(" ; ", parts.ToArray());
        }

        static string Sig(Image f) => f == null ? "" : $"{(f.sprite != null ? f.sprite.name : "-")}|{f.type}|{f.color}|{(f.material != null ? f.material.name : "-")}|{f.canvasRenderer.GetColor()}";

        static void NoteWrite(Image face) { if (face != null && face == s_watch) { s_ownWrites++; s_watchSig = Sig(face); } }

        internal static void WatchOpen() { s_watch = null; s_watchSig = null; s_ownWrites = 0; s_foreign = 0; s_watchFrom = Time.unscaledTime; s_watchLogged = false; s_foreignWhat.Clear(); }

        internal static void WatchTick()
        {
            if (characterPanel == null || !PanelsMode) return;
            if (s_watch == null)
            {
                var row = FindDeep(characterPanel, RowPrefix + "Misc");
                Transform cell = null;
                if (row != null) foreach (var t in row.GetComponentsInChildren<RectTransform>(false)) if (t.name.StartsWith(CellPrefix, System.StringComparison.Ordinal)) { cell = t; break; }
                if (cell == null) return;
                var el = cell.GetComponentInChildren<InventoryElement>(false);
                s_watch = cell.GetComponent<Image>();   // 1.0.214: the Cell_ face is the square now
                s_watchSig = Sig(s_watch);
            }
            else
            {
                string now = Sig(s_watch);
                if (s_watchSig != null && now != s_watchSig) { s_foreign++; if (s_foreignWhat.Count < 3) s_foreignWhat.Add($"{s_watchSig} -> {now}"); s_watchSig = now; }
            }
            if (!s_watchLogged && Time.unscaledTime - s_watchFrom > 6f)
            {
                s_watchLogged = true;
                int writers = (s_ownWrites > 0 ? 1 : 0) + (s_foreign > 0 ? 1 : 0);
                EquipmentPanel.PanelLog?.LogDebug($"panels: WRITERS on Misc cell 0 this open: {writers} (ApplySlotLook wrote {s_ownWrites}x; foreign changes {s_foreign}{(s_foreignWhat.Count > 0 ? ": " + string.Join(" ; ", s_foreignWhat.ToArray()) : "")}) face '{(s_watch != null ? s_watch.name : "none")}'");
                EquipmentPanel.PanelLog?.LogDebug($"panels: Misc cell 0 STACK player [{Stack(characterPanel)}] vs companion [{Stack(CompanionPanel.Current != null ? CompanionPanel.Current.Root : null)}]");
                EquipmentPanel.PanelLog?.LogDebug($"panels: EMPTY rail cell STACKS (1.0.227): left [{EmptyRailStack(RailLeftName)}] right [{EmptyRailStack(RailRightName)}]");
            }
        }

        /// <summary>1.0.208: the pouch look on every slot under root (InventoryElement faces + Cell_ frame faces). Count changed.</summary>
        internal static int ApplySlotLook(Transform root)
        {
            if (root == null || !PanelsMode || !PouchLook(out var look)) return 0;
            // 1.0.229 ([generator], InventoryGui.Update 95 ms/s: ~450 faces re-painted per pass): a root is walked only when its hierarchy changed
            // (faces created / re-created) or a re-create was flagged (ForceSlotLook); otherwise the pass is a dictionary compare
            int hc = root.hierarchyCount;
            if (!s_forceLook && s_lookHierarchy.TryGetValue(root.GetInstanceID(), out int had) && had == hc) return 0;
            s_lookHierarchy[root.GetInstanceID()] = hc;
            int n = 0;
            // 1.0.214 ([lead], R-1015AB: player Misc / bags GREY while the companion's are right): the companion's cells draw ONE square,
            // the Cell_ face. A player cell drew the element's face too (the slot look at half alpha, over the Cell_ face), so it came out
            // lighter. Now every slot inside a Cell_ draws exactly the companion's stack: the Cell_ face carries the look (always on), the
            // element's own face is fully transparent (it still takes clicks), its dropFocus stays clear unless vanilla lights it in a drag.
            foreach (var el in root.GetComponentsInChildren<InventoryElement>(true))
            {
                var face = el.GetComponent<Image>();
                bool inCell = el.transform.parent != null && el.transform.parent.name.StartsWith(CellPrefix, System.StringComparison.Ordinal);
                if (inCell ? ClearFace(face) : ApplyLook(face, look)) n++;
            }
            int equip = 0;
            foreach (var t in root.GetComponentsInChildren<RectTransform>(true))
            {
                // 1.0.226 (Fire: the EQUIPMENT slots still grey): the pouch faces (Tools / Ammo / Food / Coin purse, and Jewelry on the
                // rail) are "PouchCell", not Cell_, so they never got the look; now every slot face, rails and pouches, goes the same way
                bool pouch = t.name == "PouchCell";
                if (!pouch && !t.name.StartsWith(CellPrefix, System.StringComparison.Ordinal)) continue;
                var face = t.GetComponent<Image>();
                if (face == null) continue;
                if (!pouch && !face.enabled) { face.enabled = true; n++; }
                if (ApplyLook(face, look)) n++;
                if (pouch || InRail(t)) equip++;
            }
            if (equip > 0 && Time.unscaledTime >= s_nextEquipLog && s_coreSlotLook != null)
            {
                s_nextEquipLog = Time.unscaledTime + 10f;
                EquipmentPanel.PanelLog?.LogDebug($"panels: {equip} equipment/pouch faces under '{root.name}' via FiresSkin.ApplySlotLook");
            }
            return n;
        }

        // an element face inside a Cell_: transparent, no border (the Cell_ face draws the square); KitLeaf so Core never repaints it
        static bool ClearFace(Image face)
        {
            if (face == null) return false;
            bool changed = false;
            var c = face.color; if (c.a != 0f) { c.a = 0f; face.color = c; changed = true; }
            var o = face.GetComponent<Outline>(); if (o != null && o.enabled) { o.enabled = false; changed = true; }
            var sel = face.GetComponent<Selectable>();
            if (sel != null && sel.colors.normalColor != Color.white) { var cb = sel.colors; cb.normalColor = Color.white; sel.colors = cb; changed = true; }
            if (!s_leafLooked) { s_leafLooked = true; s_leaf = System.Type.GetType("FiresCore.UI.Shell.Kit.KitLeaf, FiresUnifiedCore"); }
            if (s_leaf != null && face.GetComponent(s_leaf) == null) { face.gameObject.AddComponent(s_leaf); changed = true; }
            var kit = face.transform.Find("KitSlotOutline"); if (kit != null && kit.gameObject.activeSelf) { kit.gameObject.SetActive(false); changed = true; }
            var drop = face.transform.Find("dropFocus")?.GetComponent<Image>();
            if (drop != null && Mathf.Abs(drop.color.a - 0.047f) < 0.01f) { drop.color = new Color(1f, 1f, 1f, 0f); changed = true; }   // the idle white haze (main grid's is clear)
            return changed;
        }

        static float s_nextEquipLog;   // 1.0.226
        static readonly Dictionary<int, int> s_lookHierarchy = new Dictionary<int, int>();   // 1.0.229: root -> hierarchy count at its last look pass
        static bool s_forceLook;
        /// <summary>1.0.229: one forced look pass over a root (after an element re-create: same count, new objects).</summary>
        internal static int ForceSlotLook(Transform root) { s_forceLook = true; try { return ApplySlotLook(root); } finally { s_forceLook = false; } }
        static bool InRail(Transform t) { for (; t != null; t = t.parent) if (t.name == RailLeftName || t.name == RailRightName) return true; return false; }
        static bool InBag(Transform t) { for (; t != null; t = t.parent) if (t.name.StartsWith("BagSection_", System.StringComparison.Ordinal)) return true; return false; }

        // 1.0.201: the first main-grid element with no item (its icon off): the model for the EMPTY slot look; null = none yet
        internal static InventoryElement EmptyGridCell(InventoryGrid grid)
        {
            if (grid == null || grid.m_gridRoot == null) return null;
            foreach (Transform c in grid.m_gridRoot)
            {
                var el = c.GetComponent<InventoryElement>();
                if (el != null && el.m_icon != null && !el.m_icon.enabled) return el;
            }
            return null;
        }

        // every graphic under an element, in draw order (topmost last), with what it renders
        static string Children(Transform el)
        {
            var parts = new List<string>();
            foreach (var g in el.GetComponentsInChildren<Graphic>(true))
                parts.Add($"{g.name} {(g.isActiveAndEnabled ? "ON" : "off")} {g.color} cr {g.canvasRenderer.GetColor()} a {g.canvasRenderer.GetAlpha():0.00} {(g is Image i && i.sprite != null ? i.sprite.name : "")} {(g.material != null ? g.material.name : "")}");
            return string.Join(" ; ", parts.ToArray());
        }

        // 1.0.200: a Cell_ frame gets the grid cell's KitSlotOutline (Core's slot outline), once
        static bool CopyOutline(Transform cell, Transform modelCell)
        {
            var mo = modelCell != null ? modelCell.Find("KitSlotOutline") : null;
            if (mo == null || cell.Find("KitSlotOutline") != null || cell.GetComponentInChildren<InventoryElement>(true) != null) return false;
            var o = Object.Instantiate(mo.gameObject, cell, false);
            o.name = "KitSlotOutline";
            var rt = (RectTransform)o.transform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            o.transform.SetAsFirstSibling();
            return true;
        }

        static Transform FindDeep(
Transform parent, string name)
        {
            if (parent.name == name) return parent;
            foreach (Transform child in parent)
            {
                var hit = FindDeep(child, name);
                if (hit != null) return hit;
            }
            return null;
        }

        // The prefab carries a Canvas so it is editable as a real UI screen in Unity. None of that may reach
        // the game: vanilla's Canvas must stay the only one driving this screen.
        static void StripEditorOnly(GameObject go)
        {
            // Canvas LAST: CanvasScaler and GraphicRaycaster both RequireComponent it. Destroy is deferred
            // to end of frame so the wrong order has not thrown, but it would the moment anyone switched
            // this to DestroyImmediate. ([bc07ca] hit the same ordering on their own mount.)
            var scaler = go.GetComponent<CanvasScaler>();
            if (scaler != null) Object.Destroy(scaler);
            var raycaster = go.GetComponent<GraphicRaycaster>();
            if (raycaster != null) Object.Destroy(raycaster);
            var canvas = go.GetComponent<Canvas>();
            if (canvas != null) Object.Destroy(canvas);

            // The bundler already removed the vanilla-panel copies; this only catches a stale bundle.
            var stale = go.transform.Find("VanillaRef");
            if (stale != null) Object.Destroy(stale.gameObject);

            // The root fills vanilla's inventory root; the authored 1920x1080 is the editing frame only.
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.localScale = Vector3.one;
        }

        static bool EnsurePrefab()
        {
            if (prefab != null) return true;
            if (bundle == null)
            {
                bundle = VAInventory.GetAssetBundleFromResources(BundleName);
                if (bundle == null)
                {
                    loadFailed = true;
                    Debug.LogError($"[VAInventory] unified layout disabled: bundle '{BundleName}' missing");
                    return false;
                }
            }
            prefab = bundle.LoadAsset<GameObject>(PrefabName);
            if (prefab != null) return true;
            loadFailed = true;
            Debug.LogError($"[VAInventory] unified layout disabled: '{PrefabName}' not in '{BundleName}'");
            return false;
        }

        static void MountCraftingExtras(RectTransform ui, RectTransform crafting)
        {
            var extras = ui.Find("CraftingExtras") as RectTransform;
            if (extras == null || crafting == null || extras.parent == crafting) return;

            extras.SetParent(crafting, false);
            foreach (Transform authored in extras)
                authored.gameObject.SetActive(false);

            // The container spans the whole panel interior. Leaving it up with nothing showing puts an
            // empty full-size overlay across vanilla RecipeList and Decription - which are live and
            // populated underneath it. It comes up only while one of our tabs is active.
            extras.gameObject.SetActive(false);
            extras.anchorMin = Vector2.zero;
            extras.anchorMax = Vector2.one;
            extras.offsetMin = new Vector2(10f, 10f);
            // -101 is where vanilla starts RecipeList/Decription. At -70 an inline view rode up over the
            // tab row and, once open, covered the buttons that would have closed it.
            extras.offsetMax = new Vector2(-10f, -101f);
        }

        // Sprites, tints and fonts come from the LIVE vanilla UI rather than the bundle, which is why the
        // bundle is a few KB rather than dragging vanilla's atlases and a TMP font asset along with it.
        static void BindChrome(Transform ours, RectTransform player)
        {
            if (player == null) return;

            var panelSprite = player.parent.Find("Info/Bkg")?.GetComponent<Image>()
                              ?? player.Find("Bkg")?.GetComponent<Image>();
            var panelBkg = characterPanel != null ? PanelImage(ref s_bkgImg, "Bkg") : null;
            if (panelSprite != null && panelBkg != null)
            {
                panelBkg.sprite = panelSprite.sprite;
                panelBkg.type = panelSprite.type;
                panelBkg.color = panelSprite.color;
                panelBkg.material = panelSprite.material;
            }

            // A slot's dark face is the element Button tinting the CanvasRenderer, NOT the sprite - that
            // sprite is white. Copying only the sprite yields white squares.
            var element = InventoryGui.instance?.m_playerGrid?.m_elementPrefab;
            var elementImage = element?.GetComponent<Image>();
            var elementTint = element?.GetComponent<Selectable>();
            if (elementImage != null && characterPanel != null && !PanelsMode)   // 1.0.210 ([lead]: ONE slot-face writer under Panels = ApplySlotLook)
            {
                var backing = characterPanel.Find(BackingName);
                foreach (var image in characterPanel.GetComponentsInChildren<Image>(true))
                {
                    if (image == panelBkg || (backing != null && image.transform.IsChildOf(backing))) continue;
                    image.sprite = elementImage.sprite;
                    image.type = Image.Type.Sliced;
                    image.color = elementTint != null ? elementTint.colors.normalColor : elementImage.color;
                    // Fire 10-07 (1.0.69): a thin gold edge on every cell face (rails, rows, and the templates the
                    // Misc / bag / pouch cells are cloned from), so an empty slot reads against the panel.
                    if (!PanelsMode && image.name.StartsWith("Cell") && image.GetComponent<Outline>() == null)   // 1.0.184: under Panels Core's Role.Slot draws the outline
                    {
                        var edge = image.gameObject.AddComponent<Outline>();
                        edge.effectColor = new Color(GoldEdge.r, GoldEdge.g, GoldEdge.b, CellEdgeAlpha);
                        edge.effectDistance = new Vector2(1f, -1f);
                    }
                }
            }

            var font = player.root.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.font != null);
            if (font == null) return;
            foreach (var text in ours.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                text.font = font.font;
                text.fontSharedMaterial = font.fontSharedMaterial;
            }
        }
    }

    // The character panel's drag handle, on its backdrop. It only drags: no click or release here ever touches an
    // item, and with an item on the cursor it does nothing at all. A double-click puts the panel back.
    // 1.0.102: the bottom-right resize grip.
    internal sealed class CharacterPanelResize : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        bool dragging;

        public void OnBeginDrag(PointerEventData eventData)
        {
            dragging = eventData.button == PointerEventData.InputButton.Left;
            if (dragging) UnifiedScreen.BeginResize(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (dragging) UnifiedScreen.Resize(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (dragging) UnifiedScreen.EndResize();
            dragging = false;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left && eventData.clickCount == 2) UnifiedScreen.ResetScale();
        }
    }

    internal sealed class CharacterPanelDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        const int ResetClicks = 2;
        static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> DragItem =
            AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_dragItem");

        bool dragging;

        public void OnBeginDrag(PointerEventData eventData)
            => dragging = eventData.button == PointerEventData.InputButton.Left && !HoldingItem();

        public void OnDrag(PointerEventData eventData)
        {
            if (dragging) UnifiedScreen.MovePanel(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (dragging) UnifiedScreen.SavePanelOffset();
            dragging = false;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left && eventData.clickCount == ResetClicks && !HoldingItem())
                UnifiedScreen.ResetPanelOffset();
        }

        static bool HoldingItem() => InventoryGui.instance != null && DragItem(InventoryGui.instance) != null;
    }
}
