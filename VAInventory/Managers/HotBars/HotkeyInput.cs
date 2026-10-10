using System;
using UnityEngine;

namespace VerdantsAscent.Managers.HotBars
{
    internal static class HotkeyInput
    {
        public static bool TryParseKeybindString(string s, out KeyCode key, out bool wantAlt, out bool wantCtrl, out bool wantShift)
        {
            key = KeyCode.None;
            wantAlt = false;
            wantCtrl = false;
            wantShift = false;

            if (string.IsNullOrEmpty(s)) return false;

            var parts = s.Split('+');
            if (parts.Length == 0) return false;

            string keyPart = parts[parts.Length - 1].Trim();
            for (int p = 0; p < parts.Length - 1; p++)
            {
                var part = parts[p].Trim().ToLowerInvariant();
                if (part == "alt" || part == "leftalt" || part == "rightalt") wantAlt = true;
                else if (part == "ctrl" || part == "control" || part == "leftctrl" || part == "rightctrl") wantCtrl = true;
                else if (part == "shift" || part == "leftshift" || part == "rightshift") wantShift = true;
            }

            string kp = keyPart;
            if (kp.Length == 1 && char.IsDigit(kp[0])) kp = "Alpha" + kp;

            try
            {
                if (Enum.TryParse<KeyCode>(kp, true, out var kc))
                {
                    key = kc;
                    return true;
                }
                if (Enum.TryParse<KeyCode>("Alpha" + kp, true, out kc))
                {
                    key = kc;
                    return true;
                }
            }
            catch { }

            return false;
        }

        public static bool IsShortcutDown(string keybindText)
        {
            try
            {
                if (string.IsNullOrEmpty(keybindText)) return false;
                if (!TryParseKeybindString(keybindText, out var key, out var wantAlt, out var wantCtrl, out var wantShift)) return false;

                if (!ModifiersMatch(wantAlt, wantCtrl, wantShift)) return false;

                // 1.0.103 (Fire: no keybinds while typing): raw hotkeys wait while a text field has input.
                if (InputGate.Typing) return false;
                return Input.GetKeyDown(key) || Input.GetKeyDown(GetEquivalentKeypadKey(key));
            }
            catch { return false; }
        }

        public static bool ShouldSuppressVanillaHotbarIndex(int slotIndex)
        {
            if (slotIndex < 0) return false;

            var cfg = ConfigManager.Instance;
            if (cfg == null) return false;

            if (Slots.toolHotbarSlots != null
                && slotIndex < Slots.toolHotbarSlots.Count
                && MatchesVanillaHotbarIndexAndIsDown(GetToolHotbarKeybindText(slotIndex), slotIndex)) return true;

            var foodSlots = Slots.GetFoodSlots();
            if (foodSlots != null
                && slotIndex < foodSlots.Length
                && MatchesVanillaHotbarIndexAndIsDown(cfg.GetFoodHotbarKeybindText(slotIndex), slotIndex)) return true;

            var ammoSlots = Slots.GetAmmoSlots();
            if (ammoSlots != null
                && slotIndex < ammoSlots.Length
                && MatchesVanillaHotbarIndexAndIsDown(cfg.GetAmmoHotbarKeybindText(slotIndex), slotIndex)) return true;

            return false;
        }

        public static string GetToolHotbarKeybindText(int slotIndex)
        {
            var cfg = ConfigManager.Instance;
            if (cfg?.configToolHotbarKeybindsEx != null
                && slotIndex >= 0
                && slotIndex < cfg.configToolHotbarKeybindsEx.Length)
            {
                var ks = cfg.configToolHotbarKeybindsEx[slotIndex]?.Value;
                if (!string.IsNullOrEmpty(ks)) return ks;
            }

            return $"Alt+{slotIndex + 1}";
        }

        private static bool MatchesVanillaHotbarIndexAndIsDown(string keybindText, int slotIndex)
        {
            if (string.IsNullOrEmpty(keybindText)) return false;
            if (!TryParseKeybindString(keybindText, out var key, out var wantAlt, out var wantCtrl, out var wantShift)) return false;
            if (!ModifiersMatch(wantAlt, wantCtrl, wantShift)) return false;

            var alphaKey = (KeyCode)((int)KeyCode.Alpha1 + slotIndex);
            var keypadKey = (KeyCode)((int)KeyCode.Keypad1 + slotIndex);
            if (key != alphaKey && key != keypadKey) return false;

            if (InputGate.Typing) return false;   // 1.0.103: not while typing
            return Input.GetKeyDown(alphaKey) || Input.GetKeyDown(keypadKey);
        }

        private static bool ModifiersMatch(bool wantAlt, bool wantCtrl, bool wantShift)
        {
            bool altDown = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool ctrlDown = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shiftDown = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            return altDown == wantAlt && ctrlDown == wantCtrl && shiftDown == wantShift;
        }

        private static KeyCode GetEquivalentKeypadKey(KeyCode key)
        {
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                return (KeyCode)((int)KeyCode.Keypad0 + ((int)key - (int)KeyCode.Alpha0));
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
                return (KeyCode)((int)KeyCode.Alpha0 + ((int)key - (int)KeyCode.Keypad0));
            return KeyCode.None;
        }
    }
}
