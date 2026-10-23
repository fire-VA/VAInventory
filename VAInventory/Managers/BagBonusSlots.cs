using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // With VAInventory a bag's contents are its backpack-storage cells in the
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

        private static Inventory s_watched;
        private static bool s_dirty = true;
        private static int s_recomputed, s_cached;
        private static float s_prevCut;
        private static float s_nextForce, s_nextStat;
        private static void MarkDirty() => s_dirty = true;

        // Worn-bag changes arrive as
        // BackpackBridge's equip/unequip events (subscribed once) and mark the cache dirty; HasBonus (a reflection read) once per frame.
        private static bool s_eventsHooked;
        private static int s_bonusFrame = -1;
        private static bool s_bonus;
        private static bool Bonus { get { if (s_bonusFrame != Time.frameCount) { s_bonusFrame = Time.frameCount; s_bonus = BackpackBridge.HasBonus; } return s_bonus; } }
        private static void OnBagEvent(Player p, ItemDrop.ItemData bag) => s_dirty = true;

        private static void Refresh(Player player)
        {
            // Recomputed only when the player inventory changed (m_onChanged),
            // the worn bags changed, or 1 s passed; otherwise the cached cut/speed stand. Debug every 30 s: "recomputed N, cached M".
            if (Time.frameCount == s_frame) return;
            s_frame = Time.frameCount;
            var inv = player != null ? player.GetInventory() : null;
            if (inv != null && !ReferenceEquals(inv, s_watched)) { if (s_watched != null) s_watched.m_onChanged -= MarkDirty; s_watched = inv; inv.m_onChanged += MarkDirty; s_dirty = true; }
            if (!s_eventsHooked) { s_eventsHooked = true; BackpackBridge.OnBackpackEquipped += OnBagEvent; BackpackBridge.OnBackpackUnequipped += OnBagEvent; Log.LogDebug("[VAInventory] bag weight cache: watching VABackpacks equip/unequip events"); }
            float t = Time.unscaledTime;
            if (t >= s_nextStat) { if (s_nextStat > 0f) Log.LogDebug($"[VAInventory] bag weight cache: last 30 s recomputed {s_recomputed}, cached {s_cached}"); s_nextStat = t + 30f; s_recomputed = s_cached = 0; }
            if (!s_dirty && t < s_nextForce) { s_cached++; return; }
            s_dirty = false; s_nextForce = t + 1f; s_recomputed++;
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
            if (Mathf.Abs(s_weightCut - s_prevCut) > 0.01f) { Log.LogDebug($"[VAInventory] bag weight cache: weight cut {s_prevCut:0.##} -> {s_weightCut:0.##} kg (recomputed after a change; cached until the next)"); s_prevCut = s_weightCut; }
            string line = string.Join("; ", parts.ToArray());
            if (line != s_lastLine) { s_lastLine = line; if (line.Length > 0) Log.LogDebug("[VAInventory] bag bonus: " + line); }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetTotalWeight))]
        private static class TotalWeight
        {
            private static void Postfix(Inventory __instance, ref float __result)
            {
                if (!Bonus || __instance == null || __instance != Slots.PlayerInventory) return;
                Refresh(Player.m_localPlayer);
                if (s_weightCut > 0f) __result = Mathf.Max(0f, __result - s_weightCut);
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetEquipmentMovementModifier))]
        private static class Speed
        {
            private static void Postfix(Player __instance, ref float __result)
            {
                if (!Bonus || __instance == null || __instance != Player.m_localPlayer) return;
                Refresh(__instance);
                __result += s_speed;
            }
        }
    }
}
