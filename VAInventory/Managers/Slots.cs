using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent
{
    public static class Slots
    {
        // Slot ID constants
        public const string helmetSlotID = "Helmet";
        public const string legsSlotID = "Legs";
        public const string utilitySlotID = "Utility";
        public const string chestSlotID = "Chest";
        public const string shoulderSlotID = "Shoulder";
        public const string extraUtilitySlotID = "ExtraUtility";
        public const string quickSlotID = "Quick";
        public const string foodSlotID = "Food";
        public const string ammoSlotID = "Ammo";
        public const string miscSlotID = "Misc";
        public const string customSlotID = "Custom";
        public const string emptySlotID = "Empty";
        public const string trinketSlotID = "Trinket";
        // Row-4 slots (parallel to row 3 / Legs+Trinket+food+ammo).
        //
        // Layout in the equipment panel:
        //   Col 0: Legs       Col 1: Trinket   Col 2: (Food)   Col 3: (Ammo)
        //   Col 0: CoinPurse  Col 1: Backpack  Col 2: Ring     Col 3: TeleportBypass
        //
        // Ring / TeleportBypass occupy the same columns as Food / Ammo
        // and are gated on the same showFood / showAmmo configs so the
        // grid stays rectangular. Hiding food/ammo also hides those.
        public const string coinPurseSlotID       = "CoinPurse";
        public const string backpackSlotID        = "Backpack";
        public const string ringSlotID            = "Ring";
        // The old Ring cell is the LEFT ring (same grid position, so a saved ring stays put);
        // the right ring and the necklace take the two always-empty Torch-column cells (rows 3-4), which never held an item.
        public const string ringRightSlotID       = "RingRight";
        public const string necklaceSlotID        = "Necklace";
        // The contract with FiresRPGCrafting, read by reflection.
        public const string JewelryNecklaceSlotID  = necklaceSlotID;
        public const string JewelryRingLeftSlotID  = ringSlotID;
        public const string JewelryRingRightSlotID = ringRightSlotID;
        public const string teleportBypassSlotID  = "TeleportBypass";
        // Pocket pet (FiresNPCs' PetApi, soft-detected by PetBridge like the Backpack slot). It takes the Torch column's
        // second cell, which was an always-inactive Empty cell, so no existing slot's index or grid position moves.
        public const string petSlotID             = "Pet";

        // FiresRPGClasses' two skill bars each take their weapon and off hand from these slots,
        // so the bars no longer follow whatever is equipped. One column, present only when FiresRPGClasses is
        // loaded: Front bar / Front offhand / Back bar / Back offhand. The slots HOLD the items (they stay in the
        // player inventory at these grid positions); FiresRPGClasses equips the active bar's pair and reads the
        // slots through the public API below.
        public const string frontWeaponSlotID  = "FrontWeapon";
        public const string frontOffhandSlotID = "FrontOffhand";
        public const string backWeaponSlotID   = "BackWeapon";
        public const string backOffhandSlotID  = "BackOffhand";
        // Fire, 2026-09-30: the equipment grid's one free cell holds a torch or lantern, toggled by a double-tapped hotkey.
        public const string torchSlotID = "Torch";
        private const string ClassesPluginGuid = "com.Fire.FiresRPGClasses";
        // Prefix for the dynamic backpack-storage block. The block is appended
        // when a backpack is equipped (rows × cols extra slots) and removed
        // on unequip; serialization round-trips through the backpack item's
        // customData via BackpackStorageSync.
        public const string backpackStorageSlotPrefix = "BackpackStorage";

        // Coin pouch multi-currency: the visible CoinPurse slot shows the DISPLAYED currency;
        // the other configured currencies live in these reserved HIDDEN vault cells (one real
        // stack each). Placed BEFORE the dynamic backpack blocks so equipping bags never shifts
        // their grid positions and can't strand currency. Never rendered (IsCurrencyVaultSlot
        // is filtered out of the equipment panel). Fixed reserve → stable layout regardless of
        // how many currencies are configured (up to CurrencyVaultCount + 1 total).
        public const string currencyVaultSlotPrefix = "CurrencyVault";
        public const int CurrencyVaultCount = 8;

        public static readonly Vector2i emptyPosition = new(-1, -1);
        public const string VanillaOrder = "Helmet,Chest,Legs,Shoulder,Utility,Trinket";
        private static readonly HashSet<string> vanillaSlots = [.. VanillaOrder.Split(',')];

        // Start with a reasonable default, but we'll resize dynamically
        public static Slot[] slots = new Slot[64];
        internal static bool HasSlots => slots != null && slots.Any(s => s != null);   // Relayout carries items only across a REbuild

        private static readonly Dictionary<Vector2i, ItemDrop.ItemData> cachedItems = [];

        public const int VanillaInventoryHeight = 4;
        public const int VanillaInventoryWidth = 8;

        // Y-coord layout below the player rows (matches the
        // ExtraSlots-aligned layout in EXTRASLOTS_ALIGNMENT_PLAN.md).
        // All offsets are RELATIVE to InventoryHeightPlayer so the
        // panel slides down automatically when the player rows grow,
        // whether through configExtraRows or rows bought from a trader.
        // Tool/Ammo/Food hotbars each get one row; equipment panel +
        // misc + backpack blocks follow.
        public const int ToolHotbarRowOffset = 0;  // 1 row, 8 cells
        public const int AmmoHotbarRowOffset = 1;  // reserved for Phase D (currently equipment-panel column)
        public const int FoodHotbarRowOffset = 2;  // reserved for Phase D (currently equipment-panel column)
        public const int EquipmentPanelRowOffset = 3;  // 4 rows: helmet/shoulder, chest/utility, legs/trinket, coin/backpack
        public const int EquipmentPanelRowCount = 4;
        public const int MiscBlockRowOffset = EquipmentPanelRowOffset + EquipmentPanelRowCount; // 7

        private static int EquipmentPanelStartY => InventoryHeightPlayer + EquipmentPanelRowOffset;

        public static Player CurrentPlayer => Player.m_localPlayer;
        public static Inventory PlayerInventory => CurrentPlayer?.GetInventory();

        public static int ExtraRowsPlayer => ConfigManager.Instance.configExtraRows.Value;
        public static int VanillaPlayerRows { get; internal set; } = VanillaInventoryHeight;
        public static int InventoryWidth => PlayerInventory?.GetWidth() ?? VanillaInventoryWidth;
        public static int InventoryHeightPlayer => VanillaPlayerRows + ExtraRowsPlayer;

        // Covers any group drawn in the player's grid so its rows are not treated as out-of-bounds.
        // Restricted to those groups rather than every slot on purpose: widening it to AllSlots()
        // would be the more obviously correct rule, but it can only raise the height, and raising it
        // is a change to the value DropInvalidItems bounds-checks against. See SLOT_REGISTRY notes.
        public static int InventoryHeightFull
        {
            get
            {
                int baseFull = InventoryHeightPlayer + GetTargetInventoryHeight(slots.Length, InventoryWidth);
                int maxVisibleY = slotGroups
                    .Where(g => g.VisibleInMainGrid)
                    .SelectMany(g => g.Members)
                    .Where(s => s != null)
                    .Select(s => s.GridPosition.y)
                    .DefaultIfEmpty(InventoryHeightPlayer - 1)
                    .Max();
                // height is number of rows, so maxY +1
                return Math.Max(baseFull, maxVisibleY + 1);
            }
        }

        public static int InventorySizePlayer => InventoryHeightPlayer * InventoryWidth;
        public static int InventorySizeFull => InventoryHeightFull * InventoryWidth;
        public static int InventorySizeActive => InventorySizePlayer + AllSlots().Count(slot => slot != null && slot.IsActive);

        public static int GetTargetInventoryHeight(int inventorySize, int inventoryWidth) =>
            (inventorySize + inventoryWidth - 1) / inventoryWidth;

        public static int GetEquipmentSlotsCount() =>
            AllSlots().Count(slot => slot != null && slot.IsEquipmentSlot && slot.IsActive);

        public static int GetQuickSlotsCount() =>
            AllSlots().Count(slot => slot != null && slot.IsQuickSlot && slot.IsActive);

        public static Slot[] GetEquipmentSlots(bool onlyActive = true) =>
            [.. AllSlots().Where(slot => slot != null && slot.IsEquipmentSlot && (!onlyActive || slot.IsActive)).OrderBy(slot => slot.EquipmentIndex)];

        public static Slot[] GetQuickSlots() =>
            [.. AllSlots().Where(s => s != null && s.IsQuickSlot && s.IsActive)];

        public static Slot[] GetFoodSlots() =>
            [.. AllSlots().Where(s => s != null && s.IsFoodSlot && s.IsActive)];

        public static Slot[] GetExtraUtilitySlots() =>
            [.. AllSlots().Where(s => s != null && s.ID.StartsWith("ExtraUtility") && s.IsActive)];

        public static Slot[] GetAmmoSlots() =>
            [.. AllSlots().Where(s => s != null && s.IsAmmoSlot && s.IsActive)];

        // Pouches (UI only, Unified layout): open one the real way for tests. Group "tools" / "ammo" / "food", any case.
        // For tests: "tools" / "ammo" / "food", and from 1.0.69 "coin" (the coin purse's popup, above the purse).
        public static string OpenPouchGroup => CurrencyPouchPopup.IsOpen ? "coin" : PouchPopup.OpenGroup;
        public static void OpenPouch(string group)
        {
            if (!string.Equals(group, "coin", StringComparison.OrdinalIgnoreCase)) { PouchPopup.Open(group, false); return; }
            var purse = AllSlots().FirstOrDefault(s => s != null && s.IsActive && s.IsCoinPurseSlot);
            CurrencyPouchPopup.Open(purse != null && UnifiedScreen.TryGetCell(purse, out var cell) ? cell : null);
        }
        public static void ClosePouches()
        {
            PouchPopup.Close();
            CurrencyPouchPopup.Close();
        }

        public static Slot[] GetMiscSlots() =>
            [.. AllSlots().Where(s => s != null && s.IsMiscSlot && s.IsActive)];

        // Everything past the player rows is slot territory, plus any registered group's cell that
        // sits inside them.
        public static bool IsGridPositionASlot(Vector2i gridPos)
        {
            if (gridPos.y >= InventoryHeightPlayer)
                return true;
            return AllSlots().Any(s => s.GridPosition == gridPos);
        }

        public static bool IsItemInSlot(ItemDrop.ItemData item) =>
            item != null && IsGridPositionASlot(item.m_gridPos);

        public static bool IsItemInEquipmentSlot(ItemDrop.ItemData item) =>
            item != null && GetItemSlot(item)?.IsEquipmentSlot == true;

        // Every collection of slots is registered here exactly once, and nothing else may enumerate
        // one directly. A group that some code paths know about and others do not is precisely how
        // Inventory.StackAll came to move items out of quick/food/ammo slots: toolHotbarSlots lived
        // outside the slots array, so each path had to remember to consult it, and the capacity
        // paths did not.
        //
        // VisibleInMainGrid marks a group whose cells are drawn in the player's own grid even though
        // they sit past the player rows, so IsGridPositionHidden must not hide them.
        internal sealed class SlotGroup(string id, Func<IEnumerable<Slot>> members, bool visibleInMainGrid, bool countsAsGenericCapacity)
        {
            internal string ID { get; } = id;
            internal bool VisibleInMainGrid { get; } = visibleInMainGrid;
            internal bool CountsAsGenericCapacity { get; } = countsAsGenericCapacity;
            internal IEnumerable<Slot> Members => members() ?? [];
        }

        private static readonly List<SlotGroup> slotGroups =
        [
            new SlotGroup("Main", () => slots, visibleInMainGrid: false, countsAsGenericCapacity: true),
            new SlotGroup("ToolHotbar", () => toolHotbarSlots, visibleInMainGrid: true, countsAsGenericCapacity: false),
        ];

        // The one call a new slot group makes. Registering is what puts a group in front of every
        // capacity, validation and rendering path at once. Both flags are required rather than
        // defaulted, so a new group has to STATE whether it is free space instead of silently
        // inheriting an answer.
        internal static void RegisterSlotGroup(string id, Func<IEnumerable<Slot>> members, bool visibleInMainGrid, bool countsAsGenericCapacity)
        {
            if (string.IsNullOrEmpty(id) || members == null) return;
            if (slotGroups.Any(g => g.ID == id)) return;
            slotGroups.Add(new SlotGroup(id, members, visibleInMainGrid, countsAsGenericCapacity));
        }

        // The item-agnostic capacity view: what GetEmptySlots / HaveEmptySlot may report as generic
        // free space. Item-AWARE capacity goes through TryFindFreeSlotForItem, which sees every
        // group and honours each slot's ItemFits.
        public static IEnumerable<Slot> CapacitySlots() =>
            slotGroups.Where(g => g.CountsAsGenericCapacity)
                      .SelectMany(g => g.Members)
                      .Where(s => s != null);

        private static bool IsInVisibleGroup(Vector2i pos) =>
            slotGroups.Any(g => g.VisibleInMainGrid && g.Members.Any(s => s != null && s.GridPosition == pos));

        public static IEnumerable<Slot> AllSlots()
        {
            foreach (var group in slotGroups)
            {
                foreach (var s in group.Members)
                {
                    if (s != null) yield return s;
                }
            }
        }

        // Registration order decides ties, so the main array still wins over the tool hotbar.
        public static Slot GetSlotInGrid(Vector2i pos)
        {
            if (!IsGridPositionASlot(pos)) return null;
            return AllSlots().FirstOrDefault(slot => slot.GridPosition == pos);
        }

        public static Slot GetItemSlot(ItemDrop.ItemData item)
        {
            if (!IsItemInSlot(item) || PlayerInventory == null || !PlayerInventory.ContainsItem(item)) return null;
            return AllSlots().FirstOrDefault(slot => slot != null && slot.GridPosition == item.m_gridPos);
        }

        public static bool TryFindFreeSlotForItem(ItemDrop.ItemData item, out Slot slot)
        {
            slot = null;
            if (item == null) return false;

            if (TryGetSavedPlayerSlot(item, out var saved) && saved != null && saved.IsActive && saved.ItemFits(item) && (saved.IsFree || item == saved.Item))
            {
                slot = saved;
                return true;
            }

            // The Bypass slot takes any item, so a
            // second bag picked up while the Backpack slot was full landed there. Bypass is a slot the player fills by hand
            // (the smuggler's pocket); automatic placement never uses it.
            slot = AllSlots().FirstOrDefault(s => s != null && s.IsActive && s.IsFree && !s.IsTeleportBypassSlot && s.ItemFits(item));
            if (slot == null && item.m_dropPrefab != null && AllSlots().Any(s => s != null && s.IsActive && s.IsFree && s.IsTeleportBypassSlot))
                EquipmentPanel.PanelLog?.LogDebug($"slots: {item.m_dropPrefab.name} not auto-placed in the Bypass slot (it is filled by hand only)");
            return slot != null;
        }

        public static bool TryFindFreeEquipmentSlotForItem(ItemDrop.ItemData item, out Slot slot)
        {
            slot = null;
            if (item == null) return false;

            if (TryGetSavedPlayerSlot(item, out var saved) && saved != null && saved.IsActive && saved.IsEquipmentSlot && saved.ItemFits(item) && (saved.IsFree || item == saved.Item))
            {
                slot = saved;
                return true;
            }

            slot = AllSlots().FirstOrDefault(s => s != null && s.IsEquipmentSlot && s.IsFree && s.ItemFits(item));
            return slot != null;
        }

        public static bool TryFindFirstUnequippedSlotForItem(ItemDrop.ItemData item, out Slot slot)
        {
            slot = null;
            if (item == null) return false;

            if (TryGetSavedPlayerSlot(item, out var saved) && saved != null && saved.IsActive && saved.IsEquipmentSlot && saved.ItemFits(item) && saved.Item != null && !CurrentPlayer.IsItemEquiped(saved.Item))
            {
                slot = saved;
                return true;
            }

            slot = GetEquipmentSlots().FirstOrDefault(s => s != null && s.ItemFits(item) && s.Item != null && !CurrentPlayer.IsItemEquiped(s.Item));
            return slot != null;
        }

        public static bool TryGetSavedPlayerSlot(ItemDrop.ItemData item, out Slot slot)
        {
            slot = null;
            if (item == null) return false;

            if (!item.m_customData.TryGetValue("ExtraSlotsEquippedBy", out var playerIdStr) ||
                !item.m_customData.TryGetValue("ExtraSlotsEquippedSlot", out var slotId) ||
                !long.TryParse(playerIdStr, out var playerId) ||
                playerId != Game.instance.GetPlayerProfile().GetPlayerID())
                return false;

            slot = AllSlots().FirstOrDefault(s => s != null && s.ID == slotId);
            return slot != null;
        }

        public static bool TryMakeFreeSpaceInPlayerInventory(bool tryRegular, out Vector2i pos)
        {
            pos = emptyPosition;
            var itemsToMove = new List<ItemDrop.ItemData>();

            if (tryRegular)
            {
                for (int y = InventoryHeightPlayer - 1; y >= 0; y--)
                {
                    for (int x = InventoryWidth - 1; x >= 0; x--)
                    {
                        var item = PlayerInventory.GetItemAt(x, y);
                        if (item == null)
                        {
                            pos = new Vector2i(x, y);
                            return true;
                        }
                        itemsToMove.Add(item);
                    }
                }
            }
            else
            {
                itemsToMove.AddRange(PlayerInventory.GetAllItems()
                    .Where(i => i.m_gridPos.y < InventoryHeightPlayer)
                    .OrderByDescending(i => i.m_gridPos.y)
                    .ThenByDescending(i => i.m_gridPos.x));
            }

            ClearCachedItems();

            foreach (var item in itemsToMove)
            {
                if (TryFindFreeEquipmentSlotForItem(item, out var freeSlot))
                {
                    pos = item.m_gridPos;
                    item.m_gridPos = freeSlot.GridPosition;
                    return true;
                }

                if (TryFindFreeNonBagSlotForItem(item, out freeSlot))
                {
                    pos = item.m_gridPos;
                    item.m_gridPos = freeSlot.GridPosition;
                    return true;
                }
            }

            return false;
        }

        // Making room never moves an item into a bag's storage block. A bag block may be one that is not
        // restored yet (equip/unequip, spawn), and an item parked there was captured into that bag's blob while a live copy stayed:
        // duplicates. Typed slots (equipment, misc, hotbars) still take what fits; a bag only ever gets what the player puts in it.
        private static bool TryFindFreeNonBagSlotForItem(ItemDrop.ItemData item, out Slot slot)
        {
            slot = AllSlots().FirstOrDefault(s => s != null && s.IsActive && s.IsFree && !s.IsTeleportBypassSlot && s.ItemFits(item)
                                                  && string.IsNullOrEmpty(GetBackpackPrefabFromSlot(s)));
            return slot != null;
        }

        public static bool IsEquipmentSlotItem(ItemDrop.ItemData item) =>
            item != null && (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Helmet ||
                            item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Chest ||
                            item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Legs ||
                            item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shoulder ||
                            item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Utility ||
                            item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trinket);

        public static void PruneLastEquippedSlotFromItem(ItemDrop.ItemData item)
        {
            if (item == null) return;
            item.m_customData.Remove("ExtraSlotsEquippedBy");
            item.m_customData.Remove("ExtraSlotsEquippedSlot");
        }

        public static bool IsValidPlayer(Character character) =>
            character != null && character.IsPlayer() && character == Player.m_localPlayer;

        // Item type checks
        public static bool IsHelmetSlotItem(ItemDrop.ItemData item) => item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Helmet;
        public static bool IsChestSlotItem(ItemDrop.ItemData item) => item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Chest;
        public static bool IsLegsSlotItem(ItemDrop.ItemData item) => item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Legs;
        // Shoulder slot is for capes — NOT for VABackpacks-registered
        // backpacks. Backpacks share m_itemType=Shoulder with capes,
        // and VAInventory has a dedicated Backpack slot one row down
        // (row 4, col 1) that should receive them. If the Shoulder
        // validator accepted backpacks the auto-move loop in
        // Humanoid_EquipItem_AutoMoveToSlot would put them there
        // first (Shoulder is iterated before Backpack), and then a
        // subsequently-equipped cape would have nowhere to go —
        // VAInventory logs "No valid equipment slot found for $item_cape_*"
        // and the cape gets unequipped on the spot.
        //
        // Filtering backpacks out here lets the auto-move skip the
        // Shoulder slot for them and land on the Backpack slot, while
        // capes (which BackpackBridge.IsBackpack rejects) still take
        // the Shoulder slot as before.
        public static bool IsShoulderSlotItem(ItemDrop.ItemData item)
            => item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shoulder
               && !BackpackBridge.IsBackpack(item);
        public static bool IsUtilitySlotItem(ItemDrop.ItemData item) => item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Utility;
        public static bool IsTrinketSlotItem(ItemDrop.ItemData item) => item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trinket;
        public static bool IsCapeSlotItem(ItemDrop.ItemData item) => item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shoulder;
        public static bool IsAmmoSlotItem(ItemDrop.ItemData item) => item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo;

        // A bar's main hand: anything FiresRPGClasses can drive a bar with (a torch counts, as in its own bar rules).
        public static bool IsClassBarWeaponItem(ItemDrop.ItemData item)
        {
            switch (item?.m_shared?.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Torch:
                    return true;
                default:
                    return false;
            }
        }

        // A bar's off hand: what vanilla puts in the left hand next to a one-handed weapon.
        public static bool IsClassBarOffhandItem(ItemDrop.ItemData item) =>
            item?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Shield
            || item?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Torch;

        // BepInEx fills PluginInfos as it loads each plugin, and FiresRPGClasses loads AFTER VAInventory (its soft
        // dependency), so this is false during VAInventory's Awake. Ascend.Start re-runs InitializeSlots once every
        // plugin is loaded (1.0.47 on the rig: "class bars False" and no bar slots).
        internal static bool ClassBarsAvailable() =>
            BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(ClassesPluginGuid);

        // The Torch slot: vanilla torches, and lanterns (by name, so modded lanterns that are not Torch-typed fit too).
        public static bool IsTorchSlotItem(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Torch) return true;
            string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : string.Empty;
            return (prefab + " " + item.m_shared.m_name).IndexOf("lantern", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>For bots and other mods (by reflection): the torch or lantern in the Torch slot, or null.</summary>
        public static ItemDrop.ItemData GetTorchSlotItem() =>
            AllSlots().FirstOrDefault(s => s != null && s.IsTorchSlot)?.Item;

        /// <summary>For FiresNPCs (by reflection): the Pet slot is shown (PetApi was found).</summary>
        public static bool PetSlotActive => AllSlots().Any(s => s != null && s.IsPetSlot && s.IsActive);

        /// <summary>For FiresNPCs (by reflection): the pet item in the Pet slot, or null when empty or inactive.</summary>
        public static ItemDrop.ItemData GetPetSlotItem()
        {
            var slot = AllSlots().FirstOrDefault(s => s != null && s.IsPetSlot);
            return slot != null && slot.IsActive ? slot.Item : null;
        }

        /// <summary>
        /// For bots and other mods (by reflection): moves a torch or lantern already in the player's inventory into the
        /// Torch slot, swapping out whatever was there. False when the item does not fit or is not in the inventory.
        /// </summary>
        public static bool TryPutInTorchSlot(ItemDrop.ItemData item) => TryPutInSlot(s => s.IsTorchSlot, item);

        /// <summary>
        /// For FiresNPCs (by reflection, 10-07; Fire: "using the pet item from the inventory moves it into the Pet
        /// slot AND equips it"): moves a pet item from the player's inventory into the Pet slot; an item in the Pet slot is
        /// out. A pet already there goes to this item's old cell. Reports the change through PetApi.OnPetSlotChanged, as a
        /// drop on the slot does. False when it is not a pet, the slot is not shown, the item is not in the inventory, it
        /// is already in the slot, or there is no room for the swap.
        /// </summary>
        public static bool TryEquipPet(ItemDrop.ItemData item)
        {
            var player = Player.m_localPlayer;
            if (player == null || item == null || !PetSlotActive || !PetBridge.IsPet(item)) return false;
            if (GetPetSlotItem() == item) return false;
            if (!TryPutInSlot(s => s.IsPetSlot && s.IsActive, item)) return false;
            PetBridge.NotifyChanged(player, item);
            EquipmentPanel.MarkDirty();
            EquipmentPanel.UpdatePanel();
            return true;
        }

        /// <summary>
        /// For the hotkey, bots and other mods: equips the Torch slot's item when it is not in hand, puts it away when it
        /// is. Vanilla's EquipItem / UnequipItem decide the hand. False when the slot is empty or there is no player.
        /// </summary>
        public static bool ToggleTorchSlot()
        {
            var player = Player.m_localPlayer;
            var item = GetTorchSlotItem();
            if (player == null || item == null || player.IsDead()) return false;
            if (player.IsItemEquiped(item)) player.UnequipItem(item);
            else player.EquipItem(item);
            return true;
        }

        private static string ClassBarSlotName(string slotID) => slotID switch
        {
            frontWeaponSlotID => "Front bar",
            frontOffhandSlotID => "Front offhand",
            backWeaponSlotID => "Back bar",
            backOffhandSlotID => "Back offhand",
            ringSlotID => "Left ring",
            ringRightSlotID => "Right ring",
            _ => slotID
        };

        /// <summary>For FiresRPGClasses (by reflection): the class bar slots exist (FiresRPGClasses is loaded).</summary>
        public static bool ClassBarSlotsActive => AllSlots().Any(s => s != null && s.IsClassBarSlot && s.IsActive);

        /// <summary>For FiresRPGClasses (by reflection): the item in a class bar slot, or null when it is empty.</summary>
        public static ItemDrop.ItemData GetClassBarItem(string slotId) =>
            AllSlots().FirstOrDefault(s => s != null && s.IsClassBarSlot && s.ID == slotId)?.Item;

        /// <summary>
        /// For FiresRPGClasses and its test tools (by reflection): moves an item already in the player's inventory into a
        /// class bar slot. An item already there takes the moved item's old place, or the first free main-grid cell when
        /// it cannot go there. False when the item does not fit the slot or is not in the player's inventory.
        /// </summary>
        public static bool TryPutInClassBarSlot(string slotId, ItemDrop.ItemData item) =>
            TryPutInSlot(s => s.IsClassBarSlot && s.ID == slotId, item);

        private static bool TryPutInSlot(Func<Slot, bool> which, ItemDrop.ItemData item)
        {
            var inv = PlayerInventory;
            var slot = AllSlots().FirstOrDefault(s => s != null && which(s));
            if (inv == null || slot == null || item == null || !slot.ItemFits(item) || !inv.ContainsItem(item)) return false;

            var occupant = slot.Item;
            if (occupant == item) return true;

            Vector2i oldPos = item.m_gridPos;
            if (occupant != null)
            {
                var oldSlot = GetSlotInGrid(oldPos);
                if (oldSlot == null || oldSlot.ItemFits(occupant)) occupant.m_gridPos = oldPos;
                else if (TryMakeFreeSpaceInPlayerInventory(true, out Vector2i free)) occupant.m_gridPos = free;
                else return false;
            }
            item.m_gridPos = slot.GridPosition;
            ClearCachedItems();
            inv.m_onChanged?.Invoke();
            return true;
        }
        public static bool IsFoodSlotItem(ItemDrop.ItemData item) => item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable && item.m_shared.m_food > 0;
        public static bool IsToolHotbarSlotItem(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;

            var itemType = item.m_shared.m_itemType;
            if (itemType == ItemDrop.ItemData.ItemType.Tool || itemType == ItemDrop.ItemData.ItemType.Torch)
                return true;

            string prefabName = item.m_dropPrefab != null ? item.m_dropPrefab.name : string.Empty;
            string sharedName = item.m_shared.m_name ?? string.Empty;
            string combinedName = (prefabName + " " + sharedName).ToLowerInvariant();

            return combinedName.Contains("pickaxe")
                || combinedName.Contains("fishingrod")
                || combinedName.Contains("fishing_rod")
                || combinedName.Contains("fishing rod")
                || combinedName.Contains("lantern")
                || combinedName.Contains("torch")
                || combinedName.Contains("axe");
        }

        /// <summary>
        /// True only for items VABackpacks has registered as a backpack
        /// prefab. When VABackpacks isn't loaded, BackpackBridge.IsBackpack
        /// returns false → the slot rejects everything (and is hidden via
        /// the slot's IsActive predicate).
        /// </summary>
        public static bool IsBackpackSlotItem(ItemDrop.ItemData item)
            => BackpackBridge.IsBackpack(item);

        /// <summary>
        /// Extract the bag prefab name from a backpack-storage slot ID.
        /// Slot ID format: `BackpackStorage_<prefab>_<col>_<row>`.
        /// Returns null if the slot isn't a backpack-storage slot or the
        /// ID doesn't match the expected layout.
        /// </summary>
        public static string GetBackpackPrefabFromSlot(Slot slot)
        {
            if (slot == null) return null;
            string id = slot.ID;
            if (string.IsNullOrEmpty(id)
                || !id.StartsWith(backpackStorageSlotPrefix + "_"))
                return null;

            // Strip "BackpackStorage_" prefix, then drop the trailing
            // "_<col>_<row>" — col/row are integers, prefab is whatever
            // remains in between (may itself contain underscores like
            // "DayBag_va", which is why we trim from the right).
            string body = id.Substring(backpackStorageSlotPrefix.Length + 1);
            int last = body.LastIndexOf('_');
            if (last < 0) return null;
            int penultimate = body.LastIndexOf('_', last - 1);
            if (penultimate < 0) return null;
            return body.Substring(0, penultimate);
        }

        /// <summary>
        /// True only for the vanilla "Coins" prefab. Matched by drop-prefab
        /// name so this works regardless of localization.
        /// </summary>
        // Config-driven currency identity. An item counts as pouch currency when its prefab
        // name matches a configured currency, OR (for trade/split/chest clones that carry a
        // null m_dropPrefab) when its shared name matches a configured currency's shared name.
        // The CoinPurse slot delegates here, so it accepts every configured currency.
        private static Dictionary<string, string> _currencySharedNames;
        private static string _currencySharedNamesRaw;

        private static Dictionary<string, string> CurrencySharedNames()
        {
            var prefabs = ConfigManager.Instance.GetCurrencyPrefabs();
            string raw = string.Join(",", prefabs);
            if (_currencySharedNames != null && raw == _currencySharedNamesRaw)
                return _currencySharedNames;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in prefabs)
            {
                try
                {
                    GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(name) : null;
                    if (prefab == null && ZNetScene.instance != null) prefab = ZNetScene.instance.GetPrefab(name);
                    var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                    var shared = drop?.m_itemData?.m_shared?.m_name;
                    if (!string.IsNullOrEmpty(shared)) map[name] = shared;
                }
                catch { }
            }
            // Only cache once ObjectDB has actually resolved something (it may be null early).
            if (map.Count > 0) { _currencySharedNames = map; _currencySharedNamesRaw = raw; }
            return map;
        }

        // Canonical currency prefab name for an item, or null if it isn't configured currency.
        public static string CurrencyKey(ItemDrop.ItemData item)
        {
            if (item == null) return null;
            var prefabs = ConfigManager.Instance.GetCurrencyPrefabs();
            if (item.m_dropPrefab != null)
            {
                foreach (var name in prefabs)
                    if (string.Equals(item.m_dropPrefab.name, name, StringComparison.OrdinalIgnoreCase))
                        return name;
            }
            var shared = item.m_shared?.m_name;
            if (!string.IsNullOrEmpty(shared))
            {
                foreach (var kv in CurrencySharedNames())
                    if (string.Equals(kv.Value, shared, StringComparison.Ordinal))
                        return kv.Key;
            }
            return null;
        }

        public static bool IsCurrencyItem(ItemDrop.ItemData item) => CurrencyKey(item) != null;

        // Back-compat alias — the CoinPurse slot validator and callers still use this name.
        public static bool IsCoinSlotItem(ItemDrop.ItemData item) => IsCurrencyItem(item);

        // The visible pouch slot (shows the displayed currency) and the hidden vault cells
        // (hold the other currencies). Used by CurrencyPurse routing + the cycle/popup UI.
        public static Slot GetCoinPurseSlot() =>
            AllSlots().FirstOrDefault(s => s != null && s.IsCoinPurseSlot && s.IsActive);

        public static Slot[] GetCurrencyVaultSlots() =>
            [.. AllSlots().Where(s => s != null && s.IsCurrencyVaultSlot).OrderBy(s => s.Index)];

        /// <summary>
        /// True for items that look like Balrond's Arsenal rings (or any
        /// ring-named accessory). Heuristic match by prefab name fragment
        /// so we don't have to take a hard dep on Balrond's mod. Admins
        /// can extend the list via Slots.RingItemMatcher (init-time hook).
        /// </summary>
        public static bool IsRingSlotItem(ItemDrop.ItemData item)
        {
            if (item?.m_dropPrefab == null) return false;
            string n = item.m_dropPrefab.name?.ToLowerInvariant() ?? string.Empty;
            if (n.StartsWith("floring_") || n.StartsWith("frcring_")) return true;   // 1.0.110 rings ([fgn] contract); 1.0.135: FLO names, FRC names kept for old items
            if (RingItemMatcher != null) return RingItemMatcher(item);
            // Common naming conventions: "Ring*", "*_ring*", "ring_*".
            return n.StartsWith("ring") || n.Contains("_ring") || n.Contains("ringof");
        }

        /// <summary>
        /// Optional override for ring detection — set this once at init
        /// time to wire in a more specific matcher (e.g. "is the item from
        /// Balrond's Arsenal ring catalog"). Null falls back to the
        /// name-fragment heuristic above.
        /// </summary>
        public static System.Func<ItemDrop.ItemData, bool> RingItemMatcher;

        /// <summary>1.0.110: the Necklace cell takes FiresRPGCrafting's necklaces ("FrcNecklace_*").</summary>
        public static bool IsNecklaceSlotItem(ItemDrop.ItemData item) =>
            item?.m_dropPrefab != null && ((item.m_dropPrefab.name ?? "").StartsWith("FloNecklace_", StringComparison.OrdinalIgnoreCase) || (item.m_dropPrefab.name ?? "").StartsWith("FrcNecklace_", StringComparison.OrdinalIgnoreCase));   // 1.0.135: FLO + old FRC

        // ── Jewelry API (1.0.110, for FiresRPGCrafting by reflection) ─────────────────────────────────────────────────
        /// <summary>[necklace, left ring, right ring] for the local player; null = empty. Any other player: all null
        /// (only the local player's VAInventory slots are loaded).</summary>
        public static ItemDrop.ItemData[] GetJewelry(Player p)
        {
            var result = new ItemDrop.ItemData[3];
            if (p == null || p != Player.m_localPlayer) return result;
            result[0] = JewelryItem(necklaceSlotID);
            result[1] = JewelryItem(ringSlotID);
            result[2] = JewelryItem(ringRightSlotID);
            return result;
        }

        /// <summary>The player-inventory grid position of a jewelry cell ("Necklace" / "Ring" / "RingRight"), for tests that equip
        /// through it with vanilla Inventory.MoveItemToThis; (-1,-1) when the cell is unknown or inactive.</summary>
        public static Vector2i GetJewelrySlotPosition(string slotId)
        {
            if (slotId != necklaceSlotID && slotId != ringSlotID && slotId != ringRightSlotID) return emptyPosition;
            var slot = AllSlots().FirstOrDefault(s => s != null && s.ID == slotId && s.IsActive);
            return slot != null ? slot.GridPosition : emptyPosition;
        }

        /// <summary>Raised when one of the three jewelry cells changes (put, take, swap, load); at most once a frame.</summary>
        public static event Action<Player> JewelryChanged;

        static ItemDrop.ItemData JewelryItem(string id)
        {
            var slot = AllSlots().FirstOrDefault(s => s != null && s.ID == id && s.IsActive);
            if (slot == null || PlayerInventory == null) return null;
            return PlayerInventory.GetItemAt(slot.GridPosition.x, slot.GridPosition.y);
        }

        static readonly ItemDrop.ItemData[] lastJewelry = new ItemDrop.ItemData[3];
        static int lastJewelryFrame = -1;

        /// <summary>Called on every local inventory change and on spawn; raises JewelryChanged only when a jewelry cell
        /// holds a different item (or none) than last time.</summary>
        internal static void CheckJewelryChanged(Player p)
        {
            if (p == null || p != Player.m_localPlayer || Time.frameCount == lastJewelryFrame) return;
            var now = GetJewelry(p);
            bool changed = false;
            for (int i = 0; i < 3; i++) if (!ReferenceEquals(now[i], lastJewelry[i])) { changed = true; lastJewelry[i] = now[i]; }
            if (!changed) return;
            lastJewelryFrame = Time.frameCount;
            EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry changed: necklace {now[0]?.m_dropPrefab?.name ?? "-"}, left {now[1]?.m_dropPrefab?.name ?? "-"}, right {now[2]?.m_dropPrefab?.name ?? "-"}");
            try { JewelryChanged?.Invoke(p); }
            catch (Exception ex) { Debug.LogWarning($"[VAInventory] a JewelryChanged handler threw: {ex.Message}"); }
        }

        /// <summary>
        /// The TeleportBypass slot accepts any item that vanilla considers
        /// non-teleportable (the items Player.IsTeleportable rejects, like
        /// metal ores). The actual bypass behavior lives in
        /// PlayerPatches.Player_IsTeleportable_Postfix.
        /// </summary>
        public static bool IsTeleportBypassSlotItem(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            // Allow only items that would otherwise be blocked from
            // teleporting. An item is teleportable when m_teleportable is
            // true; a "non-teleport" item has it false. We accept both
            // here because the slot is more useful as a generic "carry
            // anything I want past the teleport check" pocket — but the
            // patch in Player_IsTeleportable_Postfix only flips the
            // result for items that genuinely needed the bypass.
            // Never a backpack, on any path (drag, auto-place,
            // restore all ask this validator through Slot.ItemFits).
            return !BackpackBridge.IsBackpack(item);
        }

        // A bag already in the Bypass slot (from before this rule) moves to a free main-grid cell, or is dropped at the
        // player's feet if the grid is full. Checked about once a second, inventory open or not, so it also runs right after load.
        static float nextBypassCheck;
        static readonly System.Reflection.MethodInfo InventoryChanged = HarmonyLib.AccessTools.Method(typeof(Inventory), "Changed");

        internal static void EvictBagFromBypass()
        {
            if (Time.unscaledTime < nextBypassCheck) return;
            nextBypassCheck = Time.unscaledTime + 1f;
            var player = Player.m_localPlayer;
            var inv = PlayerInventory;
            if (player == null || inv == null) return;
            var bypass = AllSlots().FirstOrDefault(s => s != null && s.IsActive && s.IsTeleportBypassSlot);
            if (bypass == null) return;
            var item = bypass.CacheItem();
            if (item == null || !BackpackBridge.IsBackpack(item)) return;
            for (int y = 0; y < InventoryHeightPlayer; y++)
                for (int x = 0; x < InventoryWidth; x++)
                {
                    if (inv.GetItemAt(x, y) != null) continue;
                    item.m_gridPos = new Vector2i(x, y);
                    ClearCachedItems();
                    InventoryChanged?.Invoke(inv, null);
                    EquipmentPanel.PanelLog?.LogDebug($"slots: {item.m_dropPrefab?.name} moved out of the Bypass slot to the main grid ({x},{y}) (no bags in Bypass)");
                    return;
                }
            ClearCachedItems();
            player.DropItem(inv, item, item.m_stack);
            EquipmentPanel.PanelLog?.LogDebug($"slots: {item.m_dropPrefab?.name} moved out of the Bypass slot and dropped (main grid full; no bags in Bypass)");
        }

        // VAInventory's slot ids + validators (item-only, no local
        // player) go into Core's FiresSlotCatalog once the slots exist, so Core / FiresCompanions accept or reject by OUR rules.
        static bool s_catalogDone;
        internal static void RegisterCoreCatalog()
        {
            if (s_catalogDone) return;
            var cat = Type.GetType("FiresCore.Npc.FiresSlotCatalog, FiresUnifiedCore");
            if (cat == null) return;   // Core < 0.2.563 or not loaded yet: retried on the inventory open
            var reg = cat.GetMethod("Register", new[] { typeof(string), typeof(string), typeof(Func<ItemDrop.ItemData, bool>), typeof(int) });
            var cap = cat.GetMethod("RegisterBackpackCapacity", new[] { typeof(Func<ItemDrop.ItemData, int>) });
            s_catalogDone = true;
            if (reg == null) return;
            // counts from the config (plugin start: the player's slots may not exist yet), as Slots builds them
            var cfg = ConfigManager.Instance;
            int miscCells = Mathf.Clamp(cfg != null ? cfg.configEquipmentPanelRows.Value : 0, 0, 3) * 8;
            int tools = Math.Max(0, cfg != null ? cfg.configToolHotbarSlots.Value : 4);
            int ammo = cfg == null || cfg.configShowAmmoSlots.Value ? 3 : 0, food = cfg == null || cfg.configShowFoodSlots.Value ? 3 : 0;
            int n = 0;
            void R(string id, string group, Func<ItemDrop.ItemData, bool> v, int count)
            {
                try { reg.Invoke(null, new object[] { id, group, v, count }); n++; }
                catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"slots: catalog Register {id} failed: {(e.InnerException ?? e).Message}"); }
            }
            foreach (var id in new[] { helmetSlotID, shoulderSlotID, chestSlotID, utilitySlotID, legsSlotID, trinketSlotID, torchSlotID })
                R(id, "rail", GetValidatorForID(id), 1);
            R(extraUtilitySlotID, "rail", IsUtilitySlotItem, 1);
            foreach (var id in new[] { frontWeaponSlotID, frontOffhandSlotID, backWeaponSlotID, backOffhandSlotID })
                R(id, "bars", GetValidatorForID(id), 1);
            foreach (var id in new[] { necklaceSlotID, ringSlotID, ringRightSlotID })
                R(id, "jewelry", GetValidatorForID(id), 1);
            R(coinPurseSlotID, "coin", IsCoinSlotItem, 1);
            R(teleportBypassSlotID, "bag", IsTeleportBypassSlotItem, 1);
            R(petSlotID, "bag", PetBridge.IsPet, 1);
            R(backpackSlotID, "bag", IsBackpackSlotItem, 1);
            R("Tools", "pouch", IsToolHotbarSlotItem, tools);
            R(ammoSlotID, "pouch", IsAmmoSlotItem, ammo);
            R(foodSlotID, "pouch", IsFoodSlotItem, food);
            R(miscSlotID, "misc", item => item != null, miscCells);
            R("BackpackStorage", "bag", item => item != null && !IsBackpackSlotItem(item), 32);
            var size = cat.GetMethod("RegisterBackpackSize", new[] { typeof(Func<ItemDrop.ItemData, Vector2i>) });
            if (size != null) cap = null;   // prefer the (cols, rows) size, so bag items keep their place
            try { size?.Invoke(null, new object[] { (Func<ItemDrop.ItemData, Vector2i>)(bag => { var (r, c) = BackpackBridge.GetConfigDimensions(bag); return new Vector2i(c, r); }) }); }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"slots: catalog backpack size failed: {(e.InnerException ?? e).Message}"); }
            try { cap?.Invoke(null, new object[] { (Func<ItemDrop.ItemData, int>)(bag => { var (r, c) = BackpackBridge.GetConfigDimensions(bag); return Math.Max(0, r * c); }) }); }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"slots: catalog backpack capacity failed: {(e.InnerException ?? e).Message}"); }
            EquipmentPanel.PanelLog?.LogDebug($"slots: {n} slot id(s) registered in Core's FiresSlotCatalog (misc {miscCells}, tools {tools}, ammo {ammo}, food {food}; backpack {(size != null ? "size" : cap != null ? "capacity" : "n/a")})");
        }

        // Validator helper
        private static Func<ItemDrop.ItemData, bool> GetValidatorForID(string slotID)
        {
            return slotID switch
            {
                foodSlotID => IsFoodSlotItem,
                ammoSlotID => IsAmmoSlotItem,
                helmetSlotID => IsHelmetSlotItem,
                chestSlotID => IsChestSlotItem,
                legsSlotID => IsLegsSlotItem,
                shoulderSlotID => IsShoulderSlotItem,
                utilitySlotID => IsUtilitySlotItem,
                trinketSlotID => IsTrinketSlotItem,
                backpackSlotID => IsBackpackSlotItem,
                coinPurseSlotID => IsCoinSlotItem,
                ringSlotID or ringRightSlotID => IsRingSlotItem,
                necklaceSlotID => IsNecklaceSlotItem,
                teleportBypassSlotID => IsTeleportBypassSlotItem,
                frontWeaponSlotID or backWeaponSlotID => IsClassBarWeaponItem,
                frontOffhandSlotID or backOffhandSlotID => IsClassBarOffhandItem,
                torchSlotID => IsTorchSlotItem,
                petSlotID => PetBridge.IsPet,
                emptySlotID => (item => false),
                _ => (item => true)
            };
        }

        // Tool hotbar slots are managed separately
        public static List<Slot> toolHotbarSlots = [];

        // 1.0.133 (perf audit #1, the lead GO): every input InitializeSlots builds the slot set from, folded into one number. An
        // equip that leaves it unchanged has the same slots, so the panel need not be torn down and rebuilt (RefreshPanelOnly).
        // Bags count by identity and size: a bag equipped, removed or resized changes it.
        internal static int LayoutSignature()
        {
            unchecked
            {
                var cfg = ConfigManager.Instance;
                int h = 17;
                h = h * 31 + (cfg != null && cfg.configShowFoodSlots.Value ? 1 : 0);
                h = h * 31 + (cfg != null && cfg.configShowAmmoSlots.Value ? 1 : 0);
                h = h * 31 + (cfg != null ? cfg.configEquipmentPanelRows.Value : -1);
                h = h * 31 + (cfg != null ? cfg.configToolHotbarSlots.Value : -1);
                h = h * 31 + InventoryHeightPlayer;
                h = h * 31 + InventoryWidth;
                h = h * 31 + (ClassBarsAvailable() ? 1 : 0);
                h = h * 31 + (BackpackBridge.IsAvailable ? 1 : 0);
                h = h * 31 + (PetBridge.IsAvailable ? 1 : 0);
                var localPlayer = Player.m_localPlayer;
                h = h * 31 + (localPlayer != null ? localPlayer.GetInstanceID() : 0);
                if (localPlayer != null && BackpackBridge.IsAvailable)
                    foreach (var bag in BackpackBridge.GetEquippedBackpacks(localPlayer))
                    {
                        var (r, c) = BackpackBridge.GetConfigDimensions(bag);
                        h = h * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bag);
                        h = h * 31 + r * 101 + c;
                    }
                return h;
            }
        }
        internal static int LastLayoutSignature = int.MinValue;
        internal static int Generation;

        // Every rebuild goes through Relayout, which carries each player item across it by address (slot id / main x,y)
        internal static void InitializeSlots()
        {
            string trigger = "rebuild";
            try { var f = new System.Diagnostics.StackFrame(1, false).GetMethod(); if (f != null) trigger = f.DeclaringType?.Name + "." + f.Name; } catch { }
            Relayout.Run(trigger, InitializeSlotsCore);
        }

        internal static void InitializeSlotsForRelayout() => InitializeSlotsCore();

        private static void InitializeSlotsCore()
        {
            LastLayoutSignature = LayoutSignature();
            Generation++;   // The Slot objects are new; a panel bound to the old ones must rebuild
            bool showFood = ConfigManager.Instance.configShowFoodSlots.Value;
            bool showAmmo = ConfigManager.Instance.configShowAmmoSlots.Value;

            int mainGridRows = InventoryHeightPlayer;
            int equipmentPanelStartY = EquipmentPanelStartY;

            // === Calculate required array size BEFORE writing ===
            // Row 4 layout (parallel to row 3 / Legs+Trinket+food+ammo):
            //   col 0: CoinPurse (under Legs)
            //   col 1: Backpack  (under Trinket — hidden when VABackpacks absent)
            //   col 2: Ring      (under Food   — only present when showFood)
            //   col 3: TeleportBypass (under Ammo — only present when showAmmo)
            // Tying Ring/TeleportBypass to the food/ammo column toggles keeps
            // the panel rectangular: every row has the same column count, so
            // SetSlotsPositions's i % numColumns math doesn't drift.
            var rowDefinitions = new List<List<string>>
            {
                new() { helmetSlotID, shoulderSlotID },
                new() { chestSlotID, utilitySlotID },
                new() { legsSlotID, trinketSlotID },
                new() { coinPurseSlotID, backpackSlotID }
            };

            for (int r = 0; r < rowDefinitions.Count; r++)
            {
                bool isRow4 = (r == rowDefinitions.Count - 1);
                // 1.0.260 (HOWTO limits row 157: hiding Food also hid the Left ring, hiding Ammo also hid Teleport bypass): the food and ammo
                // COLUMNS are always in the layout; only the Food / Ammo CELLS switch off with ShowFoodSlots / ShowAmmoSlots (isActive below),
                // so the Ring and TeleportBypass cells in row 4 always exist. (Default on = the same layout as before.)
                rowDefinitions[r].Add(isRow4 ? ringSlotID : foodSlotID);
                rowDefinitions[r].Add(isRow4 ? teleportBypassSlotID : ammoSlotID);
            }

            // FiresRPGClasses' bar column goes LAST, so every existing slot keeps its grid position (and the items
            // saved there). Only when FiresRPGClasses is loaded, so a plain VAInventory install looks as before.
            // The column used to exist only when
            // FiresRPGClasses was ALREADY in Chainloader.PluginInfos at this moment. BepInEx fills that dictionary one plugin at a time, so
            // a build before RPGClasses loaded had no bar column and a later one did: the Torch column moved x 4 -> 5 and every
            // bag-storage block moved down a row (array 112 -> 116) INSIDE one session, and items saved under one layout were read under
            // the other. The column is now ALWAYS reserved (its four cells inactive without RPGClasses, as the Torch column's unused
            // cells are), so every x/y is the same whatever the load order.
            bool showBars = ClassBarsAvailable();
            {
                string[] barColumn = { frontWeaponSlotID, frontOffhandSlotID, backWeaponSlotID, backOffhandSlotID };
                for (int r = 0; r < rowDefinitions.Count && r < barColumn.Length; r++) rowDefinitions[r].Add(barColumn[r]);
            }

            // The Torch column goes after everything, for the same reason: one Torch slot and three unused cells (inactive,
            // they take nothing and show nothing), which keeps the block rectangular for SetSlotsPositions.
            // The Pet slot reuses the first of those unused cells: same index and grid position the Empty cell had (it
            // never held an item), so every existing slot and saved item stays put.
            // The two unused cells (always inactive, never held an item) are now the Necklace and the right ring.
            string[] torchColumn = { torchSlotID, petSlotID, necklaceSlotID, ringRightSlotID };
            for (int r = 0; r < rowDefinitions.Count && r < torchColumn.Length; r++) rowDefinitions[r].Add(torchColumn[r]);

            int numColumns = rowDefinitions[0].Count; // 3–6
            // The misc block keeps the old width: the bar and torch columns are not repeated under it.
            int miscColumns = numColumns - 1 - 1;   // The bar column is always there now (its width never counts toward misc)
            int miscRows = Mathf.Clamp(ConfigManager.Instance.configEquipmentPanelRows.Value, 0, 3);

            // Backpack-storage blocks: one block per equipped bag (e.g. shoulder
            // bag + HoboStick = two blocks). Each block is sized from that
            // specific bag's BackpackConfig, and the slot IDs embed the bag
            // prefab name so BackpackStorageSync can serialize per-bag.
            //
            // Stacked under the misc block at increasing y-coordinates so each
            // bag has unique grid positions in the player's Inventory — that
            // keeps Inventory.Save / RemoveItem / CountItems happy. The
            // detached BackpackPanel UI re-renders these positions wherever
            // the player has dragged the panel.
            var localPlayer = Player.m_localPlayer;
            var equippedBags = (localPlayer != null && BackpackBridge.IsAvailable)
                ? BackpackBridge.GetEquippedBackpacks(localPlayer)
                : (System.Collections.Generic.IReadOnlyList<ItemDrop.ItemData>)
                    [];

            int bpBlockSlots = 0;
            foreach (var bag in equippedBags)
            {
                var (r, c) = BackpackBridge.GetConfigDimensions(bag);
                bpBlockSlots += r * c;
            }

            int totalExtraSlots = (rowDefinitions.Count * numColumns)  // first block (now 4 rows)
                                + (miscRows * miscColumns)             // misc block
                                + CurrencyVaultCount                   // hidden coin-pouch currency vault
                                + bpBlockSlots;                        // sum of all backpack blocks
            int startIndex = mainGridRows * InventoryWidth;
            int requiredTotalSlots = startIndex + totalExtraSlots;

            // Resize array if needed (with some buffer)
            if (slots.Length < requiredTotalSlots + 16) // +16 for safety/future
            {
                Array.Resize(ref slots, requiredTotalSlots + 16);
                Debug.Log($"[VAInventory] Resized slots array to {slots.Length}");
            }

            // Clear the entire array to avoid old references
            Array.Clear(slots, 0, slots.Length);

            int index = startIndex;
            int createdSlots = 0;
            int foodShortcutIndex = 0;
            int ammoShortcutIndex = 0;

            // === FIRST BLOCK: Classic equipment slots (now 4 rows) ===
            for (int row = 0; row < rowDefinitions.Count; row++)
            {
                for (int col = 0; col < numColumns; col++, index++)
                {
                    string slotID = rowDefinitions[row][col];
                    Func<ItemDrop.ItemData, bool> validator = GetValidatorForID(slotID);
                    // Backpack slot only shows when VABackpacks is loaded.
                    // Other row-4 slots (CoinPurse, Ring, TeleportBypass)
                    // are always active when their column is enabled.
                    bool barSlot = slotID == frontWeaponSlotID || slotID == frontOffhandSlotID || slotID == backWeaponSlotID || slotID == backOffhandSlotID;
                    Func<bool> isActive = barSlot ? (Func<bool>)ClassBarsAvailable
                        : slotID == backpackSlotID
                        ? (Func<bool>)(() => BackpackBridge.IsAvailable)
                        : slotID == petSlotID ? (Func<bool>)(() => PetBridge.IsAvailable)
                        : slotID == emptySlotID ? (Func<bool>)(() => false)
                        : slotID == foodSlotID ? (Func<bool>)(() => ConfigManager.Instance == null || ConfigManager.Instance.configShowFoodSlots.Value)   // 1.0.260
                        : slotID == ammoSlotID ? (Func<bool>)(() => ConfigManager.Instance == null || ConfigManager.Instance.configShowAmmoSlots.Value)   // 1.0.260
                        : () => true;

                    if (slotID == foodSlotID)
                    {
                        int shortcutIndex = foodShortcutIndex++;
                        slots[index] = new Slot(
                            slotID,
                            index,
                            () => slotID,
                            validator,
                            isActive,
                            () => new KeyboardShortcut(),
                            () => ConfigManager.Instance.GetFoodHotbarKeybindText(shortcutIndex)
                        );
                    }
                    else if (slotID == ammoSlotID)
                    {
                        int shortcutIndex = ammoShortcutIndex++;
                        slots[index] = new Slot(
                            slotID,
                            index,
                            () => slotID,
                            validator,
                            isActive,
                            () => new KeyboardShortcut(),
                            () => ConfigManager.Instance.GetAmmoHotbarKeybindText(shortcutIndex)
                        );
                    }
                    else
                    {
                        slots[index] = new Slot(
                            slotID,
                            index,
                            () => ClassBarSlotName(slotID),
                            validator,
                            isActive
                        );
                    }

                    slots[index].SetGridPosition(new Vector2i(col, equipmentPanelStartY + row));
                    createdSlots++;
                }
            }
            // 1.0.260 proof (HOWTO limits row 157): the row-4 cells under the food / ammo columns stay active whatever the toggles
            {
                bool A(string id) { foreach (var s in slots) if (s != null && s.ID == id) return s.IsActive; return false; }
                bool foodOn = false, ammoOn = false; foreach (var s in slots) { if (s == null) continue; if (s.IsFoodSlot && s.IsActive) foodOn = true; if (s.IsAmmoSlot && s.IsActive) ammoOn = true; }
                Debug.Log($"[VAInventory] slots: ShowFoodSlots {showFood} / ShowAmmoSlots {showAmmo} -> Food cells active {foodOn}, Ammo cells active {ammoOn}, Left ring active {A(ringSlotID)}, TeleportBypass active {A(teleportBypassSlotID)}");
            }
            // === SECOND BLOCK: Misc slots ===
            int miscBlockStartY = equipmentPanelStartY + rowDefinitions.Count;
            for (int miscRow = 0; miscRow < miscRows; miscRow++)
            {
                for (int col = 0; col < miscColumns; col++, index++)
                {
                    string slotID = $"{miscSlotID}_{col}_{miscRow}";

                    slots[index] = new Slot(
                        slotID,
                        index,
                        () => "Misc Slot",
                        item => true,
                        () => true
                    );

                    slots[index].SetGridPosition(new Vector2i(col, miscBlockStartY + miscRow));
                    createdSlots++;
                }
            }

            // === CURRENCY VAULT BLOCK: hidden reserved cells for the coin pouch's non-displayed
            // currencies. Placed BEFORE the dynamic backpack blocks so their grid positions never
            // move when bags are equipped/unequipped (which would strand currency). Never rendered
            // in the equipment panel (filtered by IsCurrencyVaultSlot). One real stack per cell. ===
            int vaultStartY = miscBlockStartY + miscRows;
            int vaultRows = (CurrencyVaultCount + InventoryWidth - 1) / InventoryWidth;
            for (int v = 0; v < CurrencyVaultCount; v++, index++)
            {
                slots[index] = new Slot(
                    $"{currencyVaultSlotPrefix}_{v}",
                    index,
                    () => "Coin Pouch",
                    IsCurrencyItem,
                    () => true
                );
                slots[index].SetGridPosition(new Vector2i(v % InventoryWidth, vaultStartY + v / InventoryWidth));
                createdSlots++;
            }

            // === THIRD BLOCK(S): Dynamic backpack storage — one block per equipped bag ===
            // Each bag's block is sized from its own BackpackConfig and the slot
            // ID encodes the bag prefab name so BackpackStorageSync can route
            // serialize/deserialize to the correct bag's customData.
            //
            // Slot ID format: `BackpackStorage_<prefab>_<col>_<row>` — see
            // GetBackpackPrefabFromSlot for the parser.
            //
            // Items live in the same player Inventory at grid positions beyond
            // the normal range. Each bag's block lives at its own y-strip so
            // positions are unique across bags. The detached BackpackPanel UI
            // (one panel per bag) renders those positions wherever the player
            // dragged the panel.
            int bpCurrentY = vaultStartY + vaultRows;
            var bagBlocks = new System.Text.StringBuilder();
            foreach (var bag in equippedBags)
            {
                var (bpRows, bpCols) = BackpackBridge.GetConfigDimensions(bag);
                if (bpRows <= 0 || bpCols <= 0) continue;
                string prefab = bag?.m_dropPrefab?.name ?? "Unknown";
                string bagDisplayName = bag?.m_shared?.m_name ?? "Backpack";

                for (int row = 0; row < bpRows; row++)
                {
                    for (int col = 0; col < bpCols; col++, index++)
                    {
                        // IMPORTANT: prefab is included so we can route this
                        // slot back to the right bag on unequip-serialize.
                        string slotID = $"{backpackStorageSlotPrefix}_{prefab}_{col}_{row}";

                        // Capture-by-copy so the predicates see the right
                        // local-player at evaluation time.
                        string capturedName = bagDisplayName;
                        slots[index] = new Slot(
                            slotID,
                            index,
                            () => capturedName,
                            item => !BackpackBridge.IsBackpack(item),   // 1.0.320 (Fire 10-23: "we can put a backpack in its own grid"): anything but a bag
                            () => BackpackBridge.IsAvailable // hidden if bridge died
                        );
                        slots[index].SetGridPosition(new Vector2i(col, bpCurrentY + row));
                        createdSlots++;
                    }
                }
                bagBlocks.Append($", bag {prefab} {bpRows}x{bpCols} at y={bpCurrentY}");
                bpCurrentY += bpRows;
            }

            // === TOOL HOTBAR SLOTS (separate, not in main slots array) ===
            toolHotbarSlots.Clear();
            int toolHotbarCount = ConfigManager.Instance.configToolHotbarSlots.Value;
            for (int i = 0; i < toolHotbarCount; i++)
            {
                int idx = i; // capture loop variable to avoid closure issues
                string slotID = $"ToolHotbar{idx + 1}";
                var slot = new Slot(
                    slotID,
                    -1000 - idx, // negative index to avoid collision
                    () => $"Tool {idx + 1}",
                    IsToolHotbarSlotItem,
                    () => true,
                    () => new KeyboardShortcut(),
                    () => ConfigManager.KeyText(ConfigManager.Instance.configToolHotbarKeys[idx])
                );
                // Phase A of EXTRASLOTS_ALIGNMENT_PLAN: tool hotbar slots
                // sit directly below the player rows (y = InventoryHeightPlayer)
                // so they render visibly when the inventory opens, and items
                // dropped into those grid cells flow through vanilla
                // InventoryGrid.DropItem just like quickslots in
                // shudnal-ExtraSlots. Old saves had tool slot items at
                // y = InventoryHeightPlayer + 10 — those are migrated by
                // ItemsSlotsValidation in Phase B.
                slot.SetGridPosition(new Vector2i(idx, InventoryHeightPlayer));
                toolHotbarSlots.Add(slot);
            }

            UpdateSlotsGridPosition();

            string summary = $"[VAInventory] slots: equipment {rowDefinitions.Count}x{numColumns}, misc {miscRows}x{miscColumns}, " +
                             $"tool hotbar {toolHotbarCount}, food {showFood}, ammo {showAmmo}, class bars {showBars}{bagBlocks}; {createdSlots} created, array {slots.Length}";
            if (summary != _lastSlotSummary)
            {
                _lastSlotSummary = summary;
                Debug.Log(summary);
            }
        }

        private static string _lastSlotSummary;

        internal static void UpdateSlotsGridPosition()
        {
            ClearCachedItems();

            foreach (var slot in AllSlots())
            {
                slot.CacheItem();
            }

            ClearCachedItems();
            InventoryInteraction.UpdatePlayerInventorySize();
        }

        internal static void ClearCachedItems() => cachedItems.Clear();

        public static bool IsGridPositionHidden(Vector2i pos)
        {
            // A group that draws in the player's own grid is never hidden by the row check below.
            if (IsInVisibleGroup(pos))
                return false;

            // Main inventory: hidden if row >= InventoryHeightPlayer (hidden extra rows)
            if (pos.y >= InventoryHeightPlayer)
                return true;
            // Main inventory: hidden if column >= InventoryWidth (hidden columns)
            if (pos.x >= InventoryWidth)
                return true;
            // Equipment panel/misc: hidden if slot at this grid position is not active
            var slot = GetSlotInGrid(pos);
            if (slot != null && !slot.IsActive)
                return true;
            return false;
        }

        public class Slot(string id, int index, Func<string> getName, Func<ItemDrop.ItemData, bool> itemIsValid, Func<bool> isActive)
        {
            private readonly string _id = id;
            private readonly Func<string> _getName = getName;
            private readonly Func<KeyboardShortcut> _getShortcut;
            private readonly Func<string> _getShortcutText;
            private readonly Func<ItemDrop.ItemData, bool> _itemIsValid = itemIsValid;
            private readonly Func<bool> _isActive = isActive;

            private readonly int _index = index;
            private Vector2 _position;
            internal Vector2i _gridPos = emptyPosition;

            public string ID => _id;
            public string Name => _getName != null ? Localization.instance.Localize(_getName()) : "";
            public bool IsActive => _isActive == null || _isActive();
            public Vector2 Position => _position;
            public Vector2i GridPosition => _gridPos;
            public int Index => _index;
            public int EquipmentIndex => IsCustomSlot ? _index + 200 : (IsTrinketSlot ? _index : _index + 100);
            public bool IsHotkeySlot => _getShortcut != null;

            public bool IsQuickSlot => false;
            public bool IsMiscSlot => _id.StartsWith(miscSlotID);
            public bool IsAmmoSlot => _id == ammoSlotID;
            public bool IsFoodSlot => _id == foodSlotID;
            public bool IsEquipmentSlot => _index >= InventorySizePlayer;
            public bool IsVanillaSlot => vanillaSlots.Contains(_id);
            public bool IsTrinketSlot => _id == trinketSlotID;
            public bool IsCustomSlot => false;
            public bool IsBackpackSlot => _id == backpackSlotID;
            public bool IsCoinPurseSlot => _id == coinPurseSlotID;
            public bool IsCurrencyVaultSlot => _id != null && _id.StartsWith(currencyVaultSlotPrefix);
            public bool IsRingSlot => _id == ringSlotID;
            public bool IsTeleportBypassSlot => _id == teleportBypassSlotID;
            public bool IsBackpackStorageSlot => _id != null && _id.StartsWith(backpackStorageSlotPrefix);
            public bool IsClassBarSlot => _id == frontWeaponSlotID || _id == frontOffhandSlotID
                                          || _id == backWeaponSlotID || _id == backOffhandSlotID;
            public bool IsTorchSlot => _id == torchSlotID;
            public bool IsPetSlot => _id == petSlotID;

            internal void SetPosition(Vector2 newPosition)
            {
                if (_position != newPosition)
                {
                    _position = newPosition;
                    EquipmentPanel.MarkDirty();
                }
            }

            internal void UpdateGridPosition()
            {
                _gridPos = new Vector2i(_index % InventoryWidth, _index / InventoryWidth + InventoryHeightPlayer);
                var item = Item;
                if (item != null)
                    item.m_gridPos = _gridPos;
            }

            public ItemDrop.ItemData Item
            {
                get
                {
                    if (PlayerInventory == null || _gridPos == emptyPosition) return null;
                    // A cached item that left the slot (eaten, moved, removed) is not trusted.
                    if (cachedItems.TryGetValue(_gridPos, out var cached)
                        && (cached == null || (cached.m_gridPos == _gridPos && PlayerInventory.ContainsItem(cached)))) return cached;
                    return CacheItem();
                }
            }

            internal ItemDrop.ItemData CacheItem()
            {
                if (PlayerInventory == null) return null;
                var item = PlayerInventory.GetItemAt(_gridPos.x, _gridPos.y);
                cachedItems[_gridPos] = item;
                return item;
            }

            public bool IsFree => Item == null;

            public bool ItemFits(ItemDrop.ItemData item) =>
                item != null && IsActive && (_itemIsValid == null || _itemIsValid(item));

            public string GetShortcutText() => _getShortcutText != null ? Localization.instance.Localize(_getShortcutText()) : Name;

            public Slot(string id, int index, Func<string> getName, Func<ItemDrop.ItemData, bool> itemIsValid, Func<bool> isActive,
                        Func<KeyboardShortcut> getShortcut, Func<string> getShortcutText)
                : this(id, index, getName, itemIsValid, isActive)
            {
                _getShortcut = getShortcut;
                _getShortcutText = getShortcutText;
            }

            public void SetGridPosition(Vector2i pos)
            {
                _gridPos = pos;
            }
        }
    }
}
namespace VerdantsAscent.Managers
{
    // 1.0.199
    [HarmonyLib.HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
    internal static class SlotCatalogAtObjectDB
    {
        static void Postfix() { try { Slots.RegisterCoreCatalog(); } catch (System.Exception e) { EquipmentPanel.PanelLog?.LogWarning("slots: catalog registration failed: " + e.Message); } }
    }
}
