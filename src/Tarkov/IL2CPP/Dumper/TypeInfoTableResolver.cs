/*
 * Lone EFT DMA Radar - Copyright (c) 2026 Lone DMA
 * Licensed under GNU AGPLv3. See https://www.gnu.org/licenses/agpl-3.0.html
 *
 * Adapted from the eft-dma-radar-silk project (HuiTeab, PolyForm Noncommercial 1.0.0).
 */
using LoneEftDmaRadar.DMA;
using SDK;
using VmmSharpEx.Extensions;

namespace LoneEftDmaRadar.Tarkov.IL2CPP.Dumper
{
    public static partial class Il2CppDumper
    {
        private const int MaxTableEntries = 64;
        private const int EarlyProbeCount = 16;
        private const int EarlyProbeRequired = 8;
        private const int MidProbeOffset = 5_000;
        private const int MidProbeCount = 8;
        private const int MidProbeRequired = 3;
        private const int MinTypeCount = 1_000;
        private const string GameAssemblyName = "GameAssembly.dll";
        private const string LogTag = "[Il2CppDumper]";

        /// <summary>
        /// Candidate signatures for finding the TypeInfoTable pointer in GameAssembly.dll.
        /// (Sig, RelOffset, InstrLen) — RelOffset/InstrLen are the RIP-relative decode parameters.
        /// </summary>
        private static readonly (string Sig, int RelOffset, int InstrLen, string Desc)[] TypeInfoTableSigs =
        [
            ("48 C1 E9 04 BA 08 00 00 00 ? ? ? 48 89 05 ? ? ? ? 48 8B 05 ? ? ? ?", 15, 19, "init: shr rcx,4; mov edx,8; call; mov [rip+rel32],rax"),
            ("48 8B 05 ? ? ? ? 4C 8D 34 F0 49 8B 3E", 3, 7, "read: mov rax,[rip+rel32]; lea r14,[rax+rsi*8]"),
            ("48 8B 05 ? ? ? ? ? ? ? ? ? ? ? 90 48 85 DB 75 ? 48 8D 2D ? ? ? ? 48 89 6C 24 ? 48 8B CD E8 ? ? ? ? 90 ? ? ? 48 85 DB 75 ? 8B CF", 3, 7, "read: mov rax,[rip+rel32] (table lookup)"),
            ("48 89 05 ? ? ? ? 48 8B 05 ? ? ? ? 8B 48", 3, 7, "write: mov [rip+rel32],rax (init store)"),
            ("48 89 05 ? ? ? ? 48 8B 05 ? ? ? ? 8B 48", 10, 14, "read: second mov rax,[rip+rel32] after init store"),
            ("48 89 05 ? ? ? ? 48 8B 05", 3, 7, "write: mov [rip+rel32],rax; mov rax,[rip+rel32] (minimal)"),
            ("48 89 05 ? ? ? ? 48 8B 05", 10, 14, "read: second mov rax,[rip+rel32] (minimal)"),
        ];

        private static readonly (string Il2CppName, string FieldName)[] TypeIndexMap =
        [
            ("EFTHardSettings",       nameof(Offsets.Special.EFTHardSettings_TypeIndex)),
            ("WeatherController",     nameof(Offsets.Special.WeatherController_TypeIndex)),
            ("MatchingProgress",      nameof(Offsets.Special.MatchingProgress_TypeIndex)),
            ("GamePlayerOwner",       nameof(Offsets.Special.GamePlayerOwner_TypeIndex)),
            ("TarkovApplication",     nameof(Offsets.Special.TarkovApplication_TypeIndex)),
            ("BtrController",         nameof(Offsets.Special.BtrController_TypeIndex)),
        ];

        private static readonly FieldInfo[] CachedTypeIndexFields =
            typeof(Offsets.Special).GetFields(BindingFlags.Public | BindingFlags.Static);

        private record struct SigScanResult(int Index, string Desc, string State, int Matches, int ValidMatches, ulong Rva, string Detail = "");

        private static SigScanResult[] _lastSigResults = [];
        private static string _lastResolutionMode = "not run";

