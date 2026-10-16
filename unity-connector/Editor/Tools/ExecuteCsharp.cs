using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
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
        private static readonly string[] DefaultUsings =
        {
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.IO",
            "System.Linq",
            "System.Reflection",
            "System.Threading.Tasks",
            "UnityEngine",
            "UnityEngine.SceneManagement",
            "UnityEditor",
            "UnityEditor.SceneManagement",
            "UnityEditorInternal",
        };

        public class Parameters
        {
            [ToolParameter("C# code to execute. Use 'return' for output.", Required = true)]
            public string Code { get; set; }

            [ToolParameter("Additional using directives (comma-separated, e.g. Unity.Entities,Unity.Mathematics)")]
            public string[] Usings { get; set; }

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

            var cscPath = p.Get("csc");
            var dotnetPath = p.Get("dotnet");
            var awaitTimeout = p.GetInt("await_timeout") ?? p.GetInt("awaitTimeout") ?? ExecAwait.DefaultTimeoutMs;
            return await CompileAndExecute(BuildSource(code, extraUsings, DetectBodyKind(code)),
                cscPath, dotnetPath, awaitTimeout);
        }

        private enum BodyKind { Sync, Async, Iterator }

        /// <summary>
        /// Picks the signature the body is compiled into. Only the keywords the body
        /// actually uses decide this: an async signature would break ref-struct locals
        /// (CS4012) and an iterator signature breaks plain returns, so neither is used
        /// unless the submitted code asks for it. A body that merely *returns* a Task
        /// needs no special signature — ExecAwait unwraps it after invocation.
        /// </summary>
        private static BodyKind DetectBodyKind(string code)
        {
            if (Regex.IsMatch(code, @"\byield\s+(return|break)\b")) return BodyKind.Iterator;
            if (Regex.IsMatch(code, @"\b(await|async)\b")) return BodyKind.Async;
            return BodyKind.Sync;
        }

        private static string BuildSource(string code, List<string> extraUsings, BodyKind kind)
        {
            var sb = new StringBuilder();
            foreach (var u in DefaultUsings)
                sb.AppendLine($"using {u};");
            foreach (var u in extraUsings)
                sb.AppendLine($"using {u};");

            sb.AppendLine();
            sb.AppendLine("public static class __CliDynamic {");
            switch (kind)
            {
                case BodyKind.Iterator:
                    sb.AppendLine("  public static System.Collections.IEnumerator Execute() {");
                    break;
                case BodyKind.Async:
                    sb.AppendLine("  public static async System.Threading.Tasks.Task<object> Execute() {");
                    break;
                default:
                    sb.AppendLine("  public static object Execute() {");
                    break;
            }
            sb.AppendLine(code);
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static async Task<object> CompileAndExecute(string source, string cscOverride = null,
            string dotnetOverride = null, int awaitTimeoutMs = ExecAwait.DefaultTimeoutMs)
        {
            var utf8 = new UTF8Encoding(false);
            var tmpDir = Path.Combine(Path.GetTempPath(), "unity-cli-exec");
            Directory.CreateDirectory(tmpDir);

            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            var srcFile = Path.Combine(tmpDir, $"{id}.cs");
            var outFile = Path.Combine(tmpDir, $"{id}.dll");
            var rspFile = Path.Combine(tmpDir, $"{id}.rsp");

            try
            {
                File.WriteAllText(srcFile, source, utf8);

                var rsp = new StringBuilder();
                rsp.AppendLine("-target:library");
                rsp.AppendLine($"-out:\"{outFile}\"");
                rsp.AppendLine("-nologo");
                rsp.AppendLine("-nowarn:0105,1701,1702");
                rsp.AppendLine("-langversion:latest");
                rsp.AppendLine($"\"{srcFile}\"");

                var added = new HashSet<string>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
                        if (!added.Add(asm.GetName().Name)) continue;
                        rsp.AppendLine($"-r:\"{asm.Location}\"");
                    }
                    catch { }
                }

                File.WriteAllText(rspFile, rsp.ToString(), utf8);

                var rspArg = $"@\"{rspFile}\"";
                var csc = FindCsc(cscOverride);
                string exe, args;

                if (csc != null && csc.EndsWith(".dll"))
                {
                    var dotnet = FindDotnet(dotnetOverride);
                    if (dotnet == null)
                        return new ErrorResponse(
                            "Cannot find dotnet runtime under: " +
                            EditorApplication.applicationContentsPath +
                            "\nSpecify the path manually with --dotnet <path>");
                    exe = dotnet;
                    args = $"exec \"{csc}\" {rspArg}";
                }
                else if (csc != null)
                {
                    exe = csc;
                    args = rspArg;
                }
                else
                {
                    return new ErrorResponse(
                        "Cannot find csc compiler under: " +
                        EditorApplication.applicationContentsPath +
                        "\nSpecify the path manually with --csc <path-to-csc.dll-or-csc.exe>");
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using (var proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(30000);

                    if (proc.ExitCode != 0)
                    {
                        var output = string.IsNullOrEmpty(stderr) ? stdout : stderr;
                        return new ErrorResponse($"Compile error:\n{FormatErrors(output)}");
                    }
                }

                var bytes = File.ReadAllBytes(outFile);
                var compiled = Assembly.Load(bytes);
                var method = compiled.GetType("__CliDynamic")?.GetMethod("Execute");
                if (method == null)
                    return new ErrorResponse("Internal error: compiled type or method not found.");

                object result;
                try
                {
                    result = method.Invoke(null, null);
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    return new ErrorResponse($"Runtime error: {inner.GetType().Name}: {inner.Message}");
                }

                // An async body reports its exceptions on the returned Task, not as a
                // TargetInvocationException, so both paths have to be handled.
                var outcome = await ExecAwait.ResolveAsync(result, awaitTimeoutMs);

                if (outcome.Error != null)
                    return new ErrorResponse($"Runtime error: {outcome.Error.GetType().Name}: {outcome.Error.Message}");

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
                        });
                }

                var message = outcome.Steps > 0
                    ? $"OK (routine completed in {outcome.Steps} steps)"
                    : "OK";
                return new SuccessResponse(message, Serialize(outcome.Value, 0));
            }
            finally
            {
                try { File.Delete(srcFile); } catch { }
                try { File.Delete(outFile); } catch { }
                try { File.Delete(rspFile); } catch { }
            }
        }

        private static string FindCsc(string cscOverride = null)
        {
            if (!string.IsNullOrEmpty(cscOverride))
                return cscOverride;

            var content = EditorApplication.applicationContentsPath;
            var cscDll = SearchFile(content, "csc.dll");
            if (cscDll != null) return cscDll;

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                var cscExe = SearchFile(content, "csc.exe");
                if (cscExe != null) return cscExe;
            }

            return null;
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

        private static string FindDotnet(string dotnetOverride = null)
        {
            if (!string.IsNullOrEmpty(dotnetOverride))
                return dotnetOverride;

            var name = "dotnet" + (Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : "");
            var found = SearchFile(EditorApplication.applicationContentsPath, name);
            if (found != null) return found;

            if (Application.platform != RuntimePlatform.WindowsEditor)
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
