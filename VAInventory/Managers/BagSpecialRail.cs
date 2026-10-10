// BagSpecialRail — the special-slot rail on a detached bag panel (VABackpacks 1.0.20+, Fire 10-07 layout A).
//
// Upgraded bags (quality 2+) carry tool / resources / bypass slots that live OUTSIDE the bag grid and outside
// the player inventory; VABackpacks owns the storage, we only draw it and move items in / out through the
// BackpackBridge special-slot calls. One vertical column right of the grid, one cell per kind, top-down,
// same element prefab and cell size as the grid. Older VABackpacks (no special-slot API) => no rail at all.
//
// The rail component polls SpecialSlotsVersion each frame it is active (one int compare) and redraws its
// cells when it moves, so changes made elsewhere (other mods, bag swap, upgrade carry-over) show up.

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VerdantsAscent.Managers;

namespace VerdantsAscent
{
    public class BagSpecialRail : MonoBehaviour
    {
        public const string RailName = "VAInventoryBagSpecialRail";
        // Small gap between the bag grid and the rail so the tinted frames don't touch the last grid column.
        public const float RailGap = 4f;
        private const float EmptyIconAlpha = 0.6f;
        private const float FrameBackgroundAlpha = 0.35f;

        private static readonly Dictionary<string, Color> KindColors = new()
        {
            { "tool",      new Color32(0xd9, 0xa3, 0x5b, 0xff) },
            { "resources", new Color32(0x8f, 0xbf, 0x6a, 0xff) },
            { "bypass",    new Color32(0x8a, 0xb8, 0xe6, 0xff) },
        };

        private static readonly Dictionary<string, (string topic, string desc)> KindTooltips = new()
        {
            { "tool",      ("Tool slot", "One weapon or tool.") },
            { "resources", ("Resources slot", "One stack of material; it weighs a quarter.") },
            { "bypass",    ("Teleport bypass", "One non-teleportable item here can pass through portals.") },
        };

        private static readonly System.Reflection.FieldInfo fi_dragInventory = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
        private static readonly System.Reflection.FieldInfo fi_dragAmount = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");

        private ItemDrop.ItemData bag;
        private string[] kinds = [];
        private readonly List<InventoryElement> cells = [];
        private int seenVersion = int.MinValue;

        // ── Panel-side helpers ───────────────────────────────────────

        /// <summary>The equipped bag ItemData whose drop prefab is <paramref name="prefab"/>, or null.</summary>
        public static ItemDrop.ItemData FindBag(string prefab)
        {
            if (!BackpackBridge.HasSpecialSlots || Player.m_localPlayer == null) return null;
            return BackpackBridge.GetEquippedBackpacks(Player.m_localPlayer)
                .FirstOrDefault(b => b != null && b.m_dropPrefab != null && b.m_dropPrefab.name == prefab);
        }

        public static string[] KindsFor(ItemDrop.ItemData bag) =>
            bag == null ? [] : BackpackBridge.GetSpecialSlots(bag);

        public static float RailWidth(int kindCount, float tileSize) => kindCount > 0 ? RailGap + tileSize : 0f;

        /// <summary>(Re)builds the rail on a bag panel; removes it when the bag has no special slots.</summary>
        public static void Build(RectTransform panel, ItemDrop.ItemData bag, string[] kinds, GameObject elementPrefab,
            Vector2 topLeft, float tileSize)
        {
            if (!panel) return;
            var old = panel.Find(RailName);
            if (old)
            {
                old.name = RailName + "_dead"; // Destroy is deferred; keep a same-frame rebuild from finding it again
                Destroy(old.gameObject);
            }
            if (bag == null || kinds == null || kinds.Length == 0 || elementPrefab == null) return;

            var railRect = new GameObject(RailName, typeof(RectTransform)).GetComponent<RectTransform>();
            railRect.SetParent(panel, false);
            railRect.anchorMin = railRect.anchorMax = railRect.pivot = new Vector2(0f, 1f);
            railRect.anchoredPosition = topLeft;
            railRect.sizeDelta = new Vector2(tileSize, kinds.Length * tileSize);

            var rail = railRect.gameObject.AddComponent<BagSpecialRail>();
            rail.bag = bag;
            rail.kinds = kinds;
            for (int i = 0; i < kinds.Length; i++)
                rail.cells.Add(rail.MakeCell(elementPrefab, kinds[i], new Vector2(0f, -i * tileSize)));
            rail.Redraw();
        }

