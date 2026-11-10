// BackpackBridge — reflection-based soft-reference layer over VABackpacks.
//
// This file gives VAInventory talk-only access to VABackpacks's public
// BackpackApi without taking a hard assembly dependency. When VABackpacks
// is not loaded, IsAvailable returns false and every other accessor
// degrades cleanly (returns null/0/false).
//
// All field/method handles are cached at Initialize() time and reused
// for the rest of the session. Initialize() is idempotent and cheap
// when the API is missing — it just walks loaded assemblies once.
//
// See Refs/BACKPACK_AND_COIN_SLOT_PLAN.md §7 for the design rationale.

using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

namespace VerdantsAscent.Managers
{
    public static class BackpackBridge
    {
        public const string VABACKPACKS_ASSEMBLY_NAME = "VABackpacks";
        public const string API_TYPE_FQN = "VerdantsAscent.Managers.BackpackApi";
        public const string CONFIG_TYPE_FQN = "VerdantsAscent.Managers.BackpackConfig";

        // ── Cached reflection handles ─────────────────────────────────

        private static Type _apiType;
        private static Type _cfgType;
        private static MethodInfo _isBackpack;
        private static MethodInfo _getEquipped;
        private static MethodInfo _getEquippedList;
        private static MethodInfo _getRows;
        private static MethodInfo _getCols;
        private static MethodInfo _getSlotCount;
        private static MethodInfo _readStorage;
        private static MethodInfo _writeStorage;
        private static MethodInfo _getConfig;
        private static FieldInfo _onEquippedField;
        private static FieldInfo _onUnequippedField;

        // Special-slot rail (VABackpacks 1.0.20+). OPTIONAL: older builds lack
        // these, the handles stay null and the rail simply never appears.
        private static MethodInfo _getSpecialSlots;
        private static MethodInfo _readSpecialSlot;
        private static MethodInfo _writeSpecialSlot;
        private static MethodInfo _specialSlotAccepts;
        private static MethodInfo _isBagUpgrade;
        private static MethodInfo _getSlotIcon;
        private static PropertyInfo _slotStorageActive;

        /// <summary>VABackpacks stores bag contents in VAInventory's slots (its slots mode). Older builds without the
        /// property: assume yes, as VABackpacks always picks slots mode when VAInventory is loaded.</summary>
        public static bool SlotStorageActive
        {
            get
            {
                if (!IsAvailable) return false;
                try { return _slotStorageActive == null || (bool)_slotStorageActive.GetValue(null); }
                catch { return true; }
            }
        }
        private static PropertyInfo _specialSlotsVersion;

        // Reflection accessors for fields on BackpackConfig
        private static PropertyInfo _cfgRows;
        private static PropertyInfo _cfgCols;

        // ── State ──────────────────────────────────────────────────────

        // VAInventory's Awake can run before BepInEx loads VABackpacks, and a one-shot
        // Initialize then hid the Backpack slot for the whole session. Not found yet = look again, at most every 5 s.
        private static bool _available, _missingLogged;
        private static float _nextRetry;
        public static bool IsAvailable
        {
            get
            {
                if (!_available && Time.realtimeSinceStartup >= _nextRetry)
                {
                    _nextRetry = Time.realtimeSinceStartup + 5f;
                    Initialize();
                }
                return _available;
            }
            private set => _available = value;
        }

        /// <summary>
        /// Subscribers must use these C# events (which we relay from the
        /// VABackpacks API events via reflection-built delegates).
        ///
        /// Args: (Player, ItemDrop.ItemData backpackItem).
        /// The third argument from VABackpacks (BackpackConfig) is converted
        /// to a duck-typed projection — we expose Rows/Columns/etc. via the
        /// helper accessors below rather than passing the alien type around.
        /// </summary>
        public static event Action<Player, ItemDrop.ItemData> OnBackpackEquipped;
        public static event Action<Player, ItemDrop.ItemData> OnBackpackUnequipped;

        // ── Initialization ────────────────────────────────────────────

