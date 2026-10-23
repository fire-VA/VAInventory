using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// The inventory's live character preview, bound to ANY Character. ADD-ONLY contract.
    /// The target itself is rendered (the camera is its child): what it wears, its real pose and idle, FCC model overrides included.
    /// Only the target is drawn (layer isolation around this camera's render); the key light is on only during that render; it renders
    /// only while the image is active (call Unbind or let the image go inactive). Orbit / zoom / pan / double-click reset on the image.
    /// Framing: the whole body from the target's renderer bounds (FCC creatures too).
    ///   var t = AccessTools.TypeByName("VerdantsAscent.Managers.VAInventoryPreviewApi");
    ///   object h = t.GetMethod("Bind").Invoke(null, new object[] { npcCharacter, rawImage, "FiresRPGmaker.Dressing" });
    ///   t.GetMethod("Refresh").Invoke(null, new[] { h });   t.GetMethod("Unbind").Invoke(null, new[] { h });
    /// </summary>
    public static class VAInventoryPreviewApi
    {
        /// <summary>A live preview of <paramref name="target"/> in <paramref name="image"/>; null (logged) when either is missing.</summary>
        public static object Bind(Character target, RawImage image, string owner)
        {
            var p = new LivePreview(owner ?? "api");
            if (!p.Bind(target, image, true))
            {
                EquipmentPanel.PanelLog?.LogDebug($"preview api: bind by {owner} REFUSED (target={(target != null)}, image={(image != null)})");
                return null;
            }
            EquipmentPanel.PanelLog?.LogDebug($"preview api: '{target.name}' bound for {owner}");
            return p;
        }

        /// <summary>Stops and frees the preview (its camera, light and texture).</summary>
        public static void Unbind(object handle)
        {
            if (handle is LivePreview p) { p.Teardown(); EquipmentPanel.PanelLog?.LogDebug($"preview api: unbound {p.Name}"); }
        }

        /// <summary>Re-fits the texture to the image and re-frames from the target's bounds (after an equipment / model change).</summary>
        public static void Refresh(object handle)
        {
            if (handle is LivePreview p) { p.Refit(); p.Reframe(); }
        }
    }
}
