using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // 1.0.166 took the delete BUTTON's own image, which is the generic button
    // background (the log said "icon button"). The trash can is a separate sprite somewhere under the start screen. This walks every Image
    // under FejdStartup (inactive ones too: the world list is often closed at Awake), logs each sprite whose name says trash / delete /
    // remove / bin, and keeps the best: one under the world-remove button first, then any "trash" sprite that isn't a world prop (rubble,
    // pile). Runs after 1.0.166's capture (Priority.Last), so it wins. None found = the trash cell shows its × fallback, and the log says so.
    [HarmonyPatch(typeof(FejdStartup), "Awake")]
    internal static class TrashIconCapture
    {
        static readonly string[] Words = { "trash", "delete", "remove", "bin" };
        static readonly string[] NotIcons = { "rubble", "pile", "morkhalla" };

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Postfix(FejdStartup __instance)
        {
            try
            {
                var candidates = new List<(Sprite sprite, string path, int score)>();
                // The trash can is the delete button on each WORLD in the world list, so the world-list element PREFAB (an asset,
                // not under FejdStartup's children) and the world-list panel are searched first, at the highest score
                var sources = new List<(Image img, int bonus)>();
                if (__instance.m_worldListElement != null) foreach (var i in __instance.m_worldListElement.GetComponentsInChildren<Image>(true)) sources.Add((i, 20));
                if (__instance.m_worldListPanel != null) foreach (var i in __instance.m_worldListPanel.GetComponentsInChildren<Image>(true)) sources.Add((i, 15));
                foreach (var i in __instance.GetComponentsInChildren<Image>(true)) sources.Add((i, 0));
                // the world element's every sprite, once at Debug: if the trash can's sprite is named otherwise, the round log shows its name
                if (__instance.m_worldListElement != null)
                    EquipmentPanel.PanelLog?.LogDebug("panels: world-list element sprites: " + string.Join(", ", __instance.m_worldListElement.GetComponentsInChildren<Image>(true)
                        .Where(i => i.sprite != null).Select(i => i.name + "=" + i.sprite.name).ToArray()));
                foreach (var (img, bonus) in sources)
                {
                    var sp = img.sprite;
                    if (sp == null) continue;
                    string n = sp.name.ToLowerInvariant();
                    if (!Words.Any(w => n.Contains(w)) || NotIcons.Any(w => n.Contains(w))) continue;
                    int score = (n.Contains("trash") ? 2 : 1) + bonus;
                    if (__instance.m_worldRemove != null && img.transform.IsChildOf(__instance.m_worldRemove.transform)) score += 10;
                    else if (__instance.m_csRemoveButton != null && img.transform.IsChildOf(__instance.m_csRemoveButton.transform)) score += 5;
                    candidates.Add((sp, PathOf(img.transform), score));
                }
                foreach (var c in candidates) EquipmentPanel.PanelLog?.LogDebug($"panels: trash icon candidate '{c.sprite.name}' at {c.path} (score {c.score})");
                if (candidates.Count == 0) { EquipmentPanel.PanelLog?.LogInfo("[VAInventory] panels: no trash / delete sprite under the start screen; the trash cell shows ×"); return; }
                var best = candidates.OrderByDescending(c => c.score).First();
                PanelsChrome.s_menuTrash = best.sprite; PanelsChrome.s_menuTrashName = best.sprite.name;
                EquipmentPanel.PanelLog?.LogInfo($"[VAInventory] panels: trash icon = '{best.sprite.name}' from {best.path}");
            }
            catch (Exception e) { EquipmentPanel.PanelLog?.LogWarning("panels: trash icon search failed: " + e.Message); }
        }

        static string PathOf(Transform t) { string p = t.name; for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p; return p; }
    }
}
