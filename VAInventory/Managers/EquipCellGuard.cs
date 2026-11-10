using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent
{
    /// <summary>
    /// 1.0.371 (Fire 10-29: "you can have a piece of armor in the chest [s]lot that is not actually equipped"): every worn cell (helmet,
    /// chest, legs, shoulder, utility, trinket ...: the cells a drop equips into; not misc / bag / ammo / food / torch / class bar / hotkey /
    /// pet / jewelry) must match the real equipped state.
    ///  A) an item in a worn cell that is NOT equipped -> equipped in place; if the game refuses, it moves to a free main-grid cell;
    ///  B) an equipped item that has a worn cell but sits OUTSIDE every one -> moved into its free cell (or swapped with an unworn occupant).
    /// Found by a cheap check after every change of the local player's inventory (a few cells), which also captures the stack of the change
    /// that produced the first mismatch (the log proof of WHICH path does it); fixed next frame, and again on load and inventory open.
    /// Proof: one Debug why-line per fix (with the producing path), one Info line per session on the first fix.
    /// </summary>
    [HarmonyPatch]
    internal static class EquipCellGuard
    {
        private static bool s_busy, s_dirty, s_infoSaid;
        private static string s_trace, s_found;
        private static Player s_ready;
        private static int s_fixes;

        private static bool WornCell(Slots.Slot s) =>
            s != null && s.IsActive && s.IsEquipmentSlot && !s.IsMiscSlot && !s.IsBackpackStorageSlot && !s.IsAmmoSlot && !s.IsFoodSlot
            && !s.IsTorchSlot && !s.IsClassBarSlot && !s.IsHotkeySlot && !s.IsPetSlot && !s.IsTeleportBypassSlot && !s.IsCoinPurseSlot
            && !UnifiedScreen.IsJewelrySlot(s);

        private static bool Ready(out Player p)
        {
            p = Player.m_localPlayer;
            return p != null && p == s_ready && !s_busy && p.GetInventory() != null;
        }

        // A: unworn item in a worn cell; B: worn item with a free-or-unworn worn cell of its own, sitting outside every worn cell
        private static bool FindMismatch(Player p, out string what)
        {
            var inv = p.GetInventory();
            foreach (var s in Slots.AllSlots())
            {
                if (!WornCell(s)) continue;
                var it = inv.GetItemAt(s.GridPosition.x, s.GridPosition.y);
                if (it != null && it.IsEquipable() && s.ItemFits(it) && !p.IsItemEquiped(it)) { what = $"A: {it.m_shared.m_name} in {s.ID} {s.GridPosition} NOT equipped"; return true; }
            }
            foreach (var it in inv.GetAllItems())
            {
                if (it == null || !p.IsItemEquiped(it) || WornCell(Slots.GetSlotInGrid(it.m_gridPos))) continue;
                var home = Slots.AllSlots().FirstOrDefault(s => WornCell(s) && s.ItemFits(it));
                if (home == null) continue;   // a weapon / tool: worn from the bag or hotbar is normal
                var occ = inv.GetItemAt(home.GridPosition.x, home.GridPosition.y);
                if (occ == null || !p.IsItemEquiped(occ)) { what = $"B: {it.m_shared.m_name} equipped at {it.m_gridPos} outside its {home.ID} cell"; return true; }
            }
            what = null;
            return false;
        }

        [HarmonyPatch(typeof(Inventory), "Changed"), HarmonyPostfix]
        private static void InventoryChanged(Inventory __instance)
        {
            if (s_dirty || !Ready(out var p) || __instance != p.GetInventory()) return;
            try
            {
                if (!FindMismatch(p, out s_found)) return;
                s_dirty = true;
                s_trace = System.Environment.StackTrace;   // only on a mismatch: the producing path
            }
            catch { }
        }

        [HarmonyPatch(typeof(Player), "Update"), HarmonyPostfix]
        private static void PlayerUpdate(Player __instance)
        {
            if (!s_dirty || __instance != Player.m_localPlayer) return;
            s_dirty = false;
            Reconcile("after an inventory change", s_found, s_trace);
            s_trace = null;
        }

        [HarmonyPatch(typeof(Player), "OnSpawned"), HarmonyPostfix]
        private static void Spawned(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            s_ready = __instance;
            Reconcile("on load", null, null);
        }

        [HarmonyPatch(typeof(InventoryGui), "Show"), HarmonyPostfix]
        private static void Opened() => Reconcile("on inventory open", null, null);

        internal static int Reconcile(string when, string found, string trace)
        {
            if (!Ready(out var p)) return 0;
            int fixedNow = 0;
            s_busy = true;
            try
            {
                if (!FindMismatch(p, out _)) return 0;   // resolved by itself (a drop sets the cell, then equips, inside one action)
                if (found != null)
                {
                    // the log proof: which call stack put the inventory in this state (trimmed to our + game frames)
                    string path = trace == null ? "?" : string.Join(" <- ", trace.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("at ") && !l.Contains("System.Environment") && !l.Contains("EquipCellGuard") && !l.Contains("HarmonyLib")).Take(8).Select(l => l.Substring(3)).ToArray());
                    EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] equip cells: mismatch found {when} ({found}); produced by: {path}");
                }
                var inv = p.GetInventory();
                for (int pass = 0; pass < 8 && FindMismatch(p, out var what); pass++)
                {
                    if (what.StartsWith("B:"))
                    {
                        var it = inv.GetAllItems().First(i => i != null && p.IsItemEquiped(i) && !WornCell(Slots.GetSlotInGrid(i.m_gridPos)) && Slots.AllSlots().Any(s => WornCell(s) && s.ItemFits(i)));
                        var home = Slots.AllSlots().First(s => WornCell(s) && s.ItemFits(it) && (inv.GetItemAt(s.GridPosition.x, s.GridPosition.y) == null || !p.IsItemEquiped(inv.GetItemAt(s.GridPosition.x, s.GridPosition.y))));
                        var occ = inv.GetItemAt(home.GridPosition.x, home.GridPosition.y);
                        var was = it.m_gridPos;
                        if (occ != null) occ.m_gridPos = was;
                        it.m_gridPos = home.GridPosition;
                        Slots.ClearCachedItems();
                        EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] equip cells: {it.m_shared.m_name} is worn but sat at {was}; moved into {home.ID} {home.GridPosition}{(occ != null ? $" (unworn {occ.m_shared.m_name} went to {was})" : "")} ({when})");
                    }
                    else
                    {
                        var s = Slots.AllSlots().First(c => WornCell(c) && inv.GetItemAt(c.GridPosition.x, c.GridPosition.y) is ItemDrop.ItemData i && i.IsEquipable() && c.ItemFits(i) && !p.IsItemEquiped(i));
                        var it = inv.GetItemAt(s.GridPosition.x, s.GridPosition.y);
                        bool ok;
                        InventoryInteraction.Humanoid_EquipItem_AutoMoveToSlot.KeepDroppedSlot = true;
                        try { ok = p.EquipItem(it, false) && p.IsItemEquiped(it); }
                        finally { InventoryInteraction.Humanoid_EquipItem_AutoMoveToSlot.KeepDroppedSlot = false; }
                        if (ok) EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] equip cells: {it.m_shared.m_name} sat in {s.ID} unequipped; EQUIPPED in place ({when})");
                        else if (Slots.TryMakeFreeSpaceInPlayerInventory(true, out Vector2i free))
                        {
                            it.m_gridPos = free; Slots.ClearCachedItems();
                            EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] equip cells: {it.m_shared.m_name} sat in {s.ID} unequipped and the game would not equip it; moved to the bag {free} ({when})");
                        }
                        else { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] equip cells: {it.m_shared.m_name} sat in {s.ID} unequipped; not equippable and the bag is full, left ({when})"); break; }
                    }
                    fixedNow++;
                }
                if (fixedNow > 0)
                {
                    s_fixes += fixedNow;
                    inv.Changed();
                    EquipmentPanel.MarkDirty();
                    if (!s_infoSaid) { s_infoSaid = true; EquipmentPanel.PanelLog?.LogInfo($"[VAInventory] equipment cells: fixed {fixedNow} item(s) whose cell did not match what is worn ({when}); details at Debug"); }
                }
            }
            catch (System.Exception e) { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] equip cells: reconcile failed ({when}): {e.Message}"); }
            finally { s_busy = false; }
            return fixedNow;
        }

        /// <summary>For the FDT test: true when every worn cell matches the equipped state (and the first mismatch otherwise).</summary>
        public static bool CellsMatch(out string mismatch)
        {
            mismatch = null;
            var p = Player.m_localPlayer;
            return p == null || !FindMismatch(p, out mismatch);
        }

        public static int FixesThisSession => s_fixes;

        internal static Vector2i FreeWornCell(ItemDrop.ItemData item)
        {
            var inv = Player.m_localPlayer?.GetInventory();
            if (inv == null || item == null) return new Vector2i(-1, -1);
            var s = Slots.AllSlots().FirstOrDefault(c => WornCell(c) && c.ItemFits(item) && inv.GetItemAt(c.GridPosition.x, c.GridPosition.y) == null);
            return s != null ? s.GridPosition : new Vector2i(-1, -1);
        }
    }
}
