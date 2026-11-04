using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "exec", Description = "Execute arbitrary C# code at runtime. Full access to Unity and all loaded assemblies.")]
    public static class ExecuteCsharp
    {
        // The 30000 mirrors the csc wait the old synchronous compile used, so a slow
        // compile still answers instead of hanging the request.
        private const int CompileTimeoutMs = 30000;

        // An off-main-thread await only resumes on the editor thread while this domain's
        // synchronization context is pumping; a reload mid-await leaves the continuation on
        // a thread-pool thread, where invoking the snippet would throw inside every Unity
        // API it touches.
        private const string ReloadedMessage =
            "The editor reloaded during the compile, so the snippet never ran. Re-issue the command.";

        // Snippet assemblies are named with this prefix so they can be excluded from the
        // reference list. They are byte-loaded (empty Location) and so already drop out of
        // it, but if a Unity version ever reports a location for them, referencing an
        // earlier snippet would grow both the .rsp list and every later snippet's cache key.
        private const string ExecAssemblyPrefix = "unity-cli-exec-";

        // csc and dotnet live somewhere inside the Unity install, so finding them means a
        // full-tree walk (measured 1.1 s per walk on this host; exec used to pay two of them
        // per call, on the main thread). Resolved once per domain and off the main thread.
        private static string s_CompilerMemoKey;
        private static string s_CscPath;
        private static string s_DotnetPath;

        public class Parameters
        {
            [ToolParameter("C# code to execute. Use 'return' for output.", Required = true)]
            public string Code { get; set; }

            [ToolParameter("Additional using directives (comma-separated, e.g. Unity.Entities,Unity.Mathematics)")]
            public string[] Usings { get; set; }

            [ToolParameter("Drop the default using directives and aliases; combine with --usings to supply the whole set")]
            public bool NoDefaultUsings { get; set; }

            [ToolParameter("Path to csc compiler (csc.dll or csc.exe). Auto-detected if omitted.")]
            public string Csc { get; set; }

            [ToolParameter("Path to dotnet runtime. Auto-detected if omitted.")]
            public string Dotnet { get; set; }

            // The 30000 mirrors ExecAwait.DefaultTimeoutMs: Unity's compiler rejects it
            // there as an attribute argument (CS0182), so it cannot be referenced.
            [ToolParameter("Max ms to await a returned Task / pump a returned coroutine (default 30000; 0 or negative waits forever)")]
            public int AwaitTimeout { get; set; }
        }

        public static async Task<object> HandleCommand(JObject parameters)
        {
            var p = new ToolParams(parameters);
            var code = p.Get("code")
                ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
            if (string.IsNullOrEmpty(code))
                return new ErrorResponse("'code' required");

            var usingsToken = p.GetRaw("usings");
            var extraUsings = new List<string>();
            if (usingsToken != null)
            {
                if (usingsToken.Type == JTokenType.Array)
                    extraUsings.AddRange(usingsToken.ToObject<string[]>());
                else
                    extraUsings.AddRange(usingsToken.ToString().Split(','));
            }

            var includeDefaults = !(p.GetBool("no_default_usings") || p.GetBool("noDefaultUsings"));
            var cscPath = p.Get("csc");
            var dotnetPath = p.Get("dotnet");
            var awaitTimeout = p.GetInt("await_timeout") ?? p.GetInt("awaitTimeout") ?? ExecAwait.DefaultTimeoutMs;
            return await CompileAndExecute(
                ExecSource.Build(code, extraUsings, includeDefaults, DetectBodyKind(code)),
                cscPath, dotnetPath, awaitTimeout);
        }

        /// <summary>
        /// Picks the signature the body is compiled into. Only the keywords the body
        /// actually uses decide this: an async signature would break ref-struct locals
        /// (CS4012) and an iterator signature breaks plain returns, so neither is used
        /// unless the submitted code asks for it. A body that merely *returns* a Task
        /// needs no special signature — ExecAwait unwraps it after invocation.
        /// </summary>
        private static ExecSource.BodyKind DetectBodyKind(string code)
        {
            if (Regex.IsMatch(code, @"\byield\s+(return|break)\b")) return ExecSource.BodyKind.Iterator;
            if (Regex.IsMatch(code, @"\b(await|async)\b")) return ExecSource.BodyKind.Async;
            return ExecSource.BodyKind.Sync;
        }

        /// <summary>
        /// Reference set for the snippet compile, from this domain's loadable assemblies.
        /// Dynamic and byte-loaded (in-memory) assemblies have no usable location, and
        /// previous exec output is excluded by name: both would otherwise make the .rsp
        /// list — and so every later snippet's cache key — grow with the exec count.
        /// Sorted so the .rsp order, and therefore the key, does not depend on load order.
        /// </summary>
        private static IReadOnlyList<string> CollectReferences()
        {
            var pairs = new List<(string Name, string Location)>();
            var names = new HashSet<string>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
                    var name = asm.GetName().Name;
                    if (string.IsNullOrEmpty(name) || !names.Add(name)) continue;
                    if (name.StartsWith(ExecAssemblyPrefix, StringComparison.Ordinal)) continue;
                    pairs.Add((name, asm.Location));
                }
                catch (Exception ex) when (ex is NotSupportedException || ex is InvalidOperationException)
                {
                    // Location throws for dynamic modules; one unreadable reference is no
                    // reason to drop the rest of the set.
                }
            }

            pairs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            var locations = new List<string>(pairs.Count);
            for (var i = 0; i < pairs.Count; i++) locations.Add(pairs[i].Location);
            return locations;
        }

        // The third constructor argument, not the second: `data` stays the tool's result
        // payload, so a caller parsing it sees the same shape as before the metadata existed.
        private static object CompileMeta(string state, long compileMs, int referenceCount)
            => new { compile = state, compile_ms = compileMs, reference_assemblies = referenceCount };

        private static async Task<object> CompileAndExecute(string source, string cscOverride = null,
            string dotnetOverride = null, int awaitTimeoutMs = ExecAwait.DefaultTimeoutMs)
        {
            // Compiler discovery and the reference set read Unity APIs and this domain's
            // assembly list, so both are taken before anything is awaited.
            var contentsPath = EditorApplication.applicationContentsPath;
            var isWindows = Application.platform == RuntimePlatform.WindowsEditor;
            var references = CollectReferences();

            var resolved = await ResolveCompilerAsync(cscOverride, dotnetOverride, contentsPath, isWindows);
            if (resolved.Error != null)
                return new ErrorResponse(resolved.Error)
                    { meta = CompileMeta("missing_compiler", 0, references.Count) };
            if (!MainThread.IsCurrent)
                return new ErrorResponse(ReloadedMessage)
                    { meta = CompileMeta("interrupted", 0, references.Count) };

            var csc = resolved.Csc;
            var dotnet = resolved.Dotnet;
            var key = ExecCompileCache.KeyFor(source, csc, dotnet, references);

            string compileState;
            long compileMs = 0;
            MethodInfo method;

            if (ExecCompileCache.TryGet(key, out var cached))
            {
                method = cached.Method;
                compileState = "cached";
            }
            else
            {
                var id = ExecAssemblyPrefix + Guid.NewGuid().ToString("N").Substring(0, 12);
                var sw = Stopwatch.StartNew();
                var compiled = await ExecCompiler.CompileAsync(source, references, csc, dotnet, id, CompileTimeoutMs);
                compileMs = sw.ElapsedMilliseconds;

                if (compiled.TimedOut)
                    return new ErrorResponse($"Compile timed out after {compileMs}ms.")
                        { meta = CompileMeta("timeout", compileMs, references.Count) };

                if (!compiled.Ok)
                    return new ErrorResponse($"Compile error:\n{FormatErrors(compiled.Diagnostics)}")
                        { meta = CompileMeta("failed", compileMs, references.Count) };

                // See ReloadedMessage: the compile finished, but not on the thread that may
                // load an assembly and run Unity APIs.
                if (!MainThread.IsCurrent)
                    return new ErrorResponse(ReloadedMessage)
                        { meta = CompileMeta("interrupted", compileMs, references.Count) };

                var assembly = Assembly.Load(compiled.Bytes);
                method = assembly.GetType(ExecSource.TypeName)?.GetMethod("Execute");
                if (method == null)
                    return new ErrorResponse("Internal error: compiled type or method not found.")
                        { meta = CompileMeta("compiled", compileMs, references.Count) };

                ExecCompileCache.Put(key, new CompiledSnippet(assembly, method));
                compileState = "compiled";
            }

            object result;
            try
            {
                result = method.Invoke(null, null);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                return new ErrorResponse($"Runtime error: {inner.GetType().Name}: {inner.Message}")
                    { meta = CompileMeta(compileState, compileMs, references.Count) };
            }

            // An async body reports its exceptions on the returned Task, not as a
            // TargetInvocationException, so both paths have to be handled.
            var outcome = await ExecAwait.ResolveAsync(result, awaitTimeoutMs);

            if (outcome.Error != null)
                return new ErrorResponse($"Runtime error: {outcome.Error.GetType().Name}: {outcome.Error.Message}")
                    { meta = CompileMeta(compileState, compileMs, references.Count) };

            if (outcome.TimedOut)
            {
                var budget = awaitTimeoutMs <= 0
                    ? "no timeout is set"
                    : $"await_timeout of {awaitTimeoutMs}ms is set";
                return new ErrorResponse(
                    $"exec timed out with the returned operation still unfinished (status: {outcome.Status}, " +
                    $"steps: {outcome.Steps}, {budget}). The operation is still running in the editor and its " +
                    "result is not observable from this response; a domain reload would also drop it silently. " +
                    "Raise --await_timeout, or park the result somewhere durable (static field, ScriptableObject) " +
                    "and read it back with a second exec.",
                    new
                    {
                        timed_out = true,
                        status = outcome.Status,
                        steps = outcome.Steps,
                        await_timeout_ms = awaitTimeoutMs,
                    },
                    CompileMeta(compileState, compileMs, references.Count));
            }

            var message = outcome.Steps > 0
                ? $"OK (routine completed in {outcome.Steps} steps)"
                : "OK";
            return new SuccessResponse(message, Serialize(outcome.Value, 0),
                CompileMeta(compileState, compileMs, references.Count));
        }

        /// <summary>
        /// Resolves the compiler to run: the Unity-install walk happens at most once per
        /// domain and never on the main thread. Only the reads that need the main thread
        /// (applicationContentsPath, Application.platform) stay there; their values come in.
        /// </summary>
        private static async Task<CompilerPaths> ResolveCompilerAsync(string cscOverride, string dotnetOverride,
            string contentsPath, bool isWindowsEditor)
        {
            var memoKey = (cscOverride ?? string.Empty) + "\n" + (dotnetOverride ?? string.Empty);
            if (s_CompilerMemoKey != memoKey)
            {
                s_CscPath = await Task.Run(
                    () => FindCscFile(cscOverride, contentsPath, isWindowsEditor)).ConfigureAwait(false);
                s_DotnetPath = s_CscPath != null && s_CscPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? await Task.Run(() => FindDotnetFile(dotnetOverride, contentsPath, isWindowsEditor))
                        .ConfigureAwait(false)
                    : null;
                s_CompilerMemoKey = memoKey;
            }

            if (s_CscPath == null)
                return CompilerPaths.Fail(
                    "Cannot find csc compiler under: " + contentsPath +
                    "\nSpecify the path manually with --csc <path-to-csc.dll-or-csc.exe>");

            if (s_CscPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && s_DotnetPath == null)
                return CompilerPaths.Fail(
                    "Cannot find dotnet runtime under: " + contentsPath +
                    "\nSpecify the path manually with --dotnet <path>");

            return CompilerPaths.Ok(s_CscPath, s_DotnetPath);
        }

        readonly struct CompilerPaths
        {
            internal readonly string Csc;
            /// Null when csc is an executable and so needs no host runtime.
            internal readonly string Dotnet;
            internal readonly string Error;

            CompilerPaths(string csc, string dotnet, string error)
            {
                Csc = csc;
                Dotnet = dotnet;
                Error = error;
            }

            internal static CompilerPaths Ok(string csc, string dotnet) => new CompilerPaths(csc, dotnet, null);
            internal static CompilerPaths Fail(string error) => new CompilerPaths(null, null, error);
        }

        private static string FindCscFile(string cscOverride, string contentsPath, bool isWindowsEditor)
        {
            if (!string.IsNullOrEmpty(cscOverride))
                return cscOverride;

            var cscDll = SearchFile(contentsPath, "csc.dll");
            if (cscDll != null) return cscDll;

            if (isWindowsEditor)
            {
                var cscExe = SearchFile(contentsPath, "csc.exe");
                if (cscExe != null) return cscExe;
            }

            return null;
        }

        private static string FindDotnetFile(string dotnetOverride, string contentsPath, bool isWindowsEditor)
        {
            if (!string.IsNullOrEmpty(dotnetOverride))
                return dotnetOverride;

            var name = "dotnet" + (isWindowsEditor ? ".exe" : "");
            var found = SearchFile(contentsPath, name);
            if (found != null) return found;

            if (!isWindowsEditor)
            {
                var macPaths = new[]
                {
                    "/usr/local/share/dotnet/dotnet",
                    "/opt/homebrew/bin/dotnet",
                    "/usr/local/bin/dotnet",
                };
                foreach (var p in macPaths)
                    if (File.Exists(p)) return p;
            }

            return name;
        }

        private static string SearchFile(string dir, string name)
        {
            try
            {
                var files = Directory.GetFiles(dir, name, SearchOption.AllDirectories);
                foreach (var f in files)
                    if (Path.GetFileName(f) == name)
                        return f;
            }
            catch { }
            return null;
        }

        private static string FormatErrors(string raw)
        {
            var lines = raw.Split('\n');
            var errors = new List<string>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                var m = Regex.Match(trimmed, @"\((\d+),\d+\):\s*error\s+\w+:\s*(.+)");
                if (m.Success)
                    errors.Add($"L{m.Groups[1].Value}: {m.Groups[2].Value}");
                else if (trimmed.Contains("error"))
                    errors.Add(trimmed);
            }
            return errors.Count > 0 ? string.Join("\n", errors) : raw;
        }

        private static object Serialize(object obj, int depth)
        {
            if (obj == null) return null;
            if (depth > 4) return obj.ToString();
            var type = obj.GetType();
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal)) return obj;
            if (type.IsEnum) return obj.ToString();
            if (type.Name.StartsWith("FixedString")) return obj.ToString();
            // A Task still reachable here was never awaited (nested inside a collection
            // or a field). Reflecting over it yields AsyncState/Status noise, so name
            // the state instead.
            if (obj is Task task)
            {
                if (task.IsCompleted && !task.IsFaulted && !task.IsCanceled)
                {
                    var inner = TaskResultOf(task);
                    return inner == null ? "<completed>" : Serialize(inner, depth + 1);
                }
                return $"<task {task.Status}>";
            }
            if (obj is IDictionary dict)
            {
                var r = new Dictionary<string, object>();
                foreach (DictionaryEntry e in dict)
                    r[e.Key.ToString()] = Serialize(e.Value, depth + 1);
                return r;
            }
            if (obj is IEnumerable enumerable)
            {
                var list = new List<object>();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count++ >= 100) { list.Add("... (truncated at 100)"); break; }
                    list.Add(Serialize(item, depth + 1));
                }
                return list;
            }
            if (type.IsValueType || type.IsClass)
            {
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                if (fields.Length > 0)
                {
                    var r = new Dictionary<string, object>();
                    foreach (var f in fields)
                    {
                        try { r[f.Name] = Serialize(f.GetValue(obj), depth + 1); }
                        catch { r[f.Name] = "<error>"; }
                    }
                    return r;
                }
            }
            return obj.ToString();
        }

        private static object TaskResultOf(Task task)
        {
            // Non-generic Task has no Result property.
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }
    }
}
