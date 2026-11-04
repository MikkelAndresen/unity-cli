using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
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
        const string ContentRecordFileName = "assembly-sources.json";

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
            // True when the source moved but no content record exists to prove its contents are what
            // the compile read; the compile clock is then the only evidence, so the line says so.
            public bool Unverified;
        }

        /// One assembly's source contents as the compile that produced its DLL saw them. The DLL path
        /// is part of the key so a reused assembly name in another output cannot borrow the record.
        public sealed class AssemblyContentRecord
        {
            public string Dll;
            public Dictionary<string, string> Sources = new();
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
            // The clock above is the finish when this domain never saw the start; the line must not
            // call a finish time a start.
            public bool CoveredClockIsFinish;
        }

        /// The assemblies Unity would run tests against and build the player from. Registry and git
        /// packages are skipped: their DLLs are not the caller's to rebuild.
        public static ScanResult ScanProject(int budgetMs, ISet<string> onlyAssemblies = null)
        {
            var roots = CompileProvenance.SourceRoots();
            var projectRoot = ProjectRoot();
            // Materialise the pipeline lists before the budget starts: GetAssemblies is one Unity
            // call costing the same whether or not this scan runs, while the per-source path work
            // below is what the deadline has to bound.
            var pipeline = new List<Assembly>();
            pipeline.AddRange(CompilationPipeline.GetAssemblies(AssembliesType.Editor));
            pipeline.AddRange(CompilationPipeline.GetAssemblies(AssembliesType.Player));
            var watch = Stopwatch.StartNew();
            var inputs = new List<AssemblyInput>();
            var seen = new HashSet<string>();
            var packageRoots = new Dictionary<string, string>(StringComparer.Ordinal);
            Func<string, string> resolveVirtual = path => ResolveVirtualPath(path, packageRoots);
            var truncated = false;

            foreach (var assembly in pipeline)
            {
                if (watch.ElapsedMilliseconds >= budgetMs)
                {
                    truncated = true;
                    break;
                }
                if (onlyAssemblies != null && !onlyAssemblies.Contains(assembly.name))
                    continue;
                if (string.IsNullOrEmpty(assembly.outputPath))
                    continue;
                var key = assembly.name + "|" + assembly.outputPath;
                if (!seen.Add(key))
                    continue;

                var sources = ResolveSources(assembly, projectRoot, resolveVirtual, watch, budgetMs,
                    out truncated);
                if (truncated)
                    break;
                if (!IsProjectAssembly(sources, roots))
                    continue;

                inputs.Add(new AssemblyInput
                {
                    Name = assembly.name,
                    OutputPath = ResolvePath(assembly.outputPath, projectRoot, null),
                    Sources = sources.ToArray(),
                });
            }

            var collectionMs = (int)watch.ElapsedMilliseconds;
            var remaining = Math.Max(0, budgetMs - collectionMs);
            var result = Evaluate(inputs, projectRoot, remaining,
                CompileProvenance.LastCompileStartedAtMs(), LoadContentRecords(), null,
                CompileProvenance.LastCompileClockFromFinish());
            if (truncated)
                result.Complete = false;
            result.ScanMs += collectionMs;
            return result;
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
        /// it recorded. No rebuild settles an mtime-only change, so the compile clock is what decides.
        /// <param name="contentRecords">Source hashes recorded when each assembly last compiled
        /// successfully. A candidate whose content matches its record is a touch, not an edit; one
        /// that differs is stale even when a compile has run since — the #29 case where the compile
        /// did not rewrite the DLL. Null means "no record", which falls back to the compile clock.</param>
        /// <param name="hashFile">Test seam; production hashes the file on disk.</param>
        /// <param name="coveredClockIsFinish">True when coveredSinceMs fell back to a compile finish.</param>
        public static ScanResult Evaluate(IReadOnlyList<AssemblyInput> assemblies, string projectRoot, int budgetMs,
            long coveredSinceMs = 0, IReadOnlyDictionary<string, AssemblyContentRecord> contentRecords = null,
            Func<string, string> hashFile = null, bool coveredClockIsFinish = false)
        {
            var result = new ScanResult
            {
                ProjectRoot = projectRoot,
                CoveredSinceMs = coveredSinceMs,
                CoveredClockIsFinish = coveredClockIsFinish,
            };
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
                var newerThanDll = new List<string>();
                var checkedFiles = 0;
                var stopped = false;
                if (assembly.Sources != null)
                {
                    foreach (var source in assembly.Sources)
                    {
                        if (string.IsNullOrEmpty(source) || !File.Exists(source))
                            continue;
                        if (++checkedFiles % DeadlineCheckEvery == 0 && watch.ElapsedMilliseconds >= budgetMs)
                        {
                            result.Complete = false;
                            stopped = true;
                            break;
                        }
                        var wroteMs = ToMs(File.GetLastWriteTimeUtc(source));
                        if (dllMs.HasValue && wroteMs > dllMs.Value)
                            newerThanDll.Add(source);
                        if (wroteMs <= newestMs)
                            continue;
                        newestMs = wroteMs;
                        newestSource = source;
                    }
                }

                if (stopped)
                    break;
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
                if (dllMs.HasValue)
                {
                    var sameContent = ContentMatches(assembly, newerThanDll, contentRecords, hashFile,
                        watch, budgetMs, ref result, out stopped);
                    if (stopped)
                        break;
                    if (sameContent == true)
                    {
                        result.CoveredCount++;
                        result.CoveredSample ??= sample;
                        continue;
                    }
                    if (sameContent == false)
                    {
                        // The content differs from what the last successful compile recorded: the
                        // compile clock cannot cover it even if it started after the edit (#29).
                    }
                    else if (coveredSinceMs > newestMs)
                    {
                        sample.Unverified = true;
                        result.CoveredCount++;
                        result.CoveredSample ??= sample;
                        continue;
                    }
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

        /// True/False when the record proves the candidate contents do or do not match; null when no
        /// record exists or a hash could not be read, which leaves the compile-clock rule to decide.
        static bool? ContentMatches(AssemblyInput assembly, List<string> candidates,
            IReadOnlyDictionary<string, AssemblyContentRecord> records, Func<string, string> hashFile,
            Stopwatch watch, int budgetMs, ref ScanResult result, out bool stopped)
        {
            stopped = false;
            if (records == null || candidates.Count == 0 ||
                !records.TryGetValue(ContentRecordKey(assembly.Name, assembly.OutputPath), out var record) ||
                record?.Sources == null)
                return null;

            var hashed = 0;
            foreach (var candidate in candidates)
            {
                if (++hashed % DeadlineCheckEvery == 0 && watch.ElapsedMilliseconds >= budgetMs)
                {
                    result.Complete = false;
                    stopped = true;
                    return null;
                }
                if (!record.Sources.TryGetValue(candidate, out var expected))
                    return false;
                var actual = hashFile != null ? hashFile(candidate) : ComputeFileHash(candidate);
                if (string.IsNullOrEmpty(actual))
                    return null;
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
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
            if (!first.Unverified)
            {
                return $"not stale: {dll} (built {Clock(first.DllMtimeMs ?? 0)}) is older than {first.NewestSource} " +
                       $"({Clock(first.NewestSourceMtimeMs)}){more}, but its content matches what the compile " +
                       "recorded for it; a moved mtime alone does not mean the DLL is out of date";
            }

            var compile = result.CoveredClockIsFinish ? "finished" : "started";
            return $"not stale (unverified, no content record): {dll} (built {Clock(first.DllMtimeMs ?? 0)}) " +
                   $"is older than {first.NewestSource} ({Clock(first.NewestSourceMtimeMs)}){more}, but the " +
                   $"compile that {compile} {Clock(result.CoveredSinceMs)} read that source and kept the DLL; " +
                   "a moved mtime alone does not mean the DLL is out of date";
        }

        /// The runner's refusal: a stale DLL runs old code, and an incomplete scan cannot prove the
        /// DLLs are current either. Null means it is safe to run. `--allow-stale` is the caller's
        /// escape hatch for a deliberately old assembly (e.g. bisecting a test runner change).
        public static object BlockIfStaleOrIncomplete(ScanResult scan)
        {
            if (scan == null || (scan.StaleCount == 0 && scan.Complete))
                return null;
            if (scan.StaleCount > 0)
            {
                var line = FormatLine(scan);
                var staleMessage = line + " Tests were not run; pass --allow-stale to run them anyway.";
                return new ErrorResponse(staleMessage, new
                {
                    blocked = "stale-assembly",
                    message = staleMessage,
                    stale_assembly_count = scan.StaleCount,
                    stale_assemblies = ToPayload(scan),
                    stale_assembly_line = line,
                    stale_assembly_scan_complete = scan.Complete,
                });
            }

            var message = $"Assembly staleness scan stopped after {scan.ScanMs} ms before checking every assembly; " +
                          "tests were not run because the DLLs under test could not be proved current. " +
                          "Pass --allow-stale to run them anyway.";
            return new ErrorResponse(message, new
            {
                blocked = "stale-assembly-scan-incomplete",
                message,
                stale_assembly_count = scan.StaleCount,
                stale_assemblies = ToPayload(scan),
                stale_assembly_scan_complete = scan.Complete,
                scan_ms = scan.ScanMs,
            });
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

        static List<string> ResolveSources(Assembly assembly, string projectRoot, Func<string, string> resolveVirtual,
            Stopwatch watch, int budgetMs, out bool truncated)
        {
            truncated = false;
            var sources = new List<string>();
            var seenSources = new HashSet<string>(StringComparer.Ordinal);
            var checkedPaths = 0;
            if (assembly.sourceFiles != null)
            {
                foreach (var source in assembly.sourceFiles)
                {
                    if (watch != null && ++checkedPaths % DeadlineCheckEvery == 0 &&
                        watch.ElapsedMilliseconds >= budgetMs)
                    {
                        truncated = true;
                        break;
                    }
                    AddSource(sources, seenSources, ResolvePath(source, projectRoot, resolveVirtual));
                }
            }
            if (!truncated)
            {
                var definition = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                AddSource(sources, seenSources, ResolvePath(definition, projectRoot, resolveVirtual));
            }
            return sources;
        }

        static void AddSource(List<string> sources, HashSet<string> seen, string path)
        {
            if (!string.IsNullOrEmpty(path) && seen.Add(path))
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

        static string ResolveVirtualPath(string assetPath, Dictionary<string, string> cache)
        {
            var packageRoot = PackageAssetRoot(assetPath);
            if (cache != null && cache.TryGetValue(packageRoot, out var cached))
                return cached.Length == 0 ? null : Combine(cached, assetPath.Substring(packageRoot.Length));

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            var resolved = package == null || string.IsNullOrEmpty(package.assetPath) ||
                           string.IsNullOrEmpty(package.resolvedPath)
                ? ""
                : package.resolvedPath;
            if (cache != null)
                cache[packageRoot] = resolved;
            if (resolved.Length == 0)
                return null;

            var rest = assetPath.Length > package.assetPath.Length
                ? assetPath.Substring(package.assetPath.Length)
                : "";
            return Combine(resolved, rest);
        }

        /// "Packages/<name>" — the package mount, not the file, so one lookup serves every source in it.
        static string PackageAssetRoot(string assetPath)
        {
            const string prefix = "Packages/";
            var slash = assetPath.IndexOf('/', prefix.Length);
            return slash >= 0 ? assetPath.Substring(0, slash) : assetPath;
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

        // ─── Content records: what the last successful compile of each assembly read ─────

        internal static string ContentRecordKey(string name, string outputPath) =>
            (name ?? "") + "|" + Normalize(outputPath);

        /// Called from CompilationGate's assemblyCompilationFinished when that assembly had no errors.
        /// Records the hashes now so a later edit can be told apart from a touched mtime even when the
        /// compile that follows does not rewrite the DLL.
        internal static void NoteAssemblyCompiled(string assemblyPath)
        {
            try
            {
                var projectRoot = ProjectRoot();
                var packageRoots = new Dictionary<string, string>(StringComparer.Ordinal);
                Func<string, string> resolveVirtual = path => ResolveVirtualPath(path, packageRoots);
                var target = ResolvePath(assemblyPath, projectRoot, resolveVirtual);
                var roots = CompileProvenance.SourceRoots();

                foreach (var type in new[] { AssembliesType.Editor, AssembliesType.Player })
                {
                    foreach (var assembly in CompilationPipeline.GetAssemblies(type))
                    {
                        if (string.IsNullOrEmpty(assembly.outputPath))
                            continue;
                        var output = ResolvePath(assembly.outputPath, projectRoot, resolveVirtual);
                        if (!string.Equals(output, target, StringComparison.Ordinal))
                            continue;

                        var sources = ResolveSources(assembly, projectRoot, resolveVirtual, null, 0, out _);
                        if (!IsProjectAssembly(sources, roots))
                            return;

                        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var source in sources)
                        {
                            if (!File.Exists(source))
                                continue;
                            var hash = ComputeFileHash(source);
                            if (hash == null)
                                return;
                            hashes[source] = hash;
                        }

                        var records = LoadContentRecords();
                        records[ContentRecordKey(assembly.name, output)] = new AssemblyContentRecord
                        {
                            Dll = output,
                            Sources = hashes,
                        };
                        SaveContentRecords();
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // Recording is an optimisation for the staleness check; losing it must not break the
                // compile event it rides on.
            }
        }

        sealed class ContentRecordFile
        {
            public int version = 1;
            public Dictionary<string, AssemblyContentRecord> assemblies = new();
        }

        static Dictionary<string, AssemblyContentRecord> s_ContentRecords;

        internal static Dictionary<string, AssemblyContentRecord> LoadContentRecords()
        {
            if (s_ContentRecords != null)
                return s_ContentRecords;
            s_ContentRecords = new Dictionary<string, AssemblyContentRecord>(StringComparer.Ordinal);
            try
            {
                var path = ContentRecordPath();
                if (File.Exists(path))
                {
                    var file = JsonConvert.DeserializeObject<ContentRecordFile>(File.ReadAllText(path));
                    if (file?.assemblies != null)
                        s_ContentRecords = file.assemblies;
                }
            }
            catch (Exception)
            {
                // A corrupt record costs the content proof only; the compile-clock fallback remains.
            }
            return s_ContentRecords;
        }

        static void SaveContentRecords()
        {
            try
            {
                var path = ContentRecordPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(new ContentRecordFile
                {
                    assemblies = s_ContentRecords,
                }));
            }
            catch (Exception)
            {
            }
        }

        static string ContentRecordPath() =>
            Combine(ProjectRoot(), "Library/unity-cli/" + ContentRecordFileName);

        internal static string ComputeFileHash(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
            catch (Exception)
            {
                return null;
            }
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
