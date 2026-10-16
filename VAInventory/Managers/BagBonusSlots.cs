using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // 1.0.120 ([fgn] FRC section 15, VABackpacks 1.0.49): with VAInventory a bag's contents are its backpack-storage cells in the
    // player grid, so two of the bag bonuses are applied here, where those cells are known (VABackpacks applies them itself in
    // legacy mode, and carry weight in both). Per worn bag, from BackpackApi.GetBonus (null without FRC: nothing changes):
    //   contentsWeightFactor  the weight of the items in that bag's cells times the factor (Inventory.GetTotalWeight)
    //   loadedSpeed           added to the movement modifier while that bag's cells hold anything
    // Worked out once a frame.
    internal static class BagBonusSlots
    {
        private static int s_frame = -1;
        private static float s_weightCut, s_speed;
        private static string s_lastLine;
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VAInventory.Panel");

        private static void Refresh(Player player)
        {
            if (Time.frameCount == s_frame) return;
            s_frame = Time.frameCount;
            s_weightCut = 0f; s_speed = 0f;
            if (player == null || !BackpackBridge.IsAvailable || !BackpackBridge.SlotStorageActive || !BackpackBridge.HasBonus) return;
            var bags = BackpackBridge.GetEquippedBackpacks(player);
            if (bags.Count == 0) return;
            var parts = new System.Collections.Generic.List<string>();
            foreach (var bag in bags)
            {
                var b = BackpackBridge.GetBonus(bag);
                if (b == null || bag?.m_dropPrefab == null) continue;
                string prefab = bag.m_dropPrefab.name;
                float weight = 0f; int items = 0;
                foreach (var slot in Slots.AllSlots())
                {
                    if (slot == null || !slot.IsBackpackStorageSlot || Slots.GetBackpackPrefabFromSlot(slot) != prefab) continue;
                    var it = slot.Item;
                    if (it == null) continue;
                    items++;
                    weight += it.GetWeight();
                }
                float factor = Mathf.Clamp01(b[1]);
                s_weightCut += weight * (1f - factor);
                if (items > 0) s_speed += b[2];
                parts.Add($"{prefab}: {items} item(s) {weight:0.#} kg x{factor:0.##}, speed {(items > 0 ? b[2] : 0f):+0.###;-0.###;0}");
            }
            string line = string.Join("; ", parts.ToArray());
            if (line != s_lastLine) { s_lastLine = line; if (line.Length > 0) Log.LogDebug("[VAInventory] bag bonus: " + line); }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetTotalWeight))]
        private static class TotalWeight
        {
            private static void Postfix(Inventory __instance, ref float __result)
            {
                if (!BackpackBridge.HasBonus || __instance == null || __instance != Slots.PlayerInventory) return;
                Refresh(Player.m_localPlayer);
                if (s_weightCut > 0f) __result = Mathf.Max(0f, __result - s_weightCut);
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetEquipmentMovementModifier))]
        private static class Speed
        {
            private static void Postfix(Player __instance, ref float __result)
            {
                if (!BackpackBridge.HasBonus || __instance == null || __instance != Player.m_localPlayer) return;
                Refresh(__instance);
                __result += s_speed;
            }
        }
    }
}
