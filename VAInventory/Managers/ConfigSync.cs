using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;

namespace VerdantsAscent
{
    public class ConfigSync
    {
        // Packages above this size are deflated before sending.
        const int CompressThresholdBytes = 10000;
        // Packages above this size are split into fragments of this size.
        const int FragmentSizeBytes = 250000;
        // Sending waits while a peer's socket queue is above this, for at most SendQueueTimeoutSeconds.
        const int MaxSendQueueBytes = 20000;
        const float SendQueueTimeoutSeconds = 30f;

        public static bool ProcessingServerUpdate = false;

        public readonly string Name;
        public string DisplayName;
        public string CurrentVersion;
        public string MinimumRequiredVersion;
        public bool ModRequired;

        private bool? forceConfigLocking;
        private bool isSourceOfTruth = true;

        public static HashSet<ConfigSync> configSyncs = [];

        public HashSet<OwnConfigEntryBase> allConfigs = [];
        public HashSet<CustomSyncedValueBase> allCustomValues = [];

        public static bool isServer;
        public static bool lockExempt = false;

        private OwnConfigEntryBase lockedConfig;

        private const byte PARTIAL_CONFIGS = 1;
        private const byte FRAGMENTED_CONFIG = 2;
        private const byte COMPRESSED_CONFIG = 4;

        private readonly Dictionary<string, SortedDictionary<int, byte[]>> configValueCache = [];
        private readonly List<KeyValuePair<long, string>> cacheExpirations = [];

        private static long packageCounter = 0;

        private DateTime lastConfigLogTime = DateTime.MinValue;

        public bool IsLocked
        {
            get => (forceConfigLocking ?? (lockedConfig?.BaseConfig.BoxedValue is IConvertible value && value.ToInt32(CultureInfo.InvariantCulture) != 0)) && !lockExempt;
            set => forceConfigLocking = value;
        }

        public bool IsAdmin => lockExempt || isSourceOfTruth;

        public bool IsSourceOfTruth
        {
            get => isSourceOfTruth;
            internal set
            {
                if (value == isSourceOfTruth) return;
                isSourceOfTruth = value;
                SourceOfTruthChanged?.Invoke(value);
            }
        }

        public bool InitialSyncDone
        {
            get => initialSyncDone;
            internal set => initialSyncDone = value;
        }

        private bool initialSyncDone;

        public event Action<bool> SourceOfTruthChanged;

        public event Action LockedConfigChanged;

        public ConfigSync(string name)
        {
            Name = name;
            DisplayName = name;
            configSyncs.Add(this);
        }

        internal void SendAllValuesToJoiningPeer(ZNetPeer peer, bool peerIsAdmin)
        {
            var lockExemptVerdict = new PackageEntry
            {
                section = "Internal",
                key = "lockexempt",
                type = typeof(bool),
                value = peerIsAdmin,
            };

            ZPackage allValues = ConfigsToPackage(allConfigs.Select(config => config.BaseConfig), allCustomValues, [lockExemptVerdict], partial: false);
            List<ZNetPeer> joiningPeer = [peer];
            ZNet.instance.StartCoroutine(SendZPackage(joiningPeer, allValues, waitForSendQueue: false));
        }

        internal void RestoreLocalConfigs()
        {
            ResetConfigsFromServer();
            IsSourceOfTruth = true;
            InitialSyncDone = false;
        }

        internal static void RefreshReadOnlyFlagsForAll()
        {
            foreach (var configSync in configSyncs)
                configSync.ServerLockedSettingChanged();
        }

        public SyncedConfigEntry<T> AddConfigEntry<T>(ConfigEntry<T> configEntry)
        {
            var syncedEntry = ConfigData(configEntry) as SyncedConfigEntry<T> ?? new SyncedConfigEntry<T>(configEntry);

            var tags = configEntry.Description.Tags ?? [];
            if (!tags.OfType<ConfigurationManagerAttributes>().Any())
                tags = [new ConfigurationManagerAttributes(), .. tags];
            tags = [.. tags, .. new object[] { syncedEntry }];

            AccessTools.Field(typeof(ConfigDescription), "<Tags>k__BackingField").SetValue(configEntry.Description, tags);

            configEntry.SettingChanged += (sender, args) =>
            {
                if (ProcessingServerUpdate || !syncedEntry.SynchronizedConfig) return;
                Broadcast(ZRoutedRpc.Everybody, configEntry);
            };

            allConfigs.Add(syncedEntry);
            return syncedEntry;
        }

