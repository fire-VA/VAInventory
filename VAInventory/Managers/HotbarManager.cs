using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers.HotBars;

namespace VerdantsAscent.Managers
{
    public class HotbarManager : MonoBehaviour
    {
        public static HotbarManager Instance;

        private void Awake()
        {
            Instance = this;
            StartCoroutine(PatchWhenReady());
        }

        private System.Collections.IEnumerator PatchWhenReady()
        {
            while (ZInput.instance == null)
                yield return null;

            TryPatchZInputTryGetButtonState();
        }

        private void TryPatchZInputTryGetButtonState()
        {
            try
            {
                var methods = typeof(ZInput)
                    .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name == "TryGetButtonState")
                    .ToArray();

                MethodInfo target = null;
                foreach (var method in methods)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length > 0 && parameters[0].ParameterType == typeof(string))
                    {
                        target = method;
                        break;
                    }
                }

                if (target == null)
                {
                    Debug.LogWarning("[VAInventory] HotbarManager: could not find ZInput.TryGetButtonState(string) on this game version.");
                    return;
                }

                var prefix = AccessTools.Method(typeof(HotbarManager), nameof(ZInput_TryGetButtonState_Prefix));
                if (prefix == null)
                {
                    Debug.LogWarning("[VAInventory] HotbarManager: ZInput prefix method not found.");
                    return;
                }

                new Harmony("com.fire.vainventory.zinputpatch").Patch(target, prefix: new HarmonyMethod(prefix));
                if (ConfigManager.IsVerbose)
                    Debug.Log("[VAInventory] HotbarManager: patched ZInput.TryGetButtonState successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] HotbarManager: ZInput.TryGetButtonState patch failed: {ex}");
            }
        }

        private static bool ZInput_TryGetButtonState_Prefix(string name, ref bool __result)
        {
            try
            {
                if (ZInput.IsGamepadActive()) return true;
                if (string.IsNullOrEmpty(name)) return true;

                int num = -1;
                for (int i = name.Length - 1; i >= 0; i--)
                {
                    if (!char.IsDigit(name[i]))
                    {
                        if (i < name.Length - 1)
                        {
                            string digits = name.Substring(i + 1);
                            if (int.TryParse(digits, out var parsed))
                                num = parsed;
                        }
                        break;
                    }
                }

                if (num <= 0) return true;
                int slotIndex = num - 1;
                if (!HotkeyInput.ShouldSuppressVanillaHotbarIndex(slotIndex)) return true;

                __result = false;
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
