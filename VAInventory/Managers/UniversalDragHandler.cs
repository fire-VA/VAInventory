using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System.Reflection;
using HarmonyLib;

namespace VerdantsAscent.Managers
{
    // Universal drag handler - works for both centered vanilla containers and corner-anchored custom panels
    public class UniversalDragHandler : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        [Tooltip("The RectTransform to actually move (usually the root window).")]
        public RectTransform TargetPanel;

        /// <summary>
        /// Optional callback fired on OnPointerUp with the final
        /// anchoredPosition. Used by the per-bag backpack panels to
        /// persist their dragged position to config so it survives
        /// across logouts.
        /// </summary>
        public System.Action<Vector2> OnPositionChanged;

        private Vector2 lastScreenPosition;
        private bool isDragging;
        private bool didMove;

        public bool IsDragging => isDragging;

        // Reflection for drag item/inventory
        private static readonly FieldInfo fi_dragItem = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        private static readonly FieldInfo fi_dragInventory = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");

        private void Awake()
        {
            if (TargetPanel == null)
            {
                TargetPanel = transform.parent as RectTransform;
                if (TargetPanel == null)
                {
                    Debug.LogError("[VAInventory] UniversalDragHandler: No TargetPanel assigned and no parent RectTransform found!");
                    enabled = false;
                }
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            // Only allow panel drag if NO item is on the cursor
            if (fi_dragItem.GetValue(InventoryGui.instance) is not null) return;
            lastScreenPosition = eventData.position;
            isDragging = true;
            didMove = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!isDragging) return;
            // Only allow panel drag if NO item is on the cursor
            if (fi_dragItem.GetValue(InventoryGui.instance) is not null) return;
            Vector2 currentScreenPos = eventData.position;
            Vector2 deltaScreen = currentScreenPos - lastScreenPosition;

            // Get the canvas scaler (important for resolution/scaling)
            CanvasScaler scaler = GetComponentInParent<CanvasScaler>();
            float scaleFactor = scaler != null ? scaler.scaleFactor : 1f;

            // Scale delta to match UI scale
            deltaScreen /= scaleFactor;

            // Now apply to anchoredPosition (works for both centered and corner anchors)
            TargetPanel.anchoredPosition += deltaScreen;
            if (deltaScreen.sqrMagnitude > 0f) didMove = true;

            lastScreenPosition = currentScreenPos;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            isDragging = false;

            // If the user actually dragged the panel (vs. just clicking
            // it without an item on the cursor), notify the owner so it
            // can persist the new position.
            if (didMove && OnPositionChanged != null && TargetPanel != null)
            {
                try { OnPositionChanged.Invoke(TargetPanel.anchoredPosition); }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[VAInventory] UniversalDragHandler.OnPositionChanged threw: {ex.Message}");
                }
            }
            didMove = false;
            if (fi_dragItem.GetValue(InventoryGui.instance) is not ItemDrop.ItemData dragItem || fi_dragInventory.GetValue(InventoryGui.instance) is not Inventory dragInventory) return;

            RectTransformUtility.ScreenPointToLocalPointInRectangle(TargetPanel, eventData.position, eventData.pressEventCamera, out Vector2 localPoint);
            var slot = FindSlotAtLocalPosition(localPoint);
            if (slot == null || !slot.IsActive || !slot.ItemFits(dragItem))
            {
                // Not over a valid slot: drop the item to the ground and clear drag state
                VerdantsAscent.InventoryInteraction.ForceDropItemOnGround(dragInventory, dragItem);
                VerdantsAscent.EquipmentPanel.ClearDragState();
                return;
            }
            // Valid drop: move item to slot
            dragItem.m_gridPos = slot.GridPosition;
            dragInventory.m_onChanged?.Invoke();
            VerdantsAscent.EquipmentPanel.ClearDragState();
        }

        private void CancelDrag()
        {
            // Robust drag state clear: only clear fields that exist in all versions
            var fi_dragGo = AccessTools.Field(typeof(InventoryGui), "m_dragGo");
            var fi_dragAmount = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
            fi_dragItem.SetValue(InventoryGui.instance, null);
            var dragGoObj = fi_dragGo?.GetValue(InventoryGui.instance) as GameObject;
            if (dragGoObj != null)
            {
                dragGoObj.SetActive(false);
                UnityEngine.Object.Destroy(dragGoObj); // Extra aggressive cleanup
                fi_dragGo?.SetValue(InventoryGui.instance, null);
            }
            fi_dragAmount?.SetValue(InventoryGui.instance, null);
        }

        private Slots.Slot FindSlotAtLocalPosition(Vector2 localPoint)
        {
            // Find the closest active slot whose rect contains the local point
            foreach (var slot in Slots.slots)
            {
                if (slot == null || !slot.IsActive) continue;
                // Assume slot.Position is top-left, tile size is70f (as in EquipmentPanel)
                var rect = new Rect(slot.Position.x, slot.Position.y - 70f, 70f, 70f);
                if (rect.Contains(localPoint))
                    return slot;
            }

            // Also check tool hotbar slots (they are separate and positioned relative to this panel)
            foreach (var tslot in Slots.toolHotbarSlots)
            {
                if (tslot == null || !tslot.IsActive) continue;
                var rect = new Rect(tslot.Position.x, tslot.Position.y - 70f, 70f, 70f);
                if (rect.Contains(localPoint))
                    return tslot;
            }

            return null;
        }
    }
}