        public SyncedConfigEntry<T> AddLockingConfigEntry<T>(ConfigEntry<T> lockingConfig) where T : IConvertible
        {
            if (lockedConfig != null) throw new Exception("Cannot initialize locking ConfigEntry twice");

            lockedConfig = AddConfigEntry(lockingConfig);
            lockingConfig.SettingChanged += (sender, args) => LockedConfigChanged?.Invoke();

            return (SyncedConfigEntry<T>)lockedConfig;
        }

        internal void AddCustomValue(CustomSyncedValueBase customValue)
        {
            if (allCustomValues.Any(v => v.Identifier == customValue.Identifier) || customValue.Identifier == "serverversion")
                throw new Exception("Cannot have multiple settings with the same name or with a reserved name (serverversion)");

            allCustomValues.Add(customValue);
            allCustomValues = [.. allCustomValues.OrderByDescending(v => v.Priority)];

            customValue.ValueChanged += () =>
            {
                if (ProcessingServerUpdate) return;
                Broadcast(ZRoutedRpc.Everybody, customValue);
            };
        }

        public void Broadcast(long target, ConfigEntryBase config)
        {
            if (IsLocked && !IsAdmin) return;

            ZPackage package = ConfigsToPackage([config]);
            ZNet.instance?.StartCoroutine(SendZPackage(target, package));
        }

        public void Broadcast(long target, CustomSyncedValueBase customValue)
        {
            if (IsLocked && !IsAdmin) return;

            ZPackage package = ConfigsToPackage(customValues: [customValue]);
            ZNet.instance?.StartCoroutine(SendZPackage(target, package));
        }

        internal void RPC_FromServerConfigSync(ZRpc _, ZPackage package)
        {
            LockedConfigChanged += ServerLockedSettingChanged;
            IsSourceOfTruth = false;

            if (HandleConfigSyncRPC(0L, package, false))
                InitialSyncDone = true;
        }

        internal void RPC_FromOtherClientConfigSync(long sender, ZPackage package)
        {
            // security (Core 0.2.759 port): a client takes config values from the server only, never from another client
            if (!isServer && (ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID()))
            {
                Debug.LogWarning($"[{VAInventory.PluginName}] Rejected config package for {Name} from peer {sender}: not the server");
                return;
            }
            // the server takes a config push only from an admin (or itself), whether or not the config is locked
            if (isServer && !IsSenderAdminOnServer(sender))
            {
                Debug.LogWarning($"[{VAInventory.PluginName}] Rejected config push for {Name} from peer {sender}: not an admin");
                return;
            }
            HandleConfigSyncRPC(sender, package, true);
        }

        // Caps on received config packages (wire format unchanged; senders split at 250 KB per fragment).
        private const int MaxFragments = 1024;
        private const int MaxOpenReassemblies = 64;
        private const int MaxReassembledBytes = 64 * 1024 * 1024;
        private const int MaxDecompressedBytes = 64 * 1024 * 1024;
        private const int FragmentCacheLifetimeSeconds = 60;

        private static bool IsSenderAdminOnServer(long sender)
        {
            if (ZNet.instance == null) return false;
            if (sender == ZNet.GetUID()) return true; // the host's own push (listen server)
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            string hostName = peer?.m_socket?.GetHostName();
            return hostName != null && ZNet.instance.IsAdmin(hostName);
        }

