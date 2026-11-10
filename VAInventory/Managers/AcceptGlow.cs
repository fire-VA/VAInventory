using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// The NPC dressing room's drag feedback on VAInventory's slots (Fire 10-23): while an item is on the cursor, the slot under the
    /// mouse turns green when its drop would take the item and red when it wouldn't. Same look as the dressing room (its slot
    /// background recoloured, no outline, no fade). Each marked cell carries the SAME rule its drop uses; a rule that can't tell
    /// (null) shows no glow, so the glow never lies.
    /// </summary>
    internal static class AcceptGlow
    {
        internal static readonly Color Green = new Color(0.2f, 0.5f, 0.2f, 0.9f);   // dressing room "valid"
        internal static readonly Color Red = new Color(0.5f, 0.2f, 0.2f, 0.9f);     // dressing room "invalid"

        internal sealed class Cell : MonoBehaviour
        {
            internal Func<ItemDrop.ItemData, bool?> Rule;
            internal Image Target;
            internal string What;
        }

        static Image s_tinted;
        static Color s_before;
        static Cell s_logged;
        static readonly List<RaycastResult> s_hits = new List<RaycastResult>();
        static PointerEventData s_ped;

        /// <summary>Marks go as a glowing slot (idempotent; the rule is replaced on every call).</summary>
        internal static void Mark(GameObject go, Image target, string what, Func<ItemDrop.ItemData, bool?> rule)
        {
            if (go == null || target == null) return;
            var c = go.GetComponent<Cell>() ?? go.AddComponent<Cell>();
            c.Rule = rule; c.Target = target; c.What = what;
        }

        /// <summary>Every frame (DragDriver.LateUpdate).</summary>
        internal static void Tick()
        {
            var gui = InventoryGui.instance;
            var item = gui != null ? gui.m_dragItem : null;
            Cell cell = null;
            bool? ok = null;
            if (item != null && EventSystem.current != null)
            {
                s_ped ??= new PointerEventData(EventSystem.current);
                s_ped.position = Input.mousePosition;
                s_hits.Clear();
                EventSystem.current.RaycastAll(s_ped, s_hits);
                foreach (var h in s_hits)
                {
                    cell = h.gameObject != null ? h.gameObject.GetComponentInParent<Cell>() : null;
                    if (cell != null) break;
                }
                if (cell != null && cell.Rule != null)
                    try { ok = cell.Rule(item); } catch { ok = null; }
            }
            Image target = ok.HasValue ? cell.Target : null;
            if (target != s_tinted)
            {
                if (s_tinted != null) s_tinted.color = s_before;
                s_tinted = target;
                if (target != null) s_before = target.color;
            }
            if (s_tinted != null)
            {
                var want = ok.Value ? Green : Red;
                if (s_tinted.color != want) s_tinted.color = want;
            }
            if (cell != s_logged)
            {
                s_logged = cell;
                if (cell != null && item != null)
                    EquipmentPanel.PanelLog?.LogDebug($"[AcceptGlow] {item.m_shared.m_name} over {cell.What}: {(ok.HasValue ? (ok.Value ? "green (takes it)" : "red (refuses it)") : "no glow (the drop can't be told ahead)")}");
            }
        }
    }
}
