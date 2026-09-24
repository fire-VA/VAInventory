using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Managers
{
    public static class VAItemManager
    {
        public static readonly List<string> ItemNameList = [];

        public static void LoadItems(string mainPath)
        {
            if (VAInventory.assetBundle == null)
            {
                Debug.LogError($"{VAInventory.PluginName}: AssetBundle is null.");
                return;
            }

            VAInventory.itemPrefabs.Clear();
            var allAssets = VAInventory.assetBundle.GetAllAssetNames();
            Debug.Log($"{VAInventory.PluginName}: AssetBundle contains {allAssets.Length} assets");
            if (ConfigManager.Instance?.configVerboseLogging?.Value ?? false)
                Debug.Log($"{VAInventory.PluginName}: bundle assets: {string.Join(", ", allAssets)}");

            int failed = 0;
            foreach (var itemName in ItemNameList)
            {
                string prefabPath = mainPath + "items/" + itemName + ".prefab";
                string normalizedPath = prefabPath.Replace('\\', '/').ToLower();
                GameObject itemPrefab = VAInventory.assetBundle.LoadAsset<GameObject>(normalizedPath);

                if (itemPrefab == null)
                {
                    Debug.LogError($"{VAInventory.PluginName}: Failed to load item prefab: {normalizedPath}");
                    failed++;
                    continue;
                }

                var itemDrop = itemPrefab.GetComponent<ItemDrop>();
                if (itemDrop == null)
                {
                    Debug.LogError($"{VAInventory.PluginName}: {itemName} - ItemDrop component missing!");
                    UnityEngine.Object.Destroy(itemPrefab);
                    failed++;
                    continue;
                }

                itemPrefab.SetActive(true);
                ShaderReplacement.ReplaceShaders([itemPrefab]);
                VAInventory.itemPrefabs.Add(itemPrefab);
                Debug.Log($"{VAInventory.PluginName}: Loaded item prefab: {itemName}, ItemDrop present: {itemDrop != null}, m_dropPrefab: {(itemDrop.m_itemData.m_dropPrefab != null ? itemDrop.m_itemData.m_dropPrefab.name : "null")}");
            }

            Debug.Log($"{VAInventory.PluginName}: Loaded {VAInventory.itemPrefabs.Count} items. Failed: {failed}");
        }
    }
}