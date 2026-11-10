using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.365 (lead 10-28 perf: Fire's overlay had InventoryGui.Update at 26 ms/s with the inventory OPEN). MEASURING only: the whole
    /// InventoryGui.Update (vanilla + every mod's prefix / postfix: a Priority.First prefix starts it, a finalizer stops it) split by
    /// inventory open / closed, and VAInventory's own two per-frame postfixes (DragItemKeepOnTop, TabRowEveryFrame = LayoutRow, split
    /// into its quick "nothing changed" path and its full relayout). Every 30 s, Debug, only when something ran:
    /// "[Perf] InventoryGui.Update (ms/s over 30 s): total X (open Y over N frames, closed Z); VAInventory: tab row quick A / full B (n), drag-on-top C".
    /// </summary>
    internal static class InvGuiTimer
    {
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VAInventory.Panel");
        private static readonly Stopwatch s_total = new Stopwatch();
        internal static long OpenTicks, ClosedTicks, RowQuickTicks, RowFullTicks, DragTicks; internal static int OpenFrames, ClosedFrames, RowFull;
        private static float s_windowStart = -1f;

        internal static long Now => Stopwatch.GetTimestamp();
        internal static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        [HarmonyPatch(typeof(InventoryGui), "Update")]
        private static class Whole
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First)]
            private static void Prefix() { s_total.Reset(); s_total.Start(); }

            [HarmonyFinalizer]
            private static void Finalizer()
            {
                s_total.Stop();
                if (InventoryGui.IsVisible()) { OpenTicks += s_total.ElapsedTicks; OpenFrames++; } else { ClosedTicks += s_total.ElapsedTicks; ClosedFrames++; }
                if (s_windowStart < 0f) s_windowStart = Time.realtimeSinceStartup;
                float span = Time.realtimeSinceStartup - s_windowStart;
                if (span < 30f) return;
                if (OpenFrames + ClosedFrames > 0)
                    Log.LogDebug($"[Perf] InventoryGui.Update (ms/s over {span:0} s): total {Ms(OpenTicks + ClosedTicks) / span:0.00} (open {Ms(OpenTicks) / span:0.00} over {OpenFrames} frames = {(OpenFrames > 0 ? Ms(OpenTicks) / OpenFrames : 0):0.000} ms/frame; closed {Ms(ClosedTicks) / span:0.00} over {ClosedFrames}); VAInventory: tab row quick {Ms(RowQuickTicks) / span:0.00} / full {Ms(RowFullTicks) / span:0.00} ({RowFull} relayouts), drag-on-top {Ms(DragTicks) / span:0.00}");
                OpenTicks = ClosedTicks = RowQuickTicks = RowFullTicks = DragTicks = 0; OpenFrames = ClosedFrames = RowFull = 0; s_windowStart = Time.realtimeSinceStartup;
            }
        }
    }
}
