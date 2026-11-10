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

        // 1.0.372 (Fire 10-30: "why the FUCK is the ammo hotbar defaulting to the fucking center of the screen"; his log: the ammo bar
        // registered with Core's dock, "placement owned by Core (VAInventory stands down)", then sat at anchored (1290,-540) = mid-screen):
        // ApplyTransform's first Manage call registered the bar with Core BEFORE the config placement ran, so Core (no saved spot for
        // the ".classic" id) kept the bar where the HotKeyBar clone happened to be. Now each bar is put at its config spot (bottom left,
        // stacked above the health HUD) once, before Core first sees it. Not docked, not forced again: a saved player spot still wins.
        private static readonly System.Collections.Generic.HashSet<string> s_started = new System.Collections.Generic.HashSet<string>();
        internal static void StartSpot(string name, RectTransform bar, ConfigEntry<Vector2> position, float scale)
        {
            if (bar == null || position == null || !s_started.Add(name)) return;
            Apply(bar, position, scale);
            // Fire's 0005 log: "live hit-test FAIL vainv.hotbar2.ammo.classic ... top nothing", the press under the bar went to vanilla's
            // HoverName text (canvas HUD order 400) lying over it. Hover text is display only: it no longer catches presses.
            var hn = Hud.instance != null && Hud.instance.m_hoverName != null ? Hud.instance.m_hoverName.GetComponent<UnityEngine.UI.Graphic>() : null;
            if (hn != null && hn.raycastTarget) { hn.raycastTarget = false; EquipmentPanel.PanelLog?.LogDebug("[Hotbars] vanilla HoverName text no longer catches presses (it lay over the hotbars and ate their drag)"); }
            var c = new Vector3[4]; bar.GetWorldCorners(c);
            EquipmentPanel.PanelLog?.LogDebug($"[Hotbars] {name}: start spot = config {position.Value} (bottom left above the health HUD), screen rect ({c[0].x:0},{c[0].y:0})-({c[2].x:0},{c[2].y:0}) of {Screen.width}x{Screen.height}; placed before Core's dock registers it (a saved spot of the player's still wins)");
        }

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
            bar.anchoredPosition = ClampOnScreen(bar, saved);   // The default spot is always fully on screen
        }

        internal static Vector2 ClampOnScreen(RectTransform bar, Vector2 anchored)
        {
            if (!TryGetScreenArea(bar, out Rect area)) return anchored;
            return new Vector2(
                Mathf.Clamp(anchored.x, -MinimumVisibleSpan, area.width - MinimumVisibleSpan),
                Mathf.Clamp(anchored.y, MinimumVisibleSpan, area.height + MinimumVisibleSpan));
        }

        // Every 10 s, one Debug line per bar with
        // its state, and a bar whose screen rect left the screen (any aspect ratio: 3440x1440 included) is put back on it.
        // A bar's state line is logged once at start and again only when it
        // changes; a bar moved back on-screen logs that move once (below).
        static readonly System.Collections.Generic.Dictionary<string, string> lastState = new System.Collections.Generic.Dictionary<string, string>();
        static void LogOnChange(string name, string line)
        {
            if (lastState.TryGetValue(name, out var was) && was == line) return;
            lastState[name] = line;
            EquipmentPanel.PanelLog?.LogDebug(line);
        }
        static readonly Vector3[] corners = new Vector3[4];
        static readonly System.Collections.Generic.Dictionary<string, (bool, int, Vector2, float, float, int, int, int, int, int, int)> lastKey =
            new System.Collections.Generic.Dictionary<string, (bool, int, Vector2, float, float, int, int, int, int, int, int)>();

        internal static void Report(string name, RectTransform bar, string hiddenWhy, int items)
        {
            if (bar == null)
            {
                LogOnChange(name, $"[Hotbars] {name}: no bar ({hiddenWhy})");
                return;
            }
            // The nearest Canvas can be the bar's own
            // nested one, whose worldCamera is null while the HUD's root canvas renders through a camera; the screen point must
            // come from the ROOT canvas and its camera (null only for an overlay canvas).
            var canvas = bar.GetComponentInParent<Canvas>()?.rootCanvas;
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            bar.GetWorldCorners(corners);
            Vector2 min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
            bool inside = min.x >= 0f && min.y >= 0f && max.x <= Screen.width && max.y <= Screen.height;
            if (!inside && bar.parent is RectTransform && !HotbarHudDock.Owns(bar))   // Never while Core's dock owns the bar (it fought every drag)
            {
                Vector2 before = bar.anchoredPosition;
                Vector2 after = ClampOnScreen(bar, before);
                if ((after - before).sqrMagnitude > 0.01f)   // Only a real move is applied and logged
                {
                    bar.anchoredPosition = after;
                    EquipmentPanel.PanelLog?.LogDebug($"[Hotbars] {name}: screen rect ({min.x:0},{min.y:0})-({max.x:0},{max.y:0}) was off the {Screen.width}x{Screen.height} screen; moved {before} -> {after}");
                }
            }
            // The raw values are compared first; the line (a large string, every HUD frame) is only formatted when one of them changed.
            var key = (bar.gameObject.activeInHierarchy, items, bar.anchoredPosition, bar.localScale.x, canvas != null ? canvas.scaleFactor : 0f,
                       Mathf.RoundToInt(min.x), Mathf.RoundToInt(min.y), Mathf.RoundToInt(max.x), Mathf.RoundToInt(max.y), Screen.width, Screen.height);
            if (lastKey.TryGetValue(name, out var was) && was.Equals(key)) return;
            lastKey[name] = key;
            LogOnChange(name, $"[Hotbars] {name}: active {bar.gameObject.activeInHierarchy}, items {items}, anchored {bar.anchoredPosition}, scale {bar.localScale.x:0.##}, canvas scale {(canvas != null ? canvas.scaleFactor : 0f):0.##}, screen rect ({min.x:0},{min.y:0})-({max.x:0},{max.y:0}) of {Screen.width}x{Screen.height}, inside {inside}");
        }

        // The lowest bar's bottom edge is
        // kept GlobeClearance above the top of the vanilla vitals (health panel, stamina and eitr roots, children included),
        // measured live in the bars' parent space. The whole stack moves up together by the shortfall; nothing moves when it
        // already clears.
        const float GlobeClearance = 8f;
        static float lastShift = float.NaN;
        static readonly string[] VitalFields = { "m_healthPanel", "m_staminaBar2Root", "m_eitrBarRoot" };
        // KeepAboveVitals runs every HUD frame - the FieldInfos are looked up once and the Graphic lists reuse
        // one buffer instead of a new array per root per frame.
        static readonly System.Collections.Generic.Dictionary<string, System.Reflection.FieldInfo> s_vitalFields = new System.Collections.Generic.Dictionary<string, System.Reflection.FieldInfo>();
        static readonly System.Collections.Generic.List<UnityEngine.UI.Graphic> s_graphics = new System.Collections.Generic.List<UnityEngine.UI.Graphic>();
        static System.Reflection.FieldInfo VitalField(string name)
        {
            if (!s_vitalFields.TryGetValue(name, out var f)) s_vitalFields[name] = f = HarmonyLib.AccessTools.Field(typeof(Hud), name);
            return f;
        }

        internal static void KeepAboveVitals(params RectTransform[] bars)
        {
            // The rects' relative bounds did not
            // match what is drawn. Now the DRAWN images count: every active, enabled Graphic under the vitals roots and under the
            // bars, compared in world space, the shift converted into the bars' parent space. Both edges are logged every 10 s.
            var hud = Hud.instance;
            RectTransform parent = null;
            foreach (var b in bars) if (b != null && b.gameObject.activeInHierarchy) { parent = b.parent as RectTransform; break; }
            if (hud == null || parent == null) return;
            float top = float.NegativeInfinity;
            foreach (string field in VitalFields)
            {
                var rt = VitalField(field)?.GetValue(hud) as RectTransform;
                if (rt == null || !rt.gameObject.activeInHierarchy) continue;
                rt.GetComponentsInChildren(false, s_graphics);
                foreach (var g in s_graphics)
                {
                    if (!g.enabled || g.color.a <= 0.01f) continue;
                    g.rectTransform.GetWorldCorners(corners);
                    top = Mathf.Max(top, corners[1].y);
                }
            }
            float lowest = float.PositiveInfinity;
            foreach (var b in bars)
            {
                if (b == null || !b.gameObject.activeInHierarchy) continue;
                b.GetComponentsInChildren(false, s_graphics);
                foreach (var g in s_graphics)
                {
                    if (!g.enabled || g.color.a <= 0.01f) continue;
                    g.rectTransform.GetWorldCorners(corners);
                    lowest = Mathf.Min(lowest, corners[0].y);
                }
            }
            if (float.IsNegativeInfinity(top) || float.IsPositiveInfinity(lowest)) return;
            float clearanceWorld = parent.TransformVector(new Vector3(0f, GlobeClearance, 0f)).y;
            float shiftWorld = top + clearanceWorld - lowest;
            if (Time.unscaledTime >= nextVitalsLog)
            {
                nextVitalsLog = Time.unscaledTime + 10f;
                EquipmentPanel.PanelLog?.LogDebug($"[Hotbars] vitals drawn top {top:0.##} (world), lowest bar drawn bottom {lowest:0.##}, clearance {clearanceWorld:0.##}, shortfall {shiftWorld:0.##}");
            }
            if (shiftWorld <= 0.01f * Mathf.Max(0.0001f, clearanceWorld)) return;
            float shiftLocal = parent.InverseTransformVector(new Vector3(0f, shiftWorld, 0f)).y;
            foreach (var b in bars)
                if (b != null && b.parent == parent) b.anchoredPosition += new Vector2(0f, shiftLocal);
            if (Mathf.Abs(shiftLocal - lastShift) > 0.5f)
            {
                lastShift = shiftLocal;
                EquipmentPanel.PanelLog?.LogDebug($"[Hotbars] stack moved up {shiftLocal:0} so the lowest bar clears the vitals' drawn top by {GlobeClearance:0}");
            }
        }

        static float nextVitalsLog;
        static Vector3 lastStack = new Vector3(float.NaN, 0f, 0f);

        // The stack is laid out from the Ammo
        // bar's saved position: Tool at that height, Food one pitch above, Ammo two pitches above (pitch = the saved Food-Tool
        // gap, or the default bar pitch), each keeping its own x. Nothing sits below the old Ammo row.
        internal static void StackFromAmmo(RectTransform tool, RectTransform food, RectTransform ammo,
            Vector2 toolSaved, Vector2 foodSaved, Vector2 ammoSaved, float defaultPitch)
        {
            // 1.0.373 (Fire 10-30): tools at the bottom, ammo above, food on top; every bar keeps its slot whether the others show or not
            float pitch = Mathf.Abs(ammoSaved.y - toolSaved.y);
            if (pitch < 1f) pitch = defaultPitch;
            float baseY = toolSaved.y;
            if (tool != null) tool.anchoredPosition = new Vector2(toolSaved.x, baseY);
            if (ammo != null) ammo.anchoredPosition = new Vector2(ammoSaved.x, baseY + pitch);
            if (food != null) food.anchoredPosition = new Vector2(foodSaved.x, baseY + 2f * pitch);
            var key = new Vector3(baseY, pitch, (tool != null ? 1 : 0) + (food != null ? 2 : 0) + (ammo != null ? 4 : 0));
            if (key != lastStack)
            {
                lastStack = key;
                EquipmentPanel.PanelLog?.LogDebug($"[Hotbars] stack from the Tool position: Tool y {baseY:0}, Ammo y {baseY + pitch:0}, Food y {baseY + 2f * pitch:0} (pitch {pitch:0})");
            }
        }
        internal static void ReportDone()
        {
        }

        private static bool IsOnScreen(RectTransform bar, Vector2 anchored) => ClampOnScreen(bar, anchored) == anchored;

        private static bool TryGetScreenArea(RectTransform bar, out Rect area)
        {
            area = bar.parent is RectTransform parent ? parent.rect : default;
            return area.width > 0f && area.height > 0f;
        }
    }
}
