/*
 * Lone EFT DMA Radar - Copyright (c) 2026 Lone DMA
 * Licensed under GNU AGPLv3. See https://www.gnu.org/licenses/agpl-3.0.html
 */
namespace LoneEftDmaRadar
{
    /// <summary>
    /// Integrated logging module. Enables console logging by default.
    /// Use -noconsole to disable the console window.
    /// </summary>
    internal static partial class Logging
    {
        private static bool _useConsole;
        private static readonly object _sync = new();
        private static readonly Dictionary<string, DuplicateLogState> _duplicateLogStates = new(StringComparer.Ordinal);
        private static readonly TimeSpan _duplicateLogInterval = TimeSpan.FromSeconds(5);
        private static DateTime _lastDuplicateLogCleanup = DateTime.UtcNow;

        /// <summary>
        /// <see langword="true"/> if console logging is enabled.
        /// </summary>
        public static bool UseConsole => _useConsole;

        [ModuleInitializer]
        internal static void ModuleInit()
        {
            var args = Environment.GetCommandLineArgs();
            _useConsole = !(args?.Any(arg => arg.Equals("-noconsole", StringComparison.OrdinalIgnoreCase)) ?? false);
            if (_useConsole)
            {
                AllocConsole();

                // Redirect native C runtime stdout/stderr to the new console
                nint stdoutFile = default, stderrFile = default;
                _ = freopen_s(ref stdoutFile, "CONOUT$", "w", __acrt_iob_func(1)); // stdout
                _ = freopen_s(ref stderrFile, "CONOUT$", "w", __acrt_iob_func(2)); // stderr

                // Also redirect using SetStdHandle for Win32 API calls
                SetStdHandle(STD_OUTPUT_HANDLE, CreateFileW(
                    "CONOUT$",
                    GENERIC_WRITE,
                    FILE_SHARE_WRITE,
                    nint.Zero,
                    OPEN_EXISTING,
                    0,
                    nint.Zero));

                SetStdHandle(STD_ERROR_HANDLE, CreateFileW(
                    "CONOUT$",
                    GENERIC_WRITE,
                    FILE_SHARE_WRITE,
                    nint.Zero,
                    OPEN_EXISTING,
                    0,
                    nint.Zero));

                // Reopen .NET console streams
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                Console.SetIn(new StreamReader(Console.OpenStandardInput()));
            }
        }

        /// <summary>
        /// Writes the provided value to the Log followed by a new line.
        /// </summary>
        /// <param name="value">Value to be written to logging output.</param>
        public static void WriteLine(object value)
        {
            string message = value?.ToString() ?? string.Empty;
            string[] lines = message.ReplaceLineEndings("\n").Split('\n');
            DateTime now = DateTime.Now;
            int suppressedCount;

            lock (_sync)
            {
                CleanupDuplicateLogStates(now.ToUniversalTime());
                if (_duplicateLogStates.TryGetValue(message, out DuplicateLogState state))
                {
                    if (now.ToUniversalTime() - state.LastWrittenUtc < _duplicateLogInterval)
                    {
                        state.SuppressedCount++;
                        return;
                    }
                    suppressedCount = state.SuppressedCount;
                    state.LastWrittenUtc = now.ToUniversalTime();
                    state.SuppressedCount = 0;
                }
                else
                {
                    suppressedCount = 0;
                    _duplicateLogStates.Add(message, new DuplicateLogState(now.ToUniversalTime()));
                }
            }

            if (suppressedCount > 0)
                WriteTimestampedLine(now, $"[Logging] Suppressed {suppressedCount} duplicate log entr{(suppressedCount == 1 ? "y" : "ies")}.");
            foreach (string line in lines)
                WriteTimestampedLine(now, line);
        }

        private static void CleanupDuplicateLogStates(DateTime nowUtc)
        {
            if (_duplicateLogStates.Count < 1024 ||
                nowUtc - _lastDuplicateLogCleanup < TimeSpan.FromMinutes(1))
                return;

            _lastDuplicateLogCleanup = nowUtc;
            foreach (var pair in _duplicateLogStates.ToArray())
            {
                if (nowUtc - pair.Value.LastWrittenUtc >= TimeSpan.FromMinutes(1))
                    _ = _duplicateLogStates.Remove(pair.Key);
            }
        }

        private static void WriteTimestampedLine(DateTime timestamp, string line)
        {
            string output = $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] {line}";
            if (_useConsole)
            {
                Console.WriteLine(output);
            }
#if DEBUG
            else
            {
                Debug.WriteLine(output);
            }
#endif
        }

        private sealed class DuplicateLogState(DateTime lastWrittenUtc)
        {
            public DateTime LastWrittenUtc { get; set; } = lastWrittenUtc;
            public int SuppressedCount { get; set; }
        }

        private const int STD_OUTPUT_HANDLE = -11;
        private const int STD_ERROR_HANDLE = -12;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AllocConsole();

        [LibraryImport("kernel32.dll")]
        private static partial nint GetStdHandle(int nStdHandle);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetStdHandle(int nStdHandle, nint hHandle);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            nint lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            nint hTemplateFile);

        [LibraryImport("ucrtbase.dll", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int freopen_s(ref nint pFile, string filename, string mode, nint stream);

        // Returns pointer to C runtime FILE* streams (0=stdin, 1=stdout, 2=stderr)
        [LibraryImport("ucrtbase.dll")]
        private static partial nint __acrt_iob_func(int index);
    }
}

