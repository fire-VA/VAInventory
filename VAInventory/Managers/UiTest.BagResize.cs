using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // `bag_resize_test`: a bag whose grid shrinks or grows never loses an item. The Explorer bag
    // is filled with tagged stacks, then resized through VABackpacks' own Rows/Columns config entries (the settings a player or admin
    // changes) and re-equipped, which is when a resize applies. Every step counts the tagged items in the bag, in the main grid and on
    // the ground near the tester: the item count and the total stack must never change. Everything given or changed is put back.
    internal static partial class UiTest
    {
        const string BagCommand = "bag_resize_test";
        const string BagTag = "[BagResizeTest]";
        const string BagsGuid = "com.Fire.VA_Backpacks";
        const string TestBag = "ExplorerBag_va";
        const string TagKey = "bag_resize_test";
        const string FillerKey = "bag_resize_filler";
        const string FillPrefab = "Wood";
        const string FillerPrefab = "Stone";
        const int LeaveMainFree = 6;
        const float GroundMetres = 8f;
        const float HeldMetres = 30f;   // a companion that picked a stack up may have walked off a little
        const float EquipSettleSeconds = 1.5f;

        sealed class BagRun
        {
            internal ItemDrop.ItemData Bag;
            internal ConfigEntry<int> Rows, Cols;
            internal int OrigRows, OrigCols;
            internal int Count, Stack;
            internal Dictionary<string, int> Expected = new Dictionary<string, int>();
            internal int Errors;
            internal bool AutoPickupWas = true;
            internal HashSet<int> HeldLogged = new HashSet<int>();
            internal Dictionary<int, Vector2i> CellOf = new Dictionary<int, Vector2i>();
        }
        static BagRun bagRun;

        static List<Step> BagSteps() => new List<Step>
        {
            new Step("fill", BagFill),
            new Step("shrink_full", BagShrinkFull),
            new Step("main_untagged", BagMainUntagged),
            new Step("grow", BagGrow),
            new Step("shove_skips_bags", BagShoveSkipsBags),
            new Step("save_roundtrip", BagSaveRoundtrip),
            new Step("nothing_lost", BagNothingLost),
            new Step("no_errors", BagNoErrors),
            new Step("cleanup", BagCleanup),
        };

        internal static void RegisterBagResize()
        {
            new Terminal.ConsoleCommand(BagCommand,
                "[F5 test] [VAInventory] a bag's grid shrinks and grows (VABackpacks Rows/Columns) without losing an item",
                args =>
                {
                    if (running) { Say($"{BagTag} already running."); return; }
                    if (VAInventory.Instance != null) VAInventory.Instance.StartCoroutine(RunAll(BagSteps(), BagTag));
                }, isCheat: true);
        }

        static void CountErrors(string text, string trace, LogType type)
        {
            // only this area's errors: VAInventory's and the bags' own lines, or an exception thrown through them
            if (bagRun == null || (type != LogType.Error && type != LogType.Exception)) return;
            string all = text + " " + trace;
            if (all.IndexOf("VAInventory", StringComparison.Ordinal) >= 0 || all.IndexOf("Backpack", StringComparison.Ordinal) >= 0) bagRun.Errors++;
        }

        // ── counting ───────────────────────────────────────────────────────
        // Every worn bag's cells but the test bag's (the tester also wears the Wizard Staff, a bag of its own).
        static HashSet<Vector2i> OtherBagCells(Player me)
        {
            var set = new HashSet<Vector2i>();
            var bags = BackpackBridge.GetEquippedBackpacks(me);
            if (bags != null)
                foreach (var b in bags)
                    if (b != null && b != bagRun.Bag)
                        foreach (var c in BackpackStorageSync.CellsOf(me, b)) set.Add(c);
            return set;
        }

        static (int inBag, int inMain, int onGround, int count, int stack) Tally(Player me)
        {
            var cells = new HashSet<Vector2i>(BackpackStorageSync.CellsOf(me, bagRun.Bag));
            var other = OtherBagCells(me);
            int inBag = 0, inMain = 0, onGround = 0, inOther = 0, stack = 0;
            foreach (var it in me.GetInventory().GetAllItems())
            {
                if (!it.m_customData.ContainsKey(TagKey)) continue;
                if (cells.Contains(it.m_gridPos)) inBag++;
                else if (other.Contains(it.m_gridPos)) inOther++;
                else inMain++;
                stack += it.m_stack;
            }
            if (inOther > 0) Log.LogMessage($"{BagTag} {inOther} tagged item(s) sit in another worn bag's cells");
            foreach (var drop in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
            {
                if (drop == null || drop.m_itemData == null || !drop.m_itemData.m_customData.ContainsKey(TagKey)) continue;
                if (Vector3.Distance(drop.transform.position, me.transform.position) > GroundMetres) continue;
                onGround++;
                stack += drop.m_itemData.m_stack;
            }
            return (inBag, inMain, onGround, inBag + inMain + onGround + inOther, stack);
        }

        // Every tagged item, by tag, with its stack: inventory and the ground near the tester. A tag seen twice is a duplicate.
        static Dictionary<string, int> Snapshot(Player me) => SnapshotWithDupes(me, out _);

        static Dictionary<string, int> SnapshotWithDupes(Player me, out List<string> dupes)
        {
            var seen = new Dictionary<string, int>();
            dupes = new List<string>();
            var all = me.GetInventory().GetAllItems().Select(x => x).ToList();
            foreach (var drop in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
                if (drop != null && drop.m_itemData != null && Vector3.Distance(drop.transform.position, me.transform.position) <= GroundMetres)
                    all.Add(drop.m_itemData);
            // R-1013P: Core's CompanionAutoPickup lets the tester's companions pick up the dropped stacks; they are still items,
            // held by another character near the tester. Counted (and named in the log) so they are not reported lost.
            foreach (var c in UnityEngine.Object.FindObjectsByType<Humanoid>(FindObjectsSortMode.None))
            {
                if (c == null || c == me || c.GetInventory() == null || Vector3.Distance(c.transform.position, me.transform.position) > HeldMetres) continue;
                var held = c.GetInventory().GetAllItems().Where(x => x.m_customData != null && x.m_customData.ContainsKey(TagKey)).ToList();
                if (held.Count == 0) continue;
                all.AddRange(held);
                if (bagRun != null && bagRun.HeldLogged.Add(c.GetInstanceID()))
                    Log.LogMessage($"{BagTag} {c.m_name} near the tester holds {held.Count} tagged stack(s) [{string.Join(",", held.Select(x => x.m_customData[TagKey]).ToArray())}] (picked up)");
            }
            foreach (var it in all)
            {
                if (it?.m_customData == null || !it.m_customData.TryGetValue(TagKey, out var tag)) continue;
                if (seen.ContainsKey(tag)) { dupes.Add(tag); seen[tag] += it.m_stack; }
                else seen[tag] = it.m_stack;
            }
            return seen;
        }

        // Per item, not just totals: the same tags, each with the same stack, none twice.
        static string Lossless(Player me, out bool ok)
        {
            var t = Tally(me);
            var now = SnapshotWithDupes(me, out var dupes);
            var missing = bagRun.Expected.Keys.Where(k => !now.ContainsKey(k)).ToList();
            var extra = now.Keys.Where(k => !bagRun.Expected.ContainsKey(k)).ToList();
            var changed = bagRun.Expected.Where(kv => now.TryGetValue(kv.Key, out var s) && s != kv.Value).Select(kv => $"{kv.Key}:{kv.Value}->{now[kv.Key]}").ToList();
            ok = missing.Count == 0 && extra.Count == 0 && changed.Count == 0 && dupes.Count == 0;
            string why = ok ? "" : $" - LOST [{string.Join(",", missing)}] EXTRA [{string.Join(",", extra)}] CHANGED [{string.Join(",", changed)}] TWICE [{string.Join(",", dupes)}]";
            return $"bag {t.inBag}, main {t.inMain}, ground {t.onGround}: {now.Count} of {bagRun.Count} tagged item(s), {now.Values.Sum()} of {bagRun.Stack} wood{why}";
        }

        static IEnumerator Reequip(Player me, int rows, int cols)
        {
            me.UnequipItem(bagRun.Bag, true);
            yield return new WaitForSeconds(EquipSettleSeconds);
            bagRun.Rows.Value = rows;   // the real config entries: what a player or admin changes
            bagRun.Cols.Value = cols;
            yield return null;
            me.EquipItem(bagRun.Bag, true);
            yield return new WaitForSeconds(EquipSettleSeconds);
        }

        // ── steps ──────────────────────────────────────────────────────────
        static IEnumerator BagFill(Step step)
        {
            bagRun = null;
            if (!InGame(step)) yield break;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(BagsGuid, out var bags) || bags.Instance == null)
            { step.Skip("VA_Backpacks is not loaded"); yield break; }
            var cfg = bags.Instance.Config;
            string section = "Backpack." + TestBag;
            if (!cfg.TryGetEntry(new ConfigDefinition(section, "Rows"), out ConfigEntry<int> rows)
                || !cfg.TryGetEntry(new ConfigDefinition(section, "Columns"), out ConfigEntry<int> cols))
            { step.Fail($"no [{section}] Rows/Columns in VA_Backpacks' config"); yield break; }

            var me = Player.m_localPlayer;
            bagRun = new BagRun { Rows = rows, Cols = cols, OrigRows = rows.Value, OrigCols = cols.Value };
            Application.logMessageReceived += CountErrors;
            // R-1013N: the tester auto-picked-up the dropped stacks within a second and vanilla merged them into tagged stacks
            // (customData ignored), so tags "changed" while the wood total held. Auto-pickup is off for the run (put back at cleanup).
            bagRun.AutoPickupWas = Player.m_enableAutoPickup;
            if (Player.m_enableAutoPickup)
            {
                Log.LogMessage($"{BagTag} SHORTCUT: auto-pickup off for the run (Player.m_enableAutoPickup, what vanilla's AutoPickup key toggles)");
                Player.m_enableAutoPickup = false;
            }
            if (rows.Value != 4 || cols.Value != 7) { rows.Value = 4; cols.Value = 7; yield return null; }

            bagRun.Bag = me.GetInventory().AddItem(TestBag, 1, 1, 0, 0L, "", false);
            if (bagRun.Bag == null) { step.Fail($"could not give {TestBag} (inventory full?)"); yield break; }
            bagRun.Bag.m_customData[FillerKey] = "bag";
            me.EquipItem(bagRun.Bag, true);
            yield return new WaitForSeconds(EquipSettleSeconds);

            var cells = BackpackStorageSync.CellsOf(me, bagRun.Bag);
            if (cells.Count == 0) { step.Fail("the equipped bag has no cells"); yield break; }
            var inv = me.GetInventory();
            // R-1013J: vanilla's positional AddItem first tops up ANY free stack of the same item (FindFreeStackItem, customData
            // ignored), so Wood 1..28 merged into 9 stacks and the test tagged items that never landed. Each stack is now put in its
            // cell directly, the way the bag restore itself places items.
            Log.LogMessage($"{BagTag} SHORTCUT: the fill places each tagged stack in its cell directly (vanilla AddItem would merge them)");
            var prefab = ObjectDB.instance.GetItemPrefab(FillPrefab);
            int i = 0;
            foreach (var cell in cells.OrderBy(c => c.y).ThenBy(c => c.x))
            {
                if (prefab == null || inv.GetItemAt(cell.x, cell.y) != null) continue;
                i++;
                var it = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                it.m_dropPrefab = prefab;
                it.m_stack = i;
                it.m_gridPos = cell;
                it.m_customData = new Dictionary<string, string> { [TagKey] = i.ToString() };
                inv.GetAllItems().Add(it);
                bagRun.CellOf[i] = cell;
            }
            inv.m_onChanged?.Invoke();
            yield return null;
            // The baseline is what is really there: every tag with its own stack.
            bagRun.Expected = Snapshot(me);
            bagRun.Count = bagRun.Expected.Count;
            bagRun.Stack = bagRun.Expected.Values.Sum();
            var wrong = bagRun.CellOf.Where(kv => !(inv.GetItemAt(kv.Value.x, kv.Value.y) is ItemDrop.ItemData at)
                || !at.m_customData.TryGetValue(TagKey, out var tag) || tag != kv.Key.ToString() || at.m_stack != kv.Key).Select(kv => kv.Key).ToList();
            if (bagRun.Count == cells.Count && wrong.Count == 0)
                step.Pass($"{TestBag} 7x4 equipped, all {cells.Count} cells hold their own tagged {FillPrefab} stack 1..{i} ({bagRun.Stack} wood)");
            else step.Fail($"{bagRun.Count} tagged item(s) in the inventory for {cells.Count} cells; wrong cells [{string.Join(",", wrong)}]");
        }

        // The worst case: the bag drops to 5x2 with only a few main-grid cells free, so items go to the main grid AND the ground.
        static IEnumerator BagShrinkFull(Step step)
        {
            if (bagRun?.Bag == null) { step.Skip("fill did not run"); yield break; }
            var me = Player.m_localPlayer;
            var inv = me.GetInventory();
            // Fill the main grid with tagged filler, then free LeaveMainFree cells again.
            var filler = new List<ItemDrop.ItemData>();
            me.UnequipItem(bagRun.Bag, true);
            yield return new WaitForSeconds(EquipSettleSeconds);
            // Placed directly like the fill (vanilla AddItem would top up the tester's own Stone, which cleanup would then remove).
            var stone = ObjectDB.instance.GetItemPrefab(FillerPrefab);
            // R-1013Q: only EMPTY main cells. TryMakeFreeSpaceInPlayerInventory made room by moving the tester's own items (its food
            // into the food pouch, merging stacks), so the test was rearranging a real inventory.
            for (int y = 0; stone != null && y < Slots.InventoryHeightPlayer; y++)
                for (int x = 0; x < Slots.InventoryWidth; x++)
                {
                    if (inv.GetItemAt(x, y) != null) continue;
                    var f = stone.GetComponent<ItemDrop>().m_itemData.Clone();
                    f.m_dropPrefab = stone;
                    f.m_stack = 1;
                    f.m_gridPos = new Vector2i(x, y);
                    f.m_customData = new Dictionary<string, string> { [FillerKey] = "1" };
                    inv.GetAllItems().Add(f);
                    filler.Add(f);
                }
            inv.m_onChanged?.Invoke();
            for (int k = 0; k < LeaveMainFree && filler.Count > 0; k++) { inv.RemoveItem(filler[filler.Count - 1]); filler.RemoveAt(filler.Count - 1); }
            me.EquipItem(bagRun.Bag, true);
            yield return new WaitForSeconds(EquipSettleSeconds);
            yield return Reequip(me, 2, 5);
            var t = Tally(me);
            string line = Lossless(me, out bool ok);
            bool shape = t.inBag == 10 && t.onGround > 0 && !bagRun.Bag.m_customData.ContainsKey("VA_BagOverflow");
            // the filler goes again: the main grid is free for grow
            foreach (var f in inv.GetAllItems().Where(x => x.m_customData.ContainsKey(FillerKey) && x != bagRun.Bag).ToList()) inv.RemoveItem(f);
            if (ok && shape) step.Pass($"7x4 -> 5x2 with {LeaveMainFree} main cells free: {line}");
            else step.Fail($"7x4 -> 5x2: {line}{(shape ? "" : " (want 10 in the bag, some on the ground, nothing parked)")}");
        }

        static IEnumerator BagGrow(Step step)
        {
            if (bagRun?.Bag == null) { step.Skip("fill did not run"); yield break; }
            var me = Player.m_localPlayer;
            var cells = BackpackStorageSync.CellsOf(me, bagRun.Bag);
            int minX = cells.Min(c => c.x), minY = cells.Min(c => c.y);
            var before = me.GetInventory().GetAllItems().Where(x => x.m_customData.ContainsKey(TagKey) && cells.Contains(x.m_gridPos))
                .ToDictionary(x => x.m_customData[TagKey], x => new Vector2i(x.m_gridPos.x - minX, x.m_gridPos.y - minY));
            yield return Reequip(me, 4, 7);
            cells = BackpackStorageSync.CellsOf(me, bagRun.Bag);
            minX = cells.Min(c => c.x); minY = cells.Min(c => c.y);
            var after = me.GetInventory().GetAllItems().Where(x => x.m_customData.ContainsKey(TagKey) && cells.Contains(x.m_gridPos))
                .ToDictionary(x => x.m_customData[TagKey], x => new Vector2i(x.m_gridPos.x - minX, x.m_gridPos.y - minY));
            var moved = before.Where(kv => !after.TryGetValue(kv.Key, out var p) || p.x != kv.Value.x || p.y != kv.Value.y).Select(kv => kv.Key).ToList();
            string line = Lossless(me, out bool ok);
            string size = bagRun.Bag.m_customData.TryGetValue("VA_BagSize", out var s) ? s : "?";
            if (ok && moved.Count == 0 && size == "7x4") step.Pass($"5x2 -> 7x4: the {before.Count} bag item(s) kept their cells, size {size}; {line}");
            else step.Fail($"5x2 -> 7x4: {moved.Count} moved [{string.Join(",", moved)}], size {size}; {line}");
        }

        // R-1013L audit: an item that overflowed to the main grid has left the bag, so it carries no bag owner tag (a stale one let a
        // later eviction treat it as the bag's and duplicate it).
        static IEnumerator BagMainUntagged(Step step)
        {
            if (bagRun?.Bag == null) { step.Skip("fill did not run"); yield break; }
            yield return null;
            var me = Player.m_localPlayer;
            var cells = new HashSet<Vector2i>(BackpackStorageSync.CellsOf(me, bagRun.Bag));
            cells.UnionWith(OtherBagCells(me));   // an item in another worn bag is that bag's, tag and all
            var tagged = me.GetInventory().GetAllItems()
                .Where(x => x.m_customData.ContainsKey(TagKey) && !cells.Contains(x.m_gridPos) && x.m_customData.ContainsKey("VA_OwnerBackpack"))
                .Select(x => $"{x.m_customData[TagKey]}@{x.m_gridPos.x},{x.m_gridPos.y} owner={x.m_customData["VA_OwnerBackpack"]}").ToList();
            int inMain = me.GetInventory().GetAllItems().Count(x => x.m_customData.ContainsKey(TagKey) && !cells.Contains(x.m_gridPos));
            if (tagged.Count == 0) step.Pass($"{inMain} overflow item(s) in the main grid, none with a bag owner tag");
            else step.Fail($"{tagged.Count} main-grid item(s) still tagged to a bag: [{string.Join(",", tagged)}]");
        }

        // R-1013L audit: "make free space" (used by unequip, right-click, row removal, validation) never moves an item into a bag
        // block. Main full, two of this bag's cells emptied: making space must use a typed slot or fail, never those two cells.
        static IEnumerator BagShoveSkipsBags(Step step)
        {
            if (bagRun?.Bag == null) { step.Skip("fill did not run"); yield break; }
            var me = Player.m_localPlayer;
            var inv = me.GetInventory();
            var cells = BackpackStorageSync.CellsOf(me, bagRun.Bag);
            // two bag cells emptied (their items parked in a list, put back after)
            var parked = cells.Select(c => inv.GetItemAt(c.x, c.y)).Where(i => i != null).Take(2).ToList();
            foreach (var it in parked) inv.GetAllItems().Remove(it);
            // main grid full
            var stone = ObjectDB.instance.GetItemPrefab(FillerPrefab);
            var filler = new List<ItemDrop.ItemData>();
            for (int y = 0; y < Slots.InventoryHeightPlayer; y++)
                for (int x = 0; x < Slots.InventoryWidth; x++)
                    if (stone != null && inv.GetItemAt(x, y) == null)
                    {
                        var f = stone.GetComponent<ItemDrop>().m_itemData.Clone();
                        f.m_dropPrefab = stone; f.m_stack = 1; f.m_gridPos = new Vector2i(x, y);
                        f.m_customData = new Dictionary<string, string> { [FillerKey] = "1" };
                        inv.GetAllItems().Add(f); filler.Add(f);
                    }
            inv.m_onChanged?.Invoke();
            yield return null;
            int bagBefore = cells.Count(c => inv.GetItemAt(c.x, c.y) != null);
            var intoBag = new List<string>();
            for (int k = 0; k < 3; k++)
            {
                if (!Slots.TryMakeFreeSpaceInPlayerInventory(true, out var pos)) break;
                foreach (var c in cells)
                {
                    var at = inv.GetItemAt(c.x, c.y);
                    if (at != null && !at.m_customData.ContainsKey(TagKey)) intoBag.Add($"{at.m_shared?.m_name}@{c.x},{c.y}");
                }
                // fill the freed main cell again so the next call has to make room too
                if (stone != null && inv.GetItemAt(pos.x, pos.y) == null)
                {
                    var f = stone.GetComponent<ItemDrop>().m_itemData.Clone();
                    f.m_dropPrefab = stone; f.m_stack = 1; f.m_gridPos = pos;
                    f.m_customData = new Dictionary<string, string> { [FillerKey] = "1" };
                    inv.GetAllItems().Add(f); filler.Add(f);
                }
            }
            int bagAfter = cells.Count(c => inv.GetItemAt(c.x, c.y) != null);
            // put everything back: filler out (wherever it went), the two parked items into their cells
            foreach (var f in inv.GetAllItems().Where(x => x.m_customData.ContainsKey(FillerKey) && x != bagRun.Bag).ToList()) inv.RemoveItem(f);
            foreach (var it in parked) if (inv.GetItemAt(it.m_gridPos.x, it.m_gridPos.y) == null) inv.GetAllItems().Add(it);
            inv.m_onChanged?.Invoke();
            yield return null;
            string line = Lossless(me, out bool ok);
            if (intoBag.Count == 0 && bagAfter == bagBefore && ok)
                step.Pass($"main full, 2 bag cells free: making space put nothing into the bag ({bagBefore} -> {bagAfter} bag cells used); {line}");
            else step.Fail($"making space moved into the bag: [{string.Join("; ", intoBag.Distinct())}] ({bagBefore} -> {bagAfter}); {line}");
        }

        // SHORTCUT (logged): a save/load of the bag's own blob (unequip writes it, equip reads it back) instead of a full logout/login.
        static IEnumerator BagSaveRoundtrip(Step step)
        {
            if (bagRun?.Bag == null) { step.Skip("fill did not run"); yield break; }
            var me = Player.m_localPlayer;
            Log.LogMessage($"{BagTag} SHORTCUT: the bag's blob round trip (unequip + equip) stands in for a relog");
            var t0 = Tally(me);
            yield return Reequip(me, 4, 7);
            var t1 = Tally(me);
            string line = Lossless(me, out bool ok);
            if (ok && t0.inBag == t1.inBag && t0.inMain == t1.inMain) step.Pass($"unequip + equip at 7x4: {line}");
            else step.Fail($"before bag {t0.inBag}/main {t0.inMain}, after bag {t1.inBag}/main {t1.inMain}; {line}");
        }

        static IEnumerator BagNothingLost(Step step)
        {
            if (bagRun?.Bag == null) { step.Skip("fill did not run"); yield break; }
            yield return null;
            string line = Lossless(Player.m_localPlayer, out bool ok);
            if (ok) step.Pass(line); else step.Fail(line);
        }

        static IEnumerator BagNoErrors(Step step)
        {
            if (bagRun == null) { step.Skip("fill did not run"); yield break; }
            yield return null;
            if (bagRun.Errors == 0) step.Pass("no errors logged during the run");
            else step.Fail($"{bagRun.Errors} error(s) logged during the run (see LogOutput.log)");
        }

        static IEnumerator BagCleanup(Step step)
        {
            Application.logMessageReceived -= CountErrors;
            if (bagRun != null) Player.m_enableAutoPickup = bagRun.AutoPickupWas;
            if (bagRun == null) { step.Skip("nothing to clean"); yield break; }
            var me = Player.m_localPlayer;
            int removed = 0, destroyed = 0;
            if (me != null)
            {
                var inv = me.GetInventory();
                if (bagRun.Bag != null) { me.UnequipItem(bagRun.Bag, true); yield return new WaitForSeconds(EquipSettleSeconds); }
                foreach (var it in inv.GetAllItems().Where(x => x.m_customData.ContainsKey(TagKey) || x.m_customData.ContainsKey(FillerKey)).ToList())
                { inv.RemoveItem(it); removed++; }
                // stacks a companion picked up go back out of its inventory too
                foreach (var c in UnityEngine.Object.FindObjectsByType<Humanoid>(FindObjectsSortMode.None))
                {
                    if (c == null || c == me || c.GetInventory() == null) continue;
                    foreach (var it in c.GetInventory().GetAllItems().Where(x => x.m_customData != null && x.m_customData.ContainsKey(TagKey)).ToList())
                    { c.GetInventory().RemoveItem(it); removed++; }
                }
                foreach (var drop in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
                {
                    if (drop == null || drop.m_itemData == null) continue;
                    var cd = drop.m_itemData.m_customData;
                    if (!cd.ContainsKey(TagKey) && !cd.ContainsKey(FillerKey)) continue;
                    var nview = drop.GetComponent<ZNetView>();
                    if (nview != null && nview.IsValid()) { nview.ClaimOwnership(); ZNetScene.instance.Destroy(drop.gameObject); destroyed++; }
                }
            }
            bagRun.Rows.Value = bagRun.OrigRows;
            bagRun.Cols.Value = bagRun.OrigCols;
            bool restored = bagRun.Rows.Value == bagRun.OrigRows && bagRun.Cols.Value == bagRun.OrigCols;
            bagRun = null;
            if (restored) step.Pass($"{removed} given item(s) removed (bag, wood, filler), {destroyed} ground drop(s) destroyed, Rows/Columns back to the originals");
            else step.Fail("Rows/Columns not restored");
        }
    }
}
