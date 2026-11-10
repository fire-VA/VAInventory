using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers.HotBars
{
    /// <summary>
    /// With Core's hotbar kit present (Core 0.2.528+: KitHotbarRow / KitHotbarSlot)
    /// AND the Fires UI override ON, the Tool / Food / Ammo bars draw as kit HotbarRows - the same rounded wells, key chip under the slot,
    /// count strip and durability bar as the class bars - instead of vanilla's cloned HotKeyBar elements. The kit row lives INSIDE the bar's
    /// root (the HUD-dock element), so moving / docking / scaling is unchanged; vanilla's elements stay alive (other code reads them) but
    /// are hidden with a CanvasGroup. Override OFF (or an older Core) = the vanilla look, exactly as before.
    /// This class never names a Core kit type, so it loads on any Core; KitHotbarsImpl does, and is only touched after the type check.
    /// </summary>
    internal static class KitHotbars
    {
        static bool s_checked, s_available;

        internal static bool KitAvailable
        {
            get
            {
                if (!s_checked)
                {
                    s_checked = true;
                    s_available = Type.GetType("FiresCore.UI.Shell.Kit.KitHotbarRow, FiresUnifiedCore", false) != null;
                    EquipmentPanel.PanelLog?.LogDebug(s_available ? "hotbars: Core hotbar kit found; the bars draw as kit rows while the Fires UI override is ON"
                                                                  : "hotbars: no Core hotbar kit (Core older than 0.2.528); vanilla-style bars");
                }
                return s_available;
            }
        }

        static bool OverrideOn()
        {
            try { return Type.GetType("FiresCore.UI.Shell.FiresShell, FiresUnifiedCore")?.GetProperty("OverrideOn")?.GetValue(null) is bool b && b; }
            catch { return false; }
        }

        /// <summary>Called after each bar's vanilla refresh. slots = the bar's slot list; key = its key label per index.</summary>
        internal static void Paint(string barName, HotkeyBar bar, IList<Slots.Slot> slots, Func<int, string> key)
        {
            var root = bar != null ? bar.transform as RectTransform : null;
            if (root == null) return;
            bool on = KitAvailable && OverrideOn() && slots != null && slots.Count > 0;
            try
            {
                if (on) PaintKit(barName, root, slots, key);
                else if (KitAvailable) Restore(barName, root);
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"hotbars: {barName} kit paint failed: {e.Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void PaintKit(string barName, RectTransform root, IList<Slots.Slot> slots, Func<int, string> key) => KitHotbarsImpl.Paint(barName, root, slots, key);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Restore(string barName, RectTransform root) => KitHotbarsImpl.Restore(barName, root);
    }

    internal static class KitHotbarsImpl
    {
        const string RowNode = "HotbarRow";   // Core's Widgets.PathNode group name under the parent
        static readonly Dictionary<string, FiresCore.UI.Shell.Kit.KitHotbarRow> s_rows = new Dictionary<string, FiresCore.UI.Shell.Kit.KitHotbarRow>();

        /// <summary>The bar's live kit row (its drawn slots under the Fires UI override), or null.</summary>
        internal static RectTransform RowRoot(string barName) =>
            s_rows.TryGetValue(barName, out var row) && row != null && row.Root != null && row.Root.gameObject.activeInHierarchy ? row.Root : null;

        internal static void Paint(string barName, RectTransform root, IList<Slots.Slot> slots, Func<int, string> key)
        {
            s_rows.TryGetValue(barName, out var row);
            if (row == null || row.Root == null || row.Slots.Length != slots.Count)
            {
                if (row != null && row.Root != null) UnityEngine.Object.Destroy(row.Root.parent != null && row.Root.parent.name == RowNode ? row.Root.parent.gameObject : row.Root.gameObject);
                row = FiresCore.UI.Shell.Kit.Widgets.HotbarRow(root, barName, slots.Count, 0f, 0f);
                s_rows[barName] = row;
                EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] hotbars: {barName} drawn as a kit HotbarRow ({slots.Count} slot(s), {row.Width:0} wide)");
            }
            SetVanillaVisible(root, row, false);
            for (int i = 0; i < slots.Count; i++)
            {
                var s = row.Slots[i];
                var item = slots[i]?.Item;
                s.SetIcon(item != null ? item.GetIcon() : null, Color.white);
                s.SetCount(item != null ? item.m_stack : 0);
                s.SetDurability(item != null && item.m_shared.m_useDurability ? item.GetDurabilityPercentage() : -1f);
                string k = null; try { k = ShortKey(key?.Invoke(i)); } catch { }
                s.SetKey(k);
                s.SetState(item == null ? FiresCore.UI.Shell.Kit.HotbarSlotState.Empty : FiresCore.UI.Shell.Kit.HotbarSlotState.Normal);
            }
            // Each kit slot's own face takes the Tools pouch cell's look
            if (UnifiedScreen.PouchLook(out var look) && row.Root != null)
                foreach (Transform slot in row.Root)
                    UnifiedScreen.ApplyLook(slot.GetComponent<Image>(), look);
            ApplyFlow(barName, root, row);
        }

        // The dock rect's new size re-flows the kit slots into cols x rows (4x1 -> 2x2 ...).
        // Slots are scaled uniformly (never stretched), cell clamped to [MinCell, 64]; laid out from the bar rect's top-left. Persisted per bar.
        internal const float MinCell = 40f;
        const float Cell = 64f, Gap = 6f;
        static readonly Dictionary<string, Vector2> s_flow = new Dictionary<string, Vector2>();
        static bool s_flowLoaded;
        static string FlowFile => System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "cache", "VAInventory", "hotbar_layout.txt");

        // Core's SizeReset - forget the bar's saved size and lay the slots back at the default 1-row, 64 px look
        internal static void ResetFlow(string barName)
        {
            LoadFlow();
            s_flow.Remove(barName); s_flowApplied.Remove(barName);   // a later re-flow to the same size lays out again
            SaveFlow();
            if (s_rows.TryGetValue(barName, out var row) && row != null && row.Root != null)
            {
                float step = Cell + Gap;
                for (int i = 0; i < row.Slots.Length; i++)
                {
                    var rt = row.Slots[i].Root; if (rt == null) continue;
                    rt.localScale = Vector3.one;
                    var r = rt.rect;
                    var first = row.Slots[0].Root;
                    rt.localPosition = first != null && i > 0 ? new Vector3(first.localPosition.x + i * step, first.localPosition.y, rt.localPosition.z) : rt.localPosition;
                }
            }
            EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] hotbars: {barName} size reset to the default row");
        }

        internal static void Reflow(string barName, Vector2 want)
        {
            // A rect smaller than one cell is never a player's resize (a stray sliver fit would turn the bar vertical) - ignored
            // And never a size that can't hold all of the bar's slots at the minimum cell (40x40 for 4 tools = one vertical column)
            int slots = s_rows.TryGetValue(barName, out var r0) && r0 != null && r0.Slots != null ? r0.Slots.Length : 1;
            if (want.x < MinCell - 1f || want.y < MinCell * 0.5f || !Holds(want, slots)) { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] hotbars: {barName} re-flow to {want.x:0}x{want.y:0} ignored (can't hold its {slots} slot(s))"); return; }
            LoadFlow();
            s_flow[barName] = want;
            SaveFlow();
            if (s_rows.TryGetValue(barName, out var row) && row != null && row.Root != null) ApplyFlow(barName, null, row);
        }

        static void SaveFlow()
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FlowFile));
                var sb = new System.Text.StringBuilder();
                foreach (var kv in s_flow) sb.Append(kv.Key).Append('|').Append(kv.Value.x.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(kv.Value.y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                System.IO.File.WriteAllText(FlowFile, sb.ToString());
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"hotbars: layout save failed: {e.Message}"); }
        }

        static void LoadFlow()
        {
            if (s_flowLoaded) return;
            s_flowLoaded = true;
            try
            {
                if (!System.IO.File.Exists(FlowFile)) return;
                foreach (var line in System.IO.File.ReadAllLines(FlowFile))
                {
                    var p = line.Split('|');
                    if (p.Length == 3 && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float w)
                        && float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float h)) s_flow[p[0]] = new Vector2(w, h);
                }
            }
            catch { }
        }

        // True when a w x h box fits n slots at MinCell with Gap between them (any cols x rows split, 0.5 px slack)
        static bool Holds(Vector2 want, int n)
        {
            if (n <= 1) return want.x >= MinCell - 1f && want.y >= MinCell - 1f;
            int cols = Mathf.Max(1, Mathf.FloorToInt((want.x + Gap + 0.5f) / (MinCell + Gap)));
            int rows = Mathf.Max(1, Mathf.FloorToInt((want.y + Gap + 0.5f) / (MinCell + Gap)));
            return cols * rows >= n;
        }

        static readonly Dictionary<string, (FiresCore.UI.Shell.Kit.KitHotbarRow row, Vector4 sig)> s_flowApplied = new Dictionary<string, (FiresCore.UI.Shell.Kit.KitHotbarRow, Vector4)>();

        static void ApplyFlow(string barName, RectTransform root, FiresCore.UI.Shell.Kit.KitHotbarRow row)
        {
            LoadFlow();
            if (!s_flow.TryGetValue(barName, out var want) || row.Slots.Length == 0) return;
            int n = row.Slots.Length;
            // A flow that cannot hold all n slots at the minimum cell (e.g. Core's MinSize clamp of a sliver fit, saved through SizeChanged)
            // is not a player's layout - it is dropped (file rewritten) and the bar keeps its default one-row layout. A real vertical resize
            // (tall enough for every slot) still applies.
            if (!Holds(want, n))
            {
                s_flow.Remove(barName); SaveFlow();
                EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] hotbars: {barName} saved flow {want.x:0}x{want.y:0} can't hold its {n} slot(s) - dropped, default layout kept");
                return;
            }
            int cols = Mathf.Clamp(Mathf.FloorToInt((want.x + Gap) / (MinCell + Gap)), 1, n);
            int rows = (n + cols - 1) / cols;
            float cell = Mathf.Min((want.x - (cols - 1) * Gap) / cols, (want.y - (rows - 1) * Gap) / rows);
            cell = Mathf.Clamp(cell, MinCell, Cell);
            float k = cell / Cell, step = cell + Gap;
            // origin = the bar rect's top-left (Core keeps it fixed on resize), in the row's local space
            var bar = root != null ? root : row.Root.parent != null && row.Root.parent.name == RowNode ? row.Root.parent.parent as RectTransform : row.Root.parent as RectTransform;
            Vector3 origin = Vector3.zero;
            if (bar != null) { var c = new Vector3[4]; bar.GetWorldCorners(c); origin = row.Root.InverseTransformPoint(c[1]); }
            // Paint calls this on EVERY hotbar repaint (icons / counts), so the same flow was re-applied and logged hundreds of times a
            // minute (Fire's log 10-23, 654 lines in 3 min; hotbar_layout.txt untouched since 10-21, so not the resize path). It
            // re-lays only when the saved size, the slot count, the row or its origin changed.
            var sig = new Vector4(want.x, want.y, n, origin.x * 7919f + origin.y);
            if (s_flowApplied.TryGetValue(barName, out var last) && last.row == row && (last.sig - sig).sqrMagnitude < 0.01f) return;
            s_flowApplied[barName] = (row, sig);
            for (int i = 0; i < n; i++)
            {
                var rt = row.Slots[i].Root;
                if (rt == null) continue;
                int cx = i % cols, cy = i / cols;
                rt.localScale = new Vector3(k, k, 1f);
                // slot's own pivot offset so its top-left lands on the grid cell
                var r = rt.rect;
                float px = (rt.pivot.x * r.width) * k, py = ((1f - rt.pivot.y) * r.height) * k;
                rt.localPosition = new Vector3(origin.x + cx * step + px, origin.y - cy * step - py, rt.localPosition.z);
            }
            EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] hotbars: {barName} re-flowed to {cols}x{rows}, cell {cell:0} (rect {want.x:0}x{want.y:0})");
        }

        // A key with two or more modifiers is written with their initials ('C+A+1', 'S+A+1'; single modifiers stay 'Ctrl+1' / 'Alt+1'),
        // so it fits the kit's key chip
        static string ShortKey(string k)
        {
            if (string.IsNullOrEmpty(k)) return k;
            var parts = k.Split('+');
            if (parts.Length < 3) return k;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string m = parts[i].Trim();
                parts[i] = m.StartsWith("Ctrl", System.StringComparison.OrdinalIgnoreCase) ? "C" : m.StartsWith("Alt", System.StringComparison.OrdinalIgnoreCase) ? "A"
                    : m.StartsWith("Shift", System.StringComparison.OrdinalIgnoreCase) ? "S" : m;
            }
            return string.Join("+", parts);
        }

        internal static void Restore(string barName, RectTransform root)
        {
            if (!s_rows.TryGetValue(barName, out var row)) return;
            s_rows.Remove(barName);
            if (row != null && row.Root != null) UnityEngine.Object.Destroy(row.Root.parent != null && row.Root.parent.name == RowNode ? row.Root.parent.gameObject : row.Root.gameObject);
            SetVanillaVisible(root, null, true);
            EquipmentPanel.PanelLog?.LogInfo($"[VAInventory] hotbars: {barName} back to the vanilla look (override OFF)");
        }

        // vanilla's cloned elements: hidden while the kit row draws (kept alive, other code reads them); our kit row group is skipped
        static void SetVanillaVisible(RectTransform root, FiresCore.UI.Shell.Kit.KitHotbarRow row, bool visible)
        {
            foreach (Transform child in root)
            {
                if (child.name == RowNode) continue;
                var cg = child.GetComponent<CanvasGroup>();
                if (visible) { if (cg != null) { cg.alpha = 1f; cg.blocksRaycasts = true; } continue; }
                if (cg == null) cg = child.gameObject.AddComponent<CanvasGroup>();
                if (cg.alpha != 0f) { cg.alpha = 0f; cg.blocksRaycasts = false; }
            }
        }
    }
}
