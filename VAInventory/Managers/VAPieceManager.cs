using BepInEx.Bootstrap;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Utilities;

namespace VerdantsAscent.Managers
{
    public static class VAPieceManager
    {
        // All pieces that go in the hammer (kept empty for future use)
        public static readonly List<string> PieceNames = [];

        // Check if FiresGhettoNetworkMod is loaded (kept for potential future compatibility needs)
        private static bool IsNetworkingModLoaded()
        {
            return Chainloader.PluginInfos.ContainsKey("com.Fire.FiresGhettoNetworkMod");
        }

        public static void LoadPieces(string mainPath)
        {
            if (VAInventory.assetBundle == null)
            {
                Debug.LogError($"{VAInventory.PluginName}: AssetBundle is null.");
                return;
            }

            VAInventory.piecePrefabs.Clear();

            var allAssets = VAInventory.assetBundle.GetAllAssetNames();
            Debug.Log($"{VAInventory.PluginName}: AssetBundle contains {allAssets.Length} assets");
            if (ConfigManager.Instance?.configVerboseLogging?.Value ?? false)
                Debug.Log($"{VAInventory.PluginName}: bundle assets: {string.Join(", ", allAssets)}");

            int failed = 0;

            foreach (var pieceName in PieceNames)
            {
                string prefabPath = mainPath + "pieces/" + pieceName + ".prefab";
                string normalizedPath = prefabPath.Replace('\\', '/').ToLower();

                GameObject piecePrefab = VAInventory.assetBundle.LoadAsset<GameObject>(normalizedPath);

                if (piecePrefab == null)
                {
                    Debug.LogError($"{VAInventory.PluginName}: Failed to load piece prefab: {normalizedPath}");
                    failed++;
                    continue;
                }

                var piece = piecePrefab.GetComponent<Piece>();
                if (piece == null)
                {
                    Debug.LogError($"{VAInventory.PluginName}: {pieceName} - Piece component missing!");
                    UnityEngine.Object.Destroy(piecePrefab);
                    failed++;
                    continue;
                }

                // Default category fallback
                if (piece.m_category == Piece.PieceCategory.Max)
                    piece.m_category = Piece.PieceCategory.Furniture;

                // Default crafting station fallback
                if (piece.m_craftingStation == null)
                {
                    var workbench = ZNetScene.instance?.GetPrefab("piece_workbench")?.GetComponent<CraftingStation>();
                    piece.m_craftingStation = workbench;
                }

                VAInventory.piecePrefabs.Add(piecePrefab);
                Debug.Log($"{VAInventory.PluginName}: Loaded piece prefab: {pieceName}");
            }

            Debug.Log($"{VAInventory.PluginName}: Loaded {VAInventory.piecePrefabs.Count} pieces. Failed: {failed}");
        }

        public static void AddPiecesToHammer()
        {
            if (!VAInventory.IsObjectDBValid() || ObjectDB.instance == null)
            {
                Debug.LogWarning($"{VAInventory.PluginName}: ObjectDB not valid, cannot add pieces to Hammer");
                return;
            }

            var hammer = ObjectDB.instance.GetItemPrefab("Hammer");
            if (hammer == null) return;

            var itemDrop = hammer.GetComponent<ItemDrop>();
            if (itemDrop?.m_itemData?.m_shared?.m_buildPieces == null) return;

            var pieceTable = itemDrop.m_itemData.m_shared.m_buildPieces;

            int added = 0;
            foreach (var piece in VAInventory.piecePrefabs)
            {
                if (piece != null && !pieceTable.m_pieces.Contains(piece))
                {
                    pieceTable.m_pieces.Add(piece);
                    added++;
                }
            }
            if (added > 0) Debug.Log($"{VAInventory.PluginName}: added {added} piece(s) to the Hammer");
        }

    }
}