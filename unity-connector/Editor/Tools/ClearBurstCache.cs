using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "clear_burst_cache", Description = "Delete the project's Burst compilation cache so the next Burst compile rebuilds it.", SkipCompilationGate = true)]
    public static class ClearBurstCache
    {
        public class Parameters
        {
            [ToolParameter("all (default) = whole Library/BurstCache, jit = only the JIT library cache")]
            public string Scope { get; set; }

            [ToolParameter("Report what would be deleted without deleting anything")]
            public bool DryRun { get; set; }
        }

        internal const string LibraryFolder = "Library";
        internal const string CacheFolder = "BurstCache";
        internal const string JitFolder = "JIT";

        public static object HandleCommand(JObject parameters)
        {
            var p = new ToolParams(parameters ?? new JObject());
            var scope = (p.Get("scope") ?? "all").Trim().ToLowerInvariant();
            var dryRun = p.GetBool("dry_run");

            if (scope != "all" && scope != "jit")
                return new ErrorResponse($"Unknown scope '{scope}'. Use all or jit.");

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(projectRoot))
                return new ErrorResponse("Cannot resolve the project root; refusing to delete anything.");

            ClearReport report;
            try
            {
                report = Clear(projectRoot, scope == "jit", dryRun);
            }
            catch (Exception e) when (e is InvalidOperationException || e is IOException || e is UnauthorizedAccessException)
            {
                return new ErrorResponse($"Burst cache clear failed: {e.Message}");
            }

            var data = new
            {
                target = report.Target,
                scope,
                dry_run = dryRun,
                existed = report.Existed,
                removed = report.Removed,
                files = report.Files,
                bytes = report.Bytes,
                size = FormatBytes(report.Bytes),
                failures = report.Failed,
            };

            // A cache directory that survived the clear means Burst still holds it (or the
            // OS refused a delete). Report that as a failure so scripts see exit 1 rather
            // than a success that freed nothing.
            if (!dryRun && report.Existed && !report.Removed)
                return new ErrorResponse(BuildMessage(report), data);

            return new SuccessResponse(BuildMessage(report), data);
        }

        internal sealed class ClearReport
        {
            public string Target;
            public bool DryRun;
            public bool Existed;
            public bool Removed;
            public int Files;
            public long Bytes;
            public readonly List<object> Failed = new List<object>();
        }

        internal static string CachePath(string projectRoot, bool jitOnly)
        {
            var cacheRoot = Path.Combine(Path.GetFullPath(projectRoot), LibraryFolder, CacheFolder);
            return jitOnly ? Path.Combine(cacheRoot, JitFolder) : cacheRoot;
        }

        /// Deletes only what Burst itself puts under <project>/Library/BurstCache: the JIT
        /// library cache (BurstCompilerOptions.DefaultCacheFolder) and the AotSettings*.hash
        /// files next to it. Both are pure cache — Burst recreates them on the next compile —
        /// but the recursion is what makes rm -rf attractive, so the path set here is built
        /// from constants and never from caller input.
        internal static ClearReport Clear(string projectRoot, bool jitOnly, bool dryRun)
        {
            var cacheRoot = Path.GetFullPath(Path.Combine(projectRoot, LibraryFolder, CacheFolder));
            var target = Path.GetFullPath(CachePath(projectRoot, jitOnly));

            var inCache = target == cacheRoot ||
                          target.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            if (!inCache)
                throw new InvalidOperationException($"{target} is outside {cacheRoot}; refusing to delete.");

            var report = new ClearReport
            {
                Target = target,
                DryRun = dryRun,
                Existed = Directory.Exists(target),
            };
            if (!report.Existed) return report;

            var files = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var size = SizeOf(file);
                if (!dryRun)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        // Most often a file Burst still has mapped. Report it and keep going
                        // rather than leaving the cache half-deleted with no account of it.
                        report.Failed.Add(new { path = file, error = e.Message });
                        continue;
                    }
                }
                report.Files++;
                report.Bytes += size;
            }

            if (!dryRun)
            {
                // Longest path first: children are always longer than their parent, so this
                // empties the tree bottom-up and Directory.Delete (non-recursive) either
                // succeeds or names the directory still holding a locked file.
                var dirs = Directory.GetDirectories(target, "*", SearchOption.AllDirectories);
                foreach (var dir in dirs.OrderByDescending(d => d.Length))
                    TryDeleteDir(dir, report);
                TryDeleteDir(target, report);

                report.Removed = !Directory.Exists(target);
            }

            return report;
        }

        static void TryDeleteDir(string dir, ClearReport report)
        {
            try
            {
                Directory.Delete(dir);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                report.Failed.Add(new { path = dir, error = e.Message });
            }
        }

        static long SizeOf(string file)
        {
            try
            {
                return new FileInfo(file).Length;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }

        static string BuildMessage(ClearReport report)
        {
            if (!report.Existed)
                return $"No Burst cache at {report.Target} — nothing to clear.";

            var amount = $"{report.Files} file(s), {FormatBytes(report.Bytes)}";

            if (report.DryRun)
                return $"Would delete {amount} from {report.Target}.";

            if (report.Failed.Count == 0)
                return $"Deleted {amount} from {report.Target}. Burst rebuilds it on the next compile.";

            return report.Removed
                ? $"Deleted {amount} from {report.Target}, but {report.Failed.Count} item(s) failed (see failures)."
                : $"Deleted {amount} from {report.Target}; {report.Failed.Count} item(s) are still in use, so the cache directory remains. Quit the editor and retry.";
        }

        internal static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{(long)value} {units[unit]}" : $"{value:0.#} {units[unit]}";
        }
    }
}
