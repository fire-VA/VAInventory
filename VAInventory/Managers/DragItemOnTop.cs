using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.279. Vanilla's drag item
    /// (InventoryGui.m_dragGo, made in SetupDragItem as a plain child of the InventoryGui object) has no sorting of its own, so anything with
    /// its own canvas sorting or a later draw order - the lifted, free dock panels (1.0.273/275) and their Core grip/overlay canvases
    /// (DockService sortingOrder 30900 / GripSortingOrder) - drew over it. Panels are never lowered (dock order stays); the drag item gets
    /// its own override canvas sorted above every inventory panel, grip and popup (DragOrder), and no raycasts so drops still hit the cells.
    /// </summary>
    /// <summary>
    /// Vanilla's
    /// shared tooltip (UITooltip.m_tooltip) has no sorting of its own, so the lifted panels' canvases covered it. It gets an override
    /// canvas at TooltipOrder (above the panels and Core's grips, under the cursor item) and no raycasts.
    /// </summary>
    [HarmonyPatch]
    internal static class TooltipOnTop
    {
        internal const int TooltipOrder = 32760;   // Was 32600 and still drew under the bag panel (Core lifts docked panels high)
        static GameObject s_done;
        static readonly System.Reflection.FieldInfo s_tooltip = AccessTools.Field(typeof(UITooltip), "m_tooltip");
        // guarded: a game version without UITooltip.LateUpdate or its static m_tooltip skips this patch (logged) instead of failing PatchAll
        static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(UITooltip), "LateUpdate") ?? AccessTools.Method(typeof(UITooltip), "Update");
        static bool Prepare()
        {
            bool ok = TargetMethod() != null && s_tooltip != null && s_tooltip.IsStatic;
            if (!ok) EquipmentPanel.PanelLog?.LogWarning("panels: tooltip-on-top skipped (no UITooltip.LateUpdate/Update or static m_tooltip in this game version)");
            return ok;
        }
        static void Postfix()
        {
            var go = s_tooltip?.GetValue(null) as GameObject;
            if (go == null || go == s_done) return;
            s_done = go;
            var cv = go.GetComponent<Canvas>() ?? go.AddComponent<Canvas>();
            cv.overrideSorting = true;
            cv.sortingOrder = TooltipOrder;
            foreach (var g in go.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            EquipmentPanel.PanelLog?.LogDebug($"panels: tooltip on top (own canvas, sortingOrder {TooltipOrder}) - '{go.name}' under '{(go.transform.parent != null ? go.transform.parent.name : "-")}'");
            // Once, every active canvas that could
            // draw over it - its sorting layer, order, override, root render mode - so the next shot names the one that wins
            var tipRoot = cv.rootCanvas;
            var sb = new System.Text.StringBuilder($"panels: tooltip canvas: layer '{cv.sortingLayerName}' order {cv.sortingOrder}, root '{(tipRoot != null ? tipRoot.name : "-")}' {(tipRoot != null ? tipRoot.renderMode.ToString() : "?")}; rivals:");
            int n = 0;
            foreach (var c in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (c == null || c == cv || !c.isActiveAndEnabled) continue;
                var root = c.rootCanvas;
                bool otherRoot = root != tipRoot;
                if (!otherRoot && !(c.overrideSorting && c.sortingOrder >= 30000)) continue;
                sb.Append($" ['{c.name}' layer '{c.sortingLayerName}' order {c.sortingOrder} override {c.overrideSorting}{(otherRoot ? $" ROOT '{(root != null ? root.name : "-")}' {(root != null ? root.renderMode.ToString() : "?")} order {(root != null ? root.sortingOrder : 0)}" : "")}]");
                if (++n >= 25) { sb.Append(" ..."); break; }
            }
            EquipmentPanel.PanelLog?.LogDebug(sb.ToString());
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "SetupDragItem")]
    internal static class DragItemOnTop
    {
        internal const int DragOrder = 32767;   // above Core's grips (30900+), VAInventory popups and the 32000 tooltips; under the 32767 cap
        static bool s_logged;

        static void Postfix(InventoryGui __instance)
        {
            var go = __instance.m_dragGo;
            if (go == null) return;
            var cv = go.GetComponent<Canvas>() ?? go.AddComponent<Canvas>();
            cv.overrideSorting = true;
            cv.sortingOrder = DragOrder;
            foreach (var g in go.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;   // the cell under the cursor takes the drop
            go.transform.SetAsLastSibling();
            if (!s_logged) { s_logged = true; EquipmentPanel.PanelLog?.LogDebug($"panels: drag item on top (own canvas, sortingOrder {DragOrder}, raycasts off) - parent '{go.transform.parent?.name}'"); }
        }
    }
}
