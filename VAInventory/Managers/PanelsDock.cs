using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.160 (Fire 10-15: "everything dockable, movable and resizable", and Fire arranges the shipped default himself): in the Panels
    /// layout, every inventory panel becomes a Core FiresDock element (FiresCore.UI.Dock.DockService, Core 0.2.522+), layer "inventory".
    /// Resolved by exact Type.GetType; on an older Core this is inert and PanelsLayout keeps placing the fixed row as in 1.0.159.
    ///   vainv.grid       vanilla m_player (the carry grid)          root of the default row
    ///   vainv.chest      vanilla m_container                         docked under vainv.grid (Fire: its own panel, movable)
    ///   vainv.character  the character panel                         docked right of vainv.grid
    ///   vainv.companion  the companion column (while one is open)    docked right of vainv.info
    ///   vainv.info       vanilla m_info (the player name plate)      docked right of vainv.character
    ///   vainv.crafting   vanilla m_crafting                          docked under vainv.info (Fire: "attached like vanilla")
    /// Core places only position + localScale; nothing is re-parented except OUR character panel, lifted off vanilla's Player panel so
    /// it moves on its own. Defaults = where 1.0.159's row put them on the first open (DefaultRect null = the spot at Register); Fire's own
    /// arrangement replaces that via Core's "fires_layout_capture inventory".
    /// </summary>
    internal static class PanelsDock
    {
        const string ServiceType = "FiresCore.UI.Dock.DockService, FiresUnifiedCore";
        const string OptionsType = "FiresCore.UI.Dock.DockElementOptions, FiresUnifiedCore";
        const string EdgeType = "FiresCore.UI.Dock.DockEdge, FiresUnifiedCore";
        const string SkinType = "FiresCore.UI.Shell.CraftingSkin, FiresUnifiedCore";

        static bool s_resolved, s_ok;
        static Type s_service, s_options, s_edge;
        static MethodInfo s_register, s_skin;
        internal static bool Registered { get; private set; }

        /// <summary>1.0.170: a new InventoryGui = register everything again (Core drops elements whose rect was destroyed).</summary>
        internal static void ResetForNewGui()
        {
            if (Available)
                foreach (var id in s_ids)
                    try { s_service.GetMethod("Unregister", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { id }); } catch { }
            s_ids.Clear();
            Registered = false;
        }
        static readonly HashSet<string> s_ids = new HashSet<string>();

        internal static bool Available
        {
            get
            {
                if (!s_resolved)
                {
                    s_resolved = true;
                    s_service = Type.GetType(ServiceType); s_options = Type.GetType(OptionsType); s_edge = Type.GetType(EdgeType);
                    s_register = s_service?.GetMethod("Register", BindingFlags.Public | BindingFlags.Static);
                    s_skin = Type.GetType(SkinType)?.GetMethod("ApplyTo", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(RectTransform) }, null);
                    // Core 0.2.525 3BF4095EC804+ only: the same build added Passive AND re-applies roots / floating panels every frame
                    // (vanilla's Animator rewrites m_player / m_crafting each frame; an older FiresDock would let them snap back).
                    s_ok = s_register != null && s_options != null && s_edge != null && s_options.GetField("Passive", BindingFlags.Public | BindingFlags.Instance) != null;
                    EquipmentPanel.PanelLog?.LogDebug(s_ok
                        ? "panels: Core FiresDock found; every inventory panel is dockable / movable / resizable"
                        : s_register != null ? "panels: Core FiresDock is older than 0.2.525 3BF4095EC804 (no Passive / root re-place); the fixed row stays"
                        : "panels: no Core FiresDock (needs Core 0.2.525+); the fixed row stays");
                }
                return s_ok;
            }
        }

        /// <summary>Lifts OUR character panel off vanilla's Player panel (it rode on it for the Unified layout) so it docks on its own.</summary>
        internal static void DetachCharacter(RectTransform character, Transform inventoryRoot)
        {
            if (character == null || inventoryRoot == null || character.parent == inventoryRoot) return;
            character.SetParent(inventoryRoot, true);   // world position and size kept
            // 1.0.169 (Fire's live edit: 'dragging immediately fucks everything up'): the Unified layout's own drag handler (on the panel's
            // backdrop) and its resize grip moved the SAME panel Core's dock was moving. Under the dock, they go.
            foreach (var drag in character.GetComponentsInChildren<CharacterPanelDrag>(true)) UnityEngine.Object.Destroy(drag);
            var grip = character.Find("VAInventoryResizeGrip");
            if (grip != null) UnityEngine.Object.Destroy(grip.gameObject);
            EquipmentPanel.PanelLog?.LogDebug("panels: character panel lifted off vanilla's Player panel for docking");
        }

        /// <summary>1.0.160: the grid's first row (the 1-8 slots) as a Passive element ([generator], Core 0.2.525: no handles, never dragged,
        /// not saved, moved by its parent); the HUD hotbar follows it (FollowId, HUD layout part 2). A rect over row 0 inside the grid root.</summary>
        internal static void RegisterGridRow1(InventoryGrid grid)
        {
            if (!Available || grid == null || grid.m_gridRoot == null || s_ids.Contains("vainv.grid.row1")) return;
            if (s_options.GetField("Passive", BindingFlags.Public | BindingFlags.Instance) == null) return;   // never as a draggable element on an older Core
            var go = new GameObject("VAInventoryGridRow1", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(grid.m_gridRoot, false);
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = Vector2.zero; rt.sizeDelta = new Vector2(0f, grid.m_elementSpace);
            Register("vainv.grid.row1", "Inventory row 1", rt, null, null, null, passive: true);
        }

        internal static void Register(string id, string label, RectTransform rect, string dockTo, string edge, Func<bool> visible) => Register(id, label, rect, dockTo, edge, visible, false);

        internal static void Register(string id, string label, RectTransform rect, string dockTo, string edge, Func<bool> visible, bool passive) =>
            Register(id, label, rect, dockTo, edge, visible, passive, null);

        internal static void Register(string id, string label, RectTransform rect, string dockTo, string edge, Func<bool> visible, bool passive, Func<Rect> defaultRect)
        {
            if (!Available || rect == null || s_ids.Contains(id)) return;
            try
            {
                var o = Activator.CreateInstance(s_options);
                Set(o, "Id", id); Set(o, "Owner", "VAInventory"); Set(o, "Layer", "inventory"); Set(o, "Rect", rect); Set(o, "Label", label);
                if (dockTo != null) { Set(o, "DefaultDockTo", dockTo); Set(o, "DefaultEdge", Enum.Parse(s_edge, edge)); }
                if (visible != null) Set(o, "Visible", visible);
                if (passive) Set(o, "Passive", true);   // Core 0.2.525 3BF4095EC804+; an older Core has no field and it stays a normal element
                // 1.0.178: an owner default spot (Core reads DefaultRect at Register and at Reset); then the seeded row spot must not replace it
                if (defaultRect != null) Set(o, "DefaultRect", defaultRect);
                // 1.0.223 (Fire: "move by header, resize bottom-right, dock, all without Alt"; Core 0.2.570 LiveEdit): every inventory panel
                // moves by its header and resizes by Core's bottom-right handle at any time; the Alt editor stays for the extras
                var handle = passive ? null : LiveHandle(id, rect);   // 1.0.227 ([generator]): a PASSIVE element (vainv.grid.row1, a FollowId reference) gets no drag in Core; the strip belongs to vainv.grid
                if (handle != null) { Set(o, "DragHandle", handle); Set(o, "LiveEdit", true); }
                Vector2? seeded = passive || defaultRect != null ? (Vector2?)null : ScreenTopLeft(rect);   // 1.0.171: BEFORE Register (it may move the panel to a saved spot)
                s_register.Invoke(null, new[] { o });
                if (seeded.HasValue) SetDefault(id, seeded.Value);   // 1.0.171 ([generator], Core 0.2.534): Reset returns here (the seeded row)
                s_ids.Add(id);
                Registered = true;
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"panels: dock register {id} failed: {(e.InnerException ?? e).Message}"); }
        }

        /// <summary>
        /// 1.0.178 (Fire, many times: "get crafting AWAY from the player grid, flush against the RIGHT edge, vertically centred"): the
        /// crafting panel's default spot, screen px top-left (y down): zero gap to the right edge, centred top to bottom, from its own
        /// on-screen size. It is no longer docked to the character panel (that dock rule glued it beside the grid).
        /// </summary>
        internal static Rect CraftingRightDefault(RectTransform crafting)
        {
            if (crafting == null) return new Rect(0f, 0f, 0f, 0f);
            var c = new Vector3[4]; crafting.GetWorldCorners(c);
            var canvas = crafting.GetComponentInParent<Canvas>()?.rootCanvas;
            Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, c[0]), tr = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
            float w = Mathf.Abs(tr.x - bl.x), h = Mathf.Abs(tr.y - bl.y);
            var r = new Rect(Screen.width - w, Mathf.Max(0f, (Screen.height - h) * 0.5f), w, h);
            if (!s_craftingLogged || r.size != s_craftingLast) { s_craftingLogged = true; s_craftingLast = r.size; EquipmentPanel.PanelLog?.LogDebug($"panels: crafting default = right edge, centred: {r.x:0},{r.y:0} ({w:0}x{h:0} on {Screen.width}x{Screen.height})"); }
            return r;
        }
        static bool s_craftingLogged; static Vector2 s_craftingLast;

        /// <summary>
        /// 1.0.178 (so Fire never has to press Reset): ONCE per install, drop the saved "vainv.crafting" spot (and anything docked to it) from
        /// every saved inventory dock layout, before Core loads them, so the new right-edge default applies. A marker in
        /// BepInEx\cache\VAInventory records that it ran; later moves Fire makes are saved and kept as usual.
        /// </summary>
        internal static void MigrateCraftingRightOnce()
        {
            try
            {
                string mine = System.IO.Path.Combine(BepInEx.Paths.CachePath, "VAInventory");
                string marker = System.IO.Path.Combine(mine, "crafting_right_edge_1178.done");
                if (System.IO.File.Exists(marker)) return;
                string dir = System.IO.Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "layout");
                int files = 0, dropped = 0;
                if (System.IO.Directory.Exists(dir))
                    foreach (string f in System.IO.Directory.GetFiles(dir, "dock_inventory_*.txt"))
                    {
                        var keep = new List<string>();
                        foreach (string line in System.IO.File.ReadAllLines(f))
                        {
                            string[] p = line.Split('|');
                            bool crafting = p.Length > 0 && p[0] == "vainv.crafting";
                            bool onCrafting = p.Length > 5 && p[5] == "vainv.crafting";
                            if (crafting || onCrafting) { dropped++; continue; }
                            keep.Add(line);
                        }
                        System.IO.File.WriteAllLines(f, keep);
                        files++;
                    }
                System.IO.Directory.CreateDirectory(mine);
                System.IO.File.WriteAllText(marker, DateTime.Now.ToString("s") + " dropped " + dropped + " line(s) in " + files + " file(s)\n");
                // Core read the saved layouts when the HUD registered (world load), before the inventory opens: have it read the edited files
                // again WITHOUT saving first (TestReload(false), Core 0.2.525+), or the old spot would still be in memory
                if (dropped > 0 && Available)
                    try { s_service.GetMethod("TestReload", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null)?.Invoke(null, new object[] { false }); }
                    catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"panels: layout reload after the crafting move failed: {(e.InnerException ?? e).Message}"); }
                EquipmentPanel.PanelLog?.LogDebug($"panels: crafting moved to the right edge: dropped {dropped} saved line(s) in {files} inventory layout file(s) (once)");
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"panels: crafting layout migration failed: {e.Message}"); }
        }

        /// <summary>1.0.196: ONCE per install (marker in BepInEx\cache\VAInventory), drop the saved spot of these element ids (and anything
        /// docked to them) from every saved inventory dock layout, then have Core re-read the files. Same steps as MigrateCraftingRightOnce.</summary>
        internal static void MigrateDropSavedOnce(string marker, params string[] ids)
        {
            try
            {
                string mine = System.IO.Path.Combine(BepInEx.Paths.CachePath, "VAInventory");
                string file = System.IO.Path.Combine(mine, marker + ".done");
                if (System.IO.File.Exists(file)) return;
                string dir = System.IO.Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "layout");
                int files = 0, dropped = 0;
                var drop = new HashSet<string>(ids);
                if (System.IO.Directory.Exists(dir))
                    foreach (string f in System.IO.Directory.GetFiles(dir, "dock_inventory_*.txt"))
                    {
                        var keep = new List<string>();
                        foreach (string line in System.IO.File.ReadAllLines(f))
                        {
                            string[] p = line.Split('|');
                            if ((p.Length > 0 && drop.Contains(p[0])) || (p.Length > 5 && drop.Contains(p[5]))) { dropped++; continue; }
                            keep.Add(line);
                        }
                        System.IO.File.WriteAllLines(f, keep);
                        files++;
                    }
                System.IO.Directory.CreateDirectory(mine);
                System.IO.File.WriteAllText(file, DateTime.Now.ToString("s") + " dropped " + dropped + " line(s) in " + files + " file(s)\n");
                if (dropped > 0 && Available)
                    try { s_service.GetMethod("TestReload", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null)?.Invoke(null, new object[] { false }); } catch { }
                EquipmentPanel.PanelLog?.LogDebug($"panels: saved spot of {string.Join(", ", ids)} dropped: {dropped} line(s) in {files} inventory layout file(s) (once, {marker})");
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"panels: layout migration {marker} failed: {e.Message}"); }
        }

        // 1.0.171: DockService.SetDefault(id, screen top-left, y down), Core 0.2.534+; skipped on an older Core
        static MethodInfo s_setDefault; static bool s_setDefaultLooked;

        static void SetDefault(string id, Vector2 topLeft)
        {
            if (!s_setDefaultLooked) { s_setDefaultLooked = true; s_setDefault = s_service?.GetMethod("SetDefault", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Vector2) }, null); }
            if (s_setDefault == null) return;
            try { s_setDefault.Invoke(null, new object[] { id, topLeft }); EquipmentPanel.PanelLog?.LogDebug($"panels: {id} default = the seeded row spot {topLeft.x:0},{topLeft.y:0}"); }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning($"panels: {id} SetDefault failed: {(e.InnerException ?? e).Message}"); }
        }

        static Vector2 ScreenTopLeft(RectTransform rect)
        {
            var c = new Vector3[4]; rect.GetWorldCorners(c);
            var canvas = rect.GetComponentInParent<Canvas>()?.rootCanvas;
            Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector2 tl = RectTransformUtility.WorldToScreenPoint(cam, c[1]);
            return new Vector2(tl.x, Screen.height - tl.y);
        }
        /// <summary>The Fires UI kit look (Core's shared classifier; restores the vanilla look with the override OFF). Idempotent.</summary>
        internal static void Skin(RectTransform rect)
        {
            if (rect == null) return;
            if (!s_resolved) { var _ = Available; }
            if (s_skin == null) return;
            try { s_skin.Invoke(null, new object[] { rect }); } catch { }
        }

        // 1.0.223: the panel's header as Core's live drag handle - the name header (character / companion), the CRAFTING title, or (the
        // grid has no header) a thin transparent strip just ABOVE the grid's top edge. Never the whole rect: slots and buttons keep their clicks.
        internal static RectTransform LiveHandle(string id, RectTransform panel)
        {
            if (panel == null || id == "vainv.info") return null;
            string want = id == "vainv.crafting" ? "VAInventoryPanelsTitle" : (id == "vainv.character" || id == "vainv.companion") ? "VAInventoryPanelsHeader" : null;
            if (want != null)
                foreach (var t in panel.GetComponentsInChildren<RectTransform>(true))
                    if (t.name == want)
                    {
                        var g = t.GetComponent<UnityEngine.UI.Graphic>(); if (g != null) g.raycastTarget = true;
                        EquipmentPanel.PanelLog?.LogDebug($"panels: {id} live drag handle = its '{want}'");
                        return t;
                    }
            const string StripName = "VAInventoryLiveHandle_NoKitSkin";
            var strip = panel.Find(StripName) as RectTransform;
            if (strip == null)
            {
                strip = (RectTransform)new GameObject(StripName, typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.LayoutElement)).transform;
                strip.SetParent(panel, false);
                strip.GetComponent<UnityEngine.UI.LayoutElement>().ignoreLayout = true;
                strip.anchorMin = new Vector2(0f, 1f); strip.anchorMax = new Vector2(1f, 1f); strip.pivot = new Vector2(0.5f, 0f);
                strip.sizeDelta = new Vector2(0f, 18f); strip.anchoredPosition = new Vector2(0f, 2f);   // above the top edge
                var img = strip.GetComponent<UnityEngine.UI.Image>(); img.color = new Color(0f, 0f, 0f, 0f); img.raycastTarget = true;
            }
            EquipmentPanel.PanelLog?.LogDebug($"panels: {id} live drag handle = an 18 px strip above its top edge (no header)");
            return strip;
        }

        static void Set(object o, string field, object value)
        {
            var f = s_options.GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) { f.SetValue(o, value); return; }
            s_options.GetProperty(field, BindingFlags.Public | BindingFlags.Instance)?.SetValue(o, value);
        }
    }
}
