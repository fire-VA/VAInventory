using HarmonyLib;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// The Companions tab ("firescompanions.roster")
    /// opened its roster by calling InventoryGui.Hide(), and an empty dark panel with two rows of pills stayed far right where the crafting
    /// column was - VAInventory's tab view (VAView_&lt;id&gt; under Crafting/CraftingExtras) was still active, because HideAll only ran from
    /// our own tab buttons, never on a close. Now every InventoryGui.Hide with a tab view open runs HideAll and gives the crafting column its
    /// recipe list / description / repair buttons back, so the next open starts on plain Crafting.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
    internal static class TabsOnInventoryHide
    {
        static void Postfix(InventoryGui __instance)
        {
            if (InventoryTabs.ActiveId == null) return;
            string was = InventoryTabs.ActiveId;
            InventoryTabs.HideAll();
            var root = __instance.m_inventoryRoot;
            if (root != null)
                foreach (var name in new[] { "Crafting/RecipeList", "Crafting/Decription", "Crafting/RepairButton", "Crafting/RepairSimple" })
                    root.Find(name)?.gameObject.SetActive(true);
            EquipmentPanel.PanelLog?.LogDebug($"tabs: inventory closed with tab '{was}' open - its view hidden, crafting restored");
        }
    }
}
