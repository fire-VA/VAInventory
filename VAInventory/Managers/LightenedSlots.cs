using BepInEx.Configuration;
using System.Collections.Generic;

namespace VerdantsAscent
{
    public static class LightenedSlots
    {
        private static readonly List<int> affectedRows = [];

        // Config entries - bound in ConfigManager
        public static ConfigEntry<bool> configEnabled;
        public static ConfigEntry<float> configWeightFactor;
        public static ConfigEntry<int> configStartIndex;
        public static ConfigEntry<bool> configOnlyExtraRows;

        public static bool IsEnabled => configEnabled?.Value ?? false;

        public static float WeightFactor => configWeightFactor?.Value ?? 1f;

        public static bool IsRowAffected(int row) => affectedRows.Contains(row);

        public static void UpdateState()
        {
            UpdateAffectedRows();
            // Weight recalc will be triggered via inventory patches later
            // No call to UpdateTotalWeight() — method doesn't exist yet
        }

        private static void UpdateAffectedRows()
        {
            affectedRows.Clear();

            if (!IsEnabled) return;

            int startIndex = configStartIndex?.Value ?? -2;
            bool onlyExtraRows = configOnlyExtraRows?.Value ?? true;

            for (int row = 0; row < Slots.InventoryHeightPlayer; row++)
            {
                if (onlyExtraRows && row < 4) continue; // Skip vanilla rows

                if (startIndex < 0)
                {
                    // Negative = count from bottom
                    if (row >= Slots.InventoryHeightPlayer + startIndex)
                        affectedRows.Add(row);
                }
                else if (startIndex > 0)
                {
                    // Positive = start from this row index
                    if (row >= startIndex - 1)
                        affectedRows.Add(row);
                }
            }
        }

        // Call this from ConfigManager.Initialize to bind configs
        public static void BindConfigs(ConfigFile config)
        {
            configEnabled = config.Bind("LightenedSlots", "Enabled", true, "Enable lightened inventory rows");
            configWeightFactor = config.Bind("LightenedSlots", "WeightFactor", 0.5f,
                new ConfigDescription("Weight multiplier for items in lightened rows", new AcceptableValueRange<float>(0f, 1f)));
            configStartIndex = config.Bind("LightenedSlots", "StartIndex", -2, "Row to start lightening: negative = from bottom, positive = from top (1-based)");
            configOnlyExtraRows = config.Bind("LightenedSlots", "OnlyExtraRows", true, "Apply only to extra rows (beyond vanilla 4)");
        }
    }
}