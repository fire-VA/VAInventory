using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// The "Panels" layout. Columns, left to
    /// right, equal width, one top Y, each as tall as its content (capped at the screen minus padding; taller content scrolls in its panel):
    ///   1 carry grid (vanilla Player panel) with the open chest (vanilla Container) below it | 2 character panel | (3) companion | crafting.
    /// The VANILLA panels are positioned, never re-parented or hidden, in LateUpdate after the show animator wrote them, so FATI's
    /// "Crafting" path, every mod reading m_playerGrid / m_containerGrid and vanilla drag all keep working. The whole row takes one
    /// uniform user scale from the grip on crafting's right edge, saved per resolution in [UI] PanelsScale.
    /// </summary>
    internal sealed class PanelsLayout : MonoBehaviour
    {
        // Reference px at 1080 canvas height; the canvas scaler scales them (kit 8-px grid).
        const float Outer = 24f, Gap = 16f, Inner = 12f, MinColumn = 360f;
        const string CompanionName = "VAInventoryCompanionColumn", GripName = "VAInventoryPanelsGrip";

        static PanelsLayout s_instance;
        internal static bool CompanionColumnShown { get; private set; }

        RectTransform root;
        RectTransform companion; VAInventoryGridApi.GridHandle companionGrid; TMP_Text companionTitle; object companionSource;
        string lastLog;

        internal static void Attach(RectTransform inventoryRoot)
        {
            if (inventoryRoot == null || s_instance != null) return;
            s_instance = inventoryRoot.gameObject.AddComponent<PanelsLayout>();
            s_instance.root = inventoryRoot;
        }

        // 
        // a NEW InventoryGui came up (the log re-mounts at line 22229) while the static 'already registered / already built' flags of the
        // OLD one stayed true, so nothing was registered or built for the new screen. Its root going away clears them.
        void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
            CompanionColumnShown = false;
            PanelsDock.ResetForNewGui();
            PanelsChrome.ResetForNewGui();
            EquipmentPanel.PanelLog?.LogDebug("panels: inventory screen destroyed; dock + chrome state cleared for the next one");
        }

        void LateUpdate()
        {
            var gui = InventoryGui.instance;
            bool on = UnifiedScreen.PanelsMode && gui != null && InventoryGui.IsVisible() && root != null;
            try { PanelsChrome.Tick(gui, UnifiedScreen.CharacterPanel); } catch (Exception e) { LogOnce("panels: chrome tick failed: " + e.Message); }   // armour/weight, header, repair, info row, trash (Panels only; restores when left)
            VanillaStatTabs(gui, on);   // 1.0.170
            PanelsGridLook.Placeholders(on);   // Grey slot names in empty equipment cells (Panels only)
            if (on && !PanelsDock.Registered) SeedHide(true);   // The first-open seeding hide BEFORE the crafting header check (it ran first and measured a frame DockTick hid later)
            try { PanelsCraftShift.Tick(gui, on); } catch (Exception e) { LogOnce("panels: crafting shift failed: " + e.Message); }   // 1.0.173
            if (!on) { if (CompanionColumnShown) HideCompanion(); skinnedThisOpen = false; watchOpen = false; borderWasOn = false; return; }
            BorderPixelFloor(gui);   // 1.0.264
            if (!watchOpen) { watchOpen = true; UnifiedScreen.WatchOpen(); }   // The Misc cell 0 writer proof, per open
            UnifiedScreen.WatchTick();
            Slots.RegisterCoreCatalog();   // Once, the first open the slots exist (Core 0.2.563 FiresSlotCatalog)
            // Core's skin styles what exists when ApplyTo runs; vanilla rebuilds grid elements between opens. Re-run it once on
            // EVERY open (idempotent; Core 0.2.558 Role.Slot), after registration, so the first and every later open look the same.
            // On the first open vanilla can build the Misc / bag cells AFTER this ran, so ALSO re-run whenever the character panel or a grid is (re)built,
            // i.e. its hierarchy count or a grid's cell count changes. Core's ApplyTo is idempotent (one reused KitSlotOutline).
            var cp = UnifiedScreen.CharacterPanel;
            int sig = (cp != null ? cp.hierarchyCount : 0) * 31 + (gui.m_playerGrid != null && gui.m_playerGrid.m_gridRoot != null ? gui.m_playerGrid.m_gridRoot.childCount : 0) * 7
                      + (gui.m_containerGrid != null && gui.m_containerGrid.m_gridRoot != null ? gui.m_containerGrid.m_gridRoot.childCount : 0);
            // EquipmentPanel re-creates the panel's elements from the vanilla prefab
            // (grey tint) with the SAME count, so the hierarchy signature did not change and Core's skin never reached them. Every element
            // creation now sets SkinDirty, and the skin re-runs that frame.
            if (SkinDirty && PanelsDock.Registered) { SkinDirty = false; skinnedThisOpen = false; EquipmentPanel.PanelLog?.LogDebug("panels: panel elements created; Core skin re-applied"); reapplyA = Time.frameCount + 2; reapplyB = Time.frameCount + 15; if (dumpsLeft > 0) dumpAfterRecreate = true; }
            // For 1 s after EquipmentPanel
            // re-creates the panel elements the slot look is laid on the character panel EVERY frame (a late writer loses), and the empty-rail
            // stack is dumped once, right after the re-create
            // 1.0.227 laid the look on EVERY frame for 1 s after each re-create, and
            // EquipmentPanel re-creates often, so it ran nearly always; now exactly twice per re-create burst (2 and 15 frames later)
            if ((Time.frameCount == reapplyA || Time.frameCount == reapplyB) && UnifiedScreen.CharacterPanel != null) UnifiedScreen.ForceSlotLook(UnifiedScreen.CharacterPanel);   // Forced (same count, new objects)
            if (dumpAfterRecreate && Time.frameCount > reapplyB) { dumpAfterRecreate = false; dumpsLeft--; UnifiedScreen.DumpEmptyRails("after an element re-create"); }   // at most 3 per session
            if (skinnedThisOpen && sig != skinSignature && PanelsDock.Registered)
            {
                skinnedThisOpen = false;
                EquipmentPanel.PanelLog?.LogDebug($"panels: inventory rebuilt (signature {skinSignature} -> {sig}); Core skin re-applied");
            }
            if (!skinnedThisOpen && PanelsDock.Registered)
            {
                skinnedThisOpen = true;
                skinSignature = sig;
                int faces = UnifiedScreen.SyncEmptyCellFaces();   // Empty Misc / bag cells keep a face on the FIRST open
                if (faces > 0) EquipmentPanel.PanelLog?.LogDebug($"panels: {faces} empty-cell face(s) switched (on while a cell holds no element)");
                foreach (var r in new[] { gui.m_player, gui.m_container, UnifiedScreen.CharacterPanel, gui.m_crafting, CompanionPanel.Current?.Root }) PanelsDock.Skin(r);   // plates; the companion copy's root takes the same skin; m_info is hidden under Panels; every slot face carries KitLeaf
                // The companion panel is NOT re-skinned: it is a clone of the already-skinned player panel, and Core's kit skin repainted its own plate / grid / Stats page
                int frames = UnifiedScreen.StyleCellFramesLikeGrid();   // Every Cell_ frame face = the main grid's Core slot look
                if (frames > 0) EquipmentPanel.PanelLog?.LogDebug($"panels: {frames} Cell_ frame face(s) styled like the main grid's slots");
                UnifiedScreen.StyleElementsLikeGrid(true);   // The certain copy + both rendered colours logged
                ApplyGridLooks(gui);   // 1.0.208
                nextElementCopy = Time.unscaledTime + 0.5f;
            }
            else if (Time.unscaledTime >= nextElementCopy)
            {
                nextElementCopy = Time.unscaledTime + 0.5f;
                if (UnifiedScreen.StyleElementsLikeGrid(false) > 0) UnifiedScreen.StyleElementsLikeGrid(true);   // Elements re-created later (open #3)
                ApplyGridLooks(gui);   // Main grid + chest + hotbars, the pouch look (re-created elements too)
            }
            companionPanel.Tick();   // The companion panel's rails + stats, twice a second while shown
            if (CompanionCloseFallback(gui)) return;
            PanelsGridLook.Tick(gui);   // The v4 mock's grid wells
            // With Core's FiresDock every panel is a dock element (PanelsDock); this only seeds the default row, then Core owns placement
            if (PanelsDock.Available) { try { DockTick(gui); } catch (Exception e) { LogOnce("panels: dock tick failed: " + e.Message); } return; }
            try { Layout(gui); }
            catch (Exception e) { LogOnce("panels: layout failed: " + e.Message); }
        }

        void Layout(InventoryGui gui)
        {
            var player = gui.m_player; var container = gui.m_container; var crafting = gui.m_crafting;
            var character = UnifiedScreen.CharacterPanel;
            if (player == null || crafting == null) return;

            // With the Fires UI override OFF VAInventory does not take over the companion screen - no companion column, so
            // VAInventoryApi.OwnsCompanionColumn stays false and FiresCompanions shows its own original stats / inventory screen
            var src = FiresCore.UI.Shell.FiresShell.OverrideOn ? CompanionSource() : null;
            if (src != null) EnsureCompanion(src); else if (CompanionColumnShown) HideCompanion();

            var columns = new List<RectTransform> { player, character };
            if (CompanionColumnShown && companion != null) columns.Add(companion);
            columns.Add(crafting);
            columns.RemoveAll(c => c == null);
            int n = columns.Count;

            // Each column is measured by what it DRAWS (its visible graphics, clipped by masks: side tabs and overhangs included), never by its
            // rect. The shared width is the narrowest column's native width, capped to the screen: no column is ever scaled above 1 (a
            // scaled-up panel runs over the grid and past the screen, and its hit tests and cells break).
            float user = UserScale();
            Rect canvas = root.rect;
            // A column is at most GoldenPanelHeight of the canvas by default (shrunk to fit, never stretched toward full screen)
            float maxHeight = Mathf.Min(canvas.height - 2f * Outer, canvas.height * GoldenPanelHeight);
            var m = new List<Measured>();
            foreach (var col in columns) m.Add(Measure(col, col == player ? character : null, col == crafting ? GripName : null));
            float narrowest = float.MaxValue; foreach (var mm in m) narrowest = Mathf.Min(narrowest, mm.Size.x);
            float fit = (canvas.width - 2f * Outer - (n - 1) * Gap * user) / n;
            float width = Mathf.Min(narrowest * user, fit);
            // Crafting keeps its own drawn
            // width at scale 1 (capped by the screen fit); the other columns share the narrowest width
            float ColW(int i) => columns[i] == crafting ? Mathf.Min(m[i].Size.x * user, fit) : width;
            float total = (n - 1) * Gap * user; for (int i = 0; i < n; i++) total += ColW(i);
            float left = canvas.xMin + Mathf.Max(Outer, (canvas.width - total) * 0.5f);
            float top = canvas.yMax - Outer;

            string over = null, sizes = null;
            float x = left;
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i]; var mm = m[i];
                float colW = ColW(i);
                float s = colW / Mathf.Max(1f, mm.Size.x);
                float colH = mm.Size.y * s;
                Measured cm = default; float cs = 0f; bool chest = col == player && container != null && container.gameObject.activeInHierarchy;
                if (chest) { cm = Measure(container, null, null); cs = colW / Mathf.Max(1f, cm.Size.x); colH += Inner * user + cm.Size.y * cs; }
                if (colH > maxHeight + 0.5f && !HasUserScale())   // The cap only picks the default; a player-set size wins
                {
                    float k = maxHeight / colH;   // too tall: the whole column shrinks to fit (narrower than the others, logged)
                    // 
                    // the CHARACTER panel never shrinks below MinCharacterScale; still too tall = it caps + its bag area scrolls (RequestScreenFit)
                    if (col == character && s * k < MinCharacterScale) k = Mathf.Min(k < 1f ? 1f : k, Mathf.Max(k, MinCharacterScale / Mathf.Max(1e-5f, s)));
                    s *= k; cs *= k; over = (over == null ? "" : over + ", ") + $"{col.name} {colH:0} > {maxHeight:0} (shrunk {k:0.00})";
                }
                PlaceDrawn(col, mm, s, new Vector2(x, top));
                if (chest) PlaceDrawn(container, cm, cs, new Vector2(x, top - mm.Size.y * s - Inner * user));
                sizes = (sizes == null ? "" : sizes + ", ") + $"{col.name} {mm.Size.x:0}x{mm.Size.y:0}@{s:0.00}";
                x += colW + Gap * user;
            }            if (!PanelsDock.Available) EnsureGrip(crafting);   // With FiresDock the grips are Core's
            LogOnce($"panels: {n} column(s) {width:0} wide (scale {user:0.00}) on a {canvas.width:0}x{canvas.height:0} canvas; drawn native {sizes}" +
                    (CompanionColumnShown ? "; companion column shown" : "") + (over != null ? $"; over max height: {over}" : ""));
        }

        // What a panel DRAWS, at scale 1 relative to the root: its visible graphics' bounds (each clipped by its nearest mask), as a size and
        // as an offset of that bounds' top-left from the rect's own top-left. Cached per panel for 0.5 s (a hierarchy walk).
        struct Measured { public Vector2 Size, Offset; }
        readonly Dictionary<RectTransform, (float at, int count, Measured m)> measured = new Dictionary<RectTransform, (float, int, Measured)>();
        static readonly Vector3[] s_c = new Vector3[4];

        Measured Measure(RectTransform rt, RectTransform exclude, string excludeName)
        {
            int count = rt.hierarchyCount;
            if (measured.TryGetValue(rt, out var hit) && hit.count == count && Time.unscaledTime - hit.at < 0.5f) return hit.m;
            float rel = Mathf.Max(1e-5f, rt.lossyScale.x / Mathf.Max(1e-5f, root.lossyScale.x));
            rt.GetWorldCorners(s_c); Vector2 rtTopLeft = root.InverseTransformPoint(s_c[1]);
            bool any = false; float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            foreach (var g in rt.GetComponentsInChildren<Graphic>(false))
            {
                if (!g.enabled || g.color.a < 0.02f || g.canvasRenderer.cull || g.canvasRenderer.GetInheritedAlpha() < 0.02f) continue;   // CanvasGroup-hidden (the moved armour/weight tabs) too
                if (exclude != null && g.transform.IsChildOf(exclude)) continue;
                if (excludeName != null && g.name == excludeName) continue;
                var r = RootRect((RectTransform)g.transform);
                var mask = g.GetComponentInParent<RectMask2D>();
                if (mask != null && mask.transform.IsChildOf(rt)) { var mr = RootRect(mask.rectTransform); r = Rect.MinMaxRect(Mathf.Max(r.xMin, mr.xMin), Mathf.Max(r.yMin, mr.yMin), Mathf.Min(r.xMax, mr.xMax), Mathf.Min(r.yMax, mr.yMax)); }
                if (r.width <= 0.5f || r.height <= 0.5f) continue;
                if (!any) { x0 = r.xMin; y0 = r.yMin; x1 = r.xMax; y1 = r.yMax; any = true; }
                else { x0 = Mathf.Min(x0, r.xMin); y0 = Mathf.Min(y0, r.yMin); x1 = Mathf.Max(x1, r.xMax); y1 = Mathf.Max(y1, r.yMax); }
            }
            Measured m;
            if (!any) { m = new Measured { Size = rt.rect.size, Offset = Vector2.zero }; }
            else m = new Measured { Size = new Vector2(x1 - x0, y1 - y0) / rel, Offset = (new Vector2(x0, y1) - rtTopLeft) / rel };
            measured[rt] = (Time.unscaledTime, count, m);
            return m;
        }

        Rect RootRect(RectTransform r)
        {
            r.GetWorldCorners(s_c);
            Vector2 a = root.InverseTransformPoint(s_c[0]), b = root.InverseTransformPoint(s_c[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        // Puts what rt DRAWS with its top-left at `topLeft`, at scale `s` relative to the root.
        void PlaceDrawn(RectTransform rt, Measured m, float s, Vector2 topLeft) => Place(rt, s, topLeft - m.Offset * s);

        // The first two frames lay out 1.0.159's row (the default spots), then every panel registers with FiresDock and Core
        // places them from then on. The companion column registers
        // the first time one opens.
        int dockSeedFrames;
        bool companionDocked;
        bool companionWasShown;   // 1.0.240
        Vector2 lastPlayerSize; float lastPlayerLossy;   // 1.0.246
        int lastDockTickFrame = -10;   // 1.0.242
        int s_seedUnhideFrame;   // 1.0.243
        CanvasGroup s_seedGroup; bool s_seedAdded, s_seedHidden; float s_seedHideUntil;
        void SeedHide(bool hide)
        {
            if (root == null || hide == s_seedHidden) return;
            if (hide)
            {
                s_seedGroup = root.GetComponent<CanvasGroup>(); s_seedAdded = s_seedGroup == null;
                if (s_seedAdded) s_seedGroup = root.gameObject.AddComponent<CanvasGroup>();
                s_seedGroup.alpha = 0f; s_seedGroup.blocksRaycasts = false; s_seedHidden = true; s_seedHideUntil = Time.realtimeSinceStartup + 2f;
                EquipmentPanel.PanelLog?.LogDebug("panels: first-open layout seeding: panels hidden until laid out + placed");
            }
            else
            {
                if (s_seedGroup != null) { if (s_seedAdded) UnityEngine.Object.Destroy(s_seedGroup); else { s_seedGroup.alpha = 1f; s_seedGroup.blocksRaycasts = true; } }
                s_seedGroup = null; s_seedHidden = false;
                EquipmentPanel.PanelLog?.LogDebug("panels: first-open layout done: panels shown");
            }
        }
        const float GoldenPanelHeight = 0.80f;   // 1.0.242
        const float MinCharacterScale = 0.80f;   // 1.0.264

        // Every slot cell's Outline (Core's
        // ApplySlotLook gives each face one 1.5-unit gold Outline) is kept at >= 1 WHOLE screen pixel. The Outline's distance is in the
        // graphic's local units; its on-screen size = distance x the graphic's lossy scale (Screen Space Overlay: world unit = 1 px).
        // So distance = max(base, 1 px / lossy). The base (Core's value) is remembered per Outline; checked twice a second while open.
        readonly Dictionary<Outline, Vector2> borderBase = new Dictionary<Outline, Vector2>();
        // NO timer - runs on each inventory open and when the screen size or a panel's
        // on-screen scale actually changes (a few float compares per frame, nothing else)
        string borderLog; bool borderWasOn; readonly float[] bs = new float[6];   // the last screen size + panel lossy scales
        void BorderPixelFloor(InventoryGui gui)
        {
            var ch = UnifiedScreen.CharacterPanel;
            float s0 = Screen.width, s1 = Screen.height, s2 = gui.m_player != null ? gui.m_player.lossyScale.x : 0f, s3 = ch != null ? ch.lossyScale.x : 0f,
                s4 = gui.m_container != null && gui.m_container.gameObject.activeInHierarchy ? gui.m_container.lossyScale.x : 0f, s5 = CompanionColumnShown && companion != null ? companion.lossyScale.x : 0f;
            bool opened = !borderWasOn; borderWasOn = true;
            bool same = Mathf.Approximately(s0, bs[0]) && Mathf.Approximately(s1, bs[1]) && Mathf.Abs(s2 - bs[2]) < 1e-5f && Mathf.Abs(s3 - bs[3]) < 1e-5f && Mathf.Abs(s4 - bs[4]) < 1e-5f && Mathf.Abs(s5 - bs[5]) < 1e-5f;
            if (!opened && same) return;
            bs[0] = s0; bs[1] = s1; bs[2] = s2; bs[3] = s3; bs[4] = s4; bs[5] = s5;
            int fixedN = 0, seen = 0; float minPx = float.MaxValue;
            foreach (var r in new RectTransform[] { gui.m_player, gui.m_container, UnifiedScreen.CharacterPanel, companion })
            {
                if (r == null || !r.gameObject.activeInHierarchy) continue;
                foreach (var ol in r.GetComponentsInChildren<Outline>(false))
                {
                    if (ol == null) continue;
                    if (!borderBase.TryGetValue(ol, out var b))
                    {
                        b = ol.effectDistance;
                        if (Mathf.Abs(b.x) < 0.5f || Mathf.Abs(b.x) > 3f) continue;   // only the slot-look border family (Core 1.5)
                        borderBase[ol] = b;
                    }
                    seen++;
                    float lossy = Mathf.Max(1e-4f, Mathf.Abs(ol.transform.lossyScale.x));
                    float need = Mathf.Max(Mathf.Abs(b.x), 1f / lossy);
                    minPx = Mathf.Min(minPx, Mathf.Abs(b.x) * lossy);
                    var want = new Vector2(Mathf.Sign(b.x == 0 ? 1 : b.x) * need, Mathf.Sign(b.y == 0 ? -1 : b.y) * need);
                    if ((ol.effectDistance - want).sqrMagnitude > 1e-4f) { ol.effectDistance = want; fixedN++; }
                }
            }
            string log = $"panels: cell border pixel floor: {seen} border(s), smallest Core border {(seen > 0 ? minPx.ToString("0.00") : "-")} px on screen, {fixedN} raised to >= 1 px";
            if (fixedN > 0 || log != borderLog) { if (fixedN > 0 || borderLog == null) EquipmentPanel.PanelLog?.LogDebug(log); borderLog = log; }
        }
        RectTransform chestDocked;   // The Container rect lifted + registered (a new InventoryGui = a new one)
        bool skinnedThisOpen;   // 1.0.184
        int skinSignature;   // 1.0.186
        internal static bool SkinDirty;   // Set by EquipmentPanel when it instantiates panel elements
        float nextFitCheck;   // 1.0.171
        float nextElementCopy;   // 1.0.197
        int reapplyA, reapplyB, dumpsLeft = 3; bool dumpAfterRecreate;   // 1.0.227/228
        bool watchOpen;   // 1.0.210

        void DockTick(InventoryGui gui)
        {
            // With the Fires UI override OFF VAInventory does not take over the companion screen - no companion column, so
            // VAInventoryApi.OwnsCompanionColumn stays false and FiresCompanions shows its own original stats / inventory screen
            var src = FiresCore.UI.Shell.FiresShell.OverrideOn ? CompanionSource() : null;
            if (src != null) EnsureCompanion(src); else if (CompanionColumnShown) HideCompanion();
            var character = UnifiedScreen.CharacterPanel;
            // The panels are
            // invisible (root CanvasGroup alpha 0, no raycasts) while the first layout seeds and Core places them; shown 2 frames after register
            if ((s_seedUnhideFrame > 0 && Time.frameCount >= s_seedUnhideFrame) || (s_seedHidden && Time.realtimeSinceStartup > s_seedHideUntil)) { SeedHide(false); s_seedUnhideFrame = 0; }   // safety: never hidden past 2 s
            if (!PanelsDock.Registered)
            {
                SeedHide(true);
                PanelsDock.DetachCharacter(character, root);
                Layout(gui);
                if (++dockSeedFrames < 3) return;
                PanelsDock.MigrateCraftingRightOnce();   // Before Core loads the saved layout
                PanelsDock.MigrateDropSavedOnce("companion_panel_1198", "vainv.companion");   // an older column / panel saved spot goes once
                PanelsDock.MigrateDropSavedOnce("character_own_group_1218", "vainv.character");
                PanelsDock.MigrateDropSavedOnce("companion_own_element_1238", "vainv.companion");   // the older "docked right of character" / "docked to the grid" spots go once
                PanelsDock.Register("vainv.grid", "Inventory", gui.m_player, null, null, null);
                // NOT a dock element. Vanilla's Container is a CHILD of the Player panel (vainv.grid), so Core moving the grid moved it
                // and then Core placed it again: the offsets compounded. It rides the grid until
                // it can be lifted out like the character panel.
                // Docked to vainv.grid, it was
                // part of the grid's group, and Core's grip scales a docked element's GROUP. Now its own element: its grip scales it; its default
                // spot is where the seeded row put it (right of the grid)
                // The player panel DOCKS to the RIGHT of the grid through Core's dock (gentle magnet, Shift =
                // free placement, a drag past the magnet undocks, a docked group's corner grip scales the group, undocked = its own grip).
                // 1.0.218's standalone registration is replaced; the old saved spot goes once so the dock default applies
                PanelsDock.MigrateDropSavedOnce("character_docked_grid_1241", "vainv.character");
                PanelsDock.MigrateDropSavedOnce("companion_docked_character_1241", "vainv.companion");
                PanelsDock.MigrateDropSavedOnce("character_compact_1242", "vainv.character");   // an older (near full screen) saved size goes once
                PanelsDock.MigrateDropSavedOnce("companion_compact_1242", "vainv.companion");   // before Core loads the saved layout
                // Those two register FREE (no dock-to);
                // the player panel was docked to vainv.grid (Right), and a docked member has no corner grip of its own (Core scales the group).
                // Now the identical call as vainv.grid (reused from this file, the vainv.grid Register above); the old docked saved spot goes
                // once so Core does not restore the dock anchor. Seeded default = the row spot beside the grid (not flush to the screen bottom).
                PanelsDock.MigrateDropSavedOnce("character_free_1273", "vainv.character");
                PanelsDock.Register("vainv.character", "Character", character, null, null, null);
                // 
                // crafting was docked to the Bottom of vainv.info, which Panels hides (its buttons live on crafting), so Core anchored crafting
                // THROUGH the hidden info panel - keeping the Bottom edge - onto the next shown panel. With the info buttons moved, the info
                // panel isn't a dock element at all and crafting docks to the RIGHT of the character panel: one row, as Fire's mock.
                if (PanelsChrome.InfoMoved)
                    PanelsDock.Register("vainv.crafting", "Crafting", gui.m_crafting, null, null, null, false, () => PanelsDock.CraftingRightDefault(gui.m_crafting));   // flush right, centred, NOT docked to the character panel
                else
                {
                    PanelsDock.Register("vainv.info", "Player info", gui.m_info, "vainv.character", "Right", null);
                    PanelsDock.Register("vainv.crafting", "Crafting", gui.m_crafting, null, null, null, false, () => PanelsDock.CraftingRightDefault(gui.m_crafting));   // 1.0.178
                }
                PanelsDock.RegisterGridRow1(gui.m_playerGrid);
                // The carry grid and chest panels keep their ORIGINAL look
                // (vanilla's wood Bkg + item_background cells, as with the unified UI off), so Core's kit skin is NOT applied to them; it painted
                // the grid's plate near-black ("[CraftSkin] Player: kit look (1888 graphic(s))"), on which vanilla's translucent cells vanish.
                // ("put all of the panels back to the colors they were before the unified stuff and only change
                // the style of the crafting panel"): Core's kit skin goes on the CRAFTING panel only; grid, chest, character panel and info keep
                // their own pre-unified look.
                // Core's CraftingSkin styles the grid, chest, character panel, info and
                // crafting again; Core 0.2.558 gives every slot face its own Role.Slot (lighter face + faint outline) instead of the flat Raised
                // pill that made the squares vanish. VAInventory does no slot styling of its own any more.
                foreach (var r in new[] { gui.m_player, gui.m_container, character, gui.m_crafting, CompanionPanel.Current?.Root }) PanelsDock.Skin(r);   // m_info is hidden under Panels
                UnifiedScreen.RemoveResizeGrip("registered with Core's dock");   // Core's grip only
                s_seedUnhideFrame = Time.frameCount + 2;
                LogOnce($"panels: registered with Core FiresDock (grid, character, {(PanelsChrome.InfoMoved ? "crafting at the right screen edge" : "info; crafting at the right screen edge")}); Core places them from here");
            }
            // The player panel's bottom off the screen (Core placed it after the open-time fit) = re-fit, so it caps + scrolls inside
            if (character != null && Time.unscaledTime >= nextFitCheck)
            {
                nextFitCheck = Time.unscaledTime + 0.5f;
                var c = new Vector3[4]; character.GetWorldCorners(c);
                if (root.InverseTransformPoint(c[0]).y < root.rect.yMin + 8f) UnifiedScreen.RequestScreenFit();
                // 
                // the player and companion panels' bottom stays >= BottomInsetPx above the screen edge, whatever a saved layout or an
                // edge snap says (only moved up, only while no mouse button is held, never over the top edge). Position only, no scale.
                // With Core's dock the inset is Core's placement (Core re-places a live dock element from its stored spot every frame, undoing
                // any lift); this lift is for the no-Core layout only
                if (!PanelsDock.Available && !Input.GetMouseButton(0) && !Input.GetMouseButton(1))
                {
                    LiftOffBottom(character, "vainv.character");
                    if (CompanionColumnShown && companion != null) LiftOffBottom(companion, "vainv.companion");
                }
            }
            // The companion is seated next to the player panel ONCE (its default spot), then registered as its OWN element (no dock-to)
            // and never re-aligned, so it doesn't scale together with the player panel: its own grip drags / resizes it, saved separately.
            // 
            // the chest (vanilla Container, a CHILD of the Player panel) is lifted off the grid like the character panel (as a
            // child, Core moving the grid moved it too), seated once just below the grid's drawn bottom, and registered as its own live element
            // "vainv.chest": header-strip drag + Core's corner resize, saved separately, shown only while a chest is open. Kit skin + pouch-cell
            // slot faces as before (PanelsDock.Skin / ApplySlotLook). Reused: DetachCharacter's lift, PanelsDock.Register + LiveHandle.
            var chestRt = gui.m_container;
            if (chestDocked != chestRt && chestRt != null && chestRt.gameObject.activeInHierarchy && gui.m_player != null)
            {
                chestDocked = chestRt;
                var g = new Vector3[4]; gui.m_player.GetWorldCorners(g);
                // The grid DRAWS below m_player's rect (its Bkg / grid root
                // reach past it), so the seat measures the lowest drawn bottom of the three, then one Gap below that
                float drawnBottom = g[0].y;
                foreach (var part in new[] { gui.m_player.Find("Bkg") as RectTransform, gui.m_playerGrid != null ? gui.m_playerGrid.m_gridRoot : null })
                    if (part != null && part.gameObject.activeInHierarchy) { var pc = new Vector3[4]; part.GetWorldCorners(pc); drawnBottom = Mathf.Min(drawnBottom, pc[0].y); }
                var k = new Vector3[4]; chestRt.GetWorldCorners(k);
                Vector3 want = new Vector3(g[0].x, drawnBottom, g[0].z) - root.TransformVector(new Vector3(0f, Gap * UserScale(), 0f));   // grid's drawn bottom-left, one Gap below
                chestRt.position += want - k[1];
                chestRt.SetParent(root, true);   // world position and size kept
                // The chest starts at the player grid's own scale (the same cells): keeping its WORLD size on the lift would make the Player
                // panel's scale (and any earlier Core scale on the chest) its base scale, with Core's saved scale on top. A saved chest spot
                // made on such an inflated base goes once, so Core starts it from its default.
                var before = chestRt.localScale;
                chestRt.localScale = gui.m_player.localScale;
                PanelsDock.MigrateDropSavedOnce("chest_scale_1315", "vainv.chest");
                EquipmentPanel.PanelLog?.LogDebug($"panels: chest scale on the lift {before.x:0.###} -> {chestRt.localScale.x:0.###} (the player grid's)");
                PanelsDock.MigrateDropSavedOnce("chest_own_element_1239", "vainv.chest");
                PanelsDock.MigrateDropSavedOnce("chest_seat_1280", "vainv.chest");   // a spot saved while an older hold fought every drag goes once
                PanelsDock.Register("vainv.chest", "Chest", chestRt, null, null, () => gui.m_container != null && gui.m_container.gameObject.activeInHierarchy);
                PanelsDock.Skin(chestRt);
                EquipmentPanel.PanelLog?.LogDebug("panels: chest lifted off the grid, seated below it, registered as vainv.chest (drag + resize, saved separately)");
            }
            // One line every time the companion panel shows, as its own element
            bool compShown = CompanionColumnShown && companion != null;
            // DockTick only runs while the inventory is open, so a new inventory open (no tick last frame) counts as "not shown before"
            if (Time.frameCount - lastDockTickFrame > 1) companionWasShown = false;
            lastDockTickFrame = Time.frameCount;
            if (compShown && !companionWasShown) EquipmentPanel.PanelLog?.LogDebug($"panels: companion panel shown as its own element ({companion.rect.width:0}x{companion.rect.height:0}, scale {companion.localScale.x:0.00})");
            // On every open
            // and whenever the player panel's drawn size or scale changes, the companion takes the player's rect size + lossy scale (its
            // dock keeps it on the player's right). Its own grip still works between player changes.
            if (compShown && character != null && companion.parent != null)
            {
                var ps = character.rect.size; float pl = character.lossyScale.x;
                bool playerChanged = (ps - lastPlayerSize).sqrMagnitude > 0.25f || Mathf.Abs(pl - lastPlayerLossy) > 1e-4f;
                if (!companionWasShown || playerChanged)
                {
                    lastPlayerSize = ps; lastPlayerLossy = pl;
                    if (companion.sizeDelta != ps) companion.sizeDelta = ps;
                    float s = pl / Mathf.Max(1e-5f, companion.parent.lossyScale.x);
                    if (Mathf.Abs(companion.localScale.x - s) > 1e-4f) companion.localScale = new Vector3(s, s, 1f);
                    EquipmentPanel.PanelLog?.LogDebug($"panels: companion size = player size {ps.x:0}x{ps.y:0} @{pl:0.00} (companion {companion.rect.width:0}x{companion.rect.height:0} @{companion.lossyScale.x:0.00})");
                }
            }
            companionWasShown = compShown;
            if (CompanionColumnShown && companion != null && !companionDocked)
            {
                AlignCompanion(character);
                companionDocked = true;
                // Seated ONCE at the player panel's size + scale (AlignCompanion, first seat only), then DOCKED to the
                // RIGHT of the player panel through Core's dock (same rules as above); never re-aligned per frame
                PanelsDock.MigrateDropSavedOnce("companion_free_1273", "vainv.companion");   // free like the player panel (seated beside it once by AlignCompanion)
                PanelsDock.Register("vainv.companion", "Companion", companion, null, null, () => CompanionColumnShown);
                EquipmentPanel.PanelLog?.LogDebug("panels: vainv.companion seated at the player panel's size once, docked right of vainv.character (Core dock: magnet, Shift = free, group scale while docked)");
                // No Core skin on the clone (see LateUpdate)
            }
        }

        // The companion panel is the player
        // panel's twin - the same size and scale, top-aligned with it, one Gap to its right - every frame it is shown.
        string lastAlign;
        const float BottomInsetPx = 40f;   // Screen pixels between a panel's bottom and the screen's bottom edge
        void LiftOffBottom(RectTransform rt, string id)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy) return;
            var canvas = rt.GetComponentInParent<Canvas>()?.rootCanvas;
            Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var c = new Vector3[4]; rt.GetWorldCorners(c);
            Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, c[0]), tl = RectTransformUtility.WorldToScreenPoint(cam, c[1]);
            float up = Mathf.Min(BottomInsetPx - bl.y, Screen.height - tl.y);   // never past the top edge
            if (up < 0.5f) return;
            // screen pixels -> the panel's parent space: move a probe point up by `up` screen px
            Vector3 w0 = c[0];
            if (!RectTransformUtility.ScreenPointToWorldPointInRectangle((RectTransform)rt.parent, bl + new Vector2(0f, up), cam, out var w1)) return;
            rt.position += new Vector3(0f, w1.y - w0.y, 0f);
            EquipmentPanel.PanelLog?.LogDebug($"panels: {id} lifted {up:0} px off the screen bottom (bottom was at y {bl.y:0}; inset {BottomInsetPx:0} px so its corner grip is reachable)");
        }

        void AlignCompanion(RectTransform character)
        {
            if (!CompanionColumnShown || companion == null || character == null || companion.parent == null) return;
            float needH = Mathf.Max(character.rect.height, CompanionPanel.Current != null ? CompanionPanel.Current.NeededHeight : 0f);   // The panel grows to hold its lower body
            var wantSize = new Vector2(character.rect.width, needH);
            if (companion.sizeDelta != wantSize) companion.sizeDelta = wantSize;
            float s = character.lossyScale.x / Mathf.Max(1e-5f, companion.parent.lossyScale.x);
            if (Mathf.Abs(companion.localScale.x - s) > 1e-4f) companion.localScale = new Vector3(s, s, 1f);
            var c = new Vector3[4]; character.GetWorldCorners(c);
            var k = new Vector3[4]; companion.GetWorldCorners(k);
            Vector3 want = c[2] + root.TransformVector(new Vector3(Gap * UserScale(), 0f, 0f));
            Vector3 d = want - k[1];
            if (d.sqrMagnitude > 1e-6f) companion.position += d;
            string line = $"panels: companion panel aligned with the player panel (scale {s:0.00}, player {character.rect.width:0}x{character.rect.height:0}, companion set {wantSize.x:0}x{wantSize.y:0} applied {companion.rect.width:0}x{companion.rect.height:0})";
            if (line != lastAlign) { lastAlign = line; EquipmentPanel.PanelLog?.LogDebug(line); }
        }

        // The WHOLE armour / weight
        // tab - the direct child of the Player panel that holds m_armor / m_weight, shield and weight icons included - is hidden under Panels
        // (the armour and weight now read in the grid panel's footer), and shown again when the layout is left.
        readonly System.Collections.Generic.List<CanvasGroup> statTabs = new System.Collections.Generic.List<CanvasGroup>();

        void VanillaStatTabs(InventoryGui gui, bool hide)
        {
            if (gui == null || gui.m_player == null) return;
            if (!hide) { if (statTabs.Count > 0 && !UnifiedScreen.PanelsMode) { foreach (var cg in statTabs) if (cg != null) { cg.alpha = 1f; cg.blocksRaycasts = true; } statTabs.Clear(); } return; }
            if (statTabs.Count > 0) { foreach (var cg in statTabs) if (cg != null && cg.alpha != 0f) cg.alpha = 0f; return; }
            foreach (var text in new Component[] { gui.m_armor, gui.m_weight })
            {
                if (text == null) continue;
                Transform t = text.transform;
                while (t.parent != null && t.parent != gui.m_player) t = t.parent;
                if (t.parent != gui.m_player) continue;
                var cg = t.GetComponent<CanvasGroup>() ?? t.gameObject.AddComponent<CanvasGroup>();
                cg.alpha = 0f; cg.blocksRaycasts = false;
                if (!statTabs.Contains(cg)) statTabs.Add(cg);
                EquipmentPanel.PanelLog?.LogDebug($"panels: vanilla stat tab '{t.name}' hidden (its value reads in the grid panel's footer)");
            }
        }
        // Puts rt's top-left at `topLeft` (root space) at uniform scale `s` relative to the root, whatever its parent / anchors / pivot.
        void Place(RectTransform rt, float s, Vector2 topLeft)
        {
            var parent = rt.parent;
            float parentRel = parent != null ? parent.lossyScale.x / Mathf.Max(1e-5f, root.lossyScale.x) : 1f;
            float local = s / Mathf.Max(1e-5f, parentRel);
            if (Mathf.Abs(rt.localScale.x - local) > 1e-4f) rt.localScale = new Vector3(local, local, 1f);
            var c = new Vector3[4]; rt.GetWorldCorners(c);
            Vector2 nowTopLeft = root.InverseTransformPoint(c[1]);
            Vector2 delta = topLeft - nowTopLeft;
            if (delta.sqrMagnitude > 0.01f) rt.position += root.TransformVector(new Vector3(delta.x, delta.y, 0f));
        }

        // ── user scale, per resolution ──
        static string ResKey => $"{Screen.width}x{Screen.height}";

        static float s_liveScale = -1f;   // during a grip drag; the cfg is written once, at the end

        // True once the player has set a size here (live drag or a saved entry
        // for this resolution). Then the 0.80 floor / height cap that choose the DEFAULT size are not applied over it.
        static bool HasUserScale()
        {
            if (s_liveScale > 0f) return true;
            foreach (var part in (ConfigManager.Instance?.configPanelsScale?.Value ?? "").Split(';')) { var kv = part.Split('='); if (kv.Length == 2 && kv[0].Trim() == ResKey) return true; }
            return false;
        }

        static float UserScale()
        {
            if (s_liveScale > 0f) return s_liveScale;
            string all = ConfigManager.Instance?.configPanelsScale?.Value ?? "";
            foreach (var part in all.Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length == 2 && kv[0].Trim() == ResKey && float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    return Mathf.Clamp(v, 0.5f, 1.5f);
            }
            return 1f;
        }

        static void SaveUserScale(float v)
        {
            var cfg = ConfigManager.Instance?.configPanelsScale;
            if (cfg == null) return;
            var keep = new List<string>();
            foreach (var part in (cfg.Value ?? "").Split(';'))
                if (part.Contains("=") && part.Split('=')[0].Trim() != ResKey) keep.Add(part.Trim());
            if (!Mathf.Approximately(v, 1f)) keep.Add(ResKey + "=" + v.ToString("0.00", CultureInfo.InvariantCulture));
            cfg.Value = string.Join(";", keep.ToArray());
            EquipmentPanel.PanelLog?.LogDebug($"panels: scale {v:0.00} saved for {ResKey}");
        }

        void EnsureGrip(RectTransform crafting)
        {
            if (crafting.Find(GripName) != null) return;
            var go = new GameObject(GripName, typeof(RectTransform), typeof(Image), typeof(PanelsGrip));
            var rt = (RectTransform)go.transform;
            rt.SetParent(crafting, false);
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f); rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(14f, 96f); rt.anchoredPosition = new Vector2(2f, 0f);
            var img = go.GetComponent<Image>(); img.color = PanelsGrip.Hidden;   // Shown on hover only; still hit-testable
            img.raycastTarget = true;
            go.GetComponent<PanelsGrip>().Layout = this;
        }

        // Dragging the grip scales the whole row: the scale follows the pointer's distance from the row's top-left corner.
        internal sealed class PanelsGrip : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
        {
            internal static readonly Color Hidden = new Color(0.85f, 0.70f, 0.40f, 0f), Shown = new Color(0.85f, 0.70f, 0.40f, 0.55f);
            internal PanelsLayout Layout;
            bool hover, dragging;
            void Show() { var i = GetComponent<Image>(); if (i != null) i.color = hover || dragging ? Shown : Hidden; }
            public void OnPointerEnter(PointerEventData e) { hover = true; Show(); }
            public void OnPointerExit(PointerEventData e) { hover = false; Show(); }
            float startScale, startDist; float live = -1f;
            Vector2 Corner() { var r = Layout.root.rect; return new Vector2(r.xMin + Outer, r.yMax - Outer); }
            float Dist(PointerEventData e)
            {
                RectTransformUtility.ScreenPointToLocalPointInRectangle(Layout.root, e.position, e.pressEventCamera, out Vector2 p);
                return Mathf.Max(1f, (p - Corner()).magnitude);
            }
            public void OnBeginDrag(PointerEventData e) { startScale = UserScale(); startDist = Dist(e); dragging = true; Show(); }
            public void OnDrag(PointerEventData e)
            {
                live = Mathf.Clamp(startScale * Dist(e) / startDist, 0.5f, 1.5f);
                s_liveScale = live;
            }
            public void OnEndDrag(PointerEventData e) { s_liveScale = -1f; if (live > 0f) SaveUserScale(live); live = -1f; dragging = false; Show(); }
            public void OnPointerClick(PointerEventData e) { if (e.clickCount == 2) SaveUserScale(1f); }
        }

        // ── companion column ──
        static bool s_apiResolved; static PropertyInfo s_current;

        static object CompanionSource()
        {
            if (!s_apiResolved)
            {
                s_apiResolved = true;
                var api = Type.GetType("VerdantsAscent.Modules.Companions.CompanionPanelApi, FiresCompanions");
                s_current = api?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                EquipmentPanel.PanelLog?.LogDebug(s_current != null ? "panels: FiresCompanions CompanionPanelApi found" : "panels: no CompanionPanelApi (FiresCompanions older than 0.1.56 or absent); no companion column");
                // VAInventory LISTENS to Opened / Closed
                Listen(api, "Opened", nameof(OnCompanionOpened));
                Listen(api, "Closed", nameof(OnCompanionClosed));
            }
            object src = null;
            try { src = s_current?.GetValue(null); } catch { }
            src ??= s_opened;
            if (src == null) return null;
            var isOpen = Get<Func<bool>>(src, "IsOpen");
            try { return isOpen == null || isOpen() ? src : null; } catch { return null; }
        }

        static object s_opened;
        static void OnCompanionOpened(object src) { s_opened = src; EquipmentPanel.PanelLog?.LogDebug("panels: companion panel source received (CompanionPanelApi.Opened)"); }
        static void OnCompanionClosed(object src) { s_opened = null; }
        static void OnCompanionOpened() { }
        static void OnCompanionClosed() { s_opened = null; }

        // subscribes a static handler to a static event whatever its delegate type (one reference-type argument or none)
        static void Listen(Type api, string evName, string handler)
        {
            try
            {
                var ev = api?.GetEvent(evName, BindingFlags.Public | BindingFlags.Static);
                if (ev == null) return;
                int argc = ev.EventHandlerType.GetMethod("Invoke").GetParameters().Length;
                var m = typeof(PanelsLayout).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Static, null, argc == 1 ? new[] { typeof(object) } : Type.EmptyTypes, null);
                if (m == null || argc > 1) return;
                ev.AddEventHandler(null, Delegate.CreateDelegate(ev.EventHandlerType, m));
                EquipmentPanel.PanelLog?.LogDebug($"panels: listening to CompanionPanelApi.{evName}");
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"panels: CompanionPanelApi.{evName} not subscribed: {e.Message}"); }
        }

        static T Get<T>(object o, string name) where T : class
        {
            try { return o?.GetType().GetProperty(name)?.GetValue(o) as T; } catch { return null; }
        }

        // The companion gets its own CHARACTER PANEL (CompanionPanel: the
        // player panel's prefab + steps, its live preview, its worn gear on the rails, its stats, its inventory grid), docked right of the
        // player's panel; crafting stays at the right screen edge. The old title + grid column below is no longer built.
        readonly CompanionPanel companionPanel = new CompanionPanel();

        void EnsureCompanion(object src)
        {
            if (companion == null && companionPanel.Build(root))
            {
                companion = companionPanel.Root;
                VAInventoryGridApi.ItemRightClicked += OnCompanionRightClick;
            }
            if (companion != null && companion == companionPanel.Root)
            {
                companionPanel.Show(src);
                companionSource = src;
                companionGrid = companionPanel.Grid;
                CompanionColumnShown = true;
                return;
            }
            if (companion == null)
            {
                var go = new GameObject(CompanionName, typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
                companion = (RectTransform)go.transform;
                companion.SetParent(root, false);
                companion.anchorMin = companion.anchorMax = companion.pivot = new Vector2(0f, 1f);
                go.GetComponent<Image>().color = UnifiedScreen.PlayerGridUnderlay;
                var v = go.GetComponent<VerticalLayoutGroup>();
                v.padding = new RectOffset(12, 12, 12, 12); v.spacing = 8f; v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandHeight = false;
                go.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                float nativeWidth = UnifiedScreen.CharacterPanel != null ? UnifiedScreen.CharacterPanel.rect.width : 600f;
                companion.sizeDelta = new Vector2(nativeWidth, 400f);

                var title = new GameObject("Title", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
                title.transform.SetParent(companion, false);
                companionTitle = title.GetComponent<TextMeshProUGUI>();
                var font = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponentInChildren<TMP_Text>(true)?.font;
                if (font != null) companionTitle.font = font;
                companionTitle.fontSize = 22f; companionTitle.alignment = TextAlignmentOptions.Center; companionTitle.color = new Color(1f, 0.85f, 0.55f);
                title.GetComponent<LayoutElement>().preferredHeight = 30f;

                var holder = new GameObject("Grid", typeof(RectTransform), typeof(LayoutElement));
                holder.transform.SetParent(companion, false);
                companionGrid = VAInventoryGridApi.MountGrid((RectTransform)holder.transform, new VAInventoryGridApi.GridOptions
                {
                    Owner = "VAInventory.CompanionColumn", VanillaRightClick = false,
                    Inventory = () => Get<Inventory>(companionSource, "Inventory"),
                });
                VAInventoryGridApi.ItemRightClicked += OnCompanionRightClick;
            }
            if (!ReferenceEquals(src, companionSource))
            {
                companionSource = src;
                companionTitle.text = Get<string>(src, "DisplayName") ?? "Companion";
                var inv = Get<Inventory>(src, "Inventory");
                float space = InventoryGui.instance.m_playerGrid.m_elementSpace;
                var le = companion.Find("Grid").GetComponent<LayoutElement>();
                le.preferredHeight = (inv != null ? inv.GetHeight() : 4) * space + 8f;
                EquipmentPanel.PanelLog?.LogDebug($"panels: companion column for '{companionTitle.text}' ({(inv != null ? inv.GetWidth() + "x" + inv.GetHeight() : "no inventory")})");
            }
            companion.gameObject.SetActive(true);
            CompanionColumnShown = true;
        }

        void OnCompanionRightClick(VAInventoryGridApi.GridHandle h, ItemDrop.ItemData item, Vector2i pos)
        {
            if (h != companionGrid || item == null || companionSource == null) return;
            var canWear = Get<Func<ItemDrop.ItemData, bool>>(companionSource, "CanWear");
            var isWorn = Get<Func<ItemDrop.ItemData, bool>>(companionSource, "IsWorn");
            bool worn = isWorn != null && isWorn(item);
            if (!worn && (canWear == null || !canWear(item))) { EquipmentPanel.PanelLog?.LogDebug($"panels: companion can't wear {item.m_shared.m_name}"); return; }
            var act = Get<Func<ItemDrop.ItemData, bool>>(companionSource, worn ? "Unequip" : "Equip");
            bool ok = act != null && act(item);
            EquipmentPanel.PanelLog?.LogDebug($"panels: companion {(worn ? "unequip" : "equip")} {item.m_shared.m_name}: {(ok ? "ok" : "refused")}");
        }

        void HideCompanion()
        {
            companionPanel.Hide();
            if (companion != null) companion.gameObject.SetActive(false);
            companionSource = null;
            CompanionColumnShown = false;
        }

        // The main grid, the chest grid and the companion panel take the Tools pouch cell's look
        void ApplyGridLooks(InventoryGui gui)
        {
            int n = UnifiedScreen.ApplySlotLook(gui.m_playerGrid != null ? gui.m_playerGrid.m_gridRoot : null)
                  + UnifiedScreen.ApplySlotLook(gui.m_containerGrid != null ? gui.m_containerGrid.m_gridRoot : null)
                  + (CompanionColumnShown && companion != null ? UnifiedScreen.ApplySlotLook(companion) : 0)
                  + CraftingSlots(gui);
            if (n > 0) LogOnce($"panels: pouch slot look applied to {n} grid / chest / companion slot(s)");
        }

        // Vanilla's requirement elements (m_recipeRequirementList)
        // and the recipe preview's item slot take the pouch look on their background Image
        static int CraftingSlots(InventoryGui gui)
        {
            if (gui == null || gui.m_recipeRequirementList == null || !UnifiedScreen.PouchLook(out var look)) return 0;
            int n = 0;
            foreach (var req in gui.m_recipeRequirementList)
            {
                if (req == null) continue;
                var bg = req.GetComponent<Image>() ?? req.transform.Find("res_bkg")?.GetComponent<Image>();
                if (bg != null && UnifiedScreen.ApplyLook(bg, look)) n++;
            }
            return n;
        }

        // Vanilla's Update closes the inventory only when Menu / TextViewer / Minimap / chat / console are all clear and the craft timer
        // is idle; something there can stay set while FiresCompanions' (invisible) screen is registered, so the Inventory key may never close it.
        // Fallback, reusing vanilla's own Hide: the inventory still open in LateUpdate after the Inventory / Use / Escape key (vanilla's own
        // close keys) while the companion column shows = close it here, and log which of vanilla's conditions held it.
        int companionShownFrames;
        bool CompanionCloseFallback(InventoryGui gui)
        {
            if (!CompanionColumnShown || gui == null || !InventoryGui.IsVisible()) { companionShownFrames = 0; return false; }
            if (++companionShownFrames <= 3) return false;   // vanilla's m_shownFrames > 1 rule: the key that OPENED it never closes it
            bool key = ZInput.GetButtonDown("Inventory") || ZInput.GetButtonDown("Use") || ZInput.GetKeyDown(KeyCode.Escape)
                       || ZInput.GetButtonDown("JoyButtonB") || ZInput.GetButtonDown("JoyButtonY");
            if (!key) return false;
            if (Chat.instance != null && Chat.instance.HasFocus() || Console.IsVisible() || TextInput.IsVisible()) return false;   // typing: leave it
            string why = $"Menu.IsVisible {Menu.IsVisible()}, TextViewer {(TextViewer.instance != null && TextViewer.instance.IsVisible())}, Minimap {Minimap.IsOpen()}, craftTimer {gui.m_craftTimer:0.00}";
            EquipmentPanel.PanelLog?.LogDebug($"panels: companion inventory: close key seen but vanilla did not close ({why}); closing via InventoryGui.Hide");
            gui.Hide();
            return true;
        }

        void LogOnce(string line)
        {
            if (line == lastLog) return;
            lastLog = line;
            EquipmentPanel.PanelLog?.LogDebug(line);
        }
    }
}
