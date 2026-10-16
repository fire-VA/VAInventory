using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Managers
{
    // The Torch slot's key (Fire, 2026-09-30: "a configurable double tap hotkey"): [TorchSlot] Key, double-tapped by default,
    // takes out or puts away whatever is in the Torch slot. Nothing happens while text is being typed or a menu is up.
    internal class TorchSlotHotkey : MonoBehaviour
    {
        private float _lastTap = -10f;

        private void Update()
        {
            var config = ConfigManager.Instance;
            if (config?.configTorchSlotKey == null || Player.m_localPlayer == null) return;
            KeyCode key = config.configTorchSlotKey.Value;
            // 1.0.103 (Fire: no keybinds while typing): not while a text field has input.
            if (key == KeyCode.None || HotBars.InputGate.Typing || !Input.GetKeyDown(key) || Busy()) return;

            if (config.configTorchSlotDoubleTap.Value)
            {
                float now = Time.unscaledTime;
                if (now - _lastTap > config.configTorchSlotDoubleTapSeconds.Value)
                {
                    _lastTap = now;
                    return;
                }
                _lastTap = -10f;
            }
            Slots.ToggleTorchSlot();
        }

        private static bool Busy()
        {
            if (Chat.instance != null && Chat.instance.HasFocus()) return true;
            if (Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible() || InventoryGui.IsVisible()) return true;
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && (selected.GetComponent<TMP_InputField>() != null || selected.GetComponent<InputField>() != null);
        }
    }
}
