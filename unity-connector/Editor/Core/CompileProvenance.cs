using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
    /// Says what the last script compilation actually covers: when it finished, how long it took,
    /// which assemblies it rebuilt, how many source files moved since the compile before it, and
    /// which sources are newer than the compile itself — the case where the edit is in the project
    /// but not in the "clean" answer, which is what makes agents poll the editor by hand.
    ///
    /// The record lives in SessionState rather than a static field: the domain reload that ends a
    /// successful compile wipes statics before the CLI ever asks, which is the same reason
    /// CompilationGate re-stamps its clock in afterAssemblyReload. SessionState survives reloads and
    /// dies with the editor process, which is exactly this record's intended lifetime.
    [InitializeOnLoad]
    public static class CompileProvenance
    {
        const string SessionKey = "unity-cli/compile-provenance";

        /// Filesystem scans are bounded: over budget the answer says it is incomplete instead of
        /// stalling the main thread, which is where every tool call runs.
        public const int DefaultScanBudgetMs = 200;
        public const int MaxStaleNames = 5;
        const int MaxRebuiltNames = 64;
        const int DeadlineCheckEvery = 256;

        static Record s_Record = new();
        static long s_PendingStartedMs;
        static readonly List<string> s_RebuiltNames = new();
        static bool s_RebuiltTruncated;

        internal sealed class Record
        {
            public long finished_at_ms;
            // 0 when the compile started before this connector subscribed, or never started.
            public long started_at_ms;
            // 0 when no earlier compile was recorded, which makes "changed since" unanswerable
            // (counting every file in the project would be noise, not a count of what changed).
            public long previous_finished_at_ms;
            public List<string> rebuilt = new();
            public bool rebuilt_truncated;
        }

        internal sealed class SourceScan
        {
            public bool have_previous;
            public int changed_since_previous;
            public int stale_count;
            public readonly List<string> stale_files = new();
            public bool stale_truncated;
            public int scanned_files;
            public int scan_ms;
            public bool complete = true;
            public List<string> roots = new();
        }

        static CompileProvenance()
        {
            Load();
        }

        // ─── Compilation pipeline feed (called by CompilationGate) ─────────

        internal static void NoteCompileStarted()
        {
            s_PendingStartedMs = NowMs();
            s_RebuiltNames.Clear();
            s_RebuiltTruncated = false;
        }

        internal static void NoteAssemblyCompiled(string assemblyPath)
        {
            var name = Path.GetFileNameWithoutExtension(assemblyPath);
            if (string.IsNullOrEmpty(name) || s_RebuiltNames.Contains(name)) return;
            if (s_RebuiltNames.Count >= MaxRebuiltNames)
            {
                s_RebuiltTruncated = true;
                return;
            }
            s_RebuiltNames.Add(name);
        }

        /// <param name="afterReload">true when called from afterAssemblyReload instead of
        /// compilationFinished. A reload's own domain never sees the compilation events again, so
        /// that call re-stamps the finish time of the compile that already updated the record —
        /// keeping this clock in step with CompilationGate.LastCompileFinishedAt, which the CLI
        /// polls from the heartbeat.</param>
        internal static void NoteCompileFinished(bool afterReload)
        {
            var now = NowMs();
            if (!afterReload || s_Record.finished_at_ms == 0)
            {
                s_Record.previous_finished_at_ms = s_Record.finished_at_ms;
                s_Record.started_at_ms = s_PendingStartedMs;
                s_Record.rebuilt = new List<string>(s_RebuiltNames);
                s_Record.rebuilt_truncated = s_RebuiltTruncated;
            }
            s_Record.finished_at_ms = now;
            s_PendingStartedMs = 0;
            Save();
        }

        // ─── Payload ──────────────────────────────────────────────────────

        /// The object every compile-related result carries: compile_status returns it flat, and
        /// compile_diagnostics nests it under "provenance". The Go CLI prints provenance_line and
        /// stale_line verbatim, so the wording lives here only.
        public static object BuildPayload(bool scan, int scanBudgetMs)
        {
            SourceScan s = null;
            AssemblyStaleness.ScanResult assemblies = null;
            if (scan)
            {
                s = ScanSourceTree(scanBudgetMs);
                assemblies = AssemblyStaleness.ScanProject(scanBudgetMs);
            }
            return BuildPayloadFrom(s_Record, s, NowMs(), assemblies);
        }

        /// The provenance sentence without the source scan, for callers that only need to say
        /// how old the compile behind an answer is (the compile gate's error message).
        public static string DescribeLastCompile() => FormatProvenanceLine(s_Record, null, NowMs());

        /// When the last observed script compilation started, or its finish if this domain never saw
        /// its start. Sources written before that moment were inputs that compile could read, which is
        /// what lets AssemblyStaleness tell "the edit was never compiled" from "the compile read the
        /// edit and kept the old DLL".
        internal static long LastCompileStartedAtMs() =>
            s_Record.started_at_ms > 0 ? s_Record.started_at_ms : s_Record.finished_at_ms;

        /// True when LastCompileStartedAtMs had to fall back to the finish clock, so a message naming
        /// it must not claim that time is a start.
        internal static bool LastCompileClockFromFinish() =>
            s_Record.started_at_ms <= 0 && s_Record.finished_at_ms > 0;

        internal static object BuildPayloadFrom(Record record, SourceScan scan, long nowMs,
            AssemblyStaleness.ScanResult assemblies = null)
        {
            var ageMs = record.finished_at_ms > 0 ? Math.Max(0, nowMs - record.finished_at_ms) : 0;
            return new
            {
                compiled_at_ms = record.finished_at_ms,
                compiled_at = record.finished_at_ms > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(record.finished_at_ms)
                        .ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)
                    : null,
                compile_started_at_ms = record.started_at_ms,
                // Spans the domain reload the compile triggers, which is the wait the caller feels.
                duration_ms = record.started_at_ms > 0 && record.finished_at_ms > record.started_at_ms
                    ? record.finished_at_ms - record.started_at_ms
                    : 0,
                age_seconds = Math.Round(ageMs / 1000.0, 1),
                rebuilt_assemblies = record.rebuilt ?? new List<string>(),
                rebuilt_truncated = record.rebuilt_truncated,
                changed_since_previous_compile = scan is { have_previous: true } ? (int?)scan.changed_since_previous : null,
                stale_count = scan?.stale_count ?? 0,
                stale_files = scan?.stale_files ?? new List<string>(),
                stale_truncated = scan?.stale_truncated ?? false,
                stale_assembly_count = assemblies?.StaleCount ?? 0,
                stale_assemblies = AssemblyStaleness.ToPayload(assemblies),
                stale_assemblies_truncated = assemblies?.Truncated ?? false,
                stale_assembly_scan_complete = assemblies?.Complete ?? false,
                stale_assembly_line = AssemblyStaleness.FormatLine(assemblies),
                stale_assembly_covered_count = assemblies?.CoveredCount ?? 0,
                stale_assembly_covered_line = AssemblyStaleness.FormatCoveredLine(assemblies),
                scanned_roots = scan?.roots ?? new List<string>(),
                scanned_files = scan?.scanned_files ?? 0,
                scan_ms = scan?.scan_ms ?? 0,
                scan_complete = scan?.complete ?? false,
                scan_note = ScanNote(scan),
                provenance_line = FormatProvenanceLine(record, scan, nowMs),
                stale_line = FormatStaleLine(scan),
            };
        }

        static string ScanNote(SourceScan scan)
        {
            if (scan == null) return "source tree not scanned";
            if (scan.complete) return null;
            return $"source scan stopped after {scan.scan_ms} ms ({scan.scanned_files} source files " +
                   $"checked); counts are a lower bound — raise --scan_budget to finish it";
        }

        /// compiled 14:02:11 (3 s ago, took 4 s) · 3 source files changed since previous compile ·
        /// rebuilt: Game, Game.Tests.Editor — clauses for anything unknown are left out rather than
        /// guessed.
        internal static string FormatProvenanceLine(Record record, SourceScan scan, long nowMs)
        {
            if (record.finished_at_ms <= 0)
                return "compiled: unknown — no script compilation has finished since this editor session started";

            var sb = new StringBuilder();
            sb.Append("compiled ")
                .Append(DateTimeOffset.FromUnixTimeMilliseconds(record.finished_at_ms)
                    .ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            sb.Append(" (").Append(AgoText(nowMs - record.finished_at_ms));
            if (record.started_at_ms > 0 && record.finished_at_ms > record.started_at_ms)
                sb.Append(", took ").Append(DurationText(record.finished_at_ms - record.started_at_ms));
            sb.Append(')');

            if (scan is { have_previous: true })
            {
                sb.Append(" · ").Append(scan.changed_since_previous)
                    .Append(scan.changed_since_previous == 1 ? " source file" : " source files")
                    .Append(" changed since previous compile");
            }

            if (record.rebuilt is { Count: > 0 })
            {
                var shown = Math.Min(record.rebuilt.Count, MaxStaleNames);
                sb.Append(" · rebuilt: ").Append(string.Join(", ", record.rebuilt.GetRange(0, shown)));
                var hidden = record.rebuilt.Count - shown;
                if (hidden > 0 || record.rebuilt_truncated) sb.Append(" (+more)");
            }

            return sb.ToString();
        }

        internal static string FormatStaleLine(SourceScan scan)
        {
            if (scan == null || scan.stale_count == 0) return null;

            var hidden = scan.stale_count - scan.stale_files.Count;
            var suffix = scan.complete ? "" : " (scan stopped early)";
            return "stale: " + string.Join(", ", scan.stale_files) +
                   (hidden > 0 ? $" (+{hidden} more)" : "") +
                   $" modified after this compile{suffix} — run verify again";
        }

        static string AgoText(long ms)
        {
            if (ms < 1000) return "just now";
            var s = ms / 1000;
            if (s < 60) return s + " s ago";
            if (s < 3600) return s / 60 + " min ago";
            return s / 3600 + " h ago";
        }

        static string DurationText(long ms)
        {
            if (ms < 1000) return ms + " ms";
            if (ms < 10_000)
                return (ms / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s";
            if (ms < 60_000) return ms / 1000 + " s";
            return ms / 60_000 + " min";
        }

        // ─── Source tree scan ─────────────────────────────────────────────

        /// Counts .cs files written since the previous compile finished, and lists sources (.cs,
        /// .asmdef, .asmref) written after the reported compile finished. Runs on the calling thread
        /// with a wall-clock budget; the counts stay a lower bound when it runs out.
        internal static SourceScan ScanSourceTree(int budgetMs)
        {
            var roots = SourceRoots();
            var projectRoot = ProjectRoot();
            return ScanRoots(roots, projectRoot, s_Record.previous_finished_at_ms, s_Record.finished_at_ms, budgetMs);
        }

        internal static SourceScan ScanRoots(IReadOnlyList<string> roots, string projectRoot,
            long previousMs, long finishedMs, int budgetMs)
        {
            var scan = new SourceScan { have_previous = previousMs > 0 };
            foreach (var root in roots) scan.roots.Add(DisplayPath(root, projectRoot));

            var watch = Stopwatch.StartNew();
            var stack = new Stack<string>();
            foreach (var root in roots)
                if (Directory.Exists(root)) stack.Push(root);

            var visited = 0;
            var outOfBudget = false;
            while (stack.Count > 0 && !outOfBudget)
            {
                var infos = EnumerateEntries(stack.Pop());
                foreach (var info in infos)
                {
                    if (++visited % DeadlineCheckEvery == 0 && watch.ElapsedMilliseconds >= budgetMs)
                    {
                        outOfBudget = true;
                        break;
                    }

                    var name = info.Name;
                    if (name.Length > 0 && name[0] == '.') continue;

                    if ((info.Attributes & FileAttributes.Directory) != 0)
                    {
                        stack.Push(info.FullName);
                        continue;
                    }

                    var ext = info.Extension.ToLowerInvariant();
                    var isSource = ext == ".cs";
                    var isAssemblyDef = ext == ".asmdef" || ext == ".asmref";
                    if (!isSource && !isAssemblyDef) continue;

                    scan.scanned_files++;
                    var wroteMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                    if (isSource && scan.have_previous && wroteMs > previousMs) scan.changed_since_previous++;
                    if (finishedMs <= 0 || wroteMs <= finishedMs) continue;

                    scan.stale_count++;
                    if (scan.stale_files.Count < MaxStaleNames) scan.stale_files.Add(DisplayPath(info.FullName, projectRoot));
                    else scan.stale_truncated = true;
                }
            }

            scan.complete = !outOfBudget;
            scan.scan_ms = (int)watch.ElapsedMilliseconds;
            return scan;
        }

        /// FileSystemInfos come out of one directory read per folder, so attributes and timestamps
        /// are already stat'ed — that is what keeps the scan inside its budget on a large project.
        static IEnumerable<FileSystemInfo> EnumerateEntries(string dir)
        {
            try
            {
                return new DirectoryInfo(dir).EnumerateFileSystemInfos();
            }
            catch (Exception)
            {
                // A package folder can vanish mid-scan (package resolution, another build); the
                // counts are approximate by design, so skipping it beats failing the tool call.
                return Array.Empty<FileSystemInfo>();
            }
        }

        /// Sources the user can edit: Assets/, plus embedded and file:/local packages. Registry and
        /// git packages live under Library/PackageCache — third-party code nobody edits, and walking
        /// it would spend the whole budget answering "is your code stale?".
        internal static List<string> SourceRoots()
        {
            var roots = new List<string>();
            AddRoot(roots, Application.dataPath);
            foreach (var pkg in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (pkg.source != UnityEditor.PackageManager.PackageSource.Embedded &&
                    pkg.source != UnityEditor.PackageManager.PackageSource.Local)
                    continue;
                AddRoot(roots, pkg.resolvedPath);
            }
            return roots;
        }

        static void AddRoot(List<string> roots, string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path) || roots.Contains(path)) return;
            roots.Add(path);
        }

        static string ProjectRoot()
        {
            return Application.dataPath.Replace("/Assets", "");
        }

        static string DisplayPath(string path, string projectRoot)
        {
            var normalized = path.Replace('\\', '/');
            var root = (projectRoot ?? "").TrimEnd('/').Replace('\\', '/');
            if (root.Length > 0 && normalized.StartsWith(root + "/", StringComparison.Ordinal))
                return normalized.Substring(root.Length + 1);
            return normalized;
        }

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ─── Persistence across the domain reload a compile ends with ─────

        static void Save()
        {
            try
            {
                SessionState.SetString(SessionKey, JsonConvert.SerializeObject(s_Record));
            }
            catch (Exception)
            {
                // Provenance is a diagnostic; losing it must not break a compile notification.
            }
        }

        static void Load()
        {
            try
            {
                var json = SessionState.GetString(SessionKey, null);
                if (!string.IsNullOrEmpty(json)) s_Record = JsonConvert.DeserializeObject<Record>(json) ?? new Record();
                s_Record.rebuilt ??= new List<string>();
            }
            catch (Exception)
            {
                s_Record = new Record();
            }
        }

        // ─── Testing hooks ────────────────────────────────────────────────

        internal static Record SnapshotForTests() => s_Record;

        internal static void RestoreSessionForTests(string json)
        {
            if (json == null) SessionState.EraseString(SessionKey);
            else SessionState.SetString(SessionKey, json);
        }

        internal static string DumpSessionForTests() => SessionState.GetString(SessionKey, null);

        internal static void ResetForTests()
        {
            s_Record = new Record();
            s_PendingStartedMs = 0;
            s_RebuiltNames.Clear();
            s_RebuiltTruncated = false;
        }

        internal static void ReloadFromSessionForTests() => Load();
    }
}
