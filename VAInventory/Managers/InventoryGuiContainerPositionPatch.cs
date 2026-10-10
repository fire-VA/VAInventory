using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // The chest panel (Fire, 2026-09-27). It opens below the carry grid's real bottom, whose backdrop reaches past
    // vanilla's rows to cover the extra ones, with room between the two. The player can drag it anywhere by its backdrop
    // (UniversalDragHandler): the dragged offset is kept as [UI] ContainerOffset and applied each time a chest opens. The
    // spot is taken on the panel's first visible frame after the inventory shows, then held there every frame except
    // while it is being dragged (the old hold kept only the first frame's x, which allowed dragging up and down only).
    //
    // 1.0.49 (Fire, 2026-09-30: "containers just dont show when opened"): the spot is worked out from vanilla's own
    // position, read once per InventoryGui before this patch ever moves the panel. It used to start from the panel's
    // CURRENT position, which already held last time's offset, so every chest opened added ContainerOffset again: with
    // Fire's (-4.5, -209) the panel sank ~209 units per chest until it was off the screen for the rest of the session.
    // The panel is also kept fully on screen, so no saved offset can lose it again.
    //
    // 1.0 (Fire, 2026-10-07): the default spot is now the panel's top exactly GridGap below the carry grid's lowest
    // visible edge (its backdrop or the resized m_gridRoot, whichever is lower), re-anchored whenever that edge moves
    // while open (extra rows, bag changes). A drag stores the panel's absolute spot as [UI] ContainerWindowPosition
    // ("x,y", empty = default; the old ContainerOffset is no longer applied). Right-click the backdrop to reset.
    [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
    public static class InventoryGui_UpdateContainer_Patch
    {
        private const float GridGap = 8f;
        private const float BottomMoveEpsilon = 0.5f;
        private const string BackgroundName = "Bkg";
        private const int BottomLeftCorner = 0;
        private const int TopLeftCorner = 1;
        private const int TopRightCorner = 2;

        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VAInventory.Panel");

        private static Vector2? s_home;
        private static bool s_homeIsSaved;
        private static float s_lastGridBottom = float.NaN;
        private static RectTransform s_baseOf;
        private static Vector2 s_base;
        private static readonly Vector3[] s_corners = new Vector3[4];

        private static void Postfix(InventoryGui __instance)
        {
            var container = __instance.m_container;
            if (container == null || !container.gameObject.activeSelf) return;
            if (!ReferenceEquals(s_baseOf, container))
            {
                // A new InventoryGui (each world load): its panel still stands where vanilla put it.
                s_baseOf = container;
                s_base = container.anchoredPosition;
                s_home = null;
            }
            var handle = container.GetComponentInChildren<UniversalDragHandler>(true);
            if (handle != null)
            {
                if (handle.OnPositionChanged == null) handle.OnPositionChanged = SavePosition;
            }
            if (handle != null && handle.IsDragging) return;
            if (!s_home.HasValue)
            {
                if (TryGetSaved(out Vector2 saved))
                {
                    s_home = saved;
                    s_homeIsSaved = true;
                    Log.LogDebug($"container pos: saved ({saved.x:0.#},{saved.y:0.#})");
                }
                else
                {
                    s_homeIsSaved = false;
                    s_home = BelowGrid(__instance, container, out float bottom);
                    Log.LogDebug($"container pos: default-anchored ({s_home.Value.x:0.#},{s_home.Value.y:0.#}) below grid bottom {bottom:0.#}");
                }
            }
            else if (!s_homeIsSaved)
            {
                // Cheap re-check: the grid's bottom moves when rows are added or a bag changes while the chest is open.
                float bottom = GridBottom(__instance);
                if (!float.IsNaN(bottom) && Mathf.Abs(bottom - s_lastGridBottom) > BottomMoveEpsilon)
                    s_home = BelowGrid(__instance, container, out _);
            }
            container.anchoredPosition = s_home.Value;
            KeepOnScreen(container);
        }

        // The carry grid's lowest visible edge in world space: its backdrop, or m_gridRoot when the extra rows reach
        // past it. NaN when neither exists.
        private static float GridBottom(InventoryGui gui)
        {
            float bottom = float.NaN;
            var bkg = gui.m_player != null ? gui.m_player.Find(BackgroundName) as RectTransform : null;
            if (bkg != null)
            {
                bkg.GetWorldCorners(s_corners);
                bottom = s_corners[BottomLeftCorner].y;
            }
            var root = gui.m_playerGrid != null ? gui.m_playerGrid.m_gridRoot : null;
            if (root != null && root.gameObject.activeInHierarchy)
            {
                root.GetWorldCorners(s_corners);
                float y = s_corners[BottomLeftCorner].y;
                if (float.IsNaN(bottom) || y < bottom) bottom = y;
            }
            return bottom;
        }

        // Vanilla's x, with the panel's top GridGap (scaled) below the carry grid's bottom.
        private static Vector2 BelowGrid(InventoryGui gui, RectTransform container, out float gridBottom)
        {
            container.anchoredPosition = s_base;
            gridBottom = GridBottom(gui);
            s_lastGridBottom = gridBottom;
            var parent = container.parent as RectTransform;
            if (float.IsNaN(gridBottom) || parent == null) return s_base;
            container.GetWorldCorners(s_corners);
            float top = s_corners[TopLeftCorner].y;
            float scale = Mathf.Max(parent.lossyScale.y, 1e-4f);
            float drop = top - (gridBottom - GridGap * scale);
            return s_base - new Vector2(0f, drop / scale);
        }

        private static bool TryGetSaved(out Vector2 position)
        {
            position = Vector2.zero;
            string raw = ConfigManager.Instance?.configContainerWindowPosition?.Value;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var parts = raw.Split(',');
            if (parts.Length != 2) return false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.Float;
            if (!float.TryParse(parts[0].Trim(), style, inv, out float x) || !float.TryParse(parts[1].Trim(), style, inv, out float y))
                return false;
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)) return false;
            position = new Vector2(x, y);
            return true;
        }

        // Moves the panel back inside its canvas when the spot plus the saved offset would put any edge off the screen.
        private static void KeepOnScreen(RectTransform container)
        {
            var canvas = container.GetComponentInParent<Canvas>();
            var screen = canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
            var parent = container.parent as RectTransform;
            if (screen == null || parent == null) return;
            screen.GetWorldCorners(s_corners);
            Vector2 min = s_corners[BottomLeftCorner];
            Vector2 max = s_corners[TopRightCorner];
            container.GetWorldCorners(s_corners);
            Vector2 low = s_corners[BottomLeftCorner];
            Vector2 high = s_corners[TopRightCorner];
            Vector2 shift = Vector2.zero;
            if (high.x > max.x) shift.x = max.x - high.x;
            if (low.x + shift.x < min.x) shift.x = min.x - low.x;
            if (high.y > max.y) shift.y = max.y - high.y;
            if (low.y + shift.y < min.y) shift.y = min.y - low.y;
            if (shift == Vector2.zero) return;
            Vector3 lossy = parent.lossyScale;
            container.anchoredPosition += new Vector2(shift.x / Mathf.Max(lossy.x, 1e-4f), shift.y / Mathf.Max(lossy.y, 1e-4f));
        }

        private static void SavePosition(Vector2 position)
        {
            var config = ConfigManager.Instance;
            if (config?.configContainerWindowPosition == null) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            config.configContainerWindowPosition.Value = position.x.ToString("0.##", inv) + "," + position.y.ToString("0.##", inv);
            config.NotifyLocalConfigSave();
            s_home = position;
            s_homeIsSaved = true;
            Log.LogDebug($"container pos: saved ({position.x:0.#},{position.y:0.#})");
        }

        // The [UI] ResetContainerWindowPosition config "button" (Fire 10-07: no right-click reset, too easy to hit by
        // accident): forget the dragged spot and open below the grid again.
        internal static void ResetToDefault()
        {
            var config = ConfigManager.Instance;
            if (config?.configContainerWindowPosition != null)
            {
                config.configContainerWindowPosition.Value = "";
                config.NotifyLocalConfigSave();
            }
            Reset();
            Log.LogDebug("container pos: reset to default");
        }

        // On Show and Hide: the next chest takes its spot afresh.
        public static void Reset()
        {
            s_home = null;
            s_homeIsSaved = false;
            s_lastGridBottom = float.NaN;
        }
    }
}
