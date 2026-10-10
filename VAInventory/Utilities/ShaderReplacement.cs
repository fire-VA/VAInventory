using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    public static class ShaderReplacement
    {
        private static readonly Dictionary<GameObject, Renderer[]> rendererCache = [];

        public static void RunOnce()
        {
            ReplaceShaders(
                VAInventory.piecePrefabs); // Changed Mod.piecePrefabs to VAInventory.piecePrefabs
            Debug.Log($"{VAInventory.PluginName}: Shader replacement applied.");
        }

        public static void ReplaceShaders(IEnumerable<GameObject> prefabs)
        {
            foreach (var prefab in prefabs)
            {
                if (prefab == null)
                {
                    Debug.LogWarning($"{VAInventory.PluginName}: Null prefab in ReplaceShaders, skipping.");
                    continue;
                }

                if (!rendererCache.TryGetValue(prefab, out var renderers))
                {
                    var attach = FindAttachGameObject(prefab);
                    if (attach != null)
                    {
                        renderers = attach.GetComponentsInChildren<Renderer>(true);
                    }
                    else
                    {
                        renderers = [];
                    }
                    rendererCache[prefab] = renderers;
                }

                foreach (var renderer in renderers)
                {
                    if (renderer == null)
                    {
                        Debug.LogWarning($"{VAInventory.PluginName}: Null renderer found in {prefab.name}");
                        continue;
                    }

                    foreach (var material in renderer.materials)
                    {
                        if (material == null)
                        {
                            Debug.LogWarning($"{VAInventory.PluginName}: Null material in {prefab.name} on {renderer.name}");
                            continue;
                        }

                        if (material.shader != null && material.shader.name.Contains("Custom/") && !material.shader.name.Contains("Standard"))
                        {
                            material.shader = Shader.Find("Standard");
                            Debug.Log($"{VAInventory.PluginName}: Replaced Custom/ shader with Standard on {prefab.name}");
                        }

                        if (material.mainTexture == null)
                        {
                            Debug.LogWarning($"{VAInventory.PluginName}: Material on {prefab.name} has no main texture, assigning default...");
                            material.mainTexture = Texture2D.whiteTexture;
                        }
                    }
                }
            }
        }

        private static GameObject FindAttachGameObject(GameObject root)
        {
            if (root == null) return null;

            foreach (Transform child in root.transform)
            {
                if (child.name.StartsWith("attach"))
                {
                    return child.gameObject;
                }
            }

            return null;
        }
    }
}