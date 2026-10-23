using BepInEx.Configuration;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using VerdantsAscent.Managers;
namespace VerdantsAscent.Managers
{
    public class ConfigManager
    {
        private static ConfigManager _instance;
        public static ConfigManager Instance => _instance ??= new ConfigManager();

        // Null-safe shortcut for the verbose-logging flag. Some callers run during early
        // init or shutdown where Instance / configVerboseLogging may not yet be wired.
        public static bool IsVerbose
        {
            get
            {
                try { return _instance != null && _instance.configVerboseLogging != null && _instance.configVerboseLogging.Value; }
                catch { return false; }
            }
        }

        public bool _isProcessingSync = false;
        private FileSystemWatcher configWatcher;
        // Stamped on every ConfigEntry write we make (SettingChanged fires on .Value sets and
        // SaveOnConfigSet rewrites the .cfg on the same set) - lets the watcher tell our own
        // file writes from an external/admin edit, so interactive UI writes can't loop us into
        // endless self-reloads (+ synced-entry rebroadcasts).
        private DateTime _lastSelfWriteUtc = DateTime.MinValue;
        private const double SelfWriteSuppressSeconds = 3.0;

        private void SelfWriteStamp(object sender, BepInEx.Configuration.SettingChangedEventArgs e)
            => _lastSelfWriteUtc = DateTime.UtcNow;

        private bool IsRecentSelfWrite()
            => (DateTime.UtcNow - _lastSelfWriteUtc).TotalSeconds < SelfWriteSuppressSeconds;
        private bool _isShuttingDown = false;
        private DateTime _lastConfigUpdate = DateTime.MinValue;
        private Coroutine _reloadCoroutine;

        // ────── General Settings ──────
        public ConfigEntry<bool> configVerboseLogging;
        public ConfigEntry<bool> configServerAuthority; // Server locking config

        // ────── UI layout ──────
        // One slot registry, two renderers. The toggle must never become a data migration: Legacy and
        // Unified read the same Slots, so switching is a re-layout and nothing moves in the inventory.
        public ConfigEntry<string> configUiLayout;
        // Where the player dragged the Unified character panel, as an offset from where the prefab puts it, so a
        // re-authored layout still moves the panel and the player's nudge rides on top.
        public ConfigEntry<Vector2> configCharacterPanelOffset;
        public ConfigEntry<float> configPlayerPanelScale;   // 1.0.100
        public ConfigEntry<string> configPanelsScale;   // 1.0.157
        public ConfigEntry<Vector2> configContainerOffset;
        public ConfigEntry<string> configContainerWindowPosition;
        public ConfigEntry<bool> configResetContainerWindowPosition;
        // Fire 10-07 (1.0.69): the Unified panel was too see-through; a solid plate under its backdrop, this opaque.
        public ConfigEntry<float> configPanelOpacity;
        // Fire 10-08 (1.0.72): the space under the lowest grid row, matched to the top's.
        public ConfigEntry<int> configPanelBottomGap;

        // ────── Extra Slots Settings ──────
        public ConfigEntry<int> configExtraRows; // main inventory
        public ConfigEntry<int> configEquipmentPanelRows; // new: equipment panel
        public ConfigEntry<bool> configShowFoodSlots; // new: show/hide food slots
        public ConfigEntry<bool> configShowAmmoSlots; // new: show/hide ammo slots

        // ────── Coin Pouch (multi-currency) ──────
        // Comma-separated PREFAB names the pouch holds. Vanilla valuables by default; add
        // modded currency prefab ids (e.g. Peningar) to make them auto-deposit into the pouch.
        public ConfigEntry<string> configCurrencyPrefabs;
        public ConfigEntry<bool> configUnifiedMigrated;   // 1.0.305: the one-time Fires UI override migration has run

        private static readonly char[] CurrencySplit = [',', ';'];
        private const string DefaultCurrencyPrefabs = "Coins,Ruby,Amber,AmberPearl";

        // ── 1.0.305 (Fire 10-22: "override on force for the first time to migrate, but so help me god if i try to then adjust my positions
        // and they get forced back i will flip my lid") ── ONE-TIME migration, written to the cfg: the first time VAInventory sees Core's
        // Fires UI override ON while [UI] UnifiedMigrated is false, it sets the approved v6b values - Layout Panels, EquipmentPanelRows 3,
        // ExtraRows 3, the six panel backgrounds on - then UnifiedMigrated = true, one log line. After that nothing here reads or forces
        // those keys again (no read-time overrides), and no Core dock / layout position is ever touched. Run from the main menu
        // (FejdStartup.Awake, before any world) and again on the first local spawn, whichever first sees the override ON.
        internal void MigrateToUnifiedOnce(string when)
        {
            if (configUnifiedMigrated == null || configUnifiedMigrated.Value || !UiOverrideOn()) return;
            configUiLayout.Value = "Panels";
            configEquipmentPanelRows.Value = 3;
            configExtraRows.Value = 3;
            foreach (var t in new[] { EquipmentPanelBackgroundToggle, InventoryBackgroundToggle, ContainerBackgroundToggle, CraftingBackgroundToggle, InfoBackgroundToggle, SplitBackgroundToggle })
                if (t != null) t.Value = true;
            configUnifiedMigrated.Value = true;
            _cfgFile?.Save();
            // 1.0.306: at the main menu RefreshSlotsAndPanel no-ops (no world), so the slot list - built in Awake with the old Misc rows -
            // is rebuilt here from the migrated values, the same call Awake makes without a world.
            Slots.InitializeSlots();
            Debug.Log($"[VAInventory] Fires UI migration ({when}): Layout Panels, EquipmentPanelRows 3, ExtraRows 3, six panel backgrounds on, written once; "
                + "UnifiedMigrated = true - your own changes are never overwritten again.");
        }

        private ConfigFile _cfgFile;
        private static System.Reflection.PropertyInfo s_overrideProp; private static bool s_overrideLooked;
        /// <summary>1.0.307: Core's Fires UI override, read safely (no Core = false); ContainerGridPlate stands down under Panels when ON.</summary>
        internal static bool UiOverrideOnForChest() => UiOverrideOn();
        private static bool UiOverrideOn()
        {
            if (!s_overrideLooked || s_overrideProp == null) { s_overrideLooked = true; try { s_overrideProp = Type.GetType("FiresCore.UI.Shell.FiresShell, FiresUnifiedCore", false)?.GetProperty("OverrideOn"); } catch { } }
            try { return s_overrideProp != null && s_overrideProp.GetValue(null) is bool b && b; } catch { return false; }
        }