        private bool HandleConfigSyncRPC(long sender, ZPackage package, bool clientUpdate)
        {
            try
            {
                if (isServer && sender != 0L && sender != ZDOMan.GetSessionID())   // 1.0.381 (standing RPC rule): server config only from an admin, locked or not; a sender it cannot name is refused
                {
                    string hostName = SnatchCurrentlyHandlingRPC.currentRpc?.GetSocket()?.GetHostName();
                    if (string.IsNullOrEmpty(hostName) || !(ZNet.instance.IsAdmin(hostName) || ZNet.instance.IsAdmin("Steam_" + hostName)))
                        return false;
                }

                cacheExpirations.RemoveAll(kv => kv.Key < DateTimeOffset.Now.Ticks && configValueCache.Remove(kv.Value));

                byte flags = package.ReadByte();

                if ((flags & FRAGMENTED_CONFIG) != 0)
                {
                    long packageId = package.ReadLong();
                    string cacheKey = sender.ToString() + packageId.ToString();

                    if (!configValueCache.TryGetValue(cacheKey, out var fragments))
                    {
                        fragments = [];
                        configValueCache[cacheKey] = fragments;
                        cacheExpirations.Add(new KeyValuePair<long, string>(DateTimeOffset.Now.Ticks + FragmentCacheLifetimeSeconds * TimeSpan.TicksPerSecond, cacheKey));
                    }

                    int fragmentIndex = package.ReadInt();
                    int fragmentCount = package.ReadInt();
                    if (fragmentCount <= 0 || fragmentCount > MaxFragments || fragmentIndex < 0 || fragmentIndex >= fragmentCount || configValueCache.Count > MaxOpenReassemblies)
                    {
                        configValueCache.Remove(cacheKey);
                        Debug.LogWarning($"[{VAInventory.PluginName}] Rejected config fragment for {Name} from peer {sender}: index {fragmentIndex} / count {fragmentCount} out of range or too many open reassemblies ({configValueCache.Count})");
                        return false;
                    }
                    fragments[fragmentIndex] = package.ReadByteArray();
                    long reassembledBytes = 0;
                    foreach (var f in fragments.Values) reassembledBytes += f.Length;
                    if (reassembledBytes > MaxReassembledBytes)
                    {
                        configValueCache.Remove(cacheKey);
                        Debug.LogWarning($"[{VAInventory.PluginName}] Rejected config fragments for {Name} from peer {sender}: {reassembledBytes} bytes exceeds the {MaxReassembledBytes} cap");
                        return false;
                    }

                    if (fragments.Count < fragmentCount) return false;

                    configValueCache.Remove(cacheKey);
                    package = new ZPackage([.. fragments.Values.SelectMany(a => a)]);
                    flags = package.ReadByte();
                }

                ProcessingServerUpdate = true;

                if ((flags & COMPRESSED_CONFIG) != 0)
                {
                    using var input = new MemoryStream(package.ReadByteArray());
                    using var output = new MemoryStream();
                    using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                    {
                        var buffer = new byte[81920]; int n;
                        while ((n = deflate.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, n);
                            if (output.Length > MaxDecompressedBytes)
                            {
                                Debug.LogWarning($"[{VAInventory.PluginName}] Rejected config package for {Name} from peer {sender}: decompressed size exceeds the {MaxDecompressedBytes} cap");
                                return false;
                            }
                        }
                    }

                    package = new ZPackage(output.ToArray());
                    flags = package.ReadByte();
                }

                if ((flags & PARTIAL_CONFIGS) == 0)
                    ResetConfigsFromServer();

                var parsed = ReadConfigsFromPackage(package, fromServer: !isServer && !clientUpdate);

                ConfigFile configFile = null;
                bool saveOnSet = false;
                bool anyValueChanged = false;

                foreach (var kv in parsed.configValues)
                {
                    if (!isServer && kv.Key.LocalBaseValue == null)
                        kv.Key.LocalBaseValue = kv.Key.BaseConfig.BoxedValue;

                    if (configFile == null)
                    {
                        configFile = kv.Key.BaseConfig.ConfigFile;
                        saveOnSet = configFile.SaveOnConfigSet;
                        configFile.SaveOnConfigSet = false;
                    }

                    object previousValue = kv.Key.BaseConfig.BoxedValue;
                    kv.Key.BaseConfig.BoxedValue = kv.Value;
                    anyValueChanged |= !Equals(previousValue, kv.Key.BaseConfig.BoxedValue);
                }

                if (configFile != null)
                {
                    configFile.SaveOnConfigSet = saveOnSet;
                    if (anyValueChanged) configFile.Save();
                }

                foreach (var kv in parsed.customValues)
                {
                    if (!isServer && kv.Key.LocalBaseValue == null)
                        kv.Key.LocalBaseValue = kv.Key.BoxedValue;

                    kv.Key.BoxedValue = kv.Value;
                }

                if ((DateTime.Now - lastConfigLogTime).TotalSeconds > 12)
                {
                    Debug.Log($"[{VAInventory.PluginName}] Received {parsed.configValues.Count} configs and {parsed.customValues.Count} custom values from {(isServer || clientUpdate ? $"client {sender}" : "server")} for mod {DisplayName ?? Name}");
                    lastConfigLogTime = DateTime.Now;
                }

                if (!isServer)
                    ServerLockedSettingChanged();

                return true;
            }
            finally
            {
                ProcessingServerUpdate = false;
            }
        }

        private void ServerLockedSettingChanged()
        {
            foreach (var config in allConfigs)
            {
                var attr = config.BaseConfig.Description.Tags?
                    .OfType<ConfigurationManagerAttributes>()
                    .FirstOrDefault();

                if (attr != null)
                {
                    attr.ReadOnly = !IsWritableConfig(config);
                }
            }
        }

