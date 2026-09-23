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
        public const string teleportBypassSlotID  = "TeleportBypass";
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

        // InventoryHeightFull now considers any tool hotbar rows so tool slots are not treated as out-of-bounds
        public static int InventoryHeightFull
        {
            get
            {
                int baseFull = InventoryHeightPlayer + GetTargetInventoryHeight(slots.Length, InventoryWidth);
                if (toolHotbarSlots != null && toolHotbarSlots.Count > 0)
                {
                    int maxHotbarY = toolHotbarSlots.Where(s => s != null).Select(s => s.GridPosition.y).DefaultIfEmpty(InventoryHeightPlayer - 1).Max();
                    // height is number of rows, so maxY +1
                    return Math.Max(baseFull, maxHotbarY + 1);
                }
                return baseFull;
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

        public static Slot[] GetMiscSlots() =>
            [.. AllSlots().Where(s => s != null && s.IsMiscSlot && s.IsActive)];

        // Consider positions that are either in the hidden rows OR explicitly present in toolHotbarSlots as valid slot positions
        public static bool IsGridPositionASlot(Vector2i gridPos)
        {
            if (gridPos.y >= InventoryHeightPlayer)
                return true;
            if (toolHotbarSlots != null && toolHotbarSlots.Any(s => s != null && s.GridPosition == gridPos))
                return true;
            return false;
        }

        public static bool IsItemInSlot(ItemDrop.ItemData item) =>
            item != null && IsGridPositionASlot(item.m_gridPos);

        public static bool IsItemInEquipmentSlot(ItemDrop.ItemData item) =>
            item != null && GetItemSlot(item)?.IsEquipmentSlot == true;

        // Return a sequence containing both the main slots array and tool hotbar slots
        private static IEnumerable<Slot> AllSlots()
        {
            foreach (var s in slots)
            {
                if (s != null) yield return s;
            }
            if (toolHotbarSlots != null)
            {
                foreach (var s in toolHotbarSlots)
                {
                    if (s != null) yield return s;
                }
            }
        }

        public static Slot GetSlotInGrid(Vector2i pos)
        {
            if (!IsGridPositionASlot(pos)) return null;
            // Prefer slots from the main slots array (equipment panel) first
            var main = slots.FirstOrDefault(slot => slot != null && slot.GridPosition == pos);
            if (main != null) return main;
            // Then check tool hotbar slots explicitly
            if (toolHotbarSlots != null)
            {
                return toolHotbarSlots.FirstOrDefault(slot => slot != null && slot.GridPosition == pos);
            }
            return null;
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

            slot = AllSlots().FirstOrDefault(s => s != null && s.IsActive && s.IsFree && s.ItemFits(item));
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

                if (TryFindFreeSlotForItem(item, out freeSlot))
                {
                    pos = item.m_gridPos;
                    item.m_gridPos = freeSlot.GridPosition;
                    return true;
                }
            }

            return false;
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
            if (RingItemMatcher != null) return RingItemMatcher(item);
            string n = item.m_dropPrefab.name?.ToLowerInvariant() ?? string.Empty;
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
            return true;
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
                ringSlotID => IsRingSlotItem,
                teleportBypassSlotID => IsTeleportBypassSlotItem,
                _ => (item => true)
            };
        }

        // Tool hotbar slots are managed separately
        public static List<Slot> toolHotbarSlots = [];

        internal static void InitializeSlots()
        {
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
                if (showFood) rowDefinitions[r].Add(isRow4 ? ringSlotID : foodSlotID);
                if (showAmmo) rowDefinitions[r].Add(isRow4 ? teleportBypassSlotID : ammoSlotID);
            }

            int numColumns = rowDefinitions[0].Count; // 2–4
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
                                + (miscRows * numColumns)              // misc block
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
                    Func<bool> isActive = slotID == backpackSlotID
                        ? (Func<bool>)(() => BackpackBridge.IsAvailable)
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
                            () => slotID,
                            validator,
                            isActive
                        );
                    }

                    slots[index].SetGridPosition(new Vector2i(col, equipmentPanelStartY + row));
                    createdSlots++;
                }
            }
            // === SECOND BLOCK: Misc slots ===
            int miscBlockStartY = equipmentPanelStartY + rowDefinitions.Count;
            for (int miscRow = 0; miscRow < miscRows; miscRow++)
            {
                for (int col = 0; col < numColumns; col++, index++)
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
                            item => true,                    // accepts anything
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
                    () => ConfigManager.Instance.configToolHotbarKeybindsEx[idx].Value
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

            string summary = $"[VAInventory] slots: equipment {rowDefinitions.Count}x{numColumns}, misc {miscRows}x{numColumns}, " +
                             $"tool hotbar {toolHotbarCount}, food {showFood}, ammo {showAmmo}{bagBlocks}; {createdSlots} created, array {slots.Length}";
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

            foreach (var slot in slots)
            {
                slot?.CacheItem();
            }

            // Also cache tool hotbar slots so validation can see items placed there
            if (toolHotbarSlots != null)
            {
                foreach (var t in toolHotbarSlots)
                {
                    t?.CacheItem();
                }
            }

            ClearCachedItems();
            InventoryInteraction.UpdatePlayerInventorySize();
        }

        internal static void ClearCachedItems() => cachedItems.Clear();

        public static bool IsGridPositionHidden(Vector2i pos)
        {
            // If this position belongs to a tool hotbar slot, it should NOT be treated as hidden
            if (toolHotbarSlots != null && toolHotbarSlots.Any(s => s != null && s.GridPosition == pos))
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
                    if (cachedItems.TryGetValue(_gridPos, out var cached)) return cached;
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