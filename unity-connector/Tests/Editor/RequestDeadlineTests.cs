using System;
using System.Collections.Concurrent;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector;

namespace UnityCliConnector.Tests
{
    /// <summary>
    /// An accepted request must always get an answer, even when the editor's main thread never picks
    /// it up. The stage machine, the deadline policy and the reload answer are asserted here; the
    /// end-to-end path (a request racing a domain reload, a second request behind a blocked command)
    /// is exercised against the live editor and recorded in docs/bugs/8-exec-hang.md.
    /// </summary>
    [TestFixture]
    public class RequestDeadlineTests
    {
        [SetUp]
        public void SetUp()
        {
            RequestTracker.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            RequestTracker.Reset();
        }

        static readonly BusyDiagnostics Idle = new BusyDiagnostics("ready", null, 0, 0, null);

        // ---- deadline policy ----

        [Test]
        public void QueueWaitMs_UsesTheDefaultWhenTheClientDeclaredNoDeadline()
        {
            Assert.AreEqual(RequestTracker.DEFAULT_QUEUE_WAIT_MS, RequestTracker.QueueWaitMsFor(0, 0));
            Assert.AreEqual(RequestTracker.DEFAULT_QUEUE_WAIT_MS, RequestTracker.QueueWaitMsFor(-1, 0));
        }

        [Test]
        public void QueueWaitMs_LeavesTheClientTimeToReceiveTheAnswerInsideItsOwnBudget()
        {
            var wait = RequestTracker.QueueWaitMsFor(120000, 0);
            Assert.That(wait, Is.LessThan(120000), "the connector must answer before the client gives up");
            Assert.That(wait, Is.GreaterThanOrEqualTo(120000 - 13000), "the margin must not eat a large budget");
        }

        [Test]
        public void QueueWaitMs_ClampsToTheFloorAndTheCeiling()
        {
            Assert.AreEqual(RequestTracker.MIN_QUEUE_WAIT_MS, RequestTracker.QueueWaitMsFor(100, 0));
            Assert.AreEqual(RequestTracker.MAX_QUEUE_WAIT_MS, RequestTracker.QueueWaitMsFor(3_600_000, 0));
            Assert.AreEqual(RequestTracker.MAX_QUEUE_WAIT_MS, RequestTracker.QueueWaitMsFor(0, 7_200_000));
        }

        [Test]
        public void QueueWaitMs_PerCallOverrideBeatsTheClientDeadline()
        {
            Assert.AreEqual(45000, RequestTracker.QueueWaitMsFor(5000, 45000));
        }

        // ---- the stage machine: answered without running, or running without being answered ----

        [Test]
        public void TryBegin_FailsOnceTheRequestHasBeenAnswered()
        {
            var ticket = new RequestTicket("exec", 1000);
            Assert.IsTrue(ticket.TryAnswerIfWaiting(new ErrorResponse("late")));
            Assert.IsFalse(ticket.TryBegin(), "an answered request must not start running afterwards");
            Assert.IsFalse(ticket.Tcs.Task.Result is SuccessResponse);
        }

        [Test]
        public void TryAnswerIfWaiting_FailsOnceAHandlerIsRunning()
        {
            var ticket = new RequestTicket("exec", 1000);
            Assert.IsTrue(ticket.TryBegin());
            Assert.IsFalse(ticket.TryAnswerIfWaiting(new ErrorResponse("too late")),
                "a running command must not be reported as skipped");
            Assert.AreEqual(RequestStage.Running, ticket.Stage);
        }

        [Test]
        public void ARequestIsAnsweredOnlyOnce()
        {
            var ticket = new RequestTicket("exec", 1000);
            RequestTracker.Track(ticket);

            Assert.IsTrue(RequestTracker.TryAnswerWaiting(ticket, Idle));
            Assert.IsFalse(ticket.TryAnswer(new SuccessResponse("second")));
            Assert.IsInstanceOf<ErrorResponse>(ticket.Tcs.Task.Result);
        }

        // ---- the deadline answer ----