        public static void Initialize()
        {
            try
            {
                var asm = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => a != null && a.GetName().Name == VABACKPACKS_ASSEMBLY_NAME);

                if (asm == null)
                {
                    if (!_missingLogged) { _missingLogged = true; Debug.Log("[VAInventory] VABackpacks assembly not loaded yet — backpack slot hidden until it is (re-checked every 5 s)."); }
                    IsAvailable = false;
                    return;
                }

                _apiType = asm.GetType(API_TYPE_FQN);
                _cfgType = asm.GetType(CONFIG_TYPE_FQN);
                if (_apiType == null || _cfgType == null)
                {
                    Debug.LogWarning($"[VAInventory] VABackpacks loaded but API types missing " +
                                     $"(api={_apiType != null}, cfg={_cfgType != null}). Bridge disabled.");
                    IsAvailable = false;
                    return;
                }

                _isBackpack    = AccessTools.Method(_apiType, "IsBackpack", [typeof(ItemDrop.ItemData)]);
                _getEquipped   = AccessTools.Method(_apiType, "GetEquippedBackpack", [typeof(Player)]);
                // GetEquippedBackpacks returns IReadOnlyList<ItemDrop.ItemData> — added in
                // multi-bag refactor. Old VABackpacks builds without this method are tolerated:
                // the method handle stays null and GetEquippedBackpacks falls back to the
                // single-bag accessor wrapped in a list.
                _getEquippedList = AccessTools.Method(_apiType, "GetEquippedBackpacks", [typeof(Player)]);
                _getRows       = AccessTools.Method(_apiType, "GetEquippedRows", [typeof(Player)]);
                _getCols       = AccessTools.Method(_apiType, "GetEquippedColumns", [typeof(Player)]);
                _getSlotCount  = AccessTools.Method(_apiType, "GetEquippedSlotCount", [typeof(Player)]);
                _readStorage   = AccessTools.Method(_apiType, "ReadStorage", [typeof(ItemDrop.ItemData)]);
                _writeStorage  = AccessTools.Method(_apiType, "WriteStorage", [typeof(ItemDrop.ItemData), typeof(string)]);
                _getConfig     = AccessTools.Method(_apiType, "GetConfig", [typeof(ItemDrop.ItemData)]);
                _getBonus      = AccessTools.Method(_apiType, "GetBonus", [typeof(ItemDrop.ItemData)]);   // needs VABackpacks 1.0.49+
                _bonusProvider = AccessTools.Field(_apiType, "BonusProvider");

                _onEquippedField   = AccessTools.Field(_apiType, "OnEquipped");
                _onUnequippedField = AccessTools.Field(_apiType, "OnUnequipped");

                _cfgRows = AccessTools.Property(_cfgType, "Rows");
                _cfgCols = AccessTools.Property(_cfgType, "Columns");

                // Optional special slots — never part of the sanity check below.
                _getSpecialSlots     = AccessTools.Method(_apiType, "GetSpecialSlots", [typeof(ItemDrop.ItemData)]);
                _readSpecialSlot     = AccessTools.Method(_apiType, "ReadSpecialSlot", [typeof(ItemDrop.ItemData), typeof(string)]);
                _writeSpecialSlot    = AccessTools.Method(_apiType, "WriteSpecialSlot", [typeof(ItemDrop.ItemData), typeof(string), typeof(ItemDrop.ItemData)]);
                _specialSlotAccepts  = AccessTools.Method(_apiType, "SpecialSlotAccepts", [typeof(ItemDrop.ItemData), typeof(string), typeof(ItemDrop.ItemData)]);
                _isBagUpgrade        = AccessTools.Method(_apiType, "IsBagUpgrade", [typeof(ItemDrop.ItemData)]);   // VABackpacks 1.0.96+
                LookUpgradeHooks();   // the bag-cells provider is needed before any rail click (bag upgrades tick on their own)
                _getSlotIcon         = AccessTools.Method(_apiType, "GetSlotIcon", [typeof(string)]);
                _specialSlotsVersion = AccessTools.Property(_apiType, "SpecialSlotsVersion");
                _slotStorageActive   = AccessTools.Property(_apiType, "IsSlotBasedStorageActive");

                // Sanity-check the essentials
                if (_isBackpack == null || _getEquipped == null
                    || _readStorage == null || _writeStorage == null)
                {
                    Debug.LogWarning("[VAInventory] BackpackApi method signatures don't match expectations. Bridge disabled.");
                    IsAvailable = false;
                    return;
                }

                // Hook the events. The VABackpacks API events are typed
                // Action<Player, ItemDrop.ItemData, BackpackConfig> which
                // is a closed generic in this AppDomain, so we build a
                // matching delegate via Delegate.CreateDelegate.
                HookEquipEvents();

                IsAvailable = true;
                Debug.Log("[VAInventory] BackpackBridge initialized — slot-based backpack storage active.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] BackpackBridge.Initialize failed: {ex}");
                IsAvailable = false;
            }
        }