        internal static bool IsWritableConfig(OwnConfigEntryBase config)
        {
            var sync = configSyncs.FirstOrDefault(cs => cs.allConfigs.Contains(config));
            if (sync == null || sync.IsSourceOfTruth || !config.SynchronizedConfig || config.LocalBaseValue == null)
                return true;

            if (sync.IsLocked) return false;
            return config != sync.lockedConfig || lockExempt;
        }

        internal void ResetConfigsFromServer()
        {
            ConfigFile configFile = null;
            bool saveOnSet = false;
            bool anyValueChanged = false;

            foreach (var config in allConfigs.Where(c => c.LocalBaseValue != null))
            {
                if (configFile == null)
                {
                    configFile = config.BaseConfig.ConfigFile;
                    saveOnSet = configFile.SaveOnConfigSet;
                    configFile.SaveOnConfigSet = false;
                }

                object previousValue = config.BaseConfig.BoxedValue;
                config.BaseConfig.BoxedValue = config.LocalBaseValue;
                anyValueChanged |= !Equals(previousValue, config.BaseConfig.BoxedValue);
                config.LocalBaseValue = null;
            }

            if (configFile != null)
            {
                configFile.SaveOnConfigSet = saveOnSet;
                if (anyValueChanged) configFile.Save();
            }

            foreach (var customValue in allCustomValues.Where(c => c.LocalBaseValue != null))
            {
                customValue.BoxedValue = customValue.LocalBaseValue;
                customValue.LocalBaseValue = null;
            }

            LockedConfigChanged -= ServerLockedSettingChanged;
            ServerLockedSettingChanged();
        }

        private ParsedConfigs ReadConfigsFromPackage(ZPackage package, bool fromServer)
        {
            var parsed = new ParsedConfigs();

            var configs = allConfigs.ToDictionary(c => $"{c.BaseConfig.Definition.Section}*{c.BaseConfig.Definition.Key}", c => c);

            int count = package.ReadInt();
            // SECURITY (10-23): never more entries than this mod syncs (plus slack for a newer peer's extra keys)
            if (count < 0 || count > configs.Count + allCustomValues.Count + 64)
            {
                Debug.LogWarning($"[{VAInventory.PluginName}] Got {count} config entries for mod {DisplayName ?? Name} (expected at most {configs.Count + allCustomValues.Count}), abort reading of received configs");
                return new ParsedConfigs();
            }

            for (int i = 0; i < count; i++)
            {
                string section = package.ReadString();
                string key = package.ReadString();
                string typeName = package.ReadString();

                // SECURITY (10-23): the type comes from OUR bound entry (section/key), never from the sender's name. A known entry
                // only accepts its own type; an unknown one only a plain primitive / string (what a newer peer's extra key can be).
                // Anything else aborts the read: the value's bytes can't be skipped safely, and an arbitrary type must never be built.
                string expected = null;
                if (section == "Internal")
                {
                    if (key == "lockexempt") expected = GetZPackageTypeString(typeof(bool));
                    else if (allCustomValues.FirstOrDefault(v => v.Identifier == key) is CustomSyncedValueBase cv) expected = GetZPackageTypeString(cv.Type);
                }
                else if (configs.TryGetValue($"{section}*{key}", out var known)) expected = GetZPackageTypeString(ConfigType(known.BaseConfig));
                bool allowed = typeName == "" || typeName == expected || (expected == null && SafeUnknownTypes.Contains(typeName));
                if (!allowed)
                {
                    Debug.LogWarning($"[{VAInventory.PluginName}] Refused type {typeName} for {key} in section {section} for mod {DisplayName ?? Name} (expecting {expected ?? "a primitive or string"}), abort reading of received configs");
                    return new ParsedConfigs();
                }

                var type = typeName == "" ? null : Type.GetType(typeName);

                if (typeName == "" || type != null)
                {
                    object value;
                    try
                    {
                        value = typeName == "" ? null : ReadValueWithTypeFromZPackage(package, type);
                    }
                    catch (InvalidDeserializationTypeException ex)
                    {
                        Debug.LogWarning($"[{VAInventory.PluginName}] Got unexpected struct internal type {ex.received} for field {ex.field} struct {typeName} for {key} in section {section} for mod {DisplayName ?? Name}, expecting {ex.expected}");
                        return new ParsedConfigs();   // SECURITY (10-23): the rest of the package can't be read in step any more - abort, never continue
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[{VAInventory.PluginName}] Could not read {key} in section {section} ({typeName}) for mod {DisplayName ?? Name}: {ex.Message}, abort reading of received configs");
                        return new ParsedConfigs();
                    }

                    if (section == "Internal")
                    {
                        if (key == "lockexempt" && value is bool flag)
                        {
                            if (fromServer) lockExempt = flag;
                            continue;
                        }

                        if (allCustomValues.FirstOrDefault(v => v.Identifier == key) is CustomSyncedValueBase customValue)
                        {
                            if (typeName == "" && (!customValue.Type.IsValueType || Nullable.GetUnderlyingType(customValue.Type) != null) || GetZPackageTypeString(customValue.Type) == typeName)
                                parsed.customValues[customValue] = value;
                            else
                                Debug.LogWarning($"[{VAInventory.PluginName}] Got unexpected type {typeName} for internal value {key} for mod {DisplayName ?? Name}, expecting {customValue.Type.AssemblyQualifiedName}");
                        }
                    }
                    else if (configs.TryGetValue($"{section}*{key}", out var config))
                    {
                        var cfgType = ConfigType(config.BaseConfig);

                        if (typeName == "" && (!cfgType.IsValueType || Nullable.GetUnderlyingType(cfgType) != null) || GetZPackageTypeString(cfgType) == typeName)
                            parsed.configValues[config] = value;
                        else
                            Debug.LogWarning($"[{VAInventory.PluginName}] Got unexpected type {typeName} for {key} in section {section} for mod {DisplayName ?? Name}, expecting {cfgType.AssemblyQualifiedName}");
                    }
                    else
                    {
                        Debug.LogWarning($"[{VAInventory.PluginName}] Received unknown config entry {key} in section {section} for mod {DisplayName ?? Name}.");
                    }
                }
                else
                {
                    Debug.LogWarning($"[{VAInventory.PluginName}] Got invalid type {typeName}, abort reading of received configs");
                    return new ParsedConfigs();
                }
            }

            return parsed;
        }

