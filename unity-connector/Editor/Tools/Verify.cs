using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityCliConnector.Tools
{
    /// `unity-cli verify` as one job in the editor: console clear, compile, diagnostics, Burst and
    /// EditMode tests, instead of one CLI request per step. Every request waits for an editor update,
    /// which an unfocused editor runs ~6 times a second, and the CLI polled the heartbeat between them.
    ///
    /// A compile that succeeds reloads the domain, which drops this request and the task running it.
    /// The job therefore lives in SessionState and resumes in the new domain (VerifyJob); the CLI
    /// re-sends the same job id and is answered with that job's result.
    [UnityCliTool(Name = "verify", SkipCompilationGate = true,
        Description = "Compile, report errors in project code, check Burst and run EditMode tests in one call. " +
                      "unity-cli verify prints this; call it directly only for the raw JSON.")]
    public static class Verify
    {
        public class Parameters
        {
            [ToolParameter("Job id chosen by the caller; sending it again after a domain reload returns that job's result")]
            public string Job { get; set; }

            [ToolParameter("Stop after the compile and Burst checks")]
            public bool NoTests { get; set; }

            [ToolParameter("Skip the Burst check")]
            public bool NoBurst { get; set; }

            [ToolParameter("Seconds to wait for Burst to finish (default 60)")]
            public int BurstWait { get; set; }

            [ToolParameter("Only tests in these assemblies (comma-separated)")]
            public string Assembly { get; set; }

            [ToolParameter("Test filter: class, Class.Test, namespace, or a regex")]
            public string Filter { get; set; }

            [ToolParameter("EditMode (default) or PlayMode. PlayMode tests are left to the caller: entering Play mode reloads the domain")]
            public string Mode { get; set; }

            [ToolParameter("Maximum errors and failures listed (default 20)")]
            public int Max { get; set; }

            [ToolParameter("Keep the console instead of clearing it first")]
            public bool KeepConsole { get; set; }

            [ToolParameter("Run tests even when an assembly under test is older than its sources")]
            public bool AllowStale { get; set; }
        }

        public static async Task<object> HandleCommand(JObject parameters)
        {
            var args = parameters ?? new JObject();
            var job = args.Value<string>("job");
            if (string.IsNullOrEmpty(job)) job = Guid.NewGuid().ToString("N");

            var result = VerifyJob.ResultFor(job)
                         ?? (VerifyJob.PendingId == job ? await VerifyJob.Resume() : await VerifyJob.Start(job, args));
            if (result.Value<string>("job") != job)
                return new ErrorResponse($"verify: job {job} was answered with job {result.Value<string>("job")}'s result");
            return new SuccessResponse("verify finished", result);
        }
    }

    /// The job behind Verify, kept in SessionState so it survives the domain reload a successful
    /// compile causes. One job at a time: a new job replaces the previous result.
    [InitializeOnLoad]
    internal static class VerifyJob
    {
        private const string PendingKey = "UnityCliConnector.Verify.Pending";
        private const string ResultKey = "UnityCliConnector.Verify.Result";

        // The CLI's own limits for the steps it used to wait on (cmd/status.go compileWaitTimeout and
        // compileIdleGrace, cmd/verify.go verifyDiagnosticsWait).
        private const long CompileTimeoutMs = 5 * 60 * 1000;
        private const long CompileIdleGraceMs = 20 * 1000;
        private const long DiagnosticsWaitMs = 60 * 1000;

        // This domain's run of the pending job; a domain reload drops it and the static constructor
        // starts it again.
        private static Task<JObject> s_Running;

        static VerifyJob()
        {
            if (Pending() != null)
                EditorApplication.delayCall += () => Resume();
        }

        private sealed class State
        {
            public string job;
            public JObject args;
            public JObject refresh;
            public long baseline;
            public bool skip;
            public long started_ms;
            // Domains this job's steps have started in: each one after the first means a reload
            // restarted them.
            public int runs;
        }

        private static State Pending()
        {
            var json = SessionState.GetString(PendingKey, "");
            return json.Length == 0 ? null : JsonConvert.DeserializeObject<State>(json);
        }

        internal static string PendingId => Pending()?.job;

        internal static JObject ResultFor(string job)
        {
            var json = SessionState.GetString(ResultKey, "");
            if (json.Length == 0) return null;
            var result = JObject.Parse(json);
            return result.Value<string>("job") == job ? result : null;
        }

        internal static async Task<JObject> Start(string job, JObject args)
        {
            SessionState.EraseString(ResultKey);
            if (!args.Value<bool>("keep_console"))
                await CommandRouter.InvokeTool("console", new JObject { ["clear"] = true });

            var refresh = Reply(await CommandRouter.InvokeTool("refresh_unity", new JObject { ["compile"] = "request" }));
            if (!refresh.Success)
                return Finish(Error(job, "editor: " + refresh.Message, 2));

            var data = refresh.Data as JObject ?? new JObject();
            var state = new State
            {
                job = job,
                args = args,
                refresh = data,
                baseline = data.Value<long?>("compile_finished_at") ?? CompilationGate.LastCompileFinishedAt,
                skip = data.Value<bool>("nothing_to_compile"),
                started_ms = NowMs(),
            };
            SessionState.SetString(PendingKey, JsonConvert.SerializeObject(state));
            return await Resume();
        }

        internal static Task<JObject> Resume()
        {
            if (s_Running != null && !s_Running.IsCompleted) return s_Running;
            // Not `s_Running ??= Run()`: a run with nothing to wait for (no compile, synchronous
            // tests) completes inside Run(), and a finished task kept here answered the next job
            // with this job's result.
            var run = Run();
            s_Running = run.IsCompleted ? null : run;
            return run;
        }

        private static async Task<JObject> Run()
        {
            var state = Pending();
            if (state == null) return Error("", "verify: no job is pending", 2);
            state.runs++;
            SessionState.SetString(PendingKey, JsonConvert.SerializeObject(state));
            JObject result;
            // A job resumed after a domain reload runs before the CLI's request for it arrives, so
            // no request is holding the editor awake for it.
            using (EditorWake.Hold())
            {
                try { result = await Steps(state); }
                catch (Exception e) { result = Error(state.job, "verify failed: " + e.Message, 2); }
            }
            VerifyLog.Append(state.job, state.started_ms, state.runs, state.refresh, result);
            return Finish(result);
        }

        private static JObject Finish(JObject result)
        {
            SessionState.SetString(ResultKey, result.ToString(Formatting.None));
            SessionState.EraseString(PendingKey);
            return result;
        }

        private static async Task<JObject> Steps(State state)
        {
            var args = state.args;
            var max = Math.Max(1, args.Value<int?>("max") ?? 20);
            var timings = new JObject();
            var result = new JObject { ["job"] = state.job, ["refresh"] = state.refresh, ["timings_ms"] = timings };
            var clock = Stopwatch.StartNew();

            // The compile clock advances when a compile ends, with or without errors; a compile with
            // errors does not reload the domain, so this loop sees it end in the same domain.
            var compile = new JObject();
            result["compile"] = compile;
            if (state.skip)
            {
                compile["nothing_to_compile"] = true;
                compile["skip_proven"] = true;
            }
            else
            {
                while (true)
                {
                    var idle = !EditorApplication.isCompiling && !EditorApplication.isUpdating;
                    if (idle && CompilationGate.LastCompileFinishedAt > state.baseline)
                    {
                        timings["compile_wait_end"] = "clock";
                        break;
                    }
                    var elapsed = NowMs() - state.started_ms;
                    if (idle && elapsed > CompileIdleGraceMs)
                    {
                        compile["nothing_to_compile"] = true;
                        timings["compile_wait_end"] = "grace";
                        break;
                    }
                    if (elapsed > CompileTimeoutMs)
                        return Error(state.job, "compile: still compiling after 5m0s", 2);
                    await EditorUpdateAwaiter.Next();
                }
            }
            var hasErrors = EditorUtility.scriptCompilationFailed;
            compile["has_errors"] = hasErrors;
            timings["compile"] = NowMs() - state.started_ms;

            clock.Restart();
            var (diagnostics, diagError) = await Diagnostics(true, max);
            if (diagError == null && diagnostics.Value<int>("total") == 0 && hasErrors)
            {
                // The heartbeat says the compile failed but nothing is in the project's own code:
                // report the third-party errors rather than a clean compile.
                var (all, allError) = await Diagnostics(false, max);
                if (allError == null) diagnostics = all;
                else diagError = allError;
            }
            timings["diagnostics"] = clock.ElapsedMilliseconds;
            if (diagError != null) return Error(state.job, "compile: " + diagError, 2);
            result["diagnostics"] = diagnostics;

            if (diagnostics.Value<int>("total") > 0 || hasErrors)
            {
                result["stopped"] = "compile";
                return result;
            }

            var allowStale = args.Value<bool>("allow_stale");
            if (StaleUnderTest(diagnostics["provenance"] as JObject, args.Value<string>("assembly")) > 0 && !allowStale)
            {
                result["stopped"] = "stale";
                return result;
            }

            // Burst compiles on its own threads and burst_status only waits for it, so the tests run
            // meanwhile; a Burst error fails verify either way.
            clock.Restart();
            Task<object> burst = args.Value<bool>("no_burst")
                ? null
                : CommandRouter.InvokeTool("burst_status", new JObject
                {
                    ["wait"] = args.Value<int?>("burst_wait") ?? 60,
                    ["limit"] = max,
                });

            if (!args.Value<bool>("no_tests"))
            {
                var mode = args.Value<string>("mode");
                if (string.Equals(mode, "PlayMode", StringComparison.OrdinalIgnoreCase))
                {
                    result["tests_delegated"] = true;
                }
                else
                {
                    var testArgs = new JObject
                    {
                        ["mode"] = "EditMode",
                        ["auto_save_scenes"] = true,
                        ["allow_stale"] = allowStale,
                    };
                    if (!string.IsNullOrEmpty(args.Value<string>("filter"))) testArgs["filter"] = args.Value<string>("filter");
                    if (!string.IsNullOrEmpty(args.Value<string>("assembly"))) testArgs["assembly"] = args.Value<string>("assembly");
                    var tests = Reply(await CommandRouter.InvokeTool("run_tests", testArgs));
                    result["tests"] = new JObject { ["success"] = tests.Success, ["message"] = tests.Message, ["data"] = tests.Data };
                    timings["tests"] = clock.ElapsedMilliseconds;
                }
            }

            if (burst != null)
            {
                var reply = Reply(await burst);
                var found = reply.Success || !(reply.Message ?? "").Contains("Unknown command");
                result["burst"] = new JObject
                {
                    ["found"] = found, ["success"] = reply.Success, ["message"] = reply.Message, ["data"] = reply.Data,
                };
                timings["burst_and_tests"] = clock.ElapsedMilliseconds;
            }
            return result;
        }

        private static async Task<(JObject data, string error)> Diagnostics(bool sourceTreeOnly, int max)
        {
            var deadline = NowMs() + DiagnosticsWaitMs;
            while (true)
            {
                var reply = Reply(await CommandRouter.InvokeTool("compile_diagnostics", new JObject
                {
                    ["source_tree_only"] = sourceTreeOnly,
                    ["limit"] = max,
                }));
                if (!reply.Success) return (null, reply.Message);
                var data = reply.Data as JObject ?? new JObject();
                if (!data.Value<bool>("compiling")) return (data, null);
                if (NowMs() > deadline) return (null, "editor still compiling");
                await EditorUpdateAwaiter.Next();
            }
        }

        /// The stale assemblies the test run would load. Mirrors cmd/verify.go
        /// staleAssembliesUnderTest: without --assembly every stale assembly counts, and a truncated
        /// list cannot prove the requested ones are current, so it keeps the full count.
        internal static int StaleUnderTest(JObject provenance, string assembly)
        {
            if (provenance == null) return 0;
            var count = provenance.Value<int?>("stale_assembly_count") ?? 0;
            var wanted = (assembly ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (wanted.Count == 0 || provenance.Value<bool>("stale_assemblies_truncated")) return count;
            var stale = provenance["stale_assemblies"] as JArray;
            if (stale == null) return 0;
            return stale.Count(s => wanted.Contains(s.Value<string>("name")));
        }

        private static JObject Error(string job, string message, int exit) =>
            new JObject { ["job"] = job, ["error"] = message, ["error_exit"] = exit };

        private readonly struct ToolReply
        {
            public readonly bool Success;
            public readonly string Message;
            public readonly JToken Data;

            public ToolReply(bool success, string message, JToken data)
            {
                Success = success;
                Message = message;
                Data = data;
            }
        }

        private static ToolReply Reply(object response)
        {
            if (response == null) return new ToolReply(false, "no reply", null);
            var json = JObject.FromObject(response);
            var data = json["data"];
            return new ToolReply(json.Value<bool?>("success") ?? false, json.Value<string>("message"),
                data == null || data.Type == JTokenType.Null ? null : data);
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
