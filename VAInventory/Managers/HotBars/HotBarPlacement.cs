using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Managers.HotBars
{
    // One placement rule for all three bars: anchored bottom-left, and never left where the player cannot see it. A
    // saved position that falls off screen (an old coordinate system, a stray drag) puts the bar back in its default
    // spot in the corner.
    internal static class HotBarPlacement
    {
        private const float MinimumVisibleSpan = 32f;

        internal static void Apply(RectTransform bar, ConfigEntry<Vector2> position, float scale)
        {
            bar.localScale = Vector3.one * scale;
            bar.anchorMin = Vector2.zero;
            bar.anchorMax = Vector2.zero;

            Vector2 saved = position.Value;
            if (!IsOnScreen(bar, saved))
            {
                var fallback = (Vector2)position.DefaultValue;
                Debug.LogWarning($"[VAInventory] {bar.name}: saved position {saved} is off screen; restored the default {fallback}.");
                position.Value = fallback;
                saved = fallback;
            }
            bar.anchoredPosition = saved;
        }

        internal static Vector2 ClampOnScreen(RectTransform bar, Vector2 anchored)
        {
            if (!TryGetScreenArea(bar, out Rect area)) return anchored;
            return new Vector2(
                Mathf.Clamp(anchored.x, -MinimumVisibleSpan, area.width - MinimumVisibleSpan),
                Mathf.Clamp(anchored.y, MinimumVisibleSpan, area.height + MinimumVisibleSpan));
        }

        private static bool IsOnScreen(RectTransform bar, Vector2 anchored) => ClampOnScreen(bar, anchored) == anchored;

        private static bool TryGetScreenArea(RectTransform bar, out Rect area)
        {
            area = bar.parent is RectTransform parent ? parent.rect : default;
            return area.width > 0f && area.height > 0f;
        }
    }
}