        [HarmonyLib.HarmonyPatch(typeof(FejdStartup), "Awake")]
        private static class MigrateAtMenu { private static void Postfix() => Instance?.MigrateToUnifiedOnce("main menu"); }

        [HarmonyLib.HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class MigrateAtSpawn
        {
            [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.First)]
            private static void Prefix(Player __instance) { if (__instance == Player.m_localPlayer) Instance?.MigrateToUnifiedOnce("first spawn"); }
        }

        // 1.0.303: a cfg written by 1.0.116..1.0.302 on a fresh install holds the key's description instead of a list. Put the default back,
        // once, with one log line. Any value a player actually typed is left alone.
        private void RepairCurrencyPrefabs()
        {
            string v = configCurrencyPrefabs?.Value ?? "";
            if (v.IndexOf("Comma-separated PREFAB names", StringComparison.OrdinalIgnoreCase) < 0) return;
            configCurrencyPrefabs.Value = DefaultCurrencyPrefabs;
            Debug.Log($"[VAInventory] CurrencyPrefabs held its own description (a 1.0.116-1.0.302 default bug); reset to '{DefaultCurrencyPrefabs}'.");
        }

        private string[] _currencyCache;
        private string _currencyCacheRaw;

        // Ordered, de-duplicated currency prefab names. The FIRST entry is the default
        // displayed currency when the pouch is empty. Cached until the raw string changes.
        public IReadOnlyList<string> GetCurrencyPrefabs()
        {
            string raw = configCurrencyPrefabs?.Value ?? "";
            if (_currencyCache == null || raw != _currencyCacheRaw)
            {
                _currencyCacheRaw = raw;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var list = new List<string>();
                foreach (var part in raw.Split(CurrencySplit, StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = part.Trim();
                    // 1.0.303: a prefab id never holds spaces, quotes or brackets; anything that does is not a currency
                    if (name.IndexOfAny(new[] { ' ', '\'', '"', '(', ')', '.' }) >= 0) continue;
                    if (name.Length > 0 && seen.Add(name))
                        list.Add(name);
                }
                if (list.Count == 0) list.Add("Coins");
                _currencyCache = [.. list];
            }
            return _currencyCache;
        }

        // ────── Per-bag detached backpack-panel positions ──────
        // Each equipped backpack gets its own draggable panel; positions
        // are persisted as a single string entry shaped like
        //   "SmallBag_va=120,-340|HoboStick=400,-340|DayBag_va=600,-340"
        // so any number of bag prefabs can store a position without
        // pre-declaring a config entry per prefab. Read/write through
        // GetBackpackPanelPosition / SetBackpackPanelPosition below.
        public ConfigEntry<string> configBackpackPanelPositions;

        // ────── UI Background Settings ──────
        public ConfigEntry<bool> EquipmentPanelBackgroundToggle; // toggle for custom background
        public ConfigEntry<string> EquipmentPanelBackground; // filename for custom background
        public ConfigEntry<bool> InventoryBackgroundToggle;
        public ConfigEntry<string> InventoryBackground;
        public ConfigEntry<bool> ContainerBackgroundToggle;
        public ConfigEntry<string> ContainerBackground;
        public ConfigEntry<bool> CraftingBackgroundToggle;
        public ConfigEntry<string> CraftingBackground;
        public ConfigEntry<bool> InfoBackgroundToggle;
        public ConfigEntry<string> InfoBackground;
        public ConfigEntry<bool> SplitBackgroundToggle;
        public ConfigEntry<string> SplitBackground;
        public ConfigEntry<bool> SkillsBackgroundToggle;
        public ConfigEntry<string> SkillsBackground;
        public ConfigEntry<bool> TextsBackgroundToggle;
        public ConfigEntry<string> TextsBackground;
        public ConfigEntry<bool> TrophiesBackgroundToggle;
        public ConfigEntry<string> TrophiesBackground;
        public ConfigEntry<bool> VariantBackgroundToggle;
        public ConfigEntry<string> VariantBackground;

        // ────── Piece Recipes (Synced) ──────
        public readonly Dictionary<string, ConfigEntry<string>> configPerPieceRecipes = [];
        public readonly Dictionary<string, ConfigEntry<bool>> configPerPieceEnabled = [];

        // ────── Tool Hotbar Settings ──────
        public ConfigEntry<int> configToolHotbarSlots;
        public ConfigEntry<Vector2> configToolHotbarPosition;
        public ConfigEntry<float> configToolHotbarScale;
        public ConfigEntry<bool> configToolHotbarShowWhenEmpty;
        public ConfigEntry<KeyboardShortcut>[] configToolHotbarKeys = new ConfigEntry<KeyboardShortcut>[8];   // 1.0.116: KeyboardShortcut (was string KeybindEx1-8)

        // ────── Torch slot (1.0.50) ──────
        public ConfigEntry<KeyCode> configTorchSlotKey;
        public ConfigEntry<bool> configTorchSlotDoubleTap;
        public ConfigEntry<float> configTorchSlotDoubleTapSeconds;

        // Phase D of EXTRASLOTS_ALIGNMENT_PLAN: ammo & food hotbars.
        // Visual-only mirrors of the equipment-panel ammo/food slots,
        // each cloning vanilla's HotKeyBar like the tool hotbar does.
        public ConfigEntry<bool> configShowAmmoHotbar;
        public ConfigEntry<Vector2> configAmmoHotbarPosition;
        public ConfigEntry<float> configAmmoHotbarScale;
        public ConfigEntry<KeyboardShortcut>[] configAmmoHotbarKeys = new ConfigEntry<KeyboardShortcut>[3];

        public ConfigEntry<bool> configShowFoodHotbar;
        public ConfigEntry<Vector2> configFoodHotbarPosition;
        public ConfigEntry<float> configFoodHotbarScale;
        public ConfigEntry<KeyboardShortcut>[] configFoodHotbarKeys = new ConfigEntry<KeyboardShortcut>[3];

        // The three bars stack up the bottom-left corner: tools, then food, then ammo, one bar-height apart.
        private static readonly Vector2 DefaultToolHotbarPosition = new(18.25f, 206f);
        private static readonly Vector2 DefaultFoodHotbarPosition = new(17.5f, 281.5f);
        private static readonly float HotbarStackSpacing = DefaultFoodHotbarPosition.y - DefaultToolHotbarPosition.y;
        private static readonly Vector2 DefaultAmmoHotbarPosition =
            new(DefaultFoodHotbarPosition.x, DefaultFoodHotbarPosition.y + HotbarStackSpacing);

        // When true, the SettingChanged handler for position will ignore changes.
        // Used to avoid recreating the toolbar while the user is finalizing a drag move.
        public bool suppressToolHotbarPositionChangeEvents = false;

        public void Initialize(ConfigFile config)
        { _cfgFile = config;   // 1.0.305: the one-time migration saves through it
            // General
            // Off by default (1.0.51; Fire, 2026-10-01: cut verbose logging; every other Fires mod defaults it off).
            configVerboseLogging = config.Bind("General", "VerboseLogging", false,
                new ConfigDescription("Enable detailed debug logs.", null, new ConfigurationManagerAttributes { IsAdvanced = true }));

            configServerAuthority = config.Bind("General", "ServerAuthority", false,
                "When enabled, the synced settings (extra rows, equipment rows, verbose logging, per-piece recipes) follow the server and only an admin can change them. " +
                "The per-piece recipe overrides are applied ONLY while this is on; with it off, every piece keeps its normal recipe.");

            configUiLayout = config.Bind("UI", "Layout", "Unified",
                new ConfigDescription(
                    "Unified adds a character panel beside the carry grid: equipment on two rails around a live " +
                    "preview of your character (wheel zoom, left-drag spin, right-drag move, double-click reset), " +
                    "with the tool, food, ammo and misc slots in rows beneath it. Legacy keeps the equipment panel " +
                    "under the carry grid. Panels (1.0.157) puts the carry grid (chest below it), the character panel, a companion's panel " +
                    "while one is open, and crafting side by side as equal-width columns, top-aligned; drag the grip on the crafting " +
                    "panel's right edge to scale them all together. All three render the same slots - switching moves no items.",
                    new AcceptableValueList<string>("Unified", "Legacy", "Panels")));

            configPanelsScale = config.Bind("UI", "PanelsScale", "",
                "Panels layout: the scale you dragged the whole row to, per screen resolution (e.g. 1920x1080=0.85). Written for you by " +
                "the grip on the crafting panel's right edge; double-click the grip to reset. No need to edit.");

            configCharacterPanelOffset = config.Bind("UI", "CharacterPanelOffset", Vector2.zero,
                "Unified layout: how far you dragged the character panel from its default spot. Drag the panel by its " +
                "backdrop to move it; double-click the backdrop to put it back. Written for you; no need to edit.");

            configPlayerPanelScale = config.Bind("UI", "PlayerPanelScale", 1f,
                new ConfigDescription("Unified layout: the size of the player panel and everything in it (frame, cells, text, " +
                    "character preview, bag sections). 1 is the normal size; smaller makes the whole panel smaller. Applies at once.",
                    new AcceptableValueRange<float>(0.6f, 1.2f)));
            configPlayerPanelScale.SettingChanged += (s, e) => UnifiedScreen.RequestRescale();

            configContainerOffset = config.Bind("UI", "ContainerOffset", Vector2.zero,
                "How far you dragged a chest's panel from where it opens (below the carry grid). Drag the panel by its " +
                "backdrop to move it; set 0,0 to put it back. Written for you; no need to edit.");

            configContainerWindowPosition = config.Bind("UI", "ContainerWindowPosition", "",
                "Where you dragged a chest's panel, as x,y in UI units. Empty = open just below the carry grid. Drag the " +
                "panel by its backdrop to move it; set ResetContainerWindowPosition to put it back. Written for you; no need to edit.");
            // Fire 10-07: a real "Reset to default" button in BepInEx ConfigurationManager (its CustomDrawer); without the
            // manager it is a plain bool that flips itself back to false after resetting.
            // 1.0.151: 'Remap misplaced slot items' removed - the remap is a fix, always on (Fire's rule 10-14); an old cfg line is ignored
            // 1.0.152: 'Repair shifted equipment rows' removed - the repair is a fix, always on (proven R-C2); an old cfg line is ignored
            configResetContainerWindowPosition = config.Bind("UI", "ResetContainerWindowPosition", false,
                new ConfigDescription("Puts a dragged chest panel back just below the carry grid. In the config manager it is a " +
                    "button; in the file, set true (it flips itself back to false).", null,
                    new ConfigurationManagerAttributes { CustomDrawer = DrawResetContainerButton, HideDefaultButton = true }));
            configPanelOpacity = config.Bind("UI", "PanelOpacity", 0.92f,
                new ConfigDescription("Unified layout: how solid the character panel's backdrop is, from 0 (see-through) to 1 (solid). " +
                    "Higher makes the empty slots stand out from the world behind the panel.", new AcceptableValueRange<float>(0f, 1f)));
            configPanelOpacity.SettingChanged += (s, e) => UnifiedScreen.ApplyPanelOpacity();
            configPanelBottomGap = config.Bind("UI", "PanelBottomGap", 14,
                new ConfigDescription("Unified layout: extra space under the character panel's lowest grid row, in UI units, on top of " +
                    "the layout's own padding. The frame's bottom border is thicker than its top, so 14 makes the gap look the same as " +
                    "the top's. Only as much as fits is used: it never makes the panel scroll.", new AcceptableValueRange<int>(0, 40)));
            configContainerWindowPosition.SettingChanged += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(configContainerWindowPosition.Value)) InventoryGui_UpdateContainer_Patch.Reset();
            };
            configResetContainerWindowPosition.SettingChanged += (s, e) =>
            {
                if (!configResetContainerWindowPosition.Value) return;
                configResetContainerWindowPosition.Value = false;
                InventoryGui_UpdateContainer_Patch.ResetToDefault();
            };

