// PetBridge — reflection-based soft-reference layer over FiresNPCs' PetApi.
//
// Same pattern as BackpackBridge: no hard assembly dependency. When FiresNPCs
// (assembly "FiresRPGmaker") is not loaded, IsAvailable is false, the Pet slot
// stays hidden and IsPet returns false.
//
// FiresNPCs may load after VAInventory, so resolution is lazy and retried
// (at most once every few seconds) until the API type is found; after that
// the handles are cached for the session.

using System;
using System.Reflection;

namespace VerdantsAscent.Managers
{
    public static class PetBridge
    {
        public const string FIRESNPCS_ASSEMBLY_NAME = "FiresRPGmaker";
        public const string API_TYPE_FQN = "FiresRPGmaker.Modules.Pets.PetApi";
        private const double RetrySeconds = 5.0;

        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("VAInventory.Panel");

        private static MethodInfo _isPet;
        private static MethodInfo _onPetSlotChanged;   // optional hint
        private static bool _resolved;
        private static DateTime _nextTry = DateTime.MinValue;

        /// <summary>True once FiresNPCs' PetApi.IsPet was found. Retries resolution lazily while false.</summary>
        public static bool IsAvailable
        {
            get
            {
                if (!_resolved) Initialize();
                return _resolved;
            }
        }

        /// <summary>Resolves PetApi. Safe to call any time; while unresolved it walks the loaded assemblies at most once
        /// every few seconds.</summary>
        public static void Initialize()
        {
            if (_resolved) return;
            var now = DateTime.UtcNow;
            if (now < _nextTry) return;
            _nextTry = now.AddSeconds(RetrySeconds);
            try
            {
                Type api = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        api = asm.GetType(API_TYPE_FQN, false);
                        if (api != null) break;
                    }
                    catch { }
                }
                if (api == null) return;

                var isPet = api.GetMethod("IsPet", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(ItemDrop.ItemData) }, null);
                if (isPet == null || isPet.ReturnType != typeof(bool)) return;

                _onPetSlotChanged = api.GetMethod("OnPetSlotChanged", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(Player), typeof(ItemDrop.ItemData) }, null);
                _isPet = isPet;
                _resolved = true;
                Log.LogDebug($"[VAInventory] PetBridge: {API_TYPE_FQN} found in {api.Assembly.GetName().Name}"
                    + $" (OnPetSlotChanged {(_onPetSlotChanged != null ? "present" : "absent")})");
            }
            catch (Exception e)
            {
                Log.LogWarning($"[VAInventory] PetBridge init failed: {e.Message}");
            }
        }

        /// <summary>True when FiresNPCs says this item is a pet. False when unavailable or on any exception.</summary>
        public static bool IsPet(ItemDrop.ItemData item)
        {
            if (item == null || !IsAvailable) return false;
            try { return (bool)_isPet.Invoke(null, new object[] { item }); }
            catch { return false; }
        }

        /// <summary>Hint to FiresNPCs that the Pet slot changed (item may be null when emptied). No-op when absent.</summary>
        public static void NotifyChanged(Player player, ItemDrop.ItemData item)
        {
            if (!IsAvailable || _onPetSlotChanged == null) return;
            try { _onPetSlotChanged.Invoke(null, new object[] { player, item }); }
            catch (Exception e)
            {
                Log.LogWarning($"[VAInventory] PetBridge.OnPetSlotChanged threw: {(e.InnerException ?? e).Message}");
            }
        }
    }
}
