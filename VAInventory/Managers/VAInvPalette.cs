using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    /// <summary>
    /// The ONE palette every VAInventory
    /// grid and panel reads, instead of per-file colours. Approved mockup: Tools\vainv_readability\E_thin_gold_crafting_right.png
    /// .
    /// </summary>
    internal static class VAInvPalette
    {
        /// <summary>Panel background / grid plates (was 0.10/0.08/0.06, near-black).</summary>
        internal static readonly Color Background = new Color(0.18f, 0.145f, 0.105f, 1f);

        /// <summary>An empty slot's face: a step lighter than the background, so every square reads.</summary>
        internal static readonly Color SlotFace = new Color(0.235f, 0.195f, 0.145f, 1f);

        /// <summary>The Fires kit gold (#f0b44c), used for the faint slot outline and the rules.</summary>
        internal static readonly Color Gold = new Color(0.941f, 0.706f, 0.298f, 1f);

        /// <summary>Slot outline: 1 px, faint.</summary>
        internal const float EdgeAlpha = 0.30f;
        internal static readonly Vector2 EdgeDistance = new Vector2(1f, -1f);
        internal static Color Edge => new Color(Gold.r, Gold.g, Gold.b, EdgeAlpha);

        static Sprite s_slot;

        /// <summary>A FILLED rounded square (radius 5, 9-sliced), white, tinted to <see cref="SlotFace"/>. Filled on purpose: the kit's
        /// outline-only pill let whatever was behind the main grid show through as black.</summary>
        internal static Sprite SlotSprite
        {
            get
            {
                if (s_slot != null) return s_slot;
                const int size = 32, r = 5;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "VAInvSlotFill" };
                var px = new Color32[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float dx = Mathf.Max(r - x - 0.5f, x + 0.5f - (size - r), 0f), dy = Mathf.Max(r - y - 0.5f, y + 0.5f - (size - r), 0f);
                        float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                        px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                    }
                tex.SetPixels32(px); tex.Apply(false, true);
                s_slot = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r + 1, r + 1, r + 1, r + 1));
                s_slot.name = "VAInvSlotFill";
                return s_slot;
            }
        }

        /// <summary>Gives an image the faint 1 px gold slot outline (adds or updates its Outline). True when anything changed.</summary>
        internal static bool EdgeOn(Image img)
        {
            if (img == null) return false;
            var edge = img.GetComponent<Outline>();
            bool changed = false;
            if (edge == null) { edge = img.gameObject.AddComponent<Outline>(); changed = true; }
            if (edge.effectColor != Edge) { edge.effectColor = Edge; changed = true; }
            if (edge.effectDistance != EdgeDistance) { edge.effectDistance = EdgeDistance; changed = true; }
            return changed;
        }
    }
}