            // Extra Slots
            configExtraRows = config.Bind("ExtraSlots", "ExtraRows", 0,
                new ConfigDescription("Extra rows added to the main inventory grid (0-3), on top of the vanilla rows including any bought from traders", new AcceptableValueRange<int>(0, 3)));

            configEquipmentPanelRows = config.Bind("ExtraSlots", "EquipmentPanelRows", 0,
                new ConfigDescription("Number of extra rows in equipment panel (0-3)", new AcceptableValueRange<int>(0, 3)));

            configShowFoodSlots = config.Bind("ExtraSlots", "ShowFoodSlots", true,
                "Show food slots in the equipment panel.");

            configShowAmmoSlots = config.Bind("ExtraSlots", "ShowAmmoSlots", true,
                "Show ammo slots in the equipment panel.");

            // 1.0.116: was [CoinPouch], migrated below.
            // 1.0.303 ([seasons], Fire's Gale modpack 0.1.9): the default "Coins,Ruby,Amber,AmberPearl" sat INSIDE the comment on this line
            // since 1.0.116, so every fresh install got the DESCRIPTION as the value - the pouch's first currency was a sentence and plain
            // Coins never went in. The default is real again, and a stored value that is that description is put back (RepairCurrencyPrefabs).
            configCurrencyPrefabs = config.Bind("ExtraSlots", "CurrencyPrefabs", DefaultCurrencyPrefabs,
                "Comma-separated PREFAB names the coin pouch holds as separate stacks. The first is the " +
                "default displayed currency. Add modded currency prefab ids here (e.g. 'Coins,Ruby,Amber,AmberPearl,Peningar') " +
                "and they will auto-deposit into the pouch from pickups, traders, and quest rewards.");

