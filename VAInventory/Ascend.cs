using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent
{
    // Reference the consts rather than repeating the literals: the attribute is what BepInEx (and so
    // VAngarde's exact-match list) actually reports, while PluginVersion is what the ConfigSync
    // Current/MinimumRequiredVersion below use. Held as two separate literals they drifted apart on the
    // very next version bump, leaving the plugin announcing 1.0.10 while its own sync claimed 1.0.11.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    // Load after VABackpacks when it is installed, so BackpackBridge finds it in Awake.
    [BepInDependency("com.Fire.VA_Backpacks", BepInDependency.DependencyFlags.SoftDependency)]
    public class VAInventory : BaseUnityPlugin
    {
        public static VAInventory Instance;
        public static readonly Harmony harmony = new("com.Fire.VAInventory");

        public const string PluginGUID = "com.Fire.VAInventory";
        public const string PluginName = "VAInventory";
        public const string PluginVersion = "1.0.384";   // 1.0.384 ([lead] 11-07): while the player panel is suppressed (NPC book / Dressing) its Core dock element vainv.character is hidden through Core's Visible callback (no live hit-test FAIL on its header, no resize grip over the book), shown again on release; Debug "dock element vainv.character hidden/shown with suppression" line. Previous 1.0.383:   // 1.0.382: a listed admin written as Steam_<id> counts. Previous 1.0.381:   // 1.0.381: server config only from admins, locked or not. Previous 1.0.380:   // 1.0.380 ([wishbone], Fire's event rule): the 0.5 s slot restyle runs only after a change. Previous: 1.0.379;   // 1.0.379 ([wishbone]): Debug section timing in PanelsLayout.LateUpdate. Previous: 1.0.378;   // 1.0.378 ([wishbone], Fire's rail order): Unified bag rail cells sized like the grid beside them. Previous: 1.0.377;   // 1.0.377 ([wishbone], Fire 11-02 config list): + [UI] Rounded minimap; Fires UI override and Sky drift speed leave this mod (Core's own); keys handed to Core in the ConfigEntry form. 1.0.376 ([seasons] 10-30): + Core's Fires UI override, Sky drift speed, Minimap size, Minimap clock shows (CoreSettings). 1.0.375 ([seasons] 10-30): owns + Provides Core's [UI] Show day and time box (CoreSettings). 1.0.374 ([seasons], Fire 10-30 log): the hotbars' live drag strip keeps its raycast (the whole-bar pass switched it off). 1.0.373 ([seasons], Fire 10-30 "health hud, tools, ammo, food"): hotbar default order tools, ammo, food bottom up (bars on the old defaults move once; drags kept). 1.0.372 ([seasons], Fire 10-30): every hotbar starts at its config spot (bottom left above the health HUD) BEFORE Core's dock first registers it (the ammo bar started mid-screen); not docked, a saved spot still wins. 1.0.371 ([seasons], Fire 10-29): worn cells always match what is worn (EquipCellGuard: equip in place / move into the cell, producing path logged); TryUnequipJewelry for FLO; rings / necklaces dragged into / out of a jewelry cell or right-clicked off one play FLO's equip queue (FloJewelryApi, soft) and move when it ends. 1.0.370 ([seasons], fgn round 4 10-29): TryEquipJewelry + Bypass bag eviction call inv.Changed() directly (1.0.21 Changed(bool,bool) made Invoke(inv, null) throw). 1.0.369 ([seasons], fgn round 10-29): TryEquipJewelry re-reads each jewelry cell (a cached empty Left ring took both rings) + Debug why per cell. 1.0.368 ([seasons], Fire 10-28): hotbar fit no-grow guard (VA_ToolHotBar grew +12 px a pass off screen). 1.0.367 ([seasons], lead 10-28 perf): InventoryGui.Update postfix no longer walks the scene (FindObjectsOfType every 30 s = 55 ms spikes on DN). 1.0.366 ([classes-ui] for [seasons], Valheim 1.0.21): StatsPanel uses GetStat(stat, DifficultyRequirement.Any); was MissingMethodException. 1.0.365 1.0.365 ([seasons], lead 10-28 perf): MEASURING InventoryGui.Update (Managers/InvGuiTimer.cs, Debug every 30 s): open / closed, VAInventory tab row quick / full, drag-on-top. 1.0.364: 1.0.364 ([classes-ui] files, UI audit V12/V13): grid plates re-measure cells only when the layout moves; DragItemOnTop canvas looked up once; PanelsCraftShift list reused.   // 1.0.363 ([classes-ui] files, Fire 10-26 hand test): non-unified owner defaults = vanilla spots (grid where vanilla shows it, player panel beside it, chest under it), not the unified centred row. 1.0.362 ([classes-ui] files, Fire 10-26 shot 444): non-unified chest lifted off the Player panel as its own free element vainv.chest.vanilla, seated below the grid   // 1.0.361: hotbar fit never measures its own grown rect (runaway off-screen fit, rig 16:57).   // 1.0.359: Inventory Save/Load patches never throw during a world conversion (Fire: "Error loading world file").   // 1.0.358 (Fire 10-26 UI efficiency audit, [classes-ui] files): per-frame inventory UI work only on change or at a few Hz (V1-V13)
        public const string SyncMinimumVersion = "1.0.116";

        public static AssetBundle assetBundle;
        public static readonly List<GameObject> piecePrefabs = [];
        public static readonly List<GameObject> itemPrefabs = [];

        public static GameObject RootObject;
        public static GameObject PrefabContainer { get; private set; }

        public ConfigSync configSync;

        // Systems ready event and flag consumers can subscribe to
        public static event Action SystemsReady;
        public static bool SystemsReadyFlag { get; private set; } = false;

        private void Awake()
        {
            Instance = this;
            RootObject = new GameObject("_VAInventoryRoot");
            UnityEngine.Object.DontDestroyOnLoad(RootObject);
            PrefabContainer = new GameObject("VAInventory_Prefabs");
            PrefabContainer.transform.SetParent(RootObject.transform);
            PrefabContainer.SetActive(false);

            configSync = new ConfigSync(PluginGUID)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                // Fixed: raise only when a synced key's name, type or meaning changes; 1.0.116 is the config-audit release.
                MinimumRequiredVersion = SyncMinimumVersion
            };
            configSync.LockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;

            ConfigManager.Instance.Initialize(Config);
            Utilities.CoreSettingsOwned.Bind(Config);   // 1.0.375: the day/time box key, owned here (Fire 10-30)
            BindConfigsToSync();

            // Load user backgrounds from config folder
            VAMiscAssetManager.LoadAllBackgrounds();

            LoadAssets();

            // BackpackBridge must initialize BEFORE Slots.InitializeSlots so
            // the first slot layout already knows whether the Backpack slot
            // should be active and whether the integrated backpack grid is needed.
            BackpackBridge.Initialize();
            BackpackStorageSync.Initialize();

            Slots.InitializeSlots();
            // The Torch slot's key; idle without a local player, so harmless on a server.
            RootObject.AddComponent<TorchSlotHotkey>();

            // Shared help panel (FiresCore) Ã¢â‚¬â€ SOFT dependency; no-ops when Core is absent.
            Utilities.VaInventoryHelpContent.TryRegister();

            if (ZNet.instance && ZNet.instance.IsDedicated())
                StartCoroutine(DelayedDedicatedServerInit());
            else
                FinishInit();
        }

        // Unity runs Start on the frame after BepInEx loaded every plugin. FiresRPGClasses loads after VAInventory, so the
        // layout built in Awake has no class bar column; build it again now that FiresRPGClasses is known to be there.
        private void Start()
        {
            if (Slots.ClassBarsAvailable() && !Slots.ClassBarSlotsActive)
                Slots.InitializeSlots();
            // The F1 hub's Player > Stats page (Core 0.2.606+; an older Core = logged, skipped). Clients only.
            if (!SystemInfo.graphicsDeviceType.Equals(UnityEngine.Rendering.GraphicsDeviceType.Null))
                try { RegisterStatsPage(); } catch (Exception e) { Debug.LogWarning("[VAInventory] Player > Stats page skipped: " + e.GetType().Name + ": " + e.Message); }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void RegisterStatsPage() => Managers.PlayerStatsPage.Register();

        private IEnumerator DelayedDedicatedServerInit()
        {
            while (ZNetScene.instance == null || ObjectDB.instance == null)
                yield return null;

            yield return new WaitForEndOfFrame();
            FinishInit();
        }

        /// <summary>
        /// Every patch class is applied on its own, so one that can't attach (a renamed game method, a wrong parameter name) is logged
        /// and skipped instead of throwing out of PatchAll and aborting the rest of VAInventory's start-up.
        /// </summary>
        private void PatchEachClass()
        {
            int ok = 0;
            var failed = new List<string>();
            foreach (Type type in AccessTools.GetTypesFromAssembly(GetType().Assembly))   // exactly the types Harmony.PatchAll walks
            {
                try { if (harmony.CreateClassProcessor(type).Patch()?.Count > 0) ok++; }
                catch (Exception e)
                {
                    failed.Add(type.FullName);
                    Debug.LogError($"[VAInventory] patch class {type.FullName} failed to attach and is skipped (the rest of VAInventory still loads): {(e.InnerException ?? e).Message}");
                }
            }
            Debug.Log($"[VAInventory] patches attached: {ok} class(es){(failed.Count > 0 ? $"; FAILED {failed.Count}: {string.Join(", ", failed.ToArray())}" : "; none failed")}");
        }

        private void FinishInit()
        {
            PatchEachClass();

            // Register custom UI backgrounds
            VerdantsAscent.Managers.UIBackgroundManager.RegisterDefaultBackgrounds();
            VerdantsAscent.Managers.UiDump.Register();
#if FIRES_TESTS
            VerdantsAscent.Managers.UiTest.Register();
#endif

            // Force immediate panel rebuild now that slots are ready
            EquipmentPanel.UpdatePanel();

            Debug.Log($"{PluginName}: Initialized Ã¢â‚¬â€ Ready to cause Trouble");

            StartCoroutine(WaitForZNetReady());
        }

        private void BindConfigsToSync()
        {
            var cm = ConfigManager.Instance;

            // Server authority locking config
            configSync.AddLockingConfigEntry(cm.configServerAuthority);

            // General verbose logging
            configSync.AddConfigEntry(cm.configVerboseLogging);

            // Extra Slots configs (synced with server)
            configSync.AddConfigEntry(cm.configExtraRows);
            configSync.AddConfigEntry(cm.configEquipmentPanelRows);

            // Sync all per-piece enabled states (recipes are client-side only)
            foreach (var kvp in cm.configPerPieceEnabled)
                configSync.AddConfigEntry(kvp.Value);

            // Add SettingChanged events to refresh slots and panel when slot configs change
            cm.configExtraRows.SettingChanged += (_, _) => EquipmentPanel.RefreshSlotsAndPanel();
            cm.configEquipmentPanelRows.SettingChanged += (_, _) => EquipmentPanel.RefreshSlotsAndPanel();
        }

        private void OnDestroy()
        {
            if (assetBundle != null)
            {
                try
                {
                    assetBundle.Unload(true);
                    Debug.Log($"{PluginName}: AssetBundle unloaded.");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{PluginName}: AssetBundle unload error: {ex}");
                }
                finally
                {
                    assetBundle = null;
                }
            }

            if (RootObject != null)
            {
                UnityEngine.Object.Destroy(RootObject);
                RootObject = null;
            }

            if (configSync != null)
            {
                configSync.LockedConfigChanged -= ConfigManager.Instance.OnConfigLockChanged;
                configSync = null;
            }

            ConfigManager.Instance.Dispose();

            Instance = null;
        }

        public static bool IsObjectDBValid() => ObjectDB.instance != null;

        private void LoadAssets()
        {
            if (assetBundle != null)
            {
                Debug.Log($"{PluginName}: AssetBundle already loaded.");
                return;
            }

            try
            {
                assetBundle = GetAssetBundleFromResources("vainventory"); // Change if your bundle name differs

                if (assetBundle == null)
                {
                    Debug.Log($"{PluginName}: No asset bundle found Ã¢â‚¬â€ this is fine if no custom assets yet.");
                    return;
                }

                // Future: load icons here
                Debug.Log($"{PluginName}: AssetBundle loaded.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{PluginName}: Asset load failed: {ex}");
            }
        }

        internal static AssetBundle GetAssetBundleFromResources(string name)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                Debug.Log($"{PluginName}: No embedded resource ending with '{name}'.");
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            return AssetBundle.LoadFromStream(stream);
        }

        public static void AddPrefabsToZNetScene(ZNetScene zNetScene)
        {
            if (zNetScene == null) return;

            int added = 0;
            foreach (var prefab in piecePrefabs.Concat(itemPrefabs))
            {
                if (prefab != null && zNetScene.GetPrefab(prefab.name) == null)
                {
                    zNetScene.m_prefabs.Add(prefab);
                    added++;
                }
            }

            zNetScene.m_prefabs.RemoveAll(p => p == null);
            if (added > 0) Debug.Log($"{PluginName}: added {added} prefab(s) to ZNetScene");
        }

        public static void AddItemsToObjectDB()
        {
            if (!IsObjectDBValid()) return;

            foreach (var item in itemPrefabs)
            {
                if (item == null) continue;
                if (ObjectDB.instance.GetItemPrefab(item.name) != null) continue;

                ObjectDB.instance.m_items.Add(item);

                var hashDict = (Dictionary<int, GameObject>)typeof(ObjectDB)
                    .GetField("m_itemByHash", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(ObjectDB.instance);

                if (hashDict != null)
                {
                    int hash = item.name.GetStableHashCode();
                    if (!hashDict.ContainsKey(hash))
                        hashDict[hash] = item;
                }

                Debug.Log($"{PluginName}: Registered item to ObjectDB: {item.name}");
            }
        }

        private IEnumerator WaitForZNetReady()
        {
            Debug.Log($"{PluginName}: Waiting for ZNetScene & ObjectDB...");

            while (ZNetScene.instance == null || ObjectDB.instance == null)
                yield return null;

            yield return new WaitForEndOfFrame();

            AddItemsToObjectDB();
            AddPrefabsToZNetScene(ZNetScene.instance);

            VAPieceManager.AddPiecesToHammer();
            VARecipeManager.UpdatePieceRequirements();

            Debug.Log($"{PluginName}: Systems fully initialized Ã¢â‚¬â€ ready!");

            // mark and notify subscribers that systems are ready
            SystemsReadyFlag = true;
            try { SystemsReady?.Invoke(); } catch (Exception ex) { Debug.LogError($"{PluginName}: SystemsReady event threw: {ex}"); }

            // Ensure HotbarManager exists after systems are ready
            try
            {
                if (GameObject.Find("VA_HotbarManager") == null)
                {
                    var go = new GameObject("VA_HotbarManager");
                    go.AddComponent<VerdantsAscent.Managers.HotbarManager>();
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    Debug.Log("[VAInventory] HotbarManager GameObject created after SystemsReady.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"{PluginName}: Failed to create HotbarManager after SystemsReady: {ex}");
            }
        }

        private void ReloadConfigAndBackground()
        {
            // Example: call this when config file changes
            VAMiscAssetManager.LoadAllBackgrounds();
            string filename = ConfigManager.Instance.EquipmentPanelBackground?.Value;
            if (!string.IsNullOrEmpty(filename))
            {
                VAMiscAssetManager.RefreshCustomBackground(filename);
                UIBackgroundManager.ForceBackgroundRefresh("EquipmentPanel");
                EquipmentPanel.UpdateBackground();
            }
        }

        // Harmony patches unchanged
        [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB")]
        public static class ObjectDB_CopyOtherDB_Patch
        {
            public static void Postfix()
            {
                if (!IsObjectDBValid()) return;
                // 1.0.360 (load/save audit M10): a throw here skipped the later CopyOtherDB postfixes; each step is caught on its own
                try { AddItemsToObjectDB(); } catch (System.Exception e) { Debug.LogWarning($"[VAInventory] CopyOtherDB: AddItemsToObjectDB failed: {e}"); }
                try { VAPieceManager.AddPiecesToHammer(); } catch (System.Exception e) { Debug.LogWarning($"[VAInventory] CopyOtherDB: AddPiecesToHammer failed: {e}"); }
                try { VARecipeManager.UpdatePieceRequirements(); } catch (System.Exception e) { Debug.LogWarning($"[VAInventory] CopyOtherDB: UpdatePieceRequirements failed: {e}"); }
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        public static class ZNetScene_Awake_Patch
        {
            public static void Prefix(ZNetScene __instance)
            {
                // 1.0.360 (load/save audit, same risk as VAB M11): a throw in a ZNetScene.Awake PREFIX skips vanilla's Awake = no world
                try { if (__instance != null) AddPrefabsToZNetScene(__instance); } catch (System.Exception e) { Debug.LogWarning($"[VAInventory] ZNetScene.Awake: AddPrefabsToZNetScene failed: {e}"); }
            }
        }
    }
}