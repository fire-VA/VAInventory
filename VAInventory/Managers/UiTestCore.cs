using System.Reflection;
using FiresCore.Input;
using FiresCore.UI;
using HarmonyLib;

namespace VerdantsAscent.Managers
{
    // ui_test's reach into FiresUnifiedCore, VAInventory's soft dependency. Every Core type is named only here, and
    // UiTest calls in only after checking Core is loaded, so VAInventory never needs Core to load.
    internal static class UiTestCore
    {
        // Whether the window was shown this session: ConfigPanel.EverOpened (Core 0.2.122+), read by name so an older
        // Core still loads this; null when that Core cannot tell. The host object is no signal: Core creates it at the
        // first config registration during load, so it always exists in a world ([ghost], 2026-09-28: the cold step
        // always skipped and the "warm" step measured the session's first, cold open).
        static readonly PropertyInfo EverOpenedProperty = AccessTools.Property(typeof(ConfigPanel), "EverOpened");

        internal static bool? ConfigWindowEverOpened() => EverOpenedProperty != null ? (bool)EverOpenedProperty.GetValue(null) : (bool?)null;

        internal static bool ConfigWindowOpen() => ConfigPanel.IsOpen;

        internal static void ToggleConfigWindow() => ConfigPanel.Toggle();

        // Every keybind conflict Core finds now, how many are not ignored, and the list as Core writes it.
        internal static int KeybindConflicts(out int unresolved, out string list)
        {
            KeybindRegistry.EnsureScanned();
            var conflicts = KeybindConflictDetector.Detect();
            unresolved = KeybindConflictStore.UnresolvedOf(conflicts).Count;
            list = KeybindConflictDetector.Summarize(conflicts);
            return conflicts.Count;
        }
    }
}