            configBackpackPanelPositions = config.Bind("ExtraSlots", "BackpackPanelPositions", "",
                "Per-bag detached backpack-panel positions, format: 'PrefabName=x,y|PrefabName=x,y'. " +
                "Auto-written when you drag a panel; no need to edit by hand.");

            // UI Background (grouped)
            EquipmentPanelBackgroundToggle = config.Bind("UI.Backgrounds", "EquipmentPanelBackgroundToggle", false,
                "If true, load Equipment Panel background from a PNG/JPG in the backgrounds folder below instead of asset bundle.");
            EquipmentPanelBackground = config.Bind("UI.Backgrounds", "EquipmentPanelBackground", string.Empty,
                "Filename of PNG/JPG in BepInEx/cache/VAInventory/Backgrounds/ to use as Equipment Panel background. Only used if toggle above is enabled. Requires game relaunch to take effect.");

            InventoryBackgroundToggle = config.Bind("UI.Backgrounds", "InventoryBackgroundToggle", false, "If true, load Inventory background from a PNG/JPG in the backgrounds folder below instead of asset bundle.");
            InventoryBackground = config.Bind("UI.Backgrounds", "InventoryBackground", string.Empty, "Filename of PNG/JPG in BepInEx/cache/VAInventory/Backgrounds/ to use as Inventory background. Only used if toggle above is enabled. Requires game relaunch to take effect.");

            ContainerBackgroundToggle = config.Bind("UI.Backgrounds", "ContainerBackgroundToggle", false, "If true, load Container background from a PNG/JPG in the backgrounds folder below instead of asset bundle.");
            ContainerBackground = config.Bind("UI.Backgrounds", "ContainerBackground", string.Empty, "Filename of PNG/JPG in BepInEx/cache/VAInventory/Backgrounds/ to use as Container background. Only used if toggle above is enabled. Requires game relaunch to take effect.");

            CraftingBackgroundToggle = config.Bind("UI.Backgrounds", "CraftingBackgroundToggle", false, "If true, load Crafting background from a PNG/JPG in the backgrounds folder below instead of asset bundle.");
            CraftingBackground = config.Bind("UI.Backgrounds", "CraftingBackground", string.Empty, "Filename of PNG/JPG in BepInEx/cache/VAInventory/Backgrounds/ to use as Crafting background. Only used if toggle above is enabled. Requires game relaunch to take effect.");

            InfoBackgroundToggle = config.Bind("UI.Backgrounds", "InfoBackgroundToggle", false, "If true, load Info background from a PNG/JPG in the backgrounds folder below instead of asset bundle.");
            InfoBackground = config.Bind("UI.Backgrounds", "InfoBackground", string.Empty, "Filename of PNG/JPG in BepInEx/cache/VAInventory/Backgrounds/ to use as Info background. Only used if toggle above is enabled. Requires game relaunch to take effect.");

            SplitBackgroundToggle = config.Bind("UI.Backgrounds", "SplitBackgroundToggle", false, "If true, load Split background from a PNG/JPG in the backgrounds folder below instead of asset bundle.");
            SplitBackground = config.Bind("UI.Backgrounds", "SplitBackground", string.Empty, "Filename of PNG/JPG in BepInEx/cache/VAInventory/Backgrounds/ to use as Split background. Only used if toggle above is enabled. Requires game relaunch to take effect.");

            SkillsBackgroundToggle = config.Bind("UI.Backgrounds", "SkillsBackgroundToggle", false, "If true, load Skills background from a PNG/JPG in the backgrounds folder below instead of asset bundle.");
            SkillsBackground = config.Bind("UI.Backgrounds", "SkillsBackground", string.Empty, "Filename of PNG/JPG in BepInEx/cache/VAInventory/Backgrounds/ to use as Skills background. Only used if toggle above is enabled. Requires game relaunch to take effect.");

            TextsBackgroundToggle = config.Bind("UI.Backgrounds", "TextsBackgroundToggle", false, "If true, load Texts background from a PNG/JPG in the backgrounds folder below instead of asset bundle.");
            TextsBackground = config.Bind("UI.Backgrounds", "TextsBackground", string.Empty, "Filename of PNG/JPG in BepInEx/cache/VAInventory/Backgrounds/ to use as Texts background. Only used if toggle above is enabled. Requires game relaunch to take effect.");

            TrophiesBackgroundToggle = config.Bind("UI.Backgrounds", "TrophiesBackgroundToggle", false, "If true, load Trophies background from a PNG/JPG in the backgrounds folder below instead of asset bundle.");
            TrophiesBackground = config.Bind("UI.Backgrounds", "TrophiesBackground", string.Empty, "Filename of PNG/JPG in BepInEx/cache/VAInventory/Backgrounds/ to use as Trophies background. Only used if toggle above is enabled. Requires game relaunch to take effect.");

            VariantBackgroundToggle = config.Bind("UI.Backgrounds", "VariantBackgroundToggle", false, "If true, load Variant background from a PNG/JPG in the backgrounds folder below instead of asset bundle.");
            VariantBackground = config.Bind("UI.Backgrounds", "VariantBackground", string.Empty, "Filename of PNG/JPG in BepInEx/cache/VAInventory/Backgrounds/ to use as Variant background. Only used if toggle above is enabled. Requires game relaunch to take effect.");

            // Tool Hotbar
            configToolHotbarSlots = config.Bind("ToolHotbar", "Slots",4,
                new ConfigDescription("Number of tool hotbar slots (0-8)", new AcceptableValueRange<int>(0,8)));
            configToolHotbarPosition = config.Bind("ToolHotbar", "Position", DefaultToolHotbarPosition,
                "Screen position of the tool hotbar (BottomLeft anchored).");
            configToolHotbarScale = config.Bind("ToolHotbar", "Scale", 1.0f,
                new ConfigDescription("Scale of the tool hotbar UI.", new AcceptableValueRange<float>(0.5f, 2f)));
            configToolHotbarShowWhenEmpty = config.Bind("ToolHotbar", "ShowWhenEmpty", false, "Show the tool hotbar when empty (except when inventory is open)");

