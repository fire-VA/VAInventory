using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace VerdantsAscent.Managers
{
 [HarmonyPatch]
 public static class SafeGuiPatches
 {
 private static int _ig_exceptionCount =0;
 private static float _ig_lastLogTime =0f;
 private static bool _ig_diagnosticDumped = false;

 private static int _igui_exceptionCount =0;
 private static float _igui_lastLogTime =0f;
 // Rate-limit for drag-state warnings
 private static int _ig_drag_state_count =0;
 private static float _ig_drag_state_last =0f;

 // Frame number on which we last cancelled InventoryGrid.UpdateGui. Other mods that
 // mirror m_elements (e.g. FiresRPGmaker's UIOverrideSlotSystem heuristic discovery)
 // can read `Time.frameCount == LastSuppressedFrame` to know that this frame's
 // m_elements may be stale and skip their mirror update for one frame.
 public static int LastSuppressedFrame { get; private set; } = -1;
 public static bool WasSuppressedThisFrame() => Time.frameCount == LastSuppressedFrame;

 // Prefix: skip InventoryGrid.UpdateInventory if the provided inventory is null
 [HarmonyPatch(typeof(InventoryGrid), "UpdateInventory")]
 [HarmonyPrefix]
  public static bool InventoryGrid_UpdateInventory_Prefix(Inventory inventory)
 {
 if (inventory == null)
 {
 if (ConfigManager.Instance != null && ConfigManager.IsVerbose)
 {
 Debug.Log("[VAInventory] Skipping InventoryGrid.UpdateInventory because inventory==null");
 }
 return false;
 }
 return true;
 }

 // Returns false when obvious internal state is invalid and vanilla UpdateGui must not run.
  public static bool AllowUpdateGui(InventoryGrid __instance)
 {
 try
 {
 if (__instance == null) { LastSuppressedFrame = Time.frameCount; return false; }

 var inv = __instance.GetInventory();
 if (inv == null)
 {
 if (ConfigManager.Instance != null && ConfigManager.IsVerbose)
 Debug.Log("[VAInventory] Skipping InventoryGrid.UpdateGui because Inventory==null");
 LastSuppressedFrame = Time.frameCount;
 return false;
 }

 // Minimal defensive check: skip UpdateGui when InventoryGui reports a drag inventory but no drag item
 var invGui = InventoryGui.instance;
 if (invGui != null)
 {
 var fi_dragItem = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
 var fi_dragInventory = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
 var fi_dragGo = AccessTools.Field(typeof(InventoryGui), "m_dragGo");
 var fi_dragAmount = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
 var curDragItem = fi_dragItem?.GetValue(invGui) as ItemDrop.ItemData;
                    if (fi_dragInventory?.GetValue(invGui) is Inventory curDragInv && curDragItem == null)
                    {
                        // Rate-limit logs to avoid spam
                        const float logInterval = 2f;
                        const int maxImmediate = 3;
                        if (_ig_drag_state_count < maxImmediate || Time.realtimeSinceStartup - _ig_drag_state_last > logInterval)
                        {
                            _ig_drag_state_count++;
                            _ig_drag_state_last = Time.realtimeSinceStartup;
                            if (ConfigManager.Instance != null && ConfigManager.IsVerbose)
                                Debug.Log("[VAInventory] Skipping InventoryGrid.UpdateGui because InventoryGui has m_dragInventory set but m_dragItem is null");
                        }

                        // Attempt a safe recovery: clear inconsistent drag state to allow GUI updates to proceed.
                        try
                        {
                            // Only clear if dragInventory references player inventory (conservative)
                            if (curDragInv == Slots.PlayerInventory)
                            {
                                try { fi_dragGo?.SetValue(invGui, null); } catch { }
                                try { fi_dragInventory?.SetValue(invGui, null); } catch { }
                                try { fi_dragItem?.SetValue(invGui, null); } catch { }
                                try { fi_dragAmount?.SetValue(invGui, 0); } catch { }
                            }
                        }
                        catch { }

                        LastSuppressedFrame = Time.frameCount;
                        return false;
                    }
                }

 var fi_elements = AccessTools.Field(typeof(InventoryGrid), "m_elements");
 if (fi_elements != null)
 {
                    if (fi_elements.GetValue(__instance) is IList elements && elements.Count > 0)
                    {
                        // This guard stops vanilla's UpdateGui from dereferencing a half-built element. It was
                        // nested inside a GetNestedType("Element") lookup that returns NULL on Valheim 1.0, so
                        // the whole guard quietly stopped running the moment 1.0 landed. InventoryElement is a
                        // top-level MonoBehaviour now, so the check is a plain cast and its GameObject is
                        // intrinsic rather than an m_go field.
                        try
                        {
                            var first = elements[0];
                            if (first == null)
                            {
                                if (ConfigManager.Instance != null && ConfigManager.IsVerbose)
                                    Debug.Log("[VAInventory] Skipping InventoryGrid.UpdateGui because first m_elements entry is null");
                                LastSuppressedFrame = Time.frameCount;
                                return false;
                            }
                            var go = (first as InventoryElement)?.gameObject;
                            if (go == null)
                            {
                                if (ConfigManager.Instance != null && ConfigManager.IsVerbose)
                                    Debug.Log("[VAInventory] Skipping InventoryGrid.UpdateGui because the first element has no GameObject");
                                LastSuppressedFrame = Time.frameCount;
                                return false;
                            }
                        }
                        catch
                        {
                            // element inspection failed; allow original to run
                        }
                    }
                }
 }
 catch
 {
 // ignore and run original
 }

 return true;
 }

 // Catch exceptions from InventoryGrid.UpdateGui and emit one-time diagnostic dump
 public static Exception UpdateGuiFinalizer(InventoryGrid __instance, Exception __exception)
 {
 if (__exception == null) return null;

 try
 {
 // Rate-limit summary logs to avoid spamming
 const float logInterval =2f; // seconds
 const int maxImmediate =3; // always allow first few logs
 if (_ig_exceptionCount < maxImmediate || Time.realtimeSinceStartup - _ig_lastLogTime > logInterval)
 {
 _ig_exceptionCount++;
 _ig_lastLogTime = Time.realtimeSinceStartup;
 Debug.LogError("[VAInventory] Caught exception in InventoryGrid.UpdateGui: " + __exception);
 }

 // One-time detailed diagnostic dump (helps root-cause) - emit only once
 if (!_ig_diagnosticDumped)
 {
 _ig_diagnosticDumped = true;
 try
 {
 Debug.LogError("[VAInventory] InventoryGrid.UpdateGui: performing one-time diagnostic dump");

 // Full exception text
 try { Debug.LogError("[VAInventory] Exception detail: " + __exception.ToString()); } catch { }

 // InventoryGui state (if available)
 try
 {
 var invGui = InventoryGui.instance;
 Debug.LogError("InventoryGui.instance=" + (invGui != null ? "present" : "null"));
 if (invGui != null)
 {
 var fi_dragItem = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
 var fi_dragInventory = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
 var fi_dragGo = AccessTools.Field(typeof(InventoryGui), "m_dragGo");
 var fi_dragAmount = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
                                var dragGo = fi_dragGo?.GetValue(invGui) as GameObject;
                                var dragAmount = fi_dragAmount?.GetValue(invGui);

 Debug.LogError(" m_dragItem=" + (fi_dragItem?.GetValue(invGui) is ItemDrop.ItemData dragItem ? (dragItem.m_shared != null ? dragItem.m_shared.m_name : "(no name)") : "null")
 + " m_dragInventory=" + (fi_dragInventory?.GetValue(invGui) is Inventory dragInv ? "present" : "null")
 + " m_dragGo=" + (dragGo != null ? dragGo.name : "null")
 + " m_dragAmount=" + (dragAmount != null ? dragAmount.ToString() : "null"));

 if (dragGo != null)
 {
 try
 {
 var comps = dragGo.GetComponents<Component>();
 var compNames = new List<string>();
 for (int i =0; i < comps.Length; i++) compNames.Add(comps[i] != null ? comps[i].GetType().FullName : "(null)");
 Debug.LogError(" m_dragGo components: " + string.Join(", ", compNames));

 var parent = dragGo.transform.parent;
 int depth =0;
 while (parent != null && depth <6)
 {
 Debug.LogError(" m_dragGo parent[" + depth + "] = " + parent.name + " active=" + parent.gameObject.activeSelf);
 parent = parent.parent;
 depth++;
 }
 }
 catch (Exception ge)
 {
 Debug.LogError("[VAInventory] Error inspecting m_dragGo: " + ge);
 }
 }
 }
 }
 catch (Exception e)
 {
 Debug.LogError("[VAInventory] Error reading InventoryGui drag fields: " + e);
 }

                        // InventoryGrid internal elements (if available)
                        try
                        {
                            if (__instance != null)
                            {
                                Debug.LogError("InventoryGrid instance: go=" + (__instance.gameObject != null ? __instance.gameObject.name : "(no go)") + " active=" + (__instance.gameObject != null ? __instance.gameObject.activeSelf.ToString() : "n/a"));
                                var inv = __instance.GetInventory();
                                Debug.LogError(" InventoryGrid.GetInventory()=" + (inv != null ? "present" : "null") + (inv != null ? (" size=" + (inv.GetAllItems()?.Count ?? -1)) : ""));

                                var fi_elements = AccessTools.Field(typeof(InventoryGrid), "m_elements");
                                if (fi_elements != null)
                                {
                                    var elements = fi_elements.GetValue(__instance) as IList;
                                    Debug.LogError(" m_elements=" + (elements != null ? elements.Count.ToString() : "null"));
                                    if (elements != null)
                                    {
                                        // 1.0: InventoryElement replaced the nested Element; GameObject is
                                        // intrinsic and m_icon is public, so this dump needs no reflection.
                                        int toInspect = Math.Min(elements.Count, 12);
                                        for (int i = 0; i < toInspect; i++)
                                        {
                                            try
                                            {
                                                var elem = elements[i];
                                                if (elem == null)
                                                {
                                                    Debug.LogError(" m_elements[" + i + "] == null");
                                                    continue;
                                                }

                                                var inventoryElement = elem as InventoryElement;
                                                GameObject go = inventoryElement != null ? inventoryElement.gameObject : null;
                                                string goName = go != null ? go.name : (inventoryElement == null ? "(not an InventoryElement)" : "null");
                                                Debug.LogError(" m_elements[" + i + "] go=" + goName + (go != null ? (" active=" + go.activeSelf) : ""));

                                                if (go != null)
                                                {
                                                    try
                                                    {
                                                        var comps = go.GetComponents<Component>();
                                                        var compNames = new List<string>();
                                                        for (int cidx = 0; cidx < comps.Length; cidx++) compNames.Add(comps[cidx] != null ? comps[cidx].GetType().FullName : "(null)");
                                                        Debug.LogError(" components: " + string.Join(", ", compNames));

                                                        if (inventoryElement != null)
                                                        {
                                                            var icon = inventoryElement.m_icon;
                                                            Debug.LogError(" icon=" + (icon != null ? (icon.sprite != null ? icon.sprite.name : "(sprite null)") : "null"));
                                                        }

                                                        int cc = go.transform.childCount;
                                                        var childNames = new List<string>();
                                                        for (int j = 0; j < cc; j++) childNames.Add(go.transform.GetChild(j).name);
                                                        Debug.LogError(" children: " + (childNames.Count > 0 ? string.Join(", ", childNames) : "(none)"));
                                                    }
                                                    catch (Exception inspectEx)
                                                    {
                                                        Debug.LogError(" exception inspecting element GO: " + inspectEx);
                                                    }
                                                }
                                            }
                                            catch (Exception ee)
                                            {
                                                Debug.LogError(" exception inspecting m_elements[" + i + "]: " + ee);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogError("[VAInventory] Error reading InventoryGrid state: " + e);
                        }
 }
 catch (Exception dumpEx)
 {
 Debug.LogError("[VAInventory] Exception during diagnostic dump: " + dumpEx);
 }
 }
 }
 catch
 {
 // ignore
 }

 // swallow original exception to avoid spam
 return null;
 }

 // Rate-limited logging for InventoryGui.Update exceptions
 public static Exception InventoryGuiUpdateFinalizer(Exception __exception)
 {
 if (__exception == null) return null;

 try
 {
 const float logInterval =5f;
 const int maxImmediate =2;
 if (_igui_exceptionCount < maxImmediate || Time.realtimeSinceStartup - _igui_lastLogTime > logInterval)
 {
 _igui_exceptionCount++;
 _igui_lastLogTime = Time.realtimeSinceStartup;
 Debug.LogError("[VAInventory] Caught exception in InventoryGui.Update: " + __exception);
 }
 }
 catch
 {
 // ignore
 }

 return null;
 }
 }
}
