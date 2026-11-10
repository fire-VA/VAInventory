using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// An item in a worn bag's cells weighs less when the bag has a contents-weight cut (VABackpacks' Featherweight upgrade, or an FRC
    /// bag bonus), and the tooltip says so: the weight line shows the reduced numbers and a line under it names the cut. The same item
    /// in the main inventory keeps its normal tooltip.
    /// </summary>
    [HarmonyPatch]
    internal static class BagWeightTooltip
    {
        private const float NoCut = 0.999f;
        private static readonly Regex Number = new Regex(@"\d+(\.\d+)?", RegexOptions.Compiled);

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var m in typeof(ItemDrop.ItemData).GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "GetTooltip" && m.ReturnType == typeof(string) && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(ItemDrop.ItemData))
                    yield return m;
        }

        /// <summary>The share of its weight an item keeps: below 1 only for an item in a worn bag's cell whose bag has a cut.</summary>
        private static float FactorFor(ItemDrop.ItemData item)
        {
            var player = Player.m_localPlayer;
            if (item == null || player == null || !BackpackBridge.IsAvailable || !BackpackBridge.SlotStorageActive) return 1f;
            if (!player.GetInventory().ContainsItem(item)) return 1f;
            foreach (var slot in Slots.AllSlots())
            {
                if (slot == null || slot.Item != item || !slot.IsBackpackStorageSlot) continue;
                string prefab = Slots.GetBackpackPrefabFromSlot(slot);
                foreach (var bag in BackpackBridge.GetEquippedBackpacks(player))
                {
                    if (bag?.m_dropPrefab == null || bag.m_dropPrefab.name != prefab) continue;
                    var b = BackpackBridge.GetBonus(bag);
                    return b != null && b.Length > 1 ? UnityEngine.Mathf.Clamp01(b[1]) : 1f;
                }
            }
            return 1f;
        }

        private static void Postfix(ItemDrop.ItemData item, ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            string autofill = RefillBridge.TooltipLine(item);   // a Refill-marked cell says what it keeps
            if (!string.IsNullOrEmpty(autofill)) __result += "\n" + autofill;
            float factor = FactorFor(item);
            if (factor >= NoCut) return;
            var lines = __result.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf("$item_weight", System.StringComparison.Ordinal) < 0) continue;
                lines[i] = Number.Replace(lines[i], m => (float.Parse(m.Value, CultureInfo.InvariantCulture) * factor).ToString("0.0", CultureInfo.InvariantCulture));
                lines[i] += $"\n<color=#8fbf6a>Bag weight cut: -{UnityEngine.Mathf.RoundToInt((1f - factor) * 100f)}%</color>";
                __result = string.Join("\n", lines);
                return;
            }
        }
    }
}
