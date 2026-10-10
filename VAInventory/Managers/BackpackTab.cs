using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // Shows the equipped bag's grid in the crafting panel, so the backpack is reachable from the inventory
    // instead of only through its own detached panel.
    //
    // It does NOT build a second grid. EquipmentPanel already renders one detached panel per equipped bag
    // from the same Slots; this re-parents that panel into the tab view, so there is one set of cells
    // holding one set of items. Two grids over one inventory is how items appear to duplicate.
    //
    // Only offered when VABackpacks is loaded and a bag is actually equipped - BackpackBridge is a soft
    // reflection layer, so the pack mod stays optional.
    internal static class BackpackTab
    {
        const string TabId = "vainventory.backpack";
        static RectTransform view;
        static Transform borrowed;
        static Transform borrowedHome;

        internal static void RegisterTab()
        {
            InventoryTabs.Register(new InventoryTabs.Tab
            {
                Id = TabId,
                Label = "Backpack",
                Order = 20,
                IsAvailable = Available,
                Build = parent => view = parent,
                OnVisibility = Mount
            });
        }

        static bool Available()
        {
            if (!BackpackBridge.IsAvailable) return false;
            // Unified shows the worn bag inside the character panel (no detached panel to borrow).
            if (UnifiedScreen.Enabled) return false;
            var player = Player.m_localPlayer;
            if (player == null) return false;
            var bags = BackpackBridge.GetEquippedBackpacks(player);
            return bags != null && bags.Count > 0;
        }

        static void Mount(bool shown)
        {
            if (!shown)
            {
                // Put it back where EquipmentPanel expects it, or the next rebuild loses track of it.
                if (borrowed != null && borrowedHome != null)
                {
                    borrowed.SetParent(borrowedHome, false);
                    borrowed.gameObject.SetActive(false);
                }
                borrowed = null;
                borrowedHome = null;
                return;
            }

            if (view == null) return;

            var panel = EquipmentPanel.backpackPanels.Values.FirstOrDefault(p => p != null);
            if (panel == null) return;

            borrowed = panel;
            borrowedHome = panel.parent;

            panel.SetParent(view, false);
            panel.gameObject.SetActive(true);
            panel.anchorMin = new Vector2(0.5f, 1f);
            panel.anchorMax = new Vector2(0.5f, 1f);
            panel.pivot = new Vector2(0.5f, 1f);
            panel.anchoredPosition = new Vector2(0f, -12f);
        }

        internal static void Reset()
        {
            view = null;
            borrowed = null;
            borrowedHome = null;
        }
    }
}
