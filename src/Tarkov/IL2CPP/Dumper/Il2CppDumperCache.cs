/*
 * Lone EFT DMA Radar - Copyright (c) 2026 Lone DMA
 * Licensed under GNU AGPLv3. See https://www.gnu.org/licenses/agpl-3.0.html
 *
 * Adapted from the eft-dma-radar-silk project (HuiTeab, PolyForm Noncommercial 1.0.0).
 */
using System.Diagnostics.CodeAnalysis;
using LoneEftDmaRadar.DMA;
using SDK;

namespace LoneEftDmaRadar.Tarkov.IL2CPP.Dumper
{
    public static partial class Il2CppDumper
    {
        // ── Cache file paths ─────────────────────────────────────────────────────
        //   _pe.json  — keyed by GameAssembly.dll PE TimeDateStamp + SizeOfImage. Fast path.
        //   _rva.json — keyed by resolved TypeInfoTableRva. Used as stale-fallback when PE shifts.

        private static readonly string CacheDir =
            Path.Combine(Program.ConfigPath.FullName, "il2cpp_cache");

        private static readonly string CacheFilePathPe =
            Path.Combine(CacheDir, "il2cpp_offsets_pe.json");

        private static readonly string CacheFilePathRva =
            Path.Combine(CacheDir, "il2cpp_offsets_rva.json");

        /// <summary>
        /// Bump this whenever the schema's <em>meaning</em> changes in a way that
        /// makes previously-cached values wrong (e.g. switching which IL2CPP field
        /// a C# offset is sourced from). Older caches are discarded automatically.
        /// </summary>
        private const int CacheSchemaVersion = 2;

        /// <summary>
        /// Radar assembly ModuleVersionId — changes on every rebuild. Used to invalidate
        /// caches automatically when a developer hardcodes a new offset in <see cref="Offsets"/>
        /// without the game (PE fingerprint) having changed.
        /// </summary>
        private static readonly Guid RadarAssemblyMvid =
            typeof(Il2CppDumper).Assembly.ManifestModule.ModuleVersionId;

        public sealed class OffsetCache
        {
            public int SchemaVersion { get; set; }
            public Guid RadarAssemblyMvid { get; set; }
            public ulong TypeInfoTableRva { get; set; }
            public uint GameAssemblyTimestamp { get; set; }
            public uint GameAssemblySizeOfImage { get; set; }
            public Dictionary<string, string> Fields { get; set; } = new();
        }

        [JsonSourceGenerationOptions(WriteIndented = true)]
        [JsonSerializable(typeof(OffsetCache))]
        internal partial class CacheJsonContext : JsonSerializerContext { }

        /// <summary>
        /// Serializes all resolved static offset fields from <see cref="Offsets"/> to both
        /// cache files (PE-keyed and RVA-keyed). Called once after a successful live dump.
        /// </summary>
        internal static void SaveCache()
        {
            try
            {
                var (timestamp, sizeOfImage) = Memory.ReadPeFingerprint(Memory.GameAssemblyBase);
                var cache = new OffsetCache
                {
                    SchemaVersion = CacheSchemaVersion,
                    RadarAssemblyMvid = RadarAssemblyMvid,
                    TypeInfoTableRva = Offsets.Special.TypeInfoTableRva,
                    GameAssemblyTimestamp = timestamp,
                    GameAssemblySizeOfImage = sizeOfImage,
                    Fields = CollectAllFields(),
                };

                var json = JsonSerializer.Serialize(cache, CacheJsonContext.Default.OffsetCache);
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(CacheFilePathPe, json);
                File.WriteAllText(CacheFilePathRva, json);
                Logging.WriteLine($"[Il2CppDumper] Cache saved -> {CacheDir} (PE + RVA)");
            }
            catch (Exception ex)
            {
                Logging.WriteLine($"[Il2CppDumper] Cache save FAILED: {ex.Message}");
            }
        }

