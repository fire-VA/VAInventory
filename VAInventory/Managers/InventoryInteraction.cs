using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VerdantsAscent; // Assuming Slots is in this namespace
using VerdantsAscent.Managers;
using VerdantsAscent.Managers.HotBars;
namespace VerdantsAscent
{
    public static class InventoryInteraction
    {
        // When true, Humanoid_UnequipItem postfix will skip relocating unequipped items to main inventory.
        public static bool suppressUnequipReturnToInventory = false;
        // Reflection for private Inventory fields
        private static readonly FieldInfo fi_inventory_height = AccessTools.Field(typeof(Inventory), "m_height");
        private static readonly FieldInfo fi_inventory_width = AccessTools.Field(typeof(Inventory), "m_width");
        private static readonly FieldInfo fi_inventory_list = AccessTools.Field(typeof(Inventory), "m_inventory");
        private static readonly MethodInfo mi_inventory_changed = AccessTools.Method(typeof(Inventory), "Changed", new[] { typeof(bool), typeof(bool) });
        // Reflection for Player.m_inventory
        private static readonly FieldInfo fi_player_inventory = AccessTools.Field(typeof(Player), "m_inventory");
        private static readonly FieldInfo fi_onChanged = AccessTools.Field(typeof(Inventory), "m_onChanged");
        // Reflection for InventoryGui drag fields
        private static readonly FieldInfo fi_dragItem = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        private static readonly FieldInfo fi_dragInventory = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
        public static void UpdatePlayerInventorySize()
        {
            if (Slots.CurrentPlayer == null) return;
            var inventory = Slots.PlayerInventory;
            if (inventory == null) return;
            int currentHeight = (int)fi_inventory_height.GetValue(inventory);
            if (currentHeight != Slots.InventoryHeightFull)
            {
                Debug.Log($"{VAInventory.PluginName}: Player inventory height changed {currentHeight} -> {Slots.InventoryHeightFull}");
                fi_inventory_height.SetValue(inventory, Slots.InventoryHeightFull);
                inventory.m_onChanged?.Invoke();
            }
            var tombstone = Slots.CurrentPlayer.m_tombstone?.GetComponent<Container>();
            if (tombstone != null)
                tombstone.m_height = Mathf.Max(tombstone.m_height, Slots.InventoryHeightFull);
        }
        public static float GetItemWeightFactor() => 1f;
        // =============================================================================
        // Harmony Patches
        // =============================================================================
        [HarmonyPatch(typeof(Player), "Awake")]
        private static class Player_Awake_SetInventoryHeight
        {
            private static void Postfix(Player __instance)
            {
                var inv = (Inventory)fi_player_inventory.GetValue(__instance);
                if (inv != null)
                    fi_inventory_height.SetValue(inv, Slots.InventoryHeightFull);
            }
        }
        [HarmonyPatch(typeof(Player), "Update")]
        private static class Player_Update_UpdateInventoryHeight
        {
            private static Container tombstoneContainer;
            private static void Postfix(Player __instance)
            {
                // Only run for the local player. The previous version ran reflection on
                // every networked Player every frame, which scaled poorly with player
                // count. Inventory height also only needs to be re-applied when it has
                // actually drifted (other patches restore it after their own use).
                if (__instance != Player.m_localPlayer) return;
                var inv = (Inventory)fi_player_inventory.GetValue(__instance);
                if (inv != null)
                {
                    int target = Slots.InventoryHeightFull;
                    int current = (int)fi_inventory_height.GetValue(inv);
                    if (current != target)
                        fi_inventory_height.SetValue(inv, target);
                }
                if (tombstoneContainer == null)
                    tombstoneContainer = __instance.m_tombstone?.GetComponent<Container>();
                if (tombstoneContainer != null && tombstoneContainer.m_height != Slots.InventoryHeightFull)
                    tombstoneContainer.m_height = Slots.InventoryHeightFull;
            }
        }
        [HarmonyPatch(typeof(Inventory), "FindEmptySlot")]
        private static class Inventory_FindEmptySlot_FindAppropriateSlot
        {
            // Prefix temporarily shrinks m_height so FindEmptySlot only scans the visible
            // player area. We MUST always restore even if the original method or another
            // patch throws — otherwise the inventory is stuck at the smaller height, and
            // items in the extended rows get flagged out-of-bounds and dropped. Use
            // __state + Finalizer for guaranteed restore.
            private static void Prefix(Inventory __instance, out bool __state)
            {
                __state = (__instance == Slots.PlayerInventory);
                if (!__state) return;
                fi_inventory_height.SetValue(__instance, Slots.InventoryHeightPlayer);
            }
            private static void Postfix(ref Vector2i __result, bool __state)
            {
                if (!__state) return;
                if (__result == Slots.emptyPosition)
                {
                    if (Slots.TryFindFreeSlotForItem(null, out Slots.Slot slot))
                        __result = slot.GridPosition;
                }
            }
            private static void Finalizer(Inventory __instance, bool __state)
            {
                if (!__state) return;
                try { fi_inventory_height.SetValue(__instance, Slots.InventoryHeightFull); }
                catch { }
            }
        }
        [HarmonyPatch(typeof(Inventory), "HaveEmptySlot")]
        private static class Inventory_HaveEmptySlot_CheckExtraSlots
        {
            private static void Postfix(Inventory __instance, ref bool __result)
            {
                if (__instance != Slots.PlayerInventory) return;
                __result = __instance.GetEmptySlots() > 0 || Slots.CapacitySlots().Any(s => s != null && s.IsActive && s.IsFree);
            }
        }
        [HarmonyPatch(typeof(Inventory), "GetEmptySlots")]
        private static class Inventory_GetEmptySlots_CountExtra
        {
            private static void Postfix(Inventory __instance, ref int __result)
            {
                if (__instance != Slots.PlayerInventory) return;
                int width = (int)fi_inventory_width.GetValue(__instance);
                var list = (List<ItemDrop.ItemData>)fi_inventory_list.GetValue(__instance);
                // Count only items occupying the VISIBLE grid — items parked in custom slots
                // live in the same list but sit in extended rows, and subtracting them here
                // made a half-equipped player look fuller than they are.
                int visibleUsed = 0;
                foreach (var existing in list)
                {
                    if (existing.m_gridPos.y < Slots.InventoryHeightPlayer)
                        visibleUsed++;
                }
                __result = Mathf.Max(0, Slots.InventoryHeightPlayer * width - visibleUsed) +
                           Slots.CapacitySlots().Count(s => s != null && s.IsActive && s.IsFree);
            }
        }
            // Vanilla's CanAddItem math uses the FULL extended m_height, so every empty
            // reserved cell (equipment/ammo/food/quick slot rows) counts as generic free
            // space — a completely full visible inventory still answers "yes". Player.AutoPickup
            // gates its attraction on exactly this call, so loot got pulled to the player,
            // AddItem (which DOES respect the slots) refused it, and the drop hovered and
            // followed forever. Recompute against the VISIBLE area only, then let the typed
            // custom slots opt items in that actually fit them.
            // Runs before the coin-purse contribution so its false→true upgrade still applies
            // after this correction; the hook orders the two.
            internal static void CanAddItemCheckExtraSlots(Inventory __instance, ItemDrop.ItemData item, int stack, ref bool __result)
            {
                if (__instance != Slots.PlayerInventory) return;
                if (stack <= 0) stack = item.m_stack;
                int width = (int)fi_inventory_width.GetValue(__instance);
                var list = (List<ItemDrop.ItemData>)fi_inventory_list.GetValue(__instance);
                int visibleUsed = 0;
                foreach (var existing in list)
                {
                    if (existing.m_gridPos.y < Slots.InventoryHeightPlayer)
                        visibleUsed++;
                }
                int freeVisible = Mathf.Max(0, Slots.InventoryHeightPlayer * width - visibleUsed);
                // Stack space scans the whole list on purpose: AddItem's stacking loop also
                // tops up matching stacks sitting in custom slots (ammo in the ammo slot).
                __result = __instance.FindFreeStackSpace(item.m_shared.m_name, item.m_worldLevel)
                    + freeVisible * item.m_shared.m_maxStackSize >= stack;
                if (!__result && stack <= item.m_shared.m_maxStackSize && Slots.TryFindFreeSlotForItem(item, out _))
                    __result = true;
            }
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.StackAll))]
        private static class Inventory_StackAll_KeepSlotItems
        {
            // StackAll copies fromInventory's whole backing list and moves anything the container
            // already holds by name, excluding only equipped items — so an UNEQUIPPED item parked in a
            // quick, food, ammo or custom slot is eligible, and one press of stack-all at a chest
            // empties those slots. Hide the slot items for the duration. Configured extra rows are part
            // of InventoryHeightPlayer, so they stay stackable; only real slots are held back.
            // Restore is a Finalizer so a throw anywhere in the chain cannot leave items detached, and
            // it re-fires Changed because vanilla already fired its own while they were hidden, which
            // would otherwise leave the weight and the grid reporting the hidden state.
            private static void Prefix(Inventory fromInventory, out List<ItemDrop.ItemData> __state)
            {
                __state = null;
                if (fromInventory == null || fromInventory != Slots.PlayerInventory) return;
                if (fi_inventory_list.GetValue(fromInventory) is not List<ItemDrop.ItemData> list) return;
                List<ItemDrop.ItemData> held = null;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (!Slots.IsItemInSlot(list[i])) continue;
                    held ??= new List<ItemDrop.ItemData>();
                    held.Add(list[i]);
                    list.RemoveAt(i);
                }
                __state = held;
            }
            private static void Finalizer(Inventory fromInventory, List<ItemDrop.ItemData> __state)
            {
                if (__state == null) return;
                try
                {
                    if (fi_inventory_list.GetValue(fromInventory) is not List<ItemDrop.ItemData> list) return;
                    for (int i = __state.Count - 1; i >= 0; i--)
                    {
                        if (!list.Contains(__state[i])) list.Add(__state[i]);
                    }
                    mi_inventory_changed?.Invoke(fromInventory, new object[] { false, false });
                }
                catch { }
            }
        }
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        public static class Humanoid_EquipItem_AutoMoveToSlot
        {
            /// <summary>Set by the panel's drop-to-equip: the item already sits in the slot the player chose.</summary>
            internal static bool KeepDroppedSlot;

            private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool __result)
            {
                if (KeepDroppedSlot || !__result ||!Slots.IsValidPlayer(__instance) || item == null) return;
                // Only move items that are valid for a custom slot (armor, utility, ammo, food, etc.)
                if (!Slots.IsEquipmentSlotItem(item) && !Slots.GetAmmoSlots().Any(s => s.ItemFits(item)) && !Slots.GetFoodSlots().Any(s => s.ItemFits(item)))
                {
                    // Do not move weapons or items without a dedicated slot
                    return;
                }
                var inv = __instance.GetInventory();
                var list = (List<ItemDrop.ItemData>)AccessTools.Field(typeof(Inventory), "m_inventory").GetValue(inv);
                // Find first valid dedicated equipment-panel slot. Do not limit this
                // to the old first 12 slots; row 4 contains Backpack/CoinPurse/Ring.
                var slot = Slots.GetEquipmentSlots()
                    .FirstOrDefault(s => s != null
                                      && !s.IsMiscSlot
                                      && !s.IsBackpackStorageSlot
                                      && s.ItemFits(item)
                                      && (s.IsFree || s.Item == item));
                if (slot != null)
                {
                    list.Remove(item);
                    item.m_gridPos = slot.GridPosition;
                    if (!list.Contains(item)) list.Add(item);
                    inv.m_onChanged?.Invoke();
                    Debug.Log($"[VAInventory] Equipped {item.m_shared.m_name} to slot {slot?.Name ?? "NONE"} at {item.m_gridPos}");
                    EquipmentPanel.RefreshPanelOnly();
                    ToolHotBar.MarkDirty();
                    AmmoHotBar.MarkDirty();
                    FoodHotBar.MarkDirty();
                    return;
                }
                Debug.LogWarning($"[VAInventory] No valid equipment slot found for {item.m_shared.m_name}");
            }
        }
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
        public static class Humanoid_UnequipItem_ReturnToInventory
        {
            // Only an item that WAS equipped is moved out ([classes-ui] 10-07: vanilla UnequipItem is a no-op on an item that
            // isn't worn, but this postfix still moved it, so an UnequipItem call on a Pet-slot item - never m_equipped by
            // agreement - "Returned unequipped Pocket Bonemaw from slot" and emptied the Pet slot).
            private static void Prefix(Humanoid __instance, ItemDrop.ItemData item, out bool __state) =>
                __state = item != null && __instance != null && __instance.IsItemEquiped(item);

            private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool __state)
            {
             if (suppressUnequipReturnToInventory) return;
             if (!__state) return;
             if (!Slots.IsValidPlayer(__instance) || item == null) return;
             if (!Slots.IsItemInSlot(item)) return;

             var inv = __instance.GetInventory();
             var oldPos = item.m_gridPos;

             // If the unequipped item belongs to a tool hotbar slot, keep it at that grid position
             // and ensure it remains present in the inventory list. This preserves quickslot behavior
             // where equip/unequip does not migrate the item into the main inventory.
             var slot = Slots.GetItemSlot(item);
             // FiresRPGClasses' bar slots HOLD their weapon and off hand: switching bars unequips the old pair,
             // which must stay in its bar slot, not go back to the main inventory.
             // The Torch slot holds its torch the same way: putting it away leaves it in the slot.
             // The Pet slot holds its pet: the slot itself is "out" (FiresNPCs), so nothing moves it but the player.
             if (slot != null && (slot.IsClassBarSlot || slot.IsTorchSlot || slot.IsPetSlot)) return;
             if (slot != null && Slots.toolHotbarSlots != null && Slots.toolHotbarSlots.Contains(slot))
             {
               try
               {
                 var list = (List<ItemDrop.ItemData>)fi_inventory_list.GetValue(inv);
                 item.m_gridPos = slot.GridPosition;
                 if (!list.Contains(item)) list.Add(item);
                 inv.m_onChanged?.Invoke();
                 Debug.Log($"[VAInventory] Unequipped '{item.m_shared.m_name}' from tool hotbar slot at {oldPos} and kept at {item.m_gridPos}");
                 EquipmentPanel.RefreshPanelOnly();
                  ToolHotBar.MarkDirty();
               }
               catch (Exception ex)
               {
                 Debug.Log($"[VAInventory] Error handling unequip for tool hotbar slot: {ex}");
               }
               return;
             }
             
             // Default behavior for equipment-panel slots: attempt to move the item back to the main inventory,
             // or drop it if no space is available.
             inv.RemoveItem(item);
                if (Slots.TryMakeFreeSpaceInPlayerInventory(true, out Vector2i newPos))
                {
                    item.m_gridPos = newPos;
                    var list = (List<ItemDrop.ItemData>)fi_inventory_list.GetValue(inv);
                    list.Add(item);
                    Slots.ClearCachedItems();   // the slot reads free at once (a drop this frame saw the old occupant)
                    inv.m_onChanged?.Invoke();
                    Debug.Log($"[VAInventory] Returned unequipped {item.m_shared.m_name} from slot at {oldPos} to inventory at {item.m_gridPos}");
                    EquipmentPanel.RefreshPanelOnly();
                }
                else
                {
                    // No space: drop the item instead of forcing to (0,0)
                    __instance.DropItem(inv, item, item.m_stack);
                    Debug.Log($"[VAInventory] Dropped unequipped {item.m_shared.m_name} from slot at {oldPos} (no space in inventory)");
                }
                ToolHotBar.MarkDirty();
              AmmoHotBar.MarkDirty();
              FoodHotBar.MarkDirty();
            }
        }
        internal static bool DropItemBlockInvalid(InventoryGrid __instance, ItemDrop.ItemData item, Vector2i pos)
 {
 if (__instance != InventoryGui.instance.m_playerGrid) return true;
 // Block drop if grid position is hidden by config (row, column, or equipment panel)
 if (Slots.IsGridPositionHidden(pos))
 {
 return false; // block drop
 }
 if (Slots.IsGridPositionASlot(pos))
 {
 var slot = Slots.GetSlotInGrid(pos);
 if (slot != null)
 {
 if (item != null && !slot.ItemFits(item))
 {
 return false; // block drop
 }
 }
 }
 return true;
 }
        private static void InvokeChanged(Inventory inventory)
        {
            var onChanged = fi_onChanged.GetValue(inventory) as Action;
            onChanged?.Invoke();
        }
            internal static void AddItemTryExtraSlot(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
            {
                if (__instance != Slots.PlayerInventory || __result) return;
                if (Slots.TryFindFreeSlotForItem(item, out Slots.Slot slot))
                {
                    item.m_gridPos = slot.GridPosition;
                    var list = (List<ItemDrop.ItemData>)fi_inventory_list.GetValue(__instance);
                    list.Add(item);
                    InvokeChanged(__instance);
                    __result = true;
                }
            }
            internal static void OnInventoryChangedMarkDirty(Player __instance)
            {
                if (!Slots.IsValidPlayer(__instance)) return;
                // Mark dirty and defer full validation to InventoryGrid.UpdateGui postfix
                // to avoid performing potentially collection-modifying operations inside
                // the Player.OnInventoryChanged call chain (which can be triggered
                // during UI event handlers).
                ItemsSlotsValidation.MarkDirty();
 }
            internal static bool OnRightDownTransferOrDrop(InventoryGrid __instance, UIInputHandler element)
            {
                // Only handle the player's inventory grid
                if (__instance != InventoryGui.instance?.m_playerGrid) return true;

                var player = Player.m_localPlayer;
                if (player == null) return true;

                // Get m_elements list via reflection
                var elementsList = (IList)AccessTools.Field(typeof(InventoryGrid), "m_elements").GetValue(__instance);
                if (elementsList == null || elementsList.Count == 0) return true;

                // Find the matching element by GameObject. This used to reflect m_go off the runtime element
                // type; 1.0 deleted that field (InventoryElement is now a MonoBehaviour, so the GameObject is
                // intrinsic), which made AccessTools.Field return null and the GetValue below throw.
                int index = -1;
                for (int i = 0; i < elementsList.Count; i++)
                {
                    if (elementsList[i] is InventoryElement elem && elem.gameObject == element.gameObject)
                    {
                        index = i;
                        break;
                    }
                }

                if (index == -1) return true; // Not found

                // Calculate grid position from index
                int width = __instance.GetInventory().GetWidth();
                Vector2i pos = new(index % width, index / width);

                ItemDrop.ItemData item = __instance.GetInventory().GetItemAt(pos.x, pos.y);
                if (item == null) return true;

                // Only handle our custom/extra slots
                if (Slots.IsGridPositionASlot(pos))
                {
                    var inventory = player.GetInventory();

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
                    return false; // Block vanilla right-click
                }

                return true;
            }
        /// <summary>
        /// Tries to relocate the item to a valid inventory slot or drops it if no space is available.
        /// </summary>
        public static void TryRelocateOrDropItem(Inventory inventory, ItemDrop.ItemData item)
        {
            if (inventory == null || item == null) return;
            var player = Player.m_localPlayer;
            if (player == null) return;

            // Only proceed if item is still in inventory
            if (!inventory.ContainsItem(item)) return;

            if (Slots.TryMakeFreeSpaceInPlayerInventory(true, out Vector2i freePos))
            {
                inventory.RemoveItem(item);
                item.m_gridPos = freePos;
                inventory.AddItem(item); // Use official method
                inventory.m_onChanged?.Invoke();
                Debug.Log($"[VAInventory] Relocated '{item.m_shared.m_name}' to inventory at {freePos.x},{freePos.y}");
            }
            else
            {
                Debug.Log($"[VAInventory] Dropping '{item.m_shared.m_name}' (no space in inventory)");
                player.DropItem(inventory, item, item.m_stack);
            }
        }
        public static void ForceDropItemOnGround(Inventory inventory, ItemDrop.ItemData item)
        {
            var player = Player.m_localPlayer;
            if (player == null || inventory == null || item == null) return;

            // Collect all item data before removal (item reference is enough for DropItem)
            int stack = item.m_stack;

            // Remove from inventory if present
            if (inventory.ContainsItem(item))
                inventory.RemoveItem(item);

            // Use ItemDrop.DropItem to properly spawn the item in the world
            var dropPos = player.transform.position + player.transform.forward + Vector3.up *1.0f;
            var dropRot = Quaternion.identity;
            var dropped = ItemDrop.DropItem(item, stack, dropPos, dropRot);
            if (dropped != null)
            {
                Debug.Log($"[VAInventory] Force-dropped '{item.m_shared.m_name}' x{stack} at {dropPos}");
            }
            else
            {
                Debug.LogWarning($"[VAInventory] Could not drop item '{item.m_shared.m_name}' at {dropPos}");
            }
        }
        // Robust drop/move validation (ExtraSlots-style)
        public static bool PassDropItem(Inventory fromInventory, ItemDrop.ItemData item, Vector2i pos)
        {
            // Block if dropping a custom slot item into a container
            if (fromInventory != Slots.PlayerInventory && Slots.IsGridPositionASlot(item.m_gridPos))
                return false;
            // Block if dropping into a custom slot and item doesn't fit
            var slot = Slots.GetSlotInGrid(pos);
            if (slot != null && !slot.ItemFits(item))
                return false;
            // Block if dropping out-of-bounds
            if (pos.y >= Slots.InventoryHeightFull || pos.x >= Slots.InventoryWidth || pos.x <0 || pos.y <0)
                return false;
            return true;
        }
        internal static bool DropItemRobust(Inventory fromInventory, ItemDrop.ItemData item, Vector2i pos)
 {
  bool allowed = InventoryInteraction.PassDropItem(fromInventory, item, pos);
 if (!allowed)
 {
 VerdantsAscent.EquipmentPanel.ClearDragState();
 }
 return allowed;
 }
        // Harmony patch for InventoryGui.OnSelectedItem
        // Run AFTER FiresRPGmaker's prefix so its dressing-room / companion drop handlers
        // get first crack at consuming the drop. If they return false, our grid validation
        // never runs (correct — they handled the drop entirely). If they return true, we
        // run and validate the grid position.
        [HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
 public static class InventoryGui_OnSelectedItem_Robust
 {
 [HarmonyPriority(Priority.LowerThanNormal)]
 private static bool Prefix(Vector2i pos)
 {
                if (fi_dragItem.GetValue(InventoryGui.instance) is not ItemDrop.ItemData dragItem || fi_dragInventory.GetValue(InventoryGui.instance) is not Inventory dragInventory)
                    return true;
                bool allowed = InventoryInteraction.PassDropItem(dragInventory, dragItem, pos);
 if (!allowed)
 {
 VerdantsAscent.EquipmentPanel.ClearDragState();
 }
 return allowed;
 }
 }
    }
                
}