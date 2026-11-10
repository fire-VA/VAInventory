// BagStackCap - bigger stacks in bag cells (bag upgrades such as Compactor and Trophy Pouch). VAInventory doesn't know the upgrades:
// it asks VABackpacks' BackpackApi.BagCellStackCap(player, bag, item) (0 = vanilla), soft-bound, and every bag cell of the local
// player's inventory may then hold up to Max(inv, item, pos) of an item instead of its vanilla m_maxStackSize.
// Never lose items:
//   - save / load: vanilla Inventory.Load cuts stacks back to m_maxStackSize, so Save stashes an over-cap stack's real size in the
//     item's custom data and Load puts it back (the bag blob, the player save and chests all go through these).
//   - out of a bag cell (to the main grid, a chest, the ground): at most a vanilla stack moves at a time; the rest stays.
//   - when a cap goes away (upgrade removed or swapped, bag unequipped): OnCapDropped splits the oversize stacks into free cells and
//     drops what doesn't fit at the player's feet. A check every second does the same as a backstop, over the whole inventory.
//   - a big stack arriving in any other inventory (MoveAll / quick-stack / take all into a chest, the tombstone on death) is
//     split into vanilla stacks on arrival; what doesn't fit stays in the source. A big stack lives ONLY in a capped bag cell.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    [HarmonyPatch]
    internal static class BagStackCap
    {
        private const string TrueStackKey = "VAInv.TrueStack";
        private const string PurseStackKey = "VAInventoryCoinPurseStack";   // the coin purse keeps its own over-cap stack
        private const string ApiTypeName = "VerdantsAscent.Managers.BackpackApi";
        private const float EnforceInterval = 1f;

        private static Func<Player, ItemDrop.ItemData, ItemDrop.ItemData, int> s_api;
        private static bool s_apiLooked;
        private static float s_nextEnforce;
        private static bool s_splitting;   // AddItem_SplitBig adds its pieces through AddItem: no recursion
        private static int s_cacheFrame = -1;
        private static readonly Dictionary<Vector2i, ItemDrop.ItemData> s_bagAtCell = new Dictionary<Vector2i, ItemDrop.ItemData>();
        private static readonly System.Reflection.FieldInfo s_elements = AccessTools.Field(typeof(InventoryGrid), "m_elements");

        // ── the cap ──────────────────────────────────────────────────────────────────────────────────────────────────

        private static Func<Player, ItemDrop.ItemData, ItemDrop.ItemData, int> Api
        {
            get
            {
                if (s_apiLooked) return s_api;
                s_apiLooked = true;
                var m = AccessTools.Method(AccessTools.TypeByName(ApiTypeName), "BagCellStackCap", new[] { typeof(Player), typeof(ItemDrop.ItemData), typeof(ItemDrop.ItemData) });
                if (m != null) s_api = (Func<Player, ItemDrop.ItemData, ItemDrop.ItemData, int>)Delegate.CreateDelegate(typeof(Func<Player, ItemDrop.ItemData, ItemDrop.ItemData, int>), m);
                Debug.Log($"[VAInventory] bag stack caps: {(s_api != null ? "bound to VABackpacks" : "VABackpacks has none (vanilla stacks)")}");
                return s_api;
            }
        }

        private static Inventory LocalInventory => Player.m_localPlayer != null ? Player.m_localPlayer.GetInventory() : null;

        /// <summary>The worn bag whose cell is at pos in the local player's inventory, or null.</summary>
        private static ItemDrop.ItemData BagAt(Vector2i pos)
        {
            Player p = Player.m_localPlayer;
            if (p == null) return null;
            if (s_cacheFrame != Time.frameCount)
            {
                s_cacheFrame = Time.frameCount;
                s_bagAtCell.Clear();
                foreach (ItemDrop.ItemData bag in BackpackBridge.GetEquippedBackpacks(p) ?? new List<ItemDrop.ItemData>())
                    foreach (Vector2i cell in BackpackStorageSync.CellsOf(p, bag))
                        s_bagAtCell[cell] = bag;
            }
            return s_bagAtCell.TryGetValue(pos, out ItemDrop.ItemData b) ? b : null;
        }

        /// <summary>How many of the item the cell at pos may hold: vanilla, or more in a bag cell with a stack upgrade.</summary>
        internal static int Max(Inventory inv, ItemDrop.ItemData item, Vector2i pos)
        {
            if (item == null) return 0;
            int vanilla = item.m_shared.m_maxStackSize;
            if (vanilla <= 1 || inv == null || inv != LocalInventory || Api == null) return vanilla;
            ItemDrop.ItemData bag = BagAt(pos);
            if (bag == null) return vanilla;
            int cap;
            try { cap = s_api(Player.m_localPlayer, bag, item); } catch { cap = 0; }
            return Math.Max(vanilla, cap);
        }

        private static bool Same(ItemDrop.ItemData a, ItemDrop.ItemData b) =>
            a != null && b != null && a.m_shared.m_name == b.m_shared.m_name && a.m_quality == b.m_quality && a.m_worldLevel == b.m_worldLevel;

        /// <summary>The bag cells already holding this item that have room above their vanilla max.</summary>
        private static IEnumerable<ItemDrop.ItemData> RoomyStacks(Inventory inv, ItemDrop.ItemData item) =>
            inv.GetAllItems().Where(x => x != item && Same(x, item) && x.m_stack < Max(inv, x, x.m_gridPos)).ToList();

        // ── save / load keep over-cap stacks ────────────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.Save)), HarmonyPrefix]
        private static void Save_Stash(Inventory __instance, out List<ItemDrop.ItemData> __state)
        {
            __state = null;
            // 1.0.359 (Fire: "Error loading world file. Check backups!" on converted worlds): vanilla upgrades an older world by re-saving
            // every container's Inventory inside ZDOMan.Load (ZDOMan.ConvertContainers), before any player or scene; items there can have no
            // m_shared yet. A Save patch must never throw (it aborts the whole world load): skip those items, and catch + log once.
            try
            {
            foreach (ItemDrop.ItemData it in __instance.GetAllItems())
            {
                if (it == null || it.m_shared == null || it.m_stack <= it.m_shared.m_maxStackSize || (it.m_customData != null && it.m_customData.ContainsKey(PurseStackKey))) continue;
                if (it.m_customData == null) it.m_customData = new Dictionary<string, string>();
                it.m_customData[TrueStackKey] = it.m_stack.ToString(CultureInfo.InvariantCulture);
                (__state = __state ?? new List<ItemDrop.ItemData>()).Add(it);
            }
            }
            catch (Exception e) { SaveGuard.Once("BagStackCap.Save_Stash", e); }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.Save)), HarmonyPostfix]
        private static void Save_Clean(List<ItemDrop.ItemData> __state)
        {
            try { if (__state != null) foreach (ItemDrop.ItemData it in __state) it?.m_customData?.Remove(TrueStackKey); } catch (Exception e) { SaveGuard.Once("BagStackCap.Save_Clean", e); }   // 1.0.359
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.Load), new[] { typeof(ZPackage) }), HarmonyPostfix]
        private static void Load_Restore(Inventory __instance)
        {
            try   // 1.0.359: a Load patch must never throw either (world conversion)
            {
            foreach (ItemDrop.ItemData it in __instance.GetAllItems())
            {
                if (it?.m_customData == null || !it.m_customData.TryGetValue(TrueStackKey, out string raw)) continue;
                it.m_customData.Remove(TrueStackKey);
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stack) && stack > it.m_stack) it.m_stack = stack;
            }
            }
            catch (Exception e) { SaveGuard.Once("BagStackCap.Load_Restore", e); }
        }

        // ── adding into bag cells past the vanilla max ──────────────────────────────────────────────────────────────

        // Pickup: top up the roomy bag stacks first. Runs after the coin purse's prefix (which handles coins and skips the original).
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) }), HarmonyPrefix, HarmonyPriority(Priority.Low)]
        private static bool AddItem_TopUp(Inventory __instance, ItemDrop.ItemData item, ref bool __result, bool __runOriginal)
        {
            if (!__runOriginal || item == null || __instance != LocalInventory || item.m_shared.m_maxStackSize <= 1) return true;
            int left = item.m_stack;
            foreach (ItemDrop.ItemData s in RoomyStacks(__instance, item))
            {
                int move = Math.Min(left, Max(__instance, s, s.m_gridPos) - s.m_stack);
                if (move <= 0) continue;
                s.m_stack += move; left -= move;
                if (left == 0) break;
            }
            if (left == item.m_stack) return true;
            __instance.Changed();
            if (left == 0) { __result = true; return false; }
            item.m_stack = left;
            return true;
        }

        // Any inventory: a big stack is added as vanilla-size pieces, never as one object. The pieces that fit are taken out of the
        // incoming item; if the target fills up, the item keeps the rest (MoveAll / take all leave it in the source, exact total).
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) }), HarmonyPrefix, HarmonyPriority(Priority.VeryLow)]
        private static bool AddItem_SplitBig(Inventory __instance, ItemDrop.ItemData item, ref bool __result, bool __runOriginal)
        {
            if (!__runOriginal || item == null || s_splitting) return true;
            int max = item.m_shared.m_maxStackSize;
            if (max < 1 || item.m_stack <= max) return true;
            int total = item.m_stack, pieces = 0;
            s_splitting = true;
            try
            {
                while (item.m_stack > max)
                {
                    ItemDrop.ItemData piece = item.Clone();
                    piece.m_stack = max;
                    if (!__instance.AddItem(piece)) break;
                    item.m_stack -= max;
                    pieces++;
                }
            }
            finally { s_splitting = false; }
            string name = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;
            Debug.Log($"[VAInventory] [BagStackCap] split {name} {total} -> {pieces} x {max}{(item.m_stack > 0 ? $" + {item.m_stack}" : "")} into {__instance.GetName()}");
            if (item.m_stack > max) { __result = false; return false; }   // full: the rest stays with the caller's source
            return true;                                                   // the last piece (<= vanilla) goes the vanilla way
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.CanAddItem), new[] { typeof(ItemDrop.ItemData), typeof(int) }), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void CanAddItem_Room(Inventory __instance, ItemDrop.ItemData item, int stack, ref bool __result)
        {
            if (__result || item == null || __instance != LocalInventory || item.m_shared.m_maxStackSize <= 1) return;
            int want = stack > 0 ? stack : item.m_stack;
            int room = RoomyStacks(__instance, item).Sum(s => Max(__instance, s, s.m_gridPos) - s.m_stack);
            if (room >= want) __result = true;
        }

        // Drag / drop or a move onto a bag cell holding the same item: merge up to that cell's cap (both sides changed here).
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveItemToThis), new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int) }), HarmonyPrefix]
        private static bool Move_MergeOrClamp(Inventory __instance, Inventory fromInventory, ItemDrop.ItemData item, ref int amount, int x, int y, ref bool __result)
        {
            if (item == null || fromInventory == null) return true;
            var pos = new Vector2i(x, y);
            ItemDrop.ItemData at = __instance.GetItemAt(x, y);
            int cap = Max(__instance, item, pos);
            if (at != null && at != item && Same(at, item) && cap > item.m_shared.m_maxStackSize)
            {
                int move = Math.Min(amount, cap - at.m_stack);
                if (move <= 0) return true;
                fromInventory.RemoveItem(item, move);
                at.m_stack += move;
                __instance.Changed();
                __result = true;
                return false;
            }
            // out of an over-cap stack into a vanilla cell: at most one vanilla stack moves, the rest stays where it was
            if (amount > item.m_shared.m_maxStackSize && cap <= item.m_shared.m_maxStackSize) amount = item.m_shared.m_maxStackSize;
            return true;
        }


        // A drag out of an over-cap stack to another grid (chest, main grid) or the ground: at most a vanilla stack at a time.
        [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem)), HarmonyPrefix]
        private static void Grid_Clamp(InventoryGrid __instance, ItemDrop.ItemData item, ref int amount, Vector2i pos)
        {
            if (item == null || amount <= item.m_shared.m_maxStackSize) return;
            if (Max(__instance.GetInventory(), item, pos) <= item.m_shared.m_maxStackSize) amount = item.m_shared.m_maxStackSize;
        }

        // only stacks in bag cells: the coin purse's own over-cap stack keeps its vanilla-mod behaviour
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DropItem)), HarmonyPrefix]
        private static void Ground_Clamp(Inventory inventory, ItemDrop.ItemData item, ref int amount)
        {
            if (item == null || amount <= item.m_shared.m_maxStackSize || inventory != LocalInventory || BagAt(item.m_gridPos) == null) return;
            amount = item.m_shared.m_maxStackSize;
        }

        // ── the stack count text ────────────────────────────────────────────────────────────────────────────────────

        private struct CountCell { public ItemDrop.ItemData Item; public int Stack; public float Recheck; public string Text; }
        private const float CountRecheck = 0.5f;
        private static CountCell[] s_countCache = new CountCell[0];

        [HarmonyPatch(typeof(InventoryGrid), "UpdateGui"), HarmonyPostfix]
        private static void Grid_Count(InventoryGrid __instance)
        {
            Inventory inv = __instance.GetInventory();
            if (inv == null || inv != LocalInventory || Api == null) return;
            if (!(s_elements.GetValue(__instance) is System.Collections.IList list)) return;
            // Valheim 1.0's InventoryElement has no m_pos: m_elements is laid out row by row (index = y * width + x), as vanilla's GetElement reads it.
            int width = inv.GetWidth();
            for (int i = 0; i < list.Count && width > 0; i++)
            {
                if (!(list[i] is InventoryElement e) || e.m_amount == null) continue;
                var pos = new Vector2i(i % width, i / width);
                TMPro.TMP_Text amount = e.m_amount;
                ItemDrop.ItemData it = inv.GetItemAt(pos.x, pos.y);
                if (it == null || it.m_shared.m_maxStackSize <= 1) continue;
                // 1.0.358 (UI efficiency audit V10): vanilla rewrites the count each frame, so ours is set each frame too, but the cap
                // (reflection) and the string are worked out once per (cell, item, stack) and re-checked every CountRecheck seconds
                if (i >= s_countCache.Length) System.Array.Resize(ref s_countCache, list.Count);
                var c = s_countCache[i];
                if (c.Item != it || c.Stack != it.m_stack || Time.unscaledTime >= c.Recheck)
                {
                    int max = Max(inv, it, pos);
                    c = new CountCell { Item = it, Stack = it.m_stack, Recheck = Time.unscaledTime + CountRecheck, Text = max > it.m_shared.m_maxStackSize ? $"{it.m_stack}/{max}" : null };
                    s_countCache[i] = c;
                }
                if (c.Text != null && amount.text != c.Text) amount.text = c.Text;
            }
        }

        /// <summary>The count text for a mounted grid's cell (VAInventoryGridApi): "stack/max" with the cell's real max.</summary>
        internal static string CountText(Inventory inv, ItemDrop.ItemData item) => $"{item.m_stack}/{Max(inv, item, item.m_gridPos)}";

        // ── a cap going away: split, never lose ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Call BEFORE a bag's cap goes away (its stack upgrade is removed or swapped, or the bag is unequipped): every stack in its
        /// cells over the vanilla max is cut to a vanilla stack, and the rest goes to free cells or is dropped at the player's feet.
        /// </summary>
        internal static void OnCapDropped(Player player, ItemDrop.ItemData bag)
        {
            if (player == null || bag == null || player != Player.m_localPlayer) return;
            Inventory inv = player.GetInventory();
            foreach (Vector2i cell in BackpackStorageSync.CellsOf(player, bag))
            {
                ItemDrop.ItemData it = inv.GetItemAt(cell.x, cell.y);
                if (it != null && it.m_stack > it.m_shared.m_maxStackSize) Split(player, inv, it, it.m_shared.m_maxStackSize, bag);
            }
        }

        /// <summary>Cuts the stack to keep, moves the rest in vanilla-size stacks to free cells (this bag first), else to the ground.</summary>
        private static void Split(Player player, Inventory inv, ItemDrop.ItemData it, int keep, ItemDrop.ItemData bag)
        {
            int extra = it.m_stack - keep;
            if (extra <= 0) return;
            string name = it.m_dropPrefab != null ? it.m_dropPrefab.name : it.m_shared.m_name;
            Vector2i at = it.m_gridPos;
            it.m_stack = keep;
            var freeBagCells = bag == null ? new List<Vector2i>() : BackpackStorageSync.CellsOf(player, bag)
                .Where(c => inv.GetItemAt(c.x, c.y) == null).ToList();
            var parts = new List<string>();
            while (extra > 0)
            {
                int chunk = Math.Min(extra, it.m_shared.m_maxStackSize);
                ItemDrop.ItemData piece = it.Clone();
                piece.m_stack = chunk;
                if (freeBagCells.Count > 0) { piece.m_gridPos = freeBagCells[0]; freeBagCells.RemoveAt(0); inv.GetAllItems().Add(piece); parts.Add($"{chunk} to a bag cell"); }
                else if (inv.FindEmptySlot(false) is Vector2i free && free.x >= 0 && BagAt(free) == null) { piece.m_gridPos = free; inv.GetAllItems().Add(piece); parts.Add($"{chunk} to the inventory"); }
                else
                {
                    HaloDrop.Pose(player.transform, 1f, 1f, Quaternion.identity, out Vector3 dropAt, out Quaternion dropRot);   // 1.0.353: along the ring's up on a halo ring
                    ItemDrop.DropItem(piece, chunk, dropAt, dropRot);
                    parts.Add($"{chunk} dropped at your feet");
                }
                extra -= chunk;
            }
            inv.Changed();
            Debug.Log($"[VAInventory] [BagStackCap] split {name} at {at.x},{at.y}: kept {keep}; {string.Join(", ", parts.ToArray())}");
        }

        private static bool IsInPurse(ItemDrop.ItemData it)
        {
            if (it.m_customData != null && it.m_customData.ContainsKey(PurseStackKey)) return true;
            Slots.Slot s = Slots.GetItemSlot(it);
            return s != null && s.IsCoinPurseSlot;
        }

        // backstop: any stack above its cell's cap (the cap went away without OnCapDropped, or a big stack was looted into the main
        // grid) is split the same way
        [HarmonyPatch(typeof(Player), "Update"), HarmonyPostfix]
        private static void Enforce(Player __instance)
        {
            if (__instance != Player.m_localPlayer || Time.time < s_nextEnforce || Api == null) return;
            s_nextEnforce = Time.time + EnforceInterval;
            Inventory inv = __instance.GetInventory();
            foreach (ItemDrop.ItemData it in inv.GetAllItems().ToList())
            {
                if (it == null || it.m_stack <= it.m_shared.m_maxStackSize || IsInPurse(it)) continue;   // the whole inventory, never the coin purse
                int max = Max(inv, it, it.m_gridPos);
                if (it.m_stack > max) Split(__instance, inv, it, max, BagAt(it.m_gridPos));
            }
        }
    }
}
