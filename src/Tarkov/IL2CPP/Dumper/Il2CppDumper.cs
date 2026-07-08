/*
 * Lone EFT DMA Radar - Copyright (c) 2026 Lone DMA
 * Licensed under GNU AGPLv3. See https://www.gnu.org/licenses/agpl-3.0.html
 *
 * Adapted from the eft-dma-radar-silk project (HuiTeab, PolyForm Noncommercial 1.0.0).
 */
using System.Diagnostics.CodeAnalysis;
using LoneEftDmaRadar.DMA;
using SDK;
using VmmSharpEx;
using VmmSharpEx.Extensions;
using VmmSharpEx.Options;

namespace LoneEftDmaRadar.Tarkov.IL2CPP.Dumper
{
    /// <summary>
    /// Resolves IL2CPP offsets at runtime by reading the IL2CPP type table from
    /// GameAssembly.dll and writing the resulting field offsets back onto the
    /// <c>static uint</c> fields of <see cref="SDK.Offsets"/> via reflection.
    /// Persists results to <c>%AppData%\Lone-EFT-DMA\il2cpp_cache\</c>.
    /// </summary>
    public static partial class Il2CppDumper
    {
        // ── Il2CppClass layout offsets ───────────────────────────────────────────
        private const uint K_Name = 0x10;
        private const uint K_Parent = 0x58;
        private const uint K_Fields = 0x80;
        private const uint K_Methods = 0x98;
        private const uint K_MethodCount = 0x120;
        private const uint K_FieldCount = 0x124;

        private const int MaxClasses = 80_000;
        private const int MaxNameLen = 256;

        [StructLayout(LayoutKind.Sequential)]
        private struct ClassNamePtrs
        {
            public ulong NamePtr;      // Il2CppClass::name      @ +0x10
            public ulong NamespacePtr; // Il2CppClass::namespaze @ +0x18
        }

        [StructLayout(LayoutKind.Explicit, Size = 0x20)]
        private struct RawFieldInfo
        {
            [FieldOffset(0x00)] public ulong NamePtr;
            [FieldOffset(0x08)] public ulong TypePtr;
            [FieldOffset(0x18)] public int Offset; // signed
        }

        [StructLayout(LayoutKind.Explicit, Size = 0x20)]
        private struct RawMethodInfo
        {
            [FieldOffset(0x00)] public ulong MethodPointer;
            [FieldOffset(0x18)] public ulong NamePtr;
        }

        /// <summary>
        /// Run-once guard. Reset via <see cref="ResetForRedump"/> for the force-redump UI action.
        /// </summary>
        private static volatile bool _dumped;
        public static bool LastLiveDumpSucceeded { get; private set; }
        public static bool LastDumpUsedStaleCache { get; private set; }
        public static bool NeedsLiveRefresh => LastDumpUsedStaleCache || !LastLiveDumpSucceeded;

        /// <summary>
        /// Wipes the run-once guard and on-disk caches so the next <see cref="Dump"/>
        /// call performs a fresh live read of the type table.
        /// </summary>
        public static void ResetForRedump()
        {
            _dumped = false;
            DeleteCacheFiles();
        }