        // ── Cells ────────────────────────────────────────────────────

        private InventoryElement MakeCell(GameObject elementPrefab, string kind, Vector2 pos)
        {
            var go = Instantiate(elementPrefab, transform);
            go.name = "BagSpecialSlot_" + kind;
            EquipmentPanel.HideOverlays(go);
            go.GetComponent<RectTransform>().anchoredPosition = pos;

            // Frame tinted by kind: the element's own background, tinted, plus an outline in the full colour.
            Color c = KindColor(kind);
            var bkg = go.GetComponent<Image>();
            if (bkg)
            {
                bkg.color = new Color(c.r, c.g, c.b, FrameBackgroundAlpha);
                var outline = go.AddComponent<Outline>();
                outline.effectColor = c;
                outline.effectDistance = new Vector2(2f, -2f);
            }

            go.AddComponent<CellHandler>().Init(this, kind);

            var button = go.GetComponent<Button>();
            if (button != null)
            {
                if (!go.GetComponent<EquipmentPanel.CellPointerUp>()) go.AddComponent<EquipmentPanel.CellPointerUp>();
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() =>
                {
                    // Same gate as the Unified cells: the pointer-up position must be inside this cell.
                    if (!RectTransformUtility.RectangleContainsScreenPoint(go.GetComponent<RectTransform>(),
                            EquipmentPanel.CellPointerUp.PositionOr(go, Input.mousePosition), null))
                        return;
                    OnLeftClick(kind);
                });
            }

            var elem = go.GetComponent<InventoryElement>();
            if (elem == null)
            {
                Debug.LogWarning("[VAInventory] element prefab carries no InventoryElement component - bag special slot skipped.");
                Destroy(go);
            }
            return elem;
        }

        private static Color KindColor(string kind) => KindColors.TryGetValue(kind, out var c) ? c : Color.white;

        private void Update()
        {
            if (bag == null) return;
            if (BackpackBridge.SpecialSlotsVersion != seenVersion) Redraw();
        }

        private void Redraw()
        {
            seenVersion = BackpackBridge.SpecialSlotsVersion;
            int filled = 0;
            for (int i = 0; i < kinds.Length && i < cells.Count; i++)
            {
                var elem = cells[i];
                if (elem == null) continue;
                var item = BackpackBridge.ReadSpecialSlot(bag, kinds[i]);
                if (item != null) filled++;
                if (elem.m_icon)
                {
                    elem.m_icon.enabled = true;
                    if (item != null)
                    {
                        elem.m_icon.sprite = item.GetIcon();
                        elem.m_icon.color = Color.white;
                    }
                    else
                    {
                        var c = KindColor(kinds[i]);
                        elem.m_icon.sprite = BackpackBridge.GetSlotIcon(kinds[i]);
                        elem.m_icon.color = new Color(c.r, c.g, c.b, EmptyIconAlpha);
                        if (elem.m_icon.sprite == null) elem.m_icon.enabled = false;
                    }
                }
                if (elem.m_amount)
                {
                    // Vanilla InventoryGrid.UpdateGui: "stack/max" only for stackables.
                    bool show = item != null && item.m_shared.m_maxStackSize > 1;
                    elem.m_amount.enabled = show;
                    elem.m_amount.gameObject.SetActive(show);
                    if (show) elem.m_amount.text = $"{item.m_stack}/{item.m_shared.m_maxStackSize}";
                }
            }
            EquipmentPanel.PanelLog.LogDebug($"[VAInventory] bag special slots: {bag.m_shared?.m_name} kinds={string.Join(",", kinds)} filled={filled}");
        }

        // ── Interaction ──────────────────────────────────────────────

