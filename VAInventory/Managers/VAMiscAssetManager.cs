using BepInEx;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System.Reflection; // Added for reflection
using System; // Add this at the top with other using statements

namespace VerdantsAscent.Managers
{
 public static class VAMiscAssetManager
 {
 // The panel
 // backgrounds live in BepInEx\cache\VAInventory\Backgrounds. The old config\VAInventoryBackgrounds is read once: its images are COPIED
 // across (never deleted, never overwritten), logged; the config folder is never created any more.
 private static readonly string BackgroundsDir = Path.Combine(Paths.BepInExRootPath, "cache", "VAInventory", "Backgrounds");
 private static readonly string OldBackgroundsDir = Path.Combine(Paths.ConfigPath, "VAInventoryBackgrounds");

 private static void CarryOldBackgrounds()
 {
 try
 {
 if (!Directory.Exists(OldBackgroundsDir)) return;
 // 1.0.154 (the lead: "copies exactly once"): a marker in the new folder records the carry; later starts never copy again (an
 // image the player deletes from the new folder stays deleted)
 string marker = Path.Combine(BackgroundsDir, ".carried_from_config");
 if (File.Exists(marker)) return;
 int copied = 0, kept = 0;
 foreach (var f in Directory.GetFiles(OldBackgroundsDir))
 {
 string ext = Path.GetExtension(f).ToLowerInvariant();
 if (ext != ".png" && ext != ".jpg") continue;
 string to = Path.Combine(BackgroundsDir, Path.GetFileName(f));
 if (File.Exists(to)) { kept++; continue; }
 File.Copy(f, to); copied++;
 }
 File.WriteAllText(marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " copied " + copied + ", kept " + kept + " from " + OldBackgroundsDir);
 if (copied > 0 || kept > 0) Debug.Log($"[VAInventory] backgrounds: {copied} image(s) copied from config/VAInventoryBackgrounds to cache/VAInventory/Backgrounds ({kept} already there); the old folder is left as is and can be removed");
 }
 catch (System.Exception e) { Debug.LogWarning($"[VAInventory] backgrounds: copy from the old config folder failed: {e.Message}"); }
 }
 private static readonly Dictionary<string, Sprite> _backgroundSprites = [];

 public static void LoadAllBackgrounds()
 {
 _backgroundSprites.Clear();
 if (!Directory.Exists(BackgroundsDir))
 Directory.CreateDirectory(BackgroundsDir);
 CarryOldBackgrounds();

 foreach (var file in Directory.GetFiles(BackgroundsDir, "*.png"))
 {
 var sprite = LoadSpriteFromFile(file);
 if (sprite != null)
 _backgroundSprites[Path.GetFileName(file)] = sprite;
 }
 foreach (var file in Directory.GetFiles(BackgroundsDir, "*.jpg"))
 {
 var sprite = LoadSpriteFromFile(file);
 if (sprite != null)
 _backgroundSprites[Path.GetFileName(file)] = sprite;
 }
 // 1.0.154 (the lead: point-of-use): the folder the images are actually read from, and what was loaded
 Debug.Log($"[VAInventory] backgrounds: {_backgroundSprites.Count} image(s) read from {BackgroundsDir}{(_backgroundSprites.Count > 0 ? " (" + string.Join(", ", new List<string>(_backgroundSprites.Keys).ToArray()) + ")" : "")}");
 }

 public static Sprite GetBackgroundByName(string filename)
 {
 if (string.IsNullOrWhiteSpace(filename)) return null;

 // Try exact match first
 if (_backgroundSprites.TryGetValue(filename.Trim(), out var sprite))
 return sprite;

 // Try matching without extension if not found
 string filenameNoExt = Path.GetFileNameWithoutExtension(filename.Trim());
 foreach (var kvp in _backgroundSprites)
 {
 if (Path.GetFileNameWithoutExtension(kvp.Key).Equals(filenameNoExt, StringComparison.OrdinalIgnoreCase))
 return kvp.Value;
 }
 return null;
 }

 public static Sprite LoadInventoryBackgroundSprite()
 {
 string[] paths = [
 "Assets/Custom/VAitems/Icons/va_background",
 "Assets/Custom/VAitems/Icons/va_background.png"
 ];
 foreach (var path in paths)
 {
 var sprite = VAInventory.assetBundle?.LoadAsset<Sprite>(path);
 if (sprite != null)
 {
 Debug.Log($"{VAInventory.PluginName}: Loaded inventory background from {path}");
 return sprite;
 }
 }
 Debug.LogWarning($"{VAInventory.PluginName}: va_background not found in asset bundle.");
 return null;
 }
 
 public static readonly List<string> BackgroundAssetNames =
 [
 "va_background",
 "va_background1",
 "va_background2",
 // Add more as needed
 ];

 public static Sprite LoadBackgroundSprite(string assetName)
 {
 if (VAInventory.assetBundle == null)
 {
 Debug.LogError($"{VAInventory.PluginName}: AssetBundle is null.");
 return null;
 }

 string[] searchPaths = [
 $"Assets/Custom/VAitems/Icons/{assetName}",
 $"Assets/Custom/VAitems/Icons/{assetName.ToLower()}",
 $"Assets/Custom/VAitems/Icons/{assetName.ToUpper()}",
 $"Assets/Custom/VAitems/Icons/{assetName}.png",
 $"Assets/Custom/VAitems/Icons/{assetName}.jpg"
 ];

 foreach (var path in searchPaths)
 {
 var sprite = VAInventory.assetBundle.LoadAsset<Sprite>(path);
 if (sprite != null)
 {
 Debug.Log($"{VAInventory.PluginName}: Loaded background sprite '{assetName}' from {path}");
 return sprite;
 }
 }
 Debug.LogWarning($"{VAInventory.PluginName}: Background sprite '{assetName}' not found in asset bundle.");
 return null;
 }

