using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// Mount the REAL player inventory grid into another mod's panel.
    /// ADD-ONLY CONTRACT, same rules as InventoryTabs: members are only ever added, never renamed or removed, and every
    /// mount/unmount is logged. The mounted grid is a clone of vanilla's m_playerGrid (same element prefab, icons, tooltips),
    /// bound to the local player's inventory, redrawn every frame while active. Clicks go through vanilla
    /// InventoryGui.OnSelectedItem / OnRightClickItem, so drag, split and equip behave exactly like the main grid.
    ///
    /// From another mod without a hard reference:
    ///   var t = AccessTools.TypeByName("VerdantsAscent.Managers.VAInventoryGridApi");
    ///   object handle = t.GetMethod("MountGrid").Invoke(null, new object[] { parentRect, null });
    /// </summary>
    public static class VAInventoryGridApi
    {
        public sealed class GridOptions
        {
            /// <summary>Who mounted it - shows in the log lines. Default "unknown".</summary>
            public string Owner = "unknown";
            /// <summary>Forward clicks to vanilla (drag/split/equip). False = events only, the host handles clicks.</summary>
            public bool VanillaClicks = true;
            /// <summary>Optional scroll: true keeps the cloned ScrollRect (if any) on the grid.</summary>
            public bool Scroll = true;
            /// <summary>The inventory to show (the open container, a companion's, ...). Null = the local player's.
            /// Read every frame; when it returns null the grid keeps its last draw.</summary>
            public Func<Inventory> Inventory;
            /// <summary>Show only the first N rows (0 = the inventory's own rule). A companion's backing grid also holds its section rows.</summary>
            public int MaxRows;
            /// <summary>False = right clicks raise ItemRightClicked only (vanilla's right click uses/equips on the LOCAL player,
            /// wrong for a companion's or a container's grid). Default true.</summary>
            public bool VanillaRightClick = true;
        }

        public sealed class GridHandle
        {
            internal InventoryGrid Grid;
            internal GridOptions Options;
            internal GridDriver Driver;
            public bool IsMounted => Grid != null;
            public RectTransform Root => Grid != null ? (RectTransform)Grid.transform : null;
            /// <summary>Redraws now (normally done every frame while the host panel is active).</summary>
            public void Refresh() => Driver?.Redraw();
            public void Unmount() => VAInventoryGridApi.Unmount(this);
        }

        /// <summary>(handle, item, gridPos, modifier) after a left click on a mounted grid; item may be null (empty cell).</summary>
        public static event Action<GridHandle, ItemDrop.ItemData, Vector2i, InventoryGrid.Modifier> ItemClicked;
        /// <summary>(handle, item) when a drag starts from a mounted grid (vanilla's drag item became set).</summary>
        public static event Action<GridHandle, ItemDrop.ItemData> ItemDragged;
        /// <summary>(handle, item, gridPos) after a right click on a mounted grid.</summary>
        public static event Action<GridHandle, ItemDrop.ItemData, Vector2i> ItemRightClicked;

        static readonly List<GridHandle> mounted = new List<GridHandle>();
        static readonly System.Reflection.MethodInfo miSelected = AccessTools.Method(typeof(InventoryGui), "OnSelectedItem");
        static readonly System.Reflection.MethodInfo miRight = AccessTools.Method(typeof(InventoryGui), "OnRightClickItem");

        public static int MountedCount { get { mounted.RemoveAll(h => h.Grid == null); return mounted.Count; } }

        /// <summary>Returns null (logged) when the inventory UI or player isn't up yet - call again after InventoryGui.Awake.</summary>
        public static GridHandle MountGrid(RectTransform parent, GridOptions options = null)
        {
            options ??= new GridOptions();
            var gui = InventoryGui.instance;
            if (parent == null || gui == null || gui.m_playerGrid == null)
            {
                EquipmentPanel.PanelLog?.LogDebug($"grid api: mount by {options.Owner} REFUSED (parent={(parent != null)}, gui={(gui != null)})");
                return null;
            }
            // A CLEAN grid - a new GameObject with its own
            // RectTransform + InventoryGrid + an empty cell root, and ONLY the config vanilla's UpdateGui / clicks read, copied from the main
            // grid. Vanilla UpdateInventory then builds the cells from the element ASSET prefab. The old Instantiate(m_playerGrid) copied every
            // child and every VA / Core-added object with its live state, and left InventoryGrid.CanDropDragOntoItem (a runtime delegate
            // InventoryGui.Awake adds to its own two grids only) NULL, so vanilla UpdateGui threw a NullReferenceException at
            // "m_canBeDroppedOn = CanDropDragOntoItem(item)" on EVERY frame with an item: the placeholder wall and the per-frame exception cost.
            var src = gui.m_playerGrid;
            var go = new GameObject("VAInventoryMountedGrid_" + options.Owner, typeof(RectTransform));
            go.SetActive(false);   // config first, then OnEnable
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; rt.pivot = new Vector2(0.5f, 0.5f);
            var rootGo = new GameObject("Root", typeof(RectTransform));
            var root = (RectTransform)rootGo.transform;
            root.SetParent(rt, false);
            root.anchorMin = src.m_gridRoot.anchorMin; root.anchorMax = src.m_gridRoot.anchorMax; root.pivot = src.m_gridRoot.pivot;   // vanilla's own cell-root anchoring
            root.anchoredPosition = Vector2.zero; root.sizeDelta = new Vector2(0f, src.m_gridRoot.sizeDelta.y);
            var grid = go.AddComponent<InventoryGrid>();
            grid.m_elementPrefab = src.m_elementPrefab;   // the asset prefab (scene object False in the 1.0.187 probe)
            grid.m_gridRoot = root;
            grid.m_elementSpace = src.m_elementSpace;
            grid.m_tooltipAnchor = src.m_tooltipAnchor;
            grid.m_showDropFocus = src.m_showDropFocus;
            grid.m_uiGroup = parent.GetComponentInParent<UIGroupHandler>() ?? src.m_uiGroup;
            grid.CanDropDragOntoItem = src.CanDropDragOntoItem ?? (item => false);
            go.SetActive(true);

            var handle = new GridHandle { Grid = grid, Options = options };
            grid.m_onSelected = (g, item, pos, mod) => OnSelected(handle, g, item, pos, mod);
            grid.m_onRightClick = (g, item, pos) => OnRight(handle, g, item, pos);
            handle.Driver = go.AddComponent<GridDriver>();
            handle.Driver.Handle = handle;
            mounted.Add(handle);
            EquipmentPanel.PanelLog?.LogDebug($"grid api: mounted for {options.Owner} under '{parent.name}' ({MountedCount} mounted)");
            return handle;
        }

        // ---- 1.0.191 --------------------------------------
        // LendPlayerGrid moves the REAL player grid (InventoryGui.m_playerGrid, VAInventory's styled grid) into a host rect of another mod's
        // window; clicks, drag, tooltips and the look are the main grid's own. InventoryGui is opened behind it (if it wasn't open) with every
        // other inventory panel invisible and click-through, because vanilla only updates the grid while the inventory is shown.
        // ReturnPlayerGrid puts it back exactly (parent, sibling index, anchors, offsets, pivot, scale) and restores the panels; it also runs
        // by itself when the inventory is closed (Esc / Tab) while lent. ADD-ONLY like the rest of this API.
        //   var t = AccessTools.TypeByName("VerdantsAscent.Managers.VAInventoryGridApi");
        //   t.GetMethod("LendPlayerGrid").Invoke(null, new object[] { hostRect, "FiresRPGmaker.Dressing" });   // on tab open
        //   t.GetMethod("ReturnPlayerGrid").Invoke(null, null);                                                  // on tab close
        sealed class Lend
        {
            public string Owner; public RectTransform Grid, Parent; public int Sibling;
            public Vector2 AnchorMin, AnchorMax, OffsetMin, OffsetMax, Pivot; public Vector3 Scale;
            public bool OpenedInventory;
            public float Space;
            public GameObject DropButton;
            public readonly List<(CanvasGroup group, float alpha, bool raycasts, bool added)> Hidden = new List<(CanvasGroup, float, bool, bool)>();
        }
        static Lend s_lend;

        public static bool IsPlayerGridLent => s_lend != null;
        public static bool IsLent => s_lend != null;
        public static string LentTo => s_lend?.Owner;
        /// <summary>Raised (owner) after the grid went back - also when the inventory was closed while lent.</summary>
        public static event Action<string> PlayerGridReturned;

        /// <summary>Returns a handle for ReturnPlayerGrid, or null (logged).</summary>
        public static object LendPlayerGrid(RectTransform host, string owner)
        {
            var gui = InventoryGui.instance;
            if (host == null || gui == null || gui.m_playerGrid == null || Player.m_localPlayer == null)
            {
                EquipmentPanel.PanelLog?.LogDebug($"grid api: lend to {owner} REFUSED (host={(host != null)}, gui={(gui != null)})");
                return null;
            }
            if (s_lend != null) ReturnPlayerGrid();
            var grid = (RectTransform)gui.m_playerGrid.transform;
            var l = new Lend
            {
                Owner = owner, Grid = grid, Parent = (RectTransform)grid.parent, Sibling = grid.GetSiblingIndex(),
                AnchorMin = grid.anchorMin, AnchorMax = grid.anchorMax, OffsetMin = grid.offsetMin, OffsetMax = grid.offsetMax,
                Pivot = grid.pivot, Scale = grid.localScale
            };
            if (!InventoryGui.IsVisible()) { gui.Show(null); l.OpenedInventory = true; }
            // every other inventory panel: invisible and click-through while the grid is lent (restored exactly on return)
            foreach (var panel in new[] { gui.m_player, gui.m_container, gui.m_crafting, gui.m_info, UnifiedScreen.CharacterPanel })
            {
                if (panel == null) continue;
                var cg = panel.GetComponent<CanvasGroup>();
                bool added = cg == null;
                if (added) cg = panel.gameObject.AddComponent<CanvasGroup>();
                l.Hidden.Add((cg, cg.alpha, cg.blocksRaycasts, added));
                cg.alpha = 0f; cg.blocksRaycasts = false;
            }
            // vanilla's full-screen drop zone (IngameGui/Inventory_screen/root/dropButton, a stretched Button) would eat every click on the
            // host window; off while lent, back on return
            var drop = gui.m_inventoryRoot != null ? gui.m_inventoryRoot.Find("dropButton") : null;
            if (drop != null && drop.gameObject.activeSelf) { drop.gameObject.SetActive(false); l.DropButton = drop.gameObject; }
            grid.SetParent(host, false);
            grid.anchorMin = Vector2.zero; grid.anchorMax = Vector2.one; grid.offsetMin = grid.offsetMax = Vector2.zero; grid.pivot = new Vector2(0.5f, 0.5f);
            grid.localScale = Vector3.one;
            // the grid's own CanvasGroup chain now comes from the host; make sure nothing inherited hides it
            var own = grid.GetComponent<CanvasGroup>(); if (own != null) { own.alpha = 1f; own.blocksRaycasts = true; }
            // cells sized to the host (restored on return); the grid keeps its own visible-rows-only rule
            l.Space = gui.m_playerGrid.m_elementSpace;
            int cols = Mathf.Max(1, Player.m_localPlayer.GetInventory().GetWidth());
            if (host.rect.width > 10f) gui.m_playerGrid.m_elementSpace = Mathf.Min(l.Space, Mathf.Floor(host.rect.width / cols));
            s_lend = l;
            EquipmentPanel.PanelLog?.LogDebug($"grid api: player grid LENT to {owner} under '{host.name}' ({host.rect.width:0}x{host.rect.height:0}); inventory opened for it {l.OpenedInventory}, {l.Hidden.Count} panel(s) hidden");
            return l;
        }

        public static void ReturnPlayerGrid(object handle) { if (handle == null || ReferenceEquals(handle, s_lend)) ReturnPlayerGrid(); }

        public static void ReturnPlayerGrid()
        {
            var l = s_lend;
            if (l == null) return;
            if (InventoryGui.instance != null && InventoryGui.instance.m_playerGrid != null && l.Space > 0f) InventoryGui.instance.m_playerGrid.m_elementSpace = l.Space;
            s_lend = null;
            if (l.Grid != null && l.Parent != null)
            {
                l.Grid.SetParent(l.Parent, false);
                l.Grid.SetSiblingIndex(Mathf.Min(l.Sibling, l.Parent.childCount - 1));
                l.Grid.anchorMin = l.AnchorMin; l.Grid.anchorMax = l.AnchorMax; l.Grid.pivot = l.Pivot;
                l.Grid.offsetMin = l.OffsetMin; l.Grid.offsetMax = l.OffsetMax; l.Grid.localScale = l.Scale;
            }
            if (l.DropButton != null) l.DropButton.SetActive(true);
            foreach (var (cg, alpha, raycasts, added) in l.Hidden)
            {
                if (cg == null) continue;
                if (added) UnityEngine.Object.Destroy(cg); else { cg.alpha = alpha; cg.blocksRaycasts = raycasts; }
            }
            if (l.OpenedInventory && InventoryGui.IsVisible() && InventoryGui.instance != null) InventoryGui.instance.Hide();
            EquipmentPanel.PanelLog?.LogDebug($"grid api: player grid RETURNED from {l.Owner} (back under '{l.Parent?.name}' at sibling {l.Sibling}; inventory closed again {l.OpenedInventory})");
            try { PlayerGridReturned?.Invoke(l.Owner); } catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning("grid api: a PlayerGridReturned handler threw: " + e.Message); }
        }

        public static void Unmount(GridHandle handle)
        {
            if (handle == null || handle.Grid == null) return;
            var owner = handle.Options?.Owner;
            UnityEngine.Object.Destroy(handle.Grid.gameObject);
            handle.Grid = null; handle.Driver = null;
            mounted.Remove(handle);
            EquipmentPanel.PanelLog?.LogDebug($"grid api: unmounted for {owner} ({MountedCount} mounted)");
        }

        static void OnSelected(GridHandle h, InventoryGrid g, ItemDrop.ItemData item, Vector2i pos, InventoryGrid.Modifier mod)
        {
            var gui = InventoryGui.instance;
            var dragBefore = gui != null ? gui.m_dragItem : null;
            if (h.Options.VanillaClicks && gui != null) miSelected?.Invoke(gui, new object[] { g, item, pos, mod });
            Raise(() => ItemClicked?.Invoke(h, item, pos, mod), "ItemClicked");
            var dragAfter = gui != null ? gui.m_dragItem : null;
            if (dragAfter != null && dragAfter != dragBefore) Raise(() => ItemDragged?.Invoke(h, dragAfter), "ItemDragged");
            // One Debug line per click on a mounted grid - what was under the
            // pointer, what the cursor held before and after, and what vanilla's OnSelectedItem did (picked up / dropped / swapped / nothing)
            var inv = g != null ? g.GetInventory() : null;
            string N(ItemDrop.ItemData i) => i == null ? "nothing" : $"{i.m_shared.m_name} x{i.m_stack}";
            string what = dragBefore == null && dragAfter != null ? "picked up" : dragBefore != null && dragAfter == null ? "dropped"
                : dragBefore != null && dragAfter != null && dragAfter != dragBefore ? "swapped" : dragBefore != null ? "held still (refused?)" : "nothing";
            EquipmentPanel.PanelLog?.LogDebug($"grid api: click on {h.Options.Owner} {pos} ({mod}): cell {N(item)}, cursor {N(dragBefore)} -> {N(dragAfter)} = {what}; vanilla clicks {h.Options.VanillaClicks}, inventory {(inv != null ? inv.GetName() + " " + inv.GetWidth() + "x" + inv.GetHeight() : "none")}");
        }

        static void OnRight(GridHandle h, InventoryGrid g, ItemDrop.ItemData item, Vector2i pos)
        {
            var gui = InventoryGui.instance;
            if (h.Options.VanillaClicks && h.Options.VanillaRightClick && gui != null) miRight?.Invoke(gui, new object[] { g, item, pos });
            Raise(() => ItemRightClicked?.Invoke(h, item, pos), "ItemRightClicked");
        }

        static void Raise(Action a, string what)
        {
            try { a(); }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"grid api: a {what} handler threw: {e.Message}"); }
        }

        // ---- 1.0.187 probe --
        static readonly Dictionary<InventoryGrid, bool> s_allowed = new Dictionary<InventoryGrid, bool>();
        static readonly Dictionary<InventoryGrid, int> s_suppressed = new Dictionary<InventoryGrid, int>();

        /// <summary>Called from the UpdateGui hook prefix: did vanilla's UpdateGui run on this grid this frame?</summary>
        internal static void NoteUpdateGui(InventoryGrid grid, bool ran)
        {
            bool ours = false;
            foreach (var h in mounted) if (h.Grid == grid) { ours = true; break; }
            if (!ours) return;
            s_allowed[grid] = ran;
            if (!ran) s_suppressed[grid] = (s_suppressed.TryGetValue(grid, out int n) ? n : 0) + 1;
        }

        static void ProbeMounted(GridHandle h, Inventory inv, string when)
        {
            var grid = h.Grid;
            if (grid == null) return;
            var sb = new System.Text.StringBuilder();
            sb.Append($"grid api: probe {when} {h.Options?.Owner}: {grid.m_elements.Count} element(s), last UpdateGui ran {(s_allowed.TryGetValue(grid, out bool r) ? r.ToString() : "never")}, suppressed {(s_suppressed.TryGetValue(grid, out int s) ? s : 0)} time(s); prefab '{grid.m_elementPrefab?.name}' (scene object {(grid.m_elementPrefab != null && grid.m_elementPrefab.scene.IsValid())})");
            InventoryElement used = null, empty = null;
            foreach (var e in grid.m_elements)
            {
                if (e == null) continue;
                bool has = inv != null && inv.GetItemAt(e.Position.x, e.Position.y) != null;
                if (has && used == null) used = e;
                if (!has && empty == null) empty = e;
            }
            foreach (var (label, e) in new[] { ("ITEM cell", used), ("EMPTY cell", empty) })
            {
                if (e == null) { sb.Append($" | {label}: none"); continue; }
                sb.Append($" | {label} {e.Position.x},{e.Position.y} m_used {e.m_used} active {e.gameObject.activeInHierarchy}:");
                foreach (var g in e.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
                {
                    if (!g.enabled) continue;
                    var img = g as UnityEngine.UI.Image;
                    var txt = g as TMPro.TMP_Text;
                    sb.Append($" ['{g.name}' {g.GetType().Name} col {g.color} rendered {g.canvasRenderer.GetColor()}{(img != null ? " sprite '" + img.sprite?.name + "'" : "")}{(txt != null ? " text '" + txt.text + "'" : "")}]");
                }
            }
            EquipmentPanel.PanelLog?.LogDebug(sb.ToString());
        }

        /// <summary>Vanilla UpdateGui's per-cell drawing (InventoryGrid.UpdateGui), for a mounted grid.</summary>
        static void DrawCells(InventoryGrid grid, Inventory inv, Player player)
        {
            var drag = InventoryGui.instance != null ? InventoryGui.instance.m_dragItem : null;
            foreach (var e in grid.m_elements)
            {
                if (e == null) continue;
                var item = inv.GetItemAt(e.Position.x, e.Position.y);
                e.m_used = item != null;
                if (e.m_selected != null && e.m_selected.activeSelf) e.m_selected.SetActive(false);
                if (e.m_dropFocus != null && e.m_dropFocus.color.a > 0f) e.m_dropFocus.color = Color.clear;
                if (item == null)
                {
                    if (e.m_durability != null) e.m_durability.gameObject.SetActive(false);
                    if (e.m_icon != null) e.m_icon.enabled = false;
                    if (e.m_amount != null) e.m_amount.enabled = false;
                    if (e.m_quality != null) e.m_quality.enabled = false;
                    if (e.m_equiped != null) e.m_equiped.enabled = false;
                    if (e.m_queued != null) e.m_queued.enabled = false;
                    if (e.m_noteleport != null) e.m_noteleport.enabled = false;
                    if (e.m_food != null) e.m_food.enabled = false;
                    if (e.m_tooltip != null) { e.m_tooltip.m_text = ""; e.m_tooltip.m_topic = ""; }
                    continue;
                }
                if (e.m_icon != null) { e.m_icon.enabled = true; e.m_icon.sprite = item.GetIcon(); e.m_icon.color = item == drag ? Color.grey : Color.white; }
                bool dur = item.m_shared.m_useDurability && item.m_durability < item.GetMaxDurability();
                if (e.m_durability != null)
                {
                    e.m_durability.gameObject.SetActive(dur);
                    if (dur) { e.m_durability.SetValue(item.m_durability <= 0f ? 1f : item.GetDurabilityPercentage()); e.m_durability.ResetColor(); }
                }
                bool mine = player != null && inv == player.GetInventory();
                if (e.m_equiped != null) e.m_equiped.enabled = mine && item.m_equipped;
                if (e.m_queued != null) e.m_queued.enabled = mine && player.IsEquipActionQueued(item);
                if (e.m_noteleport != null) e.m_noteleport.enabled = !item.m_shared.m_teleportable && !(ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.TeleportAll));
                if (e.m_food != null)
                    e.m_food.enabled = item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable
                                       && (item.m_shared.m_food > 0f || item.m_shared.m_foodStamina > 0f || item.m_shared.m_foodEitr > 0f);
                if (e.m_quality != null) { e.m_quality.enabled = item.m_shared.m_maxQuality > 1; if (e.m_quality.enabled) e.m_quality.text = item.m_quality.ToString(); }
                if (e.m_amount != null) { e.m_amount.enabled = item.m_shared.m_maxStackSize > 1; if (e.m_amount.enabled) e.m_amount.text = mine ? BagStackCap.CountText(inv, item) : $"{item.m_stack}/{item.m_shared.m_maxStackSize}"; }
                if (e.m_tooltip != null) { e.m_tooltip.m_topic = item.m_shared.m_name; e.m_tooltip.m_text = item.GetTooltip(); }
            }
        }

        /// <summary>Once per mount, the references vanilla's UpdateGui dereferences, so an NRE's owner can be named.</summary>
        static void LogNulls(InventoryGrid grid)
        {
            var nulls = new List<string>();
            if (grid.m_uiGroup == null) nulls.Add("m_uiGroup");
            if (grid.CanDropDragOntoItem == null) nulls.Add("CanDropDragOntoItem");   // a null here caused an NRE in UpdateGui
            if (grid.m_gridRoot == null) nulls.Add("m_gridRoot");
            if (grid.m_elementPrefab == null) nulls.Add("m_elementPrefab");
            if (UnityEngine.EventSystems.EventSystem.current == null) nulls.Add("EventSystem.current");
            if (ZoneSystem.instance == null) nulls.Add("ZoneSystem.instance");
            var p = grid.m_elementPrefab != null ? grid.m_elementPrefab.GetComponent<InventoryElement>() : null;
            if (p != null)
            {
                if (p.m_icon == null) nulls.Add("element.m_icon"); if (p.m_durability == null) nulls.Add("element.m_durability");
                if (p.m_equiped == null) nulls.Add("element.m_equiped"); if (p.m_queued == null) nulls.Add("element.m_queued");
                if (p.m_noteleport == null) nulls.Add("element.m_noteleport"); if (p.m_food == null) nulls.Add("element.m_food");
                if (p.m_tooltip == null) nulls.Add("element.m_tooltip"); if (p.m_selected == null) nulls.Add("element.m_selected");
                if (p.m_dropFocus == null) nulls.Add("element.m_dropFocus");
            }
            EquipmentPanel.PanelLog?.LogDebug($"grid api: '{grid.name}' null references vanilla UpdateGui reads: {(nulls.Count == 0 ? "none (the NRE comes from a patch on UpdateGui)" : string.Join(", ", nulls.ToArray()))}");
        }

        internal sealed class GridDriver : MonoBehaviour
        {
            bool nullsLogged;
            float probe1At = -1f, probe4At = -1f;
            void Start() { probe1At = Time.unscaledTime + 1f; probe4At = Time.unscaledTime + 4f; }
            internal GridHandle Handle;
            void LateUpdate() => Redraw();
            void OnDestroy() { if (Handle != null && Handle.Grid != null) { mounted.Remove(Handle); Handle.Grid = null; } }

            static Inventory SafeInv(Func<Inventory> f) { try { return f(); } catch { return null; } }

            int lastShown = -1, lastSkinnedCount = -1;
            float lastSpace = -1f;
            // 1.0.358 (V4): redraw on change only
            const float IdleHz = 4f, InvResolveSeconds = 1f;
            Inventory cachedInv; float invAt, nextRedraw; int lastSig = int.MinValue; ItemDrop.ItemData lastDrag; bool redrawLogged;
            static Camera GridCamera(RectTransform rt)
            {
                var canvas = rt.GetComponentInParent<Canvas>();
                return canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            }

            internal void Redraw()
            {
                var player = Player.m_localPlayer;
                if (Handle?.Grid == null || player == null) return;
                // 1.0.358 (UI efficiency audit V4, Fire 10-26): this LateUpdate redrew the whole grid (vanilla UpdateInventory over every
                // cell) EVERY frame, and resolved the inventory through the caller's delegate (reflection for the companion panel) each time.
                // Now the inventory is re-resolved once a second, and the grid redraws when its items or the dragged item change, while the
                // pointer is over it (hover / drop highlights), or at IdleHz otherwise.
                float now = Time.unscaledTime;
                if (cachedInv == null || now >= invAt)
                {
                    cachedInv = Handle.Options?.Inventory != null ? SafeInv(Handle.Options.Inventory) : player.GetInventory();
                    invAt = now + InvResolveSeconds;
                }
                var inv = cachedInv;
                if (inv == null) return;
                var grid = Handle.Grid;
                var drag = InventoryGui.instance != null ? InventoryGui.instance.m_dragItem : null;
                int sig = inv.NrOfItems() * 397 + inv.GetWidth() * 13 + inv.GetHeight();
                foreach (var it in inv.GetAllItems()) if (it != null) sig = sig * 31 + it.m_stack + it.m_gridPos.x * 7 + it.m_gridPos.y * 131 + (it.m_equipped ? 1 : 0) + (it.m_shared != null ? it.m_shared.m_name.GetHashCode() : 0);
                var gridRt = grid.transform as RectTransform;
                bool hover = gridRt != null && RectTransformUtility.RectangleContainsScreenPoint(gridRt, Input.mousePosition, GridCamera(gridRt));
                if (sig == lastSig && drag == lastDrag && !hover && now < nextRedraw) return;
                lastSig = sig; lastDrag = drag; nextRedraw = now + 1f / IdleHz;
                if (!redrawLogged) { redrawLogged = true; EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] grid api: '{grid.name}' redraws on change / hover / drag, {IdleHz:0} Hz idle (was every frame)"); }
                // Vanilla's UpdateGui builds width x GetHeight()
                // cells, and the player's backing inventory also holds VAInventory's hidden storage rows (pouches, tools, ammo, food, hotbars,
                // equipment). The main grid hides them through patches gated on m_playerGrid; this clone got none of them. Here:
                // (2) the cells fit the host panel: element space = host width / columns, never larger than vanilla's
                var host = (RectTransform)grid.transform.parent;
                int cols = Mathf.Max(1, inv.GetWidth());
                float vanillaSpace = InventoryGui.instance != null && InventoryGui.instance.m_playerGrid != null ? InventoryGui.instance.m_playerGrid.m_elementSpace : grid.m_elementSpace;
                if (host != null && host.rect.width > 10f)
                {
                    float space = Mathf.Min(vanillaSpace, Mathf.Floor(host.rect.width / cols));
                    if (Mathf.Abs(space - grid.m_elementSpace) > 0.5f) grid.m_elementSpace = space;
                }
                if (!nullsLogged) { nullsLogged = true; LogNulls(grid); }
                // Vanilla draws the cells (clean grid, CanDropDragOntoItem set); the 1.0.188 per-cell redraw workaround is gone
                grid.UpdateInventory(inv, player, InventoryGui.instance != null ? InventoryGui.instance.m_dragItem : null);
                // (1) only the VISIBLE rows of the player's own inventory (the main grid's rule: y < Slots.InventoryHeightPlayer)
                bool playerInv = inv == player.GetInventory();
                int rows = playerInv ? Slots.InventoryHeightPlayer : inv.GetHeight();
                if (Handle.Options != null && Handle.Options.MaxRows > 0) rows = Mathf.Min(rows, Handle.Options.MaxRows);   // 1.0.200
                int shown = 0;
                foreach (var e in grid.m_elements)
                {
                    if (e == null) continue;
                    bool show = e.Position.y < rows;   // 1.0: InventoryElement.Position (the grid cell)
                    if (e.gameObject.activeSelf != show) e.gameObject.SetActive(show);
                    if (show) shown++;
                }
                if (grid.m_gridRoot != null)
                {
                    var size = new Vector2(cols * grid.m_elementSpace, rows * grid.m_elementSpace);
                    if (grid.m_gridRoot.sizeDelta != size) grid.m_gridRoot.sizeDelta = size;
                }
                // (3) the same slot look as the main grid: Core's CraftingSkin (Role.Slot) after each rebuild of the cells
                if (grid.m_elements.Count != lastSkinnedCount) { lastSkinnedCount = grid.m_elements.Count; PanelsDock.Skin((RectTransform)grid.transform); }
                if (probe1At > 0f && Time.unscaledTime >= probe1At) { probe1At = -1f; ProbeMounted(Handle, inv, "mount+1s"); }
                if (probe4At > 0f && Time.unscaledTime >= probe4At) { probe4At = -1f; ProbeMounted(Handle, inv, "mount+4s"); }
                if (shown != lastShown || Mathf.Abs(grid.m_elementSpace - lastSpace) > 0.5f)
                {
                    lastShown = shown; lastSpace = grid.m_elementSpace;
                    EquipmentPanel.PanelLog?.LogDebug($"grid api: {Handle.Options?.Owner} grid shows {shown} cell(s) = {cols} x {rows} visible rows of {inv.GetHeight()} (player inventory {playerInv}), cell {grid.m_elementSpace:0} px in a {(host != null ? host.rect.width : 0f):0} px wide host");
                }
            }
        }
    }
}
