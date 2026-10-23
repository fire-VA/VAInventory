using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using FiresCore.UI.Shell.Kit;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// The companion panel's pills and Stats page drawn with Core's own kit
    /// widgets (Widgets.SubTabs / Section / Row / Button), laid out like FiresCompanions' stats + attributes screens
    /// . Kit types live only in this class, so VAInventory loads
    /// without them; callers check Available first.
    /// </summary>
    internal static class CompanionKitPage
    {
        static bool s_checked, s_available;
        internal static bool Available
        {
            get
            {
                if (!s_checked) { s_checked = true; s_available = Type.GetType("FiresCore.UI.Shell.Kit.Widgets, FiresUnifiedCore", false) != null; }
                return s_available;
            }
        }

        internal sealed class Attr { public string Name; public int Value; public int Max; public string Effect; }
        internal sealed class Model
        {
            public string Subtitle;
            public List<(string section, List<(string label, string value)> rows)> Left = new List<(string, List<(string, string)>)>();
            public List<(string section, List<(string label, string value)> rows)> Right = new List<(string, List<(string, string)>)>();
            public List<Attr> Attributes;   // null = no attribute API
            public int Points;
            public Action<string> Spend;
            public Action Reset;
        }

        /// <summary>Small rounded Equipment | Stats pills (the Dressing room's), centred at y (from the parent's top). Returns their group.</summary>
        static bool s_pillsLogged;
        internal static RectTransform Pills(RectTransform parent, float y, bool statsOn, Action<bool> onPick, Action onAppearance = null, RectTransform under = null)
        {
            var root0 = PillsRow(parent, y, statsOn, onPick, onAppearance);
            if (under != null && root0 != null) Column(parent, root0, under);
            return root0;
        }

        // The pills stack in a column under the Armor label, left of the preview, so each has room for its text; each is as wide as
        // the widest label, every face a rounded kit pill like everything else.
        static void Column(RectTransform parent, RectTransform root, RectTransform under)
        {
            var c = new Vector3[4]; under.GetWorldCorners(c);
            Vector3 bl = parent.InverseTransformPoint(c[0]);
            float left = bl.x + parent.rect.width * parent.pivot.x, top = bl.y - parent.rect.height * (1f - parent.pivot.y);   // parent top-left space
            float w = 0f;
            foreach (RectTransform n in root) { var l = n.Find("Label")?.GetComponent<TMPro.TMP_Text>(); w = Mathf.Max(w, l != null ? Mathf.Ceil(l.preferredWidth) + 28f : 110f); }
            const float H = 30f, Gap = 6f;
            root.anchorMin = root.anchorMax = new Vector2(0f, 1f); root.pivot = new Vector2(0f, 1f);
            root.sizeDelta = new Vector2(w, root.childCount * (H + Gap));
            root.anchoredPosition = new Vector2(left, top - 8f);
            int i = 0;
            foreach (RectTransform n in root)
            {
                n.anchorMin = n.anchorMax = new Vector2(0f, 1f); n.pivot = new Vector2(0f, 1f);
                n.sizeDelta = new Vector2(w, H); n.anchoredPosition = new Vector2(0f, -i * (H + Gap)); i++;
                // 
                // every pill in this narrow column carries Core's standard hover tip with its FULL label, so a cut label (any language) reads
                // in full on hover; attached to all of them because the cut depends on the final drawn width, not known here
                var lbl = n.Find("Label")?.GetComponent<TMPro.TMP_Text>();
                string full = lbl != null ? (lbl.text ?? "").Trim() : "";
                // The label shrinks to fit the whole word (down to 9 pt) before
                // it would cut; the hover tip still names it in full
                if (lbl != null && !lbl.enableAutoSizing)
                {
                    float size = lbl.fontSize;
                    lbl.enableWordWrapping = false;
                    lbl.fontSizeMax = size; lbl.fontSizeMin = Mathf.Min(9f, size);
                    lbl.enableAutoSizing = true;                }
                if (full.Length > 0) PillTip(n.gameObject, full);
                foreach (var name in new[] { "Fill", "Outline" })
                {
                    var img = n.Find(name)?.GetComponent<Image>();
                    if (img != null && img.sprite == null) { img.sprite = KitSprites.RoundPill(FiresTokens.PillRadius, name == "Outline"); img.type = Image.Type.Sliced; }
                }
            }
            VerdantsAscent.EquipmentPanel.PanelLog?.LogDebug($"panels: companion pills stacked under '{under.name}' at ({left:0},{top - 8f:0}), {root.childCount} x {w:0}x{H:0}");
        }

        // A LABEL tip - shown even when RPGmaker's admin hints are off; an older Core keeps the plain tip
        private static void PillTip(GameObject go, string full)
        {
            try { LabelTip(go, full); }
            catch (System.MissingMethodException) { FiresCore.UI.FiresHoverTip.Attach(go, full, null); }
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void LabelTip(GameObject go, string full) => FiresCore.UI.FiresHoverTip.AttachLabel(go, full);

        static RectTransform PillsRow(RectTransform parent, float y, bool statsOn, Action<bool> onPick, Action onAppearance)
        {
            Impl.DropGroup(parent, "Pills");
            // A third pill, "Appearance", when
            // the caller gives an appearance action (FiresCompanions 0.1.83+ CompanionBarberScreen.OpenFor); otherwise Gear | Stats as before
            var tabs = onAppearance != null
                ? new[] { ("equipment", "Gear"), ("stats", "Stats"), ("appearance", "Appearance") }
                : new[] { ("equipment", "Gear"), ("stats", "Stats") };
            var root = Widgets.SubTabs(parent, y, tabs, statsOn ? "stats" : "equipment");
            foreach (Transform node in root)
            {
                bool stats = node.name == "stats", appearance = node.name == "appearance";
                var target = node.Find("Outline")?.GetComponent<Graphic>();
                if (target == null) continue;
                target.raycastTarget = true;
                var b = node.gameObject.GetComponent<Button>() ?? node.gameObject.AddComponent<Button>();
                b.targetGraphic = target; b.transition = Selectable.Transition.None;
                b.onClick.RemoveAllListeners();
                if (appearance) b.onClick.AddListener(() => onAppearance());
                else b.onClick.AddListener(() => onPick(stats));
            }
            var le = root.GetComponent<LayoutElement>() ?? root.gameObject.AddComponent<LayoutElement>(); le.ignoreLayout = true;
            // Every pill label
            // gets a font that draws (the inventory's own when the kit's is missing) and a visible colour; one log line says what it found
            var gameFont = InventoryGui.instance?.m_playerGrid?.m_elementPrefab?.GetComponentInChildren<TMPro.TMP_Text>(true)?.font;
            var seen = new System.Text.StringBuilder();
            foreach (var t in root.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                seen.Append($"'{t.text}' font {(t.font != null ? t.font.name : "NONE")} a {t.color.a:0.##} on {t.enabled}; ");
                if (t.font == null && gameFont != null) t.font = gameFont;
                if (t.color.a < 0.1f) { var c = FiresTokens.TextMuted; t.color = c; }
                if (!t.enabled) t.enabled = true;
            }
            Font legacyFont = null;
            foreach (var t in root.GetComponentsInChildren<Text>(true))
            {
                seen.Append($"legacy '{t.text}' font {(t.font != null ? t.font.name : "NONE")} a {t.color.a:0.##} on {t.enabled}; ");
                if (t.font == null)
                {
                    if (legacyFont == null) foreach (var any in UnityEngine.Object.FindObjectsOfType<Text>()) if (any != null && any.font != null) { legacyFont = any.font; break; }
                    if (legacyFont != null) t.font = legacyFont;
                }
                if (t.color.a < 0.1f) t.color = FiresTokens.TextMuted;
                if (!t.enabled) t.enabled = true;
            }
            if (!s_pillsLogged) { s_pillsLogged = true; VerdantsAscent.EquipmentPanel.PanelLog?.LogDebug("panels: companion pill labels: " + (seen.Length > 0 ? seen.ToString() : "NO TMP labels under the pills")); }
            return root;
        }

        /// <summary>The Stats page body: two columns of kit sections, attributes with a + per row when points are free.</summary>
        internal static void Build(RectTransform page, Model m)
        {
            Impl.Build(page, m);
        }

        static class Impl
        {
            internal static void DropGroup(RectTransform parent, string name)
            {
                var old = parent.Find(name);
                if (old == null) return;
                old.gameObject.SetActive(false); old.name = name + "_old";
                UnityEngine.Object.Destroy(old.gameObject);
            }

            internal static void Build(RectTransform page, Model m)
            {
                DropGroup(page, "Content");
                var content = Widgets.Node(page, "Content");
                Widgets.Stretch(content, 0, 0, 0, 0);
                float w = page.rect.width;
                const float pad = 18f, gap = 24f;
                float colW = (w - pad * 2f - gap) * 0.5f;
                float top = 52f;   // under the pills (they sit at the page top on the Stats page)
                if (!string.IsNullOrEmpty(m.Subtitle))
                {
                    var sub = KitText.Create(content, "Subtitle", m.Subtitle, FontRole.SansRegular, FiresTokens.HintSize + 1f, FiresTokens.TextMuted, KitAlign.Center);
                    Widgets.Place(sub.Rect, 0, top, w, 20f);
                    top += 30f;
                }
                float yl = top, yr = top;
                foreach (var (title, rows) in m.Left) yl = Section(content, title, rows, pad, yl, colW);
                if (m.Attributes != null) yl = Attributes(content, m, pad, yl, colW);
                foreach (var (title, rows) in m.Right) yr = Section(content, title, rows, pad + colW + gap, yr, colW);
            }

            static float Section(RectTransform parent, string title, List<(string label, string value)> rows, float x, float y, float width)
            {
                var s = Widgets.Section(parent, title.ToLowerInvariant().Replace(' ', '_'), title, x, y, width);
                int i = 0;
                foreach (var (label, value) in rows) Widgets.Row(s, "r" + i++, label, value);
                return y + s.Y + 14f;
            }

            static float Attributes(RectTransform parent, Model m, float x, float y, float width)
            {
                var s = Widgets.Section(parent, "attributes", "Attributes", x, y, width, m.Points > 0 ? $"{m.Points} point(s) to spend" : null);
                int i = 0;
                foreach (var a in m.Attributes)
                {
                    var row = Widgets.Row(s, "a" + i++, a.Name, a.Max > 0 ? $"{a.Value} / {a.Max}" : a.Value.ToString());
                    bool can = m.Points > 0 && (a.Max <= 0 || a.Value < a.Max);
                    if (!string.IsNullOrEmpty(a.Effect)) row.Label.Value = a.Name + "  " + KitText.Tint(a.Effect, FiresTokens.TextFaint);
                    // the value moves left of a "+" (attributes_1920.png)
                    Widgets.PlaceRight(row.Value.Rect, 34f, 0, width * 0.30f, FiresTokens.RowHeight);
                    var plus = Widgets.Button(row.Root, "plus", "+", can ? ButtonKind.Primary : ButtonKind.Disabled, 0, 0, 26f, 24f);
                    Widgets.PlaceRight(plus, 0, (FiresTokens.RowHeight - 24f) * 0.5f, 26f, 24f);
                    if (can)
                    {
                        var fill = plus.Find("Fill")?.GetComponent<Graphic>();
                        if (fill != null)
                        {
                            fill.raycastTarget = true;
                            var b = plus.gameObject.AddComponent<Button>(); b.targetGraphic = fill; b.transition = Selectable.Transition.None;
                            string name = a.Name;
                            b.onClick.AddListener(() => m.Spend?.Invoke(name));
                        }
                    }
                }
                float end = y + s.Y + 8f;
                if (m.Reset != null)
                {
                    var reset = Widgets.Button(parent, "reset_attributes", "Reset attributes", ButtonKind.Primary, x, end, 0f, 28f);
                    var fill = reset.Find("Fill")?.GetComponent<Graphic>();
                    if (fill != null) { fill.raycastTarget = true; var b = reset.gameObject.AddComponent<Button>(); b.targetGraphic = fill; b.transition = Selectable.Transition.None; b.onClick.AddListener(() => m.Reset()); }
                    end += 40f;
                }
                return end + 6f;
            }
        }
    }
}
