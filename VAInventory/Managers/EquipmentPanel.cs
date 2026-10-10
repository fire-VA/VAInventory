    using HarmonyLib;
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using TMPro;
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.UI;
    using VerdantsAscent.Managers;
    using static VerdantsAscent.Slots;

    namespace VerdantsAscent
    {
        public static class EquipmentPanel
        {
            // Vanilla spaces its grid by m_elementSpace (InventoryGrid:273, j * m_elementSpace). Hardcoding
            // 70 made our equipment, tool, food and ammo cells a different size from every other inventory
            // square. Read vanilla's own value so they match by construction.
            private static float tileSize =>
                InventoryGui.instance != null && InventoryGui.instance.m_playerGrid != null
                    ? InventoryGui.instance.m_playerGrid.m_elementSpace
                    : 70f;
            private const float inventoryPanelOffset = 100f;

            private static int equipmentSlotsCount = 0;
            internal static int quickSlotsCount = 0;

            public static RectTransform inventoryDarken;
            public static RectTransform inventoryBackground;
            public static Image inventoryBackgroundImage;
            public static RectTransform equipmentBackground;
            public static Image equipmentBackgroundImage;

            // Per-bag detached backpack panels. Keyed by bag prefab name
            // (e.g. "SmallBag_va", "HoboStick"). Each panel is parented
            // directly to the inventory window root (NOT to equipmentBackground)
            // so it can be dragged and positioned independently. The dict
            // is rebuilt each time the equipped-backpack set changes:
            // panels for newly-equipped bags are created, panels for
            // unequipped bags are destroyed.
            //
            // Old single-panel API (`backpackBackground` / `backpackBackgroundImage`)
            // is gone — callers should iterate `backpackPanels` instead.
            // If you need "the first one" for legacy reasons, take
            // `backpackPanels.Values.FirstOrDefault()`.
            public static Dictionary<string, RectTransform> backpackPanels = [];
            public static Dictionary<string, Image> backpackPanelImages = [];
            private static readonly Dictionary<string, GameObject> backpackPanelLabels = [];
            public static RectTransform selectedFrame;
            public static RectTransform inventorySelectedFrame;

            private static bool isDirty = true;
            private static int updateSidePanelsInFrames;
            private static int updateSidePanelsPreviousExtraRows = -10;

            private static Color normalColor = Color.clear;
            private static Color highlightedColor = Color.clear;
            private static Color normalColorUnfit = Color.clear;
            private static Color highlightedColorUnfit = Color.clear;

            private static Material iconMaterial;
            private static Vector3 originalScale = Vector3.zero;
            private static Vector2 originalTooltipPosition = Vector2.zero;

            internal static Sprite ammoSlot = null;
            internal static Sprite miscSlot = null;
            internal static Sprite quickSlot = null;
            internal static Sprite lightenedSlot = null;
            internal static Sprite background = null;

            private static readonly Color emptySlotHintColor = new(0.75f, 0.75f, 0.75f, 0.22f);

            // Reflection fields
            internal static readonly FieldInfo fi_dragItem =AccessTools.Field(typeof(InventoryGui), "m_dragItem");
            private static readonly FieldInfo fi_elements = AccessTools.Field(typeof(InventoryGrid), "m_elements");

            // Valheim 1.0 promoted InventoryGrid's PRIVATE NESTED Element class to a top-level PUBLIC
            // InventoryElement, and made it a MonoBehaviour. Three consequences, all of which bit us:
            //   * typeof(InventoryGrid).GetNestedType("Element", NonPublic) returns NULL on 1.0, so every
            //     AccessTools.Field built from it resolved to null - three "AccessTools.Field: type is null"
            //     warnings per boot and a silently dead equipment panel.
            //   * m_icon and m_amount are PUBLIC now, so no reflection is needed for them at all.
            //   * m_go is GONE. The element no longer holds a GameObject reference - it IS the component on
            //     that GameObject, so the replacement is simply .gameObject.
            // Kept as helpers rather than retyping ~23 call sites, so the change stays reviewable.
            private static Image ElemIcon(object e) => (e as InventoryElement)?.m_icon;
            private static TMP_Text ElemAmount(object e) => (e as InventoryElement)?.m_amount;
            private static GameObject ElemGo(object e) => (e as InventoryElement)?.gameObject;

            private static GameObject elementPrefab;
            private static readonly List<object> sidePanelElements = [];
            private static readonly List<Slots.Slot> sidePanelSlots = [];
            private static GameObject backpackLabelObject;
            private static TMP_FontAsset averiaSansFont;

            private static float InventoryPanelWidth => InventoryGui.instance ? InventoryGui.instance.m_player.rect.width : 0f;

            // This is the key to the slim vertical look - no extra offset
            private static Vector2 PanelOffset => Vector2.zero;

            private static Vector2 PanelPosition => new Vector2(InventoryPanelWidth + inventoryPanelOffset, 0f) + PanelOffset;

            private const int EquipmentPanelBaseRows = 4;
            private const int BackpackLabelRows = 1;
            private const float BackpackSectionGap = 12f;
            // Padding around the cell grid inside each per-bag detached
            // panel, so cells don't touch the panel's background edge.
            private const float BackpackPanelPadding = 8f;

            private static int EquipmentColumns
            {
                get
                {
                    int columns = 2;
                    if (ConfigManager.Instance.configShowFoodSlots.Value) columns++;
                    if (ConfigManager.Instance.configShowAmmoSlots.Value) columns++;
                    // FiresRPGClasses' bar column (Slots.InitializeSlots adds it last when that mod is loaded).
                    if (Slots.ClassBarSlotsActive) columns++;
                    // The Torch column, always last (1.0.50).
                    columns++;
                    return columns;
                }
            }

            private static int MiscRows => Mathf.Clamp(ConfigManager.Instance.configEquipmentPanelRows.Value, 0, 3);

            private static Slots.Slot[] BackpackStorageSlots => [.. Slots.AllSlots()
                .Where(s => s != null && s.IsActive && s.IsBackpackStorageSlot)
                .OrderBy(s => s.GridPosition.y).ThenBy(s => s.GridPosition.x)];

            private static int BackpackStorageRows => BackpackStorageSlots
                .Select(s => s.GridPosition.y)
                .Distinct()
                .Count();

            private static int BackpackStorageColumns => BackpackStorageSlots
                .Select(s => s.GridPosition.x)
                .Distinct()
                .Count();

            private static int EquipmentPanelRows => EquipmentPanelBaseRows + MiscRows;

            private static float EquipmentPanelWidth => EquipmentColumns * tileSize + 3f;
            private static float EquipmentPanelHeight => EquipmentPanelRows * tileSize + 3f;
            // With detached per-bag panels, "total panel height" only
            // describes the equipment panel itself — backpack panels are
            // independent floating windows whose layout doesn't push
            // anything else around.
            private static float TotalPanelHeight => EquipmentPanelHeight;

            private static float PanelWidth => EquipmentPanelWidth;
            private static float PanelHeight => EquipmentPanelHeight;
            public static void MarkDirty() => isDirty = true;
            public static void UpdateSidePanels() => updateSidePanelsInFrames = 3;

            internal static void UpdateSlotsCount()
            {
                equipmentSlotsCount = Slots.GetEquipmentSlotsCount();
                quickSlotsCount = Slots.GetQuickSlotsCount();
            }

            internal static void UpdatePanel()
            {
                UpdateSlotsCount();
                UpdateBackground();
                SetSlotsPositions();
                CreateSidePanelElements(); // This is what draws the actual grid slots
                MarkDirty();
                UpdateSidePanels();
            }

            // Equipment-panel slot count = rowDefinitions.Count × numColumns
            // in Slots.InitializeSlots. With the new row 4 (CoinPurse,
            // Backpack, Ring, TeleportBypass) this is 4 × 4 = 16 max.
            // We use a generous EQUIPMENT_PANEL_SLOTS to future-proof.
            private const int EQUIPMENT_PANEL_SLOTS = 32;
            // The index window of the slots the side panel shows. 1.0.72 (Fire, hand test 4: the Explorer Bag read "7 × 4"
            // but drew 20 cells): it was a fixed 64, the slot array's first size. The array grows (144 with a bag on), and
            // the equipment/misc/pouch/class slots take 44, so a 28-cell bag lost its last 8 cells to the window. It now
            // spans the whole array; the bag sections are part of the Unified panel.
            private static int SidePanelMaxSlots => Math.Max(64, Slots.slots?.Length ?? 0);

            private static bool IsItemInEquipmentPanel(ItemDrop.ItemData item)
            {
                if (item == null) return false;
                for (int i = 0; i < EQUIPMENT_PANEL_SLOTS && Slots.InventorySizePlayer + i < Slots.slots.Length; i++)
                {
                    var slot = Slots.slots[Slots.InventorySizePlayer + i];
                    if (slot != null && slot.Item == item)
                        return true;
                }
                return false;
            }

            private static bool IsItemInAnyEquipmentSlot(ItemDrop.ItemData item)
            {
                if (item == null) return false;
                for (int i = 0; i < EQUIPMENT_PANEL_SLOTS && Slots.InventorySizePlayer + i < Slots.slots.Length; i++)
                {
                    var slot = Slots.slots[Slots.InventorySizePlayer + i];
                    if (slot != null && slot.Item == item)
                        return true;
                }
                return false;
            }

            internal static void UpdateInventorySlots()
            {
                // Only run for player inventory grid
                if (InventoryGui.instance == null || InventoryGui.instance.m_playerGrid == null)
                    return;
                var grid = InventoryGui.instance.m_playerGrid;
                if (!ReferenceEquals(grid, InventoryGui.instance.m_playerGrid))
                    return;
                // A tool slot with a Unified cell is drawn there (the Tools row under the preview), so its grid
                // row must not show under the carry grid as well. Only cell-less tool slots stay in the grid.
                // Fire 10-06: under Unified the Tools row lives only in the character panel - never fall back to the
                // grid row (TryGetCell is false before the cells are built). Same inventory cells, so nothing moves.
                var gridToolSlots = Slots.toolHotbarSlots == null || UnifiedScreen.Enabled
                    ? new List<Slots.Slot>()
                    : Slots.toolHotbarSlots.Where(s => s != null && s.IsActive && !UnifiedScreen.TryGetCell(s, out _)).ToList();
                int visiblePlayerGridRows = Slots.InventoryHeightPlayer;
                if (gridToolSlots.Count > 0)
                {
                    int maxToolHotbarY = gridToolSlots
                        .Select(s => s.GridPosition.y)
                        .DefaultIfEmpty(Slots.InventoryHeightPlayer - 1)
                        .Max();
                    visiblePlayerGridRows = Math.Max(visiblePlayerGridRows, maxToolHotbarY + 1);
                }
                grid.m_gridRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, visiblePlayerGridRows * grid.m_elementSpace);

                if (originalScale == Vector3.zero)
                {
                    var elements = (IList)fi_elements.GetValue(grid);
                    if (elements.Count >0)
                    {
                        var icon = ElemIcon(elements[0]);
                        if (icon?.material)
                            originalScale = icon.transform.localScale;
                    }
                }

                int inventorySizePlayer = Slots.InventorySizePlayer;
                var elementsList = (IList)fi_elements.GetValue(grid);
                var elementsCopy = elementsList.Cast<object>().ToList(); // Safe copy
                var sidePanelCopy = sidePanelElements.ToList(); // Safe copy
                var sideSlotsCopy = sidePanelSlots.ToList(); // Safe copy

                // Only apply lightened slot visuals to vanilla grid
                for (int i =0; i < Math.Min(inventorySizePlayer, elementsCopy.Count); i++)
                    SetInventorySlotBackgroundImage(elementsCopy[i], LightenedSlots.IsRowAffected(i / Slots.InventoryWidth));

                // Keep the tool-hotbar row visible in the player grid so
                // users can fill it by dragging tools into those cells.
                // Other extra-slot coordinates are rendered by the side
                // equipment/backpack panels and should remain off-screen.
                for (int i = inventorySizePlayer; i < elementsCopy.Count; i++)
                {
                    var go = ElemGo(elementsCopy[i]);
                    if (go)
                    {
                        go.SetActive(true);
                        var rect = go.GetComponent<RectTransform>();
                        var pos = new Vector2i(i % Slots.InventoryWidth, i / Slots.InventoryWidth);
                        var slot = Slots.GetSlotInGrid(pos);
                        if (slot != null && gridToolSlots.Contains(slot))
                        {
                            rect.anchoredPosition = new Vector2(pos.x * tileSize, -pos.y * tileSize);
                            SetToolHotbarInventorySlotElement(elementsCopy[i], slot);
                        }
                        else
                        {
                            rect.anchoredPosition = new Vector2(-10000, -10000);
                        }
                    }
                }

                if (!isDirty || Slots.PlayerInventory == null) return;

                ItemDrop.ItemData dragItem = (ItemDrop.ItemData)fi_dragItem.GetValue(InventoryGui.instance);
                bool dragIsEquippedInExtraSlot = dragItem != null && Slots.CurrentPlayer.IsItemEquiped(dragItem) && Slots.IsItemInEquipmentSlot(dragItem);

                // MAIN INVENTORY: Hide icons/overlays for items that are in custom slots
                for (int i =0; i < Math.Min(inventorySizePlayer, elementsCopy.Count); i++)
                {
                    var elem = elementsCopy[i];
                    var go = ElemGo(elem);
                    if (go == null) continue;

                    var icon = ElemIcon(elem);
                    if (icon == null) continue;

                    var item = Slots.PlayerInventory.GetItemAt(i % Slots.InventoryWidth, i / Slots.InventoryWidth);
                    bool isInCustomSlot = item != null && Slots.IsGridPositionASlot(item.m_gridPos);

                    if (isInCustomSlot)
                    {
                        // Hide icon and overlays
                        icon.sprite = null;
                        icon.color = Color.clear;
                        icon.enabled = false;
                        // Move the vanilla grid element offscreen to prevent vanilla code from accessing it
                        var rect = go.GetComponent<RectTransform>();
                        rect.anchoredPosition = new Vector2(-10000, -10000);
                    }
                    else
                    {
                        // Restore position if not in custom slot
                        var rect = go.GetComponent<RectTransform>();
                        rect.anchoredPosition = new Vector2((i % Slots.InventoryWidth) * tileSize, -(i / Slots.InventoryWidth) * tileSize);
                        SetSlotColor(go.GetComponent<Button>(), dragIsEquippedInExtraSlot);
                    }
                }

                // Copy overlays/icons from hidden vanilla extra elements to side panel
                for (int i =0; i < sidePanelCopy.Count && i < sideSlotsCopy.Count; i++)
                {
                    var sideElem = sidePanelCopy[i];
                    var slot = sideSlotsCopy[i];

                    if (slot == null || !slot.IsActive) continue;

                    var sideIcon = ElemIcon(sideElem);
                    int mainIndex = slot.Index;
                    GameObject sideGo = ElemGo(sideElem);
                    string[] overlayNames = ["durability", "amount", "foodicon", "noteleport", "quality", "queued", "selected", "equiped"];

                    if (mainIndex >= 0 && mainIndex < elementsCopy.Count)
                    {
                        var mainElem = elementsCopy[mainIndex];
                        var mainIcon = ElemIcon(mainElem);
                        if (mainIcon && sideIcon)
                        {
                            sideIcon.sprite = mainIcon.sprite;
                            sideIcon.color = mainIcon.color;
                            sideIcon.material = mainIcon.material;
                            sideIcon.transform.localScale = mainIcon.transform.localScale;
                            sideIcon.enabled = mainIcon.enabled;
                        }

                        var mainAmount = ElemAmount(mainElem);
                        var sideAmount = ElemAmount(sideElem);
                        if (mainAmount && sideAmount)
                        {
                            sideAmount.text = mainAmount.text;
                            sideAmount.enabled = mainAmount.enabled;
                        }

                        var mainGo = ElemGo(mainElem);
                        foreach (var name in overlayNames)
                        {
                            var mainOverlay = mainGo.transform.Find(name);
                            var sideOverlay = sideGo.transform.Find(name);
                            if (mainOverlay && sideOverlay)
                            {
                                // Copy what vanilla actually draws (see OverlayShown)
                                bool active = OverlayShown(mainOverlay);
                                sideOverlay.gameObject.SetActive(active);

                                // Only copy content if overlay is active
                                if (active)
                                {
                                    var mainText = mainOverlay.GetComponent<TMP_Text>();
                                    var sideText = sideOverlay.GetComponent<TMP_Text>();
                                    if (mainText && sideText)
                                        sideText.text = mainText.text;
                                    var mainBar = mainOverlay.GetComponent<GuiBar>();
                                    var sideBar = sideOverlay.GetComponent<GuiBar>();
                                    if (mainBar != null && sideBar != null)
                                        sideBar.SetValue(mainBar.GetSmoothValue());
                                }

                                // Debug logging for overlay state
                                if (VerboseLogging)
                                {
                                    string overlayInfo = $"[OverlaySync] SlotIdx={mainIndex} Overlay={name} VanillaActive={active} CustomActive={sideOverlay.gameObject.activeSelf}";
                                    var mainText = mainOverlay.GetComponent<TMP_Text>();
                                    var sideText = sideOverlay.GetComponent<TMP_Text>();
                                    if (mainText && sideText)
                                        overlayInfo += $" VanillaText='{mainText.text}' CustomText='{sideText.text}'";
                                    var mainBar = mainOverlay.GetComponent<GuiBar>();
                                    var sideBar = sideOverlay.GetComponent<GuiBar>();
                                    if (mainBar != null && sideBar != null)
                                        overlayInfo += $" VanillaBar={mainBar.GetSmoothValue()} CustomBar={sideBar.GetSmoothValue()}";
                                    Log(overlayInfo);
                                }
                            }
                        }
                    }

                    var item = slot.Item;
                    if (item != null)
                    {
                        var player = Player.m_localPlayer;
                        foreach (var name in overlayNames)
                        {
                            var sideOverlay = sideGo.transform.Find(name);
                            if (!sideOverlay) continue;
                            bool shouldEnable = false;
                            switch (name)
                            {
                                case "amount":
                                    shouldEnable = item.m_stack >1;
                                    break;
                                case "foodicon":
                                    shouldEnable = item.m_shared != null && item.m_shared.m_food >0;
                                    break;
                                case "durability":
                                    shouldEnable = item.m_shared != null && item.m_shared.m_useDurability;
                                    break;
                                case "noteleport":
                                    shouldEnable = item.m_shared != null && !item.m_shared.m_teleportable;
                                    break;
                                case "quality":
                                    shouldEnable = item.m_quality >1;
                                    // Always set the quality overlay text to the item's actual quality
                                    var qualityText = sideOverlay.GetComponent<TMP_Text>();
                                    if (qualityText) qualityText.text = item.m_quality.ToString();
                                    break;
                                case "equiped":
                                    shouldEnable = player != null && player.IsItemEquiped(item);
                                    break;
                                case "selected":
                                    shouldEnable = false;
                                    break;
                                case "queued":
                                    shouldEnable = false;
                                    break;
                                default:
                                    shouldEnable = false;
                                    break;
                            }
                            sideOverlay.gameObject.SetActive(shouldEnable);
                        }
                    }

                    // Show slot suggestion/hint for empty slots, and hide overlays
                    if (slot.IsFree && sideIcon != null)
                    {
                        sideIcon.enabled = true;
                        sideIcon.material = null;
                        // For first12 slots, show correct icon and label
                        if (i <12)
                        {
                            if (slot.IsAmmoSlot) sideIcon.sprite = ammoSlot;
                            else if (slot.IsFoodSlot) sideIcon.sprite = lightenedSlot;
                            else if (slot.IsMiscSlot) sideIcon.sprite = quickSlot;
                            else if (slot.IsQuickSlot) sideIcon.sprite = quickSlot;
                            else sideIcon.sprite = background;
                            sideIcon.color = emptySlotHintColor;
                            // Show label (slot name)
                            Transform binding = sideGo.transform.Find("binding");
                            SetSlotLabel(binding, slot);
                        }
                        else
                        {
                            // For other slots, fallback to previous logic
                            if (slot.IsAmmoSlot) sideIcon.sprite = ammoSlot;
                            else if (slot.IsFoodSlot) sideIcon.sprite = lightenedSlot;
                            else if (slot.IsMiscSlot) sideIcon.sprite = quickSlot;
                            else if (slot.IsQuickSlot) sideIcon.sprite = quickSlot;
                            else sideIcon.sprite = background;
                            sideIcon.color = emptySlotHintColor;
                        }

                        // Hide all overlays for empty slots
                        foreach (var name in overlayNames)
                        {
                            var sideOverlay = sideGo.transform.Find(name);
                            if (sideOverlay) sideOverlay.gameObject.SetActive(false);
                        }
                    }

                    // Always update slot element for button color, label, etc.
                    SetSlotElement(sideElem, slot, dragIsEquippedInExtraSlot, i);
                }

                // Adjust tooltip position
                if (originalTooltipPosition == Vector2.zero)
                    originalTooltipPosition = grid.m_tooltipAnchor.anchoredPosition;
                // The legacy panel hangs under the grid and pushes the tooltip below it; Unified has nothing there.
                grid.m_tooltipAnchor.anchoredPosition = UnifiedScreen.Enabled
                    ? originalTooltipPosition
                    : originalTooltipPosition - new Vector2(0f, TotalPanelHeight +21f);

                isDirty = false;
            }



            public static void UpdateBackground()
            {
                if (!equipmentBackground) return;

                float dynamicPanelWidth = EquipmentPanelWidth;
                float dynamicPanelHeight = EquipmentPanelHeight;

                equipmentBackground.sizeDelta = new Vector2(dynamicPanelWidth, dynamicPanelHeight);
                equipmentBackground.anchoredPosition = PanelPosition + new Vector2(dynamicPanelWidth / 2f, -dynamicPanelHeight / 2f);

                // Load and set custom background
                var customBackground = VerdantsAscent.Managers.UIBackgroundManager.GetBackground("EquipmentPanel");
                if (equipmentBackgroundImage && customBackground)
                {
                    equipmentBackgroundImage.sprite = customBackground;
                    equipmentBackgroundImage.type = UnityEngine.UI.Image.Type.Sliced;
                }
                else if (equipmentBackgroundImage && background)
                {
                    equipmentBackgroundImage.sprite = background;
                }

                if (selectedFrame)
                {
                    selectedFrame.sizeDelta = equipmentBackground.sizeDelta + Vector2.one * 26f;
                    selectedFrame.anchoredPosition = equipmentBackground.anchoredPosition;
                }
            }

            // Add new grid for equipment panel
            public static InventoryGrid equipmentGrid;

            // Small clickable "cycle currency" badge in the top-left of the coin pouch slot.
            // Plain Unity UI (VAInventory doesn't reference FUC's helpers). The icon is a
            // procedurally-drawn gold swap-arrows sprite so it always renders — the game font
            // has no recycle glyph, which left just an empty box. Click swaps the displayed currency.
            private static Sprite _cycleSprite;

            private static Sprite GetCycleSprite()
            {
                if (_cycleSprite != null) return _cycleSprite;
                const int S = 64;
                var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var px = new Color[S * S];
                var clear = new Color(0f, 0f, 0f, 0f);
                for (int i = 0; i < px.Length; i++) px[i] = clear;
                var gold = new Color(0.94f, 0.78f, 0.36f, 1f);

                void Set(int x, int y) { if (x >= 0 && x < S && y >= 0 && y < S) px[y * S + x] = gold; }
                void Bar(int x0, int x1, int y0, int y1) { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y); }
                // Arrowhead tapering from xBase to xTip (dir = +1 right, -1 left), centred at yc, half-height h.
                void Head(int xBase, int xTip, int yc, int h, int dir)
                {
                    int steps = Mathf.Abs(xTip - xBase);
                    for (int s = 0; s <= steps; s++)
                    {
                        int x = xBase + dir * s;
                        int hh = Mathf.RoundToInt(h * (1f - (float)s / Mathf.Max(1, steps)));
                        for (int y = yc - hh; y <= yc + hh; y++) Set(x, y);
                    }
                }
                // Top arrow → right (y is up: higher = top).
                Bar(12, 43, 37, 44); Head(44, 58, 40, 11, +1);
                // Bottom arrow ← left.
                Bar(20, 51, 20, 27); Head(19, 5, 23, 11, -1);

                tex.SetPixels(px);
                tex.Apply();
                _cycleSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
                return _cycleSprite;
            }

            private static void AddCurrencyCycleBadge(GameObject slotGo)
            {
                if (slotGo == null || slotGo.transform.Find("CurrencyCycle") != null) return;

                var badge = new GameObject("CurrencyCycle", typeof(RectTransform));
                badge.transform.SetParent(slotGo.transform, false);
                var rt = badge.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(1f, -1f);
                rt.sizeDelta = new Vector2(21f, 21f);

                var bg = badge.AddComponent<Image>();
                bg.color = new Color(0.09f, 0.08f, 0.06f, 0.9f);
                bg.raycastTarget = true;

                var btn = badge.AddComponent<Button>();
                btn.targetGraphic = bg;
                btn.onClick.AddListener(() =>
                {
                    VerdantsAscent.Managers.CoinPurseStackPatch.CycleDisplayed();
                    MarkDirty();
                    UpdatePanel();
                });

                var iconGo = new GameObject("i", typeof(RectTransform));
                iconGo.transform.SetParent(badge.transform, false);
                var irt = iconGo.GetComponent<RectTransform>();
                irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
                irt.offsetMin = new Vector2(2f, 2f); irt.offsetMax = new Vector2(-2f, -2f);
                var iconImg = iconGo.AddComponent<Image>();
                iconImg.sprite = GetCycleSprite();
                iconImg.raycastTarget = false;
            }

            // The element prefab ships with every overlay showing. A square built for the panel starts as vanilla shows an
            // unused one, so opening the inventory never flashes them for the frame before UpdateInventorySlots sets them
            // (Fire, 2026-09-28).
            private static readonly string[] SquareOverlays = ["durability", "foodicon", "noteleport", "quality", "queued", "selected", "equiped"];

            internal static void HideOverlays(GameObject go)
            {
                foreach (var name in SquareOverlays)
                {
                    var overlay = go.transform.Find(name);
                    if (overlay) overlay.gameObject.SetActive(false);
                }
            }

            // Whether a vanilla element's overlay is actually drawn. Valheim 1.0's InventoryGrid.UpdateGui hides most of
            // them by disabling the Image/TMP (m_food/m_equiped/m_queued/m_noteleport/m_quality/m_amount .enabled = false)
            // and leaves the GameObject active; only durability and selected use SetActive. Copying activeSelf alone
            // read every hidden overlay as shown, which turned the new squares' overlays back on for the frame after
            // the inventory opened ([ghost], ui_test no_flash, 2026-09-28).
            private static bool OverlayShown(Transform overlay)
            {
                if (!overlay.gameObject.activeSelf) return false;
                var graphic = overlay.GetComponent<Graphic>();
                return graphic == null || graphic.enabled;
            }

            // ui_test's flash check: of the empty squares on screen, how many show any overlay right now. An empty square
            // shows none once synced, so any at the first frame after the inventory opens is the flash.
            internal static int EmptySquaresShowingOverlays(out int emptySquares)
            {
                emptySquares = 0;
                int showing = 0;
                for (int i = 0; i < sidePanelElements.Count && i < sidePanelSlots.Count; i++)
                {
                    var slot = sidePanelSlots[i];
                    var go = ElemGo(sidePanelElements[i]);
                    if (slot == null || !slot.IsActive || slot.Item != null || !go || !go.activeInHierarchy) continue;
                    emptySquares++;
                    foreach (var name in SquareOverlays)
                    {
                        var overlay = go.transform.Find(name);
                        if (!overlay || !overlay.gameObject.activeSelf) continue;
                        showing++;
                        break;
                    }
                }
                return showing;
            }

            internal static readonly BepInEx.Logging.ManualLogSource PanelLog =BepInEx.Logging.Logger.CreateLogSource("VAInventory.Panel");
            static bool toolCellsLogged;

            private static void CreateSidePanelElements()
            {
                if (InventoryGui.instance == null || InventoryGui.instance.m_playerGrid == null)
                {
                    PanelLog.LogDebug("[VAInventory] panel refresh skipped: no player grid");
                    return;
                }
                // Unified never builds the legacy backdrop: its slots go into the character panel's cells, so
                // bailing here on a missing backdrop left every rail and hotbar cell empty.
                if (!equipmentBackground && !UnifiedScreen.Enabled)
                {
                    PanelLog.LogDebug("[VAInventory] panel refresh skipped: no equipment backdrop (Legacy)");
                    return;
                }
                if (elementPrefab == null)
                    elementPrefab = InventoryGui.instance?.m_playerGrid?.m_elementPrefab;
                if (elementPrefab == null)
                {
                    PanelLog.LogDebug("[VAInventory] panel refresh skipped: no element prefab");
                    return;
                }
                if (UnifiedScreen.Enabled && !toolCellsLogged && Slots.toolHotbarSlots != null && Slots.PlayerInventory != null)
                {
                    toolCellsLogged = true;
                    int held = Slots.toolHotbarSlots.Count(s => s != null && s.Item != null);
                    PanelLog.LogDebug($"[VAInventory] Unified: grid tool row hidden; {held} item(s) in its {Slots.toolHotbarSlots.Count} cells, shown in the panel's Tools row (same cells, nothing moved)");
                }

                foreach (var elem in sidePanelElements)
                {
                    var oldGo = ElemGo(elem);
                    if (oldGo) UnityEngine.Object.Destroy(oldGo);
                }
                sidePanelElements.Clear();
                sidePanelSlots.Clear();
                if (backpackLabelObject)
                {
                    UnityEngine.Object.Destroy(backpackLabelObject);
                    backpackLabelObject = null;
                }
                // Per-bag detached panels: rebuild from the current
                // equipped-bag set. EnsureBackpackPanelsExist creates panels
                // for newly-equipped bags, destroys panels for unequipped
                // ones, and updates dimensions for existing ones.
                EnsureBackpackPanelsExist();

                // Tool hotbar slots carry negative indices (they are kept out of the main array on purpose),
                // so the index window below cannot reach them. In Unified they have a cell in the character
                // panel, so append them; in Legacy there is no cell and this adds nothing, leaving
                // ToolHotBar's own HUD bar as the only view. One slot, two views - never two slots.
                var extraSlots = Slots.AllSlots()
                    .Where(s => s != null && s.IsActive && !s.IsCurrencyVaultSlot && s.Index >= Slots.InventorySizePlayer && s.Index < Slots.InventorySizePlayer + SidePanelMaxSlots)
                    .OrderBy(s => s.Index)
                    .Concat(Slots.toolHotbarSlots.Where(s => s != null && s.IsActive && UnifiedScreen.TryGetCell(s, out _)))
                    .ToArray();

                int panelInCells = 0, panelUnplaced = 0;
                var unplacedIds = new List<string>();
                for (int i = 0; i < extraSlots.Length; i++)
                {
                    var slot = extraSlots[i];
                    Transform slotParent;
                    RectTransform unifiedCell = null;
                    // Unified first: tool/ammo/food cells sit in the pouch popups, bag storage in the panel's bag section.
                    if (UnifiedScreen.TryGetCell(slot, out unifiedCell))
                    {
                        slotParent = unifiedCell;
                    }
                    else if (slot.IsBackpackStorageSlot)
                    {
                        // Route to that bag's detached panel; fall back to
                        // equipmentBackground only if something went wrong.
                        var slotBag = Slots.GetBackpackPrefabFromSlot(slot);
                        if (slotBag != null && backpackPanels.TryGetValue(slotBag, out var bagPanel) && bagPanel != null)
                            slotParent = bagPanel;
                        else
                            slotParent = equipmentBackground;
                    }
                    else
                    {
                        slotParent = equipmentBackground;
                    }
                    // A Unified slot with no cell (more equipment than the rails hold) has nowhere to be drawn. It is
                    // never placed by its legacy Slot.Position: relative to a Unified parent that lands outside the
                    // rails/rows (Fire 10-07 backpack_shot_q2). Skipped and named in the per-open dump instead.
                    if (UnifiedScreen.Enabled && unifiedCell == null && !slot.IsBackpackStorageSlot) { panelUnplaced++; unplacedIds.Add(slot.ID + "@" + slot.Index); continue; }
                    if (slotParent == null) { panelUnplaced++; continue; }
                    if (unifiedCell != null) panelInCells++;
                    else if (UnifiedScreen.Enabled && !slot.IsBackpackStorageSlot) panelUnplaced++;
                    GameObject go = UnityEngine.Object.Instantiate(elementPrefab, slotParent);
                    HideOverlays(go);
                    var rect = go.GetComponent<RectTransform>();
                    // In Unified the prefab cell owns the geometry, so the element just fills it; Legacy
                    // keeps positioning by Slot.Position against the equipment background.
                    if (unifiedCell != null)
                        UnifiedScreen.FillCell(rect);
                    else
                        rect.anchoredPosition = slot.Position;

                    var iconImg = go.transform.Find("icon")?.GetComponent<Image>();
                    if (iconImg)
                    {
                        if (iconMaterial == null) iconMaterial = iconImg.material;
                        iconImg.sprite = null;
                        iconImg.color = Color.clear;
                        iconImg.enabled = false;
                    }

                    if (go.GetComponent<UIInputHandler>() == null)
                        go.AddComponent<UIInputHandler>();
                    if (go.GetComponent<ExtraSlotDragHandler>() == null)
                        go.AddComponent<ExtraSlotDragHandler>().Init(slot);
                    if (go.GetComponent<ExtraSlotTooltipHandler>() == null)
                        go.AddComponent<ExtraSlotTooltipHandler>().Init(slot);
                    if (go.GetComponent<ExtraSlotRightClickHandler>() == null)
                        go.AddComponent<ExtraSlotRightClickHandler>().Init(slot);

                    // 1.0: InventoryElement is a MonoBehaviour living ON the element prefab, so it can no
                    // longer be Activator.CreateInstance'd - the Instantiate above already produced one,
                    // with m_icon/m_amount wired from the prefab. This is exactly what vanilla's own
                    // InventoryGrid.UpdateGui does with the same prefab, so hand-finding "icon"/"amount"
                    // children is now redundant as well.
                    var newElem = go.GetComponent<InventoryElement>();
                    if (newElem == null)
                    {
                        Debug.LogWarning("[VAInventory] element prefab carries no InventoryElement component - " +
                                         "skipping side-panel slot " + slot.Index + ".");
                        UnityEngine.Object.Destroy(go);
                        continue;
                    }

                    sidePanelElements.Add(newElem);
                    sidePanelSlots.Add(slot);

                    SetSlotElement(newElem, slot, false, i);

                    // Coin pouch: a small cycle badge (top-left) swaps the displayed currency.
                    if (slot.IsCoinPurseSlot)
                        AddCurrencyCycleBadge(go);

                    var button = go.GetComponent<Button>();
                    if (button != null)
                    {
                        if (!go.GetComponent<CellPointerUp>()) go.AddComponent<CellPointerUp>();
                        button.onClick.RemoveAllListeners();
                        button.onClick.AddListener(() =>
                        {
                            var slotRect = go.GetComponent<RectTransform>();
                            // Gate on the pointer event's position (equals Input.mousePosition for a real mouse; lets
                            // synthetic pointer events through). Submit has no pointer-up this frame: keeps the mouse.
                            if (!RectTransformUtility.RectangleContainsScreenPoint(slotRect, CellPointerUp.PositionOr(go, Input.mousePosition), null))
                                return;

                            if (InventoryGui.instance == null || InventoryGui.instance.m_playerGrid == null)
                                return;

                            var grid = InventoryGui.instance.m_playerGrid;
                            var pos = slot.GridPosition;
                            var itemAt = Slots.PlayerInventory.GetItemAt(pos.x, pos.y);
                            var dragItem = (ItemDrop.ItemData)fi_dragItem.GetValue(InventoryGui.instance);

                            if (!slot.IsActive)
                                return;

                            if (dragItem != null && slot.IsFree)
                            {
                                var dragInventory = (Inventory)AccessTools.Field(typeof(InventoryGui), "m_dragInventory").GetValue(InventoryGui.instance);
                                if (dragInventory != Slots.PlayerInventory || !slot.ItemFits(dragItem))
                                {
                                    ClearDragState();
                                    return;
                                }

                                var fromPos = dragItem.m_gridPos;
                                PanelLog.LogDebug($"[VAInventory] cell drop: {dragItem.m_shared.m_name} {fromPos} -> {slot.ID} {pos}");
                                dragItem.m_gridPos = pos;
                                Slots.ClearCachedItems();
                                // Fire 10-06: an equipment slot means worn, so the drop equips. If vanilla refuses
                                // (swimming, etc.) the item goes back where it was, never left unequipped in the slot.
                                if (WornOnDrop(slot, dragItem) && Player.m_localPlayer != null && !Player.m_localPlayer.IsItemEquiped(dragItem))
                                {
                                    bool equipped;
                                    InventoryInteraction.Humanoid_EquipItem_AutoMoveToSlot.KeepDroppedSlot = true;
                                    try { equipped = Player.m_localPlayer.EquipItem(dragItem); }
                                    finally { InventoryInteraction.Humanoid_EquipItem_AutoMoveToSlot.KeepDroppedSlot = false; }
                                    if (!equipped || !Player.m_localPlayer.IsItemEquiped(dragItem))
                                        dragItem.m_gridPos = fromPos;
                                    PanelLog.LogDebug($"[VAInventory] drop equip: {dragItem.m_shared.m_name} -> {slot.ID} {(dragItem.m_gridPos == pos ? "equipped" : "refused, returned")}");
                                }
                                Slots.PlayerInventory.m_onChanged?.Invoke();
                                // Hint only: FiresNPCs also pulls Slots.GetPetSlotItem itself.
                                if (slot.IsPetSlot && dragItem.m_gridPos == pos)
                                    PetBridge.NotifyChanged(Player.m_localPlayer, dragItem);
                                ClearDragState();
                                MarkDirty();
                                UpdatePanel();
                                return;
                            }

                            InventoryGrid.Modifier modifier = InventoryGrid.Modifier.Select;
                            if (ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift))
                                modifier = InventoryGrid.Modifier.Split;
                            else if (ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl))
                                modifier = InventoryGrid.Modifier.Move;

                            // Fire 10-07 (1.0.69): in Unified a plain left click on the coin purse opens its popup, like the
                            // pouches beside it. Shift / Ctrl keep vanilla's split / move; a held item still drops in above.
                            if (slot.IsCoinPurseSlot && UnifiedScreen.Enabled && dragItem == null && modifier == InventoryGrid.Modifier.Select)
                            {
                                VerdantsAscent.Managers.CurrencyPouchPopup.Toggle(slotRect);
                                return;
                            }

                            var mi_onSelectedItem = AccessTools.Method(typeof(InventoryGui), "OnSelectedItem");
                            mi_onSelectedItem?.Invoke(InventoryGui.instance, [grid, itemAt, pos, modifier]);
                            // Hint only (take-out / swap): report what the Pet slot holds now, null when emptied.
                            if (slot.IsPetSlot)
                                PetBridge.NotifyChanged(Player.m_localPlayer, Slots.PlayerInventory.GetItemAt(pos.x, pos.y));
                        });
                    }
                }
                // Fire 10-06 (blank panel after a drag): how many slots found their Unified cell this rebuild.
                if (UnifiedScreen.Enabled)
                    PanelLog.LogDebug($"[VAInventory] panel refresh: {extraSlots.Length} slot(s), {panelInCells} placed in cells, {panelUnplaced} unplaced");
                if (UnifiedScreen.Enabled && UnifiedScreen.ConsumeCellDump())
                    DumpCells(unplacedIds);
            }

            // Once per inventory open (Fire 10-07): which slot - and item - every mapped cell really holds. A filled cell
            // shows no label, so a filled extra slot (CoinPurse, TeleportBypass, ...) can read as "misplaced" on screen.
            static void DumpCells(List<string> unplacedIds)
            {
                for (int i = 0; i < sidePanelSlots.Count; i++)
                {
                    var slot = sidePanelSlots[i];
                    if (slot == null || !UnifiedScreen.TryGetCell(slot, out var cell)) continue;
                    var item = slot.Item;
                    string itemName = item?.m_shared != null ? Localization.instance.Localize(item.m_shared.m_name) + (item.m_stack > 1 ? " x" + item.m_stack : "") : "empty";
                    PanelLog.LogDebug($"[VAInventory] cell {slot.Name} -> item {itemName} (slot {slot.ID}@{slot.Index}) [{cell.parent?.name}/{cell.name}]");
                }
                if (unplacedIds.Count > 0)
                    PanelLog.LogDebug($"[VAInventory] unplaced: {string.Join(", ", unplacedIds)}");
            }

            /// <summary>
            /// Create / update / destroy detached backpack panels so there's
            /// exactly one panel per currently-equipped registered bag.
            /// Each panel:
            ///  - Is parented to the inventory window root (NOT the equipment
            ///    panel) so its position is fully independent.
            ///  - Has a UniversalDragHandler that persists its anchored
            ///    position to a per-prefab config entry on drag-end.
            ///  - Picks up its initial anchored position from config on
            ///    first creation, or a default cascade layout if none saved.
            ///  - Sized to its bag's rows × cols + a uniform padding so
            ///    cells aren't flush against the background edges.
            /// </summary>
            private static void EnsureBackpackPanelsExist()
            {
                var allBackpackSlots = BackpackStorageSlots;
                if (InventoryGui.instance == null || InventoryGui.instance.m_player == null) return;
                // Fire 10-07: Unified draws the worn bag inside the character panel (UnifiedScreen.BuildBagSections), so no
                // floating window there. Legacy keeps its detached panels as before.
                if (UnifiedScreen.Enabled) allBackpackSlots = [];

                // Group active backpack-storage slots by bag prefab.
                var byPrefab = new Dictionary<string, List<Slots.Slot>>();
                foreach (var s in allBackpackSlots)
                {
                    string prefab = Slots.GetBackpackPrefabFromSlot(s) ?? "Unknown";
                    if (!byPrefab.TryGetValue(prefab, out var list))
                    {
                        list = [];
                        byPrefab[prefab] = list;
                    }
                    list.Add(s);
                }

                // Destroy panels for prefabs that aren't equipped anymore.
                var stalePrefabs = new List<string>();
                foreach (var key in backpackPanels.Keys)
                    if (!byPrefab.ContainsKey(key)) stalePrefabs.Add(key);
                foreach (var key in stalePrefabs)
                {
                    if (backpackPanels.TryGetValue(key, out var rt) && rt)
                        UnityEngine.Object.Destroy(rt.gameObject);
                    backpackPanels.Remove(key);
                    backpackPanelImages.Remove(key);
                    if (backpackPanelLabels.TryGetValue(key, out var lblGo) && lblGo)
                        UnityEngine.Object.Destroy(lblGo);
                    backpackPanelLabels.Remove(key);
                }

                // Create / update one panel per equipped bag prefab.
                int defaultIndex = 0;
                foreach (var kv in byPrefab)
                {
                    string prefab = kv.Key;
                    var bagSlots = kv.Value;
                    int rows = 0, cols = 0;
                    foreach (var bs in bagSlots)
                    {
                        rows = Mathf.Max(rows, bs.GridPosition.y);
                        cols = Mathf.Max(cols, bs.GridPosition.x);
                    }
                    // Distinct counts (since bagSlots is irregular shaped only in degenerate cases).
                    var ys = new HashSet<int>();
                    var xs = new HashSet<int>();
                    foreach (var bs in bagSlots) { ys.Add(bs.GridPosition.y); xs.Add(bs.GridPosition.x); }
                    rows = ys.Count;
                    cols = xs.Count;

                    // Special-slot rail (VABackpacks 1.0.20+, upgraded bags): one column right of the
                    // grid, so the panel grows by its width and by any rows the rail is taller than the grid.
                    var bagItem = BagSpecialRail.FindBag(prefab);
                    var railKinds = BagSpecialRail.KindsFor(bagItem);
                    float bodyW = cols * tileSize + BagSpecialRail.RailWidth(railKinds.Length, tileSize);
                    float bodyH = Mathf.Max(rows, railKinds.Length) * tileSize;
                    float panelW = bodyW + BackpackPanelPadding * 2f;
                    float panelH = BackpackLabelRows * tileSize + bodyH + BackpackPanelPadding * 2f;

                    if (!backpackPanels.TryGetValue(prefab, out var panel) || panel == null)
                    {
                        panel = new GameObject($"VAInventoryBackpackPanel_{prefab}",
                            typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>();
                        panel.SetParent(InventoryGui.instance.m_player, false);
                        // Render on top of equipmentBackground / inventory
                        // grid so the draggable header is reachable. Cells
                        // inside still get their own raycast targets.
                        panel.SetAsLastSibling();
                        panel.anchorMin = new Vector2(0f, 1f);
                        panel.anchorMax = new Vector2(0f, 1f);
                        panel.pivot = new Vector2(0f, 1f);

                    // Initial position: from config if saved, else
                    // default cascade to the right of the equipment
                    // panel so multiple panels don't overlap on first use.
                    if (!ConfigManager.Instance.TryGetBackpackPanelPosition(prefab, out Vector2 initPos))
                    {
                        float baseX = EquipmentPanelWidth + 32f;
                        float baseY = -((float)defaultIndex) * (panelH + 16f);
                        initPos = new Vector2(baseX, baseY);
                    }
                    panel.anchoredPosition = initPos;

                        // Background image
                        var img = panel.GetComponent<Image>();
                        var customBackground = VerdantsAscent.Managers.UIBackgroundManager.GetBackground("EquipmentPanel");
                        if (customBackground)
                        {
                            img.sprite = customBackground;
                            img.type = Image.Type.Sliced;
                        }
                        else if (background)
                        {
                            img.sprite = background;
                            img.type = Image.Type.Sliced;
                        }
                        img.color = equipmentBackgroundImage ? equipmentBackgroundImage.color : Color.white;
                        backpackPanelImages[prefab] = img;

                        // Drag handler — persist anchoredPosition on drag-end.
                        // Closure captures the prefab string by value.
                        string capturedPrefab = prefab;
                        var drag = panel.gameObject.AddComponent<UniversalDragHandler>();
                        drag.TargetPanel = panel;
                        drag.OnPositionChanged = (pos) =>
                            ConfigManager.Instance.SetBackpackPanelPosition(capturedPrefab, pos);

                        backpackPanels[prefab] = panel;
                    }

                    panel.sizeDelta = new Vector2(panelW, panelH);

                    // Label (bag display name) — created once, text
                    // refreshed each rebuild so locale changes apply.
                    if (!backpackPanelLabels.TryGetValue(prefab, out var labelGo) || labelGo == null)
                    {
                        labelGo = new GameObject("BackpackLabel",
                            typeof(RectTransform), typeof(TextMeshProUGUI));
                        labelGo.transform.SetParent(panel, false);

                        var lblRect = labelGo.GetComponent<RectTransform>();
                        lblRect.anchorMin = new Vector2(0f, 1f);
                        lblRect.anchorMax = new Vector2(0f, 1f);
                        lblRect.pivot = new Vector2(0f, 1f);
                        lblRect.anchoredPosition = Vector2.zero;

                        var label = labelGo.GetComponent<TextMeshProUGUI>();
                        label.enableAutoSizing = true;
                        var font = GetAveriaSansFont();
                        if (font != null) label.font = font;
                        label.fontSizeMin = 12f;
                        label.fontSizeMax = 16f;
                        label.color = Color.white;
                        label.alignment = TextAlignmentOptions.Center;
                        label.raycastTarget = false;

                        backpackPanelLabels[prefab] = labelGo;
                    }
                    // Refresh size + text every rebuild
                    var labelRect = backpackPanelLabels[prefab].GetComponent<RectTransform>();
                    labelRect.sizeDelta = new Vector2(panelW, tileSize);
                    var labelTmp = backpackPanelLabels[prefab].GetComponent<TextMeshProUGUI>();
                    labelTmp.text = bagSlots.Count > 0 ? bagSlots[0].Name : "Backpack";

                    if (elementPrefab == null)
                        elementPrefab = InventoryGui.instance.m_playerGrid?.m_elementPrefab;
                    BagSpecialRail.Build(panel, bagItem, railKinds, elementPrefab,
                        new Vector2(BackpackPanelPadding + cols * tileSize + BagSpecialRail.RailGap,
                                    -BackpackPanelPadding - BackpackLabelRows * tileSize),
                        tileSize);

                    defaultIndex++;
                }
            }

            private static TMP_FontAsset GetAveriaSansFont()
            {
                if (IsValidFontAsset(averiaSansFont)) return averiaSansFont;

                averiaSansFont = Resources.FindObjectsOfTypeAll<TMP_Text>()
                    .Where(t => t != null && IsValidFontAsset(t.font))
                    .Select(t => t.font)
                    .FirstOrDefault(f => IsAveriaSansFontName(f.name));

                if (averiaSansFont) return averiaSansFont;

                averiaSansFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                    .FirstOrDefault(f => IsValidFontAsset(f) && IsAveriaSansFontName(f.name));

                return averiaSansFont;
            }

            private static bool IsAveriaSansFontName(string name)
            {
                if (string.IsNullOrEmpty(name)) return false;
                return name.IndexOf("Averia", StringComparison.OrdinalIgnoreCase) >= 0 &&
                       name.IndexOf("Sans", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private static bool IsValidFontAsset(TMP_FontAsset font)
            {
                if (font == null) return false;
                if (font.atlasTexture == null) return false;
                if (font.material == null) return false;
                return true;
            }



            /// <summary>Slots whose item is worn (armour, utility, ring, backpack...): a drop there equips. Storage, hand and
            /// consumable slots (misc, bag storage, ammo, food, torch, class bar, hotkey) only hold the item, as before.</summary>
            private static bool WornOnDrop(Slots.Slot slot, ItemDrop.ItemData item) =>
                slot.IsEquipmentSlot && item.IsEquipable()
                && !slot.IsMiscSlot && !slot.IsBackpackStorageSlot && !slot.IsAmmoSlot && !slot.IsFoodSlot
                && !slot.IsTorchSlot && !slot.IsClassBarSlot && !slot.IsHotkeySlot && !slot.IsPetSlot;

            /// <summary>Records the pointer-up position (runs before Button's click in the same input frame) for the cell click gate.</summary>
            public sealed class CellPointerUp : MonoBehaviour, IPointerUpHandler
            {
                private Vector2 lastUp;
                private int lastUpFrame = -1;

                public void OnPointerUp(PointerEventData eventData)
                {
                    lastUp = eventData.position;
                    lastUpFrame = Time.frameCount;
                }

                public static Vector2 PositionOr(GameObject go, Vector2 fallback)
                {
                    var rec = go ? go.GetComponent<CellPointerUp>() : null;
                    return rec != null && rec.lastUpFrame == Time.frameCount ? rec.lastUp : fallback;
                }
            }

            [HarmonyPatch(typeof(RectTransformUtility), "RectangleContainsScreenPoint", [typeof(RectTransform), typeof(Vector2)])]
            public static class RectTransformUtility_RectangleContainsScreenPoint_Patch
            {
                private static void Postfix(RectTransform rect, Vector2 screenPoint, ref bool __result)
                {
                    if (!InventoryGui.instance || rect != InventoryGui.instance.m_playerGrid.m_gridRoot) return;

                    var elementsList = (IList)fi_elements.GetValue(InventoryGui.instance.m_playerGrid);
                    int extraStart = Slots.InventorySizePlayer;

                    for (int i = extraStart; i < elementsList.Count; i++)
                    {
                        var go = ElemGo(elementsList[i]);
                        if (go && RectTransformUtility.RectangleContainsScreenPoint(go.GetComponent<RectTransform>(), screenPoint))
                        {
                            __result = true;
                            break;
                        }
                    }
                }



                private static Vector2i GetButtonPos(GameObject go)
                {
                    var elementsList = (IList)AccessTools.Field(typeof(InventoryGrid), "m_elements").GetValue(InventoryGui.instance.m_playerGrid);
                    for (int index = 0; index < elementsList.Count; ++index)
                    {
                        var elem = elementsList[index];
                        var elemGo = ElemGo(elem);
                        if ((UnityEngine.Object)elemGo == (UnityEngine.Object)go)
                        {
                            int _y = index / Slots.InventoryWidth;
                            return new Vector2i(index - _y * Slots.InventoryWidth, _y);
                        }
                    }
                    return new Vector2i(-1, -1);
                }
            }

            private static void SetInventorySlotBackgroundImage(object element, bool weightReduction)
            {
                Image icon = ElemIcon(element);
                if (icon == null) return;

                if (iconMaterial == null && icon.material) iconMaterial = icon.material;

                if (weightReduction)
                {
                    icon.enabled = true;
                    icon.material = null;
                    icon.sprite = lightenedSlot;
                    icon.transform.localScale = Vector3.one * 0.7f;
                    icon.color = new Color(0.5f, 0.5f, 0.3f, 0.3f);
                }
                else
                {
                    // Only reset material/scale, do NOT clear sprite or color
                    icon.material = iconMaterial;
                    icon.transform.localScale = originalScale == Vector3.zero ? Vector3.one : originalScale;
                    // Do not touch icon.sprite or icon.color here!
                }
            }

            private static void SetToolHotbarInventorySlotElement(object element, Slots.Slot slot)
            {
                if (element == null || slot == null) return;

                GameObject go = ElemGo(element);
                if (!go) return;

                var item = slot.Item;
                var icon = ElemIcon(element);
                if (icon != null)
                {
                    if (item != null && item.m_shared != null && item.m_shared.m_icons != null && item.m_shared.m_icons.Length > 0)
                    {
                        if (iconMaterial == null && icon.material) iconMaterial = icon.material;
                        icon.material = iconMaterial;
                        icon.transform.localScale = originalScale == Vector3.zero ? Vector3.one : originalScale;
                        icon.sprite = item.m_shared.m_icons[0];
                        icon.color = Color.white;
                        icon.enabled = true;
                    }
                    else
                    {
                        icon.material = null;
                        icon.transform.localScale = Vector3.one;
                        icon.sprite = quickSlot ?? background;
                        icon.color = emptySlotHintColor;
                        icon.enabled = true;
                    }
                }

                var amount = ElemAmount(element);
                if (amount)
                {
                    amount.text = item != null && item.m_stack > 1 ? item.m_stack.ToString() : "";
                    amount.enabled = item != null && item.m_stack > 1;
                }

                Transform binding = go.transform.Find("binding");
                if (binding)
                    SetSlotLabel(binding, slot, true);

                string[] overlayNames = ["durability", "foodicon", "noteleport", "quality", "queued", "selected", "equiped"];
                foreach (var name in overlayNames)
                {
                    var overlay = go.transform.Find(name);
                    if (overlay) overlay.gameObject.SetActive(false);
                }

                var button = go.GetComponent<Button>();
                ItemDrop.ItemData dragItem = InventoryGui.instance != null ? (ItemDrop.ItemData)fi_dragItem.GetValue(InventoryGui.instance) : null;
                bool unfit = dragItem != null && slot.IsActive && !slot.ItemFits(dragItem);
                SetSlotColor(button, unfit);
            }

            internal static void SetSlotColor(Button button, bool useUnfitColor)
            {
                if (!button) return;

                if (normalColor == Color.clear) normalColor = button.colors.normalColor;
                if (highlightedColor == Color.clear) highlightedColor = button.colors.highlightedColor;
                if (normalColorUnfit == Color.clear) normalColorUnfit = normalColor + new Color(0.3f, 0f, 0f, 0.1f);
                if (highlightedColorUnfit == Color.clear) highlightedColorUnfit = highlightedColor + new Color(0.3f, 0f, 0f, 0.1f);

                var cb = button.colors;
                cb.normalColor = useUnfitColor ? normalColorUnfit : normalColor;
                cb.highlightedColor = useUnfitColor ? highlightedColorUnfit : highlightedColor;
                button.colors = cb;
            }

            internal static void SetSlotLabel(Transform binding, Slots.Slot slot, bool hotbarElement = false)
            {
                if (!binding || !slot.IsActive) return;

                var rect = binding.GetComponent<RectTransform>();
                var text = binding.GetComponent<TMP_Text>();
                SetSlotLabel(rect, text, slot, hotbarElement);
            }

            internal static void SetSlotLabel(RectTransform rect, TMP_Text text, Slots.Slot slot, bool hotbarElement = false)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                rect.sizeDelta = Vector2.zero;
                rect.anchoredPosition = Vector2.zero;

                text.enableAutoSizing = true;
                text.text = hotbarElement ? (ZInput.IsGamepadActive() ? "" : slot.GetShortcutText()) : (slot.IsFree ? "" : slot.Name);
                text.enabled = true;
                text.overflowMode = TextOverflowModes.Overflow;
                text.fontSizeMin = 10f;
                text.fontSizeMax = 14f;
                text.color = slot.IsHotkeySlot ? Color.cyan : Color.white;
                text.margin = new Vector4(3f, 0f, 3f, 0f);
                text.horizontalAlignment = HorizontalAlignmentOptions.Center;
                text.verticalAlignment = VerticalAlignmentOptions.Top;
            }

            private static void SetSlotBackgroundImage(object element, Slots.Slot slot)
            {
                Image icon = ElemIcon(element);
                if (icon == null) return;

                if (iconMaterial == null && icon.material) iconMaterial = icon.material;

                bool isFree = slot.IsFree;

                if (!isFree)
                {
                    // Only reset material/scale, do NOT touch sprite or color
                    icon.material = iconMaterial;
                    icon.transform.localScale = originalScale == Vector3.zero ? Vector3.one : originalScale;
                    // Do not set icon.sprite or icon.color here!
                }
                else
                {
                    // Empty slot: clear everything
                    icon.material = null;
                    icon.sprite = null;
                    icon.color = Color.clear;
                    icon.transform.localScale = Vector3.one;
                }
            }

            internal static void SetSlotElement(object element, Slots.Slot slot, bool regularInventoryUnfitsForDragItem, int slotPanelIndex = -1)
            {
                GameObject go = ElemGo(element);
                if (!go) return;
                go.SetActive(slot.IsActive);
                // In Unified the prefab cell owns the geometry. Writing slot.Position here would undo the
                // cell placement on EVERY refresh, not just drift it - the element is parented to a ~50px
                // cell, so a position meant for the equipment background throws it clean out of the panel.
                var elemRect = go.GetComponent<RectTransform>();
                if (UnifiedScreen.TryGetCell(slot, out _))
                    UnifiedScreen.FillCell(elemRect);
                else if (UnifiedScreen.Enabled && !slot.IsBackpackStorageSlot)
                    go.SetActive(false);   // no cell after a re-key: hidden, never thrown to its legacy coordinates
                else
                    elemRect.anchoredPosition = slot.Position;

                var item = slot.Item;
                var icon = ElemIcon(element);

                // Show icon in equipment panel if item exists
                if (icon != null)
                {
                    if (item != null && item.m_shared != null && item.m_shared.m_icons != null && item.m_shared.m_icons.Length > 0)
                    {
                        icon.sprite = item.m_shared.m_icons[0];
                        icon.color = Color.white;
                        icon.enabled = true;
                    }
                    else
                    {
                        // Empty slot: make icon fully transparent and clear
                        icon.sprite = null;
                        icon.color = Color.clear;
                        icon.enabled = true;
                    }
                }

                // Amount text
                var amount = ElemAmount(element);
                if (amount)
                {
                    amount.text = item != null && item.m_stack > 1 ? item.m_stack.ToString() : "";
                    amount.enabled = item != null && item.m_stack > 1;
                }

                // Label (binding): only show slot name for first12 slots if empty and not a misc slot
                Transform binding = go.transform.Find("binding");
                if (binding)
                {
                    var rect = binding.GetComponent<RectTransform>();
                    var text = binding.GetComponent<TMP_Text>();
                    // A tool cell reads its hotkey (Alt+1...), exactly as the grid row it replaces did.
                    if (rect && text && Slots.toolHotbarSlots.Contains(slot))
                        SetSlotLabel(rect, text, slot, true);
                    else if (rect && text)
                    {
                        rect.anchorMin = Vector2.zero;
                        rect.anchorMax = Vector2.one;
                        rect.offsetMin = rect.offsetMax = Vector2.zero;
                        rect.sizeDelta = Vector2.zero;
                        rect.anchoredPosition = Vector2.zero;
                        text.enableAutoSizing = true;
                        // Show the slot's text label when the slot is empty
                        // (helps the player see where each slot type goes).
                        // Only the equipment-panel block — misc rows stay blank.
                        // Unified (Fire's approved mockup): every rail cell keeps its name, filled or not, so an item never
                        // reads as sitting under the next cell's caption.
                        if (slotPanelIndex >= 0 && slotPanelIndex < EQUIPMENT_PANEL_SLOTS && (item == null || UnifiedScreen.Enabled) && !slot.IsMiscSlot && !slot.IsBackpackStorageSlot)
                            text.text = slot.Name;
                        else
                            text.text = "";
                        text.enabled = true;
                        text.overflowMode = TextOverflowModes.Overflow;
                        text.fontSizeMin = 10f;
                        text.fontSizeMax = 14f;
                        text.color = slot.IsHotkeySlot ? Color.cyan : Color.white;
                        text.margin = new Vector4(3f, 0f, 3f, 0f);
                        text.horizontalAlignment = HorizontalAlignmentOptions.Center;
                        text.verticalAlignment = VerticalAlignmentOptions.Top;
                        // Unified (Fire 10-07): the caption sits in a strip ABOVE the cell, never over the icon, with a short
                        // name; the coin purse has none (its pouch-row header already says "Coin purse").
                        if (UnifiedScreen.Enabled && text.text.Length > 0 && UnifiedScreen.TryGetCell(slot, out _))
                            UnifiedScreen.PlaceCaption(rect, text, slot);
                    }
                }

                // Button color for unfit items / drag
                Button button = go.GetComponent<Button>();
                ItemDrop.ItemData dragItem = (ItemDrop.ItemData)fi_dragItem.GetValue(InventoryGui.instance);
                bool unfit = dragItem != null && slot.IsActive && !slot.ItemFits(dragItem);
                SetSlotColor(button, unfit || regularInventoryUnfitsForDragItem);
            }

            internal static void SetSlotsPositions()
            {
                // Position every visible side-panel slot. With per-bag
                // detached panels, each backpack-storage slot is positioned
                // relative to ITS OWN panel (origin = panel's top-left
                // + padding offset), not a single shared origin like before.
                var extraSlots = Slots.AllSlots()
                    .Where(s => s != null
                             && !s.IsCurrencyVaultSlot
                             && s.Index >= Slots.InventorySizePlayer
                             && s.Index < Slots.InventorySizePlayer + SidePanelMaxSlots)
                    .ToArray();

                // Group backpack-storage slots by their bag prefab so each
                // bag's grid uses its own (minX, minY) origin.
                var storageByPrefab = new Dictionary<string, (int minX, int minY)>();
                foreach (var s in extraSlots)
                {
                    if (!s.IsBackpackStorageSlot) continue;
                    string prefab = Slots.GetBackpackPrefabFromSlot(s) ?? "Unknown";
                    if (!storageByPrefab.TryGetValue(prefab, out var current))
                    {
                        storageByPrefab[prefab] = (s.GridPosition.x, s.GridPosition.y);
                    }
                    else
                    {
                        storageByPrefab[prefab] = (
                            Mathf.Min(current.minX, s.GridPosition.x),
                            Mathf.Min(current.minY, s.GridPosition.y)
                        );
                    }
                }

                int equipmentPanelStartY = Slots.InventoryHeightPlayer + Slots.EquipmentPanelRowOffset;
                foreach (var slot in extraSlots)
                {
                    int col;
                    int row;
                    if (slot.IsBackpackStorageSlot)
                    {
                        string prefab = Slots.GetBackpackPrefabFromSlot(slot) ?? "Unknown";
                        var (minX, minY) = storageByPrefab[prefab];
                        col = slot.GridPosition.x - minX;
                        row = BackpackLabelRows + (slot.GridPosition.y - minY);
                        // Apply panel-internal padding so cells aren't
                        // flush against the background edges.
                        slot.SetPosition(new Vector2(
                            BackpackPanelPadding + col * tileSize,
                            -BackpackPanelPadding - row * tileSize));
                    }
                    else
                    {
                        col = slot.GridPosition.x;
                        row = slot.GridPosition.y - equipmentPanelStartY;
                        slot.SetPosition(new Vector2(col * tileSize, -row * tileSize));
                    }
                }
            }

            private const int ExtraRowsTunedPanelRows = Slots.VanillaInventoryHeight;
            private const float ExtraRowsTunedCorrectionPerRow = 0.01f;
            private static readonly AccessTools.FieldRef<InventoryGui, float> fr_basePlayerPanelHeight =
                AccessTools.FieldRefAccess<InventoryGui, float>("m_playerHeight");

            // The extension is tuned against the four-row panel. Vanilla grows the panel for rows bought from a
            // trader, so rescale by the panel's growth to keep the same extension in pixels.
            private static float ExtraRowsBackgroundExtension(int extraRows)
            {
                float tunedFraction = (float)extraRows / ExtraRowsTunedPanelRows
                                      - ExtraRowsTunedCorrectionPerRow * Mathf.Max(extraRows - 1, 0);
                float currentPanelHeight = InventoryGui.instance.m_player.rect.height;
                if (currentPanelHeight <= 0f) return tunedFraction;
                return tunedFraction * fr_basePlayerPanelHeight(InventoryGui.instance) / currentPanelHeight;
            }

            // Stretches the carry grid's own backdrop down over the extra rows.
            private static void FitGridBackground(int extraRows)
            {
                if (!inventoryBackground) return;
                float anchorY = -ExtraRowsBackgroundExtension(extraRows);
                inventoryBackground.anchorMin = new Vector2(inventoryBackground.anchorMin.x, anchorY);
                inventoryBackground.anchorMax = new Vector2(inventoryBackground.anchorMax.x, 1f);

                LayoutRebuilder.ForceRebuildLayoutImmediate(inventoryBackground);
                LayoutRebuilder.ForceRebuildLayoutImmediate(InventoryGui.instance.m_player.GetComponent<RectTransform>());
            }

            internal static void UpdateEquipmentBackground()
            {
                if (!InventoryGui.instance) return;

                if (inventoryBackground == null)
                    inventoryBackground = InventoryGui.instance.m_player?.Find("Bkg")?.GetComponent<RectTransform>();

                if (inventoryBackground == null) return;

                // ONE panel owner. The unified layout draws its own CharacterPanel, and this one is created
                // with new GameObject(), which is active by default - so every rebuild after Apply() hid it
                // put a second backdrop straight back on top of the first. The slots are unaffected; they are
                // parented into the unified cells separately. The carry grid's own backdrop still has to cover
                // the extra rows in both layouts.
                if (UnifiedScreen.Enabled)
                {
                    if (equipmentBackground != null && equipmentBackground.gameObject.activeSelf)
                        equipmentBackground.gameObject.SetActive(false);
                    FitGridBackground(Slots.ExtraRowsPlayer);
                    return;
                }

                if (!equipmentBackground && InventoryGui.instance.m_player)
                {
                    var uiGroup = InventoryGui.instance.m_player.GetComponent<UIGroupHandler>();
                    var enableWhenActive = uiGroup?.m_enableWhenActiveAndGamepad.transform;

                    inventoryDarken = InventoryGui.instance.m_player.Find("Darken").GetComponent<RectTransform>();

                    equipmentBackground = new GameObject("VAInventoryEquipmentPanel", typeof(RectTransform)).GetComponent<RectTransform>();
                    equipmentBackground.gameObject.layer = inventoryBackground.gameObject.layer;
                    equipmentBackground.SetParent(InventoryGui.instance.m_player, false);
                    equipmentBackground.SetSiblingIndex(1 + (enableWhenActive ? enableWhenActive.GetSiblingIndex() : inventoryDarken.GetSiblingIndex()));

                    equipmentBackground.anchorMin = new Vector2(0f, 1f);
                    equipmentBackground.anchorMax = new Vector2(0f, 1f);
                    equipmentBackground.anchoredPosition = Vector2.zero;
                    equipmentBackground.sizeDelta = Vector2.zero;

                    // Attach drag handler
                    if (equipmentBackground.GetComponent<UniversalDragHandler>() == null)
                    {
                        var drag = equipmentBackground.gameObject.AddComponent<UniversalDragHandler>();
                        drag.TargetPanel = equipmentBackground;
                    }

                    var darkenCopy = UnityEngine.Object.Instantiate(inventoryDarken, equipmentBackground);
                    darkenCopy.name = "Darken";

                    var bkgCopy = UnityEngine.Object.Instantiate(inventoryBackground.transform, equipmentBackground);
                    bkgCopy.name = "Bkg";
                    equipmentBackgroundImage = bkgCopy.GetComponent<Image>();
                    inventoryBackgroundImage = inventoryBackground.GetComponent<Image>();

                    if (enableWhenActive)
                    {
                        inventorySelectedFrame = enableWhenActive.GetChild(0) as RectTransform;
                        selectedFrame = UnityEngine.Object.Instantiate(inventorySelectedFrame, enableWhenActive);
                        selectedFrame.name = "selected (VAInventory)";
                    }

                    UpdateBackground();
                    CreateSidePanelElements();

                    // First open only: the side-slot elements are actually born HERE (this one-time block),
                    // not in the InventoryGui.Show postfix. On the first open equipmentBackground is still null
                    // when Show runs, so UpdatePanel's CreateSidePanelElements bails — and by the time the
                    // elements exist, the Show/UpdateGui passes have already consumed isDirty. The freshly
                    // cloned elements therefore keep the vanilla prefab's "all overlays visible" look until a
                    // click/reopen marks dirty again. Mark dirty and derive overlays now so the FIRST open
                    // renders correctly. (Reopens hit the Show path, where equipmentBackground already exists.)
                    MarkDirty();
                    UpdateInventorySlots();
                }

                if (equipmentBackgroundImage && background)
                    equipmentBackgroundImage.sprite = background;

                int extraRows = Slots.ExtraRowsPlayer;
                FitGridBackground(extraRows);

                if (updateSidePanelsInFrames > 0) updateSidePanelsInFrames--;

                if (updateSidePanelsInFrames == 0)
                {
                    if (updateSidePanelsPreviousExtraRows == -10)
                        updateSidePanelsPreviousExtraRows = extraRows;
                    else if (updateSidePanelsPreviousExtraRows != extraRows)
                        updateSidePanelsPreviousExtraRows = extraRows;
                }

                if (equipmentBackground.GetComponent<EquipmentBackgroundDragCleanup>() == null)
                {
                    equipmentBackground.gameObject.AddComponent<VerdantsAscent.Managers.EquipmentBackgroundDragCleanup>();
                }
            }

            internal static void ClearPanel()
            {
                foreach (var elem in sidePanelElements)
                {
                    var go = ElemGo(elem);
                    if (go) UnityEngine.Object.Destroy(go);
                }
                sidePanelElements.Clear();
                sidePanelSlots.Clear();
                if (backpackLabelObject)
                {
                    UnityEngine.Object.Destroy(backpackLabelObject);
                    backpackLabelObject = null;
                }
                // Tear down every per-bag detached panel.
                foreach (var kv in backpackPanels)
                {
                    if (kv.Value) UnityEngine.Object.Destroy(kv.Value.gameObject);
                }
                backpackPanels.Clear();
                backpackPanelImages.Clear();
                foreach (var kv in backpackPanelLabels)
                {
                    if (kv.Value) UnityEngine.Object.Destroy(kv.Value);
                }
                backpackPanelLabels.Clear();

                inventoryDarken = null;
                inventoryBackground = null;
                equipmentBackground = null;
                equipmentBackgroundImage = null;
                inventoryBackgroundImage = null;
                selectedFrame = null;
                inventorySelectedFrame = null;

                normalColor = highlightedColor = normalColorUnfit = highlightedColorUnfit = Color.clear;
                iconMaterial = null;
                originalScale = Vector3.zero;
                updateSidePanelsInFrames = 0;
                updateSidePanelsPreviousExtraRows = -10;
            }

            public class SidePanelDefaults
            {
                public bool active;
                public Vector2 anchoredPosition;
                public Vector2 anchorMin;
                public Vector2 anchorMax;
            }


            [HarmonyPatch(typeof(InventoryGui), "OnDestroy")]
            private static class InventoryGui_OnDestroy_ClearObjects
            {
                // Teardown on logout too: the preview camera is parented to the player, and the prefab
                // instance to the GUI root, so both have to go with the screen or they leak across sessions.
                private static void Postfix()
                {
                    ClearPanel();
                    UnifiedScreen.Reset();
                }
            }

            internal static int CaptureGridElementCount(InventoryGrid grid)
            {
                if (grid != InventoryGui.instance?.m_playerGrid) return 0;
                return ((IList)fi_elements.GetValue(grid)).Count;
            }

            internal static void AfterUpdateGui(InventoryGrid grid, int elementCountBefore)
            {
                if (grid != InventoryGui.instance?.m_playerGrid) return;
                if (elementCountBefore != ((IList)fi_elements.GetValue(grid)).Count)
                    MarkDirty();

                UpdateInventorySlots();
            }

            [HarmonyPatch(typeof(InventoryGui), "SetupDragItem")]
            public static class InventoryGui_SetupDragItem_UpdateSlotsOnItemDrag
            {
                private static void Prefix(ref bool __state)
                {
                    __state = fi_dragItem.GetValue(InventoryGui.instance) != null;
                }

                private static void Postfix(bool __state)
                {
                    MarkDirty();
                    if (fi_dragItem.GetValue(InventoryGui.instance) == null && __state)
                        UpdateSidePanels();
                }
            }

            // --- Only call migration logic on config changes ---
            // Add a method to be called when config changes:
            public static void OnConfigChanged(bool gridSizeReduced, bool equipmentPanelSizeReduced, bool mainGridRowsHidden)
            {
             bool needsMigration = gridSizeReduced || equipmentPanelSizeReduced || mainGridRowsHidden;
             if (needsMigration)
             {
               Slots.InitializeSlots();
               MigrateEquipmentPanelItems(); // Moves items first
               Slots.UpdateSlotsGridPosition(); // Cache after moves
               UpdatePanel();
             }
             else
             {
               // Only update visuals, do not migrate slots
               Slots.InitializeSlots();
               Slots.UpdateSlotsGridPosition();
               UpdatePanel();
             }
            }

            public static void RefreshSlotsAndPanel()
            {
                // Use reflection to check if a container is open (prevents migration on chest transfer)
                var fi_currentContainer = AccessTools.Field(typeof(InventoryGui), "m_currentContainer");
                var currentContainer = fi_currentContainer?.GetValue(InventoryGui.instance);
                if (InventoryGui.instance == null || currentContainer == null)
                {
                    Slots.InitializeSlots();
                    MigrateEquipmentPanelItems(); // Moves items first
                    Slots.UpdateSlotsGridPosition(); // Cache after moves
                    UnifiedScreen.RebuildCells();
                    UpdatePanel();
                }
                else
                {
                    // Only update visuals, do not migrate slots
                    Slots.InitializeSlots();
                    Slots.UpdateSlotsGridPosition();
                    UnifiedScreen.RebuildCells();
                    UpdatePanel();
                }
            }

            // Visuals-only refresh for events that don't change slot layout (equip / unequip /
            // hotbar slot activation). DO NOT call MigrateEquipmentPanelItems here — that is a
            // config-change operation. Migration treats every position with y >= InventoryHeightPlayer
            // as "hidden grid" (see Slots.IsGridPositionHidden) and pulls items off food/ammo
            // slots into a relocate list, which can drop them on the ground when the
            // free-slot lookup races the freshly re-initialised slot binding. Equip/unequip
            // never changes the slot layout, so migration is unnecessary and item-loss-prone.
            public static void RefreshPanelOnly()
            {
                Slots.InitializeSlots();
                Slots.UpdateSlotsGridPosition();
                UnifiedScreen.RebuildCells(); // the Slots were re-newed: re-key the Unified cells first (Fire 10-06 blank panel)
                UpdatePanel();
            }

            /// <summary>
            /// Remaps items from slots that are no longer visible due to config (columns, rows, equipment panel rows).
            /// </summary>
            private static void MigrateEquipmentPanelItems()
            {
                EquipmentPanel.IsMigrating = true;
                var inventory = Slots.PlayerInventory;
                if (inventory == null) { EquipmentPanel.IsMigrating = false; return; }

                // Define the always-visible slot IDs (first6 slots)
                var alwaysVisibleSlotIDs = new HashSet<string>
                {
                    Slots.helmetSlotID,
                    Slots.shoulderSlotID,
                    Slots.legsSlotID,
                    Slots.chestSlotID,
                    Slots.utilitySlotID,
                    Slots.trinketSlotID
                };

                bool showFood = ConfigManager.Instance.configShowFoodSlots.Value;
                bool showAmmo = ConfigManager.Instance.configShowAmmoSlots.Value;
                int numColumns =2 + (showFood ?1 :0) + (showAmmo ?1 :0);

                // Gather all new active slots
                var allSlots = Slots.AllSlots()
                .Where(s => s != null && s.IsActive && s.IsEquipmentSlot)
                .ToArray();

                // Log all ammo slots before migration
                foreach (var slot in allSlots.Where(s => s.IsAmmoSlot))
                    Debug.Log($"[VAInventory] PRE-MIGRATE Ammo slot {slot.Index} (x={slot.GridPosition.x}, y={slot.GridPosition.y}) IsFree={slot.IsFree} Item={(slot.Item != null ? slot.Item.m_shared.m_name : "(empty)")}");

                var itemsToRelocate = new List<ItemDrop.ItemData>();

                // Step1: Collect items from any hidden grid position (row, column, or equipment panel)
                var hiddenItems = inventory.GetAllItems()
                .Where(i => Slots.IsGridPositionHidden(i.m_gridPos))
                // Skip items the migration must NOT touch:
                //  - Always-visible equipment slots (helmet, chest, …) — owned by the slot binding itself.
                //  - Backpack storage panels — owned by VAInventory.BackpackStorageSync, which captures
                //    them into the bag's customData blob on unequip. If the migration relocates a bag
                //    item to a main-inventory slot first, it survives the unequip serialize-and-drain
                //    pass (because the slot ID no longer matches the bag prefab) and ends up in both
                //    the player's regular inventory AND the bag's blob — the duplication symptom.
                .Where(i => {
                    var slot = Slots.GetItemSlot(i);
                    if (slot == null) return true;
                    if (alwaysVisibleSlotIDs.Contains(slot.ID)) return false;
                    if (slot.IsBackpackStorageSlot) return false;
                    return true;
                })
                .ToList();

                foreach (var item in hiddenItems)
                {
                    if (Player.m_localPlayer.IsItemEquiped(item))
                        Player.m_localPlayer.UnequipItem(item, false);

                    Debug.Log($"[VAInventory] MIGRATE: Collecting item '{item.m_shared.m_name}' from hidden grid {item.m_gridPos.x}, {item.m_gridPos.y}");
                    itemsToRelocate.Add(item);
                }

                // Step2: Collect items from visible columns that no longer fit (e.g., Food in new Ammo slot)
                foreach (var slot in allSlots)
                {
                    var item = slot.Item;
                    // Skip migration for items in always-visible slots
                    if (item != null && alwaysVisibleSlotIDs.Contains(slot.ID))
                        continue;
                    // Skip backpack storage panels — see Step1's skip-list comment.
                    if (item != null && slot.IsBackpackStorageSlot)
                        continue;
                    if (item != null && (!slot.IsActive || !slot.ItemFits(item)))
                    {
                        if (Player.m_localPlayer.IsItemEquiped(item))
                            Player.m_localPlayer.UnequipItem(item, false);

                        Debug.Log($"[VAInventory] MIGRATE: Collecting item '{item.m_shared.m_name}' from slot {slot.ID} (x={slot.GridPosition.x}, y={slot.GridPosition.y}) due to restriction");
                        itemsToRelocate.Add(item);
                    }
                }

                // Step3: Relocate items to fitting slots in the new visible layout
                var freeAmmoSlots = allSlots.Where(s => s.IsActive && s.IsFree && s.IsAmmoSlot).ToList();
                foreach (var item in itemsToRelocate)
                {
                    Slot targetSlot = null;
                    if (Slots.IsAmmoSlotItem(item))
                    {
                        if (freeAmmoSlots.Count >0)
                        {
                            targetSlot = freeAmmoSlots[0];
                            freeAmmoSlots.RemoveAt(0);
                            Debug.Log($"[VAInventory] MIGRATE: Placing ammo '{item.m_shared.m_name}' into ammo slot {targetSlot.ID} (x={targetSlot.GridPosition.x}, y={targetSlot.GridPosition.y})");
                        }
                        else
                        {
                            Debug.Log($"[VAInventory] MIGRATE: No ammo slot available for '{item.m_shared.m_name}', will try inventory");
                        }
                    }
                    else if (Slots.IsFoodSlotItem(item))
                        targetSlot = allSlots.FirstOrDefault(s => s.IsActive && s.IsFree && s.IsFoodSlot);
                    else if (Slots.IsTrinketSlotItem(item))
                        targetSlot = allSlots.FirstOrDefault(s => s.IsActive && s.IsFree && s.IsTrinketSlot);
                    else if (Slots.IsUtilitySlotItem(item))
                        targetSlot = allSlots.FirstOrDefault(s => s.IsActive && s.IsFree && s.ID == Slots.utilitySlotID);
                    else if (Slots.IsHelmetSlotItem(item))
                        targetSlot = allSlots.FirstOrDefault(s => s.IsActive && s.IsFree && s.ID == Slots.helmetSlotID);
                    else if (Slots.IsChestSlotItem(item))
                        targetSlot = allSlots.FirstOrDefault(s => s.IsActive && s.IsFree && s.ID == Slots.chestSlotID);
                    else if (Slots.IsLegsSlotItem(item))
                        targetSlot = allSlots.FirstOrDefault(s => s.IsActive && s.IsFree && s.ID == Slots.legsSlotID);
                    else if (Slots.IsShoulderSlotItem(item))
                        targetSlot = allSlots.FirstOrDefault(s => s.IsActive && s.IsFree && s.ID == Slots.shoulderSlotID);

                    // Fallback: any valid active slot that fits the item
                    targetSlot ??= allSlots.FirstOrDefault(s => s.IsActive && s.IsFree && s.ItemFits(item));

                    if (targetSlot != null)
                    {
                        // Internal move: just update pos (no remove/add needed)
                        item.m_gridPos = targetSlot.GridPosition;
                        targetSlot.CacheItem();
                        var slotAfter = Slots.GetSlotInGrid(targetSlot.GridPosition);
                        string slotItemName = slotAfter?.Item != null ? slotAfter.Item.m_shared.m_name : "(empty)";
                        bool slotIsFree = slotAfter?.IsFree ?? true;
                        Debug.Log($"[VAInventory] POST-ADD: Slot {targetSlot.ID} (x={targetSlot.GridPosition.x}, y={targetSlot.GridPosition.y}) IsFree={slotIsFree} Item={slotItemName}");
                        Debug.Log($"[VAInventory] MIGRATE: Placed '{item.m_shared.m_name}' into slot {targetSlot.ID} (x={targetSlot.GridPosition.x}, y={targetSlot.GridPosition.y})");
                    }
                    else
                    {
                        Debug.LogWarning($"[VAInventory] MIGRATE: No valid slot found for '{item.m_shared.m_name}', dropping at {item.m_gridPos}");
                        InventoryInteraction.ForceDropItemOnGround(inventory, item);
                    }
                }

                // Log all ammo slots after migration
                foreach (var slot in allSlots.Where(s => s.IsAmmoSlot))
                    Debug.Log($"[VAInventory] POST-MIGRATE Ammo slot {slot.Index} (x={slot.GridPosition.x}, y={slot.GridPosition.y}) IsFree={slot.IsFree} Item={(slot.Item != null ? slot.Item.m_shared.m_name : "(empty)")}");

                inventory.m_onChanged?.Invoke();
                EquipmentPanel.IsMigrating = false;
            }

            // Verbose logging flag
            public static bool VerboseLogging = false;
            private static void Log(string message)
            {
                if (VerboseLogging)
                    Debug.Log(message);
            }
            private static void LogWarning(string message)
            {
                if (VerboseLogging)
                    Debug.LogWarning(message);
            }

            // Drag handler for extra slots
            public class ExtraSlotDragHandler : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
            {
                private Slots.Slot slot;
                public void Init(Slots.Slot s) { slot = s; }
                public void OnPointerDown(PointerEventData eventData) { }
                public void OnPointerUp(PointerEventData eventData) { }
                public void OnBeginDrag(PointerEventData eventData)
                {
                    var grid = InventoryGui.instance.m_playerGrid;
                    var pos = slot.GridPosition;
                    var itemAt = Slots.PlayerInventory.GetItemAt(pos.x, pos.y);
                    // Valheim 1.0 removed BeginDragItem/EndDragItem; the whole drag lifecycle is SetupDragItem(item, inventory, amount).
                    var mi_setupDrag = AccessTools.Method(typeof(InventoryGui), "SetupDragItem");
                    mi_setupDrag?.Invoke(InventoryGui.instance, [itemAt, Slots.PlayerInventory, itemAt != null ? itemAt.m_stack : 1]);
                }
                public void OnDrag(PointerEventData eventData) { }
                public void OnEndDrag(PointerEventData eventData)
                {
                    // (null, null, 1) is what vanilla passes to END a drag.
                    var mi_setupDrag = AccessTools.Method(typeof(InventoryGui), "SetupDragItem");
                    mi_setupDrag?.Invoke(InventoryGui.instance, [null, null, 1]);
                }
            }

            // Tooltip handler for extra slots
            public class ExtraSlotTooltipHandler : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
            {
                private Slots.Slot slot;
                public void Init(Slots.Slot s) { slot = s; }
                public void OnPointerEnter(PointerEventData eventData)
                {
                    if (InventoryGui.instance == null || InventoryGui.instance.m_playerGrid == null) return;
                    var tooltip = GetComponent<UITooltip>();
                    if (tooltip == null) return;
                    var anchor = InventoryGui.instance.m_playerGrid.m_tooltipAnchor;
                    var item = slot.Item;
                    if (item != null)
                    {
                        tooltip.Set(item.m_shared.m_name, item.GetTooltip(), anchor);
                        return;
                    }
                    // Use slot.ID to determine slot type for tooltip
                    string topic = null, desc = null;
                    switch (slot.ID)
                    {
                        case Slots.foodSlotID:
                            topic = "Food Slot";
                            desc = "This slot is only for food.";
                            break;
                        case Slots.ammoSlotID:
                            topic = "Ammo Slot";
                            desc = "This slot is only for ammo.";
                            break;
                        case Slots.quickSlotID:
                            topic = "Quick Slot";
                            desc = "This slot is only for quick-access items.";
                            break;
                        case Slots.miscSlotID:
                            topic = "Misc Slot";
                            desc = "This slot is for miscellaneous items.";
                            break;
                        case Slots.petSlotID:
                            topic = "Pet";
                            desc = "Equip a pocket pet to have it follow you.";
                            break;
                        case Slots.trinketSlotID:
                            topic = "Trinket Slot";
                            desc = "This slot is only for trinkets.";
                            break;
                        case Slots.utilitySlotID:
                            topic = "Utility Slot";
                            desc = "This slot is for utility items.";
                            break;
                        case Slots.helmetSlotID:
                            topic = "Helmet Slot";
                            desc = "This slot is only for helmets.";
                            break;
                        case Slots.chestSlotID:
                            topic = "Chest Slot";
                            desc = "This slot is only for chest armor.";
                            break;
                        case Slots.legsSlotID:
                            topic = "Legs Slot";
                            desc = "This slot is only for leg armor.";
                            break;
                        case Slots.shoulderSlotID:
                            topic = "Shoulder Slot";
                            desc = "This slot is only for shoulder armor or capes.";
                            break;
                        case Slots.customSlotID:
                            topic = "Custom Slot";
                            desc = "This slot is for custom items.";
                            break;
                        default:
                            if (slot.ID.StartsWith("Extra"))
                            {
                                topic = "Extra Slot";
                                desc = "This is an extra slot for any item.";
                            }
                            else if (slot.ID.StartsWith(Slots.extraUtilitySlotID))
                            {
                                topic = "Extra Utility Slot";
                                desc = "This slot is for extra utility items.";
                            }
                            else if (slot.ID == Slots.emptySlotID)
                            {
                                topic = "Empty Slot";
                                desc = "This slot is empty.";
                            }
                            else if (slot.IsEquipmentSlot)
                            {
                                topic = "Equipment Slot";
                                desc = "Some extra inventory space dedicated to special item types...";
                            }
                            break;
                    }
                    if (!string.IsNullOrEmpty(topic) && !string.IsNullOrEmpty(desc))
                    {
                        tooltip.Set(topic, desc, anchor);
                    }
                    else
                    {
                        // Hide tooltip for truly empty/unknown slots
                        anchor.gameObject.SetActive(false);
                    }
                }
                public void OnPointerExit(PointerEventData eventData)
                {
                    InventoryGui.instance.m_playerGrid.m_tooltipAnchor.gameObject.SetActive(false);
                }
            }

            // Right-click handler for extra slots
            public class ExtraSlotRightClickHandler : MonoBehaviour, IPointerClickHandler
            {
                private Slots.Slot slot;
                public void Init(Slots.Slot s) { slot = s; }
                public void OnPointerClick(PointerEventData eventData)
                {
                    if (eventData.button != PointerEventData.InputButton.Right) return;
                    if (!InventoryGui.IsVisible()) return;
                    var player = Player.m_localPlayer;
                    if (player == null) return;

                    // Coin pouch: right-click opens the currency picker popup instead of dumping
                    // the stack to inventory (which the auto-deposit would just bounce back). In Unified the popup is on
                    // the left click, like the other pouches (Fire 10-07), and a right click does nothing.
                    if (slot != null && slot.IsCoinPurseSlot)
                    {
                        if (!UnifiedScreen.Enabled)
                            VerdantsAscent.Managers.CurrencyPouchPopup.Toggle(transform as RectTransform);
                        return;
                    }

                    var inventory = Slots.PlayerInventory;
                    var pos = slot.GridPosition;
                    var item = inventory.GetItemAt(pos.x, pos.y);
                    if (item == null) return;

                    // Unequip if equipped
                    if (player.IsItemEquiped(item))
                    {
                        player.UnequipItem(item, false);
                    }

                // Try to move to main inventory
                if (Slots.TryMakeFreeSpaceInPlayerInventory(true, out Vector2i freePos))
                {
                    inventory.RemoveItem(item);
                    item.m_gridPos = freePos;
                    inventory.AddItem(item);
                    inventory.m_onChanged?.Invoke();
                }
                else
                {
                    player.DropItem(inventory, item, item.m_stack);
                }

                EquipmentPanel.MarkDirty();
                    EquipmentPanel.UpdatePanel(); // Only update visuals, do not migrate
                }
            }

            /// <summary>
            /// Updates only the extra slots panel (sidePanelElements) and logs overlay sync for debugging.
            /// </summary>
            public static void UpdateExtraSlotsPanel()
            {
                if (InventoryGui.instance == null || InventoryGui.instance.m_playerGrid == null)
                    return;

                var grid = InventoryGui.instance.m_playerGrid;
                var elementsList = (IList)fi_elements.GetValue(grid);
                string[] overlayNames = ["durability", "amount", "foodicon", "noteleport", "quality", "queued", "selected", "equiped"];

                for (int i = 0; i < sidePanelElements.Count && i < sidePanelSlots.Count; i++)
                {
                    var slot = sidePanelSlots[i];
                    if (slot == null || !slot.IsActive) continue;

                    int mainIndex = slot.Index;
                    if (mainIndex < 0 || mainIndex >= elementsList.Count) continue;

                    var mainElem = elementsList[mainIndex];
                    var sideElem = sidePanelElements[i];
                    var mainGo = ElemGo(mainElem);
                    var sideGo = ElemGo(sideElem);
                    if (!mainGo || !sideGo) continue;
                    // An empty square shows no overlay (the same rule as UpdateInventorySlots). This runs in the Show
                    // postfix right after CreateSidePanelElements, before vanilla has synced its hidden elements.
                    if (slot.IsFree)
                    {
                        HideOverlays(sideGo);
                        continue;
                    }

                    foreach (var name in overlayNames)
                    {
                        var mainOverlay = mainGo.transform.Find(name);
                        var sideOverlay = sideGo.transform.Find(name);
                        if (mainOverlay && sideOverlay)
                        {
                            bool active = OverlayShown(mainOverlay);
                            sideOverlay.gameObject.SetActive(active);
                            if (active)
                            {
                                var mainText = mainOverlay.GetComponent<TMP_Text>();
                                var sideText = sideOverlay.GetComponent<TMP_Text>();
                                if (mainText && sideText)
                                    sideText.text = mainText.text;
                                var mainBar = mainOverlay.GetComponent<GuiBar>();
                                var sideBar = sideOverlay.GetComponent<GuiBar>();
                                if (mainBar != null && sideBar != null)
                                    sideBar.SetValue(mainBar.GetSmoothValue());
                            }
                            if (VerboseLogging)
                            {
                                string overlayInfo = $"[ExtraSlotOverlaySync] SlotIdx={mainIndex} Overlay={name} VanillaActive={active} CustomActive={sideOverlay.gameObject.activeSelf}";
                                Log(overlayInfo);
                            }
                        }
                    }
                }
            }

            public static bool IsMigrating = false;

            /// <summary>
            /// Clears the drag state and hides any floating drag icon/tooltips.
            /// </summary>
            public static void ClearDragState()
            {
     var fi_dragGo = AccessTools.Field(typeof(InventoryGui), "m_dragGo");
     var fi_dragAmount = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
     fi_dragItem.SetValue(InventoryGui.instance, null);
     var dragGoObj = fi_dragGo?.GetValue(InventoryGui.instance) as GameObject;
     if (dragGoObj != null)
     {
     dragGoObj.SetActive(false);
     UnityEngine.Object.Destroy(dragGoObj); // Extra aggressive cleanup
     fi_dragGo?.SetValue(InventoryGui.instance, null);
     }
     fi_dragAmount?.SetValue(InventoryGui.instance, null);
     }
        }
    }

