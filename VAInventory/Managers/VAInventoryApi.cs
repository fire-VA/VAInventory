using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.105 (Fire: while RPGmaker's NPC admin book is open, the VA player panel hides and only the inventory grid stays).
    /// Reference-counted: the player panel is hidden while ANY owner holds a suppression and shown again when the last one
    /// releases. Owners are plain strings (e.g. "FiresRPGmaker.NpcBook"); suppressing twice with the same owner counts once.
    /// Hiding is a CanvasGroup (invisible, not clickable), so layout, slots and saved positions are untouched.
    /// </summary>
    public static class VAInventoryApi
    {
        static readonly HashSet<string> _owners = new HashSet<string>();

        /// <summary>True while at least one owner suppresses the player panel.</summary>
        public static bool PlayerPanelSuppressed => _owners.Count > 0;

        public static void SuppressPlayerPanel(string owner)
        {
            if (string.IsNullOrEmpty(owner) || !_owners.Add(owner)) return;
            EquipmentPanel.PanelLog?.LogDebug($"player panel: suppressed by {owner} ({_owners.Count} owner(s))");
            UnifiedScreen.ApplySuppression();
        }

        public static void ReleasePlayerPanel(string owner)
        {
            if (string.IsNullOrEmpty(owner) || !_owners.Remove(owner)) return;
            EquipmentPanel.PanelLog?.LogDebug($"player panel: released by {owner} ({_owners.Count} owner(s) left)");
            UnifiedScreen.ApplySuppression();
        }
    }
}