        private static string GetZPackageTypeString(Type type) => type.AssemblyQualifiedName;

        // SECURITY (10-23): the only types an UNKNOWN entry (a newer peer's extra key) may carry, and a cap on every received collection
        private static readonly HashSet<string> SafeUnknownTypes = new HashSet<string>(new[] { typeof(bool), typeof(int), typeof(long), typeof(float), typeof(double), typeof(string) }.Select(GetZPackageTypeString));
        private const int MaxCollectionCount = 4096;

        private static int ReadCollectionCount(ZPackage package)
        {
            int count = package.ReadInt();
            if (count < 0 || count > MaxCollectionCount) throw new InvalidDeserializationTypeException { received = $"(collection of {count})", expected = $"(0..{MaxCollectionCount})", field = "count" };
            return count;
        }

        private static void AddValueToZPackage(ZPackage package, object value)
        {
            var type = value?.GetType();

            if (value is Enum)
                value = ((IConvertible)value).ToType(Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
            else if (value is ICollection collection)
            {
                package.Write(collection.Count);

                var enumerator = collection.GetEnumerator();
                try
                {
                    while (enumerator.MoveNext())
                        AddValueToZPackage(package, enumerator.Current);
                }
                finally
                {
                    if (enumerator is IDisposable disposable)
                        disposable.Dispose();
                }

                return;
            }
            else if (type != null && type.IsValueType && !type.IsPrimitive)
            {
                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                package.Write(fields.Length);

                foreach (var field in fields)
                {
                    package.Write(GetZPackageTypeString(field.FieldType));
                    AddValueToZPackage(package, field.GetValue(value));
                }

                return;
            }

            ZRpc.Serialize([value], ref package);
        }

        private static object ReadValueWithTypeFromZPackage(ZPackage package, Type type)
        {
            if (type != null && type.IsValueType && !type.IsPrimitive && !type.IsEnum)
            {
                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                int fieldCount = package.ReadInt();

                if (fieldCount != fields.Length)
                    throw new InvalidDeserializationTypeException { received = $"(field count: {fieldCount})", expected = $"(field count: {fields.Length})" };

                object instance = FormatterServices.GetUninitializedObject(type);

                foreach (var field in fields)
                {
                    string fieldTypeName = package.ReadString();
                    if (fieldTypeName != GetZPackageTypeString(field.FieldType))
                        throw new InvalidDeserializationTypeException { received = fieldTypeName, expected = GetZPackageTypeString(field.FieldType), field = field.Name };

                    field.SetValue(instance, ReadValueWithTypeFromZPackage(package, field.FieldType));
                }

                return instance;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                int count = ReadCollectionCount(package);
                var dictType = type;
                var pairType = typeof(KeyValuePair<,>).MakeGenericType(type.GenericTypeArguments);

                var instance = (IDictionary)Activator.CreateInstance(dictType);

                var keyField = pairType.GetField("key", BindingFlags.Instance | BindingFlags.NonPublic);
                var valueField = pairType.GetField("value", BindingFlags.Instance | BindingFlags.NonPublic);

                for (int i = 0; i < count; i++)
                {
                    var pair = ReadValueWithTypeFromZPackage(package, pairType);
                    instance.Add(keyField.GetValue(pair), valueField.GetValue(pair));
                }

                return instance;
            }

            if (type.IsGenericType)
            {
                var collectionType = typeof(ICollection<>).MakeGenericType(type.GenericTypeArguments[0]);
                if (collectionType.IsAssignableFrom(type))
                {
                    int count = ReadCollectionCount(package);

                    var instance = Activator.CreateInstance(type);
                    var addMethod = collectionType.GetMethod("Add");

                    for (int i = 0; i < count; i++)
                        addMethod.Invoke(instance, [ReadValueWithTypeFromZPackage(package, type.GenericTypeArguments[0])]);

                    return instance;
                }
            }

            var param = (ParameterInfo)FormatterServices.GetUninitializedObject(typeof(ParameterInfo));
            AccessTools.Field(typeof(ParameterInfo), "ClassImpl").SetValue(param, type);

            var parameters = new List<object>();
            ZRpc.Deserialize([null, param], package, ref parameters);

            return parameters.First();
        }

        internal ZPackage ConfigsToPackage(IEnumerable<ConfigEntryBase> configs = null, IEnumerable<CustomSyncedValueBase> customValues = null, IEnumerable<PackageEntry> packageEntries = null, bool partial = true)
        {
            var configList = configs?.Where(c => ConfigData(c).SynchronizedConfig).ToList() ?? [];
            var customList = customValues?.ToList() ?? [];
            var entryList = packageEntries?.ToList() ?? [];

            var package = new ZPackage();

            package.Write((byte)(partial ? PARTIAL_CONFIGS : 0));
            package.Write(configList.Count + customList.Count + entryList.Count);

            foreach (var entry in entryList)
            {
                package.Write(entry.section);
                package.Write(entry.key);
                package.Write(entry.value == null ? "" : GetZPackageTypeString(entry.type));
                AddValueToZPackage(package, entry.value);
            }

            foreach (var customValue in customList)
            {
                package.Write("Internal");
                package.Write(customValue.Identifier);
                package.Write(GetZPackageTypeString(customValue.Type));
                AddValueToZPackage(package, customValue.BoxedValue);
            }

            foreach (var config in configList)
            {
                package.Write(config.Definition.Section);
                package.Write(config.Definition.Key);
                package.Write(GetZPackageTypeString(ConfigType(config)));
                AddValueToZPackage(package, config.BoxedValue);
            }

            return package;
        }

        private static Type ConfigType(ConfigEntryBase config) => ConfigType(config.SettingType);

        private static Type ConfigType(Type type) => type.IsEnum ? Enum.GetUnderlyingType(type) : type;

        public IEnumerator SendZPackage(long target, ZPackage package)
        {
            if (!ZNet.instance) yield break;

            var peers = ((List<ZNetPeer>)AccessTools.Field(typeof(ZRoutedRpc), "m_peers").GetValue(ZRoutedRpc.instance))
                .Where(p => target == ZRoutedRpc.Everybody || p.m_uid == target).ToList();

            var enumerator = SendZPackage(peers, package);

            while (enumerator.MoveNext())
                yield return enumerator.Current;
        }

        public IEnumerator SendZPackage(List<ZNetPeer> peers, ZPackage package) =>
            SendZPackage(peers, package, waitForSendQueue: true);

        private IEnumerator SendZPackage(List<ZNetPeer> peers, ZPackage package, bool waitForSendQueue)
        {
            if (!ZNet.instance) yield break;

            byte[] data = package.GetArray();

            if (data.Length > CompressThresholdBytes)
            {
                var compressed = new ZPackage();
                compressed.Write(COMPRESSED_CONFIG);

                using var output = new MemoryStream();
                using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal))
                    deflate.Write(data, 0, data.Length);

                compressed.Write(output.ToArray());
                package = compressed;
            }

