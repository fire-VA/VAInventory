using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // Applies the unified inventory layout to the LIVE vanilla screen.
    //
    // The prefab is a layout spec plus new chrome, never a replacement screen: Inventory_screen carries the
    // InventoryGui MonoBehaviour and the Canvas, so instantiating a copy would stand up a second
    // InventoryGui against vanilla's singleton.
    //
    // NOTHING VANILLA IS EVER RE-PARENTED. Mods resolve these panels by string path - FATI uses
    // InventoryGui.instance.m_inventoryRoot.Find("Crafting") plus "Crafting/RecipeList" and
    // "Crafting/Decription" - so Crafting stays a direct child of root and Info keeps its buttons. Only our
    // own CraftingExtras is mounted INTO Crafting, which is what FATI does with its own view.
    internal static class UnifiedScreen
    {
        internal const string BundleName = "vainventoryui";
        const string PrefabName = "VAInventoryScreen";

        static readonly string[] InfoTabs = { "Texts", "Skills", "Trophies", "Achievements", "PVP" };

        static AssetBundle bundle;
        static GameObject prefab;
        static GameObject instance;
        static bool loadFailed;

        // Slot -> the prefab cell its element should live in. Rebuilt whenever the panel is rebuilt, since
        // the active slot set is config driven. Empty in Legacy, which is what makes EquipmentPanel fall
        // back to its own positioning: one renderer with a placement strategy, not two renderers.
        static readonly System.Collections.Generic.Dictionary<Slots.Slot, RectTransform> cells = [];

        internal static bool Enabled => ConfigManager.Instance?.configUiLayout?.Value == "Unified";

        internal static void Reset()
        {
            if (instance != null) Object.Destroy(instance);
            instance = null;
            cells.Clear();
            CharacterPreview.Teardown();
            StatsPanel.Reset();
        }

        // The preview camera renders every frame it is enabled, so it is switched off with the screen
        // rather than left running behind a closed inventory.
        internal static void Hide() => CharacterPreview.Hide();

        /// <summary>
        /// The prefab cell a slot's element belongs in, or false in Legacy / for slots with no cell (the
        /// currency vault is never drawn, backpack storage goes to its own detached bag panel).
        /// EquipmentPanel parents into this instead of positioning by Slot.Position.
        /// </summary>
        internal static bool TryGetCell(Slots.Slot slot, out RectTransform cell)
        {
            cell = null;
            if (!Enabled || slot == null) return false;
            return cells.TryGetValue(slot, out cell) && cell != null;
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

        internal static void Apply()
        {
            if (!Enabled || loadFailed) return;
            var gui = InventoryGui.instance;
            var root = gui != null ? gui.m_inventoryRoot : null;
            if (root == null) return;

            if (!EnsurePrefab()) return;

            if (instance == null)
            {
                instance = Object.Instantiate(prefab, root);
                instance.name = PrefabName;
                instance.transform.SetAsFirstSibling();   // our chrome draws behind the vanilla panels
            }

            var ui = (RectTransform)instance.transform;
            var layout = ui.Find("Layout");
            if (layout == null) return;

            var player = root.Find("Player") as RectTransform;
            var info = root.Find("Info") as RectTransform;
            var crafting = root.Find("Crafting") as RectTransform;

            // Bind chrome BEFORE mounting anything into Crafting: binding walks our prefab root, so mounting
            // first moves the stats labels out from under it and leaves them fontless and untinted.
            BindChrome(ui, player);

            ApplyLayout(root, layout, player, info, crafting);
            ApplyInfoTabs(layout.Find("InfoTabs") as RectTransform, info as RectTransform);
            MountCraftingExtras(ui, crafting);
            BuildCellMap(ui);
            CharacterPreview.Bind(ui.Find("CharacterPanel/PreviewMount")?.GetComponent<RawImage>());

            // After MountCraftingExtras, so the view is already a child of Crafting when the tab binds it.
            StatsPanel.EnsureTab(crafting != null ? crafting.Find("CraftingExtras") as RectTransform : null);
            if (StatsPanel.Active) StatsPanel.Populate();

            // Layout groups do not rebuild themselves for anything cloned after the prefab was authored.
            foreach (var group in ui.GetComponentsInChildren<LayoutGroup>(true))
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)group.transform);
        }

        // Assigns every active slot a cell. Equipment slots fill the two rails in index order; food, ammo and
        // the tool hotbar fill their rows, whose cells are cloned from a template to match the live counts
        // (all three are config driven, so a fixed cell count would either waste or truncate).
        //
        // The tool hotbar appears here AND stays on the HUD: both read the same Slots, so the inventory copy
        // is a second view of one slot, not a second slot.
        static void BuildCellMap(RectTransform ui)
        {
            cells.Clear();

            var railLeft = ui.Find("CharacterPanel/RailLeft");
            var railRight = ui.Find("CharacterPanel/RailRight");
            if (railLeft == null || railRight == null) return;

            var equipment = Slots.AllSlots()
                .Where(s => s != null && s.IsActive && !s.IsCurrencyVaultSlot && !s.IsBackpackStorageSlot
                            && !s.IsFoodSlot && !s.IsAmmoSlot && s.Index >= Slots.InventorySizePlayer)
                .OrderBy(s => s.Index)
                .ToList();

            int perRail = railLeft.childCount;
            for (int i = 0; i < equipment.Count; i++)
            {
                var rail = i < perRail ? railLeft : railRight;
                int index = i < perRail ? i : i - perRail;
                if (index >= rail.childCount) break;   // more equipment slots than the rails hold
                cells[equipment[i]] = (RectTransform)rail.GetChild(index);
            }

            AssignRow(ui, "Tools", Slots.toolHotbarSlots.Where(s => s != null && s.IsActive).ToList());
            AssignRow(ui, "Food", Slots.GetFoodSlots().ToList());
            AssignRow(ui, "Ammo", Slots.GetAmmoSlots().ToList());
        }

        static void AssignRow(RectTransform ui, string row, System.Collections.Generic.List<Slots.Slot> slots)
        {
            var container = ui.Find("CharacterPanel/Hotbars/Row" + row + "/Cells");
            var template = container?.Find("CellTemplate");
            if (container == null || template == null) return;

            // Drop last run's clones; counts change when the player edits config or equips a bag.
            for (int i = container.childCount - 1; i >= 0; i--)
            {
                var child = container.GetChild(i);
                if (child != template) Object.Destroy(child.gameObject);
            }

            for (int i = 0; i < slots.Count; i++)
            {
                var clone = Object.Instantiate(template.gameObject, container);
                clone.name = "Cell_" + i;
                clone.SetActive(true);
                cells[slots[i]] = (RectTransform)clone.transform;
            }
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
            if (prefab == null)
            {
                loadFailed = true;
                Debug.LogError($"[VAInventory] unified layout disabled: '{PrefabName}' not in '{BundleName}'");
            }
            return prefab != null;
        }

        // m_inventoryRoot is declared Transform on InventoryGui even though it carries a RectTransform.
        static void ApplyLayout(Transform root, Transform layout, RectTransform player, RectTransform info, RectTransform crafting)
        {
            var rootRect = root as RectTransform;
            if (rootRect == null) return;

            var character = (RectTransform)((RectTransform)layout.parent).Find("CharacterPanel");

            // The canvas is NOT a fixed 1920. GuiScaler sets scaleFactor to
            // Mathf.Min(Screen.width/1920, Screen.height/1080) * the player's GUI Scale, so raising GUI Scale
            // SHRINKS the canvas in units - 1.3 leaves about 1477. Narrow the character column to fit rather
            // than let the grid's Armor/Weight badges meet Crafting's repair button.
            var characterSlot = (RectTransform)layout.Find("Slot_Character");
            var gridSlot = (RectTransform)layout.Find("Slot_Grid");
            float wanted = characterSlot.sizeDelta.x;
            float allowed = AvailableCharacterWidth(rootRect.rect.width, wanted);

            ApplySlot(characterSlot, character);
            ApplySlot((RectTransform)layout.Find("Slot_Info"), info);
            ApplySlot(gridSlot, player);
            ApplySlot((RectTransform)layout.Find("Slot_Side"), crafting);

            if (allowed < wanted - 0.5f)
            {
                float shrink = wanted - allowed;
                character.sizeDelta = new Vector2(allowed, character.sizeDelta.y);
                if (info != null) info.sizeDelta = new Vector2(allowed, info.sizeDelta.y);
                player.anchoredPosition -= new Vector2(shrink, 0f);
            }
        }

        static float AvailableCharacterWidth(float canvasWidth, float wanted)
        {
            const float margin = 40f, gutter = 40f, panel = 570f;
            // 72 = the grid's Armor/Weight overhang, 80 = Crafting's RepairSimple/RepairButton overhang.
            const float clearance = 72f + 80f + 16f;
            float available = canvasWidth - (2f * margin) - gutter - (2f * panel) - clearance;
            return Mathf.Clamp(available, 0f, wanted);
        }

        static void ApplySlot(RectTransform slot, RectTransform target)
        {
            if (slot == null || target == null) return;
            target.anchorMin = slot.anchorMin;
            target.anchorMax = slot.anchorMax;
            target.pivot = slot.pivot;
            target.anchoredPosition = slot.anchoredPosition;
            target.sizeDelta = slot.sizeDelta;
            // Scale matters: Info's buttons carry fixed 64px Icon children, so resizing a button leaves its
            // icon full size. Scaling the button is what actually shrinks the tab.
            target.localScale = slot.localScale;
        }

        // Turns vanilla's Info panel into a tab strip. Its parchment is hidden so the character panel frames
        // the row; the buttons keep their paths for any mod resolving Info/Skills and friends by string.
        static void ApplyInfoTabs(RectTransform infoTabs, RectTransform info)
        {
            if (infoTabs == null || info == null) return;

            foreach (var chrome in new[] { "Bkg", "Darken", "selected_frame" })
                info.Find(chrome)?.gameObject.SetActive(false);

            foreach (var name in InfoTabs)
                ApplySlot(infoTabs.Find("Tab_" + name) as RectTransform, info.Find(name) as RectTransform);

            ApplySlot(infoTabs.Find("Tab_TitlePanel") as RectTransform, info.Find("TitlePanel") as RectTransform);

            // Vanilla ships this braid inactive; reuse it as the divider so the separator is vanilla art.
            var divider = info.Find("BraidLineHorisontalMedium");
            if (divider != null)
            {
                divider.gameObject.SetActive(true);
                ApplySlot(infoTabs.Find("Tab_Divider") as RectTransform, (RectTransform)divider);
            }

            // TitlePanel's flourishes are sized for vanilla's 570 bar and spill out of the column.
            foreach (var flourish in new[] { "BraidLineHorisontalMedium (1)", "BraidLineHorisontalMedium (2)" })
                info.Find("TitlePanel/" + flourish)?.gameObject.SetActive(false);
        }

        static void MountCraftingExtras(RectTransform ui, RectTransform crafting)
        {
            var extras = ui.Find("CraftingExtras") as RectTransform;
            if (extras == null || crafting == null || extras.parent == crafting) return;

            extras.SetParent(crafting, false);
            extras.anchorMin = Vector2.zero;
            extras.anchorMax = Vector2.one;
            extras.offsetMin = new Vector2(10f, 10f);
            extras.offsetMax = new Vector2(-10f, -70f);
        }

        // Sprites, tints and fonts come from the LIVE vanilla UI rather than the bundle, which is why the
        // bundle is a few KB rather than dragging vanilla's atlases and a TMP font asset along with it.
        static void BindChrome(RectTransform ui, RectTransform player)
        {
            if (player == null) return;

            var panelSprite = player.parent.Find("Info/Bkg")?.GetComponent<Image>()
                              ?? player.Find("Bkg")?.GetComponent<Image>();
            var panelBkg = ui.Find("CharacterPanel/Bkg")?.GetComponent<Image>();
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
            var character = ui.Find("CharacterPanel");
            if (elementImage != null && character != null)
            {
                foreach (var image in character.GetComponentsInChildren<Image>(true))
                {
                    if (image == panelBkg) continue;
                    image.sprite = elementImage.sprite;
                    image.type = Image.Type.Sliced;
                    image.color = elementTint != null ? elementTint.colors.normalColor : elementImage.color;
                }
            }

            var font = player.root.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.font != null);
            if (font == null) return;
            foreach (var text in ui.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                text.font = font.font;
                text.fontSharedMaterial = font.fontSharedMaterial;
            }
        }
    }
}
