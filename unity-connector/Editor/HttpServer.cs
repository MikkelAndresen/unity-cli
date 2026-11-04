using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityCliConnector.Modal;

namespace UnityCliConnector
{
    /// <summary>
    /// Lightweight HTTP server on localhost. Receives CLI commands as POST /command,
    /// dispatches via CommandRouter, returns JSON responses.
    /// Uses ConcurrentQueue + EditorApplication.update for main-thread marshaling.
    /// Background editor throttling can still delay command execution.
    /// Survives domain reloads via InitializeOnLoad.
    /// Every accepted request gets exactly one answer: the request thread answers it when the main
    /// thread does not pick it up within its queue deadline, and the reload/quit path answers
    /// whatever is still in flight before the listener stops. See RequestTracker.
    /// </summary>
    [InitializeOnLoad]
    public static class HttpServer
    {
        const int DEFAULT_PORT = 8090;
        const int MAX_PORT_ATTEMPTS = 10;
        const double AUTO_RESTART_INTERVAL = 1.0;
        const double FAILURE_LOG_INTERVAL = 5.0;

        /// How long a reload or quit waits for in-flight answers to reach the client before it
        /// closes the listener anyway. Bounded on purpose: the main thread is inside
        /// beforeAssemblyReload here and Unity's reload clock keeps running.
        const int PENDING_ANSWER_WAIT_MS = 1500;

        /// A main-thread request that waited longer than this reports its wait in `meta`: without it a
        /// slow pickup leaves no trace, and "slow" is indistinguishable from "about to be answered
        /// late" after the fact.
        internal const int SLOW_QUEUE_WAIT_MS = 1000;
        static HttpListener s_Listener;
        static CancellationTokenSource s_Cts;
        static int s_Port;
        static SynchronizationContext s_MainContext;
        static double s_NextStartAttemptTime;
        static string s_LastFailureMessage;
        static double s_LastFailureLogTime;
        static bool s_Stopping;
        static bool s_RestartPending;
        // Set as soon as a reload or quit starts: the main-thread queue is going away, so a request
        // accepted after that point could never be answered. Read from the HTTP threads.
        static volatile bool s_RefusingWork;

        static readonly ConcurrentQueue<WorkItem> s_Queue = new();

        struct WorkItem
        {
            public string Command;
            public JObject Parameters;
            public RequestTicket Ticket;
        }

        static HttpServer()
        {
            // AssetImportWorker children load project assemblies and run [InitializeOnLoad] too. They
            // carry the same -projectPath as the editor, so listening here makes a worker look like a
            // connector and steals the editor's port after a reload.
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;

            s_MainContext = SynchronizationContext.Current;
            Start();
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += Start;
            EditorApplication.update += ProcessQueue;
        }

        public static int Port => s_Port;
        public static bool IsRunning => s_Listener != null && s_Listener.IsListening;

        static void Start()
        {
            s_Stopping = false;
            if (IsRunning) return;
            if (s_Listener != null) StopListener();
            s_Stopping = false;

            for (var attempt = 0; attempt < MAX_PORT_ATTEMPTS; attempt++)
            {
                var port = DEFAULT_PORT + attempt;
                if (TryStartOnPort(port))
                    return;
            }

            Heartbeat.MarkStopped();
            ScheduleRetry();
            LogStartFailure("[UnityCliConnector] Failed to start HTTP server — no available port", true);
        }

        static bool TryStartOnPort(int port)
        {
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();

                s_Listener = listener;
                s_Port = port;
                s_RefusingWork = false;
                var cts = new CancellationTokenSource();
                s_Cts = cts;
                ClearRetry();
                ClearStartFailure();

                _ = ListenLoop(listener, cts);

                // The socket is up before the main thread has ticked (a cold import can take
                // minutes), so publish a heartbeat now rather than leaving the daemon with none.
                Heartbeat.OnListenerStarted();

                Debug.Log($"[UnityCliConnector] HTTP server started on port {port}");
                return true;
            }
            catch (HttpListenerException)
            {
                return false;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Windows/Mono throws SocketException instead of HttpListenerException
                return false;
            }
            catch (Exception ex)
            {
                ScheduleRetry();
                LogStartFailure($"[UnityCliConnector] Failed to start HTTP server on port {port}: {ex.Message}", true);
                return false;
            }
        }

        static void ScheduleRetry()
        {
            s_NextStartAttemptTime = EditorApplication.timeSinceStartup + AUTO_RESTART_INTERVAL;
        }

        static void ClearRetry()
        {
            s_NextStartAttemptTime = 0;
        }

        static void LogStartFailure(string message, bool error = false)
        {
            var now = EditorApplication.timeSinceStartup;
            if (s_LastFailureMessage == message && now - s_LastFailureLogTime < FAILURE_LOG_INTERVAL)
                return;

            s_LastFailureMessage = message;
            s_LastFailureLogTime = now;
            if (error) Debug.LogError(message);
            else Debug.LogWarning(message);
        }

