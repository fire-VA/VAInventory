using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // The character sheet, shown as a tab inside the vanilla Crafting panel.
    //
    // Same mechanism FATI uses, deliberately: clone m_tabUpgrade, hide Crafting/RecipeList and
    // Crafting/Decription while active, and cross-hook every other button in the row so the tabs turn each
    // other off. Matching it means FATI and this cooperate instead of both thinking they own the panel -
    // FATI's own HookOtherTabs will find this tab on its next sweep and wire it the same way.
    internal static class StatsPanel
    {
        const string TabId = "vainventory.character";

        static RectTransform view;
        static Transform content;
        static GameObject rowTemplate;
        static GameObject headerTemplate;
        static readonly List<GameObject> spawned = [];

        internal static void Reset()
        {
            view = null;
            content = null;
            rowTemplate = null;
            headerTemplate = null;
            spawned.Clear();
        }

        // Registered once at startup. The registry replays it whenever the screen opens, so nothing here
        // needs the inventory to exist yet.
        internal static void RegisterTab()
        {
            // UnifiedScreen calls this on every open. A mod that took the tab over as a launcher keeps it: with FiresRPGClasses
            // installed, Character opens the full Classes window on its Character board (Fire, popup 2026-09-29).
            if (InventoryTabs.HasLauncher(TabId)) return;
            InventoryTabs.Register(new InventoryTabs.Tab
            {
                Id = TabId,
                Label = "Character",
                Order = 10,
                Build = BuildInto,
                OnVisibility = shown => { if (shown) Populate(); }
            });
        }

        // The prefab's StatsView is authored inside CraftingExtras so it can be laid out in Unity. Move it
        // into the view the registry hands us rather than building a second copy in code.
        static void BuildInto(RectTransform parent)
        {
            var authored = parent.parent != null ? parent.parent.Find("StatsView") as RectTransform : null;
            if (authored == null)
            {
                Debug.LogWarning("[VAInventory] stats: no authored StatsView under "
                    + (parent.parent != null ? parent.parent.name : "<no parent>")
                    + " - the tab will be empty. Is CraftingExtras mounted?");
                return;
            }

            authored.SetParent(parent, false);
            authored.anchorMin = Vector2.zero;
            authored.anchorMax = Vector2.one;
            authored.offsetMin = Vector2.zero;
            authored.offsetMax = Vector2.zero;
            authored.gameObject.SetActive(true);

            view = authored;
            content = view.Find("Content");
            rowTemplate = content?.Find("RowTemplate")?.gameObject;
            headerTemplate = content?.Find("HeaderTemplate")?.gameObject;

            // The prefab ships sample rows so the panel is not blank while being positioned in Unity.
            // DestroyImmediate, not Destroy: deferred destruction leaves them on screen for the rest of the
            // frame, and Populate runs in that same frame - so the placeholders were still there, stacked
            // under the real rows, all reading the template's "Health 92 / 92".
            int samples = 0;
            if (content != null)
            {
                for (int i = content.childCount - 1; i >= 0; i--)
                {
                    var child = content.GetChild(i);
                    if (!child.name.StartsWith("SampleRow_")) continue;
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                    samples++;
                }
            }

            Debug.Log($"[VAInventory] stats built: content={(content != null)} rowTemplate={(rowTemplate != null)} "
                + $"headerTemplate={(headerTemplate != null)} samplesRemoved={samples}");

            BindFont();
        }

        static void BindFont()
        {
            var source = InventoryGui.instance?.m_inventoryRoot
                ?.GetComponentsInChildren<TextMeshProUGUI>(true)
                .FirstOrDefault(t => t.font != null && !t.transform.IsChildOf(view));
            if (source == null || view == null) return;
            foreach (var text in view.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                text.font = source.font;
                text.fontSharedMaterial = source.fontSharedMaterial;
            }
        }

        internal static void Populate()
        {
            var player = Player.m_localPlayer;
            if (player == null || content == null || rowTemplate == null || headerTemplate == null)
            {
                Debug.LogWarning($"[VAInventory] stats NOT populated: player={(player != null)} "
                    + $"content={(content != null)} rowTemplate={(rowTemplate != null)} "
                    + $"headerTemplate={(headerTemplate != null)} - the panel keeps whatever the prefab shipped.");
                return;
            }

            // Populate re-runs on every re-show and refills below in this same call; deferred Destroy would
            // leave the previous rows drawing underneath the new ones at identical positions.
            foreach (var old in spawned) if (old != null) UnityEngine.Object.DestroyImmediate(old);
            spawned.Clear();

            var title = view.Find("Title")?.GetComponent<TextMeshProUGUI>();
            if (title != null) title.text = player.GetPlayerName();

            var inventory = player.GetInventory();

            Header("Attributes");
            Row("Health", Pair(player.GetHealth(), player.GetMaxHealth()));
            Row("Stamina", Pair(player.GetStamina(), player.GetMaxStamina()));
            if (player.GetMaxEitr() > 0f) Row("Eitr", Pair(player.GetEitr(), player.GetMaxEitr()));
            Row("Armor", player.GetBodyArmor().ToString("0"));
            if (inventory != null)
                Row("Weight", Pair(inventory.GetTotalWeight(), player.GetMaxCarryWeight()));

            // The profile holds the lifetime counters; it is absent on the very first frames of a load.
            var profile = Game.instance?.GetPlayerProfile();
            if (profile == null) return;

            Header("General");
            Row("Deaths", Stat(profile, PlayerStatType.Deaths));
            Row("Builds", Stat(profile, PlayerStatType.Builds));
            Row("Crafts", Stat(profile, PlayerStatType.Crafts));
            Row("World Loads", Stat(profile, PlayerStatType.WorldLoads));

            Header("Combat");
            Row("Hits", Stat(profile, PlayerStatType.EnemyHits));
            Row("Kills", Stat(profile, PlayerStatType.EnemyKills));
            Row("Hits Taken", Stat(profile, PlayerStatType.HitsTakenEnemies));
            Row("PvP Kills", Stat(profile, PlayerStatType.PlayerKills));
        }

        static string Stat(PlayerProfile profile, PlayerStatType type)
        {
            try { return profile.GetStat(type).ToString("N0"); }
            catch { return "-"; }
        }

        static string Pair(float current, float max) =>
            current.ToString("0") + " / " + max.ToString("0");

        static void Header(string text)
        {
            var go = UnityEngine.Object.Instantiate(headerTemplate, content);
            go.SetActive(true);
            var label = go.GetComponent<TextMeshProUGUI>();
            if (label != null) label.text = text;
            spawned.Add(go);
        }

        static void Row(string name, string value)
        {
            var go = UnityEngine.Object.Instantiate(rowTemplate, content);
            go.SetActive(true);
            var label = go.transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
            var amount = go.transform.Find("Value")?.GetComponent<TextMeshProUGUI>();
            // A missing child here is why every row would read the template's placeholder instead of data.
            if (label == null || amount == null)
                Debug.LogWarning($"[VAInventory] stats row '{name}': Label={(label != null)} Value={(amount != null)}"
                    + " - row will show the prefab's placeholder text.");
            if (label != null) label.text = name;
            if (amount != null) amount.text = value;
            spawned.Add(go);
        }
    }
}