            // Torch slot (Fire, 2026-09-30: double-tap T)
            configTorchSlotKey = config.Bind("TorchSlot", "Key", KeyCode.T,
                "Takes out, or puts away, the torch or lantern in the Torch slot.");
            configTorchSlotDoubleTap = config.Bind("TorchSlot", "Double tap", true,
                "On: the key toggles the torch on a double tap. Off: on a single press.");
            configTorchSlotDoubleTapSeconds = config.Bind("TorchSlot", "Double tap seconds", 0.35f,
                new ConfigDescription("Longest gap between the two taps.", new AcceptableValueRange<float>(0.1f, 1f)));
            for (int i =0; i <8; i++)
            {
                configToolHotbarKeys[i] = config.Bind("ToolHotbar", $"Slot {i + 1} key", new KeyboardShortcut(KeyCode.Alpha1 + i, KeyCode.LeftAlt),
                    $"Uses tool hotbar slot {i + 1}. Alt, Ctrl and Shift count on either side of the keyboard.");
            }

            // Ammo hotbar — visual mirror of the equipment-panel ammo slots.
            configShowAmmoHotbar = config.Bind("AmmoHotbar", "ShowHotbar", true,
                "Show a separate Ammo hotbar UI that mirrors the equipment-panel ammo slots.");
            configAmmoHotbarPosition = config.Bind("AmmoHotbar", "Position", DefaultAmmoHotbarPosition,
                "Screen position of the ammo hotbar (BottomLeft anchored).");
            configAmmoHotbarScale = config.Bind("AmmoHotbar", "Scale", 1.0f,
                new ConfigDescription("Scale of the ammo hotbar UI.", new AcceptableValueRange<float>(0.5f, 2f)));
            for (int i = 0; i < configAmmoHotbarKeys.Length; i++)
                configAmmoHotbarKeys[i] = config.Bind("AmmoHotbar", $"Slot {i + 1} key", new KeyboardShortcut(KeyCode.Alpha1 + i, KeyCode.LeftControl, KeyCode.LeftAlt),
                    $"Uses ammo hotbar slot {i + 1}. Alt, Ctrl and Shift count on either side of the keyboard.");

            // Food hotbar — visual mirror of the equipment-panel food slots.
            configShowFoodHotbar = config.Bind("FoodHotbar", "ShowHotbar", true,
                "Show a separate Food hotbar UI that mirrors the equipment-panel food slots.");
            configFoodHotbarPosition = config.Bind("FoodHotbar", "Position", DefaultFoodHotbarPosition,
                "Screen position of the food hotbar (BottomLeft anchored).");
            configFoodHotbarScale = config.Bind("FoodHotbar", "Scale", 1.0f,
                new ConfigDescription("Scale of the food hotbar UI.", new AcceptableValueRange<float>(0.5f, 2f)));
            for (int i = 0; i < configFoodHotbarKeys.Length; i++)
                configFoodHotbarKeys[i] = config.Bind("FoodHotbar", $"Slot {i + 1} key", new KeyboardShortcut(KeyCode.Alpha1 + i, KeyCode.LeftControl),
                    $"Uses food hotbar slot {i + 1}. Alt, Ctrl and Shift count on either side of the keyboard.");
            // 1.0.116 (config audit 10-13): renamed / moved keys keep the player's value - copied once from the old entry.
            MigrateOldKeys(config);

            // Mark dirty on change so the bars rebuild themselves on the
            // next Hud.Update postfix tick. Cheap — Refresh() returns
            // immediately when not dirty.
            // Re-lays out on the next Show; RefreshSlotsAndPanel rebuilds the elements so they move between
            // the prefab cells and the legacy background rather than being stranded in whichever the
            // previous layout used.
            configUiLayout.SettingChanged += (_, _) => EquipmentPanel.RefreshSlotsAndPanel();

            configShowAmmoHotbar.SettingChanged += (_, _) => HotBars.AmmoHotBar.MarkDirty();
            configAmmoHotbarPosition.SettingChanged += (_, _) => HotBars.AmmoHotBar.MarkDirty();
            configAmmoHotbarScale.SettingChanged += (_, _) => HotBars.AmmoHotBar.MarkDirty();
            configShowFoodHotbar.SettingChanged += (_, _) => HotBars.FoodHotBar.MarkDirty();
            configFoodHotbarPosition.SettingChanged += (_, _) => HotBars.FoodHotBar.MarkDirty();
            configFoodHotbarScale.SettingChanged += (_, _) => HotBars.FoodHotBar.MarkDirty();
            for (int i = 0; i < configAmmoHotbarKeys.Length; i++)
            {
                configAmmoHotbarKeys[i].SettingChanged += (_, _) =>
                {
                    HotBars.AmmoHotBar.MarkDirty();
                    EquipmentPanel.MarkDirty();
                };
                configFoodHotbarKeys[i].SettingChanged += (_, _) =>
                {
                    HotBars.FoodHotBar.MarkDirty();
                    EquipmentPanel.MarkDirty();
                };
            }

            // Add event handlers to refresh panel on change
            configShowFoodSlots.SettingChanged += (_, _) => EquipmentPanel.RefreshSlotsAndPanel();
            configShowAmmoSlots.SettingChanged += (_, _) => EquipmentPanel.RefreshSlotsAndPanel();
            configCurrencyPrefabs.SettingChanged += (_, _) => { _currencyCache = null; EquipmentPanel.RefreshSlotsAndPanel(); };
            RepairCurrencyPrefabs();
            configUnifiedMigrated = config.Bind("UI", "UnifiedMigrated", false,
                "Set by the mod: the one-time move to the Fires UI look has run (Layout Panels, 3 Misc rows, 3 extra rows, panel backgrounds), the first "
                + "time VAInventory saw Core's Fires UI override ON. After that your own settings are never changed again. Set false to run it once more.");
            EquipmentPanelBackgroundToggle.SettingChanged += (_, _) => EquipmentPanel.RefreshSlotsAndPanel();
            EquipmentPanelBackground.SettingChanged += (_, _) => EquipmentPanel.RefreshSlotsAndPanel();

            // Tool hotbar config change handlers
            configToolHotbarSlots.SettingChanged += (_, _) => {
                // Reinitialize slots mapping
                Slots.InitializeSlots();
                Debug.Log("[VAInventory] ConfigManager: configToolHotbarSlots changed. Updating toolbar visuals");
                LogToolHotbarConfigValues();
                HotBars.ToolHotBar.MarkDirty();
            };

