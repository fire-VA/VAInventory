using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// The Refill upgrade's "Mark for autofill" UI ([classes-ui]'s RefillMenu in VABackpacks 1.0.114+), wired into the player grid:
    /// a right-click on a cell of a bag with Refill opens its menu (instead of vanilla's use / equip), every grid refresh puts the
    /// autofill badge on the cells, and the item tooltip gets the badge's line. Reached by reflection; without RefillMenu nothing changes.
    /// </summary>
    [HarmonyPatch]   // 1.0.353: without the class attribute PatchAll skipped Badges, so the autofill badge never reached the grid
    internal static class RefillBridge
    {
        private static Type s_menu;
        private static MethodInfo s_hasRows, s_open, s_stateOf, s_badgeTooltip, s_ensureBadge;
        private static bool s_looked;
        private static readonly FieldInfo s_elements = AccessTools.Field(typeof(InventoryGrid), "m_elements");

        private static bool Ready()
        {
            if (!s_looked)
            {
                s_looked = true;
                s_menu = AccessTools.TypeByName("VerdantsAscent.Managers.RefillMenu");
                if (s_menu != null)
                {
                    s_hasRows = AccessTools.Method(s_menu, "HasRows");
                    s_open = AccessTools.Method(s_menu, "Open");
                    s_stateOf = AccessTools.Method(s_menu, "StateOf");
                    s_badgeTooltip = AccessTools.Method(s_menu, "BadgeTooltip");
                    s_ensureBadge = AccessTools.Method(s_menu, "EnsureBadge");
                }
                EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] refill menu: {(s_open != null ? "bound to VABackpacks' RefillMenu" : "not found (VABackpacks 1.0.114+ adds it)")}");
            }
            return s_open != null;
        }

        // A right-click on a cell of the bag that has Refill opens its menu instead of vanilla's use / equip.
        [HarmonyPatch(typeof(InventoryGui), "OnRightClickItem")]
        private static class RightClick
        {
            private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item)
            {
                var p = Player.m_localPlayer;
                if (item == null || p == null || grid == null || grid != __instance.m_playerGrid || !Ready()) return true;
                try
                {
                    if (!(bool)s_hasRows.Invoke(null, new object[] { p, item })) return true;
                    bool opened = (bool)s_open.Invoke(null, new object[] { p, item, (Vector2)Input.mousePosition });
                    return !opened;   // shown: vanilla's right-click doesn't run
                }
                catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] refill menu: right-click threw {(e.InnerException ?? e).Message}"); return true; }
            }
        }

        private const float BadgeHz = 4f;
        private static int s_badgeSig; private static float s_badgeNext;
        private static readonly object[] s_stateArgs = new object[2], s_badgeArgs = new object[2];   // 1.0.358: reused (Invoke copies nothing back)

        // Every player-grid refresh puts the autofill badge on each cell (state None clears a stale one).
        [HarmonyPatch(typeof(InventoryGrid), "UpdateGui"), HarmonyPostfix]
        private static void Badges(InventoryGrid __instance)
        {
            var p = Player.m_localPlayer;
            if (p == null || __instance.GetInventory() != p.GetInventory() || !Ready() || s_ensureBadge == null) return;
            if (!(s_elements?.GetValue(__instance) is IList list)) return;
            Inventory inv = p.GetInventory();
            // 1.0.358 (UI efficiency audit V2, Fire 10-26): this postfix runs EVERY frame while the inventory is open and made 2 reflection
            // Invokes (new object[] each) per cell. Now: only when the grid's items changed (a cheap signature) or at most BadgeHz, with
            // the argument arrays reused
            int sig = list.Count * 397 ^ inv.NrOfItems();
            foreach (var it in inv.GetAllItems()) if (it != null) sig = sig * 31 + it.m_stack + it.m_gridPos.x * 7 + it.m_gridPos.y * 131 + (it.m_shared != null ? it.m_shared.m_name.GetHashCode() : 0);
            if (sig == s_badgeSig && Time.unscaledTime < s_badgeNext) return;
            s_badgeSig = sig; s_badgeNext = Time.unscaledTime + 1f / BadgeHz;
            // Valheim 1.0's InventoryElement has no m_pos / m_go: m_elements is laid out row by row (index = y * width + x).
            int width = inv.GetWidth();
            for (int i = 0; i < list.Count && width > 0; i++)
            {
                if (!(list[i] is InventoryElement e)) continue;
                var pos = new Vector2i(i % width, i / width);
                GameObject go = e.gameObject;
                try
                {
                    s_stateArgs[0] = p; s_stateArgs[1] = inv.GetItemAt(pos.x, pos.y);
                    object state = s_stateOf.Invoke(null, s_stateArgs);
                    s_badgeArgs[0] = (RectTransform)go.transform; s_badgeArgs[1] = state;
                    s_ensureBadge.Invoke(null, s_badgeArgs);
                }
                catch { }
            }
        }

        /// <summary>The badge's tooltip line for this item, or null (used by the item tooltip).</summary>
        internal static string TooltipLine(ItemDrop.ItemData item)
        {
            var p = Player.m_localPlayer;
            if (item == null || p == null || !Ready() || s_badgeTooltip == null) return null;
            try { return s_badgeTooltip.Invoke(null, new object[] { p, item }) as string; } catch { return null; }
        }
    }
}
