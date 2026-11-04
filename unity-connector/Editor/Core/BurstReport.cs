using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace UnityCliConnector.Tools
{
    /// One Burst (BCxxxx) compile error, in the same shape verify prints C# compile errors:
    /// file:line:col CODE message.
    public sealed class BurstErrorEntry
    {
        public string File = "";
        public int Line;
        public int Col;
        public string Code = "";
        public string Message = "";

        /// The console entry's stack trace, when there was one. Burst puts both the position and the
        /// job it failed on in there, so target attribution reads it too.
        public string Detail = "";

        public string Format()
        {
            var where = "";
            if (!string.IsNullOrEmpty(File) && Line > 0)
                where = Col > 0 ? $"{File}:{Line}:{Col} " : $"{File}:{Line} ";
            var code = string.IsNullOrEmpty(Code) ? "" : Code + " ";
            return where + code + Message;
        }
    }

    /// A console entry as the connector reads it: mode bits, the full message (head + stack trace),
    /// and the entry's own file/line for when the message carries neither.
    public readonly struct BurstConsoleEntry
    {
        public readonly int Mode;
        public readonly string Message;
        public readonly string File;
        public readonly int Line;

        public BurstConsoleEntry(int mode, string message, string file, int line)
        {
            Mode = mode;
            Message = message;
            File = file;
            Line = line;
        }
    }

    /// One [BurstCompile] target: a job's generated Execute entry point or a [BurstCompile]
    /// static method. Field-carrying so the reflection bridge and the query helpers stay decoupled.
    public sealed class BurstTargetInfo
    {
        /// What the Burst Inspector shows, e.g. "ScaleValuesJob - (IJobFor)".
        public string DisplayName = "";

        /// Qualified display name, e.g. "MyGame.HeavyJob - (IJobFor)".
        public string FullName = "";

        /// Type that owns the target, as loaded (nested types use '+'), e.g. "MyGame.Heavy+InnerJob".
        public string DeclaringType = "";

        /// Burst's own scope string for the target: its namespace, or the containing type when the
        /// job is nested. Kept because a static-method target names its owner here.
        public string Scope = "";

        /// Owning assembly, "" when it could not be resolved.
        public string Assembly = "";

        /// One of the Status* constants below.
        public string Status = "";
    }

    /// Parsing, matching and counting of Burst compile diagnostics. Deliberately free of Burst
    /// references so these paths test (and compile) in projects without the Burst package.
    public static class BurstErrors
    {
        // Burst writes "<file>(<line>,<col>): Burst error BC1051: ..." — "Burst error" where the C#
        // compiler writes "error", so ConsoleLogEntries' header pattern misses it and its file/line
        // fall back to the LogEntry's own (empty) fields. Re-parse the header out of the message.
        private static readonly Regex HeaderPattern = new Regex(
            @"^(?<file>[^()]+)\((?<line>\d+),(?<col>\d+)\):\s*Burst\s+error\s+(?<code>BC\d+):\s*(?<msg>.*)$",
            RegexOptions.Compiled);

        // Names Burst quotes in its messages: "the struct `MyGame.HeavyJob`".
        private static readonly Regex QuotedNamePattern = new Regex(
            @"`(?<name>[A-Za-z_][\w.+]*)`", RegexOptions.Compiled);

        // Most Burst errors carry "(0,0): Burst error BCxxxx: ..." and put the position in the stack
        // trace instead: "at MyGame.Job.Execute(MyGame.Job* this) (at Assets/Job.cs:39)". Columns are
        // never in those frames.
        private static readonly Regex FramePattern = new Regex(
            @"\(at (?<file>[^()]+):(?<line>\d+)\)", RegexOptions.Compiled);

        // Burst errors inside a job's generated producer cite the engine's own job struct; that path
        // is not the user's code, so a frame outside it is preferred.
        private const string EnginePathMarker = "build/output/unity";

        /// Reads a Burst error out of a full console message (head plus stack trace). A BCxxxx code
        /// without a parseable header still counts as Burst; its position then comes from the first
        /// stack frame outside the engine, falling back to the console entry's own fields.
        public static bool TryParse(string fullMessage, string fileFallback, int lineFallback, out BurstErrorEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(fullMessage)) return false;

            var newline = fullMessage.IndexOf('\n');
            var head = newline >= 0 ? fullMessage.Substring(0, newline) : fullMessage;
            var detail = newline >= 0 ? fullMessage : null;

            var m = HeaderPattern.Match(head);
            if (m.Success)
            {
                entry = new BurstErrorEntry
                {
                    File = m.Groups["file"].Value.Trim(),
                    Line = int.Parse(m.Groups["line"].Value),
                    Col = int.Parse(m.Groups["col"].Value),
                    Code = m.Groups["code"].Value,
                    Message = m.Groups["msg"].Value.Trim(),
                    Detail = detail,
                };
                return true;
            }

            var code = BurstLead(head, out var burstMessage);
            if (code == null) return false;

            var (frameFile, frameLine) = FramePosition(detail);
            entry = new BurstErrorEntry
            {
                File = !string.IsNullOrEmpty(frameFile) ? frameFile : fileFallback ?? "",
                Line = frameLine > 0 ? frameLine : lineFallback,
                Col = 0,
                Code = code,
                Message = burstMessage,
                Detail = detail,
            };
            return true;
        }

        // "Burst error BC1091: ..." / "Burst warning BC1370: ..." with no "file(line,col):" in front.
        // Requiring the words "Burst error" is what keeps a C# error in a file whose name contains
        // BCnn from being read as a Burst code.
        private static readonly Regex BurstLeadPattern = new Regex(
            @"Burst\s+(?:error|warning)\s+(?<code>BC\d+)\s*:\s*(?<msg>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static string BurstLead(string head, out string message)
        {
            message = "";
            var m = BurstLeadPattern.Match(head);
            if (!m.Success) return null;
            message = m.Groups["msg"].Value.Trim();
            return m.Groups["code"].Value;
        }

        static (string File, int Line) FramePosition(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return ("", 0);

            string fallbackFile = "";
            int fallbackLine = 0;
            foreach (Match frame in FramePattern.Matches(detail))
            {
                var file = frame.Groups["file"].Value.Trim();
                var line = int.Parse(frame.Groups["line"].Value);
                if (!file.Contains(EnginePathMarker, StringComparison.OrdinalIgnoreCase))
                    return (file, line);
                if (fallbackLine == 0)
                {
                    fallbackFile = file;
                    fallbackLine = line;
                }
            }
            return (fallbackFile, fallbackLine);
        }

        /// Burst's own errors out of the live compile diagnostics (whose message is only the head) and
        /// the raw console entries (which carry the stack trace). Deduped by code + message, keeping
        /// whichever copy knows a position outside the engine — the same error reaches both sources.
        public static List<BurstErrorEntry> Collect(IEnumerable<ConsoleLogEntries.CompileDiag> diagnostics,
            IEnumerable<BurstConsoleEntry> consoleEntries)
        {
            var errors = new List<BurstErrorEntry>();
            var indexByKey = new Dictionary<string, int>();

            void Add(BurstErrorEntry entry)
            {
                var key = entry.Code + "|" + entry.Message;
                if (!indexByKey.TryGetValue(key, out var at))
                {
                    indexByKey[key] = errors.Count;
                    errors.Add(entry);
                    return;
                }
                if (HasUsablePosition(entry) && !HasUsablePosition(errors[at]))
                {
                    var stored = errors[at];
                    stored.File = entry.File;
                    stored.Line = entry.Line;
                    stored.Col = entry.Col;
                    stored.Detail = entry.Detail ?? stored.Detail;
                }
            }

            if (diagnostics != null)
                foreach (var d in diagnostics)
                {
                    if (d.Severity != "error") continue;
                    if (!TryParse(d.Message, d.File, d.Line, out var entry)) continue;
                    Add(entry);
                }

            if (consoleEntries != null)
                foreach (var c in consoleEntries)
                {
                    if (!TryParse(c.Message, c.File, c.Line, out var entry)) continue;
                    Add(entry);
                }

            return errors;
        }

        static bool HasUsablePosition(BurstErrorEntry entry)
        {
            return entry.Line > 0 && !string.IsNullOrEmpty(entry.File) &&
                   !entry.File.Contains(EnginePathMarker, StringComparison.OrdinalIgnoreCase);
        }

        /// Whether a Burst error names this target. The head line carries the type Burst choked on
        /// ("the struct `MyGame.HeavyJob`"), and stack frames carry the job's own method. The rest of
        /// the entry is a compiler dump that lists many unrelated names, so the body is read frame by
        /// frame and never matched as a whole.
        public static bool PointsAt(BurstErrorEntry error, BurstTargetInfo target)
        {
            if (error == null || target == null) return false;
            var declaring = target.DeclaringType ?? "";
            if (declaring.Length == 0) return false;

            var head = error.Message ?? "";
            if (ContainsTypeName(head, declaring)) return true;
            foreach (var name in QuotedNames(head))
                if (SameType(name, declaring)) return true;

            foreach (var frame in FrameFunctions(error.Detail ?? ""))
                if (ContainsTypeName(frame, declaring)) return true;
            return false;
        }

        // "at Ns.Job.Execute(Ns.Job* this)" or "at Ns.Job.Execute" — the callable part of a frame.
        private static readonly Regex FrameFunctionPattern = new Regex(
            @"^\s*at\s+(?<fn>[^\s(]+)", RegexOptions.Compiled | RegexOptions.Multiline);

        static IEnumerable<string> FrameFunctions(string detail)
        {
            if (string.IsNullOrEmpty(detail)) yield break;
            foreach (Match m in FrameFunctionPattern.Matches(detail))
                yield return m.Groups["fn"].Value;
        }

        /// Name containment with a word boundary, so "MyGame.Job" does not match "MyGame.JobUtils".
        /// Burst writes generics as Ns.Job`1<Ns.Job>, nested types as Ns.Outer+Inner, so the check
        /// covers both spellings.
        static bool ContainsTypeName(string hay, string needle)
        {
            if (string.IsNullOrEmpty(hay) || string.IsNullOrEmpty(needle)) return false;
            foreach (var n in new[] { needle, needle.Replace('+', '.') })
            {
                var h = n == needle ? hay : hay.Replace('+', '.');
                var from = 0;
                while (true)
                {
                    var at = h.IndexOf(n, from, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;
                    var end = at + n.Length;
                    if (end >= h.Length || !IsNameChar(h[end])) return true;
                    from = at + 1;
                }
            }
            return false;
        }

        static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        static List<string> QuotedNames(string hay)
        {
            var names = new List<string>();
            foreach (Match m in QuotedNamePattern.Matches(hay))
                names.Add(m.Groups["name"].Value);
            return names;
        }

        static bool SameType(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            var x = a.Replace('+', '.');
            var y = b.Replace('+', '.');
            if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;
            // Burst sometimes names the member ("MyGame.HeavyJob.Execute") rather than the type.
            return x.EndsWith("." + y, StringComparison.OrdinalIgnoreCase) ||
                   y.EndsWith("." + x, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// Filtering and status rules over a list of Burst targets. Same no-Burst-dependency rule as
    /// <see cref="BurstErrors"/>: the connector's EditMode tests compile in projects without Burst.
    public static class BurstTargets
    {
        public const string StatusCompiled = "compiled";
        public const string StatusFailed = "failed";
        public const string StatusNotCompiled = "not-compiled-yet";
        public const string StatusUnknown = "unknown";

        /// Case-insensitive substring on the type/target name, namespace prefix, and assembly name
        /// list. Empty/absent filters match everything.
        public static bool Matches(BurstTargetInfo target, string type, string ns, IReadOnlyCollection<string> assemblies)
        {
            if (target == null) return false;

            if (!string.IsNullOrEmpty(type))
            {
                var needle = type.Trim();
                var hit = IndexOfName(target.FullName, needle) >= 0 ||
                          IndexOfName(target.DisplayName, needle) >= 0 ||
                          IndexOfName(target.DeclaringType, needle) >= 0;
                if (!hit) return false;
            }

            if (!string.IsNullOrEmpty(ns))
            {
                var want = ns.Trim().TrimEnd('.');
                var scope = NamespaceOf(target.DeclaringType);
                if (!scope.StartsWith(want, StringComparison.OrdinalIgnoreCase) &&
                    !target.DeclaringType.StartsWith(want, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (assemblies != null && assemblies.Count > 0)
            {
                if (string.IsNullOrEmpty(target.Assembly)) return false;
                var found = assemblies.Any(a => string.Equals(a?.Trim(), target.Assembly, StringComparison.OrdinalIgnoreCase));
                if (!found) return false;
            }

            return true;
        }

        static int IndexOfName(string hay, string needle)
        {
            if (string.IsNullOrEmpty(hay)) return -1;
            return hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        }

        /// Whether a target's resolved assembly is one of the project's own. Having an assembly name
        /// is not enough: on Burst 1.8 every target carries the assembly of its job type, including
        /// package and engine jobs.
        public static bool IsProjectTarget(BurstTargetInfo target, IReadOnlyCollection<string> projectAssemblies)
        {
            if (target == null || string.IsNullOrEmpty(target.Assembly) || projectAssemblies == null) return false;
            return projectAssemblies.Any(name => string.Equals(name?.Trim(), target.Assembly, StringComparison.OrdinalIgnoreCase));
        }

        public static string NamespaceOf(string declaringType)
        {
            if (string.IsNullOrEmpty(declaringType)) return "";
            var lastDot = declaringType.LastIndexOf('.');
            return lastDot <= 0 ? "" : declaringType.Substring(0, lastDot);
        }

        /// compiled / failed / not-compiled-yet / unknown.
        ///
        /// Burst eager-compiles every target it knows about when the assembly finishes compiling, and
        /// its own queue answers "is any of that still running", so an idle queue plus no error naming
        /// the target is the strongest signal available without re-running Burst. idleKnown is false
        /// only when this Burst build does not answer the queue question at all.
        public static string StatusOf(BurstTargetInfo target, IReadOnlyList<BurstErrorEntry> errors, bool idleKnown, bool idle)
        {
            if (errors != null)
                foreach (var e in errors)
                    if (BurstErrors.PointsAt(e, target))
                        return StatusFailed;

            if (!idleKnown) return StatusUnknown;
            return idle ? StatusCompiled : StatusNotCompiled;
        }

        public static void Count(IReadOnlyList<BurstTargetInfo> targets, out int compiled, out int failed, out int notCompiled, out int unknown)
        {
            compiled = failed = notCompiled = unknown = 0;
            foreach (var t in targets)
                switch (t.Status)
                {
                    case StatusCompiled: compiled++; break;
                    case StatusFailed: failed++; break;
                    case StatusNotCompiled: notCompiled++; break;
                    default: unknown++; break;
                }
        }

        /// Whether a source file belongs to a package rather than to the project's own code.
        /// packageRoots are the absolute resolved paths of registered packages, which is the only
        /// way to catch file: packages that live outside Packages/ (e.g. LocalPackages/...).
        public static bool IsPackageSourcePath(string sourceFile, IReadOnlyCollection<string> packageRoots)
        {
            if (string.IsNullOrEmpty(sourceFile)) return true;
            var path = sourceFile.Replace('\\', '/');
            if (path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) return true;
            // Embedded packages live in <project>/Packages/<name>, and resolved registry packages under
            // Library/PackageCache; both show up as absolute paths here.
            if (path.Contains("/Packages/", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Contains("PackageCache/", StringComparison.OrdinalIgnoreCase)) return true;
            if (packageRoots == null) return false;
            foreach (var root in packageRoots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                var normalized = root.Replace('\\', '/');
                if (!normalized.EndsWith("/", StringComparison.Ordinal)) normalized += "/";
                if (path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
