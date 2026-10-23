using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// What sits in the coin pouch (the visible
    /// CoinPurse slot + its hidden CurrencyVault cells) adds nothing to the player's carry weight, and its tooltip says so. Only the
    /// weight SUM and the tooltip change; the item's own m_weight is untouched, so the same stack in the grid, dropped or in a chest
    /// weighs what vanilla says.
    /// </summary>
    internal static class CoinPouchWeightless
    {
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VAInventory.Panel");
        private static float s_lastLogged = -1f;

        internal static bool InPouch(ItemDrop.ItemData item) =>
            item != null && Slots.AllSlots().Any(s => s != null && (s.IsCoinPurseSlot || s.IsCurrencyVaultSlot) && ReferenceEquals(s.Item, item));

        internal static float PouchWeight()
        {
            float w = 0f;
            foreach (var s in Slots.AllSlots())
            {
                if (s == null || !(s.IsCoinPurseSlot || s.IsCurrencyVaultSlot)) continue;
                var it = s.Item;
                if (it != null) w += it.GetWeight();
            }
            return w;
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetTotalWeight))]
        private static class TotalWeight
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(Inventory __instance, ref float __result)
            {
                if (__instance == null || __instance != Slots.PlayerInventory) return;
                float pouch = PouchWeight();
                if (Mathf.Abs(pouch - s_lastLogged) > 0.001f) { s_lastLogged = pouch; Log.LogDebug($"[VAInventory] coin pouch weightless: {pouch:0.##} kg of pouch currency excluded from the carry weight"); }
                if (pouch > 0f) __result = Mathf.Max(0f, __result - pouch);
            }
        }

        // vanilla's tooltip weight line: "$item_weight: <color=orange>0.1 (115.1 $item_total)</color>"
        private static readonly Regex WeightLine = new Regex(@"(\$item_weight:\s*(?:<color=[^>]+>)?)[^\n<]*((?:</color>)?)", RegexOptions.Compiled);

        [HarmonyPatch]
        private static class Tooltip
        {
            private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
                AccessTools.GetDeclaredMethods(typeof(ItemDrop.ItemData)).Where(m => m.Name == "GetTooltip" && m.IsStatic && m.ReturnType == typeof(string)
                    && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(ItemDrop.ItemData)).Cast<System.Reflection.MethodBase>();

            private static void Postfix(object[] __args, ref string __result)
            {
                if (string.IsNullOrEmpty(__result) || __args == null || __args.Length == 0 || !(__args[0] is ItemDrop.ItemData item) || !InPouch(item)) return;
                __result = WeightLine.Replace(__result, "${1}0 (in the coin pouch)${2}", 1);
            }
        }
    }
}
