// HotBarDragHandle — mouse-drag-to-reposition for the cloned hotbars.
// Attached to each cloned bar's root GameObject by *HotBar.CreateBar.
//
// Design constraints (deliberate deviation from shudnal-ExtraSlots,
// which only repositions hotbars via config offsets):
//
//   1. The drag only engages when InventoryGui.m_dragItem == null.
//      That means while the user is moving an item between inventory
//      cells, the bar is not a drag target — so this can never steal a
//      drop event meant for a slot cell.
//
//   2. On EndDrag, the new anchoredPosition is written back to the
//      bar's config entry so it persists across reloads.
//
//   3. The component requires a raycast-target Image on the bar root
//      so PointerDown events on the bar background bubble to the
//      IBeginDragHandler. *HotBar.CreateBar adds a transparent Image
//      for this purpose. The image's alpha is ~0 so it's invisible.
//
//   4. The bar lives on its own override-sorting Canvas so its events
//      win over background HUD raycasts. We don't toggle blocksRaycasts
//      based on inventory visibility — the m_dragItem check above is
//      already enough to keep slot drops working.

using System;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using HarmonyLib;
using System.Reflection;

namespace VerdantsAscent.Managers.HotBars
{
    public class HotBarDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        // The bar's RectTransform — Init sets it.
        public RectTransform Bar;

        // Config entry to write the new anchoredPosition into on EndDrag.
        public ConfigEntry<Vector2> PositionConfig;

        private static readonly FieldInfo fi_dragItem = AccessTools.Field(typeof(InventoryGui), "m_dragItem");

        private Vector2 dragStartLocal;
        private Vector2 startAnchored;
        private bool dragging;

        public void Init(RectTransform bar, ConfigEntry<Vector2> positionConfig)
        {
            Bar = bar;
            PositionConfig = positionConfig;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            // If the user is moving an inventory item, this drag is for
            // the item — not the bar. Bail so InventoryGui keeps the
            // drag.
            if (HasActiveInventoryDrag()) return;

            if (Bar == null) Bar = GetComponent<RectTransform>();
            if (Bar == null) return;
            var parent = Bar.parent as RectTransform;
            if (parent == null) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, eventData.position, eventData.pressEventCamera, out dragStartLocal);
            startAnchored = Bar.anchoredPosition;
            dragging = true;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!dragging || Bar == null) return;
            var parent = Bar.parent as RectTransform;
            if (parent == null) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, eventData.position, eventData.pressEventCamera, out var local)) return;
            var delta = local - dragStartLocal;
            Bar.anchoredPosition = startAnchored + delta;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!dragging) return;
            dragging = false;
            if (Bar == null || PositionConfig == null) return;
            Bar.anchoredPosition = HotBarPlacement.ClampOnScreen(Bar, Bar.anchoredPosition);
            try { PositionConfig.Value = Bar.anchoredPosition; }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] HotBarDragHandle.OnEndDrag persist exception: {ex}");
            }
        }

        private static bool HasActiveInventoryDrag()
        {
            try
            {
                var gui = InventoryGui.instance;
                if (gui == null || fi_dragItem == null) return false;
                return fi_dragItem.GetValue(gui) != null;
            }
            catch { return false; }
        }
    }
}