        static void ClearStartFailure()
        {
            s_LastFailureMessage = null;
            s_LastFailureLogTime = 0;
        }

        static void StopListener()
        {
            s_Stopping = true;
            s_RestartPending = false;
            s_RefusingWork = true;
            ClearRetry();

            if (s_Listener == null) return;

            s_Cts?.Cancel();
            s_Cts?.Dispose();
            s_Cts = null;

            try
            {
                s_Listener.Stop();
                s_Listener.Close();
            }
            catch
            {
            }

            s_Listener = null;
        }

        /// <summary>
        /// Answers everything still in flight, then closes the listener. Abandoning a queued request
        /// instead would leave the CLI with a dropped connection it can only guess at, and the
        /// command would still run afterwards with nobody waiting for its result.
        /// </summary>
        static void OnBeforeAssemblyReload()
        {
            s_RefusingWork = true;
            AnswerPending("the domain is reloading", quitting: false);
            RequestTracker.AwaitForAnswers(PENDING_ANSWER_WAIT_MS);
            StopListener();
        }

        /// <summary>
        /// Answers every tracked request that has no answer yet. A request whose tool had already
        /// returned keeps its own answer (the ticket is answered once), so what is left to do for it
        /// is the <see cref="RequestTracker.AwaitForAnswers"/> window that follows: the response is
        /// written from the request thread, and closing the listener first would drop it.
        /// </summary>
        static void AnswerPending(string why, bool quitting)
        {
            var answered = RequestTracker.AnswerAllPending(ticket => RequestTracker.BuildReloadError(ticket, why, quitting));
            if (answered > 0)
                Debug.Log($"[UnityCliConnector] Answered {answered} in-flight request(s): {why}");
        }

        static void Stop()
        {
            var port = s_Port;
            // No error code for a quit: the editor is gone, so "wait for it and retry" would be a lie.
            AnswerPending("the editor is quitting", quitting: true);
            RequestTracker.AwaitForAnswers(PENDING_ANSWER_WAIT_MS);
            StopListener();
            Heartbeat.MarkStopped();
            Debug.Log($"[UnityCliConnector] HTTP server stopped (was port {port})");
        }

        static void ForceEditorUpdate()
        {
            s_MainContext?.Post(_ =>
            {
                try { EditorApplication.QueuePlayerLoopUpdate(); }
                catch { }
                try { UnityEditorInternal.InternalEditorUtility.RepaintAllViews(); }
                catch { }
            }, null);
        }

        static void ProcessQueue()
        {
            if (s_RestartPending)
            {
                s_RestartPending = false;
                Heartbeat.MarkStopped();
                ScheduleRetry();
            }

            if (!IsRunning && s_NextStartAttemptTime > 0 && EditorApplication.timeSinceStartup >= s_NextStartAttemptTime)
                Start();

            while (s_Queue.TryDequeue(out var item))
                ProcessItem(item);
        }

        static async void ProcessItem(WorkItem item)
        {
            try
            {
                var r = await CommandRouter.Dispatch(item.Command, item.Parameters, item.Ticket);
                // null means the request was answered while it waited for the command lock, so the
                // handler was skipped; a tool's own answer is never null.
                if (r != null) item.Ticket.TryAnswer(r);
            }
            catch (Exception ex)
            {
                item.Ticket.TryAnswer(new ErrorResponse(ex.Message));
            }
        }

