using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.290 (Fire 10-21, non-unified: the trash cell was white): on the trash button, each time it is enabled (every inventory open) and
    /// while the Fires UI override is OFF, the cell takes the Tools pouch cell's look - sprite, face colour, gold edge - through
    /// UnifiedScreen.PouchLook / ApplyLook, the same call every slot face uses. With the override ON it does nothing (Core's skin owns it).
    /// If the pouch look is not ready on that frame it tries again on the next enable; a one-line log says which look it took.
    /// </summary>
    internal sealed class TrashLook : MonoBehaviour
    {
        static bool s_logged;

        void OnEnable()
        {
            if (FiresCore.UI.Shell.FiresShell.OverrideOn) return;
            var img = GetComponent<Image>();
            if (img == null) return;
            if (UnifiedScreen.PouchLook(out var look) && UnifiedScreen.ApplyLook(img, look))
            {
                if (!s_logged) { s_logged = true; EquipmentPanel.PanelLog?.LogDebug($"panels: trash cell (Unified OFF) took the pouch cell look: sprite '{look.Sprite?.name}', face {look.Color}"); }
            }
            else
            {
                if (img.color == Color.white) img.color = new Color(0.227f, 0.165f, 0.071f, 1f);   // never white: the kit slot face until the pouch look exists
                if (!s_logged) { s_logged = true; EquipmentPanel.PanelLog?.LogDebug("panels: trash cell (Unified OFF): no pouch look yet, dark slot face used"); }
            }
        }
    }
}