        /// <summary>
        /// Resolves IL2CPP offsets at runtime and applies them to <see cref="Offsets"/> via
        /// reflection. Hardcoded defaults in SDK.cs serve as fallback for any field that
        /// cannot be resolved. Runs only once per process lifetime.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Offsets nested types are statically reachable.")]
        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Offsets nested types are statically reachable.")]
        public static void Dump(bool force = false)
        {
            if (_dumped && !force)
            {
                return;
            }

            LastLiveDumpSucceeded = false;
            LastDumpUsedStaleCache = false;
            Logging.WriteLine("[Il2CppDumper] Dump starting...");

            var gaBase = Memory.GameAssemblyBase;
            if (gaBase == 0)
            {
                Logging.WriteLine("[Il2CppDumper] ERROR: GameAssemblyBase is 0 - game not ready.");
                return;
            }

            // Fast path: PE fingerprint match — restore offsets straight from cache.
            if (TryFastLoadCache(gaBase))
            {
                _dumped = true;
                LastLiveDumpSucceeded = true;
                return;
            }

            // Resolve TypeInfoTableRva via signature scan. Keep the startup retry
            // window short so a stale signature cannot block GOM fallback for minutes.
            const int maxRvaRetries = 3;
            bool rvaResolved = false;
            for (int rvaAttempt = 1; rvaAttempt <= maxRvaRetries; rvaAttempt++)
            {
                if (ResolveTypeInfoTableRva(gaBase, quiet: rvaAttempt < maxRvaRetries))
                {
                    rvaResolved = true;
                    break;
                }

                if (rvaAttempt < maxRvaRetries)
                {
                    const int delay = 500;
                    Logging.WriteLine($"[Il2CppDumper] TypeInfoTable not ready, retrying in {delay}ms... ({rvaAttempt}/{maxRvaRetries})");
                    Thread.Sleep(delay);
                }
            }

            if (!rvaResolved)
            {
                Logging.WriteLine("[Il2CppDumper] TypeInfoTable resolution failed after all retries.");
                if (TryLoadCacheStale())
                {
                    _dumped = true;
                    LastDumpUsedStaleCache = true;
                    Logging.WriteLine("[Il2CppDumper] Using last cached offsets as fallback.");
                    return;
                }
                Logging.WriteLine("[Il2CppDumper] No cache available - falling back to compiled-in offsets.");
                return;
            }

            // RVA-keyed cache: if the cache was written against the same TypeInfoTableRva,
            // we can skip the live read entirely.
            if (TryLoadCache(Offsets.Special.TypeInfoTableRva))
            {
                _dumped = true;
                Logging.WriteLine("[Il2CppDumper] Offsets restored from cache - live dump skipped.");
                LastLiveDumpSucceeded = true;
                SaveCache(); // refresh PE fingerprint for next fast-path
                return;
            }

            // Resolve the type-info table pointer.
            ulong tablePtr = 0;
            bool tableOk = false;
            try
            {
                tablePtr = Memory.ReadPtr(gaBase + Offsets.Special.TypeInfoTableRva, false);
                tableOk = tablePtr.IsValidUserVA();
                if (!tableOk)
                    Logging.WriteLine("[Il2CppDumper] TypeInfoTable pointer is invalid.");
            }
            catch (Exception ex)
            {
                Logging.WriteLine($"[Il2CppDumper] ReadPtr(TypeInfoTableRva) failed: {ex.Message}");
            }

            if (!tableOk)
            {
                if (TryLoadCacheStale())
                {
                    _dumped = true;
                    LastDumpUsedStaleCache = true;
                    Logging.WriteLine("[Il2CppDumper] Using last cached offsets as fallback.");
                    return;
                }
                Logging.WriteLine("[Il2CppDumper] No cache available - falling back to compiled-in offsets.");
                return;
            }

            // Live-read the type table with retries (transient DMA failures during loading).
            const int MinExpectedClasses = 1_000;
            const int maxRetries = 3;
            List<(string Name, string Namespace, ulong KlassPtr, int Index)> classes = [];

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                classes = ReadAllClassesFromTable(tablePtr);
                if (classes.Count >= MinExpectedClasses) break;

                if (attempt < maxRetries)
                {
                    Logging.WriteLine($"[Il2CppDumper] Only {classes.Count} classes found (expected >= {MinExpectedClasses}), retrying... ({attempt}/{maxRetries})");
                    Thread.Sleep(1000);
                }
            }

            if (classes.Count < MinExpectedClasses)
            {
                Logging.WriteLine($"[Il2CppDumper] Live dump failed: only {classes.Count} classes found (expected >= {MinExpectedClasses}) after {maxRetries} attempts.");
                if (TryLoadCacheStale())
                {
                    _dumped = true;
                    LastDumpUsedStaleCache = true;
                    Logging.WriteLine("[Il2CppDumper] Using last cached offsets as fallback.");
                    return;
                }
                Logging.WriteLine("[Il2CppDumper] No cache available - falling back to compiled-in offsets.");
                return;
            }

            // Build lookup tables — index classes by raw name, sanitized name, and dedup suffix
            // (silk's "World" / "World_2" / "World_3" convention for repeated names).
            var nameLookup = new Dictionary<string, ulong>(classes.Count * 2, StringComparer.Ordinal);
            var nameToIndex = new Dictionary<string, int>(classes.Count * 2, StringComparer.Ordinal);
            var baseNameSeen = new Dictionary<string, int>(classes.Count, StringComparer.Ordinal);