        [Test]
        public void TryAnswerWaiting_ReportsTheCommandDidNotRunAndWhy()
        {
            var running = RequestTracker.Track(new RequestTicket("test", 60000));
            Assert.IsTrue(running.TryBegin());
            var waiting = RequestTracker.Track(new RequestTicket("exec", 30000));

            var diag = new BusyDiagnostics("playing", "test", 42000, 2, null);
            Assert.IsTrue(RequestTracker.TryAnswerWaiting(waiting, diag));

            var error = (ErrorResponse)waiting.Tcs.Task.Result;
            Assert.That(error.message, Does.Contain("exec was queued but the main thread did not pick it up within 30s"));
            Assert.That(error.message, Does.Contain("blocked by the running command test (42s)"));
            Assert.That(error.message, Does.Contain("heartbeat: playing"));
            Assert.That(error.message, Does.Contain("The command did not run"));

            var json = JObject.Parse(JsonConvert.SerializeObject(error));
            Assert.AreEqual(ErrorCodes.MainThreadBusy, json["data"]["code"].Value<string>());
            Assert.AreEqual("exec", json["data"]["command"].Value<string>());
            Assert.AreEqual(30000, json["data"]["queue_deadline_ms"].Value<int>());
            Assert.AreEqual("test", json["data"]["running_command"].Value<string>());
            Assert.AreEqual(false, json["data"]["started"].Value<bool>());
        }

        [Test]
        public void TryAnswerWaiting_NamesAModalDialogAndRefusesToAnswerAStartedRequest()
        {
            var running = RequestTracker.Track(new RequestTicket("exec", 60000));
            Assert.IsTrue(running.TryBegin());
            var modal = RequestTracker.Track(new RequestTicket("scene_get", 1000));

            var diag = new BusyDiagnostics("modal", "exec", 9000, 2, "Save scene?");
            Assert.IsTrue(RequestTracker.TryAnswerWaiting(modal, diag));
            var message = ((ErrorResponse)modal.Tcs.Task.Result).message;
            Assert.That(message, Does.Contain("modal dialog \"Save scene?\""));

            Assert.IsFalse(RequestTracker.TryAnswerWaiting(running, diag),
                "the handler is already running, so its own answer is the only truthful one");
        }

        [Test]
        public void FailureData_CarriesEveryKeyTheCLIParses()
        {
            // cmd/connector_error.go switches on data.code and the CLI prints the rest verbatim;
            // a renamed key is a silent regression there, so the shape is pinned here.
            var ticket = RequestTracker.Track(new RequestTicket("exec", 5000));
            RequestTracker.TryAnswerWaiting(ticket, new BusyDiagnostics("compiling", null, 0, 1, null));

            var json = JObject.Parse(JsonConvert.SerializeObject(ticket.Tcs.Task.Result))["data"].Value<JObject>();
            foreach (var key in new[]
                     {
                         "code", "command", "waited_ms", "queue_deadline_ms", "queue_depth",
                         "running_command", "running_ms", "heartbeat_state", "modal", "started",
                     })
            {
                Assert.IsTrue(json.ContainsKey(key), $"failure data lost the '{key}' key the CLI reads");
            }
        }

        // ---- reload / quit ----

        [Test]
        public void AnswerAllPending_AnswersQueuedAndRunningRequestsWithDistinctMessages()
        {
            var queued = RequestTracker.Track(new RequestTicket("exec", 60000));
            var running = RequestTracker.Track(new RequestTicket("test_run", 60000));
            Assert.IsTrue(running.TryBegin());

            var answered = RequestTracker.AnswerAllPending(
                ticket => RequestTracker.BuildReloadError(ticket, "the domain is reloading"));
            Assert.AreEqual(2, answered);

            var queuedError = (ErrorResponse)queued.Tcs.Task.Result;
            Assert.That(queuedError.message, Does.Contain("exec never ran: Unity stopped answering"));
            Assert.That(queuedError.message, Does.Contain("Retry it once Unity answers again"));

            var runningError = (ErrorResponse)running.Tcs.Task.Result;
            Assert.That(runningError.message, Does.Contain("test_run had already started when Unity stopped answering"));
            Assert.That(runningError.message, Does.Contain("its return value was lost"));
            Assert.That(runningError.message, Does.Not.Contain("never ran"),
                "the handler was entered, so saying it did not run would be a lie");

            var queuedJson = JObject.Parse(JsonConvert.SerializeObject(queued.Tcs.Task.Result));
            var runningJson = JObject.Parse(JsonConvert.SerializeObject(running.Tcs.Task.Result));
            Assert.AreEqual(ErrorCodes.DomainReload, queuedJson["data"]["code"].Value<string>());
            Assert.AreEqual(ErrorCodes.ReloadDuringCommand, runningJson["data"]["code"].Value<string>(),
                "a command that ran must not be reported as one the caller can safely repeat");
            Assert.AreEqual(false, queuedJson["data"]["started"].Value<bool>());
            Assert.AreEqual(true, runningJson["data"]["started"].Value<bool>());

            Assert.IsFalse(queued.TryBegin(), "a request answered by the reload must not run later");
        }

