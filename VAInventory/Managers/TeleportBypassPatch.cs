// TeleportBypassPatch — let one item slot in the equipment panel
// carry an otherwise non-teleportable item (ore, ingots, etc.) past
// vanilla's teleport-gating check.
//
// Vanilla flow (Inventory.cs:896):
//   public bool IsTeleportable()
//   {
//       if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.TeleportAll)) return true;
//       foreach (var item in m_inventory)
//           if (!item.m_shared.m_teleportable) return false;
//       return true;
//   }
//
// The patch reimplements the loop with one extra rule: a single item
// sitting in the TeleportBypass slot is allowed through. Other
// non-teleportable items still block as normal — the slot is a
// "smuggler's pocket", not a global override.
//
// Only applies to the local player's inventory; server-side teleport
// rules and other players' inventories are unaffected.

using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    [HarmonyPatch]
    internal static class TeleportBypassPatch
    {
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.IsTeleportable))]
        [HarmonyPrefix]
        private static bool Inventory_IsTeleportable_Prefix(Inventory __instance, ref bool __result)
        {
            // Honor the vanilla TeleportAll global key (admin override).
            if (ZoneSystem.instance != null
                && ZoneSystem.instance.GetGlobalKey(GlobalKeys.TeleportAll))
            {
                __result = true;
                return false;
            }

            // Only the local player has the slot system. For other
            // inventories (companions, chests, server-side checks), defer
            // to vanilla so we don't accidentally let chests teleport ores.
            var local = Player.m_localPlayer;
            if (local == null || __instance != local.GetInventory())
            {
                return true;     // run vanilla
            }

            // Find the bypass slot's grid position. Slot may be null
            // (config disables, or extra-rows config hides it).
            Vector2i? bypassPos = null;
            var bypassSlot = Slots.AllSlots().FirstOrDefault(
                s => s != null && s.IsTeleportBypassSlot && s.IsActive);
            if (bypassSlot != null) bypassPos = bypassSlot.GridPosition;

            foreach (var item in __instance.GetAllItems())
            {
                if (item == null || item.m_shared == null) continue;
                if (item.m_shared.m_teleportable) continue;          // benign
                if (bypassPos.HasValue && item.m_gridPos == bypassPos.Value)
                    continue;                                         // smuggled
                __result = false;
                LogBlock(item);
                return false;
            }

            __result = true;
            lastBlock = null;
            return false;
        }

        // 1.0.87 ([lead]: "teleportable False" three rounds running): name what blocks, once per change, at Debug.
        static string lastBlock;

        static void LogBlock(ItemDrop.ItemData item)
        {
            var slot = Slots.AllSlots().FirstOrDefault(s => s != null && s.GridPosition == item.m_gridPos);
            string where = slot != null ? $"slot {slot.ID}"
                : item.m_gridPos.y < Slots.InventoryHeightPlayer ? $"main grid {item.m_gridPos}" : $"hidden row {item.m_gridPos} (bag section / extra rows)";
            string line = $"teleport blocked by {item.m_dropPrefab?.name ?? item.m_shared.m_name} x{item.m_stack} at {where}";
            if (line == lastBlock) return;
            lastBlock = line;
            EquipmentPanel.PanelLog?.LogDebug(line);
        }

    }
}
