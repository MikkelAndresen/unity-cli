using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "console", Description = "Read or clear Unity console logs.", SkipCompilationGate = true)]
    public static class ReadConsole
    {
        // Defaults keep a chatty editor inside one tool output: 50 of the newest entries, at most
        // 6 stack frames each, 1 KB per entry, 15 KB of entry text in total. --full removes them.
        // 15 KB rather than 16 KB: the CLI re-encodes the entries (indentation, \u003c escaping of
        // the angle brackets in generic frame names), so the printed reply is a few percent larger
        // than the entry bytes counted here.
        private const int DefaultLines = 50;
        private const int DefaultFrames = 6;
        private const int DefaultEntryChars = 1000;
        private const int DefaultMaxBytes = 15360;

        private static MethodInfo _startGettingEntriesMethod, _endGettingEntriesMethod, _clearMethod, _getCountMethod, _getEntryMethod;
        private static MethodInfo _getEntryTimestampMethod;
        private static FieldInfo _modeField, _messageField, _fileField, _lineField, _globalLineIndexField;
        private static Type _logEntryType;
        private static PropertyInfo _consoleFlagsProperty;
        private static MethodInfo _getFilteringTextMethod, _setFilteringTextMethod;

        static ReadConsole()
        {
            try
            {
                Type logEntriesType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntriesType == null) throw new Exception("Could not find UnityEditor.LogEntries");
                BindingFlags sf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                BindingFlags inf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _startGettingEntriesMethod = logEntriesType.GetMethod("StartGettingEntries", sf)
                    ?? throw new Exception("Method not found: LogEntries.StartGettingEntries");
                _endGettingEntriesMethod = logEntriesType.GetMethod("EndGettingEntries", sf)
                    ?? throw new Exception("Method not found: LogEntries.EndGettingEntries");
                _clearMethod = logEntriesType.GetMethod("Clear", sf)
                    ?? throw new Exception("Method not found: LogEntries.Clear");
                _getCountMethod = logEntriesType.GetMethod("GetCount", sf)
                    ?? throw new Exception("Method not found: LogEntries.GetCount");
                _getEntryMethod = logEntriesType.GetMethod("GetEntryInternal", sf)
                    ?? throw new Exception("Method not found: LogEntries.GetEntryInternal");
                // Optional: used to tell a current compile error from a stale console entry.
                _getEntryTimestampMethod = logEntriesType.GetMethod("GetEntryTimestampInternal", sf);

                // Optional: older Unity versions may not expose the Console window filter. The read
                // still works then, it just cannot widen what the window is currently showing.
                _consoleFlagsProperty = logEntriesType.GetProperty("consoleFlags", sf);
                _getFilteringTextMethod = logEntriesType.GetMethod("GetFilteringText", sf);
                _setFilteringTextMethod = logEntriesType.GetMethod("SetFilteringText", sf);

                _logEntryType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntry")
                    ?? throw new Exception("Could not find UnityEditor.LogEntry");
                _modeField = _logEntryType.GetField("mode", inf)
                    ?? throw new Exception("Field not found: LogEntry.mode");
                _messageField = _logEntryType.GetField("message", inf)
                    ?? throw new Exception("Field not found: LogEntry.message");
                _fileField = _logEntryType.GetField("file", inf);
                _lineField = _logEntryType.GetField("line", inf);
                // Optional: the console-wide ordinal of the entry. It only grows while the console
                // keeps its entries and restarts at 0 when the console is cleared, which is what
                // lets CompilationGate tell this compile's entries from older ones.
                _globalLineIndexField = _logEntryType.GetField("globalLineIndex", inf);
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityCliConnector] ReadConsole init failed: {e.Message}");
                _startGettingEntriesMethod = _endGettingEntriesMethod = _clearMethod = _getCountMethod = _getEntryMethod = null;
                _getEntryTimestampMethod = null;
                _modeField = _messageField = _fileField = _lineField = _globalLineIndexField = null;
                _logEntryType = null;
                _consoleFlagsProperty = null;
                _getFilteringTextMethod = _setFilteringTextMethod = null;
            }
        }

        public class Parameters
        {
            [ToolParameter("Comma-separated log types: error, warning, log. Default: error,warning,log")]
            public string Type { get; set; }

            [ToolParameter("Most recent N entries to return (0 = no limit). Default: 50")]
            public int Lines { get; set; }

            [ToolParameter("Stack trace mode: none (first line), user (user code frames only, engine frames collapsed), full (raw). Default: user")]
            public string Stacktrace { get; set; }

            [ToolParameter("Stack frames kept per entry (0 = none, -1 = all). Default: 6")]
            public int Frames { get; set; }

            [ToolParameter("Max characters per entry before it is cut (0 = no limit). Default: 1000")]
            public int EntryChars { get; set; }

            [ToolParameter("Max total bytes of entry text in the response (0 = no limit). Default: 15360")]
            public int MaxBytes { get; set; }

            [ToolParameter("Return everything: turns off the lines/frames/entry_chars/max_bytes caps")]
            public bool Full { get; set; }

            [ToolParameter("Clear console")]
            public bool Clear { get; set; }
        }

        // Caps applied to the entry list. Lines/EntryChars/MaxBytes treat 0 (or less) as "no
        // limit", matching how an absent --lines meant unlimited before; Frames uses
        // StackTraceFilter.UnlimitedFrames for "every frame" so that 0 can mean "no frames".
        private readonly struct Limits
        {
            public readonly int Lines;
            public readonly int Frames;
            public readonly int EntryChars;
            public readonly int MaxBytes;

            public Limits(int lines, int frames, int entryChars, int maxBytes)
            {
                Lines = Math.Max(0, lines);
                Frames = frames;
                EntryChars = Math.Max(0, entryChars);
                MaxBytes = Math.Max(0, maxBytes);
            }

            public static Limits Resolve(ToolParams p, bool full)
            {
                if (full) return new Limits(0, StackTraceFilter.UnlimitedFrames, 0, 0);
                return new Limits(
                    p.GetInt("lines") ?? p.GetInt("count") ?? DefaultLines,
                    p.GetInt("frames") ?? DefaultFrames,
                    p.GetInt("entry_chars") ?? DefaultEntryChars,
                    p.GetInt("max_bytes") ?? DefaultMaxBytes);
            }
        }

        public static object HandleCommand(JObject @params)
        {
            if (_startGettingEntriesMethod == null || _endGettingEntriesMethod == null ||
                _clearMethod == null || _getCountMethod == null || _getEntryMethod == null ||
                _modeField == null || _messageField == null || _logEntryType == null)
                return new ErrorResponse("ReadConsole failed to initialize (reflection error).");

            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);

            // --clear
            if (p.GetBool("clear"))
            {
                _clearMethod.Invoke(null, null);
                return new SuccessResponse("Console cleared.");
            }

            var type = p.Get("type", "error,warning,log").ToLower();
            var types = type.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

            if (!StackTraceFilter.TryParseMode(p.Get("stacktrace", "user"), out var mode))
                return new ErrorResponse("Unknown stacktrace mode. Use none, user, or full.");

            return GetEntries(types, mode, Limits.Resolve(p, p.GetBool("full")));
        }

        private static object GetEntries(List<string> types, StackTraceFilter.StacktraceMode mode, Limits limits)
        {
            // Newest-first: the Console is oldest-first, but a flood is only interesting at its
            // tail, and stopping at the line cap is what keeps `--lines 5` from reading a 100k-entry
            // console. Entries are formatted after the scan so byte-truncated ones are never shaped.
            var matched = new List<int>();
            var texts = new List<string>();
            bool cutByBytes = false, stoppedAtLineCap = false;
            int framesOmitted = 0, framesOverCap = 0, charsOmitted = 0, bytes = 0, scanned = 0;

            var filterScope = EnterConsoleFilterOverride();
            try
            {
                _startGettingEntriesMethod.Invoke(null, null);
                int total = (int)_getCountMethod.Invoke(null, null);
                object logEntry = Activator.CreateInstance(_logEntryType);

                int index = total - 1;
                for (; index >= 0; index--)
                {
                    scanned++;
                    _getEntryMethod.Invoke(null, new object[] { index, logEntry });
                    int entryMode = (int)_modeField.GetValue(logEntry);

                    LogType logType = GetLogTypeFromMode(entryMode);
                    bool want = logType == LogType.Exception || logType == LogType.Assert
                        ? types.Contains("error")
                        : types.Contains(logType.ToString().ToLowerInvariant());
                    if (!want) continue;

                    string message = (string)_messageField.GetValue(logEntry);
                    if (string.IsNullOrEmpty(message)) continue;

                    matched.Add(index);
                    if (limits.Lines > 0 && matched.Count >= limits.Lines)
                    {
                        stoppedAtLineCap = index > 0;
                        break;
                    }
                }

                foreach (var entryIndex in matched)
                {
                    _getEntryMethod.Invoke(null, new object[] { entryIndex, logEntry });
                    string message = (string)_messageField.GetValue(logEntry);
                    var formatted = StackTraceFilter.Format(message, mode, limits.Frames, limits.EntryChars);
                    framesOmitted += formatted.FramesOmitted;
                    framesOverCap += formatted.FramesOverCap;
                    charsOmitted += formatted.CharsOmitted;

                    int cost = Encoding.UTF8.GetByteCount(JsonConvert.ToString(formatted.Text));
                    if (limits.MaxBytes > 0 && bytes + cost > limits.MaxBytes)
                    {
                        cutByBytes = true;
                        break;
                    }
                    bytes += cost;
                    texts.Add(formatted.Text);
                }
            }
            finally
            {
                try { _endGettingEntriesMethod.Invoke(null, null); } catch { }
                filterScope.Dispose();
            }

            texts.Reverse();

            var truncatedBy = new List<string>();
            if (stoppedAtLineCap) truncatedBy.Add("lines");
            if (cutByBytes) truncatedBy.Add("bytes");
            if (framesOverCap > 0) truncatedBy.Add("frames");
            if (charsOmitted > 0) truncatedBy.Add("chars");

            string summary;
            if (stoppedAtLineCap)
            {
                summary = $"Retrieved {texts.Count} of the newest {matched.Count} matching entries " +
                          "(stopped at --lines; older matches were not counted or read): raise --lines for more.";
            }
            else
            {
                summary = truncatedBy.Count == 0
                    ? $"Retrieved {texts.Count} entries."
                    : $"Retrieved {texts.Count} of {matched.Count} entries (cut by {string.Join(",", truncatedBy)}): pass --full for everything.";
            }
            if (filterScope.Note != null) summary += $" ({filterScope.Note})";
            if (filterScope.RestoreError != null) summary += $" (warning: {filterScope.RestoreError})";

            var data = new Dictionary<string, object>
            {
                ["entries"] = texts,
                ["entries_total"] = matched.Count,
                ["entries_total_is_lower_bound"] = stoppedAtLineCap,
                ["entries_scanned"] = scanned,
                ["entries_returned"] = texts.Count,
                ["entries_omitted"] = matched.Count - texts.Count,
                ["frames_omitted"] = framesOmitted,
                ["chars_omitted"] = charsOmitted,
                ["truncated"] = truncatedBy.Count > 0,
                ["truncated_by"] = truncatedBy,
                ["limits"] = new
                {
                    lines = limits.Lines,
                    frames = limits.Frames,
                    entry_chars = limits.EntryChars,
                    max_bytes = limits.MaxBytes,
                    stacktrace = StackTraceFilter.ModeName(mode),
                },
            };
            if (filterScope.Changed || filterScope.FilterUnavailable)
                data["console_window_filter"] = filterScope.Details;
            if (filterScope.RestoreError != null)
                data["console_filter_restore_error"] = filterScope.RestoreError;
            return new SuccessResponse(summary, data);
        }

        // Matches UnityEditor.ConsoleWindow+ConsoleFlags. The Console window writes them to
        // LogEntries.consoleFlags, which is also what GetCount/GetEntryInternal filter on.
        private const int CollapseFlag = 1 << 0;
        private const int LogLevelLogFlag = 1 << 7;
        private const int LogLevelWarningFlag = 1 << 8;
        private const int LogLevelErrorFlag = 1 << 9;
        private const int LogLevelMask = LogLevelLogFlag | LogLevelWarningFlag | LogLevelErrorFlag;

        /// Temporarily widens the Console window's view to every entry. Save/restore happens here
        /// rather than in the caller so console reads and the compile-diagnostics fallback cannot
        /// drift apart. No-op when the filter API is unavailable.
        private static ConsoleFilterScope EnterConsoleFilterOverride()
        {
            if (_consoleFlagsProperty == null) return ConsoleFilterScope.Unavailable;

            int savedFlags;
            try
            {
                var raw = _consoleFlagsProperty.GetValue(null);
                if (raw == null) return ConsoleFilterScope.Unavailable;
                savedFlags = Convert.ToInt32(raw);
            }
            catch { return ConsoleFilterScope.Unavailable; }

            bool haveFilter = _getFilteringTextMethod != null && _setFilteringTextMethod != null;
            string savedFilter = "";
            if (haveFilter)
            {
                try { savedFilter = _getFilteringTextMethod.Invoke(null, null) as string ?? ""; }
                catch { haveFilter = false; }
            }

            var hidden = new List<string>();
            if ((savedFlags & LogLevelLogFlag) == 0) hidden.Add("log");
            if ((savedFlags & LogLevelWarningFlag) == 0) hidden.Add("warning");
            if ((savedFlags & LogLevelErrorFlag) == 0) hidden.Add("error");
            bool collapsed = (savedFlags & CollapseFlag) != 0;

            int desiredFlags = (savedFlags | LogLevelMask) & ~CollapseFlag;
            bool changeFlags = desiredFlags != savedFlags;
            bool changeFilter = haveFilter && !string.IsNullOrEmpty(savedFilter);

            bool flagsSet = false, filterSet = false;
            if (changeFlags) flagsSet = TrySetConsoleFlags(desiredFlags);
            if (changeFilter)
            {
                try { _setFilteringTextMethod.Invoke(null, new object[] { "" }); filterSet = true; }
                catch { }
            }

            if (!flagsSet && !filterSet)
                return changeFlags || changeFilter ? ConsoleFilterScope.Unavailable : ConsoleFilterScope.None;
            return new ConsoleFilterScope(savedFlags, savedFilter, flagsSet, filterSet, hidden, collapsed);
        }

        private static bool TrySetConsoleFlags(int flags)
        {
            if (SetFlagsForTests != null) return SetFlagsForTests(flags);
            try
            {
                var type = _consoleFlagsProperty.PropertyType;
                object value = type.IsEnum ? Enum.ToObject(type, flags) : Convert.ChangeType(flags, type);
                _consoleFlagsProperty.SetValue(null, value);
                return true;
            }
            catch { return false; }
        }

        internal static Func<int, bool> SetFlagsForTests;

        static void ReportRestoreFailure(string error)
        {
            if (error != null) Debug.LogWarning("[UnityCliConnector] console: " + error);
        }

        private sealed class ConsoleFilterScope : IDisposable
        {
            public static readonly ConsoleFilterScope None = new ConsoleFilterScope();
            public static readonly ConsoleFilterScope Unavailable = new ConsoleFilterScope(true);

            private readonly int _savedFlags;
            private readonly string _savedFilter;
            private readonly bool _restoreFlags;
            private readonly bool _restoreFilter;
            private readonly List<string> _hidden;
            private readonly bool _collapsed;
            private readonly bool _unavailable;

            private ConsoleFilterScope(bool unavailable = false)
            {
                _hidden = new List<string>();
                _unavailable = unavailable;
            }

            public ConsoleFilterScope(int savedFlags, string savedFilter, bool restoreFlags, bool restoreFilter,
                List<string> hidden, bool collapsed)
            {
                _savedFlags = savedFlags;
                _savedFilter = savedFilter;
                _restoreFlags = restoreFlags;
                _restoreFilter = restoreFilter;
                _hidden = hidden;
                _collapsed = collapsed;
            }

            public bool Changed => _restoreFlags || _restoreFilter;

            public bool FilterUnavailable => _unavailable;

            // Non-null when the user's Console window settings could not be put back: the window is
            // left widened, which is exactly the state the override must not leave behind silently.
            public string RestoreError { get; private set; }

            public string Note
            {
                get
                {
                    if (_unavailable)
                        return "Console window filter unavailable on this Unity version; an empty result may be filtered";
                    var parts = new List<string>();
                    if (_restoreFlags && _hidden.Count > 0) parts.Add(string.Join("/", _hidden) + " hidden");
                    if (_restoreFilter && !string.IsNullOrEmpty(_savedFilter)) parts.Add($"search '{_savedFilter}'");
                    if (_restoreFlags && _collapsed) parts.Add("collapse on");
                    return parts.Count == 0 ? null : "ignored Console window filter: " + string.Join(", ", parts);
                }
            }

            public object Details => new
            {
                available = !_unavailable,
                note = Note,
                log_hidden = _restoreFlags && _hidden.Contains("log"),
                warning_hidden = _restoreFlags && _hidden.Contains("warning"),
                error_hidden = _restoreFlags && _hidden.Contains("error"),
                search = _restoreFilter && !string.IsNullOrEmpty(_savedFilter) ? _savedFilter : null,
                collapsed = _restoreFlags && _collapsed,
            };

            public void Dispose()
            {
                if (_restoreFlags && !TrySetConsoleFlags(_savedFlags))
                    RestoreError = "the Console window's Log/Warning/Error toggles could not be restored; set them back in the Console window";
                if (_restoreFilter)
                {
                    try { _setFilteringTextMethod.Invoke(null, new object[] { _savedFilter }); }
                    catch
                    {
                        RestoreError ??= "the Console window's search text could not be restored";
                    }
                }
            }
        }

        private const int ErrorMask =
            (1 << 0)  |  // Error
            (1 << 6)  |  // AssetImportError
            (1 << 8)  |  // ScriptingError
            (1 << 11) |  // ScriptCompileError
            (1 << 13);   // StickyError

        private const int WarningMask =
            (1 << 7)  |  // AssetImportWarning
            (1 << 9)  |  // ScriptingWarning
            (1 << 12);   // ScriptCompileWarning

        private const int ExceptionMask =
            (1 << 1)  |  // Assert
            (1 << 4)  |  // Fatal
            (1 << 17) |  // ScriptingException
            (1 << 21);   // ScriptingAssertion

        private static LogType GetLogTypeFromMode(int mode)
        {
            if ((mode & ExceptionMask) != 0) return LogType.Exception;
            if ((mode & ErrorMask) != 0) return LogType.Error;
            if ((mode & WarningMask) != 0) return LogType.Warning;
            return LogType.Log;
        }

        /// Reflection seam for other connector code that needs to iterate the
        /// in-memory editor console without duplicating the LogEntries binding.
        /// Returns false when reflection is unavailable (init failed); the visitor
        /// receives (mode, message, file, line) for each entry. StartGettingEntries
        /// / EndGettingEntries are paired by this method.
        internal static bool TryForEachEntry(Action<int, string, string, int> visitor)
        {
            if (visitor == null) throw new ArgumentNullException(nameof(visitor));
            return TryForEachEntry((mode, message, file, line, _, _) => visitor(mode, message, file, line));
        }

        /// Same iteration, also passing the entry's console timestamp (null when the Unity
        /// version does not expose GetEntryTimestampInternal) and its globalLineIndex (-1 when the
        /// field is absent).
        internal static bool TryForEachEntry(Action<int, string, string, int, string, int> visitor)
        {
            if (visitor == null) throw new ArgumentNullException(nameof(visitor));
            if (_startGettingEntriesMethod == null || _endGettingEntriesMethod == null ||
                _getCountMethod == null || _getEntryMethod == null ||
                _modeField == null || _messageField == null || _logEntryType == null)
                return false;

            var filterScope = EnterConsoleFilterOverride();
            try
            {
                _startGettingEntriesMethod.Invoke(null, null);
                int total = (int)_getCountMethod.Invoke(null, null);
                object logEntry = Activator.CreateInstance(_logEntryType);
                for (int i = 0; i < total; i++)
                {
                    _getEntryMethod.Invoke(null, new object[] { i, logEntry });
                    int mode = (int)_modeField.GetValue(logEntry);
                    string message = _messageField.GetValue(logEntry) as string;
                    string file = _fileField?.GetValue(logEntry) as string;
                    int line = _lineField != null ? (int)_lineField.GetValue(logEntry) : 0;
                    string timestamp = null;
                    if (_getEntryTimestampMethod != null)
                    {
                        try
                        {
                            var args = new object[] { i, null };
                            _getEntryTimestampMethod.Invoke(null, args);
                            timestamp = args[1] as string;
                        }
                        catch { }
                    }
                    int index = _globalLineIndexField != null
                        ? (int)_globalLineIndexField.GetValue(logEntry)
                        : -1;
                    visitor(mode, message, file, line, timestamp, index);
                }
                return true;
            }
            finally
            {
                try { _endGettingEntriesMethod.Invoke(null, null); } catch { }
                filterScope.Dispose();
                ReportRestoreFailure(filterScope.RestoreError);
            }
        }

        /// The console's ordinal range: the oldest remaining entry's globalLineIndex and the newest
        /// one. An empty console reports (-1, -1); false means the binding is unavailable, not empty.
        internal static bool TryGetConsoleIndexRange(out int firstIndex, out int lastIndex)
        {
            firstIndex = -1;
            lastIndex = -1;
            if (_startGettingEntriesMethod == null || _endGettingEntriesMethod == null ||
                _getCountMethod == null || _getEntryMethod == null || _globalLineIndexField == null ||
                _logEntryType == null)
                return false;

            var filterScope = EnterConsoleFilterOverride();
            try
            {
                _startGettingEntriesMethod.Invoke(null, null);
                int total = (int)_getCountMethod.Invoke(null, null);
                if (total <= 0) return true;

                object logEntry = Activator.CreateInstance(_logEntryType);
                _getEntryMethod.Invoke(null, new object[] { 0, logEntry });
                firstIndex = (int)_globalLineIndexField.GetValue(logEntry);
                if (total > 1)
                    _getEntryMethod.Invoke(null, new object[] { total - 1, logEntry });
                lastIndex = (int)_globalLineIndexField.GetValue(logEntry);
                return true;
            }
            finally
            {
                try { _endGettingEntriesMethod.Invoke(null, null); } catch { }
                filterScope.Dispose();
                ReportRestoreFailure(filterScope.RestoreError);
            }
        }

        internal static bool IsReflectionAvailable =>
            _startGettingEntriesMethod != null && _endGettingEntriesMethod != null &&
            _getCountMethod != null && _getEntryMethod != null &&
            _modeField != null && _messageField != null && _logEntryType != null;
    }
}
