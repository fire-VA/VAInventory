using System;
using System.Collections.Generic;
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
        const string TabName = "VAStats";
        const float TabPitch = 107f;   // FATI's spacing, so the row stays even

        static Button tab;
        static RectTransform view;
        static Transform content;
        static GameObject rowTemplate;
        static GameObject headerTemplate;
        static readonly List<GameObject> spawned = [];

        internal static bool Active { get; private set; }

        internal static void Reset()
        {
            if (tab != null) UnityEngine.Object.Destroy(tab.gameObject);
            tab = null;
            view = null;
            content = null;
            rowTemplate = null;
            headerTemplate = null;
            spawned.Clear();
            Active = false;
        }

        internal static void EnsureTab(RectTransform craftingExtras)
        {
            var gui = InventoryGui.instance;
            if (gui == null || craftingExtras == null) return;

            view = craftingExtras.Find("StatsView") as RectTransform;
            if (view == null) return;
            content = view.Find("Content");
            rowTemplate = content?.Find("RowTemplate")?.gameObject;
            headerTemplate = content?.Find("HeaderTemplate")?.gameObject;

            if (tab != null) { HookSiblings(); return; }
            if (gui.m_tabUpgrade == null) return;

            tab = UnityEngine.Object.Instantiate(gui.m_tabUpgrade, gui.m_tabUpgrade.transform.parent);
            tab.gameObject.SetActive(true);
            tab.name = TabName;
            tab.onClick.RemoveAllListeners();
            tab.onClick.AddListener(Show);

            var label = tab.transform.Find("Text")?.GetComponent<TextMeshProUGUI>();
            if (label != null) label.text = "Character";

            HookSiblings();
            Hide();
        }

        // Every other button in the row turns this view off, which is how vanilla's Craft/Upgrade and FATI's
        // own tab stay mutually exclusive with it.
        static void HookSiblings()
        {
            if (tab == null) return;
            foreach (var button in tab.transform.parent.GetComponentsInChildren<Button>(true))
            {
                if (button == tab) continue;
                button.onClick.RemoveListener(Hide);
                button.onClick.AddListener(Hide);
            }
        }

        internal static void Show()
        {
            var gui = InventoryGui.instance;
            if (gui == null || view == null) return;

            Active = true;
            if (tab != null) tab.interactable = false;
            gui.m_inventoryRoot.Find("Crafting/RecipeList")?.gameObject.SetActive(false);
            gui.m_inventoryRoot.Find("Crafting/Decription")?.gameObject.SetActive(false);
            gui.m_inventoryRoot.Find("Crafting/RepairButton")?.gameObject.SetActive(false);
            gui.m_inventoryRoot.Find("Crafting/RepairSimple")?.gameObject.SetActive(false);

            view.gameObject.SetActive(true);
            Populate();
            PositionTab();

            if (gui.m_tabCraft != null) gui.m_tabCraft.interactable = true;
            if (gui.m_tabUpgrade != null) gui.m_tabUpgrade.interactable = true;
        }

        internal static void Hide()
        {
            Active = false;
            if (tab != null) tab.interactable = true;
            if (view != null) view.gameObject.SetActive(false);
        }

        // Sits after the last visible tab, so it does not overlap FATI's when both are installed.
        static void PositionTab()
        {
            if (tab == null) return;
            var rect = (RectTransform)tab.transform;
            for (int i = rect.GetSiblingIndex() - 1; i >= 0; i--)
            {
                var sibling = rect.parent.GetChild(i);
                if (!sibling.gameObject.activeSelf) continue;
                rect.anchoredPosition = ((RectTransform)sibling).anchoredPosition + new Vector2(TabPitch, 0f);
                break;
            }
        }

        internal static void Populate()
        {
            var player = Player.m_localPlayer;
            if (player == null || content == null || rowTemplate == null || headerTemplate == null) return;

            foreach (var old in spawned) if (old != null) UnityEngine.Object.Destroy(old);
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
            if (label != null) label.text = name;
            if (amount != null) amount.text = value;
            spawned.Add(go);
        }
    }
}
