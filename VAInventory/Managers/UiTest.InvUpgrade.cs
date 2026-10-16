using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // `inventory_upgrade_test` (Fire 10-13): Valheim 1.0's trader inventory upgrades (Haldor's "invrows" +1 each, Player.SetInventorySize)
    // with VAInventory's layout. Two rounds, like private_keys_test, so a REAL relog sits between them:
    //   inventory_upgrade_test         - snapshot every item, fill the free main cells, +1 row the vanilla way, check, and LEAVE it applied
    //                                    (the expected items and positions go to BepInEx/FiresTests/inventory_upgrade_state.txt)
    //   inventory_upgrade_test verify  - after the relog: the row and every item survived; then fill the bought row, shrink back,
    //                                    nothing lost; clean up.
    internal static partial class UiTest
    {
        const string UpgradeCommand = "inventory_upgrade_test";
        const string UpgradeTag = "[InventoryUpgradeTest]";
        const string UpgradeKey = "inventory_upgrade_test";
        const string RowsKey = "invrows";   // Player.InventoryRowsKey
        const string StateFile = "inventory_upgrade_state.txt";

        sealed class UpgradeRun
        {
            internal int RowsBefore = -1;
            internal int Errors;
            internal bool Stale;   // round 1 ran on another build: verify skips its checks, cleanup still restores the rows
            internal Dictionary<string, string> Expected = new Dictionary<string, string>();   // identity -> "x,y" (all items)
        }
        static UpgradeRun upRun;

        static List<Step> UpgradeSteps(bool verify) => verify
            ? new List<Step>
            {
                new Step("rows_survived", UpRowsSurvived),
                new Step("items_survived", UpItemsSurvived),
                new Step("shrink_back", UpShrinkBack),
                new Step("no_errors", UpNoErrors),
                new Step("cleanup", UpCleanup),
            }
            : new List<Step>
            {
                new Step("snapshot_fill", UpSnapshotFill),
                new Step("add_row", UpAddRow),
                new Step("row_in_ui", UpRowInUi),
                new Step("items_kept", UpItemsKept),
                new Step("saved", UpSaved),
                new Step("no_errors", UpNoErrors),
            };

        internal static void RegisterInventoryUpgrade()
        {
            new Terminal.ConsoleCommand(UpgradeCommand,
                "[F5 test] [VAInventory] vanilla trader inventory rows (+1 row) with VAInventory's layout; 'verify' after a relog checks and cleans up",
                args =>
                {
                    if (running) { Say($"{UpgradeTag} already running."); return; }
                    bool verify = args != null && args.Length > 1 && string.Equals(args[1], "verify", StringComparison.OrdinalIgnoreCase);
                    if (VAInventory.Instance != null) VAInventory.Instance.StartCoroutine(RunAll(UpgradeSteps(verify), UpgradeTag));
                }, isCheat: true);
        }

        static string StatePath => Path.Combine(BepInEx.Paths.BepInExRootPath, ResultsFolder, StateFile);

        static void CountUpErrors(string text, string trace, LogType type)
        {
            if (upRun == null || (type != LogType.Error && type != LogType.Exception)) return;
            string all = text + " " + trace;
            if (all.IndexOf("VAInventory", StringComparison.Ordinal) >= 0 || all.IndexOf("Inventory", StringComparison.Ordinal) >= 0) upRun.Errors++;
        }

        // Anything a test put there: this test's stacks, bag_resize_test's wood, filler and given bag.
        static bool IsTestItem(ItemDrop.ItemData i) => i?.m_customData != null
            && (i.m_customData.ContainsKey(UpgradeKey) || i.m_customData.ContainsKey(TagKey) || i.m_customData.ContainsKey(FillerKey));

        static int Rows(Player me) => me.TryGetUniqueKeyValue(RowsKey, out var v) && int.TryParse(v, out int n) ? n : -1;

        // One line per item: what it is (not where). Two identical items get #1, #2 so the multiset survives as a dictionary.
        static Dictionary<string, Vector2i> Identities(IEnumerable<ItemDrop.ItemData> items)
        {
            var result = new Dictionary<string, Vector2i>();
            foreach (var it in items.OrderBy(i => i.m_gridPos.y).ThenBy(i => i.m_gridPos.x))
            {
                // R-1013Q: what the item IS, not what it holds right now. A worn bag's contents blob, its special slots and VAInventory's
                // own VA_ bookkeeping change with play (and other tests); a stack or durability can drop (eating, wear). None of that is
                // the relog question, which is "is this item still there, in its cell".
                string cd = it.m_customData == null ? "" : string.Join(";", it.m_customData
                    .Where(kv => !kv.Key.StartsWith("BackpackInventory") && !kv.Key.StartsWith("BackpackSpecial") && !kv.Key.StartsWith("VA_"))
                    .OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToArray());
                string id = $"{it.m_dropPrefab?.name ?? it.m_shared?.m_name}|q{it.m_quality}|v{it.m_variant}|{cd}";
                string key = id; int n = 1;
                while (result.ContainsKey(key)) key = id + "#" + (++n);
                result[key] = it.m_gridPos;
            }
            return result;
        }

        static void PutTagged(Inventory inv, string prefabName, Vector2i cell, int n)
        {
            var prefab = ObjectDB.instance.GetItemPrefab(prefabName);
            if (prefab == null) return;
            var it = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            it.m_dropPrefab = prefab;
            it.m_stack = 1;
            it.m_gridPos = cell;
            it.m_customData = new Dictionary<string, string> { [UpgradeKey] = n.ToString() };
            inv.GetAllItems().Add(it);
        }

        // ── round 1 ───────────────────────────────────────────────────────────────────────────────────────────────────
        static IEnumerator UpSnapshotFill(Step step)
        {
            upRun = null;
            if (!InGame(step)) yield break;
            var me = Player.m_localPlayer;
            upRun = new UpgradeRun { RowsBefore = Rows(me) };
            Application.logMessageReceived += CountUpErrors;
            if (upRun.RowsBefore < 0 || upRun.RowsBefore >= 9) { step.Skip($"invrows is {upRun.RowsBefore}: no room for +1 (max 9)"); upRun = null; yield break; }
            var inv = me.GetInventory();
            // R-1013T: leftovers of an earlier test run (bag_resize filler, an old run of this test) are not the tester's items: they
            // go first, logged, so the record holds only what the character really owns.
            var stray = inv.GetAllItems().Where(IsTestItem).ToList();
            foreach (var s in stray) inv.RemoveItem(s);
            if (stray.Count > 0) Log.LogMessage($"{UpgradeTag} removed {stray.Count} leftover test item(s) from earlier runs before the snapshot");
            // Fill every free main cell with a tagged stack (placed directly: vanilla AddItem would top up the tester's own stacks).
            int n = 0;
            for (int y = 0; y < upRun.RowsBefore; y++)
                for (int x = 0; x < inv.GetWidth(); x++)
                    if (inv.GetItemAt(x, y) == null) PutTagged(inv, FillerPrefab, new Vector2i(x, y), ++n);
            inv.m_onChanged?.Invoke();
            yield return null;
            upRun.Expected = Identities(inv.GetAllItems()).ToDictionary(kv => kv.Key, kv => kv.Value.x + "," + kv.Value.y);
            step.Pass($"invrows {upRun.RowsBefore}, VAInventory rows {Slots.VanillaPlayerRows}; {upRun.Expected.Count} item(s) recorded, {n} tagged {FillerPrefab} filled the free main cells");
        }

        // The vanilla way: the `inventorysize` command (onlyServer, so a dedicated-server client is refused), else
        // Player.SetInventorySize, the method the trader purchase (StoreGui) and the command both call (SHORTCUT, logged).
        static IEnumerator UpAddRow(Step step)
        {
            if (upRun == null) { step.Skip("snapshot did not run"); yield break; }
            var me = Player.m_localPlayer;
            int want = upRun.RowsBefore + 1;
            Console.instance?.TryRunCommand("inventorysize " + want, false, true);
            yield return null;
            string how = "vanilla 'inventorysize'";
            if (Rows(me) != want)
            {
                Log.LogMessage($"{UpgradeTag} SHORTCUT: 'inventorysize' was refused here (onlyServer); Player.SetInventorySize({want}), the trader purchase's own call");
                me.SetInventorySize(want);
                how = "Player.SetInventorySize (SHORTCUT)";
                yield return null;
            }
            yield return new WaitForSeconds(0.5f);
            if (Rows(me) == want && Slots.VanillaPlayerRows == want)
                step.Pass($"{how}: invrows {upRun.RowsBefore} -> {want}, VAInventory rows {Slots.VanillaPlayerRows}, inventory height {me.GetInventory().GetHeight()}");
            else step.Fail($"{how}: invrows {Rows(me)}, VAInventory rows {Slots.VanillaPlayerRows} (want {want})");
        }

        static IEnumerator UpRowInUi(Step step)
        {
            if (upRun == null) { step.Skip("snapshot did not run"); yield break; }
            yield return OpenInventory();
            for (int f = 0; f < SettleFrames; f++) yield return null;
            var me = Player.m_localPlayer;
            var grid = InventoryGui.instance?.m_playerGrid;
            int row = upRun.RowsBefore, width = me.GetInventory().GetWidth();
            if (grid == null) { step.Fail("no player grid"); yield break; }
            int shown = 0;
            for (int x = 0; x < width; x++)
            {
                int idx = row * width + x;
                if (idx < grid.m_elements.Count && grid.m_elements[idx] != null && grid.m_elements[idx].gameObject.activeInHierarchy) shown++;
            }
            if (shown == width) step.Pass($"the bought row {row} shows all {width} cells in the inventory grid");
            else step.Fail($"the bought row {row} shows {shown} of {width} cells");
        }

        // Main-grid items keep their cell; everything in VAInventory's slot regions (y >= the old rows) is exactly one row lower.
        static string CheckMoved(Player me, int oldRows, int shift, out bool ok, int onlyFromY = 0)
        {
            // round 1 (onlyFromY 0) checks its own filler too; verify compares the character's own items only, as recorded
            var now = Identities(me.GetInventory().GetAllItems().Where(i => i.m_gridPos.y >= onlyFromY && (onlyFromY == 0 || !IsTestItem(i))));
            var missing = new List<string>(); var wrong = new List<string>();
            foreach (var kv in upRun.Expected)
            {
                var p = kv.Value.Split(',');
                int x = int.Parse(p[0]), y = int.Parse(p[1]);
                var want = new Vector2i(x, y >= oldRows ? y + shift : y);
                if (!now.TryGetValue(kv.Key, out var at)) missing.Add(kv.Key);
                else if (at.x != want.x || at.y != want.y) wrong.Add($"{kv.Key.Split('|')[0]} {x},{y}->{at.x},{at.y} (want {want.x},{want.y})");
            }
            var extra = now.Keys.Where(k => !upRun.Expected.ContainsKey(k)).ToList();
            ok = missing.Count == 0 && wrong.Count == 0 && extra.Count == 0;
            return $"{now.Count} of {upRun.Expected.Count} item(s)" + (ok ? ", every one where it belongs"
                : $"; MISSING {missing.Count} [{string.Join(", ", missing.Take(5).ToArray())}] MOVED {wrong.Count} [{string.Join("; ", wrong.Take(5).ToArray())}] EXTRA {extra.Count}");
        }

        static IEnumerator UpItemsKept(Step step)
        {
            if (upRun == null) { step.Skip("snapshot did not run"); yield break; }
            yield return null;
            string line = CheckMoved(Player.m_localPlayer, upRun.RowsBefore, 1, out bool ok);
            if (ok) step.Pass("+1 row: " + line); else step.Fail("+1 row: " + line);
        }

        static IEnumerator UpSaved(Step step)
        {
            if (upRun == null) { step.Skip("snapshot did not run"); yield break; }
            yield return null;
            var me = Player.m_localPlayer;
            bool vaKey = me.m_customData.TryGetValue("VAInventory.VanillaPlayerRows", out var va) && va == (upRun.RowsBefore + 1).ToString();
            // What round 2 checks after the relog: the rows, and every item where it now is.
            // R-1013M: FDT clears the tester's bag (the main grid + hotbar) between runs, keeping equipped gear and VAInventory's slots.
            // So the filler goes now, and round 2 checks what survives that: every item in VAInventory's slot regions, the ones the
            // +1 row moved down (the relog risk: a load refusing y >= height before "invrows" is re-applied).
            var inv = me.GetInventory();
            foreach (var f in inv.GetAllItems().Where(i => i.m_customData.ContainsKey(UpgradeKey)).ToList()) inv.RemoveItem(f);
            int rowsNow = upRun.RowsBefore + 1;
            var lines = new List<string> { "rows_before=" + upRun.RowsBefore, "version=" + VAInventory.PluginVersion };
            lines.AddRange(Identities(inv.GetAllItems().Where(i => i.m_gridPos.y >= rowsNow && !IsTestItem(i))).Select(kv => $"{kv.Value.x},{kv.Value.y}\t{kv.Key}"));
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            File.WriteAllLines(StatePath, lines.ToArray());
            if (Rows(me) == upRun.RowsBefore + 1 && vaKey)
                step.Pass($"invrows {Rows(me)} and VAInventory's saved row count {va} are on the character; {lines.Count - 1} item(s) written for 'inventory_upgrade_test verify' after a relog. The +1 row stays until then.");
            else step.Fail($"invrows {Rows(me)}, VAInventory saved rows '{va}'");
            Application.logMessageReceived -= CountUpErrors;
        }

        static IEnumerator UpNoErrors(Step step)
        {
            if (upRun == null) { step.Skip("nothing ran"); yield break; }
            yield return null;
            if (upRun.Errors == 0) step.Pass("no inventory errors logged during the run");
            else step.Fail($"{upRun.Errors} inventory error(s) logged during the run (see LogOutput.log)");
        }

        // ── round 2 (after a relog) ───────────────────────────────────────────────────────────────────────────────────
        static IEnumerator UpRowsSurvived(Step step)
        {
            upRun = null;
            if (!InGame(step)) yield break;
            if (!File.Exists(StatePath)) { step.Skip("no saved state: run 'inventory_upgrade_test' first, then relog"); yield break; }
            var lines = File.ReadAllLines(StatePath);
            upRun = new UpgradeRun();
            Application.logMessageReceived += CountUpErrors;
            upRun.RowsBefore = int.Parse(lines[0].Substring("rows_before=".Length));
            // R-1013O: round 1 on one build and verify on another compared different records. A different (or missing) version skips.
            string recorded = lines.Length > 1 && lines[1].StartsWith("version=") ? lines[1].Substring("version=".Length) : "unknown";
            if (recorded != VAInventory.PluginVersion)
            {
                step.Skip($"round 1 ran on VAInventory {recorded}, this is {VAInventory.PluginVersion}: run 'inventory_upgrade_test' again, relog, then verify (cleanup still puts the rows back)");
                upRun.Stale = true;
                yield break;
            }
            foreach (var l in lines.Skip(2)) { int tab = l.IndexOf('\t'); if (tab > 0) upRun.Expected[l.Substring(tab + 1)] = l.Substring(0, tab); }
            yield return null;
            var me = Player.m_localPlayer;
            int want = upRun.RowsBefore + 1;
            if (Rows(me) == want && Slots.VanillaPlayerRows == want && me.GetInventory().GetHeight() >= want)
                step.Pass($"after the relog: invrows {Rows(me)}, VAInventory rows {Slots.VanillaPlayerRows}, height {me.GetInventory().GetHeight()}");
            else step.Fail($"after the relog: invrows {Rows(me)}, VAInventory rows {Slots.VanillaPlayerRows} (want {want})");
        }

        static IEnumerator UpItemsSurvived(Step step)
        {
            if (upRun == null || upRun.Stale) { step.Skip(upRun == null ? "rows_survived did not run" : "round 1 ran on another build"); yield break; }
            yield return null;
            string line = CheckMoved(Player.m_localPlayer, int.MaxValue, 0, out bool ok, upRun.RowsBefore + 1);   // the slot regions, saved after the +1: no shift
            if (ok) step.Pass("after the relog: " + line); else step.Fail("after the relog: " + line);
        }

        // Fill the bought row, then take it away again: its items must be relocated (or dropped), never lost.
        static IEnumerator UpShrinkBack(Step step)
        {
            if (upRun == null || upRun.Stale) { step.Skip(upRun == null ? "rows_survived did not run" : "round 1 ran on another build"); yield break; }
            var me = Player.m_localPlayer;
            var inv = me.GetInventory();
            int row = upRun.RowsBefore;
            int n = 1000;
            for (int x = 0; x < inv.GetWidth(); x++) if (inv.GetItemAt(x, row) == null) PutTagged(inv, FillerPrefab, new Vector2i(x, row), ++n);
            inv.m_onChanged?.Invoke();
            yield return null;
            int tagged = inv.GetAllItems().Count(i => i.m_customData.ContainsKey(UpgradeKey));
            Console.instance?.TryRunCommand("inventorysize " + row, false, true);
            yield return null;
            if (Rows(me) != row)
            {
                Log.LogMessage($"{UpgradeTag} SHORTCUT: Player.SetInventorySize({row}) (the command was refused here)");
                me.SetInventorySize(row);
            }
            yield return new WaitForSeconds(1f);
            int inInv = inv.GetAllItems().Count(i => i.m_customData.ContainsKey(UpgradeKey));
            int onGround = UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)
                .Count(d => d != null && d.m_itemData?.m_customData != null && d.m_itemData.m_customData.ContainsKey(UpgradeKey)
                            && Vector3.Distance(d.transform.position, me.transform.position) <= GroundMetres);
            bool ok = Rows(me) == row && Slots.VanillaPlayerRows == row && inInv + onGround == tagged;
            string line = $"{row + 1} -> {row} rows: {tagged} tagged item(s) before, {inInv} in the inventory + {onGround} on the ground after";
            if (ok) step.Pass(line); else step.Fail(line + $" (invrows {Rows(me)}, VAInventory rows {Slots.VanillaPlayerRows})");
        }

        static IEnumerator UpCleanup(Step step)
        {
            Application.logMessageReceived -= CountUpErrors;
            var me = Player.m_localPlayer;
            if (me == null) { step.Skip("no player"); yield break; }
            var inv = me.GetInventory();
            int removed = 0, destroyed = 0;
            foreach (var it in inv.GetAllItems().Where(i => i.m_customData.ContainsKey(UpgradeKey)).ToList()) { inv.RemoveItem(it); removed++; }
            foreach (var d in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None))
            {
                if (d == null || d.m_itemData?.m_customData == null || !d.m_itemData.m_customData.ContainsKey(UpgradeKey)) continue;
                var nview = d.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid()) { nview.ClaimOwnership(); ZNetScene.instance.Destroy(d.gameObject); destroyed++; }
            }
            int back = upRun != null ? upRun.RowsBefore : Rows(me);
            if (upRun != null && Rows(me) != back) me.SetInventorySize(back);
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
            yield return null;
            upRun = null;
            if (Rows(me) == back) step.Pass($"{removed} tagged item(s) removed, {destroyed} ground drop(s) destroyed, invrows back to {back}, state file removed");
            else step.Fail($"invrows {Rows(me)}, wanted {back}");
        }
    }
}
