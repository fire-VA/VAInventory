using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.105.
    /// Reference-counted: the player panel is hidden while ANY owner holds a suppression and shown again when the last one
    /// releases. Owners are plain strings (e.g. "FiresRPGmaker.NpcBook"); suppressing twice with the same owner counts once.
    /// Hiding is a CanvasGroup (invisible, not clickable), so layout, slots and saved positions are untouched.
    /// </summary>
    public static class VAInventoryApi
    {
        static readonly HashSet<string> _owners = new HashSet<string>();

        /// <summary>True while at least one owner suppresses the player panel.</summary>
        public static bool PlayerPanelSuppressed => _owners.Count > 0;

        /// <summary>The owner whose suppress / release changed the state last (for the dock-element log line).</summary>
        internal static string LastSuppressionBy { get; private set; }

        public static void SuppressPlayerPanel(string owner)
        {
            if (string.IsNullOrEmpty(owner) || !_owners.Add(owner)) return;
            LastSuppressionBy = owner;
            EquipmentPanel.PanelLog?.LogDebug($"player panel: suppressed by {owner} ({_owners.Count} owner(s))");
            UnifiedScreen.ApplySuppression();
        }

        // Extra columns for the side-by-side "Panels" layout (1.0.157+), e.g.
        // 's companion panel. ADD-ONLY. Registering is order-free; nothing is created until the Panels layout exists, so until
        // then GetLayoutPanel returns null and nothing on screen changes.
        public sealed class LayoutPanel { public string Id; public int Order; public System.Func<bool> Visible; internal RectTransform Rect; }
        static readonly Dictionary<string, LayoutPanel> _layoutPanels = new Dictionary<string, LayoutPanel>();
        internal static IEnumerable<LayoutPanel> LayoutPanels => _layoutPanels.Values;

        public static void RegisterLayoutPanel(string id, int order, System.Func<bool> visible)
        {
            if (string.IsNullOrEmpty(id)) return;
            _layoutPanels[id] = new LayoutPanel { Id = id, Order = order, Visible = visible };
            EquipmentPanel.PanelLog?.LogDebug($"layout panel: registered '{id}' (order {order}; {_layoutPanels.Count} registered)");
        }

        public static void UnregisterLayoutPanel(string id)
        {
            if (id != null && _layoutPanels.Remove(id)) EquipmentPanel.PanelLog?.LogDebug($"layout panel: unregistered '{id}'");
        }

        /// <summary>The panel's rect while the Panels layout is up and the panel is visible; null otherwise.</summary>
        public static RectTransform GetLayoutPanel(string id) => id != null && _layoutPanels.TryGetValue(id, out var p) ? p.Rect : null;

        /// <summary>True while the Panels layout draws the companion column, so the
        /// companion screen hides its own panel. False on Unified / Legacy and while the inventory is closed.</summary>
        public static bool OwnsCompanionColumn => PanelsLayout.CompanionColumnShown;

        /// <summary>The companion panel's page, "Equipment" or
        /// "Stats", as its pills do. False when no companion panel is shown. ADD-ONLY.</summary>
        /// <summary>Lays the pouch-cell slot look (dark face + light gold
        /// border, read live from the Tools pouch cell) on ONE cell background. False while it isn't available (not the Panels layout, or no
        /// pouch cell built yet: call again on enable). ADD-ONLY.</summary>
        public static bool ApplySlotLook(UnityEngine.UI.Image face)
        {
            if (face == null || !UnifiedScreen.PanelsMode || !UnifiedScreen.PouchLook(out var look)) return false;
            UnifiedScreen.ApplyLook(face, look);
            return true;
        }

        public static bool SetCompanionPage(string page)
        {
            // The panel object is built once and kept; the page is set whenever it exists
            // With Unified UI OFF there is no companion panel by design, so a caller's request is simply not applicable: false, silently, no log line
            if (!OwnsCompanionColumn) return false;
            var p = CompanionPanel.Current;
            if (p == null) { EquipmentPanel.PanelLog?.LogDebug($"panels: SetCompanionPage(\"{page}\") refused: no companion panel built yet"); return false; }
            p.SetPageFromApi(string.Equals(page, "Stats", System.StringComparison.OrdinalIgnoreCase));
            return true;
        }

        /// <summary>True while [UI] Layout = Panels.</summary>
        public static bool PanelsLayoutActive => UnifiedScreen.PanelsMode;

        /// <summary>The player's pouch cells for "Tool", "Food" or "Ammo" - the ACTIVE slots'
        /// grid positions in the player's Inventory, in bar order. Moving an item there with Inventory.MoveItemToThis(inv, item, n, x, y) is
        /// what a drag onto that pouch cell does. Unknown kind / no player = empty.</summary>
        /// <summary>Put a ring or necklace the LOCAL player carries
        /// into VAInventory's jewelry slots the way a drop onto that cell does - the first FREE Necklace / Left ring / Right ring slot whose
        /// validator accepts it, else SWAP with the first occupied accepting one (the worn piece goes to the item's old cell). Same
        /// inventory, so only grid positions change, then the inventory's Changed (save + UI refresh). False = not jewelry, no slot,
        /// not the local player, or the item isn't in the player's inventory. Never throws.</summary>
        public static bool TryEquipJewelry(Player player, ItemDrop.ItemData item)
        {
            try
            {
                if (player == null || player != Player.m_localPlayer || item == null) return false;
                var inv = player.GetInventory();
                if (inv == null || !inv.ContainsItem(item)) return false;
                Slots.Slot free = null, taken = null;
                var why = new System.Text.StringBuilder();
                foreach (var id in new[] { Slots.necklaceSlotID, Slots.ringSlotID, Slots.ringRightSlotID })
                    foreach (var s in Slots.AllSlots())
                    {
                        if (s == null || s.ID != id) continue;
                        // Re-read the cell: a cached EMPTY is trusted by Slot.Item, and the last call here moved a ring in by m_gridPos alone,
                        // so the Left ring still read free and the second ring landed on the same cell (both rings in ring1, round 10-29).
                        var at = s.CacheItem();
                        why.Append($" {s.ID}[{(s.IsActive ? "on" : "off")},{(s.ItemFits(item) ? "fits" : "no fit")},{(at == null ? "free" : at.m_shared.m_name)}]");
                        if (!s.IsActive || !s.ItemFits(item)) continue;
                        if (at == item) return true;   // already worn there
                        if (at == null) { if (free == null) free = s; }
                        else if (taken == null) taken = s;
                    }
                var target = free ?? taken;
                if (target == null) return false;
                var from = item.m_gridPos;
                var other = target.IsFree ? null : target.Item;
                if (other != null) other.m_gridPos = from;   // the swap: the worn piece takes the new one's old cell
                item.m_gridPos = target.GridPosition;
                target.CacheItem();   // the cell now holds the new piece (the next call must see it taken)
                // Direct call: Valheim 1.0.21 made it Changed(bool success = false, bool cheatedStateChanged = false), and the old
                // reflection Invoke(inv, null) threw "Number of parameters" AFTER the move (round 4, 10-29: worn but reported failed)
                inv.Changed();
                EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] TryEquipJewelry: {item.m_shared.m_name} -> {target.ID}{(other != null ? " (swapped with " + other.m_shared.m_name + ")" : "")}; cells:{why} -> {(other != null ? "SWAP, no free cell" : "first free cell")}");
                return true;
            }
            catch (System.Exception e) { EquipmentPanel.PanelLog?.LogDebug("[VAInventory] TryEquipJewelry failed: " + e.Message); return false; }
        }

        /// <summary>1.0.371 (FDT equip_cells_test): true when every worn cell of the local player matches what is worn; else the first
        /// mismatch ("A: ... NOT equipped" / "B: ... outside its cell"). Also the number of fixes EquipCellGuard made this session.</summary>
        public static bool EquipCellsMatch(out string mismatch, out int fixesThisSession)
        {
            fixesThisSession = EquipCellGuard.FixesThisSession;
            return EquipCellGuard.CellsMatch(out mismatch);
        }

        /// <summary>1.0.371 (FDT equip_cells_test): a FREE worn cell (helmet, chest, legs ...) of the local player that fits the item; (-1,-1) = none.</summary>
        public static Vector2i FindFreeWornCell(ItemDrop.ItemData item) => EquipCellGuard.FreeWornCell(item);

        /// <summary>1.0.371 (FLO's right-click unequip, Fire 10-29): the twin of TryEquipJewelry. A ring or necklace the LOCAL player wears in a
        /// jewelry cell moves to the first free main-grid cell (never a jewelry / pouch / bag cell), then the inventory's Changed. False = not
        /// worn in a jewelry cell, not the local player, or no free main-grid cell (it stays where it is). Never throws.</summary>
        public static bool TryUnequipJewelry(Player player, ItemDrop.ItemData item)
        {
            try
            {
                if (player == null || player != Player.m_localPlayer || item == null) return false;
                var inv = player.GetInventory();
                if (inv == null || !inv.ContainsItem(item)) return false;
                var from = Slots.GetSlotInGrid(item.m_gridPos);
                if (!UnifiedScreen.IsJewelrySlot(from)) { EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] TryUnequipJewelry: {item.m_shared.m_name} at {item.m_gridPos} is not in a jewelry cell; stays"); return false; }
                for (int y = 0; y < Slots.InventoryHeightPlayer; y++)
                    for (int x = 0; x < inv.GetWidth(); x++)
                    {
                        if (inv.GetItemAt(x, y) != null) continue;
                        var was = item.m_gridPos;
                        item.m_gridPos = new Vector2i(x, y);
                        Slots.ClearCachedItems();
                        inv.Changed();
                        EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] TryUnequipJewelry: {item.m_shared.m_name} {from.ID} {was} -> bag ({x},{y})");
                        return true;
                    }
                EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] TryUnequipJewelry: {item.m_shared.m_name} stays in {from.ID} (no free main-grid cell)");
                return false;
            }
            catch (System.Exception e) { EquipmentPanel.PanelLog?.LogDebug("[VAInventory] TryUnequipJewelry failed: " + e.Message); return false; }
        }

        /// <summary>Opens the F1 hub on Player > Stats in companion view for
        /// THIS companion - preview, gear, every stat + skill, the Attributes button - with no inventory and the hub kept open. False when Core's
        /// hub page is not registered (Fires UI override OFF / older Core): the caller keeps its own path then.</summary>
        public static bool OpenCompanionStats(Humanoid companion)
        {
            if (!FiresCore.UI.Shell.FiresShell.OverrideOn) return false;
            return PlayerStatsPage.OpenFor(companion);
        }

        public static Vector2i[] PouchCells(string kind)
        {
            try
            {
                System.Collections.Generic.IEnumerable<Slots.Slot> src =
                    kind == "Tool" ? (System.Collections.Generic.IEnumerable<Slots.Slot>)Slots.toolHotbarSlots
                    : kind == "Food" ? Slots.GetFoodSlots()
                    : kind == "Ammo" ? Slots.GetAmmoSlots() : null;
                if (src == null) return new Vector2i[0];
                var list = new System.Collections.Generic.List<Vector2i>();
                foreach (var s in src) if (s != null && s.IsActive) list.Add(s.GridPosition);
                return list.ToArray();
            }
            catch { return new Vector2i[0]; }
        }

        public static void ReleasePlayerPanel(string owner)
        {
            if (string.IsNullOrEmpty(owner) || !_owners.Remove(owner)) return;
            LastSuppressionBy = owner;
            EquipmentPanel.PanelLog?.LogDebug($"player panel: released by {owner} ({_owners.Count} owner(s) left)");
            UnifiedScreen.ApplySuppression();
        }
    }
}
