using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// 1.0.236 (Fire 10-18, rarity_bg_hidden_by_equipped_blue: the blue "equipped" fill covered the item's rarity colour): every
    /// slot's 'equiped' Image keeps its on/off logic (vanilla toggles .enabled, our side slots SetActive) but draws nothing itself;
    /// a thin blue inner border (4 x 2 px edges) shows "equipped" instead, so the rarity fill stays visible. Main grid (every
    /// InventoryGrid, incl. our rails) + our side slots. Hotbars are kit slots with no equipped state.
    /// </summary>
    internal sealed class EquippedBorder : MonoBehaviour
    {
        static readonly Color Blue = new Color(0.33f, 0.62f, 1f, 0.95f);
        const float Thick = 2f, Inset = 2f;
        Image _src; GameObject _frame; bool _shown;
        static bool s_logged;

        internal static void Ensure(Image eq)
        {
            if (eq == null || eq.GetComponent<EquippedBorder>() != null) return;
            var b = eq.gameObject.AddComponent<EquippedBorder>();
            b._src = eq;
            var c = eq.color; c.a = 0f; eq.color = c;   // the fill stops drawing; .enabled still says "equipped"
            b._frame = new GameObject("VAInvEquippedBorder", typeof(RectTransform));
            var fr = (RectTransform)b._frame.transform;
            fr.SetParent(eq.transform, false);
            fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one; fr.offsetMin = new Vector2(Inset, Inset); fr.offsetMax = new Vector2(-Inset, -Inset);
            Edge(fr, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -Thick), Vector2.zero);   // top
            Edge(fr, new Vector2(0, 0), new Vector2(1, 0), Vector2.zero, new Vector2(0, Thick));    // bottom
            Edge(fr, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, new Vector2(Thick, 0));    // left
            Edge(fr, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-Thick, 0), Vector2.zero);   // right
            b._shown = eq.enabled; b._frame.SetActive(b._shown);
            if (!s_logged) { s_logged = true; EquipmentPanel.PanelLog?.LogDebug("[VAInventory] equipped: slots show a thin blue inner border instead of the blue fill (rarity colour stays visible)"); }
        }

        static void Edge(RectTransform parent, Vector2 aMin, Vector2 aMax, Vector2 oMin, Vector2 oMax)
        {
            var go = new GameObject("Edge", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.offsetMin = oMin; rt.offsetMax = oMax;
            var img = go.GetComponent<Image>(); img.color = Blue; img.raycastTarget = false; img.sprite = null;
        }

        void LateUpdate()
        {
            if (_src == null || _frame == null) return;
            bool on = _src.enabled;
            if (on != _shown) { _shown = on; _frame.SetActive(on); }
            if (_src.color.a != 0f) { var c = _src.color; c.a = 0f; _src.color = c; }   // something re-tinted it: keep the fill off
        }

        [HarmonyPatch(typeof(InventoryGrid), "UpdateGui")]
        static class GridPatch
        {
            static void Postfix(InventoryGrid __instance)
            {
                var els = __instance != null ? __instance.m_elements : null;
                if (els == null) return;
                for (int i = 0; i < els.Count; i++) { var e = els[i]; if (e != null && e.m_equiped != null) Ensure(e.m_equiped); }
            }
        }
    }
}
