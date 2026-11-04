using System;
using System.Collections.Concurrent;
using System.Threading;

namespace UnityCliConnector
{
    /// <summary>What the editor is doing, as far as a thread other than the main thread can know.</summary>
    public readonly struct BusyDiagnostics
    {
        public readonly string HeartbeatState;
        public readonly string RunningCommand;
        public readonly int RunningMs;
        public readonly int QueueDepth;
        public readonly string ModalTitle;

        public BusyDiagnostics(string heartbeatState, string runningCommand, int runningMs,
            int queueDepth, string modalTitle)
        {
            HeartbeatState = heartbeatState;
            RunningCommand = runningCommand;
            RunningMs = runningMs;
            QueueDepth = queueDepth;
            ModalTitle = modalTitle;
        }
    }

    /// <summary>
    /// The `data` of a request the connector answered by itself. Field names are the contract with
    /// the Go CLI's cmd/connector_error.go, which maps `code` to a next-step hint; renaming one is a
    /// silent change for the CLI.
    /// </summary>
    public readonly struct RequestFailureInfo
    {
        public readonly string code;
        public readonly string command;
        public readonly int waited_ms;
        public readonly int queue_deadline_ms;
        public readonly int queue_depth;
        public readonly string running_command;
        public readonly int running_ms;
        public readonly string heartbeat_state;
        public readonly string modal;
        public readonly bool started;

        public RequestFailureInfo(string code, RequestTicket ticket, BusyDiagnostics diag, bool started)
            : this(code, ticket?.Command, ticket?.WaitedMs ?? 0, ticket?.QueueWaitMs ?? 0, diag, started)
        {
        }

        public RequestFailureInfo(string code, string command, int waitedMs, int queueDeadlineMs,
            BusyDiagnostics diag, bool started)
        {
            this.code = code;
            this.command = command;
            waited_ms = waitedMs;
            queue_deadline_ms = queueDeadlineMs;
            queue_depth = diag.QueueDepth;
            running_command = diag.RunningCommand;
            running_ms = diag.RunningMs;
            heartbeat_state = diag.HeartbeatState;
            modal = diag.ModalTitle;
            this.started = started;
        }
    }

    /// <summary>
    /// Tracks every command that has been accepted but not yet answered, so a request can never be
    /// left without a reply: the request thread answers it when the main thread fails to pick it up
    /// in time, and the reload/quit path answers everything still pending before the listener stops.
    /// Touched from the HTTP threads and the editor main thread; all state is behind
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> and the stage CASes on <see cref="RequestTicket"/>.
    /// </summary>
    public static class RequestTracker
    {
        /// Queue-wait budget for a client that declares no deadline of its own (an older CLI, or
        /// `--timeout 0`). Long enough that a slow but live editor is never cut off, short enough
        /// that an editor whose main thread is not ticking produces an answer instead of a hang.
        public const int DEFAULT_QUEUE_WAIT_MS = 60000;

        /// Ceiling for any queue wait, however long the client's own deadline is: a request that has
        /// not started after this much time is wedged, and waiting longer only hides it.
        public const int MAX_QUEUE_WAIT_MS = 600000;

        /// Floor, so a caller passing a tiny deadline still gets an answer rather than a race.
        public const int MIN_QUEUE_WAIT_MS = 500;

        static readonly ConcurrentDictionary<int, RequestTicket> s_Pending = new();

        /// <summary>
        /// The queue-wait budget for one request: the client's own deadline (so the connector answers
        /// before the client gives up) clamped into [<see cref="MIN_QUEUE_WAIT_MS"/>,
        /// <see cref="MAX_QUEUE_WAIT_MS"/>]. A `queue_deadline_ms` parameter overrides both.
        /// </summary>
        public static int QueueWaitMsFor(int clientDeadlineMs, int overrideMs)
        {
            if (overrideMs > 0) return Clamp(overrideMs);
            if (clientDeadlineMs <= 0) return DEFAULT_QUEUE_WAIT_MS;

            // Leave the client time to receive and print the answer inside its own budget; a tenth
            // of it, but never less than a second, so a short budget is not eaten whole by the margin.
            var margin = Math.Max(1000, clientDeadlineMs / 10);
            return Clamp(clientDeadlineMs - margin);
        }

        static int Clamp(int ms)
        {
            if (ms < MIN_QUEUE_WAIT_MS) return MIN_QUEUE_WAIT_MS;
            if (ms > MAX_QUEUE_WAIT_MS) return MAX_QUEUE_WAIT_MS;
            return ms;
        }

        public static RequestTicket Track(RequestTicket ticket)
        {
            s_Pending[ticket.Id] = ticket;
            return ticket;
        }