            foreach (var (name, _, ptr, idx) in classes)
            {
                var san = SanitizeName(name);
                nameLookup.TryAdd(name, ptr);
                nameToIndex.TryAdd(name, idx);
                if (san != name)
                {
                    nameLookup.TryAdd(san, ptr);
                    nameToIndex.TryAdd(san, idx);
                }
                if (baseNameSeen.TryGetValue(san, out int seen))
                {
                    int next = seen + 1;
                    baseNameSeen[san] = next;
                    var dedupKey = $"{san}_{next}";
                    nameLookup.TryAdd(dedupKey, ptr);
                    nameToIndex.TryAdd(dedupKey, idx);
                }
                else
                {
                    baseNameSeen[san] = 1;
                }
            }

            // Resolve TypeIndex values for singletons before building the schema —
            // the schema reads Offsets.Special.*_TypeIndex at build time.
            ResolveTypeIndices(nameToIndex, classes);

            var schema = BuildSchema();

            var offsetsType = typeof(Offsets);
            const BindingFlags bf = BindingFlags.Public | BindingFlags.Static;

            int updated = 0, fallback = 0, classesSkipped = 0;

            foreach (var sc in schema)
            {
                ulong klassPtr;
                if (sc.TypeIndex.HasValue)
                {
                    klassPtr = ReadPtr(tablePtr + (ulong)sc.TypeIndex.Value * 8UL);
                    if (!klassPtr.IsValidUserVA())
                    {
                        Logging.WriteLine($"[Il2CppDumper] SKIP '{sc.CsName}': TypeIndex={sc.TypeIndex.Value} resolved to invalid pointer.");
                        classesSkipped++;
                        continue;
                    }
                }
                else if (sc.ResolveViaChild is not null)
                {
                    if (!nameLookup.TryGetValue(sc.ResolveViaChild, out var childKlass))
                    {
                        Logging.WriteLine($"[Il2CppDumper] SKIP '{sc.CsName}': child class '{sc.ResolveViaChild}' not found in type table.");
                        classesSkipped++;
                        continue;
                    }

                    klassPtr = 0;
                    ulong walkPtr = childKlass;
                    const int MaxParentDepth = 16;
                    for (int depth = 0; depth < MaxParentDepth && walkPtr.IsValidUserVA(); depth++)
                    {
                        ulong parentPtr = ReadPtr(walkPtr + K_Parent);
                        if (!parentPtr.IsValidUserVA()) break;

                        ulong parentNamePtr = ReadPtr(parentPtr + K_Name);
                        string? parentName = ReadStr(parentNamePtr);

                        if (parentName != null && parentName == sc.Il2CppName)
                        {
                            klassPtr = parentPtr;
                            break;
                        }

                        walkPtr = parentPtr;
                    }

                    if (klassPtr == 0)
                    {
                        Logging.WriteLine($"[Il2CppDumper] SKIP '{sc.CsName}': parent '{sc.Il2CppName}' not found in parent chain of '{sc.ResolveViaChild}'.");
                        classesSkipped++;
                        continue;
                    }
                }
                else
                {
                    if (!nameLookup.TryGetValue(sc.Il2CppName, out klassPtr))
                    {
                        Logging.WriteLine($"[Il2CppDumper] SKIP '{sc.Il2CppName}': not found in type table.");
                        classesSkipped++;
                        continue;
                    }
                }

                var nestedType = offsetsType.GetNestedType(sc.CsName, BindingFlags.Public | BindingFlags.NonPublic);
                if (nestedType is null)
                {
                    Logging.WriteLine($"[Il2CppDumper] WARN: struct Offsets.{sc.CsName} not found via reflection - skipping.");
                    classesSkipped++;
                    continue;
                }

                var fieldMap = ReadClassFields(klassPtr);
                var methodMap = sc.Fields.Any(sf => sf.Kind == FieldKind.MethodRva)
                    ? ReadClassMethods(klassPtr, gaBase)
                    : null;

                foreach (var sf in sc.Fields)
                {
                    if (sf.Kind == FieldKind.MethodRva)
                    {
                        var methodName = sf.Il2CppName.EndsWith("_RVA", StringComparison.Ordinal)
                            ? sf.Il2CppName[..^4]
                            : sf.Il2CppName;

                        if (methodMap is not null && methodMap.TryGetValue(methodName, out var rva))
                        {
                            if (TrySetField(nestedType, sf.CsName, rva, bf)) updated++;
                            else fallback++;
                        }
                        else
                        {
                            Logging.WriteLine($"[Il2CppDumper] WARN: method '{methodName}' not found in '{sc.CsName}' - using fallback.");
                            fallback++;
                        }
                    }
                    else
                    {
                        if (!fieldMap.TryGetValue(sf.Il2CppName, out var offset))
                        {
                            var alt = FlipBackingFieldConvention(sf.Il2CppName);
                            if (alt is null || !fieldMap.TryGetValue(alt, out offset))
                            {
                                Logging.WriteLine($"[Il2CppDumper] WARN: field '{sf.Il2CppName}' not found in '{sc.CsName}' - using fallback.");
                                fallback++;
                                continue;
                            }
                        }

                        object value = offset >= 0 ? (object)(uint)offset : (object)offset;
                        if (TrySetField(nestedType, sf.CsName, value, bf)) updated++;
                        else fallback++;
                    }
                }
            }