        private static bool ResolveTypeInfoTableRva(ulong gaBase, bool quiet = false)
        {
            var testedRvas = new HashSet<ulong>();
            var sigResults = new List<SigScanResult>(TypeInfoTableSigs.Length);
            (ulong rva, ulong sigAddr, string sig)? first = null;

            for (int i = 0; i < TypeInfoTableSigs.Length; i++)
            {
                var (sig, relOff, instrLen, desc) = TypeInfoTableSigs[i];
                var (result, scanResult) = TryResolveFromSignature(i, sig, relOff, instrLen, desc, gaBase, testedRvas);
                sigResults.Add(scanResult);
                if (result.HasValue && first is null)
                    first = result;
            }

            bool success;
            if (first.HasValue)
            {
                var prev = Offsets.Special.TypeInfoTableRva;
                Offsets.Special.TypeInfoTableRva = first.Value.rva;
                Logging.WriteLine($"{LogTag} TypeInfoTable resolved: rva=0x{first.Value.rva:X}, unique={testedRvas.Count}");
                if (prev != first.Value.rva)
                    Logging.WriteLine($"{LogTag} TypeInfoTableRva UPDATED: 0x{prev:X} -> 0x{first.Value.rva:X}");
                _lastResolutionMode = "signature";
                success = true;
            }
            else if (Offsets.Special.TypeInfoTableRva != 0 && ValidateTypeInfoTable(gaBase, Offsets.Special.TypeInfoTableRva))
            {
                Logging.WriteLine($"{LogTag} TypeInfoTable using fallback RVA: 0x{Offsets.Special.TypeInfoTableRva:X}");
                _lastResolutionMode = "fallback (hardcoded)";
                success = true;
            }
            else
            {
                if (!quiet)
                    Logging.WriteLine($"{LogTag} WARNING: All TypeInfoTable resolution strategies failed - offsets may be stale!");
                _lastResolutionMode = "FAILED";
                success = false;
            }

            _lastSigResults = [.. sigResults];
            if (!success && !quiet)
                DebugDumpResolverState(0, 0, 0, 0);
            return success;
        }

        private static ((ulong rva, ulong sigAddr, string sig)?, SigScanResult) TryResolveFromSignature(
            int index, string sig, int relOff, int instrLen, string desc, ulong gaBase, HashSet<ulong> testedRvas)
        {
            ulong[] sigAddrs;
            try
            {
                sigAddrs = Memory.FindSignaturesAll(sig, GameAssemblyName, MaxTableEntries);
            }
            catch (Exception ex)
            {
                Logging.WriteLine($"{LogTag} TypeInfoTable sig[{index}] scan error: {ex.Message}");
                return (null, new SigScanResult(index, desc, "ERROR", 0, 0, 0, ex.Message));
            }

            if (sigAddrs.Length == 0)
                return (null, new SigScanResult(index, desc, "MISS", 0, 0, 0));

            ulong duplicateRva = 0;
            string firstInvalidDetail = "";
            int validCount = 0;
            foreach (var sigAddr in sigAddrs)
            {
                var rva = ResolveRipRelativeRva(sigAddr, relOff, instrLen, gaBase);
                if (rva == 0)
                {
                    firstInvalidDetail = $"sig=0x{sigAddr:X}, rva=0";
                    continue;
                }
                if (!ValidateTypeInfoTable(gaBase, rva, out var detail))
                {
                    firstInvalidDetail = $"sig=0x{sigAddr:X}, rva=0x{rva:X}, {detail}";
                    continue;
                }

                validCount++;
                if (testedRvas.Add(rva))
                    return ((rva, sigAddr, sig), new SigScanResult(index, desc, "OK", sigAddrs.Length, validCount, rva));

                duplicateRva = rva;
            }

            if (duplicateRva != 0)
                return (null, new SigScanResult(index, desc, "DUPLICATE", sigAddrs.Length, validCount, duplicateRva));

            return (null, new SigScanResult(index, desc, "INVALID", sigAddrs.Length, validCount, 0, firstInvalidDetail));
        }

        private static ulong ResolveRipRelativeRva(ulong sigAddr, int relOffset, int instrLen, ulong gaBase)
        {
            int rel;
            try { rel = Memory.ReadValue<int>(sigAddr + (ulong)relOffset, false); }
            catch { return 0; }

            ulong globalVa = sigAddr + (ulong)instrLen + (ulong)(long)rel;
            return globalVa > gaBase ? globalVa - gaBase : 0;
        }

        private static bool ValidateTypeInfoTable(ulong gaBase, ulong rva) =>
            ValidateTypeInfoTable(gaBase, rva, out _);

