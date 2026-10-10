using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Managers
{
 public static class UIBackgroundManager
 {
 private static readonly Dictionary<string, string> _backgroundAssetNames = [];
 private static readonly Dictionary<string, Sprite> _backgroundCache = [];

 // Register a custom background asset name for a specific UI type
 public static void SetBackground(string uiType, string assetName)
 {
 _backgroundAssetNames[uiType] = assetName;
 _backgroundCache.Remove(uiType); // Clear cache so it reloads
 }

 // Get the custom background for a specific UI type
 public static Sprite GetBackground(string uiType)
 {
 if (_backgroundCache.TryGetValue(uiType, out var cached))
 return cached;

 var config = ConfigManager.Instance;
 string filename = null;
 bool useCustom = false;

 switch (uiType)
 {
 case "Inventory":
 useCustom = config.InventoryBackgroundToggle?.Value ?? false;
 filename = config.InventoryBackground?.Value;
 break;
 case "Container":
 useCustom = config.ContainerBackgroundToggle?.Value ?? false;
 filename = config.ContainerBackground?.Value;
 break;
 case "Crafting":
 useCustom = config.CraftingBackgroundToggle?.Value ?? false;
 filename = config.CraftingBackground?.Value;
 break;
 case "Info":
 useCustom = config.InfoBackgroundToggle?.Value ?? false;
 filename = config.InfoBackground?.Value;
 break;
 case "Split":
 useCustom = config.SplitBackgroundToggle?.Value ?? false;
 filename = config.SplitBackground?.Value;
 break;
 case "Skills":
 useCustom = config.SkillsBackgroundToggle?.Value ?? false;
 filename = config.SkillsBackground?.Value;
 break;
 case "Texts":
 useCustom = config.TextsBackgroundToggle?.Value ?? false;
 filename = config.TextsBackground?.Value;
 break;
 case "Trophies":
 useCustom = config.TrophiesBackgroundToggle?.Value ?? false;
 filename = config.TrophiesBackground?.Value;
 break;
 case "Variant":
 useCustom = config.VariantBackgroundToggle?.Value ?? false;
 filename = config.VariantBackground?.Value;
 break;
 case "EquipmentPanel":
 useCustom = config.EquipmentPanelBackgroundToggle?.Value ?? false;
 filename = config.EquipmentPanelBackground?.Value;
 break;
 }

 if (useCustom && !string.IsNullOrEmpty(filename))
 {
 var sprite = VAMiscAssetManager.GetBackgroundByName(filename);
 if (sprite != null)
 {
 _backgroundCache[uiType] = sprite;
 return sprite;
 }
 else
 {
 Debug.LogWarning($"[VAInventory] Failed to load custom {uiType} background '{filename}' from backgrounds folder, falling back to asset bundle.");
 }
 }

 if (_backgroundAssetNames.TryGetValue(uiType, out var assetName))
 {
 var sprite = VAMiscAssetManager.LoadSpriteByName(assetName);
 if (sprite != null)
 {
 _backgroundCache[uiType] = sprite;
 return sprite;
 }
 }
 return null;
 }

 // Example: Register default backgrounds by asset name
 public static void RegisterDefaultBackgrounds()
 {
 SetBackground("Inventory", "va_background");
 SetBackground("Container", "va_background");
 SetBackground("Crafting", "va_background1");
 SetBackground("Info", "va_background");
 SetBackground("Split", "va_background");
 SetBackground("Skills", "va_background2");
 SetBackground("Texts", "va_background");
 SetBackground("Trophies", "va_background");
 SetBackground("Variant", "va_background");
 SetBackground("EquipmentPanel", "va_background3");
 }

 public static void ForceBackgroundRefresh(string uiType)
 {
 _backgroundCache.Remove(uiType);
 // Optionally trigger UI update for EquipmentPanel
 if (uiType == "EquipmentPanel")
 {
 VerdantsAscent.EquipmentPanel.UpdateBackground();
 }
 }
 }
}