        [Test]
        public void BuildReloadError_KeepsTheRealResultOfACompletedRequest()
        {
            // A tool that had already returned keeps its own answer: the reload only answers what is
            // still unanswered, so the caller gets the value rather than a reload notice for work that
            // finished. RequestTracker.AwaitForAnswers is what gives the unwritten response its window.
            var completed = RequestTracker.Track(new RequestTicket("exec", 60000));
            Assert.IsTrue(completed.TryBegin());
            Assert.IsTrue(completed.TryAnswer(new SuccessResponse("OK", 42)));

            Assert.AreEqual(0, RequestTracker.AnswerAllPending(
                ticket => RequestTracker.BuildReloadError(ticket, "the domain is reloading")));

            var json = JObject.Parse(JsonConvert.SerializeObject(completed.Tcs.Task.Result));
            Assert.AreEqual(true, json["success"].Value<bool>());
            Assert.AreEqual(42, json["data"].Value<int>());
        }

        [Test]
        public void BuildReloadError_AQuitAnswersWithoutAWaitableCode()
        {
            var running = RequestTracker.Track(new RequestTicket("exec", 60000));
            Assert.IsTrue(running.TryBegin());

            var error = RequestTracker.BuildReloadError(running, "the editor is quitting", quitting: true);
            Assert.That(error.message, Does.Contain("exec had already started when Unity stopped answering"));
            Assert.That(error.message, Does.Not.Contain("answers again once the reload is over"),
                "a quit editor is not coming back, so the CLI must not be told to wait for it");

            var json = JObject.Parse(JsonConvert.SerializeObject(error));
            Assert.IsNull(json["data"]["code"].Value<string>());
            Assert.AreEqual("stopped", json["data"]["heartbeat_state"].Value<string>());
        }

        [Test]
        public void BuildReloadError_AQueuedRequestDuringAQuitIsNotToldToWait()
        {
            // The editor is gone, so "retry it once Unity answers again" would be advice the caller
            // can never act on; a queued request only has to learn that nothing was applied.
            var queued = RequestTracker.Track(new RequestTicket("exec", 60000));

            var error = RequestTracker.BuildReloadError(queued, "the editor is quitting", quitting: true);

            Assert.That(error.message, Does.Contain("exec never ran"));
            Assert.That(error.message, Does.Not.Contain("Retry it once Unity answers again"),
                "a quit editor is not coming back, so there is no answer to wait for");
            var json = JObject.Parse(JsonConvert.SerializeObject(error));
            Assert.IsNull(json["data"]["code"].Value<string>());
        }

        [Test]
        public void BuildRefusedError_SaysNothingRanAndCarriesTheReloadCode()
        {
            // A request that arrives once the reload has begun must be refused on the spot instead of
            // queued into a domain that is about to be wiped.
            var error = RequestTracker.BuildRefusedError("exec", "a reload or quit is already under way");
            Assert.That(error.message, Does.Contain("exec did not run: Unity is not taking new main-thread commands"));

            var json = JObject.Parse(JsonConvert.SerializeObject(error));
            Assert.AreEqual(ErrorCodes.DomainReload, json["data"]["code"].Value<string>());
            Assert.AreEqual("exec", json["data"]["command"].Value<string>());
            Assert.AreEqual(false, json["data"]["started"].Value<bool>());
        }

        [Test]
        public void AwaitForAnswers_ReturnsOnceEveryAnswerHasBeenWritten()
        {
            var first = RequestTracker.Track(new RequestTicket("exec", 60000));
            var second = RequestTracker.Track(new RequestTicket("scene_get", 60000));
            RequestTracker.AnswerAllPending(ticket => new ErrorResponse("reloaded"));

            // Nothing has been written yet, so the wait has to time out rather than report success.
            Assert.AreEqual(2, RequestTracker.AwaitForAnswers(200));

            RequestTracker.Complete(first);
            RequestTracker.Complete(second);
            Assert.AreEqual(0, RequestTracker.AwaitForAnswers(200));
        }

