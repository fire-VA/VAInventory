using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // Tools / Ammo / Food pouches (Fire 10-07, Tools\VAINVENTORY_POUCHES_PLAN.md). UI ONLY: the pouch popups hold the
    // cells the group's real slots map to (UnifiedScreen.BuildPouches), so the items, grid positions, save data, HUD bars
    // and hotkeys are untouched. This class only shows / hides / places those popups.
    //
    // Open: click the pouch (stays until an outside click, Esc or the inventory closing), or hover it ~0.25 s while
    // dragging an item (closes again when the drag ends). One pouch open at a time; the coin popup takes part.
    internal static class PouchPopup
    {
        internal const float HoverOpenSeconds = 0.25f;

        static readonly Dictionary<string, (RectTransform popup, RectTransform anchor)> groups = [];
        static string openGroup;
        static bool openedByDrag;

        static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> DragItem =
            AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_dragItem");
        static readonly AccessTools.FieldRef<InventoryGui, Inventory> DragInventory =
            AccessTools.FieldRefAccess<InventoryGui, Inventory>("m_dragInventory");

        internal static string OpenGroup => openGroup;
        internal static bool Dragging => InventoryGui.instance != null && DragItem(InventoryGui.instance) != null;

        internal static void Register(string group, RectTransform popup, RectTransform anchor)
        {
            groups[group] = (popup, anchor);
            var panel = popup.parent;
            if (panel != null && panel.GetComponent<PouchWatcher>() == null) panel.gameObject.AddComponent<PouchWatcher>();
        }

        // The cells were rebuilt (slots re-newed): forget the old popups, keep which group is open.
        internal static void Unregister() => groups.Clear();

        internal static void Reapply()
        {
            if (openGroup == null) return;
            if (!groups.ContainsKey(openGroup)) { openGroup = null; return; }
            Show(openGroup);
        }

        internal static void Toggle(string group)
        {
            if (openGroup == group && !openedByDrag) Close();
            else Open(group, false);
        }

        internal static void Open(string group, bool byDrag)
        {
            group = group?.ToLowerInvariant();
            if (group == null || !groups.ContainsKey(group)) return;
            CurrencyPouchPopup.Close();
            HideAll();
            openGroup = group;
            openedByDrag = byDrag;
            Show(group);
            EquipmentPanel.PanelLog.LogDebug($"[VAInventory] pouch open: {group} ({(byDrag ? "drag-hover" : "click")})");
        }

        internal static void Close()
        {
            if (openGroup != null) EquipmentPanel.PanelLog.LogDebug($"[VAInventory] pouch closed: {openGroup}");
            HideAll();
            openGroup = null;
            openedByDrag = false;
        }

        static void HideAll()
        {
            foreach (var entry in groups.Values)
                if (entry.popup != null) entry.popup.gameObject.SetActive(false);
        }

        static void Show(string group)
        {
            var (popup, _) = groups[group];
            if (popup == null) return;
            popup.gameObject.SetActive(true);
            popup.SetAsLastSibling();
            LayoutRebuilder.ForceRebuildLayoutImmediate(popup);
            Place();
        }

        // Bottom-centre of the popup just above the pouch cell, in the panel's own space (so it follows a dragged panel).
        static readonly Vector3[] corners = new Vector3[4];
        internal static void Place()
        {
            if (openGroup == null || !groups.TryGetValue(openGroup, out var entry)) return;
            if (entry.popup == null || entry.anchor == null || entry.popup.parent == null) return;
            entry.anchor.GetWorldCorners(corners); // 1 = top-left, 2 = top-right
            var top = (corners[1] + corners[2]) * 0.5f;
            var local = entry.popup.parent.InverseTransformPoint(top);
            entry.popup.localPosition = new Vector3(local.x, local.y + 6f, 0f);
        }

        internal static bool PointerOverOpen(Vector2 screen)
        {
            if (openGroup == null || !groups.TryGetValue(openGroup, out var entry)) return false;
            return (entry.popup != null && RectTransformUtility.RectangleContainsScreenPoint(entry.popup, screen, null))
                   || (entry.anchor != null && RectTransformUtility.RectangleContainsScreenPoint(entry.anchor, screen, null));
        }

        // Dropped on the pouch cell itself: the first free slot of the group that accepts the item (same move as a cell drop).
        internal static void DropInto(string group)
        {
            var gui = InventoryGui.instance;
            if (gui == null) return;
            var item = DragItem(gui);
            if (item == null) return;
            var inventory = Slots.PlayerInventory;
            if (inventory == null || DragInventory(gui) != inventory) return;
            var target = UnifiedScreen.PouchSlots(group).FirstOrDefault(s => s.IsFree && s.ItemFits(item));
            if (target == null)
            {
                EquipmentPanel.PanelLog.LogDebug($"[VAInventory] pouch drop: {item.m_shared.m_name} -> {group}: no free slot accepts it");
                return;
            }
            var pos = target.GridPosition;
            EquipmentPanel.PanelLog.LogDebug($"[VAInventory] pouch drop: {item.m_shared.m_name} {item.m_gridPos} -> {group} {target.ID} {pos}");
            item.m_gridPos = pos;
            Slots.ClearCachedItems();
            inventory.m_onChanged?.Invoke();
            EquipmentPanel.ClearDragState();
            EquipmentPanel.MarkDirty();
            EquipmentPanel.UpdatePanel();
        }

        // Lives on the character panel: closes on Esc, the inventory closing, an outside click, or (drag-hover only) drag end.
        sealed class PouchWatcher : MonoBehaviour
        {
            int outsideClickFrame = -1;
            bool draggingAtClick;

            void OnDisable() => Close();

            void Update()
            {
                if (openGroup == null) return;
                if (!InventoryGui.IsVisible() || Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
                Place();
                if (openedByDrag)
                {
                    if (!Dragging) Close();
                    return;
                }
                // An outside click closes it, unless that click just picked an item up (wait one frame for the click
                // handlers): then the player is about to drop it into the pouch.
                if (Input.GetMouseButtonUp(0) && !PointerOverOpen(Input.mousePosition))
                {
                    outsideClickFrame = Time.frameCount;
                    draggingAtClick = Dragging;
                }
                else if (outsideClickFrame >= 0 && Time.frameCount > outsideClickFrame)
                {
                    outsideClickFrame = -1;
                    if (draggingAtClick || !Dragging) Close();
                }
            }
        }
    }

    // One pouch cell under the preview: click toggles, drag-hover opens, a drop stores into the first fitting slot.
    internal sealed class PouchCell : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        const float RefreshSeconds = 0.25f;
        const float DimAlpha = 0.45f;

        string group;
        Image icon;
        TMP_Text badge;
        bool hovering;
        float hoverStart;
        float nextRefresh;

        internal void Init(string pouchGroup, Image iconImage, TMP_Text badgeText)
        {
            group = pouchGroup;
            icon = iconImage;
            badge = badgeText;
            // Vanilla's own tooltip component, wired like the inventory squares' (prefab + anchor), so it shows on hover.
            var source = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponent<UITooltip>();
            var anchor = InventoryGui.instance?.m_playerGrid?.m_tooltipAnchor;
            if (source != null && anchor != null)
            {
                var tooltip = gameObject.AddComponent<UITooltip>();
                tooltip.m_tooltipPrefab = source.m_tooltipPrefab;
                string title = char.ToUpperInvariant(group[0]) + group.Substring(1);
                tooltip.Set(title + " pouch", "Click to open. Drop an item here to put it in the first free slot.", anchor);
            }
            Refresh();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            hovering = true;
            hoverStart = Time.unscaledTime;
        }

        public void OnPointerExit(PointerEventData eventData) => hovering = false;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            if (PouchPopup.Dragging) PouchPopup.DropInto(group);
            else PouchPopup.Toggle(group);
            Refresh();
        }

        void Update()
        {
            if (hovering && PouchPopup.Dragging && PouchPopup.OpenGroup != group
                && Time.unscaledTime - hoverStart >= PouchPopup.HoverOpenSeconds)
                PouchPopup.Open(group, true);
            if (!hovering || !PouchPopup.Dragging) hoverStart = Time.unscaledTime;
            if (Time.unscaledTime >= nextRefresh) Refresh();
        }

        // Badge "filled/total"; the icon is the first held item's, dimmed, or a plain tinted frame when empty.
        void Refresh()
        {
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            var slots = UnifiedScreen.PouchSlots(group);
            int filled = slots.Count(s => s.Item != null);
            if (badge != null) badge.text = $"{filled}/{slots.Count}";
            if (icon == null) return;
            var first = slots.Select(s => s.Item).FirstOrDefault(i => i != null);
            Sprite sprite = first?.GetIcon();
            if (sprite == null && group == "ammo") sprite = EquipmentPanel.ammoSlot;
            icon.sprite = sprite;
            icon.enabled = sprite != null;
            icon.color = new Color(1f, 1f, 1f, first != null ? DimAlpha + 0.25f : DimAlpha);
        }
    }
}
