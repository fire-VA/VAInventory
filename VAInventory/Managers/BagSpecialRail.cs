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
            // 1.0.316: the right mouse going DOWN while our carry is on the cursor = vanilla's drag cancel; remember it for the cell's click
            if (Input.GetMouseButtonDown(1) && s_carry != null && s_carry.NrOfItems() > 0) s_carryPressFrame = Time.frameCount;
            if (s_carry != null && s_carry.NrOfItems() > 0) ReturnCarry();   // 1.0.316: a cancelled carry / swap leftover goes home
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
                if (!BackpackBridge.SpecialSlotAccepts(bag, kind, dragItem)) return;
                var dragInventory = fi_dragInventory?.GetValue(gui) as Inventory;
                if (dragInventory == null) return;
                // 1.0.316 (Fire: "grab an item from one slot and drop it in the next" - vanilla): occupied + dragging = SWAP. The dragged
                // item goes in, the slot's item comes onto the cursor (from the hidden carry), like vanilla's grid swap.
                if (current != null)
                {
                    int amt = fi_dragAmount != null ? (int)fi_dragAmount.GetValue(gui) : dragItem.m_stack;
                    if (amt < dragItem.m_stack) return;   // a split stack never swaps (vanilla)
                    var pu = Player.m_localPlayer;
                    if (pu != null && pu.IsItemEquiped(dragItem)) pu.UnequipItem(dragItem, false);
                    if (!BackpackBridge.WriteSpecialSlot(bag, kind, null)) return;
                    dragInventory.RemoveItem(dragItem);
                    if (!BackpackBridge.WriteSpecialSlot(bag, kind, dragItem)) { dragInventory.AddItem(dragItem); BackpackBridge.WriteSpecialSlot(bag, kind, current); return; }
                    StartCarry(gui, current, kind);
                    EquipmentPanel.PanelLog.LogDebug($"[VAInventory] bag special slot swap: {dragItem.m_shared.m_name} -> {kind}, {current.m_shared.m_name} onto the cursor");
                    Redraw();
                    return;
                }

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

            if (current == null) return;
            // 1.0.316 (Fire 10-22: "trying to click and move things from our backpack special slots just auto moves them to our player grid
            // rather than picking them up to the cursor like vanilla"): a plain left click PICKS THE ITEM UP onto the cursor (vanilla's own
            // drag, from the player inventory it is handed to); shift / ctrl click keeps the quick move to the grid
            // 1.0.316 (Fire: "the vanilla behaviour of being able to grab an item from one slot and drop it in the next"): the item leaves the
            // special slot into a hidden 1x1 CARRY inventory and vanilla's own drag starts from there - nothing appears in the player grid,
            // and vanilla's drop / swap / world-drop code moves it wherever he clicks (player grid, bag cell, chest, or another special slot
            // via OnLeftClick above). A cancelled drag or a swap's leftover goes back to the special slot it came from (else the player grid).
            bool quick = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (quick) { TakeOut(kind); }
            else if (BackpackBridge.WriteSpecialSlot(bag, kind, null)) { StartCarry(gui, current, kind); Redraw(); }
            if (s_clickLogged.Add(kind + (quick ? "/quick" : "/pick")))
                EquipmentPanel.PanelLog.LogDebug($"[VAInventory] bag special slot click path ({kind}): {(quick ? "shift/ctrl = quick move to the player grid" : "plain left click = onto the cursor from a hidden carry inventory (vanilla drag; drop anywhere)")}");
        }
        private static System.Reflection.MethodInfo s_setupDrag;
        private static readonly System.Collections.Generic.HashSet<string> s_clickLogged = new System.Collections.Generic.HashSet<string>();
        private static Inventory s_carry;
        internal static bool IsCarry(Inventory inv) => inv != null && inv == s_carry;   // 1.0.318: EquipmentPanel lets a carried item into bag cells
        private static string s_carryFrom;
        private static int s_carryPressFrame = -1;

        private void StartCarry(InventoryGui gui, ItemDrop.ItemData item, string fromKind)
        {
            s_carry ??= new Inventory("VAInventorySpecialCarry", null, 1, 1);
            ReturnCarry();   // anything still in the carry goes home first
            item.m_gridPos = new Vector2i(0, 0);
            // 1.0.317 (bag_drag round 23:23: every pick-up came straight back, "carry ... returned to tool"): AddItem(item, amount, x, y)
            // stores a CLONE, so the carry never held the dragged item and vanilla's drag update (drag inventory must contain the drag
            // item) dropped the drag at once. AddItem(item) stores this very instance (an empty 1x1, nothing to stack with).
            s_carry.AddItem(item);
            if (!s_carry.ContainsItem(item)) EquipmentPanel.PanelLog.LogDebug($"[VAInventory] bag special slot carry: {item.m_shared.m_name} NOT held by the carry after AddItem (vanilla would cancel the drag)");
            s_carryFrom = fromKind;
            s_setupDrag ??= AccessTools.Method(typeof(InventoryGui), "SetupDragItem");
            try { s_setupDrag?.Invoke(gui, new object[] { item, s_carry, item.m_stack }); }
            catch (System.Exception e) { EquipmentPanel.PanelLog.LogDebug($"[VAInventory] bag special slot carry: SetupDragItem threw {(e.InnerException ?? e).Message}"); ReturnCarry(); }
        }

        // the inventory closing (or the rail gone) never strands a carried item: it goes to the player grid, else onto the ground
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        private static class CarryOnHide
        {
            private static void Postfix()
            {
                if (s_carry == null || s_carry.NrOfItems() == 0) return;
                var p = Player.m_localPlayer;
                foreach (var it in s_carry.GetAllItems().ToList())
                {
                    s_carry.RemoveItem(it);
                    if (p == null) continue;
                    if (!p.GetInventory().AddItem(it)) p.DropItem(s_carry, it, it.m_stack);
                    EquipmentPanel.PanelLog.LogDebug($"[VAInventory] bag special slot carry: inventory closed, {it.m_shared.m_name} x{it.m_stack} to the player");
                }
            }
        }

        // the carry holds something and no drag is running = a cancelled drag or a swap's leftover: back to its slot, else the player grid
        private void ReturnCarry()
        {
            if (s_carry == null || s_carry.NrOfItems() == 0) return;
            var gui = InventoryGui.instance;
            if (gui != null && fi_dragInventory?.GetValue(gui) == s_carry && EquipmentPanel.fi_dragItem.GetValue(gui) != null) return;   // still on the cursor
            // 1.0.318 ([lead]): WHY it comes back, every time - the drag's state right now
            var dItem = gui != null ? EquipmentPanel.fi_dragItem.GetValue(gui) as ItemDrop.ItemData : null;
            var dInv = gui != null ? fi_dragInventory?.GetValue(gui) as Inventory : null;
            string why = gui == null ? "no inventory screen" : dItem == null ? "the drag was cleared (cancelled, or a drop handler refused it)"
                : $"the cursor now holds {dItem.m_shared.m_name} from {(dInv == null ? "no inventory" : dInv == Player.m_localPlayer?.GetInventory() ? "the player inventory" : dInv.GetName())} (a swap's leftover)";
            foreach (var it in s_carry.GetAllItems().ToList())
            {
                s_carry.RemoveItem(it);
                bool home = bag != null && s_carryFrom != null && BackpackBridge.ReadSpecialSlot(bag, s_carryFrom) == null
                    && BackpackBridge.SpecialSlotAccepts(bag, s_carryFrom, it) && BackpackBridge.WriteSpecialSlot(bag, s_carryFrom, it);
                var inv = Player.m_localPlayer?.GetInventory();
                if (!home && (inv == null || !inv.AddItem(it))) { Player.m_localPlayer?.DropItem(s_carry, it, it.m_stack); }
                EquipmentPanel.PanelLog.LogDebug($"[VAInventory] bag special slot carry: {it.m_shared.m_name} x{it.m_stack} returned {(home ? "to " + s_carryFrom : "to the player grid")} - {why}");
            }
            Redraw();
        }

        internal ItemDrop.ItemData TakeOut(string kind)
        {
            var player = Player.m_localPlayer;
            if (player == null || bag == null) return null;
            var item = BackpackBridge.ReadSpecialSlot(bag, kind);
            if (item == null) return null;
            var inv = player.GetInventory();
            if (inv == null || !inv.CanAddItem(item))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_noroom");
                return null;
            }
            // Clear the slot first (Write null only empties VABackpacks' 1x1 inventory, the ItemData stays intact),
            // then hand the item to the player; on a refusal put it back so nothing is lost.
            if (!BackpackBridge.WriteSpecialSlot(bag, kind, null)) return null;
            if (!inv.AddItem(item))
            {
                BackpackBridge.WriteSpecialSlot(bag, kind, item);
                player.Message(MessageHud.MessageType.Center, "$msg_noroom");
                return null;
            }
            Redraw();
            return item;
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
                // 1.0.316: a right click with a carried item on the cursor is vanilla's drag CANCEL (right mouse down drops the drag, the
                // carry goes home); its pointer-up here must not then take that item out again to the grid
                if (s_carryPressFrame >= 0 && Time.frameCount - s_carryPressFrame < 120) { s_carryPressFrame = -1; return; }
                rail.TakeOut(kind);
            }
        }
    }
}
