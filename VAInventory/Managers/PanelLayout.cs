using System;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.101 (Fire: "when the player panel is scaled, the companion panel scales alongside it so they are always the same
    /// size and clamped together"). Read-only view of the player panel for other mods: the scale VAInventory applied (the
    /// [UI] PlayerPanelScale config, possibly shrunk further to clear Crafting) and where the panel is on screen. Subscribe
    /// to <see cref="ScaleChanged"/> or poll <see cref="CurrentScale"/>; both are safe to use when the panel is closed.
    /// </summary>
    public static class PanelLayout
    {
        /// <summary>The player panel's applied uniform scale (1 = normal size).</summary>
        public static float CurrentScale { get; private set; } = 1f;

        /// <summary>Raised with the new applied scale whenever it changes.</summary>
        public static event Action<float> ScaleChanged;

        /// <summary>The player panel, or null while the Unified layout is not built.</summary>
        public static RectTransform Panel => UnifiedScreen.CharacterPanel;

        /// <summary>The panel's rectangle in screen pixels (bottom-left origin), or an empty rect when it is not shown.</summary>
        public static Rect PanelScreenRect
        {
            get
            {
                var panel = Panel;
                if (panel == null || !panel.gameObject.activeInHierarchy) return Rect.zero;
                var canvas = panel.GetComponentInParent<Canvas>()?.rootCanvas;
                Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                var corners = new Vector3[4];
                panel.GetWorldCorners(corners);
                Vector2 min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                Vector2 max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            }
        }

        /// <summary>Raised with the panel's new screen rect whenever it moves or resizes (a drag, the Crafting fit, a scale
        /// change, a screen resize). Checked while the panel is shown.</summary>
        public static event Action<Rect> LayoutChanged;

        static Rect lastRect;

        internal static void TickLayout()
        {
            if (LayoutChanged == null) return;
            Rect now = PanelScreenRect;
            if (now == Rect.zero || now == lastRect) return;
            lastRect = now;
            try { LayoutChanged.Invoke(now); }
            catch (Exception ex) { Debug.LogWarning($"[VAInventory] PanelLayout.LayoutChanged listener threw: {ex.Message}"); }
        }

        internal static void Report(float applied)
        {
            if (Mathf.Approximately(applied, CurrentScale)) return;
            CurrentScale = applied;
            try { ScaleChanged?.Invoke(applied); }
            catch (Exception ex) { Debug.LogWarning($"[VAInventory] PanelLayout.ScaleChanged listener threw: {ex.Message}"); }
        }
    }
}