            configToolHotbarShowWhenEmpty.SettingChanged += (_, _) => {
                Debug.Log("[VAInventory] ConfigManager: configToolHotbarShowWhenEmpty changed. Updating toolbar show behavior");
                LogToolHotbarConfigValues();
                HotBars.ToolHotBar.MarkDirty();
            };

            configToolHotbarPosition.SettingChanged += (_, _) => {
                if (suppressToolHotbarPositionChangeEvents)
                {
                    if (configVerboseLogging.Value)
                        Debug.Log("[VAInventory] ConfigManager: configToolHotbarPosition change suppressed (originated from UI drag end).");
                    return;
                }

                Debug.Log("[VAInventory] ConfigManager: configToolHotbarPosition changed. Applying new position");
                LogToolHotbarConfigValues();
                HotBars.ToolHotBar.MarkDirty();
            };

            configToolHotbarScale.SettingChanged += (_, _) => {
                Debug.Log("[VAInventory] ConfigManager: configToolHotbarScale changed. Applying new scale");
                LogToolHotbarConfigValues();
                HotBars.ToolHotBar.MarkDirty();
            };
            for (int i =0; i <8; i++)
            {
                int idx = i; // capture
                configToolHotbarKeys[i].SettingChanged += (_, _) => {
                    Debug.Log($"[VAInventory] ConfigManager: configToolHotbarKeys[{idx}] changed. Refreshing toolbar labels");
                    LogToolHotbarConfigValues();
                    HotBars.ToolHotBar.MarkDirty();
                    EquipmentPanel.MarkDirty();
                };
            }

            // ────── Piece Recipes ──────
            foreach (var pieceName in VAPieceManager.PieceNames)
            {
                string section = $"Pieces.{pieceName}";
                configPerPieceRecipes[pieceName] = config.Bind(section, "Recipe",
                    "ElderBark,40,Blackwood,40,TrollHide,20,BronzeNails,144,Iron,10",
                    $"Recipe string for {pieceName} (name,amount,perLevel,…).");

                configPerPieceEnabled[pieceName] = config.Bind(section, "Enabled", true,
                    $"Enable piece {pieceName}.");
            }

            PlayerFacing(config);   // 1.0.252
            ValidateConfigs();
            SetupConfigWatcher(config);
        }

        // 1.0.252 (TEAM RULE, Fire: player-facing configs): ONE reviewed list. Window spots / sizes / scales / opacity = Hidden (saved, never
        // shown; players use the drag handles); everything but keybinds and the basics = Advanced (Core's config UI shows it only with Show
        // Advanced); every description = one short plain line. Existing tags (ConfigSync) are kept; values and defaults are untouched.
        static readonly System.Collections.Generic.HashSet<string> Hidden = new System.Collections.Generic.HashSet<string>
        {
            "UI/PanelsScale", "UI/CharacterPanelOffset", "UI/PlayerPanelScale", "UI/ContainerOffset", "UI/ContainerWindowPosition",
            "UI/ResetContainerWindowPosition", "UI/PanelOpacity", "UI/PanelBottomGap", "ExtraSlots/BackpackPanelPositions",
            "ToolHotbar/Position", "ToolHotbar/Scale", "AmmoHotbar/Position", "AmmoHotbar/Scale", "FoodHotbar/Position", "FoodHotbar/Scale",
        };
        static readonly System.Collections.Generic.Dictionary<string, string> Basic = new System.Collections.Generic.Dictionary<string, string>
        {
            { "UI/Layout", "Inventory layout: Unified, Legacy or Panels." },
            { "ExtraSlots/ExtraRows", "Extra rows on the inventory grid (0-3)." },
            { "ExtraSlots/ShowFoodSlots", "Show the food slots." },
            { "ExtraSlots/ShowAmmoSlots", "Show the ammo slots." },
            { "ToolHotbar/ShowWhenEmpty", "Show the tool hotbar when its pouch is empty." },
            { "AmmoHotbar/ShowHotbar", "Show the ammo hotbar." },
            { "FoodHotbar/ShowHotbar", "Show the food hotbar." },
            { "TorchSlot/Key", "Key that equips the torch slot." },
            { "TorchSlot/Double tap", "Double-tap the torch key to equip." },
        };
        static void PlayerFacing(ConfigFile config)
        {
            var field = typeof(ConfigEntryBase).GetField("<Description>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null) { Debug.LogWarning("[VAInventory] config tidy skipped: ConfigEntryBase.Description field not found"); return; }
            int hidden = 0, adv = 0, basic = 0;
            foreach (var kv in config)
            {
                var e = kv.Value; string id = kv.Key.Section + "/" + kv.Key.Key;
                bool isKey = kv.Key.Key.EndsWith(" key", StringComparison.OrdinalIgnoreCase) || e.SettingType == typeof(KeyboardShortcut) || e.SettingType == typeof(KeyCode);
                string tag; string text;
                if (Hidden.Contains(id)) { tag = "Hidden"; text = Short(e.Description?.Description); hidden++; }
                else if (Basic.TryGetValue(id, out var b)) { tag = null; text = b; basic++; }
                else if (isKey) { tag = null; text = Short(e.Description?.Description); basic++; }
                else { tag = "Advanced"; text = Short(e.Description?.Description); adv++; }
                var old = e.Description;
                var tags = new System.Collections.Generic.List<object>();
                if (old?.Tags != null) foreach (var t in old.Tags) if (!(t is string s && (s == "Hidden" || s == "Advanced"))) tags.Add(t);
                if (tag != null) tags.Add(tag);
                field.SetValue(e, new ConfigDescription(text ?? "", old?.AcceptableValues, tags.ToArray()));
            }
            Debug.Log($"[VAInventory] config tidy (player-facing rule): {basic} shown (keys + basics), {adv} advanced, {hidden} hidden (window spots / sizes / scales / opacity)");
        }
        // one short plain line: the first sentence, at most 110 characters
        static string Short(string d)
        {
            if (string.IsNullOrEmpty(d)) return d;
            d = d.Replace("\r", " ").Replace("\n", " ").Trim();
            int dot = d.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0) d = d.Substring(0, dot + 1);
            if (d.Length > 110) d = d.Substring(0, 107).TrimEnd() + "...";
            return d;
        }

