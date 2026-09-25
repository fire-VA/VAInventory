using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace VerdantsAscent.Managers
{
    public static class InventoryGridExtraSlots
    {
        // The three per-element FieldInfos that used to live here are gone: Valheim 1.0 replaced the
        // private nested InventoryGrid.Element with the public top-level InventoryElement MonoBehaviour,
        // so GetNestedType("Element") returns null and m_icon/m_amount need no reflection at all.
        private static readonly FieldInfo fi_elements = AccessTools.Field(typeof(InventoryGrid), "m_elements");
        private static readonly FieldInfo fi_elementPrefab = AccessTools.Field(typeof(InventoryGrid), "m_elementPrefab");
        private static readonly FieldInfo fi_width = AccessTools.Field(typeof(InventoryGrid), "m_width");

        internal static void ExtendGrid(InventoryGrid __instance)
        {
            try
            {
                // Only extend the grid for the player inventory grid, not containers
                if (InventoryGui.instance == null || __instance != InventoryGui.instance.m_playerGrid)
                    return;

                var player = Player.m_localPlayer;
                if (player == null) return;
                var gridInventory = __instance.GetInventory();
                if (gridInventory == null) return;
                if (gridInventory != player.GetInventory())
                    return;

                int width = (int)(fi_width?.GetValue(__instance) ?? 0);
                var elementPrefab = fi_elementPrefab?.GetValue(__instance) as GameObject;

                if (fi_elements?.GetValue(__instance) is not IList elementsList || elementPrefab == null || width <= 0)
                    return;

                var inventory = player.GetInventory();
                if (inventory == null) return;

                int requiredRows = Slots.InventoryHeightPlayer;
                var items = inventory.GetAllItems();
                if (items != null && items.Count > 0)
                {
                    var validYs = items.Where(i => i != null).Select(i => i.m_gridPos.y).ToList();
                    if (validYs.Count > 0)
                        requiredRows = Math.Max(requiredRows, validYs.Max() + 1);
                }

                if (Slots.toolHotbarSlots != null && Slots.toolHotbarSlots.Count > 0)
                {
                    int maxToolHotbarY = Slots.toolHotbarSlots
                        .Where(s => s != null && s.IsActive)
                        .Select(s => s.GridPosition.y)
                        .DefaultIfEmpty(Slots.InventoryHeightPlayer - 1)
                        .Max();
                    requiredRows = Math.Max(requiredRows, maxToolHotbarY + 1);
                }

                int requiredElements = requiredRows * width;

                if (elementsList.Count >= requiredElements)
                    return;

                for (int i = elementsList.Count; i < requiredElements; i++)
                {
                    // Mirror vanilla InventoryGrid.UpdateGui's own element construction on 1.0: instantiate
                    // the prefab, take the InventoryElement component it already carries (m_icon/m_amount
                    // wired by the prefab), and Initialize it with its grid coordinates. The old
                    // Activator.CreateInstance path cannot work any more - InventoryElement is a
                    // MonoBehaviour and must live on a GameObject.
                    var go = UnityEngine.Object.Instantiate(elementPrefab, __instance.m_gridRoot);
                    var newElem = go.GetComponent<InventoryElement>();
                    if (newElem == null)
                    {
                        Debug.LogWarning("[VAInventory] element prefab carries no InventoryElement component; cannot extend grid.");
                        UnityEngine.Object.Destroy(go);
                        return;
                    }
                    newElem.Initialize(i % width, i / width);

                    elementsList.Add(newElem);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] InventoryGridExtraSlots.ExtendGrid exception: {ex}");
            }
        }
    }
}