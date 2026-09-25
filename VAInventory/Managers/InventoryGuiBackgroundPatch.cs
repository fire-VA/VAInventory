using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
 public static class InventoryGuiBackgrounds
 {
 internal static void Apply(InventoryGui __instance)
 {
 // Helper to apply background
 void ApplyBackground(Transform panel, string key, bool attachDrag = false, RectTransform dragTarget = null)
 {
 var bkg = panel?.Find("Bkg") ?? panel?.Find("Background");
 if (bkg)
 {
 var image = bkg.GetComponent<Image>();
 var customSprite = UIBackgroundManager.GetBackground(key);
 if (image && customSprite)
 {
 image.sprite = customSprite;
 image.type = Image.Type.Sliced;
 }
 // Attach drag handler for container only
 if (attachDrag && dragTarget != null)
 {
 var handler = bkg.GetComponent<UniversalDragHandler>() ?? bkg.gameObject.AddComponent<UniversalDragHandler>();
 handler.TargetPanel = dragTarget;
 }
 }
 }

 // Robust skills dialog background
 void ApplySkillsBackground(SkillsDialog skillsDialog)
 {
 if (skillsDialog == null) return;
 var image = skillsDialog.GetComponent<Image>();
 if (!image)
 {
 var bkg = skillsDialog.transform.Find("Bkg") ?? skillsDialog.transform.Find("Background");
 if (bkg) image = bkg.GetComponent<Image>();
 }
 if (!image && skillsDialog.m_listRoot)
 {
 image = skillsDialog.m_listRoot.GetComponent<Image>();
 }
 var customSprite = UIBackgroundManager.GetBackground("Skills");
 if (image && customSprite)
 {
 image.sprite = customSprite;
 image.type = Image.Type.Sliced;
 }
 }

 // Main Inventory (do NOT attach drag handler)
 ApplyBackground(__instance.m_player, "Inventory");
 // Chest/Container (attach drag handler)
 ApplyBackground(__instance.m_container, "Container", attachDrag: true, dragTarget: __instance.m_container);
 // Crafting
 ApplyBackground(__instance.m_crafting, "Crafting");
 // Info
 ApplyBackground(__instance.m_info, "Info");
 // Split dialog
 ApplyBackground(__instance.m_splitDialog.transform, "Split");
 // Skills dialog (robust)
 ApplySkillsBackground(__instance.m_skillsDialog);
 // Texts dialog
 ApplyBackground(__instance.m_textsDialog?.transform, "Texts");
 // Trophies panel
 ApplyBackground(__instance.m_trophiesPanel?.transform, "Trophies");
 // Variant dialog
 ApplyBackground(__instance.m_variantDialog?.transform, "Variant");
 }
 }
}
