// CoinPurseStackPatch — let the dedicated coin-purse slot stack the
// "Coins" prefab past its vanilla m_maxStackSize cap.
//
// Strategy: per-slot bypass. We don't mutate the Coins prefab's
// m_maxStackSize (which would break vanilla coin behavior in chests
// and ground drops). Instead, when the player AddItems coins, we
// detect the purse and merge into the existing purse stack regardless
// of cap.
//
// See Refs/BACKPACK_AND_COIN_SLOT_PLAN.md §4.

using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    [HarmonyPatch]
    internal static class CoinPurseStackPatch
    {
        // Hard cap so AddItem maths never overflow. Anything larger than
        // ~2 billion is clearly not a player's pocket money.
        private const int MAX_PURSE_STACK = int.MaxValue - 1;
        private const string PURSE_STACK_KEY = "VAInventoryCoinPurseStack";
        private static readonly FieldInfo fi_inventory_list = AccessTools.Field(typeof(Inventory), "m_inventory");

        /// <summary>
        /// Returns the coin-purse slot if the player has one configured
        /// AND the inventory belongs to the local player. Null otherwise.
        /// </summary>
        private static Slots.Slot ResolvePurseSlot(Inventory inv)
        {
            if (inv == null) return null;
            var local = Player.m_localPlayer;
            if (local == null) return null;
            if (inv != local.GetInventory()) return null;

            return Slots.AllSlots()
                .FirstOrDefault(s => s != null && s.IsCoinPurseSlot && s.IsActive);
        }

        // Single source of truth with the slot's own fit check — Slots.IsCoinSlotItem is
        // null-m_dropPrefab tolerant (trade/split/chest coins), so pickup routing, the
        // CanAddItem override, and the purse slot all agree on what "coins" means.
        private static bool IsCoins(ItemDrop.ItemData item) => Slots.IsCoinSlotItem(item);

        private static List<ItemDrop.ItemData> GetInventoryList(Inventory inv)
        {
            return inv != null ? fi_inventory_list.GetValue(inv) as List<ItemDrop.ItemData> : null;
        }

        private static void InvokeChanged(Inventory inv)
        {
            try { inv?.m_onChanged?.Invoke(); } catch { }
        }

        private static void PersistPurseStack(ItemDrop.ItemData item)
        {
            if (!IsCoins(item)) return;
            item.m_customData[PURSE_STACK_KEY] = item.m_stack.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryGetPersistedPurseStack(ItemDrop.ItemData item, out int stack)
        {
            stack = 0;
            return IsCoins(item)
                && item.m_customData.TryGetValue(PURSE_STACK_KEY, out string value)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out stack)
                && stack > 0;
        }

        private static ItemDrop.ItemData GetPurseCoins(Inventory inv, Slots.Slot purseSlot = null)
        {
            purseSlot ??= ResolvePurseSlot(inv);
            return purseSlot != null ? inv.GetItemAt(purseSlot.GridPosition.x, purseSlot.GridPosition.y) : null;
        }

        // ── Multi-currency cell helpers ─────────────────────────────────────────────
        // The pouch = the visible CoinPurse slot (the displayed currency) + hidden vault cells
        // (the other configured currencies). Each currency occupies at most ONE cell: it merges
        // into its own cell, and a brand-new currency claims the empty purse (becoming displayed)
        // or a free vault cell.

        // The cell currently holding currency `key`, or emptyPosition if none.
        private static Vector2i FindCurrencyCell(Inventory inv, string key)
        {
            var purse = Slots.GetCoinPurseSlot();
            if (purse != null)
            {
                var it = inv.GetItemAt(purse.GridPosition.x, purse.GridPosition.y);
                if (it != null && string.Equals(Slots.CurrencyKey(it), key, System.StringComparison.OrdinalIgnoreCase))
                    return purse.GridPosition;
            }
            foreach (var v in Slots.GetCurrencyVaultSlots())
            {
                var it = inv.GetItemAt(v.GridPosition.x, v.GridPosition.y);
                if (it != null && string.Equals(Slots.CurrencyKey(it), key, System.StringComparison.OrdinalIgnoreCase))
                    return v.GridPosition;
            }
            return Slots.emptyPosition;
        }

        // A free cell for a brand-new currency: the purse if empty (becomes displayed), else a
        // free vault cell. emptyPosition if the pouch is full.
        private static Vector2i ClaimCurrencyCell(Inventory inv)
        {
            var purse = Slots.GetCoinPurseSlot();
            if (purse != null && inv.GetItemAt(purse.GridPosition.x, purse.GridPosition.y) == null)
                return purse.GridPosition;
            foreach (var v in Slots.GetCurrencyVaultSlots())
                if (inv.GetItemAt(v.GridPosition.x, v.GridPosition.y) == null)
                    return v.GridPosition;
            return Slots.emptyPosition;
        }

        // Every live currency stack in the pouch (purse + vault cells).
        private static IEnumerable<ItemDrop.ItemData> EnumCurrencyStacks(Inventory inv)
        {
            var purse = Slots.GetCoinPurseSlot();
            if (purse != null)
            {
                var it = inv.GetItemAt(purse.GridPosition.x, purse.GridPosition.y);
                if (IsCoins(it)) yield return it;
            }
            foreach (var v in Slots.GetCurrencyVaultSlots())
            {
                var it = inv.GetItemAt(v.GridPosition.x, v.GridPosition.y);
                if (IsCoins(it)) yield return it;
            }
        }

        private static void PersistCurrentPurseStack(Inventory inv)
        {
            if (inv != Slots.PlayerInventory) return;
            foreach (var it in EnumCurrencyStacks(inv))
                PersistPurseStack(it);
        }

        private static void RestorePersistedPurseStack(Inventory inv)
        {
            if (inv != Slots.PlayerInventory) return;
            bool any = false;
            foreach (var it in EnumCurrencyStacks(inv))
            {
                if (!TryGetPersistedPurseStack(it, out int persisted)) continue;
                int restored = persisted > MAX_PURSE_STACK ? MAX_PURSE_STACK : persisted;
                if (restored <= it.m_stack) continue;
                it.m_stack = restored;
                PersistPurseStack(it);
                any = true;
            }
            if (any) { InvokeChanged(inv); EquipmentPanel.MarkDirty(); }
        }

        // Deposit a currency into its home cell (over-cap merge), routed by currency key.
        // Any configured currency, from any source — this is what makes auto-deposit universal.
        private static bool TryDepositCurrency(Inventory inv, ItemDrop.ItemData item, int amount, bool itemIsAlreadyInInventory)
        {
            // Player pouch only — the vault/purse cells are player-scoped grid positions; running
            // this against a chest would read chest cells at those positions and corrupt them.
            if (inv == null || inv != Slots.PlayerInventory || amount <= 0) return false;
            string key = Slots.CurrencyKey(item);
            if (key == null) return false;

            var list = GetInventoryList(inv);
            if (list == null) return false;

            var cell = FindCurrencyCell(inv, key);
            var existing = cell != Slots.emptyPosition ? inv.GetItemAt(cell.x, cell.y) : null;

            if (existing != null && existing != item)
            {
                int moved = Mathf.Min(amount, item.m_stack);
                long combined = (long)existing.m_stack + moved;
                existing.m_stack = combined > MAX_PURSE_STACK ? MAX_PURSE_STACK : (int)combined;
                PersistPurseStack(existing);
                if (itemIsAlreadyInInventory)
                {
                    item.m_stack -= moved;
                    if (item.m_stack <= 0) list.Remove(item);
                }
                InvokeChanged(inv);
                return true;
            }
            if (existing == item) return true;

            // Brand-new currency for the pouch — claim the purse (if empty) or a vault cell.
            var target = ClaimCurrencyCell(inv);
            if (target == Slots.emptyPosition) return false;
            if (itemIsAlreadyInInventory || !list.Contains(item))
            {
                if (amount < item.m_stack) return false;   // partial move into a fresh cell unsupported
                item.m_gridPos = target;
                PersistPurseStack(item);
                if (!list.Contains(item)) list.Add(item);
                InvokeChanged(inv);
                return true;
            }
            return false;
        }

        // Back-compat name — existing callers route through the generic path now.
        private static bool TryDepositCoinsIntoPurse(Inventory inv, ItemDrop.ItemData item, int amount, bool itemIsAlreadyInInventory)
            => TryDepositCurrency(inv, item, amount, itemIsAlreadyInInventory);

        // ── Cycle the displayed currency (top-left pouch icon / popup) ───────────────
        // Swaps the currency in the visible purse slot with the next present currency (config
        // order, wrapping). No new stacks are created — just grid-position swaps — so no money
        // can be duplicated or lost.
        public static void CycleDisplayed()
        {
            var inv = Slots.PlayerInventory;
            var purse = Slots.GetCoinPurseSlot();
            if (inv == null || purse == null) return;

            var order = ConfigManager.Instance.GetCurrencyPrefabs();
            // Present currencies in config order.
            var present = new List<string>();
            foreach (var name in order)
                if (FindCurrencyCell(inv, name) != Slots.emptyPosition)
                    present.Add(name);
            if (present.Count <= 1) return;

            var pursePos = purse.GridPosition;
            var purseItem = inv.GetItemAt(pursePos.x, pursePos.y);
            string displayed = purseItem != null ? Slots.CurrencyKey(purseItem) : null;
            int idx = displayed != null ? present.IndexOf(displayed) : -1;
            string next = present[(idx + 1) % present.Count];
            if (string.Equals(next, displayed, System.StringComparison.OrdinalIgnoreCase)) return;

            var nextPos = FindCurrencyCell(inv, next);
            if (nextPos == Slots.emptyPosition) return;
            var nextItem = inv.GetItemAt(nextPos.x, nextPos.y);
            if (nextItem == null) return;

            // Swap grid positions: next → visible purse, displayed (if any) → next's old vault cell.
            nextItem.m_gridPos = pursePos;
            if (purseItem != null) purseItem.m_gridPos = nextPos;
            InvokeChanged(inv);
            EquipmentPanel.MarkDirty();
        }

        // Set a SPECIFIC currency as displayed (right-click popup cell). Swaps it into the visible
        // slot; a pure grid-position swap so nothing can be duplicated or lost.
        public static void SetDisplayed(string key)
        {
            var inv = Slots.PlayerInventory;
            var purse = Slots.GetCoinPurseSlot();
            if (inv == null || purse == null || string.IsNullOrEmpty(key)) return;
            var pursePos = purse.GridPosition;
            var purseItem = inv.GetItemAt(pursePos.x, pursePos.y);
            if (purseItem != null && string.Equals(Slots.CurrencyKey(purseItem), key, System.StringComparison.OrdinalIgnoreCase))
                return;
            var targetPos = FindCurrencyCell(inv, key);
            if (targetPos == Slots.emptyPosition) return;
            var targetItem = inv.GetItemAt(targetPos.x, targetPos.y);
            if (targetItem == null) return;
            targetItem.m_gridPos = pursePos;
            if (purseItem != null) purseItem.m_gridPos = targetPos;
            InvokeChanged(inv);
            EquipmentPanel.MarkDirty();
        }

        // The currency prefab shown in the visible pouch slot (null if empty).
        public static string DisplayedKey()
        {
            var inv = Slots.PlayerInventory;
            var purse = Slots.GetCoinPurseSlot();
            var it = (inv != null && purse != null) ? inv.GetItemAt(purse.GridPosition.x, purse.GridPosition.y) : null;
            return it != null ? Slots.CurrencyKey(it) : null;
        }

        // Every configured currency in order, paired with its live stack (null if not carried).
        public static List<(string key, ItemDrop.ItemData item)> GetPouchContents()
        {
            var res = new List<(string, ItemDrop.ItemData)>();
            var inv = Slots.PlayerInventory;
            if (inv == null) return res;
            foreach (var key in ConfigManager.Instance.GetCurrencyPrefabs())
            {
                var cell = FindCurrencyCell(inv, key);
                var item = cell != Slots.emptyPosition ? inv.GetItemAt(cell.x, cell.y) : null;
                res.Add((key, item));
            }
            return res;
        }

        // Icon for a currency — from the live stack if carried, else the prefab's item icon.
        public static Sprite GetCurrencyIcon(string key, ItemDrop.ItemData present)
        {
            try { if (present != null) return present.GetIcon(); } catch { }
            try
            {
                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(key) : null;
                var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop != null && drop.m_itemData?.m_shared?.m_icons != null && drop.m_itemData.m_shared.m_icons.Length > 0)
                    return drop.m_itemData.GetIcon();
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Coins → purse routing. When the player picks up / receives a Coins
        /// item, prefer to merge it into the existing purse stack (bypassing
        /// the per-stack cap), and skip the vanilla AddItem path entirely.
        /// If the purse is empty, fall through to vanilla and let it land
        /// somewhere — a follow-up patch (TryFindFreeSlotForItem) can route
        /// to the empty purse slot specifically.
        /// </summary>
        internal static bool AddItemDepositCoins(Inventory __instance,
                                                ItemDrop.ItemData item,
                                                ref bool __result)
        {
            if (!IsCoins(item))
                return true;

            if (TryDepositCoinsIntoPurse(__instance, item, item.m_stack, false))
            {
                __result = true;
                return false;   // skip vanilla AddItem
            }

            return true;
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.Save))]
        [HarmonyPrefix]
        private static void Inventory_Save_PersistPurseStack(Inventory __instance)
        {
            PersistCurrentPurseStack(__instance);
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.Load), [typeof(ZPackage)])]
        [HarmonyPostfix]
        private static void Inventory_Load_RestorePurseStack(Inventory __instance)
        {
            RestorePersistedPurseStack(__instance);
        }

        internal static void RestorePurseStackOnSpawn(Player __instance)
        {
            RestorePersistedPurseStack(__instance.GetInventory());
        }

        // Re-persist the purse's live stack after any removal so the PURSE_STACK_KEY
        // snapshot never lags the real amount. Spending coins FROM the purse (trader
        // buy, companion recruit, vanilla RemoveItem) lowers m_stack but would leave
        // the snapshot at the old higher value — and because vanilla clamps the
        // over-cap stack to m_maxStackSize on Load, RestorePersistedPurseStack would
        // then treat the stale-high snapshot as the truth and REFUND the spent coins
        // on the next load / respawn. Both vanilla RemoveItem overloads decrement and
        // call Changed(); neither is invoked during Inventory.Load, so re-persisting
        // here is safe and can't clobber the pre-restore clamped value.
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), [typeof(ItemDrop.ItemData), typeof(int)])]
        [HarmonyPostfix]
        private static void Inventory_RemoveItemByItem_ResyncPurse(Inventory __instance)
        {
            PersistCurrentPurseStack(__instance);
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), [typeof(string), typeof(int), typeof(int), typeof(bool)])]
        [HarmonyPostfix]
        private static void Inventory_RemoveItemByName_ResyncPurse(Inventory __instance)
        {
            PersistCurrentPurseStack(__instance);
        }

        internal static void CanAddItemCoinsFitPurse(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
        {
            if (__result || __instance != Slots.PlayerInventory) return;
            string key = Slots.CurrencyKey(item);
            if (key == null) return;
            // Fits when this currency already has a pouch cell, or a fresh cell can be claimed.
            if (FindCurrencyCell(__instance, key) != Slots.emptyPosition || ClaimCurrencyCell(__instance) != Slots.emptyPosition)
                __result = true;
        }

        internal static bool OnRightDownDepositCoins(InventoryGrid __instance, UIInputHandler element)
        {
            if (__instance == null || __instance != InventoryGui.instance?.m_playerGrid) return true;

            var inv = Slots.PlayerInventory;
            if (inv == null) return true;

            var pos = GetElementGridPosition(__instance, element);
            if (pos == Slots.emptyPosition) return true;

            var item = inv.GetItemAt(pos.x, pos.y);
            if (!IsCoins(item)) return true;

            var purseSlot = ResolvePurseSlot(inv);
            if (purseSlot == null || item.m_gridPos == purseSlot.GridPosition) return false;

            if (TryDepositCoinsIntoPurse(inv, item, item.m_stack, true))
            {
                EquipmentPanel.MarkDirty();
                return false;
            }

            return true;
        }

        internal static bool DropItemDepositCoins(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, int amount, Vector2i pos)
        {
            if (__instance == null || __instance != InventoryGui.instance?.m_playerGrid) return true;
            if (fromInventory != Slots.PlayerInventory) return true;
            if (!IsCoins(item)) return true;

            var purseSlot = ResolvePurseSlot(fromInventory);
            if (purseSlot == null || pos != purseSlot.GridPosition) return true;

            if (TryDepositCoinsIntoPurse(fromInventory, item, amount, true))
            {
                EquipmentPanel.MarkDirty();
                EquipmentPanel.ClearDragState();
                return false;
            }

            return true;
        }

        private static Vector2i GetElementGridPosition(InventoryGrid grid, UIInputHandler element)
        {
            if (grid == null || element == null) return Slots.emptyPosition;

            if (AccessTools.Field(typeof(InventoryGrid), "m_elements")?.GetValue(grid) is not System.Collections.IList elements) return Slots.emptyPosition;

            for (int i = 0; i < elements.Count; i++)
            {
                // Valheim 1.0 replaced the nested InventoryGrid.Element with the top-level InventoryElement
                // MonoBehaviour and deleted m_go, so this reflection returned null for every entry, no row
                // ever matched, and the purse slot silently reported "no grid position". Vanilla's own
                // element-to-index lookup compares .gameObject the same way.
                if (elements[i] is not InventoryElement entry) continue;
                if (entry.gameObject != element.gameObject) continue;

                int width = grid.GetInventory()?.GetWidth() ?? Slots.InventoryWidth;
                return new Vector2i(i % width, i / width);
            }

            return Slots.emptyPosition;
        }

        // (Future hook point: a defensive Inventory.Changed Postfix that
        // clamps the purse stack to MAX_PURSE_STACK could go here. Omitted
        // for now since AddItem already enforces the cap on every deposit.)
    }
}
