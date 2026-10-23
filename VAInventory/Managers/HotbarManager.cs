using System;
using System.Collections.Generic;
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

            // The ZInput.TryGetButtonState prefix (6.3k calls/s,
            // 8.3 ms/s) is no longer applied; vanilla's hotbar is suppressed at its only use, Player.UseHotbarItem (below).
            Debug.Log("[VAInventory] HotbarManager: vanilla hotbar suppressed at Player.UseHotbarItem (no ZInput hook)");
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

        // The button name -> hotbar index is parsed ONCE per name (no digit scan or
        // Substring per call; -1 = not a numbered button, returned at once), and the gamepad state + each index's suppress answer are
        // read once per frame.
        private static readonly Dictionary<string, int> s_nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private static int s_frame = -1;
        private static bool s_gamepad;
        private static readonly Dictionary<int, bool> s_suppress = new Dictionary<int, bool>();

        private static int IndexOf(string name)
        {
            if (s_nameIndex.TryGetValue(name, out int idx)) return idx;
            int num = -1;
            for (int i = name.Length - 1; i >= 0; i--)
            {
                if (!char.IsDigit(name[i]))
                {
                    if (i < name.Length - 1 && int.TryParse(name.Substring(i + 1), out var parsed)) num = parsed;
                    break;
                }
            }
            idx = num > 0 ? num - 1 : -1;
            s_nameIndex[name] = idx;
            return idx;
        }

        // Vanilla reads Hotbar1-8 (+Alt) in ONE place, Player.Update -> UseHotbarItem(i), so suppressing here is the same rule
        // as before at ~0 cost (it only runs on a hotbar key press). The decision is logged at Debug.
        [HarmonyPatch(typeof(Player), nameof(Player.UseHotbarItem))]
        private static class Player_UseHotbarItem_Suppress
        {
            private static bool Prefix(Player __instance, int index)
            {
                try
                {
                    if (__instance != Player.m_localPlayer || ZInput.IsGamepadActive()) return true;
                    if (!HotkeyInput.ShouldSuppressVanillaHotbarIndex(index - 1)) return true;
                    EquipmentPanel.PanelLog?.LogDebug($"[VAInventory] hotbar {index}: vanilla use skipped (a VAInventory hotkey owns it)");
                    return false;
                }
                catch { return true; }
            }
        }

        private static bool ZInput_TryGetButtonState_Prefix(string name, ref bool __result)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return true;
                int slotIndex = IndexOf(name);
                if (slotIndex < 0) return true;
                if (Time.frameCount != s_frame) { s_frame = Time.frameCount; s_gamepad = ZInput.IsGamepadActive(); s_suppress.Clear(); }
                if (s_gamepad) return true;
                if (!s_suppress.TryGetValue(slotIndex, out bool suppress)) s_suppress[slotIndex] = suppress = HotkeyInput.ShouldSuppressVanillaHotbarIndex(slotIndex);
                if (!suppress) return true;

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
