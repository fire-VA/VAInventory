using HarmonyLib;
using TMPro;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.278 (V4/V5: crafting + Chunk Seed "look cheap and thrown together"; split agreed with Core 0.2.637
    /// CraftingSkin draws the look, VAInventory the layout/content). Under Panels the crafting panel gets two named section rects that
    /// Core's CraftingSkin turns into inset gold-edged wells:
    ///   KitWell_Description  behind the item description (the top-level child of m_crafting holding m_recipeDecription), Pad around it;
    ///   KitWell_Requirements behind the requirement row (the union of the requirement cells + the station slot), Pad around it;
    /// and a gold caps label KitCaps_Requires ("REQUIRES") just above the requirement well. Rects follow the LIVE rects (measured, never
    /// fixed positions), re-measured at most 4x/s while the panel is shown (inside vanilla's own UpdateRecipe; no timer of ours).
    /// The same panel is the Chunk Seed station's UI (FAT ZoneModeSeeds), so it gets the same sections.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
    internal static class CraftSections
    {
        const float Pad = 8f, CapsH = 18f, CapsGap = 4f;
        static float s_next;
        static string s_lastLog;
        static readonly Vector3[] s_c = new Vector3[4];

        static void Postfix(InventoryGui __instance)
        {
            if (!UnifiedScreen.PanelsMode || !FiresCore.UI.Shell.FiresShell.OverrideOn || Time.unscaledTime < s_next) return;   // Unified UI OFF = untouched
            s_next = Time.unscaledTime + 0.25f;
            var root = __instance.m_crafting;
            if (root == null || !root.gameObject.activeInHierarchy) return;

            // description: its top-level section under the crafting root
            RectTransform desc = TopUnder(root, __instance.m_recipeDecription != null ? __instance.m_recipeDecription.transform : null);
            bool hasDesc = desc != null && desc.gameObject.activeInHierarchy;
            Rect dr = hasDesc ? Bounds(root, desc) : default;
            Well(root, "KitWell_Description", hasDesc, dr, desc);

            // requirements: the cells that are shown + the station slot when shown
            bool any = false; Rect rr = default;
            if (__instance.m_recipeRequirementList != null)
                foreach (var go in __instance.m_recipeRequirementList)
                    if (go != null && go.activeInHierarchy) { var b = Bounds(root, (RectTransform)go.transform); rr = any ? Union(rr, b) : b; any = true; }
            var st = __instance.m_minStationLevelIcon;
            if (any && st != null && st.gameObject.activeInHierarchy) rr = Union(rr, Bounds(root, (RectTransform)st.transform));
            RectTransform firstReq = any ? (RectTransform)__instance.m_recipeRequirementList[0].transform : null;
            Well(root, "KitWell_Requirements", any, rr, TopUnder(root, firstReq));
            Caps(root, "KitCaps_Requires", "REQUIRES", any, rr);

            string line = $"crafting sections: description {(hasDesc ? $"{dr.width:0}x{dr.height:0} ('{desc.name}')" : "none")}, requirements {(any ? $"{rr.width:0}x{rr.height:0}" : "none")} (KitWell_* / KitCaps_* for Core CraftingSkin)";
            if (line != s_lastLog) { s_lastLog = line; EquipmentPanel.PanelLog?.LogDebug("panels: " + line); }
        }

        internal static RectTransform TopUnder(RectTransform root, Transform t)
        {
            if (t == null || !t.IsChildOf(root) || t == root) return null;
            while (t.parent != null && t.parent != root) t = t.parent;
            return t as RectTransform;
        }

        // a child's drawn rect in the root's local space
        internal static Rect Bounds(RectTransform root, RectTransform rt)
        {
            rt.GetWorldCorners(s_c);
            Vector2 a = root.InverseTransformPoint(s_c[0]), b = root.InverseTransformPoint(s_c[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }
        static Rect Union(Rect a, Rect b) => Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));

        // the named rect, Pad larger than r, placed just BEHIND `behind` (its sibling slot), raycasts off
        internal static void Well(RectTransform root, string name, bool on, Rect r, RectTransform behind)
        {
            var w = root.Find(name) as RectTransform;
            if (!on) { if (w != null && w.gameObject.activeSelf) w.gameObject.SetActive(false); return; }
            if (w == null)
            {
                w = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
                w.SetParent(root, false);
                w.anchorMin = w.anchorMax = w.pivot = new Vector2(0.5f, 0.5f);
                var le = w.gameObject.AddComponent<UnityEngine.UI.LayoutElement>(); le.ignoreLayout = true;
            }
            if (!w.gameObject.activeSelf) w.gameObject.SetActive(true);
            var want = new Rect(r.xMin - Pad, r.yMin - Pad, r.width + 2f * Pad, r.height + 2f * Pad);
            Vector2 centre = want.center - root.rect.center, size = want.size;
            if ((w.anchoredPosition - centre).sqrMagnitude > 0.25f) w.anchoredPosition = centre;
            if ((w.sizeDelta - size).sqrMagnitude > 0.25f) w.sizeDelta = size;
            if (behind != null && behind != w)
            {
                int at = behind.GetSiblingIndex(), mine = w.GetSiblingIndex();
                if (mine != at - 1 && mine != at) w.SetSiblingIndex(mine < at ? at - 1 : at);
            }
        }

        // the caps label sits just above the requirement well, left-aligned with it
        static void Caps(RectTransform root, string name, string text, bool on, Rect r)
        {
            var t = root.Find(name) as RectTransform;
            if (!on) { if (t != null && t.gameObject.activeSelf) t.gameObject.SetActive(false); return; }
            if (t == null)
            {
                t = (RectTransform)new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI)).transform;
                t.SetParent(root, false);
                t.anchorMin = t.anchorMax = new Vector2(0.5f, 0.5f); t.pivot = new Vector2(0f, 0f);
                var tmp = t.GetComponent<TextMeshProUGUI>();
                var font = InventoryGui.instance?.m_recipeName != null ? InventoryGui.instance.m_recipeName.font : null;
                if (font != null) tmp.font = font;
                tmp.text = text; tmp.fontSize = 14f; tmp.raycastTarget = false; tmp.alignment = TextAlignmentOptions.BottomLeft; tmp.enableWordWrapping = false;
                var le = t.gameObject.AddComponent<UnityEngine.UI.LayoutElement>(); le.ignoreLayout = true;
            }
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            Vector2 pos = new Vector2(r.xMin - Pad, r.yMax + Pad + CapsGap) - root.rect.center;
            if ((t.anchoredPosition - pos).sqrMagnitude > 0.25f) t.anchoredPosition = pos;
            var size = new Vector2(Mathf.Max(120f, r.width), CapsH);
            if ((t.sizeDelta - size).sqrMagnitude > 0.25f) t.sizeDelta = size;
            t.SetAsLastSibling();
        }
    }
}
