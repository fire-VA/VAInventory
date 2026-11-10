using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// It is vanilla's
    /// station-level slot (InventoryGui.m_minStationLevelIcon + m_minStationLevelText). UpdateRecipe shows the icon only when the recipe
    /// needs a crafting station and writes just the level number; the cell behind it stayed drawn with nothing in it otherwise.
    /// Under Panels: a needed station = its own icon (CraftingStation.m_icon) and "Workbench 2" (name + level); none = the cell is hidden.
    /// Layout/content only; the look (skin) of the crafting panel stays Core's CraftingSkin.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
    internal static class CraftStationSlot
    {
        static string s_lastLog, s_lastRaw, s_lastWant; static CraftingStation s_lastStation;   // 1.0.358 V7 cache

        static void Postfix(InventoryGui __instance)
        {
            if (!UnifiedScreen.PanelsMode) return;
            var icon = __instance.m_minStationLevelIcon;
            var text = __instance.m_minStationLevelText;
            if (icon == null) return;
            Transform cell = CellOf(__instance, icon.transform);
            bool needed = icon.gameObject.activeSelf;
            if (cell != null && cell.gameObject.activeSelf != needed) cell.gameObject.SetActive(needed);
            if (!needed) { s_lastStation = null; Log("hidden (the recipe needs no station)", cell); return; }

            var recipe = __instance.m_selectedRecipe.Recipe;
            CraftingStation st = recipe != null ? recipe.m_craftingStation : null;
            if (st == null) return;
            if (st.m_icon != null && icon.sprite != st.m_icon) { icon.sprite = st.m_icon; icon.preserveAspect = true; }
            // 1.0.358 (UI efficiency audit V7): vanilla rewrites the level each frame; the station name + level line (2 Localize, a parse, the
            // log string) is built once per (station, level text), and an unchanged frame returns here
            string raw = text != null ? text.text : null;
            if (st == s_lastStation && raw != null && (raw == s_lastRaw || raw == s_lastWant)) { if (text.text != s_lastWant) text.text = s_lastWant; return; }
            if (text != null)
            {
                string level = raw;
                int n; if (!int.TryParse(level, out n)) { int sp = level.LastIndexOf(' '); if (sp >= 0) int.TryParse(level.Substring(sp + 1), out n); }
                string want = Localization.instance.Localize(st.m_name) + (n > 0 ? " " + n : "");
                if (text.text != want) { text.text = want; text.enableWordWrapping = true; text.enableAutoSizing = true; text.fontSizeMin = 8f; }
                s_lastStation = st; s_lastRaw = raw; s_lastWant = want;
            }
            Log($"'{Localization.instance.Localize(st.m_name)}' icon + name + level", cell);
        }

        // The slot's own cell: the nearest parent of the icon that is NOT the panel holding the requirement list (so a shared container
        // is never hidden). None found = only the icon toggles (vanilla behaviour).
        static Transform CellOf(InventoryGui gui, Transform icon)
        {
            Transform reqParent = gui.m_recipeRequirementList != null && gui.m_recipeRequirementList.Length > 0 && gui.m_recipeRequirementList[0] != null
                ? gui.m_recipeRequirementList[0].transform.parent : null;
            var p = icon.parent;
            if (p == null || p == reqParent || p == gui.m_crafting) return null;
            foreach (var r in gui.m_recipeRequirementList) if (r != null && r.transform.IsChildOf(p)) return null;
            return p;
        }

        static void Log(string what, Transform cell)
        {
            string line = $"crafting station slot: {what} (cell '{(cell != null ? cell.name : "none: icon only")}')";
            if (line == s_lastLog) return;
            s_lastLog = line;
            EquipmentPanel.PanelLog?.LogDebug("panels: " + line);
        }
    }
}
