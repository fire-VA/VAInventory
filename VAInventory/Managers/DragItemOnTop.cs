using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// The item tooltip and the item on the cursor draw above every inventory panel. Each lives in its own ROOT screen-overlay canvas
    /// (TopLayer). A sorting order on a canvas nested somewhere else isn't enough, for two reasons:
    /// - the tooltip's own canvas ended up a WorldSpace root, and Unity draws every screen-overlay canvas (all the panels) above a
    ///   WorldSpace one, whatever the order;
    /// - another mod (FiresCompanions' companion screen) re-sorts the vanilla drag icon to its own order on every drag, under the
    ///   docked panels.
    /// The move is checked every frame, so whatever re-parents or re-sorts them later is undone and logged.
    /// </summary>
    internal static class TopLayer
    {
        internal const int TooltipOrder = 32760;   // above the docked panels and Core's grips (30900+)
        internal const int DragOrder = 32767;      // the highest order Unity allows: the cursor item is above everything

        static Canvas s_tooltipLayer, s_dragLayer;
        static string s_lastTooltip, s_lastDrag;   // the last "it had" line per kind: logged only when it changes

        static Canvas Layer(ref Canvas layer, string name, int order)
        {
            if (layer != null) return layer;
            var go = new GameObject(name);
            Object.DontDestroyOnLoad(go);
            layer = go.AddComponent<Canvas>();
            layer.renderMode = RenderMode.ScreenSpaceOverlay;
            layer.sortingOrder = order;
            EquipmentPanel.PanelLog?.LogDebug($"panels: top layer '{name}' made (screen-overlay root canvas, order {order})");
            return layer;
        }

        /// <summary>Moves <paramref name="go"/> under its top layer when it isn't there; logs what had it before.</summary>
        internal static void Keep(GameObject go, bool tooltip)
        {
            if (go == null) return;
            var layer = tooltip ? Layer(ref s_tooltipLayer, "VAInventory_TooltipLayer", TooltipOrder) : Layer(ref s_dragLayer, "VAInventory_DragLayer", DragOrder);
            var own = go.GetComponent<Canvas>();
            bool moved = go.transform.parent != layer.transform;
            bool resorted = own != null && own.overrideSorting;
            if (!moved && !resorted) return;
            string before = $"parent '{(go.transform.parent != null ? go.transform.parent.name : "-")}', root {(own != null && own.rootCanvas != null ? own.rootCanvas.renderMode.ToString() : "-")}"
                + (own != null ? $", own canvas override {own.overrideSorting} order {own.sortingOrder}" : "");
            if (moved && tooltip) go.transform.SetParent(layer.transform, true);   // keeps its on-screen size and position
            // The drag icon is born under vanilla's inventory screen, which is scaled down to nothing while it is hidden (the companion
            // screen opens without it): keeping its world scale kept it invisible. It takes the layer's GUI scale instead, and the layer's
            // driver moves it to the mouse every frame (vanilla's own UpdateItemDrag doesn't run while that screen is hidden).
            if (moved && !tooltip) { go.transform.SetParent(layer.transform, false); go.transform.localScale = Vector3.one; DragDriver.Ensure(layer); }
            if (own != null) own.overrideSorting = false;              // it takes the layer's order, so no one can sort it under the panels
            foreach (var g in go.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;   // the cell under the cursor takes the click
            if (tooltip ? before == s_lastTooltip : before == s_lastDrag) return;
            if (tooltip) s_lastTooltip = before; else s_lastDrag = before;
            EquipmentPanel.PanelLog?.LogDebug($"panels: {(tooltip ? "tooltip" : "drag item")} '{go.name}' kept on the top layer (order {(tooltip ? TooltipOrder : DragOrder)}); it had {before}");
        }
    }

    /// <summary>
    /// On the drag layer: matches the GUI's scale every frame, keeps the cursor item under the mouse even while vanilla's inventory
    /// screen is hidden, and logs a probe line the frame after each pick-up (where the icon is, how big, and on what).
    /// </summary>
    internal sealed class DragDriver : MonoBehaviour
    {
        private Canvas _layer;
        private Canvas _guiCanvas; private InventoryGui _guiCanvasOf;
        private static int s_probeFrame = -1;
        private static string s_probeFor;

        internal static void Ensure(Canvas layer)
        {
            var d = layer.GetComponent<DragDriver>() ?? layer.gameObject.AddComponent<DragDriver>();
            d._layer = layer;
        }

        /// <summary>Asks for one probe line on the next frame (called after each pick-up).</summary>
        internal static void ProbeNextFrame(string what) { s_probeFrame = Time.frameCount + 1; s_probeFor = what; }

        private void LateUpdate()
        {
            var gui = InventoryGui.instance;
            if (gui == null || _layer == null) return;
            if (_guiCanvas == null || _guiCanvasOf != gui) { _guiCanvas = gui.GetComponentInParent<Canvas>(); _guiCanvasOf = gui; }   // 1.0.364 (UI audit V13): looked up once, not every frame
            var guiCanvas = _guiCanvas;
            if (guiCanvas != null && guiCanvas.rootCanvas != null && Mathf.Abs(_layer.scaleFactor - guiCanvas.rootCanvas.scaleFactor) > 0.001f)
                _layer.scaleFactor = guiCanvas.rootCanvas.scaleFactor;
            try { AcceptGlow.Tick(); } catch { }
            var go = gui.m_dragGo;
            if (go == null) return;
            TopLayer.Keep(go, false);
            go.transform.position = Input.mousePosition;
            if (s_probeFrame >= 0 && Time.frameCount >= s_probeFrame)
            {
                s_probeFrame = -1;
                var rt = go.transform as RectTransform;
                var own = go.GetComponentInParent<Canvas>();
                EquipmentPanel.PanelLog?.LogDebug($"panels: drag probe ({s_probeFor}): icon active {go.activeInHierarchy}, parent '{go.transform.parent?.name}', canvas {(own != null ? own.rootCanvas.renderMode + " order " + own.rootCanvas.sortingOrder : "none")}, "
                    + $"at {(Vector2)go.transform.position} (mouse {(Vector2)Input.mousePosition}), size {(rt != null ? rt.rect.size : Vector2.zero)} x scale {go.transform.lossyScale.x:0.###}, "
                    + $"inventory screen visible {InventoryGui.IsVisible()} (its scale {gui.transform.lossyScale.x:0.###})");
            }
        }
    }

    [HarmonyPatch]
    internal static class TooltipOnTop
    {
        static readonly System.Reflection.FieldInfo s_tooltip = AccessTools.Field(typeof(UITooltip), "m_tooltip");
        // guarded: a game version without UITooltip.LateUpdate or its static m_tooltip skips this patch (logged) instead of failing PatchAll
        static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(UITooltip), "LateUpdate");   // 1.0.353: 1.0 has no UITooltip.Update, so the old fallback was dead
        static bool Prepare()
        {
            bool ok = TargetMethod() != null && s_tooltip != null && s_tooltip.IsStatic;
            if (!ok) EquipmentPanel.PanelLog?.LogWarning("panels: tooltip-on-top skipped (no UITooltip.LateUpdate/Update or static m_tooltip in this game version)");
            return ok;
        }
        [HarmonyPriority(Priority.Last)]
        static void Postfix() => TopLayer.Keep(s_tooltip?.GetValue(null) as GameObject, true);
    }

    [HarmonyPatch(typeof(InventoryGui), "SetupDragItem")]
    internal static class DragItemOnTop
    {
        [HarmonyPriority(Priority.Last)]
        static void Postfix(InventoryGui __instance, ItemDrop.ItemData item, Inventory inventory)
        {
            TopLayer.Keep(__instance.m_dragGo, false);
            if (item != null) DragDriver.ProbeNextFrame($"{item.m_shared.m_name} x{item.m_stack} from {(inventory == null ? "?" : inventory.GetName())}");
        }
    }

    // Another mod may re-sort or re-parent the drag icon after SetupDragItem (FiresCompanions does on every drag): checked each frame.
    [HarmonyPatch(typeof(InventoryGui), "Update")]
    internal static class DragItemKeepOnTop
    {
        [HarmonyPriority(Priority.Last)]
        static void Postfix(InventoryGui __instance)
        {
            long t0 = InvGuiTimer.Now;
            if (__instance.m_dragGo != null) TopLayer.Keep(__instance.m_dragGo, false);
            InvGuiTimer.DragTicks += InvGuiTimer.Now - t0;
        }
    }
}