            var writers = peers.Where(p => p.IsReady()).Select(p => DistributeConfigToPeers(p, package, waitForSendQueue)).ToList();

            writers.RemoveAll(w => !w.MoveNext());

            while (writers.Any())
            {
                yield return null;
                writers.RemoveAll(w => !w.MoveNext());
            }
        }

        private IEnumerator DistributeConfigToPeers(ZNetPeer peer, ZPackage package, bool waitForSendQueue)
        {
            byte[] data = package.GetArray();

            if (data.Length > FragmentSizeBytes)
            {
                int fragments = (data.Length + FragmentSizeBytes - 1) / FragmentSizeBytes;
                long packageId = ++packageCounter;

                for (int i = 0; i < fragments; i++)
                {
                    if (waitForSendQueue)
                        foreach (bool wait in waitForQueue())
                            yield return wait;

                    if (!peer.m_socket.IsConnected()) break;

                    var pkg = new ZPackage();
                    pkg.Write(FRAGMENTED_CONFIG);
                    pkg.Write(packageId);
                    pkg.Write(i);
                    pkg.Write(fragments);
                    pkg.Write([.. data.Skip(FragmentSizeBytes * i).Take(FragmentSizeBytes)]);

                    SendPackage(pkg);

                    if (i < fragments - 1) yield return true;
                }
            }
            else
            {
                if (waitForSendQueue)
                    foreach (bool wait in waitForQueue())
                        yield return wait;

                SendPackage(package);
            }

            IEnumerable<bool> waitForQueue()
            {
                float timeout = Time.time + SendQueueTimeoutSeconds;

                while (peer.m_socket.GetSendQueueSize() > MaxSendQueueBytes)
                {
                    if (Time.time > timeout)
                    {
                        Debug.Log($"Disconnecting {peer.m_uid} after 30 seconds config sending timeout");
                        peer.m_rpc.Invoke("Error", ZNet.ConnectionStatus.ErrorConnectFailed);
                        ZNet.instance.Disconnect(peer);
                        break;
                    }

                    yield return false;
                }
            }

            void SendPackage(ZPackage pkg)
            {
                if (isServer)
                    peer.m_rpc.Invoke(Name + " ConfigSync", pkg);
                else
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_server ? 0L : peer.m_uid, Name + " ConfigSync", pkg);
            }
        }

        internal static OwnConfigEntryBase ConfigData(ConfigEntryBase config) =>
            config.Description.Tags?.OfType<OwnConfigEntryBase>().SingleOrDefault();

        private void OnClientConfigChange(long senderId, ConfigEntryBase entry)
        {
            // Only enforce on dedicated server
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                if (!AdminSyncing.IsAdmin(senderId))
                {
                    Debug.Log($"{VAInventory.PluginName}: Non-admin {senderId} attempted config change. Re-syncing server config.");
                    // Re-send server config to this client
                    BroadcastConfigToClient(senderId);
                    return;
                }
            }

            // Proceed with normal config change handling for admins
            ApplyConfigChange(entry);
        }

        private void BroadcastConfigToClient(long clientId)
        {
#pragma warning disable CS0618
            foreach (var entry in VAInventory.Instance.Config.GetConfigEntries())
#pragma warning restore CS0618
            {
                var synced = ConfigSync.ConfigData(entry);
                if (synced?.SynchronizedConfig == true)
                {
                    VAInventory.Instance.configSync.Broadcast(clientId, entry);
                }
            }
        }

        private void ApplyConfigChange(ConfigEntryBase entry)
        {
            // Broadcast the updated config to all clients
            var synced = ConfigSync.ConfigData(entry);
            if (synced?.SynchronizedConfig == true)
            {
                Broadcast(ZRoutedRpc.Everybody, entry);
            }
        }
    }



    public abstract class OwnConfigEntryBase(ConfigEntryBase config)
    {
        public readonly ConfigEntryBase BaseConfig = config;
        public object LocalBaseValue;
        public bool SynchronizedConfig = true;
    }

    public class SyncedConfigEntry<T>(ConfigEntry<T> config) : OwnConfigEntryBase(config)
    {
        public T Value
        {
            get => BaseConfig is ConfigEntry<T> configEntry ? configEntry.Value : throw new InvalidOperationException($"Cannot cast BaseConfig to ConfigEntry<{typeof(T).Name}>");
            set => ((ConfigEntry<T>)BaseConfig).Value = value;
        }

        public T DefaultValue => (T)BaseConfig.BoxedValue;
    }

