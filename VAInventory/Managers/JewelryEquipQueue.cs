using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent
{
    /// <summary>
    /// 1.0.371 (Fire 10-29: "rings and necklaces [should] get the proper equip effects that armor does ... plays the animation and takes a
    /// tiny bit of time to put on"). A drag INTO a free jewelry cell, or OUT of one onto a free main-grid cell, goes through Fires Loot
    /// Overhaul's FloJewelryApi.QueueEquip / QueueUnequip (vanilla's equip queue: animation, progress, sound). The piece stays where it is
    /// until the queue finishes, then our callback moves it into the cell the player chose (or, if that cell was taken meanwhile, to the
    /// first free jewelry cell / main-grid cell). Soft reference: without FLO (or an FLO without the API) the move is instant, as before.
    /// Proof (Debug, panel log): one line per queued move and one when it lands.
    /// </summary>
    internal static class JewelryEquipQueue
    {
        private static bool s_looked;
        private static Func<Player, ItemDrop.ItemData, Action, bool> s_equip, s_unequip;

        private static void Bind()
        {
            if (s_looked) return;
            s_looked = true;
            try
            {
                var t = Type.GetType("FiresLootOverhaul.Api.FloJewelryApi, FiresLootOverhaul", false);
                var sig = new[] { typeof(Player), typeof(ItemDrop.ItemData), typeof(Action) };
                var eq = t?.GetMethod("QueueEquip", BindingFlags.Public | BindingFlags.Static, null, sig, null);
                var un = t?.GetMethod("QueueUnequip", BindingFlags.Public | BindingFlags.Static, null, sig, null);
                if (eq != null) s_equip = (Func<Player, ItemDrop.ItemData, Action, bool>)Delegate.CreateDelegate(typeof(Func<Player, ItemDrop.ItemData, Action, bool>), eq);
                if (un != null) s_unequip = (Func<Player, ItemDrop.ItemData, Action, bool>)Delegate.CreateDelegate(typeof(Func<Player, ItemDrop.ItemData, Action, bool>), un);
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: FloJewelryApi not bindable ({e.Message}): moves stay instant"); }
            EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: FLO QueueEquip {(s_equip != null ? "found" : "MISSING")}, QueueUnequip {(s_unequip != null ? "found" : "MISSING")} ({(s_equip != null ? "jewelry drags play the equip animation" : "jewelry drags move instantly")})");
        }

        /// <summary>A held piece dropped on a FREE jewelry cell: true = queued (the piece stays in its old cell until FLO's queue ends).</summary>
        internal static bool Equip(Player p, ItemDrop.ItemData item, Slots.Slot target)
        {
            Bind();
            if (s_equip == null || p == null || item == null || target == null) return false;
            var inv = p.GetInventory();
            var cell = target.GridPosition;
            bool queued;
            try
            {
                queued = s_equip(p, item, () =>
                {
                    if (inv == null || !inv.ContainsItem(item)) { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: {item.m_shared.m_name} gone before it was put on"); return; }
                    var at = inv.GetItemAt(cell.x, cell.y);
                    if (at == null || at == item)
                    {
                        item.m_gridPos = cell;
                        Slots.ClearCachedItems();
                        inv.Changed();
                        EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: {item.m_shared.m_name} put on -> {target.ID} {cell}");
                    }
                    else
                    {
                        bool ok = VAInventoryApi.TryEquipJewelry(p, item);
                        EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: {target.ID} {cell} taken by {at.m_shared.m_name} meanwhile; first free / swap {(ok ? "done" : "REFUSED")}");
                    }
                });
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: QueueEquip threw ({e.Message}); instant move"); return false; }
            EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: {item.m_shared.m_name} -> {target.ID} {cell}: {(queued ? "QUEUED (equip animation)" : "not queued (FLO said no: not FLO jewelry or already queued), instant move")}");
            return queued;
        }

        /// <summary>Right-click on a worn piece (jewelry cell): FLO's unequip animation, then <paramref name="move"/> (the panel's own
        /// move-back-to-the-bag). True = queued; false = do the move now.</summary>
        internal static bool Unequip(Player p, ItemDrop.ItemData item, Slots.Slot from, Action move)
        {
            Bind();
            if (s_unequip == null || p == null || item == null || !UnifiedScreen.IsJewelrySlot(from)) return false;
            var inv = p.GetInventory(); var cell = from.GridPosition;
            bool queued;
            try
            {
                queued = s_unequip(p, item, () =>
                {
                    if (inv == null || !inv.ContainsItem(item) || item.m_gridPos != cell) { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: {item.m_shared.m_name} moved or gone before it came off"); return; }
                    move();
                    EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: {item.m_shared.m_name} taken off {from.ID} (right-click) -> {item.m_gridPos}");
                });
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: QueueUnequip threw ({e.Message}); instant move"); return false; }
            EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: {item.m_shared.m_name} right-click off {from.ID}: {(queued ? "QUEUED (unequip animation)" : "not queued, instant move")}");
            return queued;
        }

        // A piece dragged OUT of a jewelry cell onto a FREE main-grid cell of the player grid: FLO's unequip animation, then it moves there.
        // Runs after InventoryInteraction's drop validation (Low < LowerThanNormal). Anything else (swap, another grid, a split) stays vanilla.
        [HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
        private static class DragOut
        {
            private static readonly FieldInfo fi_dragItem = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
            private static readonly FieldInfo fi_dragInventory = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
            private static readonly FieldInfo fi_dragAmount = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");

            [HarmonyPriority(Priority.Low)]
            private static bool Prefix(InventoryGrid grid, Vector2i pos)
            {
                var gui = InventoryGui.instance; var p = Player.m_localPlayer;
                if (gui == null || p == null || grid == null || grid != gui.m_playerGrid) return true;
                if (!(fi_dragItem?.GetValue(gui) is ItemDrop.ItemData item) || !(fi_dragInventory?.GetValue(gui) is Inventory from)) return true;
                var inv = p.GetInventory();
                if (from != inv || pos.y >= Slots.InventoryHeightPlayer || inv.GetItemAt(pos.x, pos.y) != null) return true;
                if (fi_dragAmount?.GetValue(gui) is int amt && amt != item.m_stack) return true;
                var fromSlot = Slots.GetSlotInGrid(item.m_gridPos);
                if (!UnifiedScreen.IsJewelrySlot(fromSlot)) return true;
                Bind();
                if (s_unequip == null) return true;
                var fromCell = item.m_gridPos;
                bool queued;
                try
                {
                    queued = s_unequip(p, item, () =>
                    {
                        if (!inv.ContainsItem(item) || item.m_gridPos != fromCell) { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: {item.m_shared.m_name} moved or gone before it came off"); return; }
                        var to = pos;
                        if (inv.GetItemAt(to.x, to.y) != null && !FirstFreeMain(inv, out to)) { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: {item.m_shared.m_name} stays on (no free main-grid cell)"); return; }
                        item.m_gridPos = to;
                        Slots.ClearCachedItems();
                        inv.Changed();
                        EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: {item.m_shared.m_name} taken off {fromSlot.ID} -> {to}");
                    });
                }
                catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: QueueUnequip threw ({e.Message}); vanilla move"); return true; }
                EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] jewelry equip queue: {item.m_shared.m_name} {fromSlot.ID} -> {pos}: {(queued ? "QUEUED (unequip animation)" : "not queued, vanilla move")}");
                if (!queued) return true;
                EquipmentPanel.ClearDragState();
                return false;
            }

            private static bool FirstFreeMain(Inventory inv, out Vector2i at)
            {
                for (int y = 0; y < Slots.InventoryHeightPlayer; y++)
                    for (int x = 0; x < inv.GetWidth(); x++)
                        if (inv.GetItemAt(x, y) == null) { at = new Vector2i(x, y); return true; }
                at = new Vector2i(-1, -1);
                return false;
            }
        }
    }
}
