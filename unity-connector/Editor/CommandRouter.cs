using System;
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

            var handler = ToolDiscovery.FindHandler(command);
            if (handler == null)
                return new ErrorResponse($"Unknown command: {command}");

            // Central compilation gate: short-circuit when the editor has compile
            // errors so individual tools don't need to repeat the check. Tools
            // that must remain callable in a broken state opt out via
            // [UnityCliTool(SkipCompilationGate = true)].
            var toolAttr = handler.DeclaringType?.GetCustomAttribute<UnityCliToolAttribute>();
            if (toolAttr is { SkipCompilationGate: false })
            {
                var gate = CompilationGate.CheckOrNull(parameters);
                if (gate != null) return gate;
            }

            return await Invoke(handler, command, parameters);
        }

        /// <summary>
        /// Runs a [UnityCliTool(RunOffMainThread = true)] handler on the calling
        /// thread. Skips the command lock on purpose: the command holding it may be
        /// the very one stuck behind a modal dialog this tool is meant to answer.
        /// </summary>
        public static Task<object> DispatchOffMainThread(string command, JObject parameters)
        {
            var handler = ToolDiscovery.FindHandler(command);
            if (handler == null)
                return Task.FromResult<object>(new ErrorResponse($"Unknown command: {command}"));
            return Invoke(handler, command, parameters);
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