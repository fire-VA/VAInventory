using System;
using System.Reflection;

namespace VerdantsAscent.Managers.HotBars
{
    /// <summary>
    /// 1.0.103 (Fire: no keybinds while typing, e.g. in the hammer's search filter). True while Core's text-capture gate is
    /// on (FiresCore.Input.FiresInputBlock.IsCapturing), read by reflection so VAInventory still runs without Core. Raw
    /// UnityEngine.Input hotkeys bypass Core's own gate, so each one checks this first.
    /// </summary>
    internal static class InputGate
    {
        static bool _resolved;
        static PropertyInfo _isCapturing;
        static MethodInfo _isTyping;   // 1.0.106: Core 0.2.376 FiresInputBlock.IsTyping(), the team-wide check

        internal static bool Typing
        {
            get
            {
                if (!_resolved)
                {
                    _resolved = true;
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t;
                        try { t = asm.GetType("FiresCore.Input.FiresInputBlock", false); } catch { continue; }
                        if (t == null) continue;
                        _isTyping = t.GetMethod("IsTyping", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                        _isCapturing = t.GetProperty("IsCapturing", BindingFlags.Public | BindingFlags.Static);
                        break;
                    }
                }
                try
                {
                    if (_isTyping != null) return (bool)_isTyping.Invoke(null, null);
                    if (_isCapturing != null) return (bool)_isCapturing.GetValue(null);
                }
                catch { }
                return false;
            }
        }
    }
}
