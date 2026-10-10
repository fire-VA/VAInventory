// HotBarController — Harmony-patch hub that lets vanilla HotkeyBar
// render our cloned tool hotbar without us re-implementing icons,
// stacks, drag, tooltips, etc.
//
// Architecture (mirrors shudnal-ExtraSlots/HotBars/QuickBars.cs):
//
//   - InstantiateHotKeyBar(name): clone the vanilla HotKeyBar
//     RectTransform once and parent it under the HUD root.
//
//   - HotkeyBar.UpdateIcons Pre/Postfix: when vanilla updates OUR
//     cloned bar, set inCall=true so our Inventory.GetBoundItems
//     postfix knows to swap in our items. Postfix also adjusts
//     element positions for layout customization.
//
//   - Inventory.GetBoundItems Postfix: when inCall is set and the
//     inventory is the player's, clear the bound list and add our
//     slot items instead. Vanilla then renders OUR items in OUR bar.
//
//   - Hud.Update Postfix: drives ToolHotBar.Refresh() each frame
//     (cheap unless dirty), and calls UpdateItemUse() to handle
//     hotkey shortcuts for items in our slots.
//
// This is the canonical hotbar controller after Phase G cleanup.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Managers.HotBars
{
    public static class HotBarController
    {
        // ── Bar instantiation ────────────────────────────────────────

        /// <summary>
        /// Clone the vanilla HotKeyBar RectTransform and parent it
        /// under the HUD root. Returns the cloned RectTransform with
        /// the given name. Returns null if the HUD or vanilla bar
        /// can't be located (we're being called too early).
        /// </summary>
        public static RectTransform InstantiateHotKeyBar(string barName)
        {
            try
            {
                if (Hud.instance == null || Hud.instance.m_rootObject == null)
                    return null;
                var src = Hud.instance.m_rootObject.transform.Find("HotKeyBar");
                if (src == null) return null;
                var srcRect = src.GetComponent<RectTransform>();
                if (srcRect == null) return null;

                // Clone the vanilla HotKeyBar (mirrors shudnal-ExtraSlots
                // QuickBars.InstantiateHotKeyBar) and parent under the HUD
                // root. Slot drag-and-drop still flows through vanilla
                // InventoryGrid.DropItem on the height-extended player
                // grid — the bar itself is a visual readout.
                //
                // Phase E adds a Canvas + GraphicRaycaster + transparent
                // background Image so the HotBarDragHandle component on
                // the bar root can receive PointerDown / IBeginDrag
                // events for mouse-drag-to-reposition. The drag handle
                // gates on InventoryGui.m_dragItem == null, so when the
                // user is moving inventory items the bar yields and slot
                // drops still work.
                var clone = UnityEngine.Object.Instantiate(srcRect, Hud.instance.m_rootObject.transform, true);
                clone.name = barName;
                clone.localPosition = Vector3.zero;
                clone.SetSiblingIndex(srcRect.GetSiblingIndex() + 1);

                var ovCanvas = clone.gameObject.GetComponent<Canvas>()
                             ?? clone.gameObject.AddComponent<Canvas>();
                ovCanvas.overrideSorting = true;
                ovCanvas.sortingOrder = 50;  // above HUD (default 0), below open InventoryGui (~100)
                if (clone.gameObject.GetComponent<GraphicRaycaster>() == null)
                    clone.gameObject.AddComponent<GraphicRaycaster>();

                if (clone.gameObject.GetComponent<UnityEngine.UI.Graphic>() == null)
                {
                    var bg = clone.gameObject.AddComponent<UnityEngine.UI.Image>();
                    bg.color = new Color(0f, 0f, 0f, 0.001f);
                    bg.raycastTarget = true;
                }
                return clone;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] HotBarController.InstantiateHotKeyBar exception: {ex}");
                return null;
            }
        }

        // ── Hotkey input ─────────────────────────────────────────────

        /// <summary>
        /// Walk our tool slots, check for shortcut press on each, and
        /// fire Player.UseItem for the matching item. Mirrors the
        /// QuickBars.UpdateItemUse pattern.
        /// </summary>
        internal static void UpdateItemUse()
        {
            try
            {
                var player = Player.m_localPlayer;
                if (player == null) return;
                if (!player.TakeInput()) return;

                if (TryUseSlotItems(player, Slots.toolHotbarSlots, HotkeyInput.GetToolHotbarKeybindText)) return;
                if (TryUseSlotItems(player, Slots.GetFoodSlots(), i => ConfigManager.Instance.GetFoodHotbarKeybindText(i))) return;
                TryUseSlotItems(player, Slots.GetAmmoSlots(), i => ConfigManager.Instance.GetAmmoHotbarKeybindText(i));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] HotBarController.UpdateItemUse exception: {ex}");
            }
        }

        private static bool TryUseSlotItems(Player player, IList<Slots.Slot> slots, Func<int, string> keybindTextProvider)
        {
            if (player == null || slots == null || slots.Count == 0) return false;

            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot == null || !slot.IsActive) continue;
                var item = slot.Item;
                if (item == null) continue;
                if (!HotkeyInput.IsShortcutDown(keybindTextProvider(i))) continue;

                // UseItem is the same call vanilla uses for hotkey
                // bar actions (Player.UseHotbarItem internals).
                // Equipable items toggle equip; consumables consume;
                // tools select.
                bool previousSuppressUnequipReturn = InventoryInteraction.suppressUnequipReturnToInventory;
                try
                {
                    if (slot.IsAmmoSlot)
                        InventoryInteraction.suppressUnequipReturnToInventory = true;
                    player.UseItem(player.GetInventory(), item, false);
                }
                finally
                {
                    InventoryInteraction.suppressUnequipReturnToInventory = previousSuppressUnequipReturn;
                }
                return true;
            }

            return false;
        }

        // ── Harmony patches ──────────────────────────────────────────

        /// <summary>
        /// Per-frame entry point. Refresh() is cheap when not dirty.
        /// UpdateItemUse handles hotkey item activation.
        /// </summary>
        [HarmonyPatch(typeof(Hud), "Update")]
        private static class Hud_Update_BarController
        {
            private static void Postfix()
            {
                var player = Player.m_localPlayer;
                if (player == null) return;

                // Each bar's IsEnabled() short-circuits when the new
                // path is disabled or the bar is configured off, so
                // calling Refresh() unconditionally is cheap.
                ToolHotBar.Refresh();
                AmmoHotBar.Refresh();
                FoodHotBar.Refresh();

                UpdateBarIcons(ToolHotBar.GetHotkeyBar(), player);
                UpdateBarIcons(AmmoHotBar.GetHotkeyBar(), player);
                UpdateBarIcons(FoodHotBar.GetHotkeyBar(), player);

                // 1.0.94: state + on-screen check per bar (HotBarPlacement.Report).
                int tools = Slots.toolHotbarSlots?.Count(s => s != null && s.CacheItem() != null) ?? 0;
                int ammo = Slots.GetAmmoSlots()?.Count(s => s != null && s.CacheItem() != null) ?? 0;
                int food = Slots.GetFoodSlots()?.Count(s => s != null && s.CacheItem() != null) ?? 0;
                string open = InventoryGui.IsVisible() ? "inventory open: the panel shows the pouches" : null;
                HotBarPlacement.Report("Tool", ToolHotBar.GetHotkeyBar()?.transform as RectTransform, open ?? (tools == 0 ? "tools pouch empty" : "not built yet"), tools);
                HotBarPlacement.Report("Ammo", AmmoHotBar.GetHotkeyBar()?.transform as RectTransform, open ?? (ammo == 0 ? "ammo pouch empty" : "not built yet / ShowHotbar off"), ammo);
                HotBarPlacement.Report("Food", FoodHotBar.GetHotkeyBar()?.transform as RectTransform, open ?? (food == 0 ? "food pouch empty" : "not built yet / ShowHotbar off"), food);
                HotBarPlacement.ReportDone();
                var cfg = ConfigManager.Instance;
                if (cfg != null)
                    HotBarPlacement.StackFromAmmo(ToolHotBar.GetHotkeyBar()?.transform as RectTransform,
                        FoodHotBar.GetHotkeyBar()?.transform as RectTransform, AmmoHotBar.GetHotkeyBar()?.transform as RectTransform,
                        cfg.configToolHotbarPosition.Value, cfg.configFoodHotbarPosition.Value, cfg.configAmmoHotbarPosition.Value,
                        Mathf.Abs(((Vector2)cfg.configFoodHotbarPosition.DefaultValue).y - ((Vector2)cfg.configToolHotbarPosition.DefaultValue).y));
                HotBarPlacement.KeepAboveVitals(ToolHotBar.GetHotkeyBar()?.transform as RectTransform,
                    FoodHotBar.GetHotkeyBar()?.transform as RectTransform, AmmoHotBar.GetHotkeyBar()?.transform as RectTransform);

                if (ToolHotBar.IsEnabled())
                    UpdateItemUse();
            }
        }

        private static void UpdateBarIcons(HotkeyBar bar, Player player)
        {
            try
            {
                if (bar == null || player == null) return;
                bar.UpdateIcons(player);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] HotBarController.UpdateBarIcons exception for '{bar?.name}': {ex}");
            }
        }

        /// <summary>
        /// Tear down the bar when the HUD goes away (logout, scene
        /// reload). Vanilla destroys the source HotKeyBar in this
        /// callback, so our clone goes with it — but explicit cleanup
        /// keeps our static refs in sync.
        /// </summary>
        [HarmonyPatch(typeof(Hud), "OnDestroy")]
        private static class Hud_OnDestroy_ClearBar
        {
            private static void Postfix()
            {
                ToolHotBar.ClearBar();
                AmmoHotBar.ClearBar();
                FoodHotBar.ClearBar();
                ToolHotBar.MarkDirty();
                AmmoHotBar.MarkDirty();
                FoodHotBar.MarkDirty();
            }
        }

        /// <summary>
        /// When vanilla HotkeyBar.UpdateIcons runs for OUR cloned bar,
        /// flag inCall so the GetBoundItems postfix returns our items.
        /// We don't skip the original — we want vanilla to do its
        /// rendering work, just on the right item list.
        /// </summary>
        [HarmonyPatch(typeof(HotkeyBar), "UpdateIcons")]
        public static class HotkeyBar_UpdateIcons_BindOurItems
        {
            public static bool inCall;
            public static string barName;

            private static void Prefix(HotkeyBar __instance)
            {
                // Match by name across all three of our cloned bars so
                // their UpdateIcons calls all route through the same
                // GetBoundItems postfix dispatch.
                if (__instance == null) return;
                if (!ToolHotBar.IsOurBar(__instance)
                    && !AmmoHotBar.IsOurBar(__instance)
                    && !FoodHotBar.IsOurBar(__instance)) return;
                inCall = true;
                barName = __instance.name;
            }

            private static void Postfix(HotkeyBar __instance)
            {
                if (!inCall) return;
                inCall = false;

                ApplyBindingLabels(__instance);

                if (ToolHotBar.IsOurBar(__instance))
                    ToolHotBar.RestoreGridPos();
                else if (AmmoHotBar.IsOurBar(__instance))
                    AmmoHotBar.RestoreGridPos();
                else if (FoodHotBar.IsOurBar(__instance))
                    FoodHotBar.RestoreGridPos();
            }
        }

        private static void ApplyBindingLabels(HotkeyBar bar)
        {
            try
            {
                if (bar == null || bar.m_elements == null) return;
                var slots = GetVisibleSlotsForBar(bar);
                if (slots == null || slots.Count == 0) return;

                for (int elementIdx = 0; elementIdx < bar.m_elements.Count && elementIdx < slots.Count; elementIdx++)
                {
                    var slot = slots[elementIdx];
                    var elem = bar.m_elements[elementIdx];
                    if (elem?.m_go == null || slot == null) continue;

                    if (ToolHotBar.IsOurBar(bar))
                    {
                        int slotIndex = Slots.toolHotbarSlots.IndexOf(slot);
                        OverrideBindingLabel(elem.m_go, slotIndex >= 0 ? slotIndex : elementIdx);
                    }
                    else
                    {
                        ApplySlotLabel(elem.m_go, slot);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] HotBarController.ApplyBindingLabels exception: {ex}");
            }
        }

        private static List<Slots.Slot> GetVisibleSlotsForBar(HotkeyBar bar)
        {
            if (ToolHotBar.IsOurBar(bar))
                return Slots.toolHotbarSlots?.ToList();
            if (AmmoHotBar.IsOurBar(bar))
                return Slots.GetAmmoSlots()?.ToList();
            if (FoodHotBar.IsOurBar(bar))
                return Slots.GetFoodSlots()?.ToList();
            return null;
        }

        private static void ApplySlotLabel(GameObject go, Slots.Slot slot)
        {
            try
            {
                var bindingT = go.transform.Find("binding");
                if (bindingT == null) return;
                var rect = bindingT.GetComponent<RectTransform>();
                var tmp = bindingT.GetComponent<TMPro.TMP_Text>();
                if (rect == null || tmp == null || slot == null) return;
                EquipmentPanel.SetSlotLabel(rect, tmp, slot, true);
            }
            catch { }
        }

        private static void OverrideBindingLabel(GameObject go, int slotIndex)
        {
            try
            {
                var bindingT = go.transform.Find("binding");
                if (bindingT == null) return;
                var tmp = bindingT.GetComponent<TMPro.TMP_Text>();
                if (tmp == null) return;

                string label = HotkeyInput.GetToolHotbarKeybindText(slotIndex);
                tmp.text = label;

                // Vanilla's binding label is sized + positioned for a
                // single-char label and left-aligned. For "Alt+1" we
                // need a smaller font, single line, top-of-slot
                // horizontal-center alignment, and a wider RectTransform
                // so the multi-char text doesn't get clipped at the slot
                // edge.
                tmp.enableAutoSizing = false;
                tmp.fontSize = 12f;
                tmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
                tmp.overflowMode = TMPro.TextOverflowModes.Overflow;
                tmp.alignment = TMPro.TextAlignmentOptions.Top;

                var bindingRT = bindingT as RectTransform;
                if (bindingRT != null)
                {
                    // Anchor across the parent slot's full width, pinned
                    // to the top, with the text rect centered. Reuses
                    // the slot element's RectTransform width as the
                    // reference span.
                    var parentRT = go.transform as RectTransform;
                    float slotW = parentRT != null ? parentRT.rect.width : 64f;
                    bindingRT.anchorMin = new Vector2(0.5f, 1f);
                    bindingRT.anchorMax = new Vector2(0.5f, 1f);
                    bindingRT.pivot = new Vector2(0.5f, 1f);
                    bindingRT.sizeDelta = new Vector2(slotW, bindingRT.sizeDelta.y);
                    bindingRT.anchoredPosition = new Vector2(0f, bindingRT.anchoredPosition.y);
                }
            }
            catch { /* binding label is cosmetic — never throw */ }
        }


        /// <summary>
        /// During HotkeyBar.UpdateIcons for our bar, swap the bound
        /// items list to be OUR slot items. This is the key handoff —
        /// vanilla then renders our items on its own, with full
        /// fidelity to icons/stacks/quality/drag.
        /// </summary>
        [HarmonyPatch(typeof(Inventory), "GetBoundItems")]
        public static class Inventory_GetBoundItems_OurItems
        {
            private static void Postfix(Inventory __instance, List<ItemDrop.ItemData> bound)
            {
                if (!HotkeyBar_UpdateIcons_BindOurItems.inCall) return;
                if (__instance != Player.m_localPlayer?.GetInventory()) return;

                // Dispatch on bar name to populate the bound list with
                // the corresponding slot type's items. Mirrors
                // shudnal-ExtraSlots' QuickBars.Inventory_GetBoundItems_QuickBarsItems
                // postfix.
                var name = HotkeyBar_UpdateIcons_BindOurItems.barName;
                if (ToolHotBar.IsOurBar(name))
                {
                    bound.Clear();
                    ToolHotBar.GetItems(bound);
                }
                else if (AmmoHotBar.IsOurBar(name))
                {
                    bound.Clear();
                    AmmoHotBar.GetItems(bound);
                }
                else if (FoodHotBar.IsOurBar(name))
                {
                    bound.Clear();
                    FoodHotBar.GetItems(bound);
                }
            }
        }

        /// <summary>
        /// Mark the bar dirty whenever the HUD finishes initializing.
        /// First-spawn timing means Hud.Awake fires before our slots
        /// are ready; the dirty flag drives Refresh() on the next frame
        /// when slots are populated.
        /// </summary>
        [HarmonyPatch(typeof(Hud), "Awake")]
        private static class Hud_Awake_MarkDirty
        {
            private static void Postfix()
            {
                ToolHotBar.MarkDirty();
                AmmoHotBar.MarkDirty();
                FoodHotBar.MarkDirty();
            }
        }
    }
}
