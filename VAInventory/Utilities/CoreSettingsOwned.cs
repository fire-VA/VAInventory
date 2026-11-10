using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// 1.0.375 ([seasons], Fire 10-30: the day/time box under the minimap moves out of Core to VAInventory; plan
    /// Tools\CORE_CONFIG_MOVE_PLAN_20261030.md): VAInventory owns Core's HUD-look keys under Core's exact [UI] names, copies each saved
    /// value once from Core's cfg, and hands it to Core with CoreSettings.Provide (Core shows / uses them only when an owner provides
    /// them). 1.0.376 ([wishbone] 10-30, "Core = our Jotunn"): + Fires UI override, Sky drift speed, Minimap size, Minimap clock shows.
    /// Reflection only, so VAInventory still loads with an older Core or none ([wishbone]'s CoreSettingsOwned pattern).
    /// </summary>
    internal static class CoreSettingsOwned
    {
        private const string Owner = "VAInventory", Section = "UI";
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VAInventory.CoreSettings");
        private static MethodInfo s_provide, s_readSaved;

        internal static void Bind(ConfigFile cfg)
        {
            try
            {
                var cs = Type.GetType("FiresCore.Config.CoreSettings, FiresUnifiedCore", false);
                s_provide = cs?.GetMethod("Provide", new[] { typeof(string), typeof(string), typeof(Func<object>), typeof(string), typeof(int) });
                if (s_provide == null) { Log.LogInfo("[CoreSettings] the loaded Core has no CoreSettings (older than 0.2.1036): Core keeps its own [UI] keys"); return; }
                s_readSaved = cs.GetMethod("TryReadCoreSaved", new[] { typeof(string), typeof(string), typeof(string).MakeByRefType() });
                // descriptions, defaults and ranges as Core 0.2.1043 had them (FiresShell.cs)
                Own(cfg, "Show day and time box", true, new ConfigDescription("Shows the box under the minimap with the biome, the day and the time. Turn it off to hide it."));
                Own(cfg, "Minimap size", 1f, new ConfigDescription("Size of the small on-screen minimap, as a multiple of vanilla. Only used while Fires UI override is ON.", new AcceptableValueRange<float>(0.5f, 2f)));
                // 1.0.377 (Fire 11-02): the rounded minimap can be turned off; the UI override and sky drift are Core's own again (Core copies this cfg's saved values once)
                Own(cfg, "Rounded minimap", true, new ConfigDescription("While Fires UI override is ON: the round minimap. Off: the normal square minimap."));
                // Core's MinimapClockMode is internal: a string Core parses (SunTime / GameTime)
                Own(cfg, "Minimap clock shows", "SunTime", new ConfigDescription("SunTime: the clock follows the sun (06:00 sunrise, 18:00 sunset; nights pass faster). GameTime: the plain game clock, every hour the same length (matches other clock mods).", new AcceptableValueList<string>("SunTime", "GameTime")));
            }
            catch (Exception ex) { Log.LogWarning($"[CoreSettings] [UI] keys not handed to Core ({(ex.InnerException ?? ex).Message})"); }
        }

        private static void Own<T>(ConfigFile cfg, string key, T def, ConfigDescription desc)
        {
            var d = new ConfigDefinition(Section, key);
            bool had = HasSaved(cfg, d);
            var e = cfg.Bind(d, def, desc);
            if (!had && s_readSaved != null)
            {
                var args = new object[] { Section, key, null };
                if ((bool)s_readSaved.Invoke(null, args) && args[2] is string old)
                {
                    try { e.Value = (T)TomlTypeConverter.ConvertToValue(old, typeof(T)); Log.LogInfo($"[CoreSettings] migrated {Section}/{key} from Core: {e.Value}"); }
                    catch (Exception ex) { Log.LogDebug($"[CoreSettings] {Section}/{key}: Core's saved '{old}' not readable ({ex.Message}); default kept"); }
                }
            }
            // 1.0.377: the ConfigEntry form when Core has it, so a live edit reaches Core at once
            var cs = s_provide.DeclaringType;
            var byEntry = cs.GetMethod("Provide", new[] { typeof(string), typeof(string), typeof(ConfigEntryBase), typeof(string), typeof(int) });
            if (byEntry != null) byEntry.Invoke(null, new object[] { Section, key, e, Owner, 0 });
            else { Func<object> get = () => e.Value; s_provide.Invoke(null, new object[] { Section, key, get, Owner, 0 }); }
            Log.LogDebug($"[CoreSettings] {Section}/{key} = {e.Value}, handed to Core");
        }

        // whether VAInventory's cfg already holds the key (then Core's value never overwrites it); BepInEx keeps not-yet-bound keys as orphans
        private static bool HasSaved(ConfigFile cfg, ConfigDefinition d)
        {
            try { return HarmonyLib.AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(cfg) is Dictionary<ConfigDefinition, string> o && o.ContainsKey(d); }
            catch { return false; }
        }
    }
}
