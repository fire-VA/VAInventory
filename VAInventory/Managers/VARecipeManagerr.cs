// VARecipeManager.cs
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public static class VARecipeManager
    {
        private static readonly Dictionary<string, GameObject> _prefabCache = [];

        /// <summary>
        /// Call this after pieces are loaded and config is ready
        /// Applies recipe requirements to every enabled piece based on config
        /// </summary>
        public static void UpdatePieceRequirements()
        {
            // Prevent infinite reload loop from config saving
            if (ConfigSync.ProcessingServerUpdate)
                return;

            if (ZNetScene.instance == null || VAInventory.piecePrefabs.Count == 0)
            {
                Debug.Log($"{VAInventory.PluginName}: Cannot update piece requirements - ZNetScene or piece list not ready.");
                return;
            }


            if (ZNetScene.instance == null || VAInventory.piecePrefabs.Count == 0)
            {
                Debug.Log($"{VAInventory.PluginName}: Cannot update piece requirements - ZNetScene or piece list not ready.");
                return;
            }

            _prefabCache.Clear();

            // Cache all loaded piece prefabs by name (lowercase for reliable lookup)
            foreach (var prefab in VAInventory.piecePrefabs)
            {
                if (prefab != null)
                {
                    _prefabCache[prefab.name.ToLowerInvariant()] = prefab;
                }
            }

            bool anyApplied = false;

            foreach (var kvp in ConfigManager.Instance.configPerPieceEnabled)
            {
                string pieceName = kvp.Key;

                
                if (!kvp.Value.Value || !ConfigManager.Instance.configServerAuthority.Value)
                    continue;

                if (!_prefabCache.TryGetValue(pieceName.ToLowerInvariant(), out GameObject piecePrefab))
                {
                    Debug.LogWarning($"{VAInventory.PluginName}: Piece '{pieceName}' not found in loaded prefabs. Skipping recipe.");
                    continue;
                }

                var piece = piecePrefab.GetComponent<Piece>();
                if (piece == null)
                {
                    Debug.LogWarning($"{VAInventory.PluginName}: Piece prefab '{pieceName}' has no Piece component.");
                    continue;
                }

                string configValue = ConfigManager.Instance.configPerPieceRecipes[pieceName].Value;
                var requirements = ParseRequirements(configValue);

                if (requirements.Length == 0)
                {
                    Debug.LogWarning($"{VAInventory.PluginName}: No valid requirements parsed for '{pieceName}' from config: '{configValue}'");
                    continue;
                }

                piece.m_resources = requirements;
                anyApplied = true;

                if (ConfigManager.IsVerbose)
                {
                    string reqList = string.Join(" | ", requirements.Select(r => $"{r.m_resItem.m_itemData.m_shared.m_name} x{r.m_amount}"));
                    Debug.Log($"{VAInventory.PluginName}: Applied recipe to {pieceName}: {reqList}");
                }
            }

            if (anyApplied && ConfigManager.IsVerbose)
                Debug.Log($"{VAInventory.PluginName}: Piece recipe update complete.");
        }

        /// <summary>
        /// Parses a config string like "Wood,5" or "Wood,10,2,Stone,5,1" into Piece.Requirement[]
        /// </summary>
        private static Piece.Requirement[] ParseRequirements(string configValue)
        {
            var requirements = new List<Piece.Requirement>();
            var parts = configValue.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();

            int i = 0;
            while (i < parts.Length)
            {
                if (i + 1 >= parts.Length)
                {
                    Debug.LogWarning($"{VAInventory.PluginName}: Recipe ended unexpectedly at '{parts[i]}'");
                    break;
                }

                string itemName = parts[i];
                if (!int.TryParse(parts[i + 1], out int amount))
                {
                    Debug.LogWarning($"{VAInventory.PluginName}: Invalid amount '{parts[i + 1]}' for item '{itemName}'");
                    i += 2;
                    continue;
                }

                // Optional: per-level amount (if present)
                int perLevel = 0;
                if (i + 2 < parts.Length && int.TryParse(parts[i + 2], out int parsedPerLevel))
                {
                    perLevel = parsedPerLevel;
                    i += 3;
                }
                else
                {
                    i += 2; // Just item + amount
                }

                // Find the item prefab
                string lowerName = itemName.ToLowerInvariant();

                if (_prefabCache.TryGetValue(lowerName, out GameObject itemPrefab) ||
                    (itemPrefab = ZNetScene.instance?.GetPrefab(itemName)) != null ||
                    (itemPrefab = ObjectDB.instance?.GetItemPrefab(itemName)) != null)
                {
                    if (!_prefabCache.ContainsKey(lowerName))
                        _prefabCache[lowerName] = itemPrefab;
                }
                else
                {
                    Debug.LogWarning($"{VAInventory.PluginName}: Could not find item prefab for '{itemName}' in recipe.");
                    continue;
                }

                var itemDrop = itemPrefab.GetComponent<ItemDrop>();
                if (itemDrop == null)
                {
                    Debug.LogWarning($"{VAInventory.PluginName}: Prefab '{itemName}' has no ItemDrop component.");
                    continue;
                }

                requirements.Add(new Piece.Requirement
                {
                    m_resItem = itemDrop,
                    m_amount = amount,
                    m_amountPerLevel = perLevel,
                    m_recover = true
                });
            }

            return [.. requirements];
        }
    }
}