        private void OnLeftClick(string kind)
        {
            var gui = InventoryGui.instance;
            if (gui == null || bag == null) return;
            var dragItem = (ItemDrop.ItemData)EquipmentPanel.fi_dragItem.GetValue(gui);
            var current = BackpackBridge.ReadSpecialSlot(bag, kind);

            if (dragItem != null)
            {
                // Occupied + dragging: no swap, leave the drag as it is.
                if (current != null) return;
                if (!BackpackBridge.SpecialSlotAccepts(bag, kind, dragItem)) return;
                var dragInventory = fi_dragInventory?.GetValue(gui) as Inventory;
                if (dragInventory == null) return;

                var player = Player.m_localPlayer;
                if (player != null && player.IsItemEquiped(dragItem))
                    player.UnequipItem(dragItem, false);

                // A split drag (shift) carries only part of the stack: move just that part.
                int amount = fi_dragAmount != null ? (int)fi_dragAmount.GetValue(gui) : dragItem.m_stack;
                ItemDrop.ItemData moved;
                if (amount > 0 && amount < dragItem.m_stack)
                {
                    moved = dragItem.Clone();
                    moved.m_stack = amount;
                    dragInventory.RemoveItem(dragItem, amount);
                }
                else
                {
                    moved = dragItem;
                    dragInventory.RemoveItem(dragItem);
                }

                bool ok = BackpackBridge.WriteSpecialSlot(bag, kind, moved);
                if (!ok) dragInventory.AddItem(moved);
                EquipmentPanel.PanelLog.LogDebug($"[VAInventory] bag special slot drop: {moved.m_shared.m_name} x{moved.m_stack} -> {kind} {(ok ? "stored" : "refused, returned")}");
                EquipmentPanel.ClearDragState();
                Redraw();
                return;
            }

            if (current != null) TakeOut(kind);
        }

        internal void TakeOut(string kind)
        {
            var player = Player.m_localPlayer;
            if (player == null || bag == null) return;
            var item = BackpackBridge.ReadSpecialSlot(bag, kind);
            if (item == null) return;
            var inv = player.GetInventory();
            if (inv == null || !inv.CanAddItem(item))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_noroom");
                return;
            }
            // Clear the slot first (Write null only empties VABackpacks' 1x1 inventory, the ItemData stays intact),
            // then hand the item to the player; on a refusal put it back so nothing is lost.
            if (!BackpackBridge.WriteSpecialSlot(bag, kind, null)) return;
            if (!inv.AddItem(item))
            {
                BackpackBridge.WriteSpecialSlot(bag, kind, item);
                player.Message(MessageHud.MessageType.Center, "$msg_noroom");
            }
            Redraw();
        }

        private void ShowTooltip(GameObject cell, string kind)
        {
            if (InventoryGui.instance == null || InventoryGui.instance.m_playerGrid == null) return;
            var tooltip = cell.GetComponent<UITooltip>();
            if (tooltip == null) return;
            var anchor = InventoryGui.instance.m_playerGrid.m_tooltipAnchor;
            var item = BackpackBridge.ReadSpecialSlot(bag, kind);
            if (item != null)
            {
                tooltip.Set(item.m_shared.m_name, item.GetTooltip(), anchor);
                return;
            }
            if (KindTooltips.TryGetValue(kind, out var t))
                tooltip.Set(t.topic, t.desc, anchor);
            else
                anchor.gameObject.SetActive(false);
        }

        // Tooltip + right-click on one rail cell (same pattern as EquipmentPanel.ExtraSlotTooltipHandler).
        public class CellHandler : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
        {
            private BagSpecialRail rail;
            private string kind;
            public void Init(BagSpecialRail r, string k) { rail = r; kind = k; }

            public void OnPointerEnter(PointerEventData eventData)
            {
                if (rail) rail.ShowTooltip(gameObject, kind);
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                if (InventoryGui.instance && InventoryGui.instance.m_playerGrid)
                    InventoryGui.instance.m_playerGrid.m_tooltipAnchor.gameObject.SetActive(false);
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Right) return;
                if (!InventoryGui.IsVisible() || !rail) return;
                rail.TakeOut(kind);
            }
        }
    }
}
