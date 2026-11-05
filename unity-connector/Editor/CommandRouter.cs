using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCliConnector
{
    /// <summary>
    /// Routes incoming command requests to the appropriate tool handler.
    /// All requests are serialized through a single queue to prevent
    /// race conditions when multiple CLI agents access the same Unity instance.
    /// </summary>
    public static class CommandRouter
    {
        // Not reentrant, and the wait is asynchronous on purpose: a handler that blocks the main
        // thread while waiting for this lock deadlocks the editor, because the release continuation
        // needs the same pump. test_run holds it for an entire EditMode run.
        static readonly SemaphoreSlim s_Lock = new(1, 1);

        /// <summary>
        /// <paramref name="ticket"/> is the request tracker's claim on this command, and it is taken
        /// here — once the command lock is ours — because a request can be answered while it waits on
        /// that lock (server-side deadline, domain reload) and running it afterwards would apply side
        /// effects to a caller that was already told the command did not run.
        /// Returns null when the request no longer wants an answer.
        /// </summary>
        public static async Task<object> Dispatch(string command, JObject parameters, RequestTicket ticket)
        {
            await s_Lock.WaitAsync();
            try
            {
                if (!ShouldRun(ticket)) return null;
                return await DispatchInternal(command, parameters);
            }
            finally
            {
                s_Lock.Release();
            }
        }

        internal static bool ShouldRun(RequestTicket ticket) => ticket == null || ticket.TryBegin();

        static async Task<object> DispatchInternal(string command, JObject parameters)
        {
            if (command == "list")
                return new SuccessResponse("Available tools", ToolDiscovery.GetToolSchemas());

            var tool = ToolDiscovery.FindTool(command);
            if (tool == null)
                return new ErrorResponse($"Unknown command: {command}");

            var unknown = UnknownParameters(tool, parameters);
            if (unknown != null) return unknown;

            // Central compilation gate: short-circuit when the editor has compile
            // errors so individual tools don't need to repeat the check. Tools
            // that must remain callable in a broken state opt out via
            // [UnityCliTool(SkipCompilationGate = true)].
            if (tool.Attribute is { SkipCompilationGate: false })
            {
                var gate = CompilationGate.CheckOrNull(parameters);
                if (gate != null) return gate;
            }

            return await Invoke(tool.Handler, command, parameters);
        }

        /// Keys the CLI or the connector itself put into `params` before a tool sees it, so they are
        /// never a tool's to declare: `args` is the CLI's positional-argument array (cmd/root.go
        /// buildParams), `queue_deadline_ms` is read by HttpServer.QueueWaitMsFor before dispatch,
        /// `deadline_ms` is the envelope's client deadline (an agent may pass it through --params),
        /// and `skip_compile_check` is CompilationGate's escape hatch, documented for every tool.
        static readonly HashSet<string> ReservedParameters = new(StringComparer.Ordinal)
        {
            "args", "queue_deadline_ms", "deadline_ms", "skip_compile_check", "skipCompileCheck",
        };

        /// Rejects a parameter the tool's schema does not declare. Without this a misspelled flag
        /// reaches the handler as an unused key: `test --filtre X` ran the whole suite and answered
        /// as if the filter had applied, which is the worst possible failure for an agent driving
        /// the editor. Tools without a Parameters class keep accepting anything.
        internal static ErrorResponse UnknownParameters(ToolDiscovery.ToolEntry tool, JObject parameters)
        {
            if (parameters == null || tool.ParametersType == null) return null;

            var known = ToolDiscovery.DeclaredParameterNames(tool.ParametersType);
            var unknown = new List<string>();
            foreach (var property in parameters.Properties())
                if (!ReservedParameters.Contains(property.Name) && !known.Contains(property.Name))
                    unknown.Add(property.Name);
            if (unknown.Count == 0) return null;

            var knownList = new HashSet<string>(known, StringComparer.Ordinal);
            knownList.UnionWith(ReservedParameters);
            var knownSorted = new List<string>(knownList);
            knownSorted.Sort(StringComparer.Ordinal);

            var named = string.Join(", ", unknown.Select(u => "--" + StringCaseUtility.ToSnakeCase(u)));
            var message = $"{tool.Name}: unknown parameter{(unknown.Count == 1 ? "" : "s")} {named}";
            return new ErrorResponse(
                $"{message}; known parameters: {string.Join(", ", knownSorted)}.",
                new
                {
                    code = ErrorCodes.UnknownParameter,
                    command = tool.Name,
                    unknown_parameters = unknown,
                    known_parameters = knownSorted,
                });
        }

        /// <summary>
        /// Runs a [UnityCliTool(RunOffMainThread = true)] handler on the calling
        /// thread. Skips the command lock on purpose: the command holding it may be
        /// the very one stuck behind a modal dialog this tool is meant to answer.
        /// </summary>
        public static Task<object> DispatchOffMainThread(string command, JObject parameters)
        {
            var tool = ToolDiscovery.FindTool(command);
            if (tool == null)
                return Task.FromResult<object>(new ErrorResponse($"Unknown command: {command}"));
            var unknown = UnknownParameters(tool, parameters);
            if (unknown != null) return Task.FromResult<object>(unknown);
            return Invoke(tool.Handler, command, parameters);
        }

        /// Runs another tool from inside a handler (verify runs refresh_unity, compile_diagnostics,
        /// burst_status and run_tests this way). No command lock: the calling handler holds it and the
        /// lock is not reentrant. No compilation gate: the caller decides what a compile error means.
        internal static Task<object> InvokeTool(string command, JObject parameters)
        {
            var tool = ToolDiscovery.FindTool(command);
            if (tool == null)
                return Task.FromResult<object>(new ErrorResponse($"Unknown command: {command}"));
            return Invoke(tool.Handler, command, parameters);
        }

        static async Task<object> Invoke(MethodInfo handler, string command, JObject parameters)
        {
            try
            {
                var result = handler.Invoke(null, new object[] { parameters ?? new JObject() });

                if (result is Task<object> asyncTask)
                    return await asyncTask;

                if (result is Task task)
                {
                    await task;
                    return new SuccessResponse($"{command} completed");
                }

                return result ?? new SuccessResponse($"{command} completed");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Debug.LogException(inner);
                return new ErrorResponse($"{command} failed: {inner.Message}");
            }
        }
    }
}