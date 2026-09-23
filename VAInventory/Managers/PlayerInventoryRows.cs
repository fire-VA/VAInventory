using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent
{
    // Valheim 1.0 sells extra inventory rows: Player.SetInventorySize sets the vanilla row count on every spawn and
    // purchase. Every VAInventory slot sits a fixed offset below the player rows, so the layout follows that count, and
    // the count each character's saved layout was built on is kept with the character so a change moves the slots.
    public static class PlayerInventoryRows
    {
        private const string SavedLayoutRowsKey = "VAInventory.VanillaPlayerRows";
        private const int MinVanillaRows = 0;
        private const int MaxVanillaRows = 9;

        internal static void AdoptSavedLayout(Player player)
        {
            int savedRows = ReadSavedLayoutRows(player);
            if (Slots.VanillaPlayerRows == savedRows) return;

            Slots.VanillaPlayerRows = savedRows;
            Slots.InitializeSlots();
        }

        private static int ReadSavedLayoutRows(Player player)
        {
            if (player.m_customData.TryGetValue(SavedLayoutRowsKey, out string saved) && int.TryParse(saved, out int rows))
                return Mathf.Clamp(rows, MinVanillaRows, MaxVanillaRows);
            return Slots.VanillaInventoryHeight;
        }

        private static void MoveLayoutToRows(Player player, int requestedRows)
        {
            int targetRows = Mathf.Clamp(requestedRows, MinVanillaRows, MaxVanillaRows);
            int rowDelta = targetRows - Slots.VanillaPlayerRows;
            player.m_customData[SavedLayoutRowsKey] = targetRows.ToString();
            if (rowDelta == 0) return;

            var inventory = player.GetInventory();
            List<ItemDrop.ItemData> displaced = ShiftSlotAreaItems(inventory, rowDelta);

            Debug.Log($"[VAInventory] Vanilla inventory rows {Slots.VanillaPlayerRows} -> {targetRows}: slot layout moved {rowDelta:+#;-#} row(s), {displaced.Count} item(s) displaced from removed rows.");
            Slots.VanillaPlayerRows = targetRows;
            EquipmentPanel.RefreshPanelOnly();

            foreach (var item in displaced)
            {
                item.m_gridPos = Slots.emptyPosition;
                InventoryInteraction.TryRelocateOrDropItem(inventory, item);
            }
        }

        // Moves every slot-area item by rowDelta. When rows are removed, items still in the rows that disappear are
        // returned for relocation instead of being left to shadow the slots that now occupy those cells.
        private static List<ItemDrop.ItemData> ShiftSlotAreaItems(Inventory inventory, int rowDelta)
        {
            int oldPlayerHeight = Slots.InventoryHeightPlayer;
            int newPlayerHeight = oldPlayerHeight + rowDelta;
            var displaced = new List<ItemDrop.ItemData>();

            foreach (var item in inventory.GetAllItems())
            {
                int y = item.m_gridPos.y;
                if (y >= oldPlayerHeight)
                    item.m_gridPos = new Vector2i(item.m_gridPos.x, y + rowDelta);
                else if (y >= newPlayerHeight)
                    displaced.Add(item);
            }
            return displaced;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.SetInventorySize))]
        private static class Player_SetInventorySize_MoveSlotLayout
        {
            private static void Prefix(Player __instance, int rows)
            {
                if (__instance != Player.m_localPlayer) return;
                MoveLayoutToRows(__instance, rows);
            }
        }

        // SetInventorySize shrinks the inventory to the vanilla row count right before this runs, which would count
        // every slot item as out of bounds; the check has to run against the full slot layout instead.
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DropInvalidItems))]
        private static class Humanoid_DropInvalidItems_KeepSlotLayout
        {
            private static void Prefix(Humanoid __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                InventoryInteraction.UpdatePlayerInventorySize();
            }
        }
    }
}
