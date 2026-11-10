using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.359: Inventory.Save / Load patches run inside ZDOMan.Load when vanilla converts an older world (ConvertContainers), before
    /// any player or scene exists. A throw there aborts the world load ("Error loading world file. Check backups!"), so every such
    /// patch catches and reports here: one warning per patch, with the exception, then silence.
    /// </summary>
    internal static class SaveGuard
    {
        private static readonly HashSet<string> s_seen = new HashSet<string>();
        internal static void Once(string where, Exception e)
        {
            if (!s_seen.Add(where)) return;
            Debug.LogWarning($"[VAInventory] {where} skipped an inventory it could not read (world conversion / no player yet): {e.GetType().Name}: {e.Message}. The save / load went on unchanged.");
        }
    }
}