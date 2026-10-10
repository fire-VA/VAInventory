// One Harmony hook per vanilla method. Every VAInventory subsystem that needs a vanilla method
// contributes a plain static call listed here, in the order it runs. Nothing else in the mod may
// declare a [HarmonyPatch] on a method that appears in this file.
//
// Order below is the order the separate patch classes ran before consolidation, read off the
// deployed 1.0.19 DLL with Tools\HookAudit\TypeOrder (harmony.PatchAll patches in TypeDef order).
//
// HarmonyX 2.9 note, verified against the BepInEx core 0Harmony.dll rather than assumed: a prefix
// returning false does NOT skip the remaining prefixes, and runOriginal is sticky-false. So the
// separate classes were side-effect coupled, not order-fragile. Where that coupling was load
// bearing it is reproduced literally; where it was a latent bug it is called out on the hook.

using System;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent;

namespace VerdantsAscent.Managers
{
    [HarmonyPatch(typeof(InventoryGrid), "UpdateGui")]
    internal static class InventoryGrid_UpdateGui_Hook
    {
        // Run-all-then-veto, deliberately. ExtendGrid must build the missing elements BEFORE the
        // safety check inspects m_elements, and EquipmentPanel's count capture must pair with its
        // postfix even on a suppressed frame or the postfix reads __state 0 and marks dirty forever.
        private static bool Prefix(InventoryGrid __instance, ref int __state)
        {
            InventoryGridExtraSlots.ExtendGrid(__instance);
            bool runOriginal = SafeGuiPatches.AllowUpdateGui(__instance);
            __state = EquipmentPanel.CaptureGridElementCount(__instance);
            return runOriginal;
        }

        private static void Postfix(InventoryGrid __instance, int __state)
            => EquipmentPanel.AfterUpdateGui(__instance, __state);

        private static Exception Finalizer(InventoryGrid __instance, Exception __exception)
            => SafeGuiPatches.UpdateGuiFinalizer(__instance, __exception);
    }

    [HarmonyPatch(typeof(InventoryGui), "Show")]
    internal static class InventoryGui_Show_Hook
    {
        private static void Prefix() => InventoryGui_UpdateContainer_Patch.Reset();

        private static void Postfix(InventoryGui __instance)
        {
            InventoryGuiBackgrounds.Apply(__instance);
            if (!Player.m_localPlayer) return;
            // FIRST: it builds the slot -> cell map that UpdatePanel then parents its elements into. Run it
            // after and every element falls back to the legacy positioning because the map is still empty.
            UnifiedScreen.Apply();
            EquipmentPanel.UpdatePanel();
            EquipmentPanel.UpdateExtraSlotsPanel();
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "Update")]
    internal static class InventoryGui_Update_Hook
    {
        private static void Postfix()
        {
            if (Player.m_localPlayer) Slots.EvictBagFromBypass();   // 1.0.93: open or not, ~1/s
            if (!Player.m_localPlayer || !InventoryGui.IsVisible()) return;
            EquipmentPanel.UpdateEquipmentBackground();
            UnifiedScreen.Tick();
        }

        private static Exception Finalizer(Exception __exception)
            => SafeGuiPatches.InventoryGuiUpdateFinalizer(__exception);
    }

    [HarmonyPatch(typeof(InventoryGui), "Hide")]
    internal static class InventoryGui_Hide_Hook
    {
        private static void Prefix()
        {
            BackpackStorageSync.SaveEquippedBackpackContents(removeItems: false);
            InventoryGui_UpdateContainer_Patch.Reset();
            UnifiedScreen.Hide();
        }
    }

    // Short-circuits on the first refusal. Before consolidation all three ran on every drop, so a
    // coin deposit that answered "handled, stop" still had the grid validators run behind it and
    // could have its drag state cleared twice.
    [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem))]
    internal static class InventoryGrid_DropItem_Hook
    {
        private static bool Prefix(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, int amount, Vector2i pos)
        {
            if (!CoinPurseStackPatch.DropItemDepositCoins(__instance, fromInventory, item, amount, pos)) return false;
            if (!InventoryInteraction.DropItemBlockInvalid(__instance, item, pos)) return false;
            return InventoryInteraction.DropItemRobust(fromInventory, item, pos);
        }
    }

    // Short-circuits on the first refusal. Before consolidation, right-clicking coins already in the
    // purse made CoinPurse answer "this is the purse, do nothing" and TransferOrDrop then evacuated
    // the purse into the main inventory anyway.
    [HarmonyPatch(typeof(InventoryGrid), "OnRightDown")]
    internal static class InventoryGrid_OnRightDown_Hook
    {
        private static bool Prefix(InventoryGrid __instance, UIInputHandler element)
        {
            if (!CoinPurseStackPatch.OnRightDownDepositCoins(__instance, element)) return false;
            return InventoryInteraction.OnRightDownTransferOrDrop(__instance, element);
        }
    }

    // Priority.High is kept against OTHER mods' postfixes: the visible-area correction has to land
    // before anything upgrades the result. Ordering against the coin purse is now positional.
    [HarmonyPatch(typeof(Inventory), "CanAddItem", [typeof(ItemDrop.ItemData), typeof(int)])]
    internal static class Inventory_CanAddItem_Hook
    {
        [HarmonyPriority(Priority.High)]
        private static void Postfix(Inventory __instance, ItemDrop.ItemData item, int stack, ref bool __result)
        {
            InventoryInteraction.CanAddItemCheckExtraSlots(__instance, item, stack, ref __result);
            CoinPurseStackPatch.CanAddItemCoinsFitPurse(__instance, item, ref __result);
        }
    }

    // The Phase 0 audit recorded this as two distinct overloads. It is not: Inventory.cs:98 is a
    // single AddItem(ItemDrop.ItemData), and the coin-purse prefix and the extra-slot postfix were
    // both on it.
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), [typeof(ItemDrop.ItemData)])]
    internal static class Inventory_AddItem_Hook
    {
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
            => CoinPurseStackPatch.AddItemDepositCoins(__instance, item, ref __result);

        private static void Postfix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
            => InventoryInteraction.AddItemTryExtraSlot(__instance, item, ref __result);
    }

    [HarmonyPatch(typeof(Player), "OnInventoryChanged")]
    internal static class Player_OnInventoryChanged_Hook
    {
        private static void Postfix(Player __instance)
        {
            InventoryInteraction.OnInventoryChangedMarkDirty(__instance);
            ItemsSlotsValidation.OnInventoryChangedRefresh(__instance);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
    internal static class Player_OnSpawned_Hook
    {
        private static void Prefix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            PlayerInventoryRows.AdoptSavedLayout(__instance);
        }

        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            CoinPurseStackPatch.RestorePurseStackOnSpawn(__instance);
            InventoryInteraction.UpdatePlayerInventorySize();
        }
    }
}
