using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityCliConnector.Tools
{
    /// Reports what Burst has compiled. Burst runs its own compiler outside Unity's script
    /// compilation, so "compile: ok" plus a quiet console does not prove a [BurstCompile] job was
    /// compiled — and a BCxxxx error can arrive after the compile finished.
    [UnityCliTool(
        Name = "burst_status",
        Description = "Burst compiler state: idle or compiling, optional bounded wait, and the [BurstCompile] targets in the " +
                      "project's own assemblies with compiled/failed/not-compiled-yet status plus Burst (BCxxxx) errors as " +
                      "file:line:col CODE message. Built only when com.unity.burst is installed.",
        // Burst errors show up as compile errors; gating the tool on them would hide the very
        // report it is being asked for.
        SkipCompilationGate = true)]
    public static class BurstStatus
    {
        public class Parameters
        {
            [ToolParameter("Seconds to wait for Burst to finish compiling (default 0 = answer immediately; with --compile the default is 60)")]
            public float Wait { get; set; }

            [ToolParameter("Force Burst to recompile its targets now, so statuses and errors come from this call instead of the last compile")]
            public bool Compile { get; set; }

            [ToolParameter("Only targets whose type or target name contains this")]
            public string Type { get; set; }

            [ToolParameter("Only targets in this namespace or below")]
            public string Namespace { get; set; }

            [ToolParameter("Only targets in these assemblies (comma-separated)")]
            public string Assembly { get; set; }

            [ToolParameter("Include targets in package assemblies, not just the project's own")]
            public bool IncludePackages { get; set; }

            [ToolParameter("List matched targets even when no filter was given (a filter implies listing)")]
            public bool List { get; set; }

            [ToolParameter("Maximum number of Burst errors and targets to list (default 20)")]
            public int Limit { get; set; }
        }

        private const float DefaultForceWait = 60f;
        private const int DefaultLimit = 20;

        public static async Task<object> HandleCommand(JObject parameters)
        {
            var p = new ToolParams(parameters ?? new JObject());
            var force = p.GetBool("compile");
            var waitSeconds = p.GetFloat("wait", null) ?? (force ? DefaultForceWait : 0f);
            if (waitSeconds < 0f) waitSeconds = 0f;
            var limit = p.GetInt("limit", DefaultLimit) ?? DefaultLimit;
            if (limit < 1) limit = 1;

            var burst = BurstIntrospection.Create();
            if (burst == null)
                return new ErrorResponse("Burst is installed but its compiler types are not loaded; cannot report Burst status.",
                    new { idle = false, error = "burst_types_missing" });

            var notes = new List<string>();
            if (!burst.CompilationEnabled())
                notes.Add("Burst compilation is disabled (menu Burst > Enable Compilation, or a build flag); nothing new will compile.");

            var forced = false;
            if (force)
            {
                forced = burst.ForceRecompile();
                if (!forced)
                    notes.Add("this Burst build does not expose TriggerRecompilation; reporting the last compile instead of a forced one");
            }

            var (idleKnown, idle, waitedMs, timedOut) = await WaitForIdle(burst, waitSeconds);

            var targets = burst.Targets();
            if (burst.TargetEnumerationFailed)
                notes.Add("Burst's target list could not be read; target counts and statuses are unknown, not zero.");

            var projectAssemblies = ProjectAssemblies();
            var projectAssemblyNames = new HashSet<string>(projectAssemblies.Keys, StringComparer.OrdinalIgnoreCase);
            var resolutionOrder = ResolutionOrder(projectAssemblies);
            foreach (var target in targets)
                ResolveAssembly(target, resolutionOrder);

            // A target belongs to the project by its resolved assembly name, not by having one:
            // Burst 1.8 sets Assembly from the job's own type, package targets included.
            var projectTargets = targets.Where(t => BurstTargets.IsProjectTarget(t, projectAssemblyNames)).ToList();
            var packageTargets = targets.Count - projectTargets.Count;
            var candidates = p.GetBool("include_packages") ? targets : projectTargets;

            // BurstError has no use for the hint field, so it does not pay for hint generation.
            var errors = BurstErrors.Collect(CompilationGate.GetLiveDiagnostics(false, -1), CompileErrors());

            var filterGiven = !string.IsNullOrEmpty(p.Get("type")) ||
                              !string.IsNullOrEmpty(p.Get("namespace")) ||
                              !string.IsNullOrEmpty(p.Get("assembly"));
            var listing = filterGiven || p.GetBool("list");

            // Filtered: the answer is the list of matches. Unfiltered: counts over every target in
            // the project's own assemblies, and the list stays out of the reply.
            var reported = listing
                ? candidates
                    .Where(t => BurstTargets.Matches(t, p.Get("type"), p.Get("namespace"), SplitAssemblies(p.Get("assembly"))))
                    .ToList()
                : candidates;

            foreach (var target in reported)
                target.Status = BurstTargets.StatusOf(target, errors, idleKnown, idle);

            BurstTargets.Count(reported, out var compiled, out var failed, out var notCompiled, out var unknown);

            var listed = listing
                ? reported.Take(limit).Select(t => new
                {
                    name = t.DisplayName,
                    full_name = t.FullName,
                    assembly = t.Assembly,
                    status = t.Status,
                }).ToList()
                : null;

            if (!idleKnown)
                notes.Add("this Burst build does not answer IsCurrentCompilationDone, so targets are reported as unknown rather than compiled");
            else if (burst.Eager() == BurstIntrospection.EagerState.NotScheduled && !forced)
                notes.Add("Burst has not scheduled an eager compile in this editor session; the statuses come from its idle queue after the last script compile");
            var unattributed = errors.Count(e => reported.All(t => !BurstErrors.PointsAt(e, t)));
            if (unattributed > 0)
                notes.Add($"{unattributed} Burst error(s) could not be attributed to a target");
            // Measured on Unity 6000.6.5f1 / Burst 2.0.0: Burst logs a method's error once and then
            // holds the result, so a console that was cleared (or overflowed) can be quiet about a
            // job that really does fail. Say so rather than implying "no errors".
            if (errors.Count == 0 && projectTargets.Count > 0 && !forced &&
                (unknown > 0 || notCompiled > 0) &&
                burst.Eager() == BurstIntrospection.EagerState.NotScheduled)
                notes.Add("no Burst errors in the console; Burst logs a method's error once per editor session, " +
                          "so an assembly that was not recompiled may not re-log one it already reported");

            var state = !idleKnown ? "unknown" : idle ? "idle" : "compiling";
            var message = $"Burst {state}: {compiled}/{reported.Count} targets compiled, " +
                          $"{errors.Count} Burst error(s).";

            return new SuccessResponse(message, new
            {
                idle,
                idle_known = idleKnown,
                compiling = EditorApplication.isCompiling,
                eager = EagerName(burst.Eager()),
                enabled = burst.CompilationEnabled(),
                forced,
                method = forced ? "forced_recompile" : "eager",
                waited_ms = waitedMs,
                wait_s = waitSeconds,
                timed_out = timedOut,
                burst_version = BurstVersion(),
                targets_total = targets.Count,
                project_targets = projectTargets.Count,
                package_targets = packageTargets,
                matched = reported.Count,
                listing,
                compiled,
                failed,
                not_compiled = notCompiled,
                unknown,
                error_count = errors.Count,
                errors = errors.Take(limit).Select(e => new
                {
                    file = e.File,
                    line = e.Line,
                    col = e.Col,
                    code = e.Code,
                    message = e.Message,
                    formatted = e.Format(),
                }).ToList(),
                targets = listed,
                notes,
            });
        }

        /// Burst writes its compile errors into the console with the position in a stack frame, so the
        /// head-only diagnostics CompilationGate merges are not enough to say where the error is.
        private static List<BurstConsoleEntry> CompileErrors()
        {
            var entries = new List<BurstConsoleEntry>();
            ConsoleLogEntries.TryForEachRawEntry((mode, message, file, line) =>
            {
                if ((mode & ConsoleLogEntries.CompileBlockingMask) != 0)
                    entries.Add(new BurstConsoleEntry(mode, message, file, line));
            });
            return entries;
        }

        /// Bounded wait on Burst's own queue, not on Unity's script compilation. Yields instead of
        /// sleeping: the handler runs on the main thread, so blocking it would also stop the script
        /// compile whose completion IsIdle is waiting for (and freeze the heartbeat and the UI).
        private static Task<(bool IdleKnown, bool Idle, int WaitedMs, bool TimedOut)> WaitForIdle(
            BurstIntrospection burst, float waitSeconds)
        {
            return BurstWait.ForIdleAsync(() => burst.CanQueryIdle, () => IsIdle(burst), waitSeconds);
        }

        static string EagerName(BurstIntrospection.EagerState state)
        {
            switch (state)
            {
                case BurstIntrospection.EagerState.NotScheduled: return "not-scheduled";
                case BurstIntrospection.EagerState.Scheduled: return "scheduled";
                case BurstIntrospection.EagerState.Completed: return "completed";
                default: return "unknown";
            }
        }

        static bool IsIdle(BurstIntrospection burst)
        {
            // A running script compile means Burst has not seen the new code yet.
            return !EditorApplication.isCompiling && burst.IsCompilationDone();
        }

        static string BurstVersion()
        {
            var package = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .FirstOrDefault(info => info.name == "com.unity.burst");
            return package?.version;
        }

        static IReadOnlyCollection<string> SplitAssemblies(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();
            return raw.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// Assembly name -> project (non-package) assembly, from the compile pipeline's own list so
        /// the set matches what the editor just compiled. A package assembly is one whose sources
        /// live under a registered package (Packages/, Library/PackageCache/, or a file: package
        /// outside both) — the pipeline list has no package flag to read.
        private static Dictionary<string, System.Reflection.Assembly> ProjectAssemblies()
        {
            var packageRoots = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Select(info => info.resolvedPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .ToList();

            var loaded = new Dictionary<string, System.Reflection.Assembly>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (!string.IsNullOrEmpty(name) && !loaded.ContainsKey(name))
                    loaded[name] = assembly;
            }

            var project = new Dictionary<string, System.Reflection.Assembly>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
            {
                if (string.IsNullOrEmpty(assembly.name)) continue;
                if (!loaded.TryGetValue(assembly.name, out var runtime)) continue;
                var source = assembly.sourceFiles != null && assembly.sourceFiles.Length > 0 ? assembly.sourceFiles[0] : null;
                if (BurstTargets.IsPackageSourcePath(source, packageRoots)) continue;
                project[assembly.name] = runtime;
            }
            return project;
        }

        /// Project assemblies first, then every other loaded assembly. Resolution order decides which
        /// assembly a type that exists in both is attributed to, and AppDomain order must not.
        private static List<System.Reflection.Assembly> ResolutionOrder(Dictionary<string, System.Reflection.Assembly> projectAssemblies)
        {
            var ordered = new List<System.Reflection.Assembly>(projectAssemblies.Values);
            var seen = new HashSet<string>(projectAssemblies.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                ordered.Add(assembly);
            }
            return ordered;
        }

        /// Fills target.Assembly (and normalizes DeclaringType) by finding the owning type among the
        /// loaded assemblies. Burst 2.x describes targets as strings only, so this is the only way
        /// to resolve an assembly there. Package targets are resolved too, so --include_packages can
        /// list them; an unresolved target keeps whatever Burst itself reported (usually nothing).
        private static void ResolveAssembly(BurstTargetInfo target, IReadOnlyList<System.Reflection.Assembly> assemblies)
        {
            if (assemblies.Count == 0) return;

            foreach (var candidate in Candidates(target))
            {
                foreach (var assembly in assemblies)
                {
                    var found = FindType(assembly, candidate);
                    if (found == null) continue;
                    target.Assembly = assembly.GetName().Name;
                    target.DeclaringType = found.FullName;
                    return;
                }
            }
        }

        /// Burst writes nested types with '+' in Scope and with '.' in FullName; a loaded type name
        /// needs the '+' form. Try both spellings of each name Burst gave us.
        private static IEnumerable<string> Candidates(BurstTargetInfo target)
        {
            var seen = new HashSet<string>();
            foreach (var raw in new[] { target.DeclaringType, target.Scope })
            {
                if (string.IsNullOrEmpty(raw)) continue;
                foreach (var variant in NestVariants(raw))
                    if (seen.Add(variant))
                        yield return variant;
            }
        }

        static IEnumerable<string> NestVariants(string name)
        {
            yield return name;
            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0)
                yield return name.Substring(0, lastDot) + "+" + name.Substring(lastDot + 1);
            if (name.Contains("."))
                yield return name.Replace('.', '+');
        }

        static Type FindType(System.Reflection.Assembly assembly, string name)
        {
            try { return assembly.GetType(name, false); }
            catch (Exception) { return null; }
        }
    }
}
