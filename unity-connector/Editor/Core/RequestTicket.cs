using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCliConnector
{
    public enum RequestStage
    {
        /// <summary>Accepted by an HTTP thread; no tool handler has started for it yet.</summary>
        Queued,
        /// <summary>Holds the command lock and is inside a tool handler.</summary>
        Running,
        /// <summary>An answer is decided; the HTTP thread owns the response from here on.</summary>
        Answered,
    }

    /// <summary>
    /// One command accepted over HTTP, from the moment the request thread takes it over until an
    /// answer is decided for it. The request thread (server-side deadline, domain reload) and the
    /// editor main thread (queue pickup, command lock) both try to move its stage, and the stage is
    /// only ever moved by a CAS: whoever wins decides whether the tool handler runs at all. That is
    /// what makes "answered without running" and "ran without being answered" mutually exclusive.
    /// </summary>
    public sealed class RequestTicket
    {
        // Ids only label log lines and are not persisted, so a domain reload restarting the count
        // cannot collide with anything still in flight (the old domain's tickets are gone with it).
        static int s_LastId;

        public int Id { get; }
        public string Command { get; }

        /// <summary>How long this request may wait without a tool handler starting for it.</summary>
        public int QueueWaitMs { get; }

        /// <summary>Completed exactly once, by whoever wins the stage race.</summary>
        public TaskCompletionSource<object> Tcs { get; }

        public long EnqueuedTicks { get; }

        int m_Stage;

        public RequestTicket(string command, int queueWaitMs)
        {
            Id = Interlocked.Increment(ref s_LastId);
            Command = command;
            QueueWaitMs = Math.Max(0, queueWaitMs);
            EnqueuedTicks = Stopwatch.GetTimestamp();
            Tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public RequestStage Stage => (RequestStage)Volatile.Read(ref m_Stage);

        /// True while no tool handler has started for this request, so answering it can promise
        /// that the command did not run.
        public bool IsWaiting => Stage == RequestStage.Queued;

        /// Wall-clock ms since the request was accepted. Safe from any thread.
        public int WaitedMs => (int)((Stopwatch.GetTimestamp() - EnqueuedTicks) * 1000 / Stopwatch.Frequency);

        long m_StartedTicks;

        /// <summary>
        /// How long the request waited before a tool handler started, or -1 while it has not. Frozen
        /// when the handler begins, so a caller that reports the pick-up wait is not also counting the
        /// handler's own run time — `exec --meta` used to show a queue wait that contained its
        /// compile, and the two numbers then added up to more than the invocation's wall time.
        /// </summary>
        public int StartedAfterMs
        {
            get
            {
                var ticks = Volatile.Read(ref m_StartedTicks);
                return ticks == 0 ? -1 : (int)((ticks - EnqueuedTicks) * 1000 / Stopwatch.Frequency);
            }
        }

        /// Claims the request for execution. False once it has been answered, which is how a request
        /// that was already answered (deadline or reload) is skipped instead of running late.
        public bool TryBegin()
        {
            var began = Interlocked.CompareExchange(ref m_Stage, (int)RequestStage.Running, (int)RequestStage.Queued)
                == (int)RequestStage.Queued;
            // Interlocked.Exchange rather than a plain store: the request thread may read this while it
            // assembles the answer's meta.
            if (began) Interlocked.Exchange(ref m_StartedTicks, Stopwatch.GetTimestamp());
            return began;
        }

        /// Answers a request that has not started yet. False if a tool handler is already running it
        /// (or it was answered before), so the caller must not claim the command was skipped.
        public bool TryAnswerIfWaiting(object result)
        {
            return Interlocked.CompareExchange(ref m_Stage, (int)RequestStage.Answered, (int)RequestStage.Queued)
                == (int)RequestStage.Queued
                && Complete(result);
        }

        /// Answers a request whatever its stage. Used when the connector can no longer deliver a
        /// tool's own answer (domain reload, editor quit).
        public bool TryAnswer(object result)
        {
            while (true)
            {
                var current = Volatile.Read(ref m_Stage);
                if (current == (int)RequestStage.Answered) return false;
                if (Interlocked.CompareExchange(ref m_Stage, (int)RequestStage.Answered, current) == current)
                    return Complete(result);
            }
        }

        bool Complete(object result)
        {
            Tcs.TrySetResult(result);
            return true;
        }
    }
}
