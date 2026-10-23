using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Managers;

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
            if (Slots.VanillaPlayerRows != savedRows)
            {
                Slots.VanillaPlayerRows = savedRows;
                Slots.InitializeSlotsForRelayout();   // A plain rebuild - the items are already at the coordinates of this layout
            }
            Relayout.MarkLayoutAdopted(player);   // from here on every rebuild carries the items by address
            Debug.Log($"[VAInventory] saved layout adopted for {player.GetPlayerName()}: vanilla rows {savedRows}; relayouts carry items from now on");
        }

        // 1.0.141 (R12B Fire's character: every equipped item sat one ROW above its cell - the helmet in the main grid,
        // the chest in the Helmet cell, the belt in Shoulder, the bag in Trinket - because the slot block moved when the vanilla row count
        // changed and the items were not moved with it). On spawn: the equipped items not on a cell that fits them, and any slot-area
        // item on a cell that does not fit it, are checked for ONE row offset (+-1..3) that puts them on fitting cells. With a consistent
        // offset each one moves there; a cell already taken by a fitting item sends it to another free fitting cell, else a free main
        // cell. Nothing is dropped. Every move is logged; the character's layout key is then written so the next load matches.
        internal static void RepairRowShift(Player player)
        {
            try
            {
                var inv = player.GetInventory();
                if (inv == null) return;
                // Behind a client key, default OFF
                // Always on, no key
                int slotTop = Slots.InventoryHeightPlayer;
                var wrong = new List<ItemDrop.ItemData>();
                foreach (var it in inv.GetAllItems())
                {
                    var here = Slots.GetSlotInGrid(it.m_gridPos);
                    bool onFitting = here != null && here.IsEquipmentSlot && here.ItemFits(it);
                    if (player.IsItemEquiped(it) && !onFitting && Slots.IsEquipmentSlotItem(it)) wrong.Add(it);
                    else if (it.m_gridPos.y >= slotTop && here != null && !here.ItemFits(it)) wrong.Add(it);
                }
                if (wrong.Count == 0) { player.m_customData[SavedLayoutRowsKey] = Slots.VanillaPlayerRows.ToString(); return; }
                int bestDelta = 0, bestCount = 0;
                foreach (int d in new[] { 1, -1, 2, -2, 3, -3 })
                {
                    int n = wrong.Count(it => { var s = Slots.GetSlotInGrid(new Vector2i(it.m_gridPos.x, it.m_gridPos.y + d)); return s != null && s.IsEquipmentSlot && s.ItemFits(it); });
                    if (n > bestCount) { bestCount = n; bestDelta = d; }
                }
                Debug.Log($"[VAInventory] row-shift repair: {wrong.Count} item(s) off their cells; offset {bestDelta:+#;-#;0} fits {bestCount} of them (layout rows {Slots.VanillaPlayerRows}, saved key {(player.m_customData.TryGetValue(SavedLayoutRowsKey, out string k) ? k : "absent")})");
                if (bestCount == 0) return;
                // passes: every item whose offset cell is free moves; a move frees the cell another one wants, so repeat until nothing
                // moves; whatever is left then takes another free fitting cell, else a free main cell
                var pending = wrong.ToList();
                for (int pass = 0; pass < 6 && pending.Count > 0; pass++)
                {
                    int moved = 0;
                    foreach (var it in pending.ToList())
                    {
                        Vector2i from = it.m_gridPos, to = new Vector2i(from.x, from.y + bestDelta);
                        var target = Slots.GetSlotInGrid(to);
                        if (target == null || !target.IsEquipmentSlot || !target.ItemFits(it) || inv.GetItemAt(to.x, to.y) != null) continue;
                        it.m_gridPos = to; pending.Remove(it); moved++;
                        Slots.ClearCachedItems();
                        Debug.Log($"[VAInventory] row-shift repair: {it.m_dropPrefab?.name ?? it.m_shared.m_name} q{it.m_quality} (equipped {player.IsItemEquiped(it)}) ({from.x},{from.y}) -> ({to.x},{to.y}) to its cell '{target.ID}'");
                    }
                    if (moved == 0) break;
                }
                foreach (var it in pending)
                {
                    Vector2i from = it.m_gridPos; string how;
                    if (Slots.TryFindFreeEquipmentSlotForItem(it, out var free)) { it.m_gridPos = free.GridPosition; how = $"to the free cell '{free.ID}' (its offset cell was taken)"; }
                    else if (Slots.TryMakeFreeSpaceInPlayerInventory(true, out Vector2i main)) { it.m_gridPos = main; how = $"to a free main cell ({main.x},{main.y})"; }
                    else how = "LEFT in place (no free cell)";
                    Slots.ClearCachedItems();
                    Debug.Log($"[VAInventory] row-shift repair: {it.m_dropPrefab?.name ?? it.m_shared.m_name} q{it.m_quality} (equipped {player.IsItemEquiped(it)}) ({from.x},{from.y}) -> ({it.m_gridPos.x},{it.m_gridPos.y}) {how}");
                }
                player.m_customData[SavedLayoutRowsKey] = Slots.VanillaPlayerRows.ToString();
                Slots.ClearCachedItems();
                inv.m_onChanged?.Invoke();
            }
            catch (System.Exception e) { Debug.LogWarning($"[VAInventory] row-shift repair failed (nothing changed after the failure point): {e.Message}"); }
        }

        // Any item on an equipment-panel cell whose ROLE does not take it moves to
        // a free cell of its own role (TryFindFreeEquipmentSlotForItem, which prefers its saved cell), else a free main-grid cell. Matched
        // by role, not by row, so it also covers a column shift, a bag-block shift and a lone stray. Behind a client key, default OFF.
        internal static void RemapMisplacedByRole(Player player)
        {
            try
            {
                // The role remap is a fix, always on, no key
                // 
                var inv = player.GetInventory();
                if (inv == null) return;
                // A worn
                // equipment item sitting in TeleportBypass or Misc counts as off its role too
                var wrong = inv.GetAllItems().Where(it => { var s = Slots.GetSlotInGrid(it.m_gridPos); return s != null && s.IsEquipmentSlot && (!s.ItemFits(it) || (player.IsItemEquiped(it) && Slots.IsEquipmentSlotItem(it) && (s.IsTeleportBypassSlot || s.IsMiscSlot))); }).ToList();
                // 1.0.150 (Fire's hand-test 10-14: his worn Eye of the Beholder stayed on the orphan cell (0,10) with the Helmet slot free): a
                // WORN equipment item on a cell below the main grid that is no slot at all is off its role too
                var orphans = inv.GetAllItems().Where(it => Slots.GetSlotInGrid(it.m_gridPos) == null && it.m_gridPos.y >= Slots.InventoryHeightPlayer && player.IsItemEquiped(it) && Slots.IsEquipmentSlotItem(it)).ToList();
                if (orphans.Count > 0) Debug.Log($"[VAInventory] layout remap: {orphans.Count} worn item(s) on an orphan cell (no slot there)");
                wrong.AddRange(orphans);
                Debug.Log($"[VAInventory] layout remap: {wrong.Count} item(s) on a cell that does not take them");
                foreach (var it in wrong)
                {
                    var from = it.m_gridPos; var was = Slots.GetSlotInGrid(from);
                    // 1.0.148 (R_INV run 4: the chest went to TeleportBypass because its Chest cell still held the off-role legs, moved one
                    // line later): an item already moved by a chain is skipped; the role cell is never TeleportBypass / Misc / bag storage,
                    // and a role cell held by a WORN item off ITS role is freed first by moving that one to its own cell (the equip chain)
                    bool orphan = was == null && orphans.Contains(it) && Slots.GetSlotInGrid(it.m_gridPos) == null;
                    if ((was == null && !orphan) || (was != null && was.ItemFits(it) && !(player.IsItemEquiped(it) && Slots.IsEquipmentSlotItem(it) && (was.IsTeleportBypassSlot || was.IsMiscSlot)))) continue;
                    Slots.Slot own = null;
                    foreach (var s in Slots.GetEquipmentSlots().Where(s => s != null && s.IsActive && s != was && !s.IsMiscSlot && !s.IsTeleportBypassSlot && !s.IsBackpackStorageSlot && s.ItemFits(it)).ToList())
                        if (InventoryInteraction.Humanoid_EquipItem_AutoMoveToSlot.EvictOffRole(player, s, it, 0)) { own = s; break; }
                    string how;
                    if (own != null) { it.m_gridPos = own.GridPosition; how = $"its role cell '{own.ID}'"; }
                    else if (orphan) { Debug.LogWarning($"[VAInventory] layout remap: worn {it.m_dropPrefab?.name ?? it.m_shared.m_name} on the orphan cell ({from.x},{from.y}): no free cell of its role; it stays"); continue; }
                    else if (Slots.TryMakeFreeSpaceInPlayerInventory(true, out Vector2i main)) { it.m_gridPos = main; how = "a free main-grid cell (no free cell of its role)"; }
                    else how = "LEFT in place (no free cell at all)";
                    Slots.ClearCachedItems();
                    Debug.Log($"[VAInventory] layout remap: {it.m_dropPrefab?.name ?? it.m_shared.m_name} q{it.m_quality} (worn {player.IsItemEquiped(it)}) ({from.x},{from.y}) -> ({it.m_gridPos.x},{it.m_gridPos.y}), reason: {(was == null ? "worn on an orphan cell (no slot there)" : $"on '{was.ID}' ({(was.ItemFits(it) ? "worn off its role" : "takes no " + it.m_shared.m_itemType)})")}; to {how}");
                }
                if (wrong.Count > 0) inv.m_onChanged?.Invoke();
            }
            catch (System.Exception e) { Debug.LogWarning($"[VAInventory] layout remap failed: {e.Message}"); }
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

            // The rows change and the slots are rebuilt INSIDE Relayout, so every item keeps its address (a slot id, or its
            // main-grid x,y); items of removed main rows go to a free cell, never dropped (overflow if there is none)
            int fromRows = Slots.VanillaPlayerRows;
            Relayout.Run($"vanilla rows {fromRows}->{targetRows}", () => { Slots.VanillaPlayerRows = targetRows; Slots.InitializeSlotsForRelayout(); });
            Debug.Log($"[VAInventory] Vanilla inventory rows {fromRows} -> {targetRows}: carried across by Relayout");
            EquipmentPanel.RefreshPanelOnly();
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
