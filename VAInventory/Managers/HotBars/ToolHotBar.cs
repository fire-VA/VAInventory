// ToolHotBar — renders the tool bar by riding on Valheim's own
// HotkeyBar component.
//
// Architecture (mirrors shudnal-ExtraSlots/HotBars/QuickSlotsHotBar.cs):
//   1. We clone the vanilla HotKeyBar RectTransform and let its
//      HotkeyBar MonoBehaviour render icons, stacks, quality stars,
//      tooltips, and the equipped frame. No custom rendering code.
//   2. Vanilla HotkeyBar.UpdateIcons reads bound items from
//      Inventory.GetBoundItems(...). HotBarController patches that
//      method so when vanilla is updating OUR bar, it sees OUR slot
//      items instead of the player's hotkey-flagged inventory.
//   3. MarkDirty() / Refresh() pattern — the bar rebuilds on config
//      changes, never on a per-frame poll.
//
// This is now the only tool hotbar renderer after Phase G cleanup.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers.HotBars
{
    public static class ToolHotBar
    {
        // Canonical name for the cloned RectTransform. HotBarController
        // matches against this string in its Harmony patches; do not
        // rename casually.
        public const string barName = "VA_ToolHotBar";

        public static bool isDirty = true;

        private static HotkeyBar hotBar;
        private static RectTransform hotBarRect;
        private static readonly Dictionary<ItemDrop.ItemData, Vector2i> realPositions = [];

        // ── Slot enumeration ─────────────────────────────────────────

        /// <summary>
        /// Items currently held in tool-hotbar slots, in slot order.
        /// Mirrors shudnal-ExtraSlots.HotBars.QuickSlotsHotBar.GetItems:
        /// emit only the real items. Empty slots produce no element on
        /// the bar — drag-and-drop into tool slots happens through the
        /// height-extended player inventory grid (vanilla
        /// InventoryGrid.DropItem on the slot's GridPosition), not via
        /// the cloned bar. The bar is purely a visual readout.
        /// </summary>
        public static void GetItems(List<ItemDrop.ItemData> bound)
        {
            var slots = Slots.toolHotbarSlots;
            if (slots == null) return;
            realPositions.Clear();
            int hotbarIndex = 0;
            // Fire 10-06: box N = slot N = key N (empty slots stay empty boxes), matching the panel and the hotkeys.
            foreach (var slot in slots)
            {
                int slotIndex = hotbarIndex++;
                if (slot == null) continue;
                // 1.0.79 (Fire 10-08: the food bar showed a moose kebab with the food pouch empty): read the slot fresh from the
                // inventory; the slot's cached item can outlive the item (eaten, moved), and the bar then drew a stale stack.
                var item = slot.CacheItem();
                if (item == null) continue;
                bound.Add(item);
                realPositions[item] = item.m_gridPos;
                item.m_gridPos = new Vector2i(slotIndex, 0);
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
            var slots = Slots.toolHotbarSlots;
            if (slots == null || barElementIndex < 0 || barElementIndex >= slots.Count)
                return null;
            return slots[barElementIndex]?.Item;
        }

        /// <summary>
        /// The cloned bar's RectTransform — exposed so HotBarController's
        /// patches can access the live elements after vanilla's UpdateIcons.
        /// </summary>
        internal static HotkeyBar GetHotkeyBar() => hotBar;

        // ── Lifecycle ────────────────────────────────────────────────

        public static void MarkDirty() => isDirty = true;

        /// <summary>
        /// Called by HotBarController.Hud_Update_BarController each frame.
        /// Cheap when not dirty — just returns false.
        /// Returns true if the bar's existence/state changed (so callers
        /// can rebuild any cached references).
        /// </summary>
        internal static bool Refresh()
        {
            // 1.0.76 (Fire 10-08: filled pouches, no hotbars on the HUD): IsEnabled() changes with the inventory being open,
            // which never marked the bar dirty. A pouch filled with the inventory open left the bar un-built, and closing the
            // inventory never built it. Now the bar is rebuilt whenever IsEnabled() and the bar's existence disagree.
            bool enabled = IsEnabled();
            if (!isDirty && enabled == (hotBarRect != null)) return false;
            isDirty = false;
            if (enabled != (hotBarRect != null))
                Debug.Log($"[VAInventory] [Hotbars] Tool: {(enabled ? "shown" : "hidden")} ({WhyLine()})");

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

            if (hotBarRect != null)
            {
                ApplyTransform();
            }

            return stateChanged;
        }

        private static string WhyLine() => $"inventory open {InventoryGui.IsVisible()}, items {Slots.toolHotbarSlots?.Count(s => s != null && s.CacheItem() != null) ?? 0}/{Slots.toolHotbarSlots?.Count ?? 0}";

        public static bool IsEnabled()
        {
            try
            {
                if (ConfigManager.Instance == null) return false;
                // Unified: the CharacterPanel's Hotbars rows show these slots while the inventory is open, so the
                // HUD bar steps aside only then (Fire 10-06: the in-game HUD bar must still show them).
                if (UnifiedScreen.Enabled && InventoryGui.IsVisible()) return false;
            }
            catch { return false; }
            return Slots.toolHotbarSlots != null && Slots.toolHotbarSlots.Count > 0
                && (Slots.toolHotbarSlots.Any(s => s != null && s.CacheItem() != null) || (ConfigManager.Instance.configToolHotbarShowWhenEmpty?.Value ?? false));   // 1.0.79: empty pouch = no bar
        }

        public static bool IsOurBar(HotkeyBar bar)
            => bar != null && bar.name == barName;

        public static bool IsOurBar(string name)
            => name == barName;

        // ── Bar creation / teardown ──────────────────────────────────

        private static void CreateBar()
        {
            try
            {
                hotBarRect = HotBarController.InstantiateHotKeyBar(barName);
                if (hotBarRect == null)
                {
                    Debug.LogWarning("[VAInventory] ToolHotBar.CreateBar: failed to clone vanilla HotKeyBar.");
                    return;
                }

                hotBar = hotBarRect.GetComponent<HotkeyBar>();
                if (hotBar == null)
                {
                    Debug.LogWarning("[VAInventory] ToolHotBar.CreateBar: cloned bar has no HotkeyBar component.");
                    UnityEngine.Object.Destroy(hotBarRect.gameObject);
                    hotBarRect = null;
                    return;
                }

                // Vanilla HotkeyBar starts with elements left over from the
                // source HotKeyBar prefab clone. Clear them so UpdateIcons
                // rebuilds from our items instead of duplicating the source.
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

                // Phase E: attach drag-to-reposition handle. Persists
                // anchoredPosition to configToolHotbarPosition on EndDrag.
                var drag = hotBarRect.gameObject.GetComponent<HotBarDragHandle>()
                         ?? hotBarRect.gameObject.AddComponent<HotBarDragHandle>();
                drag.Init(hotBarRect, ConfigManager.Instance?.configToolHotbarPosition);

                Debug.Log($"[VAInventory] ToolHotBar.CreateBar: created '{barName}'.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] ToolHotBar.CreateBar exception: {ex}");
                ClearBar();
            }
        }

        internal static void ClearBar()
        {
            try
            {
                if (hotBar != null)
                    UnityEngine.Object.Destroy(hotBar.gameObject);
                if (hotBarRect != null)
                    UnityEngine.Object.Destroy(hotBarRect.gameObject);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] ToolHotBar.ClearBar exception: {ex}");
            }
            hotBar = null;
            hotBarRect = null;
        }

        private static void ApplyTransform()
        {
            try
            {
                if (hotBarRect == null || ConfigManager.Instance == null) return;
                HotBarPlacement.Apply(hotBarRect, ConfigManager.Instance.configToolHotbarPosition,
                    ConfigManager.Instance.configToolHotbarScale.Value);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] ToolHotBar.ApplyTransform exception: {ex}");
            }
        }
    }
}
