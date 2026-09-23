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
            var bypassSlot = Slots.slots.FirstOrDefault(
                s => s != null && s.IsTeleportBypassSlot && s.IsActive);
            if (bypassSlot != null) bypassPos = bypassSlot.GridPosition;

            foreach (var item in __instance.GetAllItems())
            {
                if (item == null || item.m_shared == null) continue;
                if (item.m_shared.m_teleportable) continue;          // benign
                if (bypassPos.HasValue && item.m_gridPos == bypassPos.Value)
                    continue;                                         // smuggled
                __result = false;
                return false;
            }

            __result = true;
            return false;
        }

        // Cosmetic: hide the red "no teleport" overlay on items sitting
        // in the bypass slot, so the player gets visual confirmation that
        // the smuggled item is actually being smuggled. Without this,
        // ore in the bypass slot still has the red diagonal cross over
        // its icon, which is confusing.
        [HarmonyPatch(typeof(InventoryGrid), "UpdateGui")]
        [HarmonyPostfix]
        private static void InventoryGrid_UpdateGui_Postfix()
        {
            // Disabled by default — InventoryGrid's element list is private
            // and varies between Valheim builds. Hooking it cleanly is a
            // follow-up. The bypass logic itself works regardless of this
            // visual cue.
        }
    }
}