        private static OffsetCache? TryReadAnyCache(out string sourcePath)
        {
            sourcePath = "";
            foreach (var path in new[] { CacheFilePathPe, CacheFilePathRva })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var json = File.ReadAllText(path);
                    var cache = JsonSerializer.Deserialize(json, CacheJsonContext.Default.OffsetCache);
                    if (cache is not null && cache.Fields.Count > 0 && cache.SchemaVersion >= CacheSchemaVersion)
                    {
                        sourcePath = path;
                        return cache;
                    }
                }
                catch { /* try next */ }
            }
            return null;
        }

        /// <summary>
        /// Last-resort cache loader used when a live dump fails. Ignores PE / RVA fingerprints
        /// (the game binary may have changed) and applies whatever offsets were last persisted.
        /// </summary>
        internal static bool TryLoadCacheStale()
        {
            try
            {
                var cache = TryReadAnyCache(out var src);
                if (cache is null) return false;

                if (cache.TypeInfoTableRva != 0)
                    Offsets.Special.TypeInfoTableRva = cache.TypeInfoTableRva;

                int applied = ApplyCachedFields(cache.Fields);
                Logging.WriteLine($"[Il2CppDumper] Stale cache applied from {Path.GetFileName(src)} - {applied}/{cache.Fields.Count} fields restored.");
                return applied > 0;
            }
            catch (Exception ex)
            {
                Logging.WriteLine($"[Il2CppDumper] Stale cache load FAILED: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// RVA-keyed cache loader. Used after a successful sig-scan resolves the
        /// current TypeInfoTableRva — if the cache was written against the same RVA
        /// we can skip the live dump entirely.
        /// </summary>
        internal static bool TryLoadCache(ulong expectedRva)
        {
            try
            {
                var cache = TryReadAnyCache(out var src);
                if (cache is null)
                {
                    Logging.WriteLine("[Il2CppDumper] No cache file found - will perform live dump.");
                    return false;
                }

                if (cache.TypeInfoTableRva != expectedRva)
                {
                    Logging.WriteLine(
                        $"[Il2CppDumper] Cache RVA mismatch: cached=0x{cache.TypeInfoTableRva:X} " +
                        $"current=0x{expectedRva:X} - performing live dump.");
                    return false;
                }

                int applied = ApplyCachedFields(cache.Fields);
                Logging.WriteLine($"[Il2CppDumper] Cache loaded from {Path.GetFileName(src)} (RVA match) - {applied}/{cache.Fields.Count} fields applied.");
                return applied > 0;
            }
            catch (Exception ex)
            {
                Logging.WriteLine($"[Il2CppDumper] Cache load FAILED: {ex.Message} - will perform live dump.");
                return false;
            }
        }

        /// <summary>
        /// Fast-path cache loader using the GameAssembly.dll PE header fingerprint
        /// (TimeDateStamp + SizeOfImage). Restores all offsets in &lt;1 ms when the
        /// game binary has not changed.
        /// </summary>
        internal static bool TryFastLoadCache(ulong gaBase)
        {
            try
            {
                var (timestamp, sizeOfImage) = Memory.ReadPeFingerprint(gaBase);
                if (timestamp == 0 || sizeOfImage == 0) return false;

                var cache = TryReadAnyCache(out var src);
                if (cache is null) return false;

                if (cache.GameAssemblyTimestamp != timestamp || cache.GameAssemblySizeOfImage != sizeOfImage)
                {
                    Logging.WriteLine("[Il2CppDumper] PE fingerprint mismatch (game updated?) - will perform fresh dump.");
                    return false;
                }

                if (cache.TypeInfoTableRva != 0)
                    Offsets.Special.TypeInfoTableRva = cache.TypeInfoTableRva;

                int applied = ApplyCachedFields(cache.Fields);
                Logging.WriteLine($"[Il2CppDumper] Fast cache loaded from {Path.GetFileName(src)} (PE match) - {applied}/{cache.Fields.Count} fields applied.");
                return applied > 0;
            }
            catch (Exception ex)
            {
                Logging.WriteLine($"[Il2CppDumper] Fast cache load failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Wipe both cache files. Called by the force-redump UI action.
        /// </summary>
        internal static void DeleteCacheFiles()
        {
            try
            {
                if (File.Exists(CacheFilePathPe)) File.Delete(CacheFilePathPe);
                if (File.Exists(CacheFilePathRva)) File.Delete(CacheFilePathRva);
                Logging.WriteLine("[Il2CppDumper] Cache files deleted.");
            }
            catch (Exception ex)
            {
                Logging.WriteLine($"[Il2CppDumper] Cache delete failed: {ex.Message}");
            }
        }

        // ── Reflection over Offsets ──────────────────────────────────────────────

        private const BindingFlags _bf = BindingFlags.Public | BindingFlags.Static;

        // The Offsets struct and all its nested types/fields are statically reachable from
        // the radar's hot path (e.g. Offsets.Player.MovementContext is read by GameWorld code).
        // The trimmer preserves them; suppress the IL warnings about runtime reflection.
        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Offsets fields are statically reachable from the radar's hot path.")]
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Offsets fields are statically reachable from the radar's hot path.")]
        private static Dictionary<string, string> CollectAllFields()
        {
            var result = new Dictionary<string, string>(256);
            var offsetsType = typeof(Offsets);

            foreach (var nested in offsetsType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (var fi in nested.GetFields(_bf))
                {
                    if (fi.IsLiteral) continue; // skip const fields

                    var raw = fi.GetValue(null);
                    if (raw is null) continue;

                    string? value = raw switch
                    {
                        uint[] arr => arr.Length > 0 ? arr[0].ToString() : null,
                        uint u => u.ToString(),
                        ulong ul => ul.ToString(),
                        int i => i.ToString(),
                        _ => null,
                    };

                    if (value is not null)
                        result[$"{nested.Name}.{fi.Name}"] = value;
                }
            }

            return result;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Offsets fields are statically reachable from the radar's hot path.")]
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Offsets fields are statically reachable from the radar's hot path.")]
        private static int ApplyCachedFields(Dictionary<string, string> fields)
        {
            var offsetsType = typeof(Offsets);
            int applied = 0;

            foreach (var (key, rawValue) in fields)
            {
                var dot = key.IndexOf('.');
                if (dot < 0) continue;

                var structName = key[..dot];
                var fieldName = key[(dot + 1)..];

                var nested = offsetsType.GetNestedType(structName, BindingFlags.Public | BindingFlags.NonPublic);
                if (nested is null) continue;

                var fi = nested.GetField(fieldName, _bf);
                if (fi is null || fi.IsLiteral) continue;

                try
                {
                    var target = fi.FieldType;
                    if (target == typeof(uint))
                    {
                        if (uint.TryParse(rawValue, out var v)) { fi.SetValue(null, v); applied++; }
                    }
                    else if (target == typeof(ulong))
                    {
                        if (ulong.TryParse(rawValue, out var v)) { fi.SetValue(null, v); applied++; }
                    }
                    else if (target == typeof(int))
                    {
                        if (int.TryParse(rawValue, out var v)) { fi.SetValue(null, v); applied++; }
                    }
                    else if (target == typeof(uint[]))
                    {
                        if (uint.TryParse(rawValue, out var v))
                        {
                            var arr = (uint[]?)fi.GetValue(null);
                            if (arr is { Length: > 0 }) { arr[0] = v; applied++; }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.WriteLine($"[Il2CppDumper] Cache: failed to apply {key}: {ex.Message}");
                }
            }

            return applied;
        }
    }
}
