using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// Keeps a picked-up item on the cursor when its inventory is reloaded underneath it. Vanilla's UpdateItemDrag cancels the drag
    /// as soon as m_dragInventory no longer holds that exact ItemData object. A companion's inventory is reloaded from its ZDO when its
    /// equipment changes (Core's CompanionInventory.LoadFromZDO), which swaps every ItemData, so an item picked from a companion fell
    /// off the cursor the next frame (Fire, 10-23). Picking up never removes the item (vanilla moves it on the drop), so after a reload
    /// the same item sits at the same cell as a new object: the drag is re-pointed at it. Every drag clear is logged with its caller.
    /// </summary>
    [HarmonyPatch]
    internal static class DragKeeper
    {
        [HarmonyPatch(typeof(InventoryGui), "UpdateItemDrag"), HarmonyPrefix]
        private static void Rebind(InventoryGui __instance)
        {
            var held = __instance.m_dragItem;
            var inv = __instance.m_dragInventory;
            if (held == null || inv == null || inv.ContainsItem(held)) return;
            var now = inv.GetItemAt(held.m_gridPos.x, held.m_gridPos.y);
            string name = held.m_dropPrefab != null ? held.m_dropPrefab.name : held.m_shared.m_name;
            string nowName = now == null ? null : now.m_dropPrefab != null ? now.m_dropPrefab.name : now.m_shared.m_name;
            if (now != null && nowName == name && now.m_quality == held.m_quality && now.m_stack >= __instance.m_dragAmount)
            {
                __instance.m_dragItem = now;
                EquipmentPanel.PanelLog?.LogDebug($"panels: drag keeper: {inv.GetName()} was reloaded under the cursor; {name} x{__instance.m_dragAmount} re-pointed at the reloaded item at {held.m_gridPos} (vanilla would have dropped the drag)");
            }
            else
                EquipmentPanel.PanelLog?.LogDebug($"panels: drag keeper: {name} is gone from {inv.GetName()} (cell {held.m_gridPos} now {(nowName ?? "empty")}): vanilla cancels the drag");
        }

        // Every clear or replace of the cursor item, with a short caller trace (Fire 10-23: prove what drops the companion pick-up).
        // Vanilla UpdateContainer cancels every drag whose inventory is neither the player's nor m_currentContainer's. The companion
        // screen isn't a Container, so a companion pick-up was cancelled the very next frame (proven by the log below, Fire 10-23).
        // While UpdateContainer runs, a clear of a drag from the OPEN companion panel's inventory is skipped; everything else is vanilla.
        private static bool s_inUpdateContainer;
        private static int s_keptFrame = -1;

        [HarmonyPatch(typeof(InventoryGui), "UpdateContainer"), HarmonyPrefix]
        private static void ContainerIn() => s_inUpdateContainer = true;

        [HarmonyPatch(typeof(InventoryGui), "UpdateContainer"), HarmonyFinalizer]
        private static void ContainerOut() => s_inUpdateContainer = false;

        [HarmonyPatch(typeof(InventoryGui), "SetupDragItem"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool LogSetup(InventoryGui __instance, object[] __args)
        {
            var before = __instance.m_dragItem;
            var next = __args != null && __args.Length > 0 ? __args[0] as ItemDrop.ItemData : null;
            if (before == null || before == next) return true;
            if (next == null && s_inUpdateContainer && CompanionPanel.Current != null && CompanionPanel.Current.OwnsInventory(__instance.m_dragInventory))
            {
                if (s_keptFrame < 0)
                    EquipmentPanel.PanelLog?.LogDebug($"panels: drag keeper: vanilla UpdateContainer tried to cancel {before.m_shared.m_name} x{__instance.m_dragAmount} from the open companion {__instance.m_dragInventory.GetName()}; kept on the cursor");
                s_keptFrame = Time.frameCount;
                return false;
            }
            s_keptFrame = -1;
            var st = new System.Diagnostics.StackTrace(2, false);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < st.FrameCount && i < 5; i++)
            {
                var m = st.GetFrame(i)?.GetMethod();
                if (m != null) sb.Append(i > 0 ? " < " : "").Append(m.DeclaringType?.Name).Append('.').Append(m.Name);
            }
            EquipmentPanel.PanelLog?.LogDebug($"panels: drag {(next == null ? "cleared" : "replaced")}: {before.m_shared.m_name} x{__instance.m_dragAmount} from {__instance.m_dragInventory?.GetName() ?? "none"}{(next != null ? " -> " + next.m_shared.m_name : "")}; by {sb}");
            return true;
        }
    }
}
