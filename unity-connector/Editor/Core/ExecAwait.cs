using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
    /// <summary>
    /// Resolves whatever exec'd code hands back: Tasks are awaited, IEnumerators are
    /// pumped one MoveNext per editor update, anything else passes through untouched.
    ///
    /// Nothing here blocks the main thread. exec runs on the main thread via
    /// HttpServer's queue, and the awaited work frequently needs that same thread to
    /// make progress (EditorApplication.delayCall, a play-mode transition, the next
    /// editor update). A synchronous .Wait() would deadlock against exactly the cases
    /// this exists to support, so every wait is an await and continuations stay on
    /// Unity's SynchronizationContext (no ConfigureAwait(false) anywhere below).
    /// </summary>
    internal static class ExecAwait
    {
        internal const int DefaultTimeoutMs = 30000;

        // Bound on what a value-yielding routine can accumulate; Serialize truncates
        // at 100 entries anyway.
        const int MaxCollectedValues = 1000;

        // Task<object> whose Result is a Task<object> whose Result is … stops here
        // rather than spinning until the await budget expires.
        const int MaxUnwrapDepth = 16;

        internal readonly struct Outcome
        {
            internal readonly object Value;
            internal readonly int Steps;
            internal readonly bool TimedOut;
            internal readonly string Status;
            internal readonly Exception Error;

            internal Outcome(object value, int steps = 0, bool timedOut = false,
                string status = null, Exception error = null)
            {
                Value = value;
                Steps = steps;
                TimedOut = timedOut;
                Status = status;
                Error = error;
            }
        }

        internal static async Task<Outcome> ResolveAsync(object value, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            var current = value;

            for (var depth = 0; depth < MaxUnwrapDepth; depth++)
            {
                if (current is Task task)
                {
                    var budget = Budget(timeoutMs, sw);
                    if (budget == 0)
                        return new Outcome(null, 0, true, StatusOf(task));

                    if (!await AwaitWithin(task, budget))
                        return new Outcome(null, 0, true, StatusOf(task));

                    if (task.IsFaulted)
                        return new Outcome(null, 0, error: Flatten(task.Exception));
                    if (task.IsCanceled)
                        return new Outcome(null, 0, status: "Canceled");

                    // Task<T>.Result may itself be a Task or an IEnumerator.
                    current = TaskResult(task);
                    continue;
                }

                if (current is IEnumerator routine)
                    return await Pump(routine, timeoutMs, sw);

                return new Outcome(current);
            }

            return new Outcome(null, 0, error: new InvalidOperationException(
                $"returned value nests Tasks more than {MaxUnwrapDepth} deep"));
        }

        /// <summary>
        /// Advances a routine: null yields wait one editor update, Tasks are awaited,
        /// nested IEnumerators are pumped inline, and any other yielded value is
        /// collected as a result (so a plain data enumerator comes back as a list).
        /// Unity's opaque YieldInstructions (WaitForSeconds, WaitForEndOfFrame, …) are
        /// rejected rather than ignored — ignoring them would run the routine at frame
        /// rate while looking like it honoured the wait.
        /// </summary>
        static async Task<Outcome> Pump(IEnumerator routine, int timeoutMs, Stopwatch sw)
        {
            var steps = 0;
            var values = new List<object>();

            while (true)
            {
                bool moved;
                object yielded;
                try
                {
                    moved = routine.MoveNext();
                    yielded = moved ? routine.Current : null;
                }
                catch (Exception ex)
                {
                    return new Outcome(null, steps, error: ex);
                }

                if (!moved)
                    return new Outcome(values.Count > 0 ? (object)values : null, steps);

                steps++;

                var budget = Budget(timeoutMs, sw);
                if (budget == 0)
                    return new Outcome(null, steps, true, "RoutineRunning");

                switch (yielded)
                {
                    case null:
                        await NextUpdate();
                        break;

                    case Task nested:
                        if (!await AwaitWithin(nested, budget))
                            return new Outcome(null, steps, true, StatusOf(nested));
                        if (nested.IsFaulted)
                            return new Outcome(null, steps, error: Flatten(nested.Exception));
                        if (nested.IsCanceled)
                            return new Outcome(null, steps, status: "Canceled");
                        break;

                    // CustomYieldInstruction implements IEnumerator, so it must be
                    // matched before the IEnumerator case below.
                    case CustomYieldInstruction poll:
                        while (poll.keepWaiting)
                        {
                            await NextUpdate();
                            var remaining = Budget(timeoutMs, sw);
                            if (remaining == 0)
                                return new Outcome(null, steps, true, "CustomYieldInstruction");
                        }
                        break;

                    case IEnumerator inner:
                    {
                        var nested = await Pump(inner, timeoutMs, sw);
                        if (nested.Error != null || nested.TimedOut)
                            return new Outcome(nested.Value, steps + nested.Steps,
                                nested.TimedOut, nested.Status, nested.Error);
                        if (nested.Value is List<object> innerValues)
                            values.AddRange(innerValues);
                        break;
                    }

                    case YieldInstruction wait:
                        return new Outcome(null, steps, error: new NotSupportedException(
                            $"exec cannot pump 'yield return {wait.GetType().Name}'. " +
                            "Use 'yield return null' to wait one editor update, or write an " +
                            "async body and 'await Task.Delay(ms)' to wait for time."));

                    default:
                        if (values.Count < MaxCollectedValues)
                            values.Add(yielded);
                        break;
                }
            }
        }

        static Task NextUpdate()
        {
            var tcs = new TaskCompletionSource<bool>();

            void Handler()
            {
                EditorApplication.update -= Handler;
                tcs.TrySetResult(true);
            }

            EditorApplication.update += Handler;
            return tcs.Task;
        }

        /// <summary>
        /// True when the task finished inside the budget. budgetMs &lt; 0 means wait
        /// forever. Never throws — faulted/cancelled state is read off the task after.
        /// </summary>
        static async Task<bool> AwaitWithin(Task task, int budgetMs)
        {
            if (task.IsCompleted) return true;
            if (budgetMs < 0)
            {
                await task;
                return true;
            }
            var winner = await Task.WhenAny(task, Task.Delay(budgetMs));
            return ReferenceEquals(winner, task);
        }

        /// <summary>Remaining budget in ms. Negative = unlimited, 0 = exhausted.</summary>
        static int Budget(int timeoutMs, Stopwatch sw)
        {
            if (timeoutMs <= 0) return -1;
            var remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
            return remaining > 0 ? remaining : 0;
        }

        static string StatusOf(Task task) => task.Status.ToString();

        static object TaskResult(Task task)
        {
            // Non-generic Task has no Result property.
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        static Exception Flatten(AggregateException ex)
        {
            // Flatten().InnerException is null for an AggregateException with no inners.
            return ex?.Flatten().InnerException ?? ex ?? new Exception("awaited task faulted");
        }
    }
}
