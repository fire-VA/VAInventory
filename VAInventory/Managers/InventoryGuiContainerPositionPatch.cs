using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
 [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
 public static class InventoryGui_UpdateContainer_Patch
 {
 private static Vector2? _lastDesiredPosition = null;

 private static void Postfix(InventoryGui __instance)
 {
 if (!__instance.m_container.gameObject.activeSelf)
 return;

 RectTransform containerRect = __instance.m_container;

 // On first visible frame, remember current (centered) position
 if (!_lastDesiredPosition.HasValue)
 {
 _lastDesiredPosition = containerRect.anchoredPosition;
 return;
 }

 // After game's update ? restore the X we want (keep game's Y if it changed)
 Vector2 current = containerRect.anchoredPosition;
 Vector2 desired = _lastDesiredPosition.Value;
 containerRect.anchoredPosition = new Vector2(desired.x, current.y);
 }

 // Call this when closing inventory to reset for next open
 public static void Reset()
 {
 _lastDesiredPosition = null;
 }
 }

 [HarmonyPatch(typeof(InventoryGui), "Show")]
 public static class InventoryGui_Show_ResetContainerPosition_Patch
 {
 static void Prefix() => InventoryGui_UpdateContainer_Patch.Reset();
 }

 [HarmonyPatch(typeof(InventoryGui), "Hide")]
 public static class InventoryGui_Hide_ResetContainerPosition_Patch
 {
 static void Prefix() => InventoryGui_UpdateContainer_Patch.Reset();
 }
}