        private static bool ValidateTypeInfoTable(ulong gaBase, ulong rva, out string detail)
        {
            detail = "";
            ulong tablePtr;
            try { tablePtr = Memory.ReadPtr(gaBase + rva, false); }
            catch (Exception ex)
            {
                detail = $"ptrSlot=0x{gaBase + rva:X}, read failed: {ex.Message}";
                return false;
            }

            int earlyValid = CountValidTableEntries(tablePtr, 0, EarlyProbeCount);
            int midValid = CountValidTableEntries(tablePtr, MidProbeOffset, MidProbeCount);
            int typeBytes = ReadTypeCountBytes(tablePtr);
            int typeCount = typeBytes > 0 ? typeBytes / 8 : 0;
            int tailStart = typeCount > EarlyProbeCount ? Math.Max(0, typeCount - EarlyProbeCount) : 0;
            int tailValid = typeCount > EarlyProbeCount ? CountValidTableEntries(tablePtr, tailStart, EarlyProbeCount) : 0;
            detail = $"ptrSlot=0x{gaBase + rva:X}, table=0x{tablePtr:X}, typeBytes={typeBytes}, typeCount={typeCount}, early={earlyValid}/{EarlyProbeCount}, mid={midValid}/{MidProbeCount}, tail={tailValid}/{EarlyProbeCount}";

            if (!tablePtr.IsValidUserVA())
                return false;

            bool hasPlausibleCount = typeBytes > 0
                && typeBytes % 8 == 0
                && typeCount is >= MinTypeCount and <= MaxClasses;
            if (hasPlausibleCount && (earlyValid >= EarlyProbeRequired || midValid >= MidProbeRequired || tailValid >= EarlyProbeRequired))
                return true;

            return earlyValid >= EarlyProbeRequired
                && midValid >= MidProbeRequired;
        }

        private static int ReadTypeCountBytes(ulong tablePtr)
        {
            if (tablePtr < 0x10)
                return 0;

            try { return Memory.ReadValue<int>(tablePtr - 0x10, false); }
            catch { return 0; }
        }

        private static bool ProbeTableEntries(ulong tablePtr, int startIndex, int count, int required) =>
            CountValidTableEntries(tablePtr, startIndex, count) >= required;

        private static int CountValidTableEntries(ulong tablePtr, int startIndex, int count)
        {
            ulong[] ptrs;
            try
            {
                using var pooled = Memory.ReadPooled<ulong>(tablePtr + (ulong)startIndex * 8, count, false);
                ptrs = pooled.Memory.Span.ToArray();
            }
            catch { return 0; }

            int valid = 0;
            foreach (var ptr in ptrs)
                if (IsValidClassPtr(ptr))
                    valid++;

            return valid;
        }

        private static bool IsValidClassPtr(ulong ptr)
        {
            if (!ptr.IsValidUserVA()) return false;
            try
            {
                var namePtr = Memory.ReadValue<ulong>(ptr + K_Name, false);
                if (!namePtr.IsValidUserVA()) return false;
                var name = ReadStr(namePtr);
                return !string.IsNullOrEmpty(name) && name.Length < MaxNameLen && IsPlausibleClassName(name);
            }
            catch { return false; }
        }

        private static bool IsPlausibleClassName(string name)
        {
            foreach (char c in name)
                if (c < 0x20 || (c > 0x7E && c < 0xA0)) return false;
            return true;
        }