            DebugDumpResolverState(classes.Count, updated, fallback, classesSkipped);
            Logging.WriteLine($"[Il2CppDumper] Done. {updated} offsets updated, {fallback} fallback, {classesSkipped} skipped.");

            _dumped = true;
            LastLiveDumpSucceeded = true;
            SaveCache();
        }

        // ── Reflection helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Sets a static field on a type via reflection, with type conversion for
        /// uint/ulong/int/uint[] (first element for deref chains). Silently skips
        /// const (IsLiteral) fields.
        /// </summary>
        private static bool TrySetField(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)] Type type,
            string fieldName, object value, BindingFlags bf)
        {
            var fi = type.GetField(fieldName, bf);
            if (fi is null)
            {
                Logging.WriteLine($"[Il2CppDumper] WARN: field '{fieldName}' not found on '{type.Name}' via reflection.");
                return false;
            }

            if (fi.IsLiteral) return true; // const — cannot set at runtime, skip silently

            try
            {
                var target = fi.FieldType;
                object converted;

                if (target == typeof(uint))
                    converted = Convert.ToUInt32(value);
                else if (target == typeof(ulong))
                    converted = Convert.ToUInt64(value);
                else if (target == typeof(int))
                    converted = Convert.ToInt32(value);
                else if (target == typeof(uint[]))
                {
                    var arr = (uint[]?)fi.GetValue(null);
                    if (arr is not null && arr.Length > 0)
                    {
                        arr[0] = Convert.ToUInt32(value);
                        return true;
                    }
                    return false;
                }
                else
                {
                    Logging.WriteLine($"[Il2CppDumper] WARN: unsupported field type '{target}' for '{type.Name}.{fieldName}'.");
                    return false;
                }

                fi.SetValue(null, converted);
                return true;
            }
            catch (Exception ex)
            {
                Logging.WriteLine($"[Il2CppDumper] ERROR: Failed to set '{type.Name}.{fieldName}': {ex.Message}");
                return false;
            }
        }

        // ── Memory helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Reads all IL2CppClass entries from the type-info table using two scatter
        /// rounds: round 1 reads the (NamePtr, NamespacePtr) pair from each valid class,
        /// round 2 reads the actual name + namespace strings.
        /// </summary>
        private static List<(string Name, string Namespace, ulong KlassPtr, int Index)> ReadAllClassesFromTable(ulong tablePtr)
        {
            var result = new List<(string, string, ulong, int)>(4096);

            // Read the pointer array in chunks so we degrade gracefully on partially-mapped memory.
            const int chunkSize = 4096;
            var allPtrs = new List<ulong>(MaxClasses);

            for (int offset = 0; offset < MaxClasses; offset += chunkSize)
            {
                int toRead = Math.Min(chunkSize, MaxClasses - offset);
                ulong[] chunk;
                try
                {
                    using var pooled = Memory.ReadPooled<ulong>(tablePtr + (ulong)offset * 8, toRead, false);
                    chunk = pooled.Memory.Span.ToArray();
                }
                catch (Exception ex)
                {
                    if (allPtrs.Count == 0)
                        Logging.WriteLine($"[Il2CppDumper] ReadPooled failed: {ex.Message}");
                    break;
                }

                bool hasValid = false;
                for (int i = 0; i < chunk.Length; i++)
                {
                    if (chunk[i].IsValidUserVA()) { hasValid = true; break; }
                }

                allPtrs.AddRange(chunk);
                if (!hasValid) break;
            }

            if (allPtrs.Count == 0) return result;

            var validIndices = new List<int>(allPtrs.Count / 2);
            for (int i = 0; i < allPtrs.Count; i++)
                if (allPtrs[i].IsValidUserVA()) validIndices.Add(i);

            if (validIndices.Count == 0) return result;

            // Round 1: read ClassNamePtrs (16 bytes) for every valid class.
            var namePairs = new ClassNamePtrs[validIndices.Count];
            using (var sc1 = Memory.CreateScatter(VmmFlags.NOCACHE))
            {
                for (int j = 0; j < validIndices.Count; j++)
                    sc1.PrepareReadValue<ClassNamePtrs>(allPtrs[validIndices[j]] + K_Name);
                sc1.Execute();

                for (int j = 0; j < validIndices.Count; j++)
                {
                    sc1.ReadValue(allPtrs[validIndices[j]] + K_Name, out namePairs[j]);
                }
            }

            // Round 2: read name + namespace strings.
            var names = new string?[validIndices.Count];
            var nses = new string?[validIndices.Count];
            using (var sc2 = Memory.CreateScatter(VmmFlags.NOCACHE))
            {
                for (int j = 0; j < validIndices.Count; j++)
                {
                    if (namePairs[j].NamePtr.IsValidUserVA())
                        sc2.PrepareRead(namePairs[j].NamePtr, (uint)MaxNameLen);
                    if (namePairs[j].NamespacePtr.IsValidUserVA())
                        sc2.PrepareRead(namePairs[j].NamespacePtr, (uint)MaxNameLen);
                }
                sc2.Execute();

                for (int j = 0; j < validIndices.Count; j++)
                {
                    if (namePairs[j].NamePtr.IsValidUserVA())
                        names[j] = sc2.ReadString(namePairs[j].NamePtr, MaxNameLen, Encoding.ASCII);
                    if (namePairs[j].NamespacePtr.IsValidUserVA())
                        nses[j] = sc2.ReadString(namePairs[j].NamespacePtr, MaxNameLen, Encoding.ASCII);
                }
            }

            for (int j = 0; j < validIndices.Count; j++)
            {
                int i = validIndices[j];
                if (string.IsNullOrEmpty(names[j])) continue;
                result.Add((names[j]!, nses[j] ?? string.Empty, allPtrs[i], i));
            }

            return result;
        }

        private static Dictionary<string, int> ReadClassFields(ulong klassPtr)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            var fieldCount = Memory.ReadValue<ushort>(klassPtr + K_FieldCount, false);
            if (fieldCount == 0 || fieldCount > 4096) return result;

            var fieldsBase = ReadPtr(klassPtr + K_Fields);
            if (!fieldsBase.IsValidUserVA()) return result;

            RawFieldInfo[] rawFields;
            try
            {
                using var pooled = Memory.ReadPooled<RawFieldInfo>(fieldsBase, fieldCount, false);
                rawFields = pooled.Memory.Span.ToArray();
            }
            catch { return result; }

            // Scatter-read all field name strings in one batch.
            var names = new string?[rawFields.Length];
            using (var sc = Memory.CreateScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < rawFields.Length; i++)
                {
                    if (rawFields[i].NamePtr.IsValidUserVA())
                        sc.PrepareRead(rawFields[i].NamePtr, (uint)MaxNameLen);
                }
                sc.Execute();

                for (int i = 0; i < rawFields.Length; i++)
                {
                    if (rawFields[i].NamePtr.IsValidUserVA())
                        names[i] = sc.ReadString(rawFields[i].NamePtr, MaxNameLen, Encoding.ASCII);
                }
            }

            for (int i = 0; i < rawFields.Length; i++)
            {
                if (string.IsNullOrEmpty(names[i])) continue;
                result.TryAdd(names[i]!, rawFields[i].Offset);
            }

            return result;
        }

        private static Dictionary<string, ulong> ReadClassMethods(ulong klassPtr, ulong gaBase)
        {
            var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var methodCount = Memory.ReadValue<ushort>(klassPtr + K_MethodCount, false);
            if (methodCount == 0 || methodCount > 4096) return result;

            var methodsBase = ReadPtr(klassPtr + K_Methods);
            if (!methodsBase.IsValidUserVA()) return result;

            ulong[] methodPtrs;
            try
            {
                using var pooled = Memory.ReadPooled<ulong>(methodsBase, methodCount, false);
                methodPtrs = pooled.Memory.Span.ToArray();
            }
            catch { return result; }

            // Round 1: read RawMethodInfo (MethodPointer + NamePtr) for each method.
            var infos = new RawMethodInfo[methodPtrs.Length];
            using (var sc1 = Memory.CreateScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < methodPtrs.Length; i++)
                {
                    if (methodPtrs[i].IsValidUserVA())
                        sc1.PrepareReadValue<RawMethodInfo>(methodPtrs[i]);
                }
                sc1.Execute();

                for (int i = 0; i < methodPtrs.Length; i++)
                {
                    if (methodPtrs[i].IsValidUserVA())
                        sc1.ReadValue(methodPtrs[i], out infos[i]);
                }
            }

            // Round 2: read method name strings.
            var names = new string?[methodPtrs.Length];
            using (var sc2 = Memory.CreateScatter(VmmFlags.NOCACHE))
            {
                for (int i = 0; i < methodPtrs.Length; i++)
                {
                    if (!infos[i].MethodPointer.IsValidUserVA() || infos[i].MethodPointer < gaBase) continue;
                    if (!infos[i].NamePtr.IsValidUserVA()) continue;
                    sc2.PrepareRead(infos[i].NamePtr, (uint)MaxNameLen);
                }
                sc2.Execute();

                for (int i = 0; i < methodPtrs.Length; i++)
                {
                    if (!infos[i].MethodPointer.IsValidUserVA() || infos[i].MethodPointer < gaBase) continue;
                    if (!infos[i].NamePtr.IsValidUserVA()) continue;
                    names[i] = sc2.ReadString(infos[i].NamePtr, MaxNameLen, Encoding.ASCII);
                }
            }

            for (int i = 0; i < methodPtrs.Length; i++)
            {
                if (string.IsNullOrEmpty(names[i])) continue;
                var rva = infos[i].MethodPointer - gaBase;
                result.TryAdd(names[i]!, rva);
            }

            return result;
        }

        // ── String / pointer helpers ─────────────────────────────────────────────

        /// <summary>
        /// Toggles between IL2CPP's two backing-field naming conventions:
        ///   "&lt;Name&gt;k__BackingField" ↔ "_Name_k__BackingField"
        /// Returns null if the input is not a backing field name.
        /// </summary>
        private static string? FlipBackingFieldConvention(string name)
        {
            const string suffix = "k__BackingField";
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) return null;

            if (name.Length > suffix.Length + 2 && name[0] == '<')
            {
                var inner = name[1..name.IndexOf('>')];
                return $"_{inner}_{suffix}";
            }

            if (name.Length > suffix.Length + 2 && name[0] == '_')
            {
                var inner = name[1..^suffix.Length];
                if (inner.EndsWith('_')) inner = inner[..^1];
                return $"<{inner}>{suffix}";
            }

            return null;
        }

        private static ulong ReadPtr(ulong addr)
        {
            if (!addr.IsValidUserVA()) return 0;
            try { return Memory.ReadValue<ulong>(addr, false); }
            catch { return 0; }
        }

        private static string? ReadStr(ulong addr)
        {
            if (!addr.IsValidUserVA()) return null;
            try { return Memory.ReadAsciiString(addr, MaxNameLen, false); }
            catch { return null; }
        }

        /// <summary>Replaces non-alphanumeric/non-underscore characters with '_'.</summary>
        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new char[name.Length];
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                sb[i] = char.IsLetterOrDigit(c) || c == '_' ? c : '_';
            }
            return new string(sb);
        }
    }
}
