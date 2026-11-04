using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCliConnector
{
    /// Compares each project assembly's output DLL with the newest source it was built from. Unity's
    /// compilation cache (Bee) can decide an assembly is up to date from an import it never saw, so a
    /// successful request is not proof that the DLL contains the caller's edit; this is the proof.
    public static class AssemblyStaleness
    {
        /// File stats run on Unity's main thread with every other tool call, so the scan stops at a
        /// deadline instead of walking a large project to the end. A scan that stopped early is never
        /// reported as clean.
        public const int DefaultBudgetMs = 150;
        public const int MaxListed = 5;

        const int DeadlineCheckEvery = 64;

        public sealed class AssemblyInput
        {
            public string Name;
            public string OutputPath;
            public string[] Sources;
        }

        public sealed class StaleAssembly
        {
            public string Name;
            public string Output;
            // Null when the DLL is missing rather than old.
            public long? DllMtimeMs;
            public string NewestSource;
            public long NewestSourceMtimeMs;
            public bool Missing;
        }

        public sealed class ScanResult
        {
            public readonly List<StaleAssembly> Stale = new();
            public int StaleCount;
            // Sources newer than their DLL that the last compile still had time to see. Unity left the
            // DLL alone on purpose (identical contents), which is not a state to refuse tests over, but
            // it is not a clean bill of health either, so it is counted and named rather than hidden.
            public int CoveredCount;
            public StaleAssembly CoveredSample;
            public bool Truncated;
            public bool Complete = true;
            public int ScannedAssemblies;
            public int ScanMs;
            public string ProjectRoot;
            // Echo of the compile clock Evaluate judged the sources against, so the report can name the
            // compile that decided a DLL was already current.
            public long CoveredSinceMs;
        }

        /// The assemblies Unity would run tests against and build the player from. Registry and git
        /// packages are skipped: their DLLs are not the caller's to rebuild.
        public static ScanResult ScanProject(int budgetMs, ISet<string> onlyAssemblies = null)
        {
            var roots = CompileProvenance.SourceRoots();
            var projectRoot = ProjectRoot();
            var inputs = new List<AssemblyInput>();
            var seen = new HashSet<string>();

            foreach (var type in new[] { AssembliesType.Editor, AssembliesType.Player })
            {
                foreach (var assembly in CompilationPipeline.GetAssemblies(type))
                {
                    if (onlyAssemblies != null && !onlyAssemblies.Contains(assembly.name))
                        continue;
                    if (string.IsNullOrEmpty(assembly.outputPath))
                        continue;
                    var key = assembly.name + "|" + assembly.outputPath;
                    if (!seen.Add(key))
                        continue;

                    var sources = new List<string>();
                    if (assembly.sourceFiles != null)
                        foreach (var source in assembly.sourceFiles)
                            AddSource(sources, ResolvePath(source, projectRoot, ResolveVirtualPath));
                    var definition = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                    AddSource(sources, ResolvePath(definition, projectRoot, ResolveVirtualPath));
                    if (!IsProjectAssembly(sources, roots))
                        continue;

                    inputs.Add(new AssemblyInput
                    {
                        Name = assembly.name,
                        OutputPath = ResolvePath(assembly.outputPath, projectRoot, null),
                        Sources = sources.ToArray(),
                    });
                }
            }

            return Evaluate(inputs, projectRoot, budgetMs, CompileProvenance.LastCompileStartedAtMs());
        }

        /// Pure file-stat helper: no Unity API, so EditMode tests can point it at a temp directory.
        /// <param name="coveredSinceMs">When the last finished script compilation started (0 when this
        /// editor session never saw one). A source written before that was an input the compile could
        /// read, so a DLL left at its old mtime afterwards is Unity's cache decision and is counted as
        /// covered, not stale; without this, an mtime-only change (a `touch`, a re-checkout) would be
        /// reported as stale forever, because Unity does not rewrite a DLL whose contents it considers
        /// current — measured on 6000.6.5f1: a touched file produced an 11 s compile with
        /// rebuilt_assemblies empty and the DLL mtime unchanged, and a CleanBuildCache request (65 s)
        /// left those mtimes alone too, because Bee restores its cached node output with the timestamps
        /// it recorded. No rebuild settles an mtime-only change, so the compile clock is what decides.</param>
        public static ScanResult Evaluate(IReadOnlyList<AssemblyInput> assemblies, string projectRoot, int budgetMs,
            long coveredSinceMs = 0)
        {
            var result = new ScanResult { ProjectRoot = projectRoot, CoveredSinceMs = coveredSinceMs };
            var watch = Stopwatch.StartNew();

            foreach (var assembly in assemblies)
            {
                if (watch.ElapsedMilliseconds >= budgetMs)
                {
                    result.Complete = false;
                    break;
                }

                result.ScannedAssemblies++;
                long? dllMs = null;
                if (!string.IsNullOrEmpty(assembly.OutputPath) && File.Exists(assembly.OutputPath))
                    dllMs = ToMs(File.GetLastWriteTimeUtc(assembly.OutputPath));

                long newestMs = long.MinValue;
                string newestSource = null;
                var checkedFiles = 0;
                if (assembly.Sources != null)
                {
                    foreach (var source in assembly.Sources)
                    {
                        if (string.IsNullOrEmpty(source) || !File.Exists(source))
                            continue;
                        if (++checkedFiles % DeadlineCheckEvery == 0 && watch.ElapsedMilliseconds >= budgetMs)
                        {
                            result.Complete = false;
                            break;
                        }
                        var wroteMs = ToMs(File.GetLastWriteTimeUtc(source));
                        if (wroteMs <= newestMs)
                            continue;
                        newestMs = wroteMs;
                        newestSource = source;
                    }
                }

                if (newestSource == null)
                    continue;
                if (dllMs.HasValue && dllMs.Value >= newestMs)
                    continue;

                var sample = new StaleAssembly
                {
                    Name = assembly.Name,
                    Output = DisplayPath(assembly.OutputPath, projectRoot),
                    DllMtimeMs = dllMs,
                    NewestSource = DisplayPath(newestSource, projectRoot),
                    NewestSourceMtimeMs = newestMs,
                    Missing = !dllMs.HasValue,
                };
                if (dllMs.HasValue && coveredSinceMs > newestMs)
                {
                    result.CoveredCount++;
                    result.CoveredSample ??= sample;
                    continue;
                }

                result.StaleCount++;
                if (result.Stale.Count >= MaxListed)
                {
                    result.Truncated = true;
                    continue;
                }
                result.Stale.Add(sample);
            }

            result.ScanMs = (int)watch.ElapsedMilliseconds;
            return result;
        }

        public static List<object> ToPayload(ScanResult result)
        {
            var list = new List<object>();
            if (result == null)
                return list;
            foreach (var stale in result.Stale)
            {
                var dllMs = stale.DllMtimeMs;
                list.Add(new
                {
                    name = stale.Name,
                    output = stale.Output,
                    dll_mtime = dllMs.HasValue ? Iso(dllMs.Value) : null,
                    dll_mtime_ms = dllMs,
                    newest_source = stale.NewestSource,
                    newest_source_mtime = Iso(stale.NewestSourceMtimeMs),
                    newest_source_mtime_ms = stale.NewestSourceMtimeMs,
                    missing = stale.Missing,
                });
            }
            return list;
        }

        /// Says which assemblies the last compile chose not to rebuild even though their DLL predates
        /// their sources. Kept separate from FormatLine so a covered assembly never reads as a refusal.
        public static string FormatCoveredLine(ScanResult result)
        {
            if (result == null || result.CoveredCount == 0 || result.CoveredSample == null)
                return null;

            var first = result.CoveredSample;
            var dll = first.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? first.Name : first.Name + ".dll";
            var more = result.CoveredCount > 1 ? $" (+{result.CoveredCount - 1} more)" : "";
            return $"not stale: {dll} (built {Clock(first.DllMtimeMs ?? 0)}) is older than {first.NewestSource} " +
                   $"({Clock(first.NewestSourceMtimeMs)}){more}, but the compile that started " +
                   $"{Clock(result.CoveredSinceMs)} read that source and kept the DLL; a moved " +
                   "mtime alone does not mean the DLL is out of date";
        }

        /// One sentence naming the first stale assembly, in the wording agents act on. It points at the
        /// refresh that re-runs the compile; `--compile clean` is the variant that also discards
        /// Unity's compiled-assembly cache.
        public static string FormatLine(ScanResult result)
        {
            if (result == null || result.StaleCount == 0)
                return null;

            var first = result.Stale.Count > 0 ? result.Stale[0] : null;
            if (first == null)
                return $"{result.StaleCount} project assembly(ies) are older than their sources — run unity-cli editor refresh --compile";
            var dll = first.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? first.Name : first.Name + ".dll";
            var source = $"{first.NewestSource} ({Clock(first.NewestSourceMtimeMs)})";
            var built = first.Missing ? "missing" : $"built {Clock(first.DllMtimeMs ?? 0)}";
            var more = result.StaleCount > 1 ? $" (+{result.StaleCount - 1} more)" : "";
            return $"stale assembly: {dll} ({built}) is older than {source}{more} — run unity-cli editor refresh --compile";
        }

        static void AddSource(List<string> sources, string path)
        {
            if (!string.IsNullOrEmpty(path) && !sources.Contains(path))
                sources.Add(path);
        }

        /// CompilationPipeline hands out project-relative paths — "Assets/...", "Packages/<pkg>/...",
        /// "Library/ScriptAssemblies/<asm>.dll" — and "Packages/<pkg>" is virtual: it names where a
        /// registry, git or `file:` package is mounted, not a directory of this project. Comparing them
        /// against absolute source roots matches nothing (that is how every assembly got filtered out of
        /// the scan), and File.* only resolves the relative form through the process CWD, which is the
        /// project root only when the editor was started from there.
        /// <param name="virtualResolver">Resolves a "Packages/..." asset path to a real path, or returns
        /// null when it is not under a package.</param>
        internal static string ResolvePath(string path, string projectRoot, Func<string, string> virtualResolver)
        {
            var normalized = Normalize(path);
            if (normalized.Length == 0 || Path.IsPathRooted(normalized))
                return normalized;

            if (normalized.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var viaPackage = virtualResolver?.Invoke(normalized);
                if (!string.IsNullOrEmpty(viaPackage))
                    return Normalize(viaPackage);
            }
            return Combine(projectRoot, normalized);
        }

        static string ResolveVirtualPath(string assetPath)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            if (package == null || string.IsNullOrEmpty(package.assetPath) || string.IsNullOrEmpty(package.resolvedPath))
                return null;

            var rest = assetPath.Length > package.assetPath.Length
                ? assetPath.Substring(package.assetPath.Length)
                : "";
            return Combine(package.resolvedPath, rest);
        }

        internal static bool IsProjectAssembly(List<string> sources, IReadOnlyList<string> roots)
        {
            foreach (var source in sources)
            {
                var path = Normalize(source);
                foreach (var root in roots)
                {
                    var normalizedRoot = Normalize(root).TrimEnd('/');
                    if (path.StartsWith(normalizedRoot + "/", StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        static string ProjectRoot()
        {
            var assets = Normalize(Application.dataPath);
            var slash = assets.LastIndexOf('/');
            return slash > 0 ? assets.Substring(0, slash) : assets;
        }

        static string Normalize(string path) => (path ?? "").Replace('\\', '/');

        static string Combine(string root, string relative)
        {
            var normalizedRoot = Normalize(root).TrimEnd('/');
            var normalizedRelative = Normalize(relative).TrimStart('/');
            if (normalizedRoot.Length == 0) return normalizedRelative;
            if (normalizedRelative.Length == 0) return normalizedRoot;
            return normalizedRoot + "/" + normalizedRelative;
        }

        static string DisplayPath(string path, string projectRoot)
        {
            var normalized = Normalize(path);
            var root = Normalize(projectRoot).TrimEnd('/');
            if (root.Length > 0 && normalized.StartsWith(root + "/", StringComparison.Ordinal))
                return normalized.Substring(root.Length + 1);
            return normalized;
        }

        static long ToMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

        static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

        static string Clock(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms)
            .ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