 public static Sprite LoadSpriteByName(string assetName)
 {
 // Use background loader if in list, else fallback to generic
 if (BackgroundAssetNames.Contains(assetName))
 return LoadBackgroundSprite(assetName);

 string[] searchPaths = [
 $"Assets/Custom/VAitems/Icons/{assetName}",
 $"Assets/Custom/VAitems/Icons/{assetName.ToLower()}",
 $"Assets/Custom/VAitems/Icons/{assetName.ToUpper()}",
 $"Assets/Custom/VAitems/Icons/{assetName}.png",
 $"Assets/Custom/VAitems/Icons/{assetName}.jpg"
 ];
 foreach (var path in searchPaths)
 {
 var sprite = VAInventory.assetBundle?.LoadAsset<Sprite>(path);
 if (sprite != null)
 {
 Debug.Log($"{VAInventory.PluginName}: Loaded sprite '{assetName}' from {path}");
 return sprite;
 }
 }
 Debug.LogWarning($"{VAInventory.PluginName}: Sprite '{assetName}' not found in asset bundle.");
 return null;
 }

 public static Sprite LoadSpriteFromFile(string filePath)
 {
 if (string.IsNullOrWhiteSpace(filePath))
 {
 Debug.LogWarning($"{VAInventory.PluginName}: Custom background file path is empty.");
 return null;
 }
 filePath = filePath.Trim().Trim('"').TrimStart('/', '\\').TrimEnd('/', '\\'); // Auto-strip quotes, leading/trailing slashes

 // Check for illegal path characters
 if (filePath.IndexOfAny(Path.GetInvalidPathChars()) >=0)
 {
 Debug.LogWarning($"{VAInventory.PluginName}: Custom background file path contains illegal characters: {filePath}");
 return null;
 }

 string ext;
 try
 {
 ext = Path.GetExtension(filePath).ToLowerInvariant();
 }
 catch (System.Exception ex)
 {
 Debug.LogWarning($"{VAInventory.PluginName}: Exception getting file extension: {filePath}\n{ex}");
 return null;
 }

 if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
 {
 Debug.LogWarning($"{VAInventory.PluginName}: Unsupported background file type: {filePath}");
 return null;
 }
 if (!File.Exists(filePath))
 {
 Debug.LogWarning($"{VAInventory.PluginName}: Custom background file not found: {filePath}");
 return null;
 }
 try
 {
 byte[] fileData = File.ReadAllBytes(filePath);
 Texture2D tex = new(2,2, TextureFormat.RGBA32, false);

 // Try using ImageConversion.LoadImage if available
 var imageConversionType = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
 if (imageConversionType != null)
 {
 var loadImageMethod = imageConversionType.GetMethod("LoadImage", BindingFlags.Static | BindingFlags.Public, null, [typeof(Texture2D), typeof(byte[]), typeof(bool)], null);
 if (loadImageMethod != null)
 {
 bool loaded = (bool)loadImageMethod.Invoke(null, [tex, fileData, true]);   // 1.0.134 (memory plan): non-readable - no CPU copy of the pixels next to the GPU one (nothing reads them back)
 if (!loaded)
 {
 Debug.LogWarning($"{VAInventory.PluginName}: Failed to load image data from file: {filePath}");
 return null;
 }
 tex.name = Path.GetFileNameWithoutExtension(filePath);
 Sprite sprite = Sprite.Create(tex, new Rect(0,0, tex.width, tex.height), new Vector2(0.5f,0.5f),100f);
 Debug.Log($"{VAInventory.PluginName}: Loaded custom background sprite from file: {filePath}");
 return sprite;
 }
 }

 Debug.LogError($"{VAInventory.PluginName}: Texture2D.LoadImage(byte[]) and ImageConversion.LoadImage not found. Custom backgrounds not supported in this Unity version/build.");
 return null;
 }
 catch (System.Exception ex)
 {
 Debug.LogError($"{VAInventory.PluginName}: Exception loading custom background: {filePath}\n{ex}");
 return null;
 }
 }

 public static void RefreshCustomBackground(string filename)
 {
 if (string.IsNullOrWhiteSpace(filename)) return;

 // Remove any cached sprite for this filename (with or without extension)
 string filenameNoExt = Path.GetFileNameWithoutExtension(filename.Trim());
 var keysToRemove = new List<string>();
 foreach (var key in _backgroundSprites.Keys)
 {
 if (Path.GetFileNameWithoutExtension(key).Equals(filenameNoExt, StringComparison.OrdinalIgnoreCase))
 keysToRemove.Add(key);
 }
 foreach (var key in keysToRemove)
 _backgroundSprites.Remove(key);

 // Attempt to reload the file from disk (try .png and .jpg)
 string pngPath = Path.Combine(BackgroundsDir, filenameNoExt + ".png");
 string jpgPath = Path.Combine(BackgroundsDir, filenameNoExt + ".jpg");

 if (File.Exists(pngPath))
 {
 var sprite = LoadSpriteFromFile(pngPath);
 if (sprite != null)
 _backgroundSprites[Path.GetFileName(pngPath)] = sprite;
 }
 else if (File.Exists(jpgPath))
 {
 var sprite = LoadSpriteFromFile(jpgPath);
 if (sprite != null)
 _backgroundSprites[Path.GetFileName(jpgPath)] = sprite;
 }
 }
 }
}
