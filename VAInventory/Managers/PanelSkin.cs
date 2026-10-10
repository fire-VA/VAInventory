using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // Fire 10-08 (1.0.82): "The crafting panel, the main inventory grid, the player panel and the companion panels and UIs all share
    // the same layout and colour scheme." The vanilla Crafting panel gets the character panel's three layers, copied live: its
    // solid dark underlay (same inset), its Bkg art on the panel's own Bkg, and its lighter Backing inlay (same anchors/offsets,
    // same Outline). Nothing is hand-coloured. Everything is put back when the Unified layout is left.
    internal static class PanelSkin
    {
        const string UnderName = "VAInventorySkinUnderlay";
        const string InlayName = "VAInventorySkinInlay";

        sealed class Saved
        {
            internal Image Bkg;
            internal Sprite Sprite;
            internal Image.Type Type;
            internal Color Color;
            internal Material Material;
            internal GameObject Under, Inlay;
        }

        static readonly Dictionary<RectTransform, Saved> skinned = new Dictionary<RectTransform, Saved>();

        internal static void Apply(RectTransform panel, Image bkgSrc, Image underSrc, Image inlaySrc)
        {
            if (panel == null || bkgSrc == null || !panel.gameObject.activeInHierarchy) return;
            var bkg = panel.Find("Bkg")?.GetComponent<Image>();
            if (!skinned.TryGetValue(panel, out var s))
            {
                s = new Saved();
                if (bkg != null) { s.Bkg = bkg; s.Sprite = bkg.sprite; s.Type = bkg.type; s.Color = bkg.color; s.Material = bkg.material; }
                skinned[panel] = s;
                EquipmentPanel.PanelLog?.LogDebug($"panel skin: '{panel.name}' takes the character panel's underlay, background and inlay");
            }
            if (bkg != null && (bkg.sprite != bkgSrc.sprite || bkg.color != bkgSrc.color || bkg.type != bkgSrc.type))
            {
                bkg.sprite = bkgSrc.sprite; bkg.type = bkgSrc.type; bkg.color = bkgSrc.color; bkg.material = bkgSrc.material;
            }
            if (underSrc != null)
            {
                if (s.Under == null) s.Under = Copy(panel, UnderName, underSrc);
                Sync(s.Under, underSrc);
                if (s.Under.transform.GetSiblingIndex() != 0) s.Under.transform.SetAsFirstSibling();
            }
            if (inlaySrc != null)
            {
                if (s.Inlay == null) s.Inlay = Copy(panel, InlayName, inlaySrc);
                Sync(s.Inlay, inlaySrc);
                int at = bkg != null ? bkg.transform.GetSiblingIndex() + 1 : 1;
                if (s.Inlay.transform.GetSiblingIndex() != at) s.Inlay.transform.SetSiblingIndex(at);
            }
            FitToContent(panel, s);
        }

        // 1.0.95 (Fire: the recipe list's text was cut off by the tan plate's left edge): the inlay copied the character panel's
        // offsets, which are narrower on the left than Crafting's recipe list. The inlay now grows so the recipe list (its
        // viewport, icons and selection highlight) sits Pad inside it, and the dark underlay stays Pad beyond the inlay on that
        // side. Only edges that are too tight move; the right side and the Craft button keep their place.
        const float Pad = 6f;
        const float LeftGap = 12f;
        const float CraftBottomGap = 8f;
        static float nextFitLog;
        static readonly Vector3[] fitCorners = new Vector3[4];

        static void FitToContent(RectTransform panel, Saved s)
        {
            var gui = InventoryGui.instance;
            if (gui == null || panel != gui.m_crafting || s.Inlay == null) return;
            var list = gui.m_recipeListRoot != null ? gui.m_recipeListRoot.parent as RectTransform : null;
            if (list == null) return;
            // 1.0.96 (Fire: the recipe ICONS and the selected-row highlight sat half on the dark frame): the rows' parts reach
            // further left than the list's viewport, so the leftmost edge of every active graphic in the list counts.
            list.GetWorldCorners(fitCorners);
            float listLeft = panel.InverseTransformPoint(fitCorners[0]).x;
            foreach (var g in gui.m_recipeListRoot.GetComponentsInChildren<Graphic>(false))
            {
                g.rectTransform.GetWorldCorners(fitCorners);
                listLeft = Mathf.Min(listLeft, panel.InverseTransformPoint(fitCorners[0]).x);
            }
            var inlay = (RectTransform)s.Inlay.transform;
            float inlayLeft = InnerLeft(panel, inlay);
            float wantInlay = listLeft - LeftGap;   // 1.0.99 (Fire: "a bit more padding on the LEFT"): 12 between the icons and the tan edge
            if (Time.unscaledTime >= nextFitLog)
            {
                nextFitLog = Time.unscaledTime + 10f;
                EquipmentPanel.PanelLog?.LogDebug($"panel skin: Crafting recipe rows left X {listLeft:0} (icons/highlight/names), tan plate left X {inlayLeft:0}, gap {listLeft - inlayLeft:0} (need >= {LeftGap:0})");
            }
            if (inlayLeft > wantInlay + 0.5f)
            {
                inlay.offsetMin = new Vector2(inlay.offsetMin.x - (inlayLeft - wantInlay), inlay.offsetMin.y);
                EquipmentPanel.PanelLog?.LogDebug($"panel skin: Crafting inlay left edge {inlayLeft:0} -> {wantInlay:0} (recipe list starts at {listLeft:0})");
                inlayLeft = wantInlay;
            }
            // 1.0.99 (Fire: "padding on the BOTTOM so the Craft button sits INSIDE the light background"): the tan plate's
            // bottom edge goes CraftBottomGap below the Craft button; the dark underlay follows Pad beyond it.
            float inlayBottom = InnerBottom(panel, inlay);
            var craft = gui.m_craftButton != null ? gui.m_craftButton.transform as RectTransform : null;
            if (craft != null && craft.gameObject.activeInHierarchy)
            {
                craft.GetWorldCorners(fitCorners);
                float craftBottom = panel.InverseTransformPoint(fitCorners[0]).y;
                float wantBottom = craftBottom - CraftBottomGap;
                if (inlayBottom > wantBottom + 0.5f)
                {
                    inlay.offsetMin = new Vector2(inlay.offsetMin.x, inlay.offsetMin.y - (inlayBottom - wantBottom));
                    EquipmentPanel.PanelLog?.LogDebug($"panel skin: Crafting inlay bottom edge {inlayBottom:0} -> {wantBottom:0} (Craft button bottom {craftBottom:0})");
                    inlayBottom = wantBottom;
                }
            }
            if (s.Under != null)
            {
                var under = (RectTransform)s.Under.transform;
                float underLeft = InnerLeft(panel, under);
                if (underLeft > inlayLeft - Pad + 0.5f)
                    under.offsetMin = new Vector2(under.offsetMin.x - (underLeft - (inlayLeft - Pad)), under.offsetMin.y);
                float underBottom = InnerBottom(panel, under);
                if (underBottom > inlayBottom - Pad + 0.5f)
                    under.offsetMin = new Vector2(under.offsetMin.x, under.offsetMin.y - (underBottom - (inlayBottom - Pad)));
            }
        }

        static float InnerBottom(RectTransform panel, RectTransform r)
        {
            r.GetWorldCorners(fitCorners);
            return panel.InverseTransformPoint(fitCorners[0]).y;
        }

        static float InnerLeft(RectTransform panel, RectTransform r)
        {
            r.GetWorldCorners(fitCorners);
            return panel.InverseTransformPoint(fitCorners[0]).x;
        }

        static GameObject Copy(RectTransform parent, string name, Image src)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var rt = (RectTransform)go.transform;
            var from = src.rectTransform;
            rt.anchorMin = from.anchorMin; rt.anchorMax = from.anchorMax; rt.pivot = from.pivot;
            rt.offsetMin = from.offsetMin; rt.offsetMax = from.offsetMax;
            var image = go.GetComponent<Image>();
            image.raycastTarget = false;
            var edge = src.GetComponent<Outline>();
            if (edge != null)
            {
                var copy = go.AddComponent<Outline>();
                copy.effectColor = edge.effectColor;
                copy.effectDistance = edge.effectDistance;
            }
            return go;
        }

        static void Sync(GameObject go, Image src)
        {
            var image = go.GetComponent<Image>();
            if (image.sprite != src.sprite) { image.sprite = src.sprite; image.type = src.type; image.material = src.material; }
            if (image.color != src.color) image.color = src.color;
        }

        internal static void RemoveAll()
        {
            foreach (var kv in skinned)
            {
                var s = kv.Value;
                if (s.Bkg != null) { s.Bkg.sprite = s.Sprite; s.Bkg.type = s.Type; s.Bkg.color = s.Color; s.Bkg.material = s.Material; }
                if (s.Under != null) Object.Destroy(s.Under);
                if (s.Inlay != null) Object.Destroy(s.Inlay);
            }
            skinned.Clear();
        }
    }
}