        private static void HookEquipEvents()
        {
            if (_onEquippedField == null || _onUnequippedField == null) return;

            // Build the (Player, ItemDrop.ItemData, BackpackConfig) → void
            // delegate type that BackpackApi.OnEquipped expects, and bind
            // it to our private relay method. We need Delegate.Combine
            // because the field stores a multicast delegate.
            var equipDelegateType = typeof(Action<,,>).MakeGenericType(
                typeof(Player), typeof(ItemDrop.ItemData), _cfgType);
            var equipMethod = typeof(BackpackBridge).GetMethod(
                nameof(RelayEquipped), BindingFlags.Static | BindingFlags.NonPublic);
            var equipDel = Delegate.CreateDelegate(equipDelegateType, equipMethod);
            var existingEquip = (Delegate)_onEquippedField.GetValue(null);
            _onEquippedField.SetValue(null, Delegate.Combine(existingEquip, equipDel));

            var unequipDelegateType = typeof(Action<,>).MakeGenericType(
                typeof(Player), typeof(ItemDrop.ItemData));
            var unequipMethod = typeof(BackpackBridge).GetMethod(
                nameof(RelayUnequipped), BindingFlags.Static | BindingFlags.NonPublic);
            var unequipDel = Delegate.CreateDelegate(unequipDelegateType, unequipMethod);
            var existingUnequip = (Delegate)_onUnequippedField.GetValue(null);
            _onUnequippedField.SetValue(null, Delegate.Combine(existingUnequip, unequipDel));
        }

