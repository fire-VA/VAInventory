using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// ("a DIRECT COPY of the player panel's layout, with the ONLY changes being the companion's name instead of
    /// ours, the health/stamina/eitr stats under the preview, and pills to swap to stats"): the companion's panel is a clone of the LIVE
    /// player panel - every section as the player sees it (header, armour / weight, preview, rails incl. the Jewelry face, Tools / Ammo /
    /// Food pouches + Coin purse, Misc, Backpack, Pet, Bypass) - stripped of the player's items and behaviours; only the trash can goes.
    /// Reused, not re-invented:
    ///   - the panel: the player's own CharacterPanel as built by UnifiedScreen / EquipmentPanel / PanelsChrome (cloned), Core's skin;
    ///   - the preview: LivePreview bound to the companion's body;
    ///   - the storage grid: VAInventoryGridApi.MountGrid on FiresCompanions' Storage();
    ///   - the data: FiresCompanions 0.1.59 CompanionPanelSource (Worn, Pouch, Storage, Unequip) and CompanionPanelApi.Stats().
    /// Sections the companion model doesn't have yet (Jewelry, Coin purse, Misc, Backpack, Pet, Bypass) stay as empty identical cells
    /// until Core / FiresCompanions expose them.
    /// </summary>
    internal sealed class CompanionPanel
    {
        const string PanelName = "VAInventoryCompanionPanel";
        // rail order on the player panel (left: armour, right: weapon bars), keyed to FiresCompanions' Worn() slot names
        static readonly string[] LeftKeys = { "Helmet", "Chest", "Legs", "Utility", "Trinket", "Shoulder" };
        static readonly string[] RightKeys = { "RightHand", "LeftHand", "RightBack", "LeftBack" };
        static readonly Dictionary<string, string> CaptionKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Helmet", "Helmet" }, { "Head", "Helmet" }, { "Chest", "Chest" }, { "Legs", "Legs" }, { "Utility", "Utility" }, { "Trinket", "Trinket" },
            { "Shoulder", "Shoulder" }, { "Cape", "Shoulder" }, { "Front bar", "RightHand" }, { "Front offhand", "LeftHand" },
            { "Back bar", "RightBack" }, { "Back offhand", "LeftBack" },
        };

        internal RectTransform Root;
        LivePreview preview;
        VAInventoryGridApi.GridHandle grid;
        TextMeshProUGUI title, stats;
        TMP_Text armorText, weightText;
        Image[] bars;
        RectTransform gridHost, pillRow, statsPage;
        Button equipPill, statsPill;
        bool statsShown;
        readonly List<RailCell> rails = new List<RailCell>();
        readonly List<(string kind, Image icon, TMP_Text badge)> pouchFaces = new List<(string, Image, TMP_Text)>();
        object source;
        Humanoid body;
        float nextRefresh;

        internal sealed class SecCell { public string Section; public int Index; public RectTransform Cell; public Image Icon; public TMP_Text Amount; public ItemDrop.ItemData Item; }
        readonly List<SecCell> secCells = new List<SecCell>();

        internal sealed class RailCell { public RectTransform Cell; public Image Icon; public string Key; public ItemDrop.ItemData Item; }

        internal bool Build(Transform parent)
        {
            Current = this;
            if (Root != null) return true;
            var live = UnifiedScreen.CharacterPanel;
            var sectionMap = UnifiedScreen.SectionCells();   // Before the clone (the player's cell -> slot map)
            if (live != null)
            {
                var go = UnityEngine.Object.Instantiate(live.gameObject, parent, false);
                go.SetActive(false);
                go.name = PanelName;
                Root = (RectTransform)go.transform;
                StripPlayerState(Root);
            }
            else Root = UnifiedScreen.BuildCharacterPanelCopy(parent, PanelName);   // fallback: the prefab copy (1.0.196)
            if (Root == null) { EquipmentPanel.PanelLog?.LogDebug("panels: companion panel REFUSED (no player panel / prefab)"); return false; }
            Root.anchorMin = Root.anchorMax = Root.pivot = new Vector2(0f, 1f);
            // The clone carries the player panel's ContentSizeFitter (it
            // shrank the panel to its preferred size once the bag sections hid) and its LayoutGroup (it sized our own children to 0):
            // the fitter goes off (VAInventory sizes the panel), our children ignore the layout (Free)
            var layoutParts = new List<string>();
            foreach (var f in Root.GetComponents<ContentSizeFitter>()) { f.enabled = false; layoutParts.Add("ContentSizeFitter (off)"); }
            foreach (var g in Root.GetComponents<LayoutGroup>()) layoutParts.Add(g.GetType().Name + " (kept; our children ignore it)");
            EquipmentPanel.PanelLog?.LogDebug($"panels: companion panel layout parts: {(layoutParts.Count > 0 ? string.Join(", ", layoutParts.ToArray()) : "none")}");
            var font = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponentInChildren<TMP_Text>(true)?.font;

            // the player's header (their name) becomes the companion's name
            string playerName = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : null;
            foreach (var t in Root.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (title == null && (t.name.IndexOf("Header", StringComparison.Ordinal) >= 0 || (playerName != null && t.text == playerName))) title = t;
            if (title == null)
            {
                title = Text(Root, "CompanionName", 26f, new Color(0.941f, 0.706f, 0.298f), FontStyles.Bold, font);
                var trt = title.rectTransform; trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f); trt.pivot = new Vector2(0.5f, 1f);
                trt.sizeDelta = new Vector2(-12f, 40f); trt.anchoredPosition = new Vector2(0f, -6f);
                title.alignment = TextAlignmentOptions.Center;
            }
            // The player panel's own vital lines (PanelsChrome "PlayerVital*") must not ride along in this copy
            foreach (var t in Root.GetComponentsInChildren<TMP_Text>(true))
                if (t != null && t.name.StartsWith("PlayerVital", System.StringComparison.Ordinal)) UnityEngine.Object.Destroy(t.gameObject);
            foreach (var t in Root.GetComponentsInChildren<TMP_Text>(true))   // PanelsChrome's footers (VAInventoryPanelsArmor / ...Weight)
            {
                if (t.name == "VAInventoryPanelsArmor") armorText = t;
                else if (t.name == "VAInventoryPanelsWeight") weightText = t;
            }

            var mount = FindDeep(Root, UnifiedScreen.PreviewMount) as RectTransform;
            float mountBottom = mount != null ? mount.anchoredPosition.y - mount.rect.height : -560f;
            if (mount != null)
            {
                // HP / Stamina / Eitr read as labels on the RIGHT of the head, stacked under
                // Weight, in the Armor / Weight label style (clones of the Weight footer)
                vitals = PanelsChrome.MakeVitals(weightText, "CompanionVital");   // the one shared implementation (the player panel uses it too)
            }
            pillMount = mount;
            BuildPills();
            // the Stats page covers the sections under the preview while the Stats pill is on
            statsPage = (RectTransform)new GameObject("CompanionStatsPage_NoKitSkin", typeof(RectTransform), typeof(Image)).transform; NoSkin(statsPage.gameObject);
            statsPage.SetParent(Root, false); Free(statsPage);
            statsPage.anchorMin = statsPage.anchorMax = statsPage.pivot = new Vector2(0f, 1f);   // An explicit rect (LayoutStatsPage)
            // An OPAQUE plate, the panel Bkg's sprite at full alpha
            var spi = statsPage.GetComponent<Image>(); var pbk = Root.Find("Bkg")?.GetComponent<Image>();
            if (pbk != null) { spi.sprite = pbk.sprite; spi.type = pbk.type; spi.material = pbk.material; }
            spi.color = new Color(0.086f, 0.067f, 0.047f, 1f);
            stats = Text(statsPage, "CompanionStats", 17f, new Color(0.92f, 0.86f, 0.72f), FontStyles.Normal, font);
            var srt = stats.rectTransform; srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one; srt.offsetMin = new Vector2(12f, 12f); srt.offsetMax = new Vector2(-12f, -12f);
            stats.alignment = TextAlignmentOptions.TopLeft; stats.enableWordWrapping = false; stats.overflowMode = TextOverflowModes.Truncate;
            statsPage.gameObject.SetActive(false);
            SetPage(false);

            // the companion's storage grid hangs under the panel (the panel keeps the player's height and sections)
            gridHost = (RectTransform)new GameObject("CompanionGrid", typeof(RectTransform)).transform;
            gridHost.SetParent(Root, false); Free(gridHost);
            gridHost.anchorMin = new Vector2(0f, 1f); gridHost.anchorMax = new Vector2(1f, 1f); gridHost.pivot = new Vector2(0.5f, 1f);   // Placed under the bag section each tick
            gridHost.offsetMin = new Vector2(14f, 0f); gridHost.offsetMax = new Vector2(-14f, 0f);
            gridHost.anchoredPosition = new Vector2(0f, -900f);
            invLabel = Text(Root, "CompanionInventoryLabel", 14f, new Color(0.941f, 0.706f, 0.298f), FontStyles.Bold, font);   // "Inventory", like a bag header
            var ilr = invLabel.rectTransform; ilr.anchorMin = new Vector2(0f, 1f); ilr.anchorMax = new Vector2(1f, 1f); ilr.pivot = new Vector2(0f, 1f);
            ilr.offsetMin = new Vector2(16f, 0f); ilr.offsetMax = new Vector2(-16f, 0f); ilr.sizeDelta = new Vector2(ilr.sizeDelta.x, 20f);
            invLabel.text = "Inventory"; invLabel.alignment = TextAlignmentOptions.BottomLeft; invLabel.enableWordWrapping = false;
            BindBagSections();   // After the grid host + label exist (it parents them into the lower body)

            if (live != null) BindSections(sectionMap, font);
            BindRail(UnifiedScreen.LeftRail, LeftKeys);
            BindRail(UnifiedScreen.RightRail, RightKeys);
            BindPouchFaces();
            { var vg = FindDeep(Root, "VAInventoryResizeGrip"); if (vg != null) { vg.gameObject.SetActive(false); UnityEngine.Object.Destroy(vg.gameObject); } }   // the copy never carries VAInventory's old grip (Core's dock grip resizes it)
            PanelsDock.Skin(Root);   // The copy can be made before Core skins the player panel; skin the copy itself
            EquipmentPanel.PanelLog?.LogDebug($"panels: companion panel built as a copy of the {(live != null ? "live player panel" : "prefab")} ({secCells.Count} section cell(s), {rails.Count} rail cell(s), {pouchFaces.Count} pouch face(s), title '{title.name}', armour {(armorText != null)}, weight {(weightText != null)})");
            return true;
        }

        // The clone carries the player's live items and behaviours: every instantiated element, every non-Unity behaviour (drag, tooltip,
        // pouch popups, preview controls, grips) and the trash can go; empty cells show their own face; buttons lose their listeners.
        static void StripPlayerState(RectTransform root)
        {
            foreach (var el in root.GetComponentsInChildren<InventoryElement>(true)) { el.gameObject.SetActive(false); UnityEngine.Object.Destroy(el.gameObject); }
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string ns = mb.GetType().Namespace ?? "";
                if (ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("TMPro", StringComparison.Ordinal)) continue;
                UnityEngine.Object.Destroy(mb);
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.IndexOf("Trash", StringComparison.OrdinalIgnoreCase) >= 0 || t.name.StartsWith("PouchPopup_", StringComparison.Ordinal)) t.gameObject.SetActive(false);
            foreach (var t in root.GetComponentsInChildren<RectTransform>(true))
                if (t.name.StartsWith(UnifiedScreen.CellPrefixName, StringComparison.Ordinal)) { var f = t.GetComponent<Image>(); if (f != null) f.enabled = true; }
            foreach (var b in root.GetComponentsInChildren<Button>(true)) b.onClick = new Button.ButtonClickedEvent();
            foreach (var r in root.GetComponentsInChildren<RawImage>(true)) if (r.name == UnifiedScreen.PreviewMount) r.texture = null;
        }

        TextMeshProUGUI Text(Transform parent, string name, float size, Color color, FontStyles style, TMP_FontAsset font)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            if (parent == Root) Free(go.transform);
            var t = go.GetComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.fontSize = size; t.color = color; t.fontStyle = style; t.raycastTarget = false; t.richText = true;
            return t;
        }

        static readonly Color PillOff = new Color(0.20f, 0.15f, 0.09f, 0.95f), PillOn = new Color(0.55f, 0.42f, 0.20f, 1f);

        Button Pill(string label, TMP_FontAsset font, Action onClick)
        {
            var go = new GameObject("Pill_" + label, typeof(RectTransform), typeof(Image), typeof(Button)); NoSkin(go);
            go.transform.SetParent(pillRow, false);
            var img = go.GetComponent<Image>(); img.color = PillOff;
            var b = go.GetComponent<Button>(); b.targetGraphic = img; b.onClick.AddListener(() => onClick());
            var t = Text(go.transform, "Label", 15f, new Color(0.96f, 0.90f, 0.76f), FontStyles.Bold, font);
            var trt = t.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = trt.offsetMax = Vector2.zero;
            t.text = label; t.alignment = TextAlignmentOptions.Center;
            return b;
        }

        internal static CompanionPanel Current;
        internal void SetPageFromApi(bool statsOn) { EquipmentPanel.PanelLog?.LogDebug($"panels: companion page set by VAInventoryApi.SetCompanionPage ({(statsOn ? "Stats" : "Equipment")})"); SetPage(statsOn); }
        TMP_Text invLabel;

        // Core's kit skin repaints every Graphic under a skinned panel; our own bars, pills
        // and Stats page opt out (KitLeaf + the _NoKitSkin name)
        static Type s_kitLeaf; static bool s_kitLeafLooked;
        static void NoSkin(GameObject go)
        {
            if (!s_kitLeafLooked) { s_kitLeafLooked = true; s_kitLeaf = Type.GetType("FiresCore.UI.Shell.Kit.KitLeaf, FiresUnifiedCore"); }
            if (s_kitLeaf != null && go.GetComponent(s_kitLeaf) == null) go.AddComponent(s_kitLeaf);
        }

        // The page covers the panel
        // body - from under the pills down to the Misc row - at the panel's full width less a margin, the topmost child
        void LayoutStatsPage()
        {
            if (statsPage == null || Root == null) return;
            float top = -48f, bottom = -Root.rect.height * 0.55f;
            if (pillMount != null) { var c = new Vector3[4]; pillMount.GetWorldCorners(c); top = Root.InverseTransformPoint(c[1]).y; }   // The whole body, from the preview's top
            if (miscRow != null && miscRow.gameObject.activeInHierarchy) { var c = new Vector3[4]; miscRow.GetWorldCorners(c); bottom = Root.InverseTransformPoint(c[0]).y; }
            float w = Mathf.Max(200f, Root.rect.width - 24f), h = Mathf.Max(160f, top - bottom);
            statsPage.anchoredPosition = new Vector2(12f, top);
            statsPage.sizeDelta = new Vector2(w, h);
            statsPage.SetAsLastSibling();
            if (pillRow != null) pillRow.SetAsLastSibling();   // The pills stay on top of the page
EquipmentPanel.PanelLog?.LogDebug($"panels: companion Stats page rect {w:0}x{h:0} at y {top:0} (applied {statsPage.rect.width:0}x{statsPage.rect.height:0})");
        }

        void SetPage(bool statsOn)
        {
            statsShown = statsOn;
            if (statsOn) LayoutStatsPage();
            if (statsPage != null) statsPage.gameObject.SetActive(statsOn);
            BuildPills();   // The kit pills redraw with the new active one
            if (statsOn) nextPage = 0f;
            nextRefresh = 0f;
            EquipmentPanel.PanelLog?.LogDebug($"panels: companion page {(statsOn ? "Stats" : "Equipment")}");
        }

        // A rail's cells in order; each cell's caption (when the player's panel has one) names its slot, else the rail's order does.
        void BindRail(string railName, string[] keys)
        {
            var rail = FindDeep(Root, railName);
            if (rail == null) return;
            int i = 0;
            foreach (Transform t in rail)
            {
                if (!t.name.StartsWith(UnifiedScreen.CellPrefixName, StringComparison.Ordinal) || !t.gameObject.activeSelf) continue;
                if (secCells.Exists(s => s.Cell == t)) continue;   // A section cell (Core's grid) already
                string key = null;
                foreach (var cap in t.GetComponentsInChildren<TMP_Text>(true))
                    if (CaptionKeys.TryGetValue((cap.text ?? "").Trim(), out var k)) { key = k; break; }
                if (key == null && i < keys.Length) key = keys[i];
                i++;
                var iconGo = new GameObject("CompanionIcon", typeof(RectTransform), typeof(Image));
                iconGo.transform.SetParent(t, false);
                var irt = (RectTransform)iconGo.transform; irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one; irt.offsetMin = new Vector2(6f, 6f); irt.offsetMax = new Vector2(-6f, -6f);
                var icon = iconGo.GetComponent<Image>(); icon.preserveAspect = true; icon.enabled = false; icon.raycastTarget = false;
                var rc = new RailCell { Cell = (RectTransform)t, Icon = icon, Key = key };
                rails.Add(rc);
                var click = t.gameObject.AddComponent<RailClick>(); click.Owner = this; click.Cell = rc;
            }
        }

        // The player's pouch faces (Pouch_Tools / Pouch_Ammo / Pouch_Food): their icon shows the companion's first item of that pouch.
        void BindPouchFaces()
        {
            foreach (var t in Root.GetComponentsInChildren<RectTransform>(true))
            {
                if (!t.name.StartsWith("Pouch_", StringComparison.Ordinal)) continue;
                // FiresCompanions serves Pouch("Jewelry") = Necklace + Ring + RingRight: the Jewelry face shows the companion's first
                // jewelry item, and a click opens its popup
                string kind = t.name.Substring(6) switch { "Tools" => "Tool", "Ammo" => "Ammo", "Food" => "Food", "Jewelry" => "Jewelry", _ => null };
                if (kind == null) continue;   // Coin purse: its own section cell
                if (kind == "Jewelry") { var jt = t.gameObject.GetComponent<JewelryToggle>() ?? t.gameObject.AddComponent<JewelryToggle>(); jt.Root = Root; var fimg = t.GetComponentInChildren<Image>(); if (fimg != null) fimg.raycastTarget = true; }
                var icon = FindDeep(t, "icon")?.GetComponent<Image>();
                var badge = FindDeep(t, "badge")?.GetComponent<TMP_Text>();
                if (icon != null) { icon.sprite = null; icon.enabled = false; }
                if (badge != null) badge.text = "";
                pouchFaces.Add((kind, icon, badge));
            }
        }

        static Transform FindDeep(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            foreach (Transform c in parent) { var hit = FindDeep(c, name); if (hit != null) return hit; }
            return null;
        }

        internal void Show(object src)
        {
            if (Root == null) return;
            bool changed = !ReferenceEquals(src, source);
            source = src;
            Root.gameObject.SetActive(true);
            if (!changed) return;
            body = Get<Humanoid>(src, "Target");
            title.text = Get<string>(src, "DisplayName") ?? "Companion";
            var mount = FindDeep(Root, UnifiedScreen.PreviewMount)?.GetComponent<RawImage>();
            preview ??= new LivePreview("Companion");
            if (body != null && mount != null) preview.Bind(body, mount, true);
            if (grid == null)
            {
                grid = VAInventoryGridApi.MountGrid(gridHost, new VAInventoryGridApi.GridOptions
                {
                    Owner = "VAInventory.CompanionPanel", VanillaRightClick = false,
                    Inventory = () => Storage(source),
                    MaxRows = StorageRows(),   // the backing grid is 8 x 18; storage = Core's first rows
                });
            }
            var inv = Storage(src);
            float cellPx = Mathf.Min(InventoryGui.instance.m_playerGrid.m_elementSpace, Mathf.Floor((Root.rect.width - 28f) / Mathf.Max(1, inv != null ? inv.GetWidth() : 8)));
            int rows = Mathf.Min(inv != null ? inv.GetHeight() : 4, StorageRows());
            gridHost.sizeDelta = new Vector2(gridHost.sizeDelta.x, rows * cellPx);

            nextRefresh = 0f;
            EquipmentPanel.PanelLog?.LogDebug($"panels: companion panel for '{title.text}' (body {(body != null ? body.name : "none")}, storage {(inv != null ? inv.GetWidth() + "x" + inv.GetHeight() : "none")})");
        }

        // Storage() = the storage grid only (FiresCompanions 0.1.59+); older: Inventory
        static Inventory Storage(object src)
        {
            var f = Get<Func<Inventory>>(src, "Storage");
            try { if (f != null) return f(); } catch { }
            return Get<Inventory>(src, "Inventory");
        }

        internal void Hide()
        {
            if (Root != null) Root.gameObject.SetActive(false);
            preview?.Hide();
            source = null; body = null;
        }

        internal VAInventoryGridApi.GridHandle Grid => grid;

        /// <summary>Rails, pouches, bars and stats, twice a second while shown.</summary>
        internal void Tick()
        {
            if (Root == null || !Root.gameObject.activeInHierarchy || source == null || Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.5f;
            if (statsShown && statsPage != null && (statsPage.rect.width < 1f || statsPage.rect.height < 1f)) LayoutStatsPage();   // 1.0.205
            UnifiedScreen.StyleCellFramesLikeGrid(Root);   // The companion's cells = the main grid's slot look
            RefreshSections();
            RefreshRails();
            RefreshPouches();
            RefreshStats();
        }

        void RefreshRails()
        {
            IDictionary worn = null;
            var wornF = source?.GetType().GetProperty("Worn")?.GetValue(source) as Delegate;
            try { worn = wornF?.DynamicInvoke() as IDictionary; } catch { worn = null; }
            if (worn == null)   // older FiresCompanions: worn items by type
            {
                var inv = Get<Inventory>(source, "Inventory");
                var isWorn = Get<Func<ItemDrop.ItemData, bool>>(source, "IsWorn");
                var d = new Dictionary<string, ItemDrop.ItemData>();
                if (inv != null && isWorn != null)
                    foreach (var item in inv.GetAllItems())
                    {
                        bool w; try { w = isWorn(item); } catch { w = false; }
                        if (w) { string k = KeyOf(item, d); if (k != null) d[k] = item; }
                    }
                worn = d;
            }
            foreach (var rc in rails)
            {
                rc.Item = rc.Key != null && worn.Contains(rc.Key) ? worn[rc.Key] as ItemDrop.ItemData : null;
                bool has = rc.Item != null;
                if (rc.Icon.enabled != has) rc.Icon.enabled = has;
                if (has && rc.Icon.sprite != rc.Item.GetIcon()) rc.Icon.sprite = rc.Item.GetIcon();
            }
        }

        static string KeyOf(ItemDrop.ItemData item, Dictionary<string, ItemDrop.ItemData> taken)
        {
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Helmet: return "Helmet";
                case ItemDrop.ItemData.ItemType.Chest: return "Chest";
                case ItemDrop.ItemData.ItemType.Legs: return "Legs";
                case ItemDrop.ItemData.ItemType.Utility: return "Utility";
                case ItemDrop.ItemData.ItemType.Trinket: return "Trinket";
                case ItemDrop.ItemData.ItemType.Shoulder: return "Shoulder";
                case ItemDrop.ItemData.ItemType.Shield: case ItemDrop.ItemData.ItemType.Torch: return "LeftHand";
                default: return taken.ContainsKey("RightHand") ? null : "RightHand";
            }
        }

        void RefreshPouches()
        {
            var pouch = source?.GetType().GetProperty("Pouch")?.GetValue(source) as Func<string, ItemDrop.ItemData[]>;
            foreach (var (kind, icon, badge) in pouchFaces)
            {
                ItemDrop.ItemData[] cells = null;
                try { cells = pouch?.Invoke(kind); } catch { cells = null; }
                ItemDrop.ItemData first = null; int n = 0;
                if (cells != null) foreach (var it in cells) if (it != null) { n++; if (first == null) first = it; }
                if (icon != null) { icon.enabled = first != null; if (first != null) icon.sprite = first.GetIcon(); }
                if (badge != null) { string s = n > 1 ? n.ToString() : ""; if (badge.text != s) badge.text = s; }
            }
        }

        // ── Every section = Core's CompanionGridLayout cell on the companion's backing grid (Core 0.2.563+) ──
        // The 8 x 18 map is read from Core at runtime when present (CompanionGridLayout.Sections: Name + Cells)
        static readonly Dictionary<string, Vector2i[]> StaticLayout = new Dictionary<string, Vector2i[]>
        {
            { "Tools", Row(4, 8) }, { "Ammo", Row(5, 4) }, { "Food", Row(6, 4) },   // Core 0.2.564 widths
            { "Helmet", At(0, 7) }, { "Shoulder", At(1, 7) }, { "FrontWeapon", At(2, 7) }, { "FrontOffhand", At(3, 7) }, { "Trinket", At(4, 7) },
            { "Torch", At(5, 7) }, { "Necklace", At(6, 7) }, { "ExtraUtility", At(7, 7) },
            { "Chest", At(0, 8) }, { "Utility", At(1, 8) }, { "BackWeapon", At(2, 8) }, { "BackOffhand", At(3, 8) }, { "Ring", At(4, 8) },
            { "RingRight", At(5, 8) }, { "CoinPurse", At(6, 8) }, { "TeleportBypass", At(7, 8) },
            { "Legs", At(0, 9) }, { "Pet", At(1, 9) }, { "Backpack", At(2, 9) },
            { "Misc", Rows(11, 3) }, { "BackpackStorage", Rows(14, 4) },
        };
        static Vector2i[] At(int x, int y) => new[] { new Vector2i(x, y) };
        static Vector2i[] Row(int y, int n) { var a = new Vector2i[n]; for (int i = 0; i < n; i++) a[i] = new Vector2i(i, y); return a; }
        static Vector2i[] Rows(int y0, int rows) { var a = new Vector2i[rows * 8]; for (int i = 0; i < a.Length; i++) a[i] = new Vector2i(i % 8, y0 + i / 8); return a; }

        static Dictionary<string, Vector2i[]> s_layout;
        static Dictionary<string, Vector2i[]> Layout()
        {
            if (s_layout != null) return s_layout;
            s_layout = StaticLayout;
            try
            {
                var t = Type.GetType("FiresCore.Npc.CompanionGridLayout, FiresUnifiedCore");
                var secs = t?.GetProperty("Sections", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IEnumerable
                           ?? t?.GetField("Sections", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IEnumerable;
                if (secs != null)
                {
                    var d = new Dictionary<string, Vector2i[]>();
                    foreach (var s in secs)
                    {
                        var name = M(s, "Name") as string; var cells = M(s, "Cells") as IEnumerable;
                        if (name == null || cells == null) continue;
                        var l = new List<Vector2i>(); foreach (var c in cells) if (c is Vector2i v) l.Add(v);
                        d[name] = l.ToArray();
                    }
                    if (d.Count > 0) s_layout = d;
                }
            }
            catch { }
            EquipmentPanel.PanelLog?.LogDebug($"panels: companion section layout from {(ReferenceEquals(s_layout, StaticLayout) ? "the 0.2.563 map (Core's CompanionGridLayout not found)" : "Core CompanionGridLayout")} ({s_layout.Count} section(s))");
            return s_layout;
        }
        static object M(object o, string n) { var ty = o.GetType(); return ty.GetProperty(n)?.GetValue(o) ?? ty.GetField(n)?.GetValue(o); }

        void BindSections(List<(string section, int index, string path, string caption)> map, TMP_FontAsset font)
        {
            foreach (var (section, index, path, caption) in map)
            {
                var cell = Root.Find(path) as RectTransform;
                if (cell == null) continue;
                if (!string.IsNullOrEmpty(caption))   // The rail label above the cell, as on the player panel
                {
                    var cap = Text(cell, "CompanionCaption", 11f, new Color(0.92f, 0.86f, 0.72f), FontStyles.Bold, font);
                    var crt = cap.rectTransform; crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(1f, 1f); crt.pivot = new Vector2(0.5f, 0f);
                    crt.sizeDelta = new Vector2(40f, 14f); crt.anchoredPosition = new Vector2(0f, 1f);
                    cap.text = caption; cap.alignment = TextAlignmentOptions.Bottom; cap.enableWordWrapping = false;
                }
                var iconGo = new GameObject("CompanionIcon", typeof(RectTransform), typeof(Image));
                iconGo.transform.SetParent(cell, false);
                var irt = (RectTransform)iconGo.transform; irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one; irt.offsetMin = new Vector2(6f, 6f); irt.offsetMax = new Vector2(-6f, -6f);
                var icon = iconGo.GetComponent<Image>(); icon.preserveAspect = true; icon.enabled = false; icon.raycastTarget = false;
                var amount = Text(cell, "CompanionAmount", 13f, Color.white, FontStyles.Bold, font);
                var art = amount.rectTransform; art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one; art.offsetMin = new Vector2(3f, 2f); art.offsetMax = new Vector2(-4f, -2f);
                amount.alignment = TextAlignmentOptions.BottomRight;
                var sc = new SecCell { Section = section, Index = index, Cell = cell, Icon = icon, Amount = amount };
                secCells.Add(sc);
                var click = cell.gameObject.AddComponent<SectionClick>(); click.Owner = this; click.Cell = sc;
                var face = cell.GetComponent<Image>(); if (face != null) face.raycastTarget = true;
            }
        }

        void RefreshSections()
        {
            if (secCells.Count == 0) return;
            var inv = Get<Inventory>(source, "Inventory");   // the full backing grid (storage + every section)
            var layout = Layout();
            // FiresCompanions 0.1.60 Section(name): the section's cells in order (null = empty); else Core's layout on the backing grid
            var section = source?.GetType().GetProperty("Section")?.GetValue(source) as Func<string, ItemDrop.ItemData[]>;
            var cache = new Dictionary<string, ItemDrop.ItemData[]>();
            foreach (var sc in secCells)
            {
                ItemDrop.ItemData item = null;
                if (section != null)
                {
                    if (!cache.TryGetValue(sc.Section, out var arr)) { try { arr = section(sc.Section); } catch { arr = null; } cache[sc.Section] = arr; }
                    if (arr != null && arr.Length > 0) { if (sc.Index < arr.Length) item = arr[sc.Index]; }
                    else if (inv != null && layout.TryGetValue(sc.Section, out var c2) && sc.Index < c2.Length) item = inv.GetItemAt(c2[sc.Index].x, c2[sc.Index].y);
                }
                else if (inv != null && layout.TryGetValue(sc.Section, out var cells) && sc.Index < cells.Length) item = inv.GetItemAt(cells[sc.Index].x, cells[sc.Index].y);
                sc.Item = item;
                bool has = item != null;
                // An empty companion Coin purse shows the faint coin,
                // as the player's does (PanelsGridLook.CoinPurseLook)
                bool ghost = !has && sc.Section == "CoinPurse" && CoinIcon() != null;
                if (sc.Icon.enabled != (has || ghost)) sc.Icon.enabled = has || ghost;
                if (has && sc.Icon.sprite != item.GetIcon()) sc.Icon.sprite = item.GetIcon();
                if (ghost && sc.Icon.sprite != CoinIcon()) sc.Icon.sprite = CoinIcon();
                var ic = has ? Color.white : new Color(1f, 1f, 1f, 0.4f);
                if (sc.Icon.color != ic) sc.Icon.color = ic;
                string a = has && item.m_shared.m_maxStackSize > 1 ? item.m_stack.ToString() : "";
                if (sc.Amount.text != a) sc.Amount.text = a;
            }
            cache.TryGetValue("BackpackStorage", out var bagArr);
            RefreshBag(bagArr);
        }

        // The cloned
        // player bag sections all hide, and the companion's LOWER BODY is built here, under Misc, on its own plate (the panel's Bkg look):
        //   "Backpack: <its bag | none>"  Quality n   +  its bag cells (FiresCompanions Bag(), the bag's cols x rows)
        //   "Inventory"                                 +  its storage grid (MountGrid, Core's storage rows), each cell the main grid's look
        readonly List<RectTransform> bagSections = new List<RectTransform>();
        RectTransform lower, lowerPlate, bagCellRoot;
        TMP_Text bagHeader, bagQuality;
        readonly List<SecCell> bagUi = new List<SecCell>();
        RectTransform miscRow;
        bool loggedLayout;

        // Every THIN Graphic (<= 4 px tall) of the
        // cloned panel in the band between the Misc row's bottom and the lower body's bag header, that is NOT part of our lower body, is
        // switched off (the clone's leftover cell borders / durability bars / dividers); logged once per panel with path + size so the
        // culprit is named. Root space.
        int strayChecked;   // Bit 1 = Gear page checked, bit 2 = Stats page checked
        void StrayLinesOff(float top)
        {
            int bit = statsShown ? 2 : 1;
            if ((strayChecked & bit) != 0 || Root == null || lower == null) return;
            strayChecked |= bit;
            var c4 = new Vector3[4]; var off = new List<string>();
            float bandTop = top + 14f, bandBottom = top - 30f;   // the gap above the bag header ("Backpack: ...") at top - 24
            foreach (var g in Root.GetComponentsInChildren<Graphic>(false))
            {
                if (g == null || !g.isActiveAndEnabled || g.transform.IsChildOf(lower) || (lowerPlate != null && g.transform.IsChildOf(lowerPlate))) continue;
                g.rectTransform.GetWorldCorners(c4);
                float y0 = Root.InverseTransformPoint(c4[0]).y, y1 = Root.InverseTransformPoint(c4[1]).y;
                float h = (c4[1] - c4[0]).magnitude / Mathf.Max(1e-5f, Root.lossyScale.y);
                if (h > 4f || y1 < bandBottom || y0 > bandTop) continue;
                g.enabled = false;
                string path = g.name; for (var p = g.transform.parent; p != null && p != Root; p = p.parent) path = p.name + "/" + path;
                off.Add($"{path} y {y0:0}..{y1:0} h {h:0.#}");
            }
            EquipmentPanel.PanelLog?.LogDebug($"panels: companion stray thin lines above the bag header ({(statsShown ? "Stats" : "Gear")} page) switched off: {(off.Count == 0 ? "none" : string.Join("; ", off.ToArray()))}");
        }

        void BindBagSections()
        {
            foreach (var t in Root.GetComponentsInChildren<RectTransform>(true))
                if (t.name.StartsWith("BagSection_", StringComparison.Ordinal) && !bagSections.Exists(b => t.IsChildOf(b))) bagSections.Add(t);
            foreach (var b in bagSections) b.gameObject.SetActive(false);
            secCells.RemoveAll(s => s.Section == "BackpackStorage" && bagSections.Exists(b => s.Cell.IsChildOf(b)));
            miscRow = FindDeep(Root, "RowMisc") as RectTransform;
            var font = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponentInChildren<TMP_Text>(true)?.font;

            // the plate: the panel's own Bkg look, behind the lower body (it reaches past the player's plate when the companion's grid does)
            var bkg = Root.Find("Bkg")?.GetComponent<Image>();
            lowerPlate = (RectTransform)new GameObject("CompanionLowerPlate", typeof(RectTransform), typeof(Image)).transform;
            lowerPlate.SetParent(Root, false); Free(lowerPlate);
            lowerPlate.SetSiblingIndex(bkg != null ? bkg.transform.GetSiblingIndex() + 1 : 0);
            lowerPlate.anchorMin = new Vector2(0f, 1f); lowerPlate.anchorMax = new Vector2(1f, 1f); lowerPlate.pivot = new Vector2(0.5f, 1f);
            var pi = lowerPlate.GetComponent<Image>(); pi.raycastTarget = false;
            if (bkg != null) { pi.sprite = bkg.sprite; pi.type = bkg.type; pi.color = bkg.color; pi.material = bkg.material; } else pi.color = new Color(0.086f, 0.067f, 0.047f, 0.97f);

            lower = (RectTransform)new GameObject("CompanionLowerBody", typeof(RectTransform)).transform;
            lower.SetParent(Root, false); Free(lower);
            lower.anchorMin = new Vector2(0f, 1f); lower.anchorMax = new Vector2(1f, 1f); lower.pivot = new Vector2(0.5f, 1f);
            lower.offsetMin = new Vector2(16f, 0f); lower.offsetMax = new Vector2(-16f, 0f);

            bagHeader = Text(lower, "BagHeader", 14f, new Color(0.941f, 0.706f, 0.298f), FontStyles.Bold, font);
            Row(bagHeader.rectTransform, 0f, 20f); bagHeader.alignment = TextAlignmentOptions.BottomLeft; bagHeader.enableWordWrapping = false;
            bagQuality = Text(lower, "BagQuality", 11f, new Color(0.92f, 0.86f, 0.72f), FontStyles.Normal, font);
            Row(bagQuality.rectTransform, 0f, 20f); bagQuality.alignment = TextAlignmentOptions.BottomRight; bagQuality.enableWordWrapping = false;
            bagCellRoot = (RectTransform)new GameObject("BagCells", typeof(RectTransform)).transform;
            bagCellRoot.SetParent(lower, false);
            Row(bagCellRoot, -24f, 0f);

            invLabel.transform.SetParent(lower, false);
            Row(invLabel.rectTransform, 0f, 20f);
            gridHost.SetParent(lower, false);
            gridHost.anchorMin = new Vector2(0f, 1f); gridHost.anchorMax = new Vector2(1f, 1f); gridHost.pivot = new Vector2(0.5f, 1f);
            gridHost.offsetMin = new Vector2(0f, 0f); gridHost.offsetMax = new Vector2(0f, 0f);
        }

        internal float NeededHeight;

        static Sprite s_coin;
        static Sprite CoinIcon() => s_coin != null ? s_coin : (s_coin = ObjectDB.instance?.GetItemPrefab("Coins")?.GetComponent<ItemDrop>()?.m_itemData?.GetIcon());

        // One of OUR objects directly under the panel: the cloned LayoutGroup must not size or move it
        static void Free(Transform t)
        {
            if (t == null) return;
            var le = t.GetComponent<LayoutElement>() ?? t.gameObject.AddComponent<LayoutElement>();
            if (!le.ignoreLayout) le.ignoreLayout = true;
        }
        float nextSelfCheck; string lastSelfCheck;

        static Vector3 Corner(RectTransform rt, int i) { var c = new Vector3[4]; rt.GetWorldCorners(c); return c[i]; }

        void StretchPlate(string name, float inset)
        {
            var rt = Root.Find(name) as RectTransform;
            if (rt == null) return;
            if (rt.anchorMin != Vector2.zero || rt.anchorMax != Vector2.one) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f); }
            var omin = new Vector2(inset, inset); var omax = new Vector2(-inset, -inset);
            if (rt.offsetMin != omin) rt.offsetMin = omin;
            if (rt.offsetMax != omax) rt.offsetMax = omax;
            if (rt.localScale != Vector3.one) rt.localScale = Vector3.one;
        }

        static void Row(RectTransform rt, float y, float h)
        {
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(0f, 0f); rt.offsetMax = new Vector2(0f, 0f);
            rt.sizeDelta = new Vector2(0f, h); rt.anchoredPosition = new Vector2(0f, y);
        }

        // one bag cell, cloned from a Misc cell (the same frame, face and size as every panel cell)
        SecCell BagCell(int index, TMP_FontAsset font)
        {
            RectTransform template = null;
            if (miscRow != null)
                foreach (var t in miscRow.GetComponentsInChildren<RectTransform>(true))
                    if (t.name.StartsWith(UnifiedScreen.CellPrefixName, StringComparison.Ordinal)) { template = t; break; }
            GameObject go;
            if (template != null)
            {
                go = UnityEngine.Object.Instantiate(template.gameObject, bagCellRoot, false);
                for (int i = go.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject);
                foreach (var mb in go.GetComponents<MonoBehaviour>()) if (!(mb is Graphic) && !(mb is Selectable)) UnityEngine.Object.Destroy(mb);
            }
            else go = new GameObject("x", typeof(RectTransform), typeof(Image));
            go.name = UnifiedScreen.CellPrefixName + "Bag" + index;
            go.SetActive(true);
            var cell = (RectTransform)go.transform;
            cell.anchorMin = cell.anchorMax = cell.pivot = new Vector2(0f, 1f);
            var iconGo = new GameObject("CompanionIcon", typeof(RectTransform), typeof(Image));
            iconGo.transform.SetParent(cell, false);
            var irt = (RectTransform)iconGo.transform; irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one; irt.offsetMin = new Vector2(6f, 6f); irt.offsetMax = new Vector2(-6f, -6f);
            var icon = iconGo.GetComponent<Image>(); icon.preserveAspect = true; icon.enabled = false; icon.raycastTarget = false;
            var amount = Text(cell, "CompanionAmount", 13f, Color.white, FontStyles.Bold, font);
            var art = amount.rectTransform; art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one; art.offsetMin = new Vector2(3f, 2f); art.offsetMax = new Vector2(-4f, -2f);
            amount.alignment = TextAlignmentOptions.BottomRight;
            var sc = new SecCell { Section = "BackpackStorage", Index = index, Cell = cell, Icon = icon, Amount = amount };
            var face = cell.GetComponent<Image>(); if (face != null) { face.enabled = true; face.raycastTarget = true; }
            var click = go.AddComponent<SectionClick>(); click.Owner = this; click.Cell = sc;
            return sc;
        }

        void RefreshBag(ItemDrop.ItemData[] bagCells)
        {
            if (lower == null) return;
            var font = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponentInChildren<TMP_Text>(true)?.font;
            ItemDrop.ItemData bag = secCells.Find(s => s.Section == "Backpack")?.Item;
            string h = bag != null ? "Backpack: " + Localization.instance.Localize(bag.m_shared.m_name) : "Backpack: none";
            if (bagHeader.text != h) bagHeader.text = h;
            int cols = 8, rws = 0;
            if (bag != null) { var (r, c) = BackpackBridge.GetConfigDimensions(bag); cols = Math.Max(1, c); rws = Math.Max(0, r); }
            int n = bag != null ? Math.Min(cols * rws, bagCells != null && bagCells.Length > 0 ? bagCells.Length : cols * rws) : 0;
            string q = bag != null ? $"Quality {bag.m_quality} · {cols} × {rws}" : "";
            if (bagQuality.text != q) bagQuality.text = q;

            // cell size + spacing = the Misc row's
            float cell = 64f, gap = 4f;
            if (miscRow != null)
                foreach (var t in miscRow.GetComponentsInChildren<RectTransform>(false))
                    if (t.name.StartsWith(UnifiedScreen.CellPrefixName, StringComparison.Ordinal) && t.rect.width > 8f) { cell = t.rect.width; break; }
            float rowW = lower.rect.width;
            float step = Mathf.Min(cell + gap, cols > 0 ? rowW / cols : cell + gap);
            while (bagUi.Count < n) bagUi.Add(BagCell(bagUi.Count, font));
            for (int i = 0; i < bagUi.Count; i++)
            {
                var sc = bagUi[i];
                bool on = i < n;
                if (sc.Cell.gameObject.activeSelf != on) sc.Cell.gameObject.SetActive(on);
                if (!on) continue;
                sc.Cell.sizeDelta = new Vector2(step - gap, step - gap);
                sc.Cell.anchoredPosition = new Vector2((i % cols) * step, -(i / cols) * step);
                var item = bagCells != null && i < bagCells.Length ? bagCells[i] : null;
                sc.Item = item;
                bool has = item != null;
                if (sc.Icon.enabled != has) sc.Icon.enabled = has;
                if (has && sc.Icon.sprite != item.GetIcon()) sc.Icon.sprite = item.GetIcon();
                string a = has && item.m_shared.m_maxStackSize > 1 ? item.m_stack.ToString() : "";
                if (sc.Amount.text != a) sc.Amount.text = a;
            }
            float bagH = n > 0 ? ((n + cols - 1) / cols) * step : 0f;
            bagCellRoot.sizeDelta = new Vector2(0f, bagH);

            // the lower body's top = the Misc row's bottom (Root space), then: bag header, bag cells, "Inventory", grid
            float top = -660f;
            if (miscRow != null && miscRow.gameObject.activeInHierarchy)
            {
                var c = new Vector3[4]; miscRow.GetWorldCorners(c);
                top = Root.InverseTransformPoint(c[0]).y - 10f;
            }
            lower.anchoredPosition = new Vector2(0f, top);
            StrayLinesOff(top);   // 1.0.246
            float y = -24f - bagH - (bagH > 0f ? 8f : 0f);
            invLabel.rectTransform.anchoredPosition = new Vector2(0f, y);
            y -= 24f;   // the header has its own row
            gridHost.anchoredPosition = new Vector2(0f, y);
            float total = -y + gridHost.rect.height + 12f;
            lower.sizeDelta = new Vector2(lower.sizeDelta.x, total);
            lowerPlate.anchoredPosition = new Vector2(0f, top + 8f);
            lowerPlate.sizeDelta = new Vector2(0f, total + 12f);
            NeededHeight = -top + total + 24f;   // PanelsLayout grows the panel to this

            // The panel's plates stretch
            // over the WHOLE panel rect (insets as the player's: Bkg 0, OpaqueUnderlay 6, Backing 20)
            StretchPlate("Bkg", 0f); StretchPlate("OpaqueUnderlay", 6f); StretchPlate("Backing", 20f);

            // SELF-CHECK: the panel rect contains the lower body, the Stats page has a size inside the panel
            if (Time.unscaledTime >= nextSelfCheck)
            {
                nextSelfCheck = Time.unscaledTime + 5f;
                float panelH = Root.rect.height, lowerBottom = top - total;
                var bk = Root.Find("Bkg") as RectTransform;
                float bkgBottom = bk != null ? Root.InverseTransformPoint(Corner(bk, 0)).y : 0f;
                bool statsIn = statsPage != null && statsPage.sizeDelta.x > 0f && statsPage.sizeDelta.y > 0f && statsPage.anchoredPosition.y - statsPage.sizeDelta.y >= -panelH;
                var sc = new Vector3[4]; Root.GetWorldCorners(sc); var cv = Root.GetComponentInParent<Canvas>()?.rootCanvas;
                float screenBottom = cv != null && cv.worldCamera == null ? sc[0].y : RectTransformUtility.WorldToScreenPoint(cv != null ? cv.worldCamera : null, sc[0]).y;
                string line = $"panels: companion self-check: panel {Root.rect.width:0}x{panelH:0}, lower body {top:0}..{lowerBottom:0} inside {(lowerBottom >= -panelH ? "YES" : "NO")}, Bkg bottom {bkgBottom:0} covers {(bkgBottom <= lowerBottom ? "YES" : "NO")}, " +
                              $"Stats page {(statsPage != null ? statsPage.sizeDelta.x : 0):0}x{(statsPage != null ? statsPage.sizeDelta.y : 0):0} inside {(statsIn ? "YES" : "NO")}, panel bottom on screen {(screenBottom >= 0f ? "YES" : "NO")} ({screenBottom:0} px)";
                if (line != lastSelfCheck) { lastSelfCheck = line; EquipmentPanel.PanelLog?.LogDebug(line); }
            }

            // the storage grid's cells: the main grid's empty-slot look (they were see-through)
            UnifiedScreen.StyleElementsLikeGrid(gridHost, false);
            UnifiedScreen.StyleCellFramesLikeGrid(bagCellRoot);

            if (!loggedLayout)
            {
                loggedLayout = true;
                EquipmentPanel.PanelLog?.LogDebug($"panels: companion lower body: misc row {(miscRow != null ? "found" : "MISSING")}, top {top:0}, bag '{h}' {cols}x{rws} = {n} cell(s) of {step - gap:0} px, inventory label at {y + 24f:0}, grid {gridHost.rect.width:0}x{gridHost.rect.height:0}, plate {total + 12f:0} px; {bagSections.Count} cloned bag section(s) hidden");
            }
        }

        // the storage rows of Core's companion grid (Sections "Storage", else rows 0-3)
        static int StorageRows()
        {
            if (Layout().TryGetValue("Storage", out var cells) && cells.Length > 0) { int m = 0; foreach (var c in cells) m = Math.Max(m, c.y + 1); return m; }
            return 4;
        }

        static MethodInfo s_setupDrag;
        static readonly HashSet<string> s_pickLogged = new HashSet<string>();   // 1.0.319


        // left click with an item on the cursor = put it in this section cell (FiresCompanions 0.1.60 Put, which accepts / rejects by
        // Core's FiresSlotCatalog); right click = take the cell's item back to the companion's storage (Take)
        internal void OnSectionClick(SecCell sc, PointerEventData.InputButton button)
        {
            if (sc == null || source == null) return;
            var gui = InventoryGui.instance;
            // A plain LEFT click on a filled section cell (Tools / Ammo / Food / Coin purse ...) puts its item ON THE CURSOR -
            // taken through FiresCompanions' Take into the companion's storage, then vanilla's own drag from there, so it drops anywhere
            // (either panel's cells, the grids, a chest, the world) like the player's special slots; with an item already on the cursor, an
            // occupied cell SWAPS (the held one goes in, the old one comes onto the cursor). Right click still sends it back to storage.
            if (button == PointerEventData.InputButton.Left && gui != null && sc.Item != null)
            {
                var held = gui.m_dragItem;
                var picked = sc.Item;
                var takeD = source.GetType().GetProperty("Take")?.GetValue(source) as Delegate;
                object taken = null;
                try { taken = takeD?.DynamicInvoke(sc.Section, sc.Index); } catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"panels: companion Take threw {(e.InnerException ?? e).Message}"); }
                var storage = Storage(source);
                var item = (taken as ItemDrop.ItemData) ?? (storage != null && storage.ContainsItem(picked) ? picked : null);
                if (item == null || storage == null || !storage.ContainsItem(item))
                {
                    EquipmentPanel.PanelLog?.LogDebug($"panels: companion pick {sc.Section}[{sc.Index}]: Take {(takeD == null ? "missing (FiresCompanions < 0.1.60)" : "refused or the item is not in its storage")} - nothing on the cursor");
                }
                else
                {
                    string swapped = "";
                    if (held != null)
                    {
                        var putD = source.GetType().GetProperty("Put")?.GetValue(source) as Delegate;
                        bool okPut = false;
                        try { okPut = putD != null && putD.DynamicInvoke(sc.Section, sc.Index, held) is bool b2 && b2; } catch { }
                        swapped = okPut ? $", {held.m_shared.m_name} went in (swap)" : $", {held.m_shared.m_name} did not fit here (kept off the cursor: it stays where it came from)";
                    }
                    s_setupDrag ??= typeof(InventoryGui).GetMethod("SetupDragItem", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    try { s_setupDrag?.Invoke(gui, new object[] { item, storage, item.m_stack }); } catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"panels: companion pick SetupDragItem threw {(e.InnerException ?? e).Message}"); }
                    if (s_pickLogged.Add(sc.Section)) EquipmentPanel.PanelLog?.LogDebug($"panels: companion pick {sc.Section}[{sc.Index}]: {item.m_shared.m_name} x{item.m_stack} onto the cursor from the companion's storage (vanilla drag){swapped}");
                }
                nextRefresh = 0f;
                return;
            }
            if (button == PointerEventData.InputButton.Left && gui != null && gui.m_dragItem != null)
            {
                var put = source.GetType().GetProperty("Put")?.GetValue(source) as Delegate;
                bool ok = false;
                try { ok = put != null && put.DynamicInvoke(sc.Section, sc.Index, gui.m_dragItem) is bool b && b; } catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"panels: companion Put threw {(e.InnerException ?? e).Message}"); }
                EquipmentPanel.PanelLog?.LogDebug($"panels: companion put {gui.m_dragItem.m_shared.m_name} -> {sc.Section}[{sc.Index}]: {(put == null ? "no Put (FiresCompanions < 0.1.60)" : ok ? "ok" : "rejected")}");
                if (ok)
                {
                    s_setupDrag ??= typeof(InventoryGui).GetMethod("SetupDragItem", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    try { s_setupDrag?.Invoke(gui, new object[] { null, null, 1 }); } catch { }
                }
            }
            else if (button == PointerEventData.InputButton.Right && sc.Item != null)
            {
                var take = source.GetType().GetProperty("Take")?.GetValue(source) as Delegate;
                object got = null;
                try { got = take?.DynamicInvoke(sc.Section, sc.Index); } catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"panels: companion Take threw {(e.InnerException ?? e).Message}"); }
                EquipmentPanel.PanelLog?.LogDebug($"panels: companion take {sc.Section}[{sc.Index}]: {(take == null ? "no Take (FiresCompanions < 0.1.60)" : got != null ? "ok" : "refused")}");
            }
            nextRefresh = 0f;
        }

        sealed class SectionClick : MonoBehaviour, IPointerClickHandler
        {
            internal CompanionPanel Owner; internal SecCell Cell;
            public void OnPointerClick(PointerEventData e) => Owner?.OnSectionClick(Cell, e.button);
        }

        static MethodInfo s_stats; static bool s_statsLooked;

        void RefreshStats()
        {
            if (!s_statsLooked)
            {
                s_statsLooked = true;
                s_stats = Type.GetType("VerdantsAscent.Modules.Companions.CompanionPanelApi, FiresCompanions")?.GetMethod("Stats", new[] { typeof(Humanoid) });
                EquipmentPanel.PanelLog?.LogDebug(s_stats != null ? "panels: companion stats from FiresCompanions CompanionPanelApi.Stats" : "panels: no CompanionPanelApi.Stats (FiresCompanions < 0.1.58); health / armour from the body");
            }
            object st = null;
            try { if (s_stats != null && body != null) st = s_stats.Invoke(null, new object[] { body }); } catch { st = null; }
            float hp = 0, maxHp = 0, sta = 0, maxSta = 0, eitr = 0, maxEitr = 0, armor = 0;
            if (st != null)
            {
                hp = P<float>(st, "Health"); maxHp = P<float>(st, "MaxHealth"); sta = P<float>(st, "Stamina"); maxSta = P<float>(st, "MaxStamina");
                eitr = P<float>(st, "Eitr"); maxEitr = P<float>(st, "MaxEitr"); armor = P<float>(st, "Armor");
            }
            if (maxHp <= 0f && body != null) { hp = body.GetHealth(); maxHp = body.GetMaxHealth(); }   // The body when Stats has none
            if (armor <= 0f && body != null) armor = body.GetBodyArmor();
            if (logStats) { logStats = false; EquipmentPanel.PanelLog?.LogDebug($"panels: companion stats {(st != null ? st.GetType().Name : "none")}: HP {hp:0}/{maxHp:0}, stamina {sta:0}/{maxSta:0}, eitr {eitr:0}/{maxEitr:0}, armour {armor:0.#}"); }
            if (vitals != null)   // HP red, Stamina yellow, Eitr purple; labels as Armor / Weight
            {
                PanelsChrome.SetVitals(vitals, hp, maxHp, sta, maxSta, eitr, maxEitr);   // Shared with the player panel
            }
            if (armorText != null) { string a = $"<color=#9C8F78>Armor</color>  {armor:0.#}"; if (armorText.text != a) armorText.text = a; }
            if (weightText != null)
            {
                var inv = Get<Inventory>(source, "Inventory");
                string w = inv != null ? $"<color=#9C8F78>Weight</color>  {inv.GetTotalWeight():0}" : "";
                if (weightText.text != w) weightText.text = w;
            }
            if (!statsShown) return;
            if (CompanionKitPage.Available)
            {
                if (Time.unscaledTime < nextPage) return;
                nextPage = Time.unscaledTime + 1f;
                if (stats != null && stats.gameObject.activeSelf) stats.gameObject.SetActive(false);
                try { CompanionKitPage.Build(statsPage, PageModel(st, hp, maxHp, sta, maxSta, eitr, maxEitr, armor)); }
                catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning("panels: companion Stats page failed: " + e.Message); }
                return;
            }
            string text;
            if (st != null)
            {
                float dmg = P<float>(st, "Damage");
                int level = P<int>(st, "Level"); string role = P<string>(st, "Role") ?? ""; string weapon = P<string>(st, "Weapon") ?? "";
                string R(string label, string value) => $"<color=#9C8F78>{label}</color><pos=45%>{value}\n";   // label | value rows
                text = R("Level", level.ToString()) + (role.Length > 0 && role != "None" ? R("Role", role) : "") + R("Health", $"{hp:0} / {maxHp:0}")
                       + R("Stamina", $"{sta:0} / {maxSta:0}") + R("Eitr", $"{eitr:0} / {maxEitr:0}") + R("Armor", $"{armor:0.#}");
                if (weapon.Length > 0) text += R("Weapon", weapon) + R("Damage", $"{dmg:0}");
                var skills = (st.GetType().GetProperty("Skills")?.GetValue(st) ?? st.GetType().GetField("Skills")?.GetValue(st)) as IEnumerable;
                if (logSkills) { logSkills = false; int cnt = 0; if (skills != null) foreach (var _ in skills) cnt++; EquipmentPanel.PanelLog?.LogDebug($"panels: companion Skills {(skills != null ? skills.GetType().Name + ", " + cnt + " entr(ies)" : "absent on " + st.GetType().Name)}"); }
                if (skills != null)
                {
                    var parts = new List<string>();
                    foreach (var kv in skills) { var k = (kv.GetType().GetProperty("Key")?.GetValue(kv) ?? kv.GetType().GetField("Key")?.GetValue(kv))?.ToString();   /* enum / any key (a string-only cast would drop them) */ var v = kv.GetType().GetProperty("Value")?.GetValue(kv) ?? kv.GetType().GetField("Value")?.GetValue(kv); if (k != null) parts.Add(R(k, $"{Convert.ToSingle(v):0}")); }
                    if (parts.Count > 0) text += "\n<b>Skills</b>\n" + string.Join("", parts.ToArray());
                    else text += NoSkills;   // 1.0.207
                }
                else text += NoSkills;
            }
            else text = body != null ? $"Health {hp:0} / {maxHp:0}\nArmor {armor:0}" : "";
            if (stats.text != text) stats.text = text;
        }

        static void SetBar(Image fill, float v, float max)
        {
            if (fill == null) return;
            float f = max > 0f ? Mathf.Clamp01(v / max) : 0f;
            if (!Mathf.Approximately(fill.fillAmount, f)) fill.fillAmount = f;
            var label = fill.transform.parent.Find("Value")?.GetComponent<TMP_Text>();   // the value on the bar
            if (label != null) { string s = max > 0f ? $"{v:0} / {max:0}" : "-"; if (label.text != s) label.text = s; }
        }
        bool logStats = true, logSkills = true;
        TMP_Text[] vitals;
        RectTransform pillMount;
        float nextPage;

        // Kit pills (Widgets.SubTabs) at the top edge of the preview
        void BuildPills()
        {
            if (Root == null || !CompanionKitPage.Available) return;
            float y = 52f;
            if (pillMount != null) { var c = new Vector3[4]; pillMount.GetWorldCorners(c); y = statsShown ? Mathf.Max(40f, -Root.InverseTransformPoint(c[1]).y + 8f) : Mathf.Max(40f, -Root.InverseTransformPoint(c[0]).y - 38f); }   // Equipment: the preview bottom edge (clear of Armor / Weight); Stats: the page top
            // With the hub page registered, Stats opens the full companion Stats page
            // (F1 > Player > Stats, companion view: preview, gear, every stat + skill, Attributes); without Core's hub the old in-panel page stays
            try { pillRow = CompanionKitPage.Pills(Root, y, statsShown, on => { if (on && PlayerStatsPage.Registered) PlayerStatsPage.OpenForCompanion(); else SetPage(on); }, BarberAction(), statsShown ? null : armorText?.rectTransform); }   // A column under Armor on the Gear page
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning("panels: companion pills failed: " + e.Message); }
        }

        // The Appearance pill opens FiresCompanions'
        // barber (0.1.83+ CompanionBarberScreen.OpenFor(Humanoid), the same screen as Unified OFF / the roster, FCC's editor when FCC takes it),
        // by reflection on the open companion; the inventory closes first so the barber is not hidden behind it. No hook = no pill.
        static MethodInfo s_barber; static bool s_barberLooked;
        static Action BarberAction()
        {
            if (!s_barberLooked)
            {
                s_barberLooked = true;
                s_barber = Type.GetType("VerdantsAscent.Modules.Companions.CompanionBarberScreen, FiresCompanions", false)?.GetMethod("OpenFor", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Humanoid) }, null);
            }
            if (s_barber == null) return null;
            return () =>
            {
                Humanoid c = null;
                try
                {
                    var cur = Type.GetType("VerdantsAscent.Modules.Companions.CompanionPanelApi, FiresCompanions", false)?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    c = cur?.GetType().GetProperty("Target")?.GetValue(cur) as Humanoid;
                }
                catch { }
                if (c == null) { EquipmentPanel.PanelLog?.LogDebug("panels: companion Appearance: no open companion"); return; }
                if (InventoryGui.IsVisible() && InventoryGui.instance != null) InventoryGui.instance.Hide();
                try { s_barber.Invoke(null, new object[] { c }); EquipmentPanel.PanelLog?.LogDebug($"panels: companion Appearance -> FiresCompanions barber for '{c.m_name}'"); }
                catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug("panels: companion Appearance failed: " + (e.InnerException ?? e).Message); }
            };
        }

        static MethodInfo s_attrs, s_spend, s_reset; static bool s_attrLooked;

        // the Stats page model: FiresCompanions Stats() + Attributes() (0.1.62), laid out like its stats / attributes screens
        CompanionKitPage.Model PageModel(object st, float hp, float maxHp, float sta, float maxSta, float eitr, float maxEitr, float armor)
        {
            var m = new CompanionKitPage.Model();
            int level = st != null ? P<int>(st, "Level") : 0;
            string role = st != null ? P<string>(st, "Role") ?? "" : "";
            string weapon = st != null ? P<string>(st, "Weapon") ?? "" : "";
            float dmg = st != null ? P<float>(st, "Damage") : 0f;
            m.Subtitle = (role.Length > 0 && role != "None" ? role + " " : "Level ") + level + (maxHp > 0f ? $"  ·  HP {hp:0} / {maxHp:0}" : "");
            m.Left.Add(("Progression", new List<(string, string)> { ("Level", level.ToString()), ("Role", role.Length > 0 ? role : "None") }));
            var combat = new List<(string, string)> { ("Armor", armor.ToString("0.#")) };
            if (weapon.Length > 0) { combat.Add(("Weapon", weapon)); combat.Add(("Damage", dmg.ToString("0"))); }
            m.Left.Add(("Combat", combat));
            m.Right.Add(("Vitals", new List<(string, string)> { ("Health", $"{hp:0} / {maxHp:0}"), ("Stamina", $"{sta:0} / {maxSta:0}"), ("Eitr", $"{eitr:0} / {maxEitr:0}") }));
            var skills = new List<(string, string)>();
            var sk = st == null ? null : (st.GetType().GetProperty("Skills")?.GetValue(st) ?? st.GetType().GetField("Skills")?.GetValue(st)) as IEnumerable;
            if (sk != null)
                foreach (var kv in sk)
                {
                    var k = (kv.GetType().GetProperty("Key")?.GetValue(kv) ?? kv.GetType().GetField("Key")?.GetValue(kv))?.ToString();
                    var v = kv.GetType().GetProperty("Value")?.GetValue(kv) ?? kv.GetType().GetField("Value")?.GetValue(kv);
                    if (k != null) skills.Add((k, $"{Convert.ToSingle(v):0}"));
                }
            if (skills.Count == 0) skills.Add(("No skills trained yet", ""));
            m.Right.Add(("Skills", skills));

            if (!s_attrLooked)
            {
                s_attrLooked = true;
                var api = Type.GetType("VerdantsAscent.Modules.Companions.CompanionPanelApi, FiresCompanions");
                s_attrs = api?.GetMethod("Attributes", new[] { typeof(Humanoid) });
                s_spend = api?.GetMethod("SpendPoint", new[] { typeof(Humanoid), typeof(string) });
                s_reset = api?.GetMethod("ResetAttributes", Type.EmptyTypes);
                EquipmentPanel.PanelLog?.LogDebug(s_attrs != null ? "panels: companion attributes from FiresCompanions CompanionPanelApi.Attributes / SpendPoint" : "panels: no CompanionPanelApi.Attributes (FiresCompanions < 0.1.62): no Attributes section");
            }
            object at = null;
            try { if (s_attrs != null && body != null) at = s_attrs.Invoke(null, new object[] { body }); } catch { at = null; }
            if (at != null)
            {
                m.Points = P<int>(at, "Points");
                m.Attributes = new List<CompanionKitPage.Attr>();
                var list = (at.GetType().GetProperty("List")?.GetValue(at) ?? at.GetType().GetField("List")?.GetValue(at)) as IEnumerable;
                if (list != null)
                    foreach (var a in list)
                        m.Attributes.Add(new CompanionKitPage.Attr { Name = P<string>(a, "Name"), Value = P<int>(a, "Value"), Max = P<int>(a, "Max"), Effect = P<string>(a, "Effect") });
                var target = body;
                m.Spend = name =>
                {
                    bool ok = false;
                    try { ok = s_spend != null && s_spend.Invoke(null, new object[] { target, name }) is bool b && b; } catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"panels: SpendPoint threw {(e.InnerException ?? e).Message}"); }
                    EquipmentPanel.PanelLog?.LogDebug($"panels: companion attribute + {name}: {(ok ? "spent" : "refused")}");
                    nextPage = 0f; nextRefresh = 0f;
                };
                if (s_reset != null)
                    m.Reset = () =>
                    {
                        bool ok = false;
                        try { ok = s_reset.Invoke(null, null) is bool b && b; } catch { }
                        EquipmentPanel.PanelLog?.LogDebug($"panels: companion attributes reset: {(ok ? "ok" : "refused")}");
                        nextPage = 0f; nextRefresh = 0f;
                    };
            }
            return m;
        }
        const string NoSkills = "\n<b>Skills</b>\n<color=#9C8F78>No skills trained yet</color>\n";

        // A plain
        // LEFT click on a worn piece (helmet / chest / legs / weapon bars ...) with nothing on the cursor lifts it ONTO the cursor like the
        // player's slots - unequipped through FiresCompanions' Unequip (it lands in the companion's storage) and vanilla's drag starts from
        // there, so it drops wherever he clicks. One Debug line per click.
        internal void OnRailLeftClick(RailCell rc)
        {
            var gui = InventoryGui.instance;
            if (rc == null || source == null || gui == null) return;
            if (gui.m_dragItem != null) { EquipmentPanel.PanelLog?.LogDebug($"panels: companion worn click {rc.Key}: an item is already on the cursor - left to the cell's own drop handling"); return; }
            var item = rc.Item;
            if (item == null) { EquipmentPanel.PanelLog?.LogDebug($"panels: companion worn click {rc.Key}: empty slot, nothing to pick up"); return; }
            var unequip = Get<Func<ItemDrop.ItemData, bool>>(source, "Unequip");
            bool ok = false;
            try { ok = unequip != null && unequip(item); } catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"panels: companion Unequip threw {e.Message}"); }
            var storage = Storage(source);
            bool inStorage = storage != null && storage.ContainsItem(item);
            if (inStorage)   // also when another handler already unequipped it this click
            {
                s_setupDrag ??= typeof(InventoryGui).GetMethod("SetupDragItem", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                try { s_setupDrag?.Invoke(gui, new object[] { item, storage, item.m_stack }); } catch (Exception e) { EquipmentPanel.PanelLog?.LogDebug($"panels: companion worn pick SetupDragItem threw {(e.InnerException ?? e).Message}"); }
            }
            EquipmentPanel.PanelLog?.LogDebug($"panels: companion worn click {rc.Key}: {item.m_shared.m_name} - unequip {(unequip == null ? "missing" : ok ? "ok" : "refused")}, in storage {inStorage}, cursor {(gui.m_dragItem == item ? "holds it (picked up)" : "empty")}");
            nextRefresh = 0f;
        }

        internal void OnRailRightClick(RailCell rc)
        {
            if (rc?.Item == null || source == null) return;
            var unequip = Get<Func<ItemDrop.ItemData, bool>>(source, "Unequip");
            bool ok = unequip != null && unequip(rc.Item);
            EquipmentPanel.PanelLog?.LogDebug($"panels: companion unequip {rc.Item.m_shared.m_name} from {rc.Key}: {(ok ? "ok" : "refused")}");
            nextRefresh = 0f;
        }

        static T Get<T>(object o, string name) where T : class
        {
            try { return o?.GetType().GetProperty(name)?.GetValue(o) as T; } catch { return null; }
        }

        static T P<T>(object o, string name)
        {
            // Properties OR fields, any numeric type
            try
            {
                var ty = o.GetType();
                var v = ty.GetProperty(name)?.GetValue(o) ?? ty.GetField(name)?.GetValue(o);
                if (v is T t) return t;
                if (v != null && (typeof(T) == typeof(float) || typeof(T) == typeof(int))) return (T)Convert.ChangeType(v, typeof(T));
                return default;
            }
            catch { return default; }
        }

        // The companion's Jewelry face opens / closes ITS jewelry popup (the clone of the player's PouchPopup_jewelry; its Necklace /
        // Ring / RingRight cells are section cells bound to the companion by BindSections), as the player's face does
        sealed class JewelryToggle : MonoBehaviour, IPointerClickHandler
        {
            internal RectTransform Root;
            public void OnPointerClick(PointerEventData e)
            {
                if (e.button != PointerEventData.InputButton.Left || Root == null) return;
                var pop = FindDeep(Root, "PouchPopup_jewelry");
                if (pop == null) { EquipmentPanel.PanelLog?.LogDebug("panels: companion jewelry popup not found in the clone"); return; }
                bool open = !pop.gameObject.activeSelf;
                pop.gameObject.SetActive(open);
                if (open) pop.SetAsLastSibling();
                EquipmentPanel.PanelLog?.LogDebug($"panels: companion jewelry popup {(open ? "opened" : "closed")} ({pop.GetComponentsInChildren<RectTransform>(true).Length} node(s))");
            }
        }

        sealed class RailClick : MonoBehaviour, IPointerClickHandler
        {
            internal CompanionPanel Owner; internal RailCell Cell;
            public void OnPointerClick(PointerEventData e)
            {
                if (e.button == PointerEventData.InputButton.Right) Owner?.OnRailRightClick(Cell);
                else if (e.button == PointerEventData.InputButton.Left) Owner?.OnRailLeftClick(Cell);   // 1.0.320
            }
        }
    }
}