        private static void ResolveTypeIndices(
            Dictionary<string, int> nameToIndex,
            List<(string Name, string Namespace, ulong KlassPtr, int Index)> classes)
        {
            foreach (var (il2cppName, fieldName) in TypeIndexMap)
            {
                var fi = GetTypeIndexField(fieldName);
                if (fi is null) continue;

                int dotIdx = il2cppName.LastIndexOf('.');
                if (dotIdx > 0)
                {
                    var ns = il2cppName[..dotIdx];
                    var shortName = il2cppName[(dotIdx + 1)..];
                    bool found = false;
                    foreach (var (cName, cNs, _, cIdx) in classes)
                    {
                        if (cName == shortName && cNs == ns)
                        {
                            UpdateTypeIndexField(fi, (uint)cIdx);
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                        Logging.WriteLine($"{LogTag} WARN: '{il2cppName}' not found in type table - {fieldName} using fallback ({fi.GetValue(null) ?? 0u}).");
                }
                else if (nameToIndex.TryGetValue(il2cppName, out var index))
                {
                    UpdateTypeIndexField(fi, (uint)index);
                }
                else
                {
                    Logging.WriteLine($"{LogTag} WARN: '{il2cppName}' not found in type table - {fieldName} using fallback ({fi.GetValue(null) ?? 0u}).");
                }
            }
        }

        private static FieldInfo? GetTypeIndexField(string fieldName) =>
            CachedTypeIndexFields.FirstOrDefault(f => f.Name == fieldName);

        private static void UpdateTypeIndexField(FieldInfo fi, uint newValue)
        {
            var previous = (uint)(fi.GetValue(null) ?? 0u);
            fi.SetValue(null, newValue);
            if (previous != newValue)
                Logging.WriteLine($"{LogTag} {fi.Name} UPDATED: {previous} -> {newValue}");
        }

        // ── Diagnostic report ───────────────────────────────────────────────────

        private static void DebugDumpResolverState(int classCount, int updated, int fallback, int skipped)
        {
            var gaBase = Memory.GameAssemblyBase;
            string gaText = gaBase.IsValidUserVA() ? $"0x{gaBase:X}" : "(not resolved)";

            int W = 56;
            const int SigTruncLen = 48;
            foreach (var r in _lastSigResults)
            {
                string rawSig = TypeInfoTableSigs[r.Index].Sig;
                int sigLen = Math.Min(rawSig.Length, SigTruncLen) + (rawSig.Length > SigTruncLen ? 3 : 0);
                int sigNeeded = 9 + sigLen;
                int statusNeeded = 4 + 7 + $"matches={r.Matches,-4} valid={r.ValidMatches,-4} rva=0x{r.Rva:X}".Length;
                if (sigNeeded > W) W = sigNeeded;
                if (statusNeeded > W) W = statusNeeded;
            }

            string Row(string text) => $"║  {text.PadRight(W - 2)}║";
            string Sep(string label) => $"╠── {label} {new string('─', W - 4 - label.Length)}╣";
            string Header(string text)
            {
                int pad = W - text.Length;
                int left = pad / 2;
                return $"║{new string(' ', left)}{text}{new string(' ', pad - left)}║";
            }

            Logging.WriteLine($"{LogTag} ╔{new string('═', W)}╗");
            Logging.WriteLine($"{LogTag} {Header("IL2CPP DIAGNOSTIC REPORT")}");
            Logging.WriteLine($"{LogTag} ╠{new string('═', W)}╣");
            Logging.WriteLine($"{LogTag} {Row("If you have issues, copy this entire block (╔ to ╚)")}");
            Logging.WriteLine($"{LogTag} {Row("and paste it when reporting to developers.")}");
            Logging.WriteLine($"{LogTag} ╠{new string('═', W)}╣");
            Logging.WriteLine($"{LogTag} {Row($"GameAssembly  : {gaText}")}");
            Logging.WriteLine($"{LogTag} {Row($"Resolution    : {_lastResolutionMode}")}");
            Logging.WriteLine($"{LogTag} {Row($"Table RVA     : 0x{Offsets.Special.TypeInfoTableRva:X}")}");
            Logging.WriteLine($"{LogTag} {Row($"Classes Found : {classCount}")}");

            int okCount = _lastSigResults.Count(r => r.State is "OK" or "DUPLICATE");
            Logging.WriteLine($"{LogTag} {Sep($"Signature Scan ({okCount}/{_lastSigResults.Length} OK)")}");
            foreach (var r in _lastSigResults)
            {
                string state = r.State is "OK" or "DUPLICATE" ? "OK" : r.State;
                string status = r.Rva != 0
                    ? $"  [{r.Index}] {state,-7} matches={r.Matches,-4} valid={r.ValidMatches,-4} rva=0x{r.Rva:X}"
                    : $"  [{r.Index}] {state,-7} matches={r.Matches,-4} valid={r.ValidMatches}";
                Logging.WriteLine($"{LogTag} {Row(status)}");
                if (!string.IsNullOrWhiteSpace(r.Detail))
                    Logging.WriteLine($"{LogTag} {Row($"       {r.Detail}")}");
                string rawSig = TypeInfoTableSigs[r.Index].Sig;
                string sigLine = rawSig.Length > SigTruncLen ? rawSig[..SigTruncLen] + "..." : rawSig;
                Logging.WriteLine($"{LogTag} {Row($"       {sigLine}")}");
            }

            Logging.WriteLine($"{LogTag} {Sep("Offset Dump")}");
            Logging.WriteLine($"{LogTag} {Row($"Updated  : {updated}")}");
            Logging.WriteLine($"{LogTag} {Row($"Fallback : {fallback}")}");
            Logging.WriteLine($"{LogTag} {Row($"Skipped  : {skipped}")}");
            Logging.WriteLine($"{LogTag} {Sep("TypeIndex Values")}");
            foreach (var (il2cppName, fieldName) in TypeIndexMap)
            {
                var fi = GetTypeIndexField(fieldName);
                string val = fi is not null ? $"{fi.GetValue(null)}" : "(missing)";
                Logging.WriteLine($"{LogTag} {Row($"  {il2cppName + ":",-24} {val}")}");
            }

            Logging.WriteLine($"{LogTag} ╚{new string('═', W)}╝");
        }
    }
}
