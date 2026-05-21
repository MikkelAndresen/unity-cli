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
        static readonly SemaphoreSlim s_Lock = new(1, 1);

        public static async Task<object> Dispatch(string command, JObject parameters)
        {
            await s_Lock.WaitAsync();
            try
            {
                return await DispatchInternal(command, parameters);
            }
            finally
            {
                s_Lock.Release();
            }
        }

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