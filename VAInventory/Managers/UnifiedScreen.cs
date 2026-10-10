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

        internal static bool Enabled => ConfigManager.Instance?.configUiLayout?.Value == "Unified";

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
        }

        const float PanelSpacing = 4f;

        // Fire 10-07 (1.0.69): "too see-through, grid squares wash into the background". The backdrop's art is translucent
        // parchment, so a solid plate goes under it at [UI] PanelOpacity, inset inside the frame's border.
        const string UnderlayName = "OpaqueUnderlay";
        const float UnderlayInset = 6f;
        static readonly Color UnderlayColor = new(0.10f, 0.08f, 0.06f, 1f);
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
            if (fitPending) ScaleToCrafting();
            // The screen fit waits for the scale (it measures in scaled units), then runs on every open and after any rebuild.
            if (!fitPending && !screenFitPending && characterPanel.gameObject.activeInHierarchy && PanelScroll.NeedsRefit(characterPanel)) screenFitPending = true;
            if (!fitPending && screenFitPending && characterPanel.gameObject.activeInHierarchy) RunScreenFit();
            // 1.0.72: the player grid's inset plate, refitted when its cells move (PlayerGridPlate).
            if (characterPanel.gameObject.activeInHierarchy)
            {
                // 1.0.81 (Fire: "a DARK background with a LIGHTER INLAY that the grid lays onto"): the grid copies the panel's
                // three layers live: the dark underlay, the Bkg art over it, and the lighter Backing inlay under the cells.
                PlayerGridPlate.Tick(characterPanel.Find("Bkg")?.GetComponent<Image>(),
                    characterPanel.Find(BackingName)?.GetComponent<Image>(),
                    characterPanel.Find(UnderlayName)?.GetComponent<Image>(), GoldEdge, CellEdgeAlpha);
                ApplyGridCellFace();
                PanelLayout.TickLayout();   // 1.0.101: companions re-dock when the panel moves
                ApplySuppression();   // 1.0.105: VAInventoryApi.SuppressPlayerPanel (cheap: a flag compare)
                // 1.0.82 (Fire: the crafting panel shares the player panel's layout and colour scheme): same three layers, live.
                PanelSkin.Apply(InventoryGui.instance != null ? InventoryGui.instance.m_crafting : null,
                    characterPanel.Find("Bkg")?.GetComponent<Image>(),
                    characterPanel.Find(UnderlayName)?.GetComponent<Image>(),
                    characterPanel.Find(BackingName)?.GetComponent<Image>());
            }
        }

        // 1.0.88 (Fire: "the slightly darker grid squares ... on the player panel too"): every cell face on the character panel
        // (equipment, Misc, pouches, bag sections: the images BindChrome gave the element sprite) takes the face the main grid's
        // vanilla cells draw (PlayerGridPlate.GridCellFace). Re-applied when that face or the panel's hierarchy changes.
        static Color lastAppliedFace = new Color(-1f, 0f, 0f, 0f);
        static int lastHierarchyCount = -1;

        static void ApplyGridCellFace()
        {
            if (characterPanel == null || PlayerGridPlate.GridCellFace == null) return;
            var face = PlayerGridPlate.GridCellFace.Value;
            int hierarchy = characterPanel.hierarchyCount;
            if (face == lastAppliedFace && hierarchy == lastHierarchyCount) return;
            var elementSprite = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponent<Image>()?.sprite;
            if (elementSprite == null) return;
            var panelBkg = characterPanel.Find("Bkg")?.GetComponent<Image>();
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
        internal static float UserScale => Mathf.Clamp(ConfigManager.Instance?.configPlayerPanelScale?.Value ?? 1f, 0.6f, 1.2f);
        static float lastLoggedScale = float.NaN;

        static void LogUserScale(float user, float applied)
        {
            PanelLayout.Report(applied);   // 1.0.101: other mods follow the applied scale
            if (Mathf.Approximately(applied, lastLoggedScale)) return;
            lastLoggedScale = applied;
            EquipmentPanel.PanelLog?.LogDebug($"player panel scale: config {user:0.00}, applied {applied:0.00}");
        }

        internal static void RequestRescale()
        {
            if (characterPanel == null) return;
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
            var handle = characterPanel.Find("Bkg")?.GetComponent<Image>();
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

        static float resizeStartScale, resizeStartDistance;

        internal static void BeginResize(PointerEventData e)
        {
            resizeStartScale = characterPanel != null ? characterPanel.localScale.x : 1f;
            resizeStartDistance = Mathf.Max(1f, DistanceFromTopLeft(e));
        }

        internal static void Resize(PointerEventData e)
        {
            if (characterPanel == null) return;
            float s = Mathf.Clamp(resizeStartScale * DistanceFromTopLeft(e) / resizeStartDistance, 0.6f, 1.2f);
            characterPanel.localScale = new Vector3(s, s, 1f);
            PanelLayout.Report(s);
        }

        internal static void EndResize()
        {
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
            if (characterPanel != null) characterPanel.anchoredPosition = authoredPosition + PanelOffset;
        }

        internal static void MovePanel(PointerEventData eventData)
        {
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
        }

        static readonly string[] LeftRailOrder =
            [Slots.helmetSlotID, Slots.chestSlotID, Slots.legsSlotID, Slots.utilitySlotID, Slots.trinketSlotID, Slots.ringSlotID,
             Slots.shoulderSlotID];
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
        static void FillRail(Transform rail, List<Slots.Slot> slots)
        {
            if (rail.childCount > 0)
                for (int i = rail.childCount; i < slots.Count; i++)
                    Object.Instantiate(rail.GetChild(0).gameObject, rail).name = CellPrefix + i;
            for (int i = 0; i < rail.childCount; i++)
            {
                var cell = (RectTransform)rail.GetChild(i);
                bool used = i < slots.Count;
                cell.gameObject.SetActive(used);
                if (used) cells[CellKey(slots[i])] = cell;
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
            if (slots.Count > rail.childCount)
                Debug.LogWarning($"[VAInventory] {slots.Count - rail.childCount} slot(s) have no cell on {rail.name}: it has no cell to clone.");
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
            _ => []
        };

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
        static readonly Color GoldEdge = new(0.55f, 0.42f, 0.20f, 1f);
        const float CellEdgeAlpha = 0.7f;

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
                var cell = NewRect(holder, "PouchCell");
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

                // The popup: the group's real slot cells, cloned from the row template exactly as AssignRow does.
                var popup = NewRect(characterPanel, "PouchPopup_" + title);
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
                var pTitle = NewText(popup, "title", title + " pouch", 15f, Gold, TextAlignmentOptions.Center, font);
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
                PouchPopup.Register(group, popup, cell);
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

        static Transform FindDeep(Transform parent, string name)
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
            var panelBkg = characterPanel != null ? characterPanel.Find("Bkg")?.GetComponent<Image>() : null;
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
            if (elementImage != null && characterPanel != null)
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
                    if (image.name.StartsWith("Cell") && image.GetComponent<Outline>() == null)
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
