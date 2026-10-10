using System;
using System.Collections.Generic;
using System.Linq;
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

        const float TabPitch = 107f;   // FATI's spacing, so a mixed row stays even
        const float RowEndMargin = 10f;
        const float MinTabScale = 0.5f;
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

        // Lay the tabs out after the last visible sibling that is not one of ours, then step along. Keeps a
        // row containing vanilla's two, FATI's and any number of these evenly spaced. When the row would run
        // past the Crafting panel's right edge (off the screen, since Crafting is pinned to it), our tabs
        // shrink together until they fit; vanilla's and other mods' tabs are never moved.
        static void Reflow()
        {
            var ordered = registered.OrderBy(t => t.Order).ThenBy(t => t.Label)
                                    .Where(t => buttons.ContainsKey(t.Id) && buttons[t.Id] != null)
                                    .ToList();
            if (ordered.Count == 0) return;

            var parent = buttons[ordered[0].Id].transform.parent;
            float rightmost = float.MinValue;
            foreach (Transform sibling in parent)
            {
                if (!sibling.gameObject.activeSelf) continue;
                if (buttons.Values.Any(b => b != null && b.transform == sibling)) continue;
                if (sibling is RectTransform rect) rightmost = Mathf.Max(rightmost, rect.anchoredPosition.x);
            }
            if (rightmost == float.MinValue) rightmost = 0f;

            var first = (RectTransform)buttons[ordered[0].Id].transform;
            float width = first.rect.width;
            float pivot = first.pivot.x;
            float firstLeft = rightmost + TabPitch - pivot * width;
            float span = (ordered.Count - 1) * TabPitch + width;
            float room = RowLimit(parent as RectTransform, first) - firstLeft;
            float scale = room >= span || float.IsNaN(room) || float.IsInfinity(room) ? 1f : Mathf.Max(MinTabScale, room / span);

            for (int i = 0; i < ordered.Count; i++)
            {
                var rect = (RectTransform)buttons[ordered[i].Id].transform;
                rect.localScale = new Vector3(scale, scale, 1f);
                rect.anchoredPosition = new Vector2(firstLeft + pivot * width * scale + TabPitch * scale * i, rect.anchoredPosition.y);
            }
            if (scale < 1f)
                Debug.Log($"[VAInventory] tabs: {ordered.Count} shrunk to {scale:0.00} to stay inside Crafting"
                    + (room / span < MinTabScale ? $" (floor {MinTabScale}, the row still overflows)" : ""));
        }

        // The Crafting panel's right edge, less a margin, in the same anchored-position space as a tab button.
        static float RowLimit(RectTransform row, RectTransform tab)
        {
            var crafting = InventoryGui.instance?.m_inventoryRoot?.Find("Crafting") as RectTransform;
            if (row == null || crafting == null) return float.MaxValue;
            crafting.GetWorldCorners(corners);
            float craftingRight = row.InverseTransformPoint(corners[TopRightCorner]).x;
            float anchorX = Mathf.Lerp(row.rect.xMin, row.rect.xMax, tab.anchorMin.x);
            return craftingRight - RowEndMargin - anchorX;
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
}
