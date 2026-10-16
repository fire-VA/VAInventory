using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    // 1.0.107: where VAInventory's slot hotkeys read the keyboard, so a test can press Ctrl+1 through the same
    // IsShortcutDown -> TryUseSlotItems -> Player.UseItem path a player's key goes through (Valcast's ValcastKeys pattern;
    // UnityEngine.Input itself can't be patched). Outside a test this IS UnityEngine.Input: nothing is injected unless a
    // test owner holds the seam.
    public static class VaInventoryKeys
    {
        private static readonly HashSet<KeyCode> s_pressed = new HashSet<KeyCode>();
        // A press's modifiers are HELD that frame, never pressed.
        private static readonly HashSet<KeyCode> s_pressedMods = new HashSet<KeyCode>();
        private static int s_pressedFrame = -1;
        private static object s_owner;

        internal static bool Injecting => s_owner != null && Time.frameCount == s_pressedFrame;

        public static bool GetKeyDown(KeyCode key) =>
            UnityEngine.Input.GetKeyDown(key) || (Injecting && s_pressed.Contains(key));

        public static bool GetKey(KeyCode key) =>
            UnityEngine.Input.GetKey(key) || (Injecting && (s_pressed.Contains(key) || s_pressedMods.Contains(key)));

        // A test takes the seam; one owner at a time. False when someone else holds it.
        public static bool Acquire(object owner)
        {
            if (owner == null || (s_owner != null && s_owner != owner)) return false;
            s_owner = owner;
            return true;
        }

        // The test lets go: every injected key is released.
        public static void Release(object owner)
        {
            if (owner == null || s_owner != owner) return;
            s_owner = null;
            s_pressed.Clear();
            s_pressedMods.Clear();
            s_pressedFrame = -1;
        }

        // Presses a key on the NEXT frame with the given modifiers held that frame: a player tapping Ctrl+1.
        public static bool Press(object owner, KeyCode key, params KeyCode[] modifiers)
        {
            if (owner == null || s_owner != owner || key == KeyCode.None) return false;
            s_pressed.Clear();
            s_pressedMods.Clear();
            s_pressed.Add(key);
            if (modifiers != null)
                foreach (KeyCode modifier in modifiers) s_pressedMods.Add(modifier);
            s_pressedFrame = Time.frameCount + 1;
            return true;
        }
    }
}
