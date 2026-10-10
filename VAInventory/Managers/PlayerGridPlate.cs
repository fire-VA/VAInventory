using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // Fire 10-08 (1.0.85), the main inventory grid built like the character panel: "a dark background with a slightly smaller lighter
    // background inside of that, with a little padding around all sides, and then a little padding inside of that lighter background
    // is our inventory grid squares." Three layers behind the vanilla cells, measured from the cells outward so nothing crosses a
    // frame edge:
    //   1. Frame: the dark outer plate, in the character panel's own underlay colour.
    //   2. Inner: the lighter panel background, the character panel's Bkg (sprite, type, colour, material) over the same underlay,
    //      exactly as the panel itself is layered. It sits Pad inside the frame.
    //   3. The vanilla cells, Pad inside the inner plate, untouched (the same element and tint as the panel's backpack/Misc cells).
    // Pad is the panel's own inset (UnifiedScreen.UnderlayInset, 6). The grid panel's vanilla Bkg and Darken are hidden while the
    // Unified layout is up and put back when it is left. The panel is the PlayerGrid's own parent (1.0.83: a name lookup under
    // m_inventoryRoot found nothing, so 1.0.72-1.0.81 never ran).
    internal static class PlayerGridPlate
    {
        const float Pad = 6f;
        const string FrameName = "VAInventoryGridFrame";
        const string InnerUnderName = "VAInventoryGridInnerUnder";
        const string InnerName = "VAInventoryGridInner";
        static readonly string[] HiddenVanilla = { "Bkg", "Darken" };
        static readonly Color FallbackDark = new Color(0.10f, 0.08f, 0.06f, 0.92f);

        static RectTransform frame, innerUnder, inner;
        static readonly List<Image> hidden = new List<Image>();
        static readonly List<Outline> edged = new List<Outline>();
        internal static Color? GridCellFace;
        static Vector4 lastSignature = new Vector4(float.NaN, 0f, 0f, 0f);
        static bool pathLogged, themedLogged;
        static float nextMissLog;

        internal static void Tick(Image backdrop, Image inlay, Image panelUnderlay, Color goldEdge, float edgeAlpha)
        {
            var gui = InventoryGui.instance;
            var grid = gui != null ? gui.m_playerGrid : null;
            var gridRoot = grid != null ? grid.m_gridRoot : null;
            var player = grid != null ? grid.transform.parent as RectTransform : null;
            if (gridRoot == null || player == null || !player.gameObject.activeInHierarchy || backdrop == null)
            {
                LogMiss(grid, gridRoot, player, backdrop);
                return;
            }
            if (!pathLogged)
            {
                pathLogged = true;
                EquipmentPanel.PanelLog?.LogDebug($"player grid: panel {PathOf(player)}, backdrop {backdrop.sprite?.name}, underlay {(panelUnderlay != null)}");
            }

            int count = Mathf.Min(gridRoot.childCount, Slots.InventorySizePlayer);
            if (count <= 0 || !TryCellBounds(gridRoot, player, count, out Rect cells)) return;

            HideVanilla(player);
            Color dark = panelUnderlay != null ? panelUnderlay.color : FallbackDark;
            if (frame == null) frame = NewImage(player, FrameName).rectTransform;
            if (innerUnder == null) innerUnder = NewImage(player, InnerUnderName).rectTransform;
            if (inner == null) inner = NewImage(player, InnerName).rectTransform;
            SetColor(frame, dark);
            SetColor(innerUnder, dark);
            // 1.0.86 (round 30: the grid's inner layer came out dark grey with the VA emblem): the tan is the panel's lighter
            // Backing inlay, not its Bkg art, exactly as the crafting skin (which came out right) uses it. Same Outline too.
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
            if (frame.GetSiblingIndex() != 0) frame.SetSiblingIndex(0);
            if (innerUnder.GetSiblingIndex() != 1) innerUnder.SetSiblingIndex(1);
            if (inner.GetSiblingIndex() != 2) inner.SetSiblingIndex(2);

            // 1.0.86: the empty cells get the Misc / backpack cell edge (UnifiedScreen.BindChrome: gold, 1 px, the panel's cell edge
            // alpha), so the 8 x N squares read on the tan like the panel's own.
            for (int i = 0; i < count; i++)
            {
                var cellImage = gridRoot.GetChild(i).GetComponent<Image>();
                if (cellImage == null || cellImage.GetComponent<Outline>() != null) continue;
                var cellEdge = cellImage.gameObject.AddComponent<Outline>();
                cellEdge.effectColor = new Color(goldEdge.r, goldEdge.g, goldEdge.b, edgeAlpha);
                cellEdge.effectDistance = new Vector2(1f, -1f);
                edged.Add(cellEdge);
            }

            // 1.0.88 (Fire: the grid's darker squares, on the player panel's cells too): the colour the main grid's empty cells
            // render at, measured from round 31's shot (docs_vainventory_full_panel 18:45, RGB 45/39/28), opaque, so the panel's
            // cells show the same square whatever is under them.
            GridCellFace = new Color(45f / 255f, 39f / 255f, 28f / 255f, 1f);

            var signature = new Vector4(cells.xMin, cells.yMin, cells.width, cells.height);
            if (signature != lastSignature)
            {
                lastSignature = signature;
                Place(frame, cells, 2f * Pad, player);
                Place(innerUnder, cells, Pad, player);
                Place(inner, cells, Pad, player);
            }
            if (!themedLogged)
            {
                themedLogged = true;
                EquipmentPanel.PanelLog?.LogDebug($"player grid: themed {PathOf(player)}: frame (underlay colour) > inner (character panel {(inlay != null ? "Backing inlay" : "Bkg")} '{innerSrc.sprite?.name}' over the underlay, {Pad} in) > {count} vanilla cells ({Pad} in); cells {cells.width:0}x{cells.height:0}; vanilla Bkg/Darken hidden");
            }
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

        static void HideVanilla(RectTransform player)
        {
            foreach (string name in HiddenVanilla)
            {
                var image = player.Find(name)?.GetComponent<Image>();
                if (image == null || !image.enabled) continue;
                image.enabled = false;
                if (!hidden.Contains(image)) hidden.Add(image);
            }
        }

        static void LogMiss(InventoryGrid grid, RectTransform gridRoot, RectTransform player, Image backdrop)
        {
            if (Time.unscaledTime < nextMissLog) return;
            nextMissLog = Time.unscaledTime + 10f;
            EquipmentPanel.PanelLog?.LogDebug($"player grid: skipped (grid {(grid != null)}, root {(gridRoot != null)}, panel {(player != null ? PathOf(player) : "none")}, active {(player != null && player.gameObject.activeInHierarchy)}, backdrop {(backdrop != null)})");
        }

        static string PathOf(Transform t)
        {
            string path = t.name;
            for (var p = t.parent; p != null && path.Length < 200; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        // The main grid's cells (the first 'count' children of the grid root), in the player panel's local space.
        static bool TryCellBounds(RectTransform gridRoot, RectTransform player, int count, out Rect bounds)
        {
            bounds = default;
            var corners = new Vector3[4];
            bool any = false;
            float xMin = 0f, yMin = 0f, xMax = 0f, yMax = 0f;
            for (int i = 0; i < count; i++)
            {
                var cell = gridRoot.GetChild(i) as RectTransform;
                if (cell == null || !cell.gameObject.activeSelf) continue;
                cell.GetWorldCorners(corners);
                for (int k = 0; k < 4; k += 2)
                {
                    Vector3 p = player.InverseTransformPoint(corners[k]);
                    if (!any) { xMin = xMax = p.x; yMin = yMax = p.y; any = true; }
                    xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x);
                    yMin = Mathf.Min(yMin, p.y); yMax = Mathf.Max(yMax, p.y);
                }
            }
            if (!any) return false;
            bounds = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return true;
        }

        static Image NewImage(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var image = go.GetComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        // Leaving the Unified layout: our layers go and the grid panel's own Bkg and Darken come back.
        internal static void Remove()
        {
            foreach (var r in new[] { frame, innerUnder, inner })
                if (r != null) Object.Destroy(r.gameObject);
            frame = innerUnder = inner = null;
            foreach (var image in hidden) if (image != null) image.enabled = true;
            hidden.Clear();
            foreach (var e in edged) if (e != null) Object.Destroy(e);
            edged.Clear();
            lastSignature = new Vector4(float.NaN, 0f, 0f, 0f);
            pathLogged = themedLogged = false;
        }
    }
}
