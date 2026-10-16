using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// The tab row inside the vanilla Crafting panel, and the public way other mods put a view there.
    ///
    /// One registry, so a shortcut to the classes screen, a leaderboard, a backpack grid or a transmog view
    /// are all registrations rather than each mod inventing its own tab and fighting over the row.
    ///
    /// It follows FATI's mechanism on purpose - clone m_tabUpgrade, hide Crafting/RecipeList and
    /// Crafting/Decription while a tab is active, cross-hook the sibling buttons so they turn each other
    /// off. FATI's own HookOtherTabs sweep finds these tabs and wires them the same way, so FATI and these
    /// cooperate instead of both believing they own the panel.
    ///
    /// REGISTERING FROM ANOTHER MOD, without taking a hard reference on VAInventory:
    ///
    ///   var type = AccessTools.TypeByName("VerdantsAscent.Managers.InventoryTabs");
    ///   type?.GetMethod("Register", new[] { typeof(string), typeof(string), typeof(Action&lt;RectTransform&gt;), typeof(Func&lt;bool&gt;) })
    ///        ?.Invoke(null, new object[] { "firesrpgclasses", "Classes", (Action&lt;RectTransform&gt;)Build, null });
    ///
    /// The REGISTRY is order-free, but RESOLVING it by reflection is not: AccessTools.TypeByName only sees
    /// assemblies already loaded, and VAInventory loads LAST (measured: consumers at log lines 81/205/218,
    /// VAInventory at 280). Calling from your own Awake returns null and registers nothing. Add
    /// [BepInDependency("com.Fire.VAInventory", DependencyFlags.SoftDependency)] or resolve from an
    /// InventoryGui.Awake postfix. Registrations themselves are replayed on the next inventory open.
    /// </summary>
    public static class InventoryTabs
    {
        public sealed class Tab
        {
            /// <summary>Stable, unique. Re-registering the same id replaces the previous entry.</summary>
            public string Id;
            public string Label;
            /// <summary>Optional. Return false to leave the tab out - e.g. the owning mod is not loaded.</summary>
            public Func<bool> IsAvailable;
            /// <summary>
            /// Set this and the tab becomes a LAUNCHER: clicking it opens the owning mod's own full-size UI
            /// and nothing is drawn inside the crafting panel. This is the right choice for a real screen -
            /// a leaderboard, the classes UI - because clamping one into the crafting panel's 570x650 both
            /// looks wrong and traps the player, since the view covers the tab row it was opened from.
            /// Use Build instead only for content that genuinely belongs in that space, like a bag grid.
            /// </summary>
            public Action Open;

            /// <summary>Called ONCE with an empty full-size parent. Build the view into it.</summary>
            public Action<RectTransform> Build;
            /// <summary>Optional. Called with true when shown, false when hidden.</summary>
            public Action<bool> OnVisibility;
            /// <summary>Lower sorts left. Vanilla's Craft/Upgrade sit before all of these.</summary>
            public int Order;
        }

        static readonly List<Tab> registered = [];
        static readonly Dictionary<string, Button> buttons = [];
        static readonly Dictionary<string, RectTransform> views = [];

        const int TopRightCorner = 2;
        static readonly Vector3[] corners = new Vector3[4];

        internal static string ActiveId { get; private set; }

        public static void Register(Tab tab)
        {
            if (tab == null || string.IsNullOrEmpty(tab.Id)) return;
            if (tab.Build == null && tab.Open == null) return;
            registered.RemoveAll(t => t.Id == tab.Id);
            registered.Add(tab);
            // Drop any live UI for this id so the new registration rebuilds cleanly.
            Forget(tab.Id);
        }

        public static void Register(string id, string label, Action<RectTransform> build, Func<bool> isAvailable = null)
            => Register(new Tab { Id = id, Label = label, Build = build, IsAvailable = isAvailable });

        /// <summary>True when a tab with this id is registered as a launcher (it opens another mod's full screen).</summary>
        public static bool HasLauncher(string id) => registered.Any(t => t.Id == id && t.Open != null);

        public static void Unregister(string id)
        {
            registered.RemoveAll(t => t.Id == id);
            Forget(id);
        }

        static void Forget(string id)
        {
            if (buttons.TryGetValue(id, out var button) && button != null) UnityEngine.Object.DestroyImmediate(button.gameObject);
            if (views.TryGetValue(id, out var view) && view != null) UnityEngine.Object.DestroyImmediate(view.gameObject);
            buttons.Remove(id);
            views.Remove(id);
            if (ActiveId == id) ActiveId = null;
        }

        internal static void Reset()
        {
            foreach (var id in buttons.Keys.ToList()) Forget(id);
            ActiveId = null;
        }

        /// <summary>Builds any missing tab buttons. Safe to call on every Show.</summary>
        internal static void Sync(RectTransform craftingExtras)
        {
            var gui = InventoryGui.instance;
            if (gui == null || gui.m_tabUpgrade == null || craftingExtras == null) return;

            foreach (var tab in registered.OrderBy(t => t.Order).ThenBy(t => t.Label))
            {
                bool available;
                try { available = tab.IsAvailable == null || tab.IsAvailable(); }
                catch { available = false; }

                if (!available) { Forget(tab.Id); continue; }
                if (buttons.TryGetValue(tab.Id, out var existing) && existing != null) continue;

                var button = UnityEngine.Object.Instantiate(gui.m_tabUpgrade, gui.m_tabUpgrade.transform.parent);
                button.gameObject.SetActive(true);
                button.name = "VATab_" + tab.Id;
                button.onClick.RemoveAllListeners();
                var id = tab.Id;
                button.onClick.AddListener(() => Show(id));

                var label = button.transform.Find("Text")?.GetComponent<TextMeshProUGUI>();
                if (label != null) label.text = tab.Label ?? tab.Id;

                buttons[tab.Id] = button;
            }

            HookSiblings();
            Reflow();

            Debug.Log($"[VAInventory] tabs: registered={registered.Count} built={buttons.Count} "
                + $"[{string.Join(", ", buttons.Keys)}] active={ActiveId ?? "<none>"}");
        }

        // Every button in the row that is not ours turns our views off, which is how vanilla's Craft and
        // Upgrade - and FATI's tab - stay mutually exclusive with these.
        static void HookSiblings()
        {
            var gui = InventoryGui.instance;
            if (gui == null || gui.m_tabUpgrade == null) return;

            foreach (var button in gui.m_tabUpgrade.transform.parent.GetComponentsInChildren<Button>(true))
            {
                if (buttons.Values.Contains(button)) continue;
                button.onClick.RemoveListener(HideAll);
                button.onClick.AddListener(HideAll);
            }
        }

        // Fire 10-12 (option B): EVERY tab in the row - vanilla's Craft and Upgrade, FATI's, these - one size, one font, Title Case,
        // spread evenly across the Crafting panel's inner width with the same margin left of the first and right of the last
        // (Fire 10-12, on 1.0.114: room to spare left of Craft, Leaderboards crowding the right frame). Fewer tabs, wider tabs. FATI moves its tab
        // on every UpdateCraftingPanel and on its clicks, so this runs after FATI there and re-checks every InventoryGui.Update;
        // the sizes are worked out only when the tab set or the panel width changes, otherwise it only puts moved tabs back.
        const float TabGap = 7f;
        const float FrameInset = 10f;   // the Crafting frame's border, inside its rect
        const float EdgeGap = 11f;      // from that border to the first / last tab, the same on both sides
        const float LabelPad = 10f;
        static readonly HashSet<string> KeepCaps = new(StringComparer.Ordinal) { "FATI" };
        static readonly List<RectTransform> rowTabs = [];
        static readonly List<float> rowTargetX = [];
        static readonly List<TMP_Text> rowLabels = [];
        static readonly List<string> rowTexts = [];
        static string rowKey;
        static int rowQuick = -1;
        static int rowGuiId;
        static float rowLeft = float.NaN, rowWidth, rowHeight, rowFont, vanillaFont = -1f;

        static void Reflow() => LayoutRow(true);

        /// <summary>The live row, for the test: each tab's rect in the Crafting panel's space and its label's font size.</summary>
        internal static IReadOnlyList<RectTransform> RowTabs => rowTabs;
        internal static float RowFont => rowFont;

        internal static void LayoutRow(bool force)
        {
            var gui = InventoryGui.instance;
            if (gui == null || gui.m_tabCraft == null || gui.m_tabUpgrade == null) return;
            var craft = (RectTransform)gui.m_tabCraft.transform;
            var row = craft.parent as RectTransform;
            var crafting = gui.m_inventoryRoot?.Find("Crafting") as RectTransform;
            if (row == null || crafting == null || !crafting.gameObject.activeInHierarchy) return;
            if (!Mathf.Approximately(craft.anchorMin.x, craft.anchorMax.x)) return;   // a stretched tab has no width of its own to set

            if (rowGuiId != gui.GetInstanceID()) { rowGuiId = gui.GetInstanceID(); rowLeft = float.NaN; vanillaFont = -1f; rowKey = null; rowQuick = -1; }
            float anchorX = Mathf.Lerp(row.rect.xMin, row.rect.xMax, craft.anchorMin.x);
            crafting.GetWorldCorners(corners);
            rowLeft = row.InverseTransformPoint(corners[0]).x + FrameInset + EdgeGap;
            float right = row.InverseTransformPoint(corners[TopRightCorner]).x - FrameInset - EdgeGap;

            // Most frames nothing changed: same children shown, same width. Then only put moved tabs back, with no allocation.
            int shown = 0;
            foreach (Transform child in row) if (child.gameObject.activeSelf) shown++;
            int quick = ((shown * 397) ^ row.childCount) * 397 ^ Mathf.RoundToInt((right - rowLeft) * 10f);
            if (!force && quick == rowQuick && rowTabs.Count > 0) { PutBack(craft); return; }
            rowQuick = quick;

            // The row: active buttons on the Craft tab's line, vanilla's two first, other mods' in sibling order, these in registry order.
            var ours = registered.OrderBy(t => t.Order).ThenBy(t => t.Label)
                                 .Where(t => buttons.TryGetValue(t.Id, out var b) && b != null && b.gameObject.activeSelf)
                                 .Select(t => (RectTransform)buttons[t.Id].transform).ToList();
            var tabs = new List<RectTransform>();
            foreach (Transform child in row)
            {
                if (!child.gameObject.activeSelf || !(child is RectTransform rect) || ours.Contains(rect) || child.GetComponent<Button>() == null) continue;
                if (Mathf.Abs(rect.anchoredPosition.y - craft.anchoredPosition.y) > 2f || rect.anchorMin != craft.anchorMin) continue;
                tabs.Add(rect);
            }
            tabs = tabs.OrderBy(r => r == craft ? 0 : r == (RectTransform)gui.m_tabUpgrade.transform ? 1 : 2).ThenBy(r => r.GetSiblingIndex()).ToList();
            tabs.AddRange(ours);
            if (tabs.Count == 0) return;

            string key = string.Join(",", tabs.Select(t => t.GetInstanceID().ToString()).ToArray()) + "|" + Mathf.Round(right - rowLeft);
            if (force || key != rowKey)
            {
                rowKey = key;
                rowTabs.Clear(); rowTabs.AddRange(tabs);
                rowWidth = (right - rowLeft - (tabs.Count - 1) * TabGap) / tabs.Count;
                rowHeight = craft.rect.height;
                rowTargetX.Clear();
                for (int i = 0; i < tabs.Count; i++) rowTargetX.Add(rowLeft + i * (rowWidth + TabGap) + craft.pivot.x * rowWidth - anchorX);

                // One font: vanilla Craft's size, or smaller when the widest Title Case label would not fit.
                var labels = tabs.Select(t => t.GetComponentInChildren<TMP_Text>(true)).ToList();
                if (vanillaFont < 0f) vanillaFont = labels[0] != null ? labels[0].fontSize : 18f;
                float fit = 1f;
                foreach (var label in labels)
                {
                    if (label == null) continue;
                    TitleCase(label);
                    label.enableAutoSizing = false;
                    float wide = label.GetPreferredValues(label.text, 9999f, 9999f).x * vanillaFont / Mathf.Max(1f, label.fontSize);
                    if (wide > 0f) fit = Mathf.Min(fit, (rowWidth - LabelPad) / wide);
                }
                rowFont = vanillaFont * fit;
                foreach (var label in labels) if (label != null) label.fontSize = rowFont;
                rowLabels.Clear(); rowLabels.AddRange(labels);
                rowTexts.Clear(); rowTexts.AddRange(labels.Select(l => l != null ? l.text : null));

                Debug.Log($"[VAInventory] tab row: {tabs.Count} tabs [{string.Join(", ", labels.Select(l => l != null ? l.text : "?").ToArray())}] "
                    + $"each {rowWidth:0}x{rowHeight:0}, font {rowFont:0.#} (vanilla {vanillaFont:0.#}), x {rowLeft:0}..{right:0} in '{row.name}'");
            }
            PutBack(craft);
        }

        // Every tab back at its slot, size and Title Case label; cheap when nothing moved.
        static void PutBack(RectTransform craft)
        {
            for (int i = 0; i < rowTabs.Count; i++)
            {
                var rect = rowTabs[i];
                if (rect == null) { rowKey = null; rowQuick = -1; return; }
                var label = rowLabels[i];
                if (label != null && !ReferenceEquals(label.text, rowTexts[i]) && label.text != rowTexts[i])
                {   // something (localization, the owning mod) put a new label back: Title Case and the row font again
                    TitleCase(label); label.fontSize = rowFont; rowTexts[i] = label.text;
                }
                var want = new Vector2(rowTargetX[i], craft.anchoredPosition.y);
                if ((rect.anchoredPosition - want).sqrMagnitude < 0.25f && Mathf.Abs(rect.sizeDelta.x - rowWidth) < 0.5f && rect.localScale == Vector3.one) continue;
                rect.pivot = craft.pivot;
                rect.localScale = Vector3.one;
                rect.sizeDelta = new Vector2(rowWidth, rowHeight);
                rect.anchoredPosition = want;
            }
        }

        // "CRAFT" -> "Craft"; FATI stays an acronym. TMP's UpperCase style would undo it, so that goes too.
        static void TitleCase(TMP_Text label)
        {
            label.fontStyle &= ~FontStyles.UpperCase;
            string text = label.text ?? "";
            var words = text.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                if (w.Length < 2 || KeepCaps.Contains(w) || w != w.ToUpperInvariant()) continue;
                words[i] = w.Substring(0, 1) + w.Substring(1).ToLowerInvariant();
            }
            string titled = string.Join(" ", words);
            if (titled != text) label.text = titled;
        }

        internal static void Show(string id)
        {
            var gui = InventoryGui.instance;
            var tab = registered.FirstOrDefault(t => t.Id == id);
            if (gui == null || tab == null) return;

            // A LAUNCHER tab hands off to the owning mod and touches nothing here: vanilla's recipe list
            // stays as it was, no view is built, and the tab row stays clickable. Fire: "these buttons are
            // supposed to open up the actual ui they are attached to, not clamp it to the size of the
            // crafting menue".
            if (tab.Open != null)
            {
                HideAll();
                try { tab.Open(); }
                catch (Exception exception)
                {
                    Debug.LogError($"[VAInventory] tab '{id}' failed to open: {exception}");
                }
                return;
            }

            var extras = CraftingExtras();
            if (extras == null) return;

            HideAll();
            extras.gameObject.SetActive(true);
            ActiveId = id;

            gui.m_inventoryRoot.Find("Crafting/RecipeList")?.gameObject.SetActive(false);
            gui.m_inventoryRoot.Find("Crafting/Decription")?.gameObject.SetActive(false);
            gui.m_inventoryRoot.Find("Crafting/RepairButton")?.gameObject.SetActive(false);
            gui.m_inventoryRoot.Find("Crafting/RepairSimple")?.gameObject.SetActive(false);

            if (!views.TryGetValue(id, out var view) || view == null)
            {
                var go = new GameObject("VAView_" + id, typeof(RectTransform));
                go.layer = 5;
                view = (RectTransform)go.transform;
                view.SetParent(extras, false);
                view.anchorMin = Vector2.zero;
                view.anchorMax = Vector2.one;
                view.offsetMin = Vector2.zero;
                view.offsetMax = Vector2.zero;
                views[id] = view;

                // A throwing tab must not take the screen down with it.
                try { tab.Build(view); }
                catch (Exception exception)
                {
                    Debug.LogError($"[VAInventory] tab '{id}' failed to build: {exception}");
                }
            }

            view.gameObject.SetActive(true);
            if (buttons.TryGetValue(id, out var button) && button != null) button.interactable = false;
            try { tab.OnVisibility?.Invoke(true); } catch { }

            // Anything ELSE still drawn inside Crafting is another mod's view that did not hear us. We only
            // own RecipeList/Decription and our own views; a foreign view staying up means its owner never
            // hooked our button, which is a cooperation failure rather than a layout one.
            ReportForeignViews(id, extras);

            if (gui.m_tabCraft != null) gui.m_tabCraft.interactable = true;
            if (gui.m_tabUpgrade != null) gui.m_tabUpgrade.interactable = true;
        }

        internal static void HideAll()
        {
            foreach (var pair in views)
            {
                if (pair.Value != null) pair.Value.gameObject.SetActive(false);
                if (pair.Key == ActiveId)
                {
                    var tab = registered.FirstOrDefault(t => t.Id == pair.Key);
                    try { tab?.OnVisibility?.Invoke(false); } catch { }
                }
            }
            foreach (var button in buttons.Values)
                if (button != null) button.interactable = true;
            ActiveId = null;

            var extras = CraftingExtras();
            if (extras != null) extras.gameObject.SetActive(false);
        }

        static void ReportForeignViews(string shownId, RectTransform extras)
        {
            var crafting = InventoryGui.instance?.m_inventoryRoot?.Find("Crafting");
            if (crafting == null) return;

            var ours = new HashSet<string> { "Bkg", "Darken", "selected_frame", "topic", "TitlePanel",
                "BraidLineHorisontalMedium", "station_icon", "TabsButtons", "Level", "RecipeList",
                "Decription", "RepairButton", "RepairSimple", "CraftingExtras" };

            var foreign = new List<string>();
            foreach (Transform child in crafting)
                if (child.gameObject.activeSelf && !ours.Contains(child.name)) foreign.Add(child.name);

            if (foreign.Count > 0)
                Debug.LogWarning($"[VAInventory] tab '{shownId}' shown, but these are STILL VISIBLE inside "
                    + $"Crafting and are not ours: {string.Join(", ", foreign)}. Expect overlap. Their owner "
                    + "should hide them when a sibling tab is clicked.");
        }

        static RectTransform CraftingExtras()
        {
            var crafting = InventoryGui.instance?.m_inventoryRoot?.Find("Crafting");
            return crafting?.Find("CraftingExtras") as RectTransform;
        }
    }

    // The tab row's layout, after FATI's own UpdateCraftingPanel postfix (it puts its tab back at sibling + 107 there), and
    // every InventoryGui.Update while the inventory is open (FATI also moves its tab on clicks). Both only put moved tabs back
    // unless the tab set or the panel width changed.
    [HarmonyPatch(typeof(InventoryGui), "UpdateCraftingPanel")]
    static class TabRowAfterCraftingPanel
    {
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyAfter("com.Fire.FiresAllTheItems")]
        static void Postfix() => InventoryTabs.LayoutRow(false);
    }

    [HarmonyPatch(typeof(InventoryGui), "Update")]
    static class TabRowEveryFrame
    {
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyAfter("com.Fire.FiresAllTheItems")]
        static void Postfix()
        {
            if (InventoryGui.IsVisible()) InventoryTabs.LayoutRow(false);
        }
    }
}