        [Test]
        public void Running_ReportsOnlyTheRequestInsideAHandler()
        {
            var queued = RequestTracker.Track(new RequestTicket("exec", 60000));
            Assert.IsNull(RequestTracker.Running);

            Assert.IsTrue(queued.TryBegin());
            Assert.AreSame(queued, RequestTracker.Running);
            Assert.That(RequestTracker.Running.WaitedMs, Is.GreaterThanOrEqualTo(0));

            RequestTracker.Complete(queued);
            Assert.IsNull(RequestTracker.Running);
        }

        [Test]
        public void ReportSlowQueue_KeepsTheToolSOwnMetaAlongsideTheQueueNumbers()
        {
            // exec's meta says what the answer means (compile cache, and the play-mode change a snippet
            // started). Overwriting it with the queue numbers hid that from every slow pickup.
            var ticket = new RequestTicket("exec", 60000);
            Thread.Sleep(HttpServer.SLOW_QUEUE_WAIT_MS + 150);
            object result = new SuccessResponse("OK", 42, new { compile = "cached", state_change = "entered play mode" });

            HttpServer.ReportSlowQueue(ticket, ref result);

            var json = JObject.Parse(JsonConvert.SerializeObject(result));
            Assert.AreEqual("cached", json["meta"]["compile"].Value<string>());
            Assert.AreEqual("entered play mode", json["meta"]["state_change"].Value<string>());
            Assert.That(json["meta"]["queue_wait_ms"].Value<int>(), Is.GreaterThanOrEqualTo(HttpServer.SLOW_QUEUE_WAIT_MS));
        }

        // ---- response serialization can never be the thing that leaves a connection unanswered ----

        sealed class Loop
        {
            public object Self => this;
            public override string ToString() => "loop";
        }

        [Test]
        public void SerializeOrError_RoundTripsAnOrdinaryResult()
        {
            var json = ResponseJson.SerializeOrError(new SuccessResponse("ok", new { value = 42 }), "exec");
            var parsed = JObject.Parse(json);
            Assert.AreEqual(true, parsed["success"].Value<bool>());
            Assert.AreEqual(42, parsed["data"]["value"].Value<int>());
        }

        [Test]
        public void SerializeOrError_AnswersWithAnErrorWhenThePayloadCannotBeSerialized()
        {
            // A cyclic payload throws inside JsonConvert. Before the fix that exception escaped the
            // request handler: the connection closed with no body and the CLI reported success.
            var json = ResponseJson.SerializeOrError(new Loop(), "exec");
            var parsed = JObject.Parse(json);

            Assert.AreEqual(false, parsed["success"].Value<bool>());
            Assert.AreEqual(ErrorCodes.SerializationFailed, parsed["data"]["code"].Value<string>());
            Assert.AreEqual("exec", parsed["data"]["command"].Value<string>());
            Assert.That(parsed["message"].Value<string>(), Does.Contain("could not be serialized"));
        }

        [Test]
        public void SerializeOrError_KeepsTheHandWrittenFallbackValidJson()
        {
            var parsed = JObject.Parse(ResponseJson.LAST_RESORT);
            Assert.AreEqual(false, parsed["success"].Value<bool>());
            Assert.AreEqual(ErrorCodes.SerializationFailed, parsed["data"]["code"].Value<string>());
        }

        [Test]
        public void TheRouterSkipsARequestThatWasAlreadyAnswered()
        {
            // The command lock is taken before a handler runs, so a request answered while it was
            // waiting for that lock must not apply its side effects afterwards.
            //
            // The guard is asserted here rather than by calling CommandRouter.Dispatch: the test_run
            // command holds the command lock for the whole EditMode run, and a nested Dispatch from
            // inside a test deadlocks the editor main thread (its await needs the pump the test is
            // blocking). That wedge is recorded in docs/bugs/8-exec-hang.md.
            var answered = RequestTracker.Track(new RequestTicket("exec", 60000));
            Assert.IsTrue(RequestTracker.TryAnswerWaiting(answered, Idle));
            Assert.IsFalse(CommandRouter.ShouldRun(answered));

            var waiting = RequestTracker.Track(new RequestTicket("exec", 60000));
            Assert.IsTrue(CommandRouter.ShouldRun(waiting));
            Assert.AreEqual(RequestStage.Running, waiting.Stage);
            Assert.IsFalse(CommandRouter.ShouldRun(waiting), "the same request never runs twice");

            Assert.IsTrue(CommandRouter.ShouldRun(null), "a request with no ticket always runs");
        }
    }
}