        /// Stops tracking a request whose answer has been written.
        public static void Complete(RequestTicket ticket)
        {
            s_Pending.TryRemove(ticket.Id, out _);
        }

        public static void Reset()
        {
            s_Pending.Clear();
        }

        public static int PendingCount => s_Pending.Count;

        /// The request currently inside a tool handler, if any. It is what lets a waiting caller be
        /// told what is holding the editor.
        public static RequestTicket Running
        {
            get
            {
                foreach (var ticket in s_Pending.Values)
                    if (ticket.Stage == RequestStage.Running) return ticket;
                return null;
            }
        }

        /// Answers a request that has not started, and only then: the caller promises in the message
        /// that the command did not run.
        public static bool TryAnswerWaiting(RequestTicket ticket, BusyDiagnostics diag)
        {
            if (!ticket.IsWaiting) return false;
            return ticket.TryAnswerIfWaiting(BuildNotPickedUpError(ticket, diag));
        }

        /// Answers everything still pending — the only way a request survives a domain reload or a
        /// quit. Returns how many were answered.
        public static int AnswerAllPending(Func<RequestTicket, object> errorFor)
        {
            var answered = 0;
            foreach (var ticket in s_Pending.Values)
            {
                if (ticket.TryAnswer(errorFor(ticket))) answered++;
            }
            return answered;
        }

        /// Blocks until every tracked request has had its answer written (i.e. until nothing is
        /// tracked any more). Returns the number still waiting when the budget ran out.
        public static int AwaitForAnswers(int timeoutMs)
        {
            var deadline = new Deadline(timeoutMs);
            while (true)
            {
                if (s_Pending.IsEmpty) return 0;
                if (deadline.Expired) return s_Pending.Count;
                Thread.Sleep(10);
            }
        }

        public static ErrorResponse BuildNotPickedUpError(RequestTicket ticket, BusyDiagnostics diag)
        {
            var waited = ticket.WaitedMs;
            var reason = diag.ModalTitle != null
                ? $"modal dialog \"{diag.ModalTitle}\""
                : diag.RunningCommand != null
                    ? $"blocked by the running command {diag.RunningCommand} ({FormatSeconds(diag.RunningMs)})"
                    : diag.HeartbeatState is "compiling" or "reloading" or "refreshing" or "entering_playmode"
                        ? diag.HeartbeatState
                        : "the editor is not ticking";

            var message = $"{ticket.Command} was queued but the main thread did not pick it up within "
                + $"{FormatSeconds(ticket.QueueWaitMs)} (waited {FormatSeconds(waited)}): editor busy ({reason})"
                + $"[heartbeat: {diag.HeartbeatState ?? "unknown"}, queue depth: {diag.QueueDepth}]. "
                + "The command did not run; nothing changed.";

            return new ErrorResponse(message,
                new RequestFailureInfo(ErrorCodes.MainThreadBusy, ticket, diag, started: false));
        }

        public static ErrorResponse BuildReloadError(RequestTicket ticket, string why, string code = ErrorCodes.DomainReload)
        {
            var started = ticket.Stage == RequestStage.Running;
            var diag = new BusyDiagnostics("reloading", Running?.Command, Running?.WaitedMs ?? 0,
                PendingCount, null);

            var message = started
                ? $"{ticket.Command} was running when Unity stopped answering ({why}); it may have partially run. "
                  + "Check the editor state before retrying."
                : $"{ticket.Command} never ran: Unity stopped answering before the main thread picked it up "
                  + $"({why}, waited {FormatSeconds(ticket.WaitedMs)}). Retry it once Unity answers again.";

            return new ErrorResponse(message,
                new RequestFailureInfo(code, ticket, diag, started));
        }

        /// <summary>
        /// Refuses a request that arrived after the main-thread queue stopped being served (a reload
        /// or quit already under way). Accepting it would be a promise nobody can keep: the queue it
        /// joins is about to be wiped and its ticket would never be answered.
        /// </summary>
        public static ErrorResponse BuildRefusedError(string command, string why)
        {
            var message = $"{command} did not run: Unity is not taking new main-thread commands ({why}). "
                + "Retry it once Unity answers again.";
            var diag = new BusyDiagnostics("reloading", Running?.Command, Running?.WaitedMs ?? 0, PendingCount, null);
            return new ErrorResponse(message, new RequestFailureInfo(ErrorCodes.DomainReload, command, 0, 0, diag, false));
        }

        static string FormatSeconds(int ms)
        {
            if (ms < 1000) return $"{ms}ms";
            return $"{ms / 1000.0:0.#}s";
        }
    }
}
