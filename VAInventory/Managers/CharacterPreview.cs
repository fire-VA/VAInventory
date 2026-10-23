using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // Renders the LIVE local player into the character panel, so the preview always matches what is actually worn.
    // The render itself moved to LivePreview (an instance, so the companion panel and VAInventoryPreviewApi can have their own);
    // this is the player panel's preview, bound to Player.m_localPlayer, with the same static surface as before (ui_test reads it).
    // Client only. Everything here is behind Player.m_localPlayer, so a dedicated server never reaches it.
    internal static class CharacterPreview
    {
        static readonly LivePreview s_player = new LivePreview("Player");

        internal static void Bind(RawImage mount)
        {
            var player = Player.m_localPlayer;
            if (player == null || mount == null) return;
            s_player.Bind(player, mount, false);   // the player keeps the human framing (3.4 m / 1.0 m)
        }

        internal static void Hide() => s_player.Hide();

        // For ui_test: the preview's camera, light, texture and private layer, and its view, saved and put back.
        internal static Camera PreviewCamera => s_player.Cam;
        internal static Light PreviewLight => s_player.KeyLight;
        internal static RenderTexture PreviewTexture => s_player.Texture;
        internal static int IsolatedLayer => s_player.IsolatedLayer;

        internal readonly struct View
        {
            internal readonly float Yaw, Pitch, Distance, Height, Side;
            internal View(float yaw, float pitch, float distance, float height, float side) { Yaw = yaw; Pitch = pitch; Distance = distance; Height = height; Side = side; }
        }

        internal static View SaveView() => new View(s_player.Yaw, s_player.Pitch, s_player.Distance, s_player.Height, s_player.Side);
        internal static void RestoreView(View view) => s_player.SetView(view.Yaw, view.Pitch, view.Distance, view.Height, view.Side);
        internal static void Teardown() => s_player.Teardown();
        internal static void Spin(Vector2 pixels) => s_player.Spin(pixels);
        internal static void Pan(Vector2 pixels) => s_player.Pan(pixels);
        internal static void Zoom(float notchesIn) => s_player.Zoom(notchesIn);
        internal static void ResetView() => s_player.ResetView();
        internal static void Refit() => s_player.Refit();
    }
}