        // The relay methods take `object` for the alien BackpackConfig type
        // because we never look at it on this side — Rows/Columns/etc. are
        // pulled from the API helpers (GetEquippedRows/Columns) instead.
        private static void RelayEquipped(Player player, ItemDrop.ItemData item, object _)
        {
            try
            {
                OnBackpackEquipped?.Invoke(player, item);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] OnBackpackEquipped subscriber threw: {ex}");
            }
        }

        private static void RelayUnequipped(Player player, ItemDrop.ItemData item)
        {
            try
            {
                OnBackpackUnequipped?.Invoke(player, item);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] OnBackpackUnequipped subscriber threw: {ex}");
            }
        }

        // ── Public accessors (safe when bridge is unavailable) ───────

        // Optional bag bonuses (VABackpacks 1.0.49+); older VABackpacks leaves both null.
        private static System.Reflection.MethodInfo _getBonus;
        private static System.Reflection.FieldInfo _bonusProvider;

        /// <summary>True when VABackpacks has the bonus API AND another mod (FRC) set a provider.</summary>
        public static bool HasBonus => IsAvailable && _getBonus != null && _bonusProvider?.GetValue(null) != null;

        /// <summary>{ carryWeight, contentsWeightFactor, loadedSpeed, extraSlots } for one bag, or null.</summary>
        public static float[] GetBonus(ItemDrop.ItemData bag)
        {
            if (_getBonus == null || bag == null) return null;
            try { return _getBonus.Invoke(null, [bag]) as float[]; }
            catch { return null; }
        }

        public static bool IsBackpack(ItemDrop.ItemData item)
        {
            if (!IsAvailable || _isBackpack == null || item == null) return false;
            try { return (bool)_isBackpack.Invoke(null, [item]); }
            catch { return false; }
        }

        public static ItemDrop.ItemData GetEquippedBackpack(Player player)
        {
            if (!IsAvailable || _getEquipped == null || player == null) return null;
            try { return (ItemDrop.ItemData)_getEquipped.Invoke(null, [player]); }
            catch { return null; }
        }

        /// <summary>
        /// All currently-equipped registered backpacks on the player, in
        /// stable order (coexistence dict → armor slots → weapon slots).
        /// Empty list if none. VAInventory uses this to render one
        /// detached storage panel per equipped bag.
        ///
        /// Falls back to the single-bag accessor wrapped in a list when
        /// connected to an older VABackpacks build that doesn't expose
        /// GetEquippedBackpacks yet.
        /// </summary>
        public static System.Collections.Generic.IReadOnlyList<ItemDrop.ItemData>
            GetEquippedBackpacks(Player player)
        {
            if (!IsAvailable || player == null)
                return [];

            // Preferred: use the new list API if present.
            if (_getEquippedList != null)
            {
                try
                {
                    var raw = _getEquippedList.Invoke(null, [player]);
                    if (raw is System.Collections.Generic.IReadOnlyList<ItemDrop.ItemData> typed)
                        return typed;
                    if (raw is System.Collections.IEnumerable e)
                    {
                        var list = new System.Collections.Generic.List<ItemDrop.ItemData>();
                        foreach (var o in e)
                            if (o is ItemDrop.ItemData item) list.Add(item);
                        return list;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VAInventory] GetEquippedBackpacks invoke failed: {ex.Message}");
                }
            }

            // Fallback: single-bag accessor.
            var single = GetEquippedBackpack(player);
            return single != null
                ? (System.Collections.Generic.IReadOnlyList<ItemDrop.ItemData>)
                  [single]
                : [];
        }

        public static int GetEquippedRows(Player player)
        {
            if (!IsAvailable || _getRows == null || player == null) return 0;
            try { return (int)_getRows.Invoke(null, [player]); }
            catch { return 0; }
        }

        public static int GetEquippedColumns(Player player)
        {
            if (!IsAvailable || _getCols == null || player == null) return 0;
            try { return (int)_getCols.Invoke(null, [player]); }
            catch { return 0; }
        }

        public static int GetEquippedSlotCount(Player player)
        {
            if (!IsAvailable || _getSlotCount == null || player == null) return 0;
            try { return (int)_getSlotCount.Invoke(null, [player]); }
            catch { return 0; }
        }

        public static (int rows, int cols) GetConfigDimensions(ItemDrop.ItemData item)
        {
            if (!IsAvailable || _getConfig == null || item == null) return (0, 0);
            try
            {
                var cfg = _getConfig.Invoke(null, [item]);
                if (cfg == null) return (0, 0);
                int rows = _cfgRows != null ? (int)_cfgRows.GetValue(cfg) : 0;
                int cols = _cfgCols != null ? (int)_cfgCols.GetValue(cfg) : 0;
                return (rows, cols);
            }
            catch { return (0, 0); }
        }

        public static string ReadStorage(ItemDrop.ItemData backpackItem)
        {
            if (!IsAvailable || _readStorage == null || backpackItem == null) return string.Empty;
            try { return (string)(_readStorage.Invoke(null, [backpackItem]) ?? string.Empty); }
            catch { return string.Empty; }
        }

        public static void WriteStorage(ItemDrop.ItemData backpackItem, string base64)
        {
            if (!IsAvailable || _writeStorage == null || backpackItem == null) return;
            try { _writeStorage.Invoke(null, [backpackItem, base64 ?? string.Empty]); }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] BackpackBridge.WriteStorage failed: {ex.Message}");
            }
        }

        // ── Special slots (optional, VABackpacks 1.0.20+) ────────────

        /// <summary>True when the connected VABackpacks exposes the special-slot API.</summary>
        public static bool HasSpecialSlots =>
            IsAvailable && _getSpecialSlots != null && _readSpecialSlot != null
            && _writeSpecialSlot != null && _specialSlotAccepts != null;

        /// <summary>The bag's special-slot kinds top-down; empty when none or unsupported.</summary>
        public static string[] GetSpecialSlots(ItemDrop.ItemData bag)
        {
            if (!HasSpecialSlots || bag == null) return [];
            try { return (string[])_getSpecialSlots.Invoke(null, [bag]) ?? []; }
            catch { return []; }
        }

        public static ItemDrop.ItemData ReadSpecialSlot(ItemDrop.ItemData bag, string kind)
        {
            if (!HasSpecialSlots || bag == null) return null;
            try { return (ItemDrop.ItemData)_readSpecialSlot.Invoke(null, [bag, kind]); }
            catch { return null; }
        }

        /// <summary>Null item clears the slot. False when not accepted (or unsupported).</summary>
        public static bool WriteSpecialSlot(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData item)
        {
            if (!HasSpecialSlots || bag == null) return false;
            try { return (bool)_writeSpecialSlot.Invoke(null, [bag, kind, item]); }
            catch (Exception ex)
            {
                Debug.LogError($"[VAInventory] BackpackBridge.WriteSpecialSlot failed: {ex.Message}");
                return false;
            }
        }

        // ── Bag upgrade hooks (VABackpacks 1.0.98+): the rail asks the upgrade's option first ──
        private static MethodInfo _upgradeRightClick, _upgradeDropOnto, _upgradeBadge, _setBagCellsProvider;
        private static bool _upgradeHooksLooked;

        private static void LookUpgradeHooks()
        {
            if (_upgradeHooksLooked || _apiType == null) return;
            _upgradeHooksLooked = true;
            _upgradeRightClick = AccessTools.Method(_apiType, "BagUpgradeRightClick");
            _upgradeDropOnto = AccessTools.Method(_apiType, "BagUpgradeDropOnto");
            _upgradeBadge = AccessTools.Method(_apiType, "BagUpgradeBadge");
            _setBagCellsProvider = AccessTools.Method(_apiType, "SetBagCellsProvider");
            if (_setBagCellsProvider != null)
            {
                try
                {
                    System.Func<Player, ItemDrop.ItemData, System.Collections.Generic.List<ItemDrop.ItemData>> provider = BagCells;
                    _setBagCellsProvider.Invoke(null, [provider]);
                    Debug.Log("[VAInventory] bag upgrades: bag-cells provider handed to VABackpacks");
                }
                catch (Exception ex) { Debug.LogWarning($"[VAInventory] bag upgrades: bag-cells provider not set: {ex.Message}"); }
            }
            var setFree = AccessTools.Method(_apiType, "SetFreeBagCellsProvider");
            if (setFree != null)
            {
                try
                {
                    System.Func<Player, ItemDrop.ItemData, System.Collections.Generic.List<Vector2i>> free = FreeBagCells;
                    setFree.Invoke(null, [free]);
                    Debug.Log("[VAInventory] bag upgrades: free-bag-cells provider handed to VABackpacks");
                }
                catch (Exception ex) { Debug.LogWarning($"[VAInventory] bag upgrades: free-bag-cells provider not set: {ex.Message}"); }
            }
        }

        /// <summary>The empty, active cells of one worn bag (player-inventory grid positions).</summary>
        private static System.Collections.Generic.List<Vector2i> FreeBagCells(Player player, ItemDrop.ItemData bag)
        {
            var list = new System.Collections.Generic.List<Vector2i>();
            if (player == null || bag?.m_dropPrefab == null) return list;
            string prefab = bag.m_dropPrefab.name;
            foreach (var slot in Slots.AllSlots())
                if (slot != null && slot.IsBackpackStorageSlot && slot.IsActive && slot.Item == null && Slots.GetBackpackPrefabFromSlot(slot) == prefab) list.Add(slot.GridPosition);
            return list;
        }

        /// <summary>The items in one worn bag's cells (the player-inventory cells VAInventory shows as that bag's storage).</summary>
        private static System.Collections.Generic.List<ItemDrop.ItemData> BagCells(Player player, ItemDrop.ItemData bag)
        {
            var list = new System.Collections.Generic.List<ItemDrop.ItemData>();
            if (player == null || bag?.m_dropPrefab == null) return list;
            string prefab = bag.m_dropPrefab.name;
            foreach (var slot in Slots.AllSlots())
                if (slot != null && slot.IsBackpackStorageSlot && slot.Item != null && Slots.GetBackpackPrefabFromSlot(slot) == prefab) list.Add(slot.Item);
            return list;
        }

        public static bool BagUpgradeRightClick(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData upgrade)
        {
            LookUpgradeHooks();
            if (_upgradeRightClick == null) return false;
            try { return (bool)_upgradeRightClick.Invoke(null, [bag, kind, upgrade]); } catch { return false; }
        }

        public static bool BagUpgradeDropOnto(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData upgrade, ItemDrop.ItemData dropped, Inventory from)
        {
            LookUpgradeHooks();
            if (_upgradeDropOnto == null) return false;
            try { return (bool)_upgradeDropOnto.Invoke(null, [bag, kind, upgrade, dropped, from]); } catch { return false; }
        }

        private static MethodInfo _upgradeRemoving, _upgradeRemoveWarning;

        /// <summary>Just before the rail destroys a slotted upgrade: its option gives back what it holds (VABackpacks 1.0.100+).</summary>
        public static void BagUpgradeRemoving(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData upgrade)
        {
            if (_apiType == null) return;
            _upgradeRemoving ??= AccessTools.Method(_apiType, "BagUpgradeRemoving");
            if (_upgradeRemoving == null) return;
            try { _upgradeRemoving.Invoke(null, [bag, kind, upgrade]); } catch (Exception ex) { Debug.LogWarning($"[VAInventory] bag upgrade removing hook threw {(ex.InnerException ?? ex).Message}"); }
        }

        /// <summary>Extra lines for the remove / swap confirm, or null.</summary>
        public static string BagUpgradeRemoveWarning(ItemDrop.ItemData upgrade)
        {
            if (_apiType == null) return null;
            _upgradeRemoveWarning ??= AccessTools.Method(_apiType, "BagUpgradeRemoveWarning");
            if (_upgradeRemoveWarning == null) return null;
            try { return _upgradeRemoveWarning.Invoke(null, [upgrade]) as string; } catch { return null; }
        }

        private static MethodInfo _headerButtons, _headerClick;

        /// <summary>VABackpacks' bag header buttons for this bag, right to left, as { id, label, tooltip } (empty without 1.0.110+).</summary>
        public static System.Collections.Generic.List<string[]> GetBagHeaderButtons(ItemDrop.ItemData bag)
        {
            if (_apiType == null || bag == null) return new System.Collections.Generic.List<string[]>();
            _headerButtons ??= AccessTools.Method(_apiType, "GetBagHeaderButtons");
            if (_headerButtons == null) return new System.Collections.Generic.List<string[]>();
            try { return _headerButtons.Invoke(null, [bag]) as System.Collections.Generic.List<string[]> ?? new System.Collections.Generic.List<string[]>(); }
            catch { return new System.Collections.Generic.List<string[]>(); }
        }

        public static void ClickBagHeaderButton(string id, ItemDrop.ItemData bag)
        {
            if (_apiType == null) return;
            _headerClick ??= AccessTools.Method(_apiType, "ClickBagHeaderButton");
            try { _headerClick?.Invoke(null, [id, bag]); } catch (Exception ex) { Debug.LogWarning($"[VAInventory] bag header button {id}: {(ex.InnerException ?? ex).Message}"); }
        }

        /// <summary>The badge over a slotted upgrade ("" = none) and whether it warns.</summary>
        public static string BagUpgradeBadge(ItemDrop.ItemData upgrade, out bool warn)
        {
            warn = false;
            LookUpgradeHooks();
            if (_upgradeBadge == null || upgrade == null) return "";
            try
            {
                var args = new object[] { upgrade, false };
                string text = _upgradeBadge.Invoke(null, args) as string ?? "";
                warn = args[1] is bool w && w;
                return text;
            }
            catch { return ""; }
        }

        /// <summary>True for a VABackpacks bag upgrade: once slotted it is destroyed, never handed back, on a swap or removal.</summary>
        public static bool IsBagUpgrade(ItemDrop.ItemData item)
        {
            if (_isBagUpgrade == null || item == null) return false;
            try { return (bool)_isBagUpgrade.Invoke(null, [item]); }
            catch { return false; }
        }

        public static bool SpecialSlotAccepts(ItemDrop.ItemData bag, string kind, ItemDrop.ItemData item)
        {
            if (!HasSpecialSlots || bag == null || item == null) return false;
            try { return (bool)_specialSlotAccepts.Invoke(null, [bag, kind, item]); }
            catch { return false; }
        }

        /// <summary>Bumped by VABackpacks on every special-slot change; 0 when unsupported.</summary>
        public static int SpecialSlotsVersion
        {
            get
            {
                if (!HasSpecialSlots || _specialSlotsVersion == null) return 0;
                try { return (int)_specialSlotsVersion.GetValue(null, null); }
                catch { return 0; }
            }
        }

        public static Sprite GetSlotIcon(string kind)
        {
            if (!HasSpecialSlots || _getSlotIcon == null) return null;
            try { return _getSlotIcon.Invoke(null, [kind]) as Sprite; }
            catch { return null; }
        }
    }
}