        static async Task ListenLoop(HttpListener listener, CancellationTokenSource cts)
        {
            var ct = cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (listener == null || !listener.IsListening) break;

                    try
                    {
                        // ConfigureAwait(false) moves the accept loop and request handling
                        // off Unity's SynchronizationContext. Otherwise every continuation
                        // waits for the main thread, and a modal dialog would stop the
                        // server from accepting even RunOffMainThread commands.
                        var context = await listener.GetContextAsync().ConfigureAwait(false);
                        _ = HandleRequest(context);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UnityCliConnector] ListenLoop crashed: {ex.Message}");
            }
            finally
            {
                // The loop now ends on a thread-pool thread; listener bookkeeping is
                // owned by the main thread (Start/StopListener), so hand it back there.
                s_MainContext?.Post(_ =>
                {
                    if (!ct.IsCancellationRequested && !s_Stopping && ReferenceEquals(s_Listener, listener))
                    {
                        try
                        {
                            listener.Stop();
                            listener.Close();
                        }
                        catch
                        {
                        }
                        s_Listener = null;
                        if (ReferenceEquals(s_Cts, cts))
                            s_Cts = null;
                        s_RestartPending = true;
                    }
                }, null);
            }
        }

        static async Task HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            response.ContentType = "application/json";

            // Block browser cross-origin requests — CLI uses Go HTTP client (not subject to CORS)
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            var origin = request.Headers["Origin"];
            if (origin != null)
            {
                response.StatusCode = 403;
                var buf = Encoding.UTF8.GetBytes("{\"error\":\"Browser requests are not allowed\"}");
                response.ContentLength64 = buf.Length;
                await response.OutputStream.WriteAsync(buf, 0, buf.Length);
                response.Close();
                return;
            }

            object result;
            var command = "request";
            var ticket = (RequestTicket)null;

            try
            {
                if (request.HttpMethod != "POST" || request.Url.AbsolutePath != "/command")
                {
                    result = new ErrorResponse($"Expected POST /command, got {request.HttpMethod} {request.Url.AbsolutePath}");
                    response.StatusCode = 400;
                }
                else
                {
                    using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var json = JObject.Parse(body);

                    command = json["command"]?.ToString();
                    var parameters = json["params"] as JObject;

                    if (string.IsNullOrEmpty(command))
                    {
                        result = new ErrorResponse("Missing 'command' field");
                        response.StatusCode = 400;
                    }
                    else if (ToolDiscovery.IsOffMainThreadTool(command))
                    {
                        result = await CommandRouter.DispatchOffMainThread(command, parameters);
                    }
                    else if (s_RefusingWork)
                    {
                        // Fail fast rather than queue into a domain that is on its way out; the
                        // caller gets the same code it would get for a dropped request.
                        result = RequestTracker.BuildRefusedError(command, "a reload or quit is already under way");
                    }
                    else
                    {
                        ticket = RequestTracker.Track(new RequestTicket(command, QueueWaitMsFor(json, parameters)));
                        s_Queue.Enqueue(new WorkItem
                        {
                            Command = command,
                            Parameters = parameters,
                            Ticket = ticket,
                        });
                        ForceEditorUpdate();
                        result = await AwaitAnswer(ticket);
                        ReportSlowQueue(ticket, ref result);
                    }
                }
            }
            catch (Exception ex)
            {
                // A queued request that dies here (bad JSON, a crash while queueing) must still be
                // answered, or its ticket would sit in the tracker until a reload names it.
                result = new ErrorResponse($"Request error: {ex.Message}");
                response.StatusCode = 500;
                if (ticket != null) ticket.TryAnswer(result);
            }

            await WriteResponse(response, result, command);
            if (ticket != null) RequestTracker.Complete(ticket);
        }

        /// <summary>
        /// The queue deadline for this request: the client's own declared deadline, so the connector
        /// answers before the caller gives up, with `queue_deadline_ms` as the per-call override.
        /// </summary>
        static int QueueWaitMsFor(JObject json, JObject parameters)
        {
            var clientDeadlineMs = json["deadline_ms"]?.Value<int>() ?? 0;
            var overrideMs = parameters?["queue_deadline_ms"]?.Value<int>() ?? 0;
            return RequestTracker.QueueWaitMsFor(clientDeadlineMs, overrideMs);
        }

        static async Task<object> AwaitAnswer(RequestTicket ticket)
        {
            // The deadline bounds the wait for the main thread to start the handler, never the
            // handler's runtime: once a tool is running, answering early would report a result for a
            // command that is still changing the editor, and the caller's client timeout already
            // bounds how long it watches. If the deadline passes while the handler is waiting on the
            // command lock, the request is answered as "did not run" and CommandRouter skips it.
            var finished = await Task.WhenAny(ticket.Tcs.Task, Task.Delay(ticket.QueueWaitMs));
            if (finished != ticket.Tcs.Task)
                RequestTracker.TryAnswerWaiting(ticket, Diagnostics());

            return await ticket.Tcs.Task;
        }

        /// <summary>What the editor is doing, as far as a non-main thread can tell.</summary>
        static BusyDiagnostics Diagnostics()
        {
            var running = RequestTracker.Running;
            return new BusyDiagnostics(
                Heartbeat.LastState,
                running?.Command,
                // Time since that request was accepted, which for the request holding the command
                // lock is how long the editor has been busy.
                running?.WaitedMs ?? 0,
                RequestTracker.PendingCount,
                ModalWatchdog.Current?.title);
        }

        internal static void ReportSlowQueue(RequestTicket ticket, ref object result)
        {
            if (result is not SuccessResponse success) return;

            var waited = ticket.WaitedMs;
            if (waited < SLOW_QUEUE_WAIT_MS) return;

            // Merged into the tool's own meta (exec reports its compile cache there) so a slow pickup
            // stays visible in the answer, not only in the failure paths. The tool's meta is a CLR
            // object rather than a JObject, so it has to be converted before anything is added to it —
            // `as JObject` would silently replace it and drop the tool's own keys.
            var meta = success.meta as JObject
                ?? (success.meta == null ? new JObject() : JObject.FromObject(success.meta));
            meta["queue_wait_ms"] = waited;
            meta["queue_depth"] = RequestTracker.PendingCount;
            success.meta = meta;
        }

        static async Task WriteResponse(HttpListenerResponse response, object result, string command)
        {
            var buffer = Encoding.UTF8.GetBytes(ResponseJson.SerializeOrError(result, command));
            try
            {
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                // The caller may already be gone (its own timeout, a killed shell). Nothing left to do.
                System.Diagnostics.Debug.WriteLine(
                    $"[UnityCliConnector] Could not write the response to {command}: {ex.Message}");
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }
    }
}