        private void SetupConfigWatcher(ConfigFile config)
        {
            if (configWatcher != null) return;

            string configPath = config.ConfigFilePath;
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return;

            string directory = Path.GetDirectoryName(configPath);
            string fileName = Path.GetFileName(configPath);

            config.SettingChanged -= SelfWriteStamp;
            config.SettingChanged += SelfWriteStamp;

            configWatcher = new FileSystemWatcher
            {
                Path = directory,
                Filter = fileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };

            configWatcher.Changed += (sender, e) =>
            {
                if (_isShuttingDown || _isProcessingSync) return;
                if (IsRecentSelfWrite()) return;
                if (_reloadCoroutine != null) return;
                _reloadCoroutine = VAInventory.Instance.StartCoroutine(ReloadConfigAfterDelay());
            };

            configWatcher.Renamed += (sender, e) =>
            {
                if (_isShuttingDown || _isProcessingSync) return;
                if (IsRecentSelfWrite()) return;
                if (_reloadCoroutine != null) return;
                _reloadCoroutine = VAInventory.Instance.StartCoroutine(ReloadConfigAfterDelay());
            };
        }

        private IEnumerator ReloadConfigAfterDelay()
        {
            yield return new WaitForSeconds(2f);

            if (_isShuttingDown || _isProcessingSync)
            {
                _reloadCoroutine = null;
                yield break;
            }

            if (IsRecentSelfWrite()) { _reloadCoroutine = null; yield break; }
            if ((DateTime.Now - _lastConfigUpdate).TotalMilliseconds < 2000)
            {
                _reloadCoroutine = null;
                yield break;
            }

            _lastConfigUpdate = DateTime.Now;
            Debug.Log($"{VAInventory.PluginName}: Config file changed, reloading...");

            var configFile = VAInventory.Instance.Config;
            bool saveOnSet = configFile.SaveOnConfigSet;
            configFile.SaveOnConfigSet = false;

            VAInventory.Instance.Config.Reload();
            ValidateConfigs();

            // Reload user backgrounds from config folder
            VAMiscAssetManager.LoadAllBackgrounds();

            configFile.SaveOnConfigSet = saveOnSet;

            VARecipeManager.UpdatePieceRequirements();

            if (configVerboseLogging.Value)
                Debug.Log($"{VAInventory.PluginName}: Config reloaded — recipe preserved.");

            _reloadCoroutine = null;
        }

        public void ValidateConfigs()
        {
            foreach (var kv in configPerPieceRecipes)
            {
                var parts = kv.Value.Value.Split(',').Select(s => s.Trim()).ToArray();
                bool valid =
                    (parts.Length % 3 == 0 && parts.All(p => !string.IsNullOrEmpty(p))) ||
                    (parts.Length % 2 == 0 && parts.All(p => !string.IsNullOrEmpty(p)));

                if (!valid)
                    kv.Value.Value = (string)kv.Value.DefaultValue;
            }
        }

        public void OnConfigLockChanged()
        {
            ValidateConfigs();
            VARecipeManager.UpdatePieceRequirements();

            if (configVerboseLogging.Value)
                Debug.Log($"{VAInventory.PluginName}: Config lock changed.");
        }

        public void Dispose()
        {
            _isShuttingDown = true;
            configWatcher?.Dispose();
        }

        private void LogToolHotbarConfigValues()
        {
            Debug.Log($"[VAInventory] ConfigManager: ToolHotbar Config: Slots={configToolHotbarSlots.Value}, Position={configToolHotbarPosition.Value}, Scale={configToolHotbarScale.Value}, ShowWhenEmpty={configToolHotbarShowWhenEmpty.Value}");
            for (int i =0; i <8; i++)
            {
                try { Debug.Log($"[VAInventory] ConfigManager: ToolHotbar Slot {i + 1} key={KeyText(configToolHotbarKeys[i])}"); } catch { }
            }
        }

