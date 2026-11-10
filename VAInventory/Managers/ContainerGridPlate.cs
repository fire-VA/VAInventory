using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // the CHEST grid gets the player grid's three layers
    // (PlayerGridPlate) so chests match the inventory: the dark frame, the lighter inner panel with its edge, and the cells, Pad
    // apart on every side. The plate hugs the container's cells (a 5 x 2 wood chest gets a small plate, a bigger chest a bigger
    // one) and leaves the chest panel's own art, title row and buttons as they are (room for 's Tags button under
    // "Place stacks"). The empty cells take the main grid's cell face and edge.
    internal static class ContainerGridPlate
    {
        const float Pad = 6f;
        const string FrameName = "VAInventoryChestFrame";
        const string InnerUnderName = "VAInventoryChestInnerUnder";
        const string InnerName = "VAInventoryChestInner";
        static readonly Color FallbackDark = new Color(0.10f, 0.08f, 0.06f, 0.92f);   // Back

        static RectTransform frame, innerUnder, inner, plateParent;
        static bool PanelsBuilt() => frame != null || innerUnder != null || inner != null || faced.Count > 0;   // 1.0.182
        static readonly List<Outline> edged = new List<Outline>();
        static readonly Dictionary<Image, Color> faced = new Dictionary<Image, Color>();
        static Vector4 lastSignature = new Vector4(float.NaN, 0f, 0f, 0f);
        static bool pathLogged;
        static int repositions;
        static float nextMissLog;

        internal static void Tick(Image backdrop, Image inlay, Image panelUnderlay, Color goldEdge, float edgeAlpha)
        {
            // Under Panels the grid keeps vanilla's OWN panel (wood Bkg + item_background cells, as with
            // the unified UI off). No plate, nothing hidden; whatever an earlier Tick built is taken down once.
            // The old Panels early-out is gone; the plate is part of the pre-unified look (as with the unified UI off)
            // 
            // under Panels with Core's Fires UI override ON, ChestSections lays the chest out and draws its own gold frame
            // (KitWell_ChestFrame) around a panel sized to the grid, and Core's CraftingSkin draws the kit look. This plate was a SECOND
            // frame + inner, sized to the cells and placed by its own rule, and re-ordered siblings around the grid every frame. It stands
            // down there (taken down once if an earlier Tick built it); the pre-Panels layouts keep it as before.
            if (UnifiedScreen.PanelsMode && ConfigManager.UiOverrideOnForChest())
            {
                if (PanelsBuilt()) { Remove(); EquipmentPanel.PanelLog?.LogDebug("chest grid plate: not drawn under Panels (ChestSections' frame is the one plate)"); }
                return;
            }
            var gui = InventoryGui.instance;
            var grid = gui != null ? gui.m_containerGrid : null;
            var gridRoot = grid != null ? grid.m_gridRoot : null;
            var panel = gui != null ? gui.m_container : null;
            if (gridRoot == null || panel == null || !panel.gameObject.activeInHierarchy || !gui.IsContainerOpen() || backdrop == null)
            {
                LogMiss(grid, gridRoot, panel, backdrop);
                return;
            }
            int count = gridRoot.childCount;
            if (count <= 0 || !TryCellBounds(gridRoot, panel, count, out Rect cells)) return;

            if (plateParent != panel) { Remove(); plateParent = panel; }
            Color dark = panelUnderlay != null ? panelUnderlay.color : FallbackDark;
            if (frame == null) frame = NewImage(panel, FrameName).rectTransform;
            if (innerUnder == null) innerUnder = NewImage(panel, InnerUnderName).rectTransform;
            if (inner == null) inner = NewImage(panel, InnerName).rectTransform;
            SetColor(frame, dark);
            SetColor(innerUnder, dark);
            var innerSrc = inlay != null ? inlay : backdrop;
            var innerImage = inner.GetComponent<Image>();
            if (innerImage.sprite != innerSrc.sprite || innerImage.color != innerSrc.color)
            {
                innerImage.sprite = innerSrc.sprite;
                innerImage.type = innerSrc.type;
                innerImage.color = innerSrc.color;
                innerImage.material = innerSrc.material;
                var srcEdge = innerSrc.GetComponent<Outline>();
                if (srcEdge != null && inner.GetComponent<Outline>() == null)
                {
                    var edge = inner.gameObject.AddComponent<Outline>();
                    edge.effectColor = srcEdge.effectColor;
                    edge.effectDistance = srcEdge.effectDistance;
                }
            }
            // just above the panel's own background art, below the grid root (so the cells and items draw on top).
            // 1.0.136 put the three
            // layers AT the grid's index, which pushed the grid 3 down; the next frame then moved the frame past the grid, covering
            // it, and so on. Now the order is only checked (frame, inner under, inner directly before the grid) and repaired when
            // wrong, each layer moved to just before the grid's CURRENT index. A repair every frame shows in the Debug count.
            int g = GridSibling(panel, gridRoot);
            if (frame.GetSiblingIndex() != g - 3 || innerUnder.GetSiblingIndex() != g - 2 || inner.GetSiblingIndex() != g - 1)
            {
                foreach (var r in new[] { frame, innerUnder, inner })
                {
                    int gi = GridSibling(panel, gridRoot), ri = r.GetSiblingIndex();
                    r.SetSiblingIndex(ri < gi ? gi - 1 : gi);
                }
                repositions++;
                EquipmentPanel.PanelLog?.LogDebug($"chest grid plate: {(repositions == 1 ? "built" : "repositioned")} ({repositions}) - frame {frame.GetSiblingIndex()}, inner under {innerUnder.GetSiblingIndex()}, inner {inner.GetSiblingIndex()}, grid {GridSibling(panel, gridRoot)}");
            }

            Color face = PlayerGridPlate.GridCellFace ?? new Color(45f / 255f, 39f / 255f, 28f / 255f, 1f);
            // Under Panels Core's CraftingSkin Role.Slot styles the chest cells; VAInventory adds no face / outline there
            for (int i = 0; i < count && !UnifiedScreen.PanelsMode; i++)
            {
                var cellImage = gridRoot.GetChild(i).GetComponent<Image>();
                if (cellImage == null) continue;
                if (!faced.ContainsKey(cellImage)) { faced[cellImage] = cellImage.color; cellImage.color = face; }
                if (cellImage.GetComponent<Outline>() != null) continue;
                var cellEdge = cellImage.gameObject.AddComponent<Outline>();
                cellEdge.effectColor = new Color(goldEdge.r, goldEdge.g, goldEdge.b, edgeAlpha);
                cellEdge.effectDistance = new Vector2(1f, -1f);
                edged.Add(cellEdge);
            }

            var signature = new Vector4(cells.xMin, cells.yMin, cells.width, cells.height);
            if (signature != lastSignature)
            {
                lastSignature = signature;
                Place(frame, cells, 2f * Pad, panel);
                Place(innerUnder, cells, Pad, panel);
                Place(inner, cells, Pad, panel);
                EquipmentPanel.PanelLog?.LogDebug($"chest grid: plate on {PathOf(panel)}: frame > inner ('{(innerSrc.sprite != null ? innerSrc.sprite.name : "none")}') > {count} cells, {Pad} px pads; cells {cells.width:0}x{cells.height:0}");
            }
            if (!pathLogged)
            {
                pathLogged = true;
                EquipmentPanel.PanelLog?.LogDebug($"chest grid: panel {PathOf(panel)}, grid root {PathOf(gridRoot)}");
            }
        }

        // The grid root's sibling index under the panel (or its ancestor's that sits directly under the panel).
        static int GridSibling(RectTransform panel, Transform gridRoot)
        {
            for (var t = gridRoot; t != null; t = t.parent)
                if (t.parent == panel) return t.GetSiblingIndex();
            return panel.childCount;
        }

        static void Place(RectTransform r, Rect cells, float grow, RectTransform panel)
        {
            Rect p = panel.rect;
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = new Vector2(cells.center.x - p.center.x, cells.center.y - p.center.y);
            r.sizeDelta = new Vector2(cells.width + 2f * grow, cells.height + 2f * grow);
        }

        static void SetColor(RectTransform r, Color c)
        {
            var image = r.GetComponent<Image>();
            if (image.color != c) image.color = c;
        }

        static void LogMiss(InventoryGrid grid, RectTransform gridRoot, RectTransform panel, Image backdrop)
        {
            if (Time.unscaledTime < nextMissLog) return;
            nextMissLog = Time.unscaledTime + 10f;
            EquipmentPanel.PanelLog?.LogDebug($"chest grid: skipped (grid {(grid != null)}, root {(gridRoot != null)}, panel {(panel != null ? PathOf(panel) : "none")}, open {(InventoryGui.instance != null && InventoryGui.instance.IsContainerOpen())}, backdrop {(backdrop != null)})");
        }

        static string PathOf(Transform t)
        {
            string path = t.name;
            for (var p = t.parent; p != null && path.Length < 200; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        static bool TryCellBounds(RectTransform gridRoot, RectTransform panel, int count, out Rect bounds)
        {
            // 1.0.364 (UI audit V12): GetWorldCorners on every cell only when the layout moved (as PlayerGridPlate)
            bounds = default;
            int active = 0;
            for (int i = 0; i < count; i++) if (gridRoot.GetChild(i).gameObject.activeSelf) active = active * 31 + i + 1;
            var first = gridRoot.GetChild(0) as RectTransform;
            var last = gridRoot.GetChild(count - 1) as RectTransform;
            Vector3 rootAt = panel.InverseTransformPoint(gridRoot.position);
            var key = new Vector4(rootAt.x, rootAt.y, gridRoot.rect.width + gridRoot.rect.height * 7919f, gridRoot.lossyScale.x / Mathf.Max(1e-6f, panel.lossyScale.x));
            var cellKey = new Vector4(first != null ? first.anchoredPosition.x : 0f, first != null ? first.anchoredPosition.y : 0f, last != null ? last.anchoredPosition.x : 0f, last != null ? last.anchoredPosition.y : 0f);
            if (cachedValid && key == cachedKey && cellKey == cachedCellKey && count == cachedCount && active == cachedActive && panel == cachedPanel)
            {
                bounds = cachedBounds;
                return true;
            }
            cachedValid = false;
            bool any = false;
            float xMin = 0f, yMin = 0f, xMax = 0f, yMax = 0f;
            for (int i = 0; i < count; i++)
            {
                var cell = gridRoot.GetChild(i) as RectTransform;
                if (cell == null || !cell.gameObject.activeSelf) continue;
                cell.GetWorldCorners(corners);
                for (int k = 0; k < 4; k += 2)
                {
                    Vector3 p = panel.InverseTransformPoint(corners[k]);
                    if (!any) { xMin = xMax = p.x; yMin = yMax = p.y; any = true; }
                    xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x);
                    yMin = Mathf.Min(yMin, p.y); yMax = Mathf.Max(yMax, p.y);
                }
            }
            if (!any) return false;
            bounds = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            cachedValid = true; cachedKey = key; cachedCellKey = cellKey; cachedCount = count; cachedActive = active; cachedPanel = panel; cachedBounds = bounds;
            return true;
        }

        static readonly Vector3[] corners = new Vector3[4];
        static bool cachedValid; static Vector4 cachedKey, cachedCellKey; static int cachedCount, cachedActive; static RectTransform cachedPanel; static Rect cachedBounds;

        static Image NewImage(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var image = go.GetComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        // Leaving the Unified layout: our layers go and the chest cells get their own face back.
        internal static void Remove()
        {
            foreach (var r in new[] { frame, innerUnder, inner })
                if (r != null) Object.Destroy(r.gameObject);
            frame = innerUnder = inner = null;
            plateParent = null;
            foreach (var e in edged) if (e != null) Object.Destroy(e);
            edged.Clear();
            foreach (var kv in faced) if (kv.Key != null) kv.Key.color = kv.Value;
            faced.Clear();
            lastSignature = new Vector4(float.NaN, 0f, 0f, 0f);
            pathLogged = false;
            repositions = 0;
        }
    }
}