#pragma warning disable 0067
    public class CustomSyncedValueBase(string identifier, Type type, int priority = 0)
    {
        public string Identifier { get; } = identifier;
        public Type Type { get; } = type;
        public object BoxedValue { get; set; }
        public object LocalBaseValue { get; set; }
        public int Priority { get; } = priority;

        public event Action ValueChanged;
    }
#pragma warning restore 0067

    public class ConfigurationManagerAttributes
    {
        public bool? ReadOnly;
    }

    public class ParsedConfigs
    {
        public readonly Dictionary<OwnConfigEntryBase, object> configValues = [];
        public readonly Dictionary<CustomSyncedValueBase, object> customValues = [];
    }

    public class PackageEntry
    {
        public string section;
        public string key;
        public Type type;
        public object value;
    }

    public class InvalidDeserializationTypeException : Exception
    {
        public string expected;
        public string received;
        public string field = "";
    }

    [HarmonyPatch(typeof(ZRpc), "HandlePackage")]
    public class SnatchCurrentlyHandlingRPC
    {
        public static ZRpc currentRpc;

        [HarmonyPrefix]
        private static void Prefix(ZRpc __instance) => currentRpc = __instance;
    }

    [HarmonyPatch(typeof(ZNet), "Awake")]
    public class RegisterRPCPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ZNet __instance)
        {
            ConfigSync.isServer = __instance.IsServer();
            if (!ConfigSync.isServer) ConfigSync.lockExempt = false;

            foreach (var configSync in ConfigSync.configSyncs)
            {
                ZRoutedRpc.instance.Register<ZPackage>(configSync.Name + " ConfigSync", configSync.RPC_FromOtherClientConfigSync);

                if (ConfigSync.isServer)
                {
                    Debug.Log($"[{VAInventory.PluginName}] Registered '{configSync.Name} ConfigSync' RPC");
                }
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    public class RegisterClientRPCPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer()) return;

            foreach (var configSync in ConfigSync.configSyncs)
                peer.m_rpc.Register<ZPackage>(configSync.Name + " ConfigSync", configSync.RPC_FromServerConfigSync);
        }
    }

    // Runs after vanilla accepted or rejected the login (only accepted peers have a uid) and after
    // other mods' postfixes, so their single-package config sends reach the socket before this one.
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    public class SendServerConfigsToJoiningPeer
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ZNet __instance, ZRpc rpc)
        {
            if (!__instance.IsServer()) return;

            ZNetPeer peer = __instance.GetPeers().Find(candidate => candidate.m_rpc == rpc);
            if (peer == null || !peer.IsReady()) return;

            bool peerIsAdmin = __instance.IsAdmin(rpc.GetSocket().GetHostName());
            foreach (var configSync in ConfigSync.configSyncs)
                configSync.SendAllValuesToJoiningPeer(peer, peerIsAdmin);
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnDestroy")]
    public class RestoreLocalConfigsOnDisconnect
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            if (ConfigSync.isServer) return;

            ConfigSync.ProcessingServerUpdate = true;
            try
            {
                foreach (var configSync in ConfigSync.configSyncs)
                    configSync.RestoreLocalConfigs();
            }
            finally
            {
                ConfigSync.ProcessingServerUpdate = false;
            }
        }
    }

    [HarmonyPatch(typeof(ConfigEntryBase), "GetSerializedValue")]
    public class PreventSavingServerInfo
    {
        [HarmonyPrefix]
        private static bool Prefix(ConfigEntryBase __instance, ref string __result)
        {
            var config = ConfigSync.ConfigData(__instance);
            if (config == null || ConfigSync.IsWritableConfig(config)) return true;

            __result = TomlTypeConverter.ConvertToString(config.LocalBaseValue, __instance.SettingType);
            return false;
        }
    }

    [HarmonyPatch(typeof(ConfigEntryBase), "SetSerializedValue")]
    public class PreventConfigRereadChangingValues
    {
        [HarmonyPrefix]
        private static bool Prefix(ConfigEntryBase __instance, string value)
        {
            var config = ConfigSync.ConfigData(__instance);
            if (config?.LocalBaseValue != null)
            {
                try
                {
                    config.LocalBaseValue = TomlTypeConverter.ConvertToValue(value, __instance.SettingType);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{VAInventory.PluginName}] Config value of setting \"{__instance.Definition}\" could not be parsed and will be ignored. Reason: {ex.Message}; Value: {value}");
                }

                return false;
            }

            return true;
        }
    }
}