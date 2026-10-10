using UnityEngine;
using UnityEngine.EventSystems;
using HarmonyLib;

namespace VerdantsAscent.Managers
{
 public class EquipmentBackgroundDragCleanup : MonoBehaviour, IPointerUpHandler
 {
 public void OnPointerUp(PointerEventData eventData)
 {
 if (eventData.button != PointerEventData.InputButton.Left) return;

 var invGui = InventoryGui.instance;
 if (invGui == null) return;

 var dragItemField = AccessTools.Field(typeof(InventoryGui), "m_dragItem");

            if (dragItemField?.GetValue(invGui) is not null)
            {
                Debug.Log("[VAInventory] Mouse up on background - forcing full drag cleanup");

                // Force clear ALL drag state
                dragItemField.SetValue(invGui, null);

                var dragGoField = AccessTools.Field(typeof(InventoryGui), "m_dragGo");
                var dragGo = dragGoField?.GetValue(invGui) as GameObject;
                if (dragGo != null)
                {
                    dragGo.SetActive(false);
                    Object.Destroy(dragGo); // Extra aggressive cleanup
                    dragGoField.SetValue(invGui, null);
                }

                var dragAmountField = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
                dragAmountField?.SetValue(invGui, 0);

                VerdantsAscent.EquipmentPanel.MarkDirty();
            }

            eventData.Use();
 }
 }
}
