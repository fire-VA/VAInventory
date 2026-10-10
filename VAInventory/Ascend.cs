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
    // Load after VABackpacks when it is installed, so BackpackBridge finds it in Awake (Fire 10-07, backpack_shot_q2).
    [BepInDependency("com.Fire.VA_Backpacks", BepInDependency.DependencyFlags.SoftDependency)]
    public class VAInventory : BaseUnityPlugin
    {
        public static VAInventory Instance;
        public static readonly Harmony harmony = new("com.Fire.VAInventory");

        public const string PluginGUID = "com.Fire.VAInventory";
        public const string PluginName = "VAInventory";
        public const string PluginVersion = "1.0.106";

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
                MinimumRequiredVersion = PluginVersion
            };
            configSync.LockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;

            ConfigManager.Instance.Initialize(Config);
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

            // Shared help panel (FiresCore) — SOFT dependency; no-ops when Core is absent.
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
        }

        private IEnumerator DelayedDedicatedServerInit()
        {
            while (ZNetScene.instance == null || ObjectDB.instance == null)
                yield return null;

            yield return new WaitForEndOfFrame();
            FinishInit();
        }

        private void FinishInit()
        {
            harmony.PatchAll();

            // Register custom UI backgrounds
            VerdantsAscent.Managers.UIBackgroundManager.RegisterDefaultBackgrounds();
            VerdantsAscent.Managers.UiDump.Register();
            VerdantsAscent.Managers.UiTest.Register();

            // Force immediate panel rebuild now that slots are ready
            EquipmentPanel.UpdatePanel();

            Debug.Log($"{PluginName}: Initialized — Ready to cause Trouble");

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
                    Debug.Log($"{PluginName}: No asset bundle found — this is fine if no custom assets yet.");
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

            Debug.Log($"{PluginName}: Systems fully initialized — ready!");

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
                AddItemsToObjectDB();
                VAPieceManager.AddPiecesToHammer();
                VARecipeManager.UpdatePieceRequirements();
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        public static class ZNetScene_Awake_Patch
        {
            public static void Prefix(ZNetScene __instance)
            {
                if (__instance != null)
                    AddPrefabsToZNetScene(__instance);
            }
        }
    }
}