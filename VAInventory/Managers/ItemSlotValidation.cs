using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent
{
    public static class ItemsSlotsValidation
    {
        private static readonly HashSet<Vector2i> occupiedPositions = [];
        private static readonly List<ItemDrop.ItemData> tempItems = [];

        private static bool isDirty = false;
        private static bool validationScheduled = false;

        // Inventory signature to avoid repeated logs
        private static int lastInventoryHash =0;
        private static bool loggedForCurrentInventory = false;

        // Suppress reacting to Inventory change events that we cause ourselves
        private static bool suppressOnInventoryChanged = false;

        // Rate-limit warnings
        private static float lastOverlapWarningTime =0f;
        private static float lastOutOfBoundsWarningTime =0f;

        // Reflection for private fields
        private static readonly FieldInfo fi_inventory_list = AccessTools.Field(typeof(Inventory), "m_inventory");
        private static readonly FieldInfo fi_player_isLoading = AccessTools.Field(typeof(Player), "m_isLoading");
        private static readonly FieldInfo fi_onChanged = AccessTools.Field(typeof(Inventory), "m_onChanged");

        public static void MarkDirty() => isDirty = true;

        public static void ScheduleValidateNextFrame()
        {
            if (validationScheduled) return;
            validationScheduled = true;
            var player = Player.m_localPlayer;
            if (player != null)
            {
                try { player.StartCoroutine(ValidateNextFrameCoroutine()); }
                catch { validationScheduled = false; }
            }
            else
            {
                validationScheduled = false;
            }
        }

        private static IEnumerator ValidateNextFrameCoroutine()
        {
            // Run after the frame to avoid re-entrant InventoryGrid/InventoryGui updates
            yield return new WaitForEndOfFrame();
            validationScheduled = false;
            try { Validate(); } catch (Exception ex) { if (VerdantsAscent.EquipmentPanel.VerboseLogging) Debug.Log($"[VAInventory] ValidateNextFrame exception: {ex}"); }
        }

        public static void Validate()
        {
            ValidateItems();
            ValidateSlots();
        }

        private static int ComputeInventoryHash(List<ItemDrop.ItemData> items)
        {
            unchecked
            {
                int hash =17;
                for (int i =0; i < items.Count; i++)
                {
                    var it = items[i];
                    int id = it != null && it.m_shared != null ? it.m_shared.m_name.GetHashCode() :0;
                    int pos = it != null ? (it.m_gridPos.x *397) ^ it.m_gridPos.y :0;
                    hash = hash *31 + id;
                    hash = hash *31 + pos;
                }
                return hash;
            }
        }

        public static void ValidateItems()
        {
            if (VerdantsAscent.EquipmentPanel.IsMigrating) return;
            if (!isDirty || Player.m_localPlayer == null || (bool)fi_player_isLoading.GetValue(Player.m_localPlayer))
                return;
            isDirty = false;
            var inventory = Slots.PlayerInventory;
            if (inventory == null) return;
            var items = (List<ItemDrop.ItemData>)fi_inventory_list.GetValue(inventory);
            if (items == null) return;

            // Compute signature and only log once per inventory change
            int currentHash = ComputeInventoryHash(items);
            if (currentHash != lastInventoryHash)
            {
                lastInventoryHash = currentHash;
                loggedForCurrentInventory = false;
            }

            if (!loggedForCurrentInventory)
            {
                loggedForCurrentInventory = true;
                Debug.Log($"[VAInventory] ValidateItems: inventory changed — starting validation. inventoryItems={items.Count}");
            }

            occupiedPositions.Clear();
            tempItems.Clear();
            var itemsToMove = new List<ItemDrop.ItemData>();
            var itemsToDrop = new List<ItemDrop.ItemData>();
            for (int idx =0; idx < items.Count; idx++)
            {
                var item = items[idx];
                if (item == null) continue;

                // NEW: If the item is placed in a tool hotbar slot, treat it as valid and skip
                bool isInToolHotbar = Slots.toolHotbarSlots != null && Slots.toolHotbarSlots.Any(s => s != null && s.GridPosition == item.m_gridPos);
                if (isInToolHotbar)
                {
                    // mark occupied to avoid false overlap detection and skip further validation
                    occupiedPositions.Add(item.m_gridPos);
                    continue;
                }

                // If this item should be in an extra slot, but isn't at a valid extra slot position, mark for move
                if (Slots.IsItemInSlot(item) && !Slots.IsGridPositionASlot(item.m_gridPos))
                {
                    var slot = Slots.GetItemSlot(item);
                    if (slot != null && slot.IsActive)
                    {
                        if (ConfigManager.IsVerbose)
                            Debug.Log($"[VAInventory] Moving {item.m_shared.m_name} to extra slot {slot.GridPosition} (was {item.m_gridPos})");
                        itemsToMove.Add(item); // mark for move
                    }
                    else
                    {
                        // No active slot for this item (e.g. config disabled the slot it
                        // was in). Queue for re-placement via PutIntoFirstEmptySlot rather
                        // than writing an out-of-bounds sentinel position — the sentinel
                        // path used to route through out-of-bounds detection and could
                        // silently drop the item on the ground if validation got
                        // interrupted before relocation.
                        if (ConfigManager.IsVerbose)
                            Debug.Log($"[VAInventory] {item.m_shared.m_name} at {item.m_gridPos}: no active slot, queuing for relocation");
                        tempItems.Add(item);
                        continue;
                    }
                }
                // Overlap detection
                if (occupiedPositions.Contains(item.m_gridPos))
                {
                    var now = Time.realtimeSinceStartup;
                    if (now - lastOverlapWarningTime >2f)
                    {
                        lastOverlapWarningTime = now;
                        var other = GetOtherItemAt(inventory, item.m_gridPos.x, item.m_gridPos.y, item);
                        Debug.LogWarning($"{VAInventory.PluginName}: ItemsValidation: Overlap detected - {item.m_shared.m_name} and {other?.m_shared.m_name} at {item.m_gridPos}");
                    }
                    tempItems.Add(item);
                }
                else
                {
                    occupiedPositions.Add(item.m_gridPos);
                }
                // Out of bounds
                if (item.m_gridPos.x <0 || item.m_gridPos.x >= Slots.InventoryWidth ||
                    item.m_gridPos.y <0 || item.m_gridPos.y >= Slots.InventoryHeightFull)
                {
                    var now = Time.realtimeSinceStartup;
                    if (now - lastOutOfBoundsWarningTime >2f)
                    {
                        lastOutOfBoundsWarningTime = now;
                        Debug.LogWarning($"{VAInventory.PluginName}: ItemsValidation: Item {item.m_shared.m_name} out of bounds at {item.m_gridPos}");
                    }
                    tempItems.Add(item);
                }
            }
            // Now process moves after the loop
            foreach (var item in itemsToMove)
            {
                var slot = Slots.GetItemSlot(item);
                if (slot != null && slot.IsActive)
                {
                    var old = item.m_gridPos;
                    item.m_gridPos = slot.GridPosition;
                    if (ConfigManager.IsVerbose)
                        Debug.Log($"[VAInventory] Applied saved slot for {item.m_shared.m_name}: {old} -> {item.m_gridPos}");
                }
            }
            // Move problematic items after the loop. Defer invoking Inventory.OnChanged until
            // after all moves are applied to avoid triggering GUI updates mid-validation.
            bool anyMoved = false;
            foreach (var item in tempItems)
            {
                if (!PutIntoFirstEmptySlot(item))
                {
                    if (ConfigManager.IsVerbose)
                        Debug.Log($"[VAInventory] No place found for {item.m_shared.m_name}; scheduling for drop (current pos {item.m_gridPos})");
                    itemsToDrop.Add(item); // collect for dropping
                }
                else
                {
                    anyMoved = true;
                }
            }
            // Do not call InvokeChanged immediately here; schedule it next frame to avoid
            // InventoryGui/InventoryGrid re-entrancy and NullReferenceExceptions.
            if (anyMoved || itemsToDrop.Count >0)
            {
                var player = Player.m_localPlayer;
                // Suppress reacting to the inventory change(s) we are about to invoke
                suppressOnInventoryChanged = true;
                if (player != null)
                {
                    try { player.StartCoroutine(InvokeChangedNextFrame(inventory)); }
                    catch
                    {
                        // fallback: invoke immediately but ensure suppression is cleared afterwards
                        try { InvokeChanged(inventory); }
                        finally { suppressOnInventoryChanged = false; }
                    }
                }
                else
                {
                    try { InvokeChanged(inventory); }
                    finally { suppressOnInventoryChanged = false; }
                }
            }
            tempItems.Clear();
            // Drop items after all moves
            var uniqueItemsToDrop = new HashSet<ItemDrop.ItemData>(itemsToDrop);
            foreach (var item in uniqueItemsToDrop)
            {
                Debug.Log($"[VAInventory] DROPPING (validation): {item.m_shared.m_name} (instanceID={item.GetHashCode()}) at {item.m_gridPos}");
                InventoryInteraction.TryRelocateOrDropItem(inventory, item);
            }
            // Prune obsolete custom data
            foreach (var item in API.GetAllExtraSlotsItems())
                Slots.PruneLastEquippedSlotFromItem(item);
            // Ensure inventory size is updated
            InventoryInteraction.UpdatePlayerInventorySize();
            // Force one final visual update after cleanup
            EquipmentPanel.MarkDirty();
        }

        public static void ValidateSlots()
        {
            // Placeholder - will be used when slot availability changes dynamically
        }

        private static bool PutIntoFirstEmptySlot(ItemDrop.ItemData item)
        {
            if (Slots.TryGetSavedPlayerSlot(item, out Slots.Slot savedSlot) && savedSlot.IsActive && savedSlot.ItemFits(item) && (savedSlot.IsFree || item == savedSlot.Item))
            {
                if (ConfigManager.IsVerbose)
                    Debug.Log($"[VAInventory] PutIntoFirstEmptySlot: using saved slot {savedSlot.ID} at {savedSlot.GridPosition} for {item.m_shared.m_name}");
                item.m_gridPos = savedSlot.GridPosition;
                return true;
            }

            // Prefer equipment or tool slots first
            if (Slots.TryFindFreeEquipmentSlotForItem(item, out Slots.Slot freeEquip))
            {
                if (ConfigManager.IsVerbose)
                    Debug.Log($"[VAInventory] PutIntoFirstEmptySlot: placing {item.m_shared.m_name} into equipment/tool slot {freeEquip.ID} at {freeEquip.GridPosition}");
                item.m_gridPos = freeEquip.GridPosition;
                return true;
            }

            // Search for first empty grid cell (player visible grid space first)
            var occupied = Slots.PlayerInventory.GetAllItemsInGridOrder()
                .Select(i => i.m_gridPos)
                .ToHashSet();

            Vector2i firstEmpty = Slots.emptyPosition;
            for (int y =0; y < Slots.InventoryHeightFull; y++)
            {
                for (int x =0; x < Slots.InventoryWidth; x++)
                {
                    var pos = new Vector2i(x, y);
                    if (!occupied.Contains(pos))
                    {
                        firstEmpty = pos;
                        break;
                    }
                }
                if (firstEmpty != Slots.emptyPosition) break;
            }

            if (firstEmpty != Slots.emptyPosition)
            {
                if (ConfigManager.IsVerbose)
                    Debug.Log($"[VAInventory] PutIntoFirstEmptySlot: placing {item.m_shared.m_name} into first empty grid cell {firstEmpty}");
                item.m_gridPos = firstEmpty;
                return true;
            }

            if (Slots.TryFindFreeSlotForItem(item, out Slots.Slot freeExtra))
            {
                if (ConfigManager.IsVerbose)
                    Debug.Log($"[VAInventory] PutIntoFirstEmptySlot: placing {item.m_shared.m_name} into extra slot {freeExtra.ID} at {freeExtra.GridPosition}");
                item.m_gridPos = freeExtra.GridPosition;
                return true;
            }

            if (Slots.TryMakeFreeSpaceInPlayerInventory(true, out Vector2i space))
            {
                if (ConfigManager.IsVerbose)
                    Debug.Log($"[VAInventory] PutIntoFirstEmptySlot: made free space at {space} for {item.m_shared.m_name}");
                item.m_gridPos = space;
                return true;
            }

            if (ConfigManager.IsVerbose)
                Debug.Log($"[VAInventory] PutIntoFirstEmptySlot: no placement found for {item.m_shared.m_name}");

            return false;
        }

        private static ItemDrop.ItemData GetOtherItemAt(Inventory inventory, int x, int y, ItemDrop.ItemData exclude)
        {
            var items = (List<ItemDrop.ItemData>)fi_inventory_list.GetValue(inventory);
            return items.FirstOrDefault(i => i != exclude && i.m_gridPos.x == x && i.m_gridPos.y == y);
        }

        private static void InvokeChanged(Inventory inventory)
        {
            var onChanged = fi_onChanged.GetValue(inventory) as Action;
            onChanged?.Invoke();
        }

        private static IEnumerator InvokeChangedNextFrame(Inventory inventory)
        {
            yield return new WaitForEndOfFrame();
            try { InvokeChanged(inventory); }
            catch (Exception ex) { if (VerdantsAscent.EquipmentPanel.VerboseLogging) Debug.Log($"[VAInventory] InvokeChangedNextFrame exception: {ex}"); }
            finally { suppressOnInventoryChanged = false; }
        }

        // =============================================================================
        // Harmony Patches
        // =============================================================================

        internal static void OnInventoryChangedRefresh(Player __instance)
        {
            // Skip if this inventory change was caused by our own validation logic
            if (ItemsSlotsValidation.suppressOnInventoryChanged) return;

            if (!Slots.IsValidPlayer(__instance)) return;

            // Update slot grid positions and mark panels dirty
            Slots.UpdateSlotsGridPosition();
            EquipmentPanel.MarkDirty(); // Trigger visual update on next UpdateGui

            // Validate because the inventory actually changed
            ItemsSlotsValidation.MarkDirty();
            ItemsSlotsValidation.ScheduleValidateNextFrame();
        }

        [HarmonyPatch(typeof(Inventory), "MoveAll")]
        private static class Inventory_MoveAll_Validate
        {
            private static void Postfix(Inventory __instance, Inventory fromInventory)
            {
                if (__instance != Slots.PlayerInventory && fromInventory != Slots.PlayerInventory) return;
                MarkDirty();
                ScheduleValidateNextFrame();
            }
        }

        [HarmonyPatch(typeof(TombStone), "EasyFitInInventory")]
        private static class TombStone_EasyFit_Validate
        {
            private static void Postfix(Player player)
            {
                if (!Slots.IsValidPlayer(player)) return;
                MarkDirty();
                ScheduleValidateNextFrame();
            }
        }

        [HarmonyPatch]
        public static class Humanoid_OnEquipUnequip
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Humanoid), "EquipItem");
                yield return AccessTools.Method(typeof(Humanoid), "UnequipItem");
            }

            static void Prefix(Humanoid __instance)
            {
                if (!Slots.IsValidPlayer(__instance)) return;
                MarkDirty();
                ScheduleValidateNextFrame();
            }
        }

    }

    // Temporary API stub (will be removed when we port the full API)
    public static class API
    {
        public static IEnumerable<ItemDrop.ItemData> GetAllExtraSlotsItems()
        {
            return Slots.slots.Where(s => s != null && s.IsActive && s.Item != null).Select(s => s.Item);
        }
    }
}