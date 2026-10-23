using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent
{
    // 1.0.144 ([seasons], Fire 10-14: "the inventory needs to change fluidly between all settings - new rows from the traders, a backpack /
    // hobo stick / Fires staff with storage equipped or not, the configs - and it all needs to be completely clean"). Every slot rebuild
    // (Slots.InitializeSlots, whatever asked for it) and the trader row purchase go through ONE path:
    //   1. capture each player item's ADDRESS in the old layout: "S:<slot id>" for a cell (Helmet, Ammo_n, Misc_c_r,
    //      BackpackStorage_<bag>_..., class bars ...), "M:x,y" for the main grid - a region + a local cell, never one global y;
    //   2. rebuild;
    //   3. put each item back BY ADDRESS: the same slot id = the same cell; a main-grid cell that still exists = the same x,y; otherwise
    //      the nearest free cell of the same role (the same region first), else a free main-grid cell, else OVERFLOW.
    // Overflow is never deleted: the items are saved into the character's customData ("vainv_overflow", a vanilla inventory blob),
    // announced on screen, and come back on the next relayout with room or with the 'vainv_overflow' command.
    // One line per relayout: trigger, items kept / moved (old -> new) / overflowed.
    internal static class Relayout
    {
        internal const string OverflowKey = "vainv_overflow";
        // the address an item was pushed OFF (its cell disappeared - ammo slots hidden, a bar column, a bag): kept on the item, so when
        // that cell comes back (the setting turned on again) the item returns there; cleared once it is home
        internal const string HomeKey = "vainv_home";
        private static bool s_inside;
        // 1.0.145 (R_INV run 1 on vafirecopy: at load the bags' equip rebuilt the slots while the DEFAULT 4-row layout was still set, so
        // the items - saved under the character's 5-row layout - were "carried" from wrong addresses: the Hammer at main (0,8) looked
        // gone and went to Misc_0_0, and AdoptSavedLayout then carried that). Until this player's saved layout is adopted, a rebuild is
        // only a rebuild; Adopt itself is a plain rebuild (it sets the layout the items were SAVED in).
        private static int s_readyFor;
        internal static void MarkLayoutAdopted(Player p) { s_readyFor = p != null ? p.GetInstanceID() : 0; }
        private static bool Ready(Player p) => p != null && s_readyFor == p.GetInstanceID();
        /// <summary>1.0.146: true once relayouts carry this player's items by address (the legacy panel migrate then stands down).</summary>
        internal static bool Carries(Player p) => Ready(p);

        private static string AddressOf(ItemDrop.ItemData it)
        {
            var s = Slots.GetSlotInGrid(it.m_gridPos);
            if (s != null)
            {
                // 1.0.146 (R_INV run 2: every ammo cell is "Ammo", so the legs' "S:Ammo" resolved to the arrows' cell): a repeated slot ID
                // carries its ordinal among the slots of that ID ("Ammo#2")
                var same = Slots.AllSlots().Where(o => o != null && o.ID == s.ID).ToList();
                int n = same.IndexOf(s);
                return same.Count > 1 && n > 0 ? $"S:{s.ID}#{n}" : "S:" + s.ID;
            }
            return $"M:{it.m_gridPos.x},{it.m_gridPos.y}";
        }

        private static Slots.Slot SlotOf(string addr)
        {
            string id = addr.Substring(2); int n = 0;
            int h = id.LastIndexOf('#');
            if (h > 0 && int.TryParse(id.Substring(h + 1), out n)) id = id.Substring(0, h); else n = 0;
            return Slots.AllSlots().Where(s => s != null && s.ID == id).Skip(n).FirstOrDefault();
        }

        private static bool CellFree(Inventory inv, Vector2i p, ItemDrop.ItemData except) =>
            !inv.GetAllItems().Any(o => o != except && o.m_gridPos.x == p.x && o.m_gridPos.y == p.y);

        private static bool FreeMainCell(Inventory inv, out Vector2i pos)
        {
            for (int y = 0; y < Slots.InventoryHeightPlayer; y++)
                for (int x = 0; x < Slots.InventoryWidth; x++)
                {
                    var p = new Vector2i(x, y);
                    if (Slots.GetSlotInGrid(p) == null && CellFree(inv, p, null)) { pos = p; return true; }
                }
            pos = Slots.emptyPosition;
            return false;
        }

        private static string RegionOf(string slotId)
        {
            int h = slotId.LastIndexOf('#'); if (h > 0) slotId = slotId.Substring(0, h);
            int i = slotId.LastIndexOf('_');
            return i > 0 ? slotId.Substring(0, i) : slotId;
        }

        /// <summary>Runs <paramref name="rebuild"/> and carries every player item across it by address. Re-entrant calls just rebuild.</summary>
        internal static void Run(string trigger, Action rebuild)
        {
            var inv = Slots.PlayerInventory;
            if (s_inside || inv == null || Slots.CurrentPlayer == null || !Slots.HasSlots) { rebuild(); return; }
            if (!Ready(Slots.CurrentPlayer)) { rebuild(); Debug.Log($"[VAInventory] relayout {trigger}: before the character's saved layout is adopted - rebuild only, no item moved"); return; }
            s_inside = true;
            try
            {
                var player = Slots.CurrentPlayer;
                // an item that was pushed off its cell earlier asks for that cell first (its home), its present address second
                var before = inv.GetAllItems().Select(it => (it, addr: it.m_customData != null && it.m_customData.TryGetValue(HomeKey, out string h) && !string.IsNullOrEmpty(h) ? h : AddressOf(it), from: it.m_gridPos, worn: player.IsItemEquiped(it), here: AddressOf(it))).ToList();
                rebuild();
                Slots.ClearCachedItems();

                int kept = 0;
                var moved = new List<string>();
                var pending = new List<(ItemDrop.ItemData it, string addr, Vector2i from)>();
                foreach (var it in before.Select(b => b.it)) it.m_gridPos = Slots.emptyPosition;   // everyone off, then back by address
                foreach (var (it, addr, from, _, here) in before)
                {
                    Vector2i target = Slots.emptyPosition;
                    if (addr.StartsWith("S:"))
                    {
                        var slot = SlotOf(addr);
                        if (slot != null && slot.GridPosition != Slots.emptyPosition) target = slot.GridPosition;
                    }
                    else
                    {
                        var xy = addr.Substring(2).Split(',');
                        var p = new Vector2i(int.Parse(xy[0]), int.Parse(xy[1]));
                        // 1.0.146 (R_INV run 2: the Eye at (0,10), between the main grid and the equipment rows, read "gone"): an orphan cell
                        // inside the inventory that is not a slot keeps its item
                        if (p.x >= 0 && p.y >= 0 && p.y < inv.GetHeight() && p.x < Slots.InventoryWidth && Slots.GetSlotInGrid(p) == null) target = p;
                    }
                    if (target != Slots.emptyPosition && CellFree(inv, target, it))
                    {
                        it.m_gridPos = target;
                        bool wasHome = addr != here;
                        if (wasHome) it.m_customData?.Remove(HomeKey);
                        if (target.x == from.x && target.y == from.y) kept++; else moved.Add($"{Name(it)} ({from.x},{from.y})->({target.x},{target.y}) {(wasHome ? $"back home '{addr}'" : "same address")}");
                    }
                    else if (addr != here)
                    {
                        // home still gone: stay at the present address (and keep the home)
                        var cur = here.StartsWith("S:") ? SlotOf(here)?.GridPosition ?? Slots.emptyPosition : ParseMain(here);
                        if (cur != Slots.emptyPosition && CellFree(inv, cur, it)) { it.m_gridPos = cur; if (cur.x == from.x && cur.y == from.y) kept++; else moved.Add($"{Name(it)} ({from.x},{from.y})->({cur.x},{cur.y}) same address (home '{addr}' not back yet)"); }
                        else pending.Add((it, addr, from));
                    }
                    else pending.Add((it, addr, from));
                }
                var overflow = new List<ItemDrop.ItemData>();
                foreach (var (it, addr, from) in pending)
                {
                    string region = addr.StartsWith("S:") ? RegionOf(addr.Substring(2)) : null;
                    // 1.0.145 (R_INV run 1: the Hammer landed in TeleportBypass): TeleportBypass and Misc take ANY item, so they are a role only
                    // for an item that came from that region; a main-grid item goes back to the main grid first
                    if (addr.StartsWith("M:") && FreeMainCell(inv, out Vector2i mainFirst)) { it.m_gridPos = mainFirst; moved.Add($"{Name(it)} ({from.x},{from.y})->({mainFirst.x},{mainFirst.y}) '{addr}' gone, main grid"); continue; }
                    var role = Slots.AllSlots().Where(s => s != null && s.IsActive && s.GridPosition != Slots.emptyPosition && s.ItemFits(it) && CellFree(inv, s.GridPosition, it)
                                                          && ((!s.IsTeleportBypassSlot && !s.IsMiscSlot) || (region != null && RegionOf(s.ID) == region)))
                        .OrderBy(s => region != null && RegionOf(s.ID) == region ? 0 : 1).FirstOrDefault();
                    if (it.m_customData != null && !it.m_customData.ContainsKey(HomeKey)) it.m_customData[HomeKey] = addr;   // remember where it belongs
                    if (role != null) { it.m_gridPos = role.GridPosition; moved.Add($"{Name(it)} ({from.x},{from.y})->({it.m_gridPos.x},{it.m_gridPos.y}) '{addr}' gone, same role '{role.ID}'"); }
                    else if (FreeMainCell(inv, out Vector2i main)) { it.m_gridPos = main; moved.Add($"{Name(it)} ({from.x},{from.y})->({main.x},{main.y}) '{addr}' gone, main grid"); }
                    else overflow.Add(it);
                }
                if (overflow.Count > 0) SaveOverflow(player, inv, overflow);
                int back = overflow.Count == 0 ? TryReturnOverflow(player, inv) : 0;
                Slots.ClearCachedItems();
                inv.m_onChanged?.Invoke();
                if (moved.Count > 0 || overflow.Count > 0 || back > 0)
                    Debug.Log($"[VAInventory] relayout {trigger}: {kept} item(s) kept, {moved.Count} moved{(moved.Count > 0 ? " (" + string.Join("; ", moved.ToArray()) + ")" : "")}, {overflow.Count} to overflow{(back > 0 ? $", {back} back from overflow" : "")}");
                else
                    Debug.Log($"[VAInventory] relayout {trigger}: {kept} item(s) kept, nothing moved");
            }
            catch (Exception e) { Debug.LogError($"[VAInventory] relayout {trigger} failed: {e}"); }
            finally { s_inside = false; }
        }

        private static Vector2i ParseMain(string addr)
        {
            var xy = addr.Substring(2).Split(',');
            var p = new Vector2i(int.Parse(xy[0]), int.Parse(xy[1]));
            return p.y < Slots.InventoryHeightPlayer && p.x < Slots.InventoryWidth && Slots.GetSlotInGrid(p) == null ? p : Slots.emptyPosition;
        }

        private static string Name(ItemDrop.ItemData it) => it.m_dropPrefab?.name ?? it.m_shared.m_name;

        // overflow: a vanilla inventory blob in the character's customData; never deleted
        private static Inventory LoadOverflow(Player p)
        {
            var box = new Inventory("vainv_overflow", null, 8, 16);
            if (p.m_customData.TryGetValue(OverflowKey, out string b64) && !string.IsNullOrEmpty(b64))
                try { box.Load(new ZPackage(Convert.FromBase64String(b64))); } catch (Exception e) { Debug.LogWarning($"[VAInventory] overflow could not be read: {e.Message}"); }
            return box;
        }
        private static void StoreOverflow(Player p, Inventory box)
        {
            if (box.GetAllItems().Count == 0) { p.m_customData.Remove(OverflowKey); return; }
            var pkg = new ZPackage(); box.Save(pkg);
            p.m_customData[OverflowKey] = Convert.ToBase64String(pkg.GetArray());
        }
        private static void SaveOverflow(Player p, Inventory inv, List<ItemDrop.ItemData> items)
        {
            var box = LoadOverflow(p);
            foreach (var it in items)
            {
                if (p.IsItemEquiped(it)) p.UnequipItem(it, false);
                box.AddItem(it.Clone());
                inv.RemoveItem(it);
                Debug.LogWarning($"[VAInventory] relayout overflow: {Name(it)} x{it.m_stack} kept in '{OverflowKey}' (no cell left); it comes back with 'vainv_overflow' or the next relayout with room");
            }
            StoreOverflow(p, box);
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"{items.Count} item(s) had no room after the inventory changed - kept safe; use 'vainv_overflow' to get them back");
        }
        internal static int TryReturnOverflow(Player p, Inventory inv)
        {
            if (!p.m_customData.ContainsKey(OverflowKey)) return 0;
            var box = LoadOverflow(p);
            int n = 0;
            foreach (var it in box.GetAllItems().ToList())
            {
                if (!FreeMainCell(inv, out Vector2i cell)) break;
                var c = it.Clone(); c.m_gridPos = cell;
                if (inv.AddItem(c, cell)) { box.RemoveItem(it); n++; Debug.Log($"[VAInventory] overflow returned: {Name(c)} x{c.m_stack} to ({cell.x},{cell.y})"); }
            }
            StoreOverflow(p, box);
            return n;
        }

        [HarmonyLib.HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class OverflowCommand
        {
            [HarmonyLib.HarmonyPostfix]
            private static void After() => new Terminal.ConsoleCommand("vainv_overflow", "[VAInventory] gives back items kept in overflow after an inventory layout change (as many as fit)", args =>
            {
                var p = Player.m_localPlayer; if (p == null) return;
                int n = TryReturnOverflow(p, p.GetInventory());
                args.Context?.AddString($"[VAInventory] {n} item(s) returned from overflow; {(p.m_customData.ContainsKey(OverflowKey) ? "some still waiting for room" : "overflow empty")}");
            });
        }
    }
}