        public string GetAmmoHotbarKeybindText(int slotIndex)
        {
            if (slotIndex >= 0 && configAmmoHotbarKeys != null && slotIndex < configAmmoHotbarKeys.Length)
            {
                string value = KeyText(configAmmoHotbarKeys[slotIndex]);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return $"Ctrl+Alt+{slotIndex + 1}";
        }

        public string GetFoodHotbarKeybindText(int slotIndex)
        {
            if (slotIndex >= 0 && configFoodHotbarKeys != null && slotIndex < configFoodHotbarKeys.Length)
            {
                string value = KeyText(configFoodHotbarKeys[slotIndex]);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return $"Ctrl+{slotIndex + 1}";
        }

        static readonly BepInEx.Logging.ManualLogSource CfgLog = BepInEx.Logging.Logger.CreateLogSource("VAInventory.Config");

        // A hotbar key as the text HotkeyInput parses ("Alt+Alpha1"): Left/Right Alt, Ctrl and Shift both become the
        // side-free name, so either key on the keyboard counts, as it did with the old text keybinds. Empty = unbound.
        internal static string KeyText(ConfigEntry<KeyboardShortcut> entry)
        {
            if (entry == null) return "";
            var ks = entry.Value;
            if (ks.MainKey == KeyCode.None) return "";
            var parts = new List<string>();
            foreach (var m in ks.Modifiers)
            {
                string name = m == KeyCode.LeftAlt || m == KeyCode.RightAlt || m == KeyCode.AltGr ? "Alt"
                    : m == KeyCode.LeftControl || m == KeyCode.RightControl ? "Ctrl"
                    : m == KeyCode.LeftShift || m == KeyCode.RightShift ? "Shift" : null;
                if (name != null && !parts.Contains(name)) parts.Add(name);
            }
            var main = ks.MainKey;
            parts.Add(main >= KeyCode.Alpha0 && main <= KeyCode.Alpha9 ? ((int)(main - KeyCode.Alpha0)).ToString() : main.ToString());   // "Alt+1", as the labels always read
            return string.Join("+", parts.ToArray());
        }

        // The old entries are no longer bound, so BepInEx holds them as orphans (its private OrphanedEntries). Each found one
        // is copied into its new key once, logged, and dropped, then the file is saved without it.
        void MigrateOldKeys(ConfigFile config)
        {
            var orphans = HarmonyLib.AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(config, null) as Dictionary<ConfigDefinition, string>;
            if (orphans == null || orphans.Count == 0) return;
            int moved = 0;
            void Keybind(string section, int slot, ConfigEntry<KeyboardShortcut> entry)
            {
                var old = new ConfigDefinition(section, $"KeybindEx{slot}");
                if (!orphans.TryGetValue(old, out string raw)) return;
                orphans.Remove(old);
                moved++;
                if (string.IsNullOrWhiteSpace(raw)) { entry.Value = KeyboardShortcut.Empty; CfgLog.LogDebug($"[VAInventory] config: {section} KeybindEx{slot} '' -> '{entry.Definition.Key}' = None"); return; }
                if (!HotBars.HotkeyInput.TryParseKeybindString(raw.Trim(), out var key, out bool alt, out bool ctrl, out bool shift))
                {
                    CfgLog.LogWarning($"[VAInventory] config: {section} KeybindEx{slot} '{raw}' did not parse; '{entry.Definition.Key}' keeps {entry.Value}");
                    return;
                }
                var mods = new List<KeyCode>();
                if (ctrl) mods.Add(KeyCode.LeftControl);
                if (alt) mods.Add(KeyCode.LeftAlt);
                if (shift) mods.Add(KeyCode.LeftShift);
                entry.Value = new KeyboardShortcut(key, mods.ToArray());
                CfgLog.LogDebug($"[VAInventory] config: {section} KeybindEx{slot} '{raw}' -> '{entry.Definition.Key}' = {entry.Value}");
            }
            for (int i = 0; i < configToolHotbarKeys.Length; i++) Keybind("ToolHotbar", i + 1, configToolHotbarKeys[i]);
            for (int i = 0; i < configAmmoHotbarKeys.Length; i++) Keybind("AmmoHotbar", i + 1, configAmmoHotbarKeys[i]);
            for (int i = 0; i < configFoodHotbarKeys.Length; i++) Keybind("FoodHotbar", i + 1, configFoodHotbarKeys[i]);

            // 1.0.123: Lightened Slots is gone (it was never switched on: its UpdateState had no caller). Its keys are dropped once.
            foreach (string retired in new[] { "Enabled", "WeightFactor", "StartIndex", "OnlyExtraRows" })
            {
                var old = new ConfigDefinition("LightenedSlots", retired);
                if (!orphans.Remove(old)) continue;
                moved++;
                CfgLog.LogDebug($"[VAInventory] config: dropped retired [LightenedSlots] {retired}");
            }

            void Moved(string oldSection, ConfigEntryBase entry)
            {
                var old = new ConfigDefinition(oldSection, entry.Definition.Key);
                if (!orphans.TryGetValue(old, out string raw)) return;
                orphans.Remove(old);
                moved++;
                entry.SetSerializedValue(raw);
                CfgLog.LogDebug($"[VAInventory] config: [{oldSection}] {entry.Definition.Key} -> [{entry.Definition.Section}] = '{entry.GetSerializedValue()}'");
            }
            Moved("CoinPouch", configCurrencyPrefabs);
            foreach (var def in config.Keys.Where(d => d.Section == "UI.Backgrounds").ToList()) Moved("UI", config[def]);

            if (moved > 0) { config.Save(); CfgLog.LogDebug($"[VAInventory] config: {moved} old key(s) migrated and removed"); }
        }
        // Called to indicate we just wrote the config file locally so the watcher should ignore it briefly
        public void NotifyLocalConfigSave()
        {
            _lastConfigUpdate = DateTime.Now;
            if (configVerboseLogging != null && configVerboseLogging.Value)
                Debug.Log($"[VAInventory] ConfigManager: NotifyLocalConfigSave at {_lastConfigUpdate}");
        }

        // ────── Backpack-panel position persistence ──────

        /// <summary>
        /// Lookup the saved screen-space position for a per-bag backpack
        /// panel. Returns false if no position has been saved yet for
        /// this prefab — caller should use a default position in that case.
        /// </summary>
        public bool TryGetBackpackPanelPosition(string bagPrefab, out Vector2 pos)
        {
            pos = Vector2.zero;
            if (string.IsNullOrEmpty(bagPrefab) || configBackpackPanelPositions == null)
                return false;

            string blob = configBackpackPanelPositions.Value;
            if (string.IsNullOrEmpty(blob)) return false;

            foreach (var entry in blob.Split('|'))
            {
                int eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                string key = entry.Substring(0, eq);
                if (key != bagPrefab) continue;
                string xy = entry.Substring(eq + 1);
                int comma = xy.IndexOf(',');
                if (comma <= 0) continue;
                if (float.TryParse(xy.Substring(0, comma),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float x)
                    && float.TryParse(xy.Substring(comma + 1),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float y))
                {
                    pos = new Vector2(x, y);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Persist a per-bag panel position. Updates the in-memory blob
        /// and writes the config entry, debounced by ConfigEntry's own
        /// save behavior (BepInEx flushes on shutdown / explicit save).
        /// </summary>
        public void SetBackpackPanelPosition(string bagPrefab, Vector2 pos)
        {
            if (string.IsNullOrEmpty(bagPrefab) || configBackpackPanelPositions == null)
                return;

            // Rebuild the blob with this prefab's entry replaced/added.
            var entries = new System.Collections.Generic.List<string>();
            string current = configBackpackPanelPositions.Value ?? "";
            foreach (var entry in current.Split('|'))
            {
                if (string.IsNullOrEmpty(entry)) continue;
                int eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                string key = entry.Substring(0, eq);
                if (key == bagPrefab) continue;     // drop old; we'll re-add below
                entries.Add(entry);
            }
            entries.Add($"{bagPrefab}={pos.x.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                        $"{pos.y.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            configBackpackPanelPositions.Value = string.Join("|", entries);
            NotifyLocalConfigSave();
        }

        // ConfigurationManager's CustomDrawer for ResetContainerWindowPosition: a "Reset to default" button. GUILayout is
        // reached by reflection so VAInventory needs no IMGUIModule reference (same reason as the audio reflection elsewhere).
        private static System.Reflection.MethodInfo s_guiButton;
        private static void DrawResetContainerButton(ConfigEntryBase entry)
        {
            try
            {
                if (s_guiButton == null)
                {
                    var t = Type.GetType("UnityEngine.GUILayout, UnityEngine.IMGUIModule") ?? Type.GetType("UnityEngine.GUILayout, UnityEngine");
                    s_guiButton = t?.GetMethod("Button", new[] { typeof(string), Type.GetType("UnityEngine.GUILayoutOption[], UnityEngine.IMGUIModule") ?? typeof(object[]) });
                }
                if (s_guiButton == null) return;
                var options = Array.CreateInstance(s_guiButton.GetParameters()[1].ParameterType.GetElementType(), 0);
                if ((bool)s_guiButton.Invoke(null, new object[] { "Reset to default", options }))
                    InventoryGui_UpdateContainer_Patch.ResetToDefault();
            }
            catch { }
        }
    }

    // BepInEx ConfigurationManager reads these by name (duck-typed tag); only the fields used here are declared.
    internal sealed class ConfigurationManagerAttributes
    {
        public Action<ConfigEntryBase> CustomDrawer;
        public bool? HideDefaultButton;
        public bool? IsAdvanced;
    }
}