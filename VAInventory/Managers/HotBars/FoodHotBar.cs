// FoodHotBar — visual mirror of the equipment-panel food slots.
// Sibling of AmmoHotBar; identical lifecycle pattern lifted from
// shudnal-ExtraSlots' FoodSlotsHotBar. Drag-and-drop into food slots
// flows through vanilla InventoryGrid.DropItem on the equipment-panel
// grid cells; the bar is purely a readout.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Managers.HotBars
{
    public static class FoodHotBar
    {
        public const string barName = "VA_FoodHotBar";

        public static bool isDirty = true;

        private static HotkeyBar hotBar;
        private static RectTransform hotBarRect;
        private static readonly Dictionary<ItemDrop.ItemData, Vector2i> realPositions = [];

        public static void GetItems(List<ItemDrop.ItemData> bound)
        {
            var slots = Slots.GetFoodSlots();
            if (slots == null) return;
            realPositions.Clear();
            int hotbarIndex = 0;
            foreach (var slot in slots)
            {
                if (slot == null) continue;
                var item = slot.Item;
                if (item == null) continue;
                bound.Add(item);
                realPositions[item] = item.m_gridPos;
                item.m_gridPos = new Vector2i(hotbarIndex++, 0);
            }
        }

        public static void RestoreGridPos()
        {
            foreach (var kvp in realPositions)
            {
                if (kvp.Key != null)
                    kvp.Key.m_gridPos = kvp.Value;
            }
            realPositions.Clear();
        }

        public static ItemDrop.ItemData GetItemInSlot(int barElementIndex)
        {
            var slots = Slots.GetFoodSlots();
            if (slots == null || barElementIndex < 0 || barElementIndex >= slots.Length)
                return null;
            return slots[barElementIndex]?.Item;
        }

        internal static HotkeyBar GetHotkeyBar() => hotBar;

        public static void MarkDirty() => isDirty = true;

        internal static bool Refresh()
        {
            if (!isDirty) return false;
            isDirty = false;
            bool stateChanged = false;

            if (IsEnabled() && hotBarRect == null)
            {
                CreateBar();
                stateChanged = true;
            }
            else if (!IsEnabled() && hotBarRect != null)
            {
                ClearBar();
                stateChanged = true;
            }

            if (hotBarRect != null) ApplyTransform();
            return stateChanged;
        }

        public static bool IsEnabled()
        {
            try
            {
                if (ConfigManager.Instance == null) return false;
                if (!ConfigManager.Instance.configShowFoodHotbar.Value) return false;
            }
            catch { return false; }
            var slots = Slots.GetFoodSlots();
            return slots != null && slots.Length > 0;
        }

        public static bool IsOurBar(HotkeyBar bar) => bar != null && bar.name == barName;
        public static bool IsOurBar(string name) => name == barName;

        private static void CreateBar()
        {
            try
            {
                hotBarRect = HotBarController.InstantiateHotKeyBar(barName);
                if (hotBarRect == null)
                {
                    Debug.LogWarning("[VAInventory] FoodHotBar.CreateBar: failed to clone HotKeyBar.");
                    return;
                }
                hotBar = hotBarRect.GetComponent<HotkeyBar>();
                if (hotBar == null)
                {
                    UnityEngine.Object.Destroy(hotBarRect.gameObject);
                    hotBarRect = null;
                    return;
                }
                hotBar.m_selected = -1;
                foreach (var element in hotBar.m_elements)
                {
                    if (element?.m_go != null)
                        UnityEngine.Object.Destroy(element.m_go);
                }
                for (int i = hotBarRect.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.Destroy(hotBarRect.GetChild(i).gameObject);
                hotBar.m_elements.Clear();
                hotBar.m_items.Clear();

                var drag = hotBarRect.gameObject.GetComponent<HotBarDragHandle>()
                         ?? hotBarRect.gameObject.AddComponent<HotBarDragHandle>();
                drag.Init(hotBarRect, ConfigManager.Instance?.configFoodHotbarPosition);

                Debug.Log($"[VAInventory] FoodHotBar.CreateBar: created '{barName}'.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] FoodHotBar.CreateBar exception: {ex}");
                ClearBar();
            }
        }

        internal static void ClearBar()
        {
            try
            {
                if (hotBar != null) UnityEngine.Object.Destroy(hotBar.gameObject);
                if (hotBarRect != null) UnityEngine.Object.Destroy(hotBarRect.gameObject);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] FoodHotBar.ClearBar exception: {ex}");
            }
            hotBar = null;
            hotBarRect = null;
        }

        private static void ApplyTransform()
        {
            try
            {
                if (hotBarRect == null || ConfigManager.Instance == null) return;
                HotBarPlacement.Apply(hotBarRect, ConfigManager.Instance.configFoodHotbarPosition,
                    ConfigManager.Instance.configFoodHotbarScale.Value);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] FoodHotBar.ApplyTransform exception: {ex}");
            }
        }
    }
}
