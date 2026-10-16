using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector;
using UnityCliConnector.Tools;

namespace UnityCliConnector.Tests
{
    /// <summary>
    /// Covers issue #18: exec used to hand a returned Task straight to Serialize, so
    /// the agent saw "System.Threading.Tasks.Task`1[System.Int32]" and had to park
    /// results in AppDomain data and poll. Each test here goes through HandleCommand,
    /// i.e. the real compile → invoke → resolve path.
    /// </summary>
    [TestFixture]
    public class ExecAsyncTests
    {
        static async Task<object> Exec(string code, int? awaitTimeoutMs = null)
        {
            var p = new JObject { ["code"] = code };
            if (awaitTimeoutMs.HasValue) p["await_timeout"] = awaitTimeoutMs.Value;
            return await ExecuteCsharp.HandleCommand(p);
        }

        static object DataOf(object response)
        {
            if (response is ErrorResponse err)
                Assert.Fail($"expected success, got: {err.message}");
            return ((SuccessResponse)response).data;
        }

        static ErrorResponse ErrorOf(object response)
        {
            if (response is ErrorResponse err) return err;
            Assert.Fail($"expected an error, got success with data {((SuccessResponse)response).data}");
            return null;
        }

        // ─── Returned Tasks are awaited ─────────────────────────

        [Test]
        public async Task SyncBody_StillReturnsPlainValue()
        {
            Assert.AreEqual(2, DataOf(await Exec("return 1 + 1;")));
        }

        [Test]
        public async Task ReturnedTaskOfT_IsAwaitedAndUnwrapped()
        {
            Assert.AreEqual(42, DataOf(await Exec("return Task.FromResult(42);")));
        }

        [Test]
        public async Task ReturnedTask_RunsOffTheMainThread()
        {
            var code = "return Task.Run(() => { System.Threading.Thread.Sleep(30); return \"bg\"; });";
            Assert.AreEqual("bg", DataOf(await Exec(code)));
        }

        [Test]
        public async Task NestedReturnedTasks_AreUnwrappedRecursively()
        {
            Assert.AreEqual(5, DataOf(await Exec("return Task.FromResult(Task.FromResult(5));")));
        }

        [Test]
        public async Task FaultedTask_ReportsRuntimeErrorNotTaskInternals()
        {
            var code = "return Task.Run(() => { throw new System.InvalidOperationException(\"boom\"); });";
            StringAssert.Contains("boom", ErrorOf(await Exec(code)).message);
        }

        [Test]
        public async Task TaskNestedInsideReturnedObject_IsUnwrappedNotReflectedToBits()
        {
            var code = "return new Dictionary<string, object> { { \"t\", Task.FromResult(3) } };";
            var data = JObject.FromObject(DataOf(await Exec(code)));
            Assert.AreEqual(3, data["t"].Value<int>(), "a Task in a payload must not serialize as AsyncState/Status fields");
        }

        // ─── await in the submitted body ────────────────────────

        [Test]
        public async Task AwaitInBody_Compiles()
        {
            Assert.AreEqual(7, DataOf(await Exec("await Task.Delay(10); return 7;")));
        }

        [Test]
        public async Task AwaitCompletedByEditorCallback_ResumesOnTheMainThread()
        {
            // The awaited task can only finish if the editor keeps ticking, so this
            // fails outright if exec ever blocks the main thread waiting for it.
            var code =
                "var tcs = new TaskCompletionSource<int>();\n" +
                "EditorApplication.CallbackFunction h = null;\n" +
                "h = () => { EditorApplication.update -= h; tcs.SetResult(System.Threading.Thread.CurrentThread.ManagedThreadId); };\n" +
                "EditorApplication.update += h;\n" +
                "await tcs.Task;\n" +
                "return \"waited=\" + tcs.Task.Result + \" resumed=\" + System.Threading.Thread.CurrentThread.ManagedThreadId;";

            var data = (string)DataOf(await Exec(code));
            StringAssert.StartsWith("waited=1", data, "the completing callback must run on the main thread");
            StringAssert.EndsWith("resumed=1", data, "the continuation must come back to the main thread");
        }

        [Test]
        public async Task RefStructLocal_StillCompiles_BecauseSyncSignatureIsChosen()
        {
            // CS4012: ref-struct locals are illegal in async methods. The signature
            // picker must not switch to async unless the body asks for it.
            Assert.AreEqual(3, DataOf(await Exec("Span<int> s = stackalloc int[3]; return s.Length;")));
        }

        // ─── Bounded wait ───────────────────────────────────────

        [Test]
        public async Task UnfinishedTask_ReportsTimedOutMarker()
        {
            var err = ErrorOf(await Exec("return Task.Delay(5000);", 200));
            StringAssert.Contains("timed out", err.message);

            var data = JObject.FromObject(err.data);
            Assert.AreEqual(true, data["timed_out"].Value<bool>());
            Assert.AreEqual("WaitingForActivation", data["status"].Value<string>());
            Assert.AreEqual(200, data["await_timeout_ms"].Value<int>());
        }

        [Test]
        public async Task AwaitTimeout_Zero_WaitsForCompletionInsteadOfTimingOut()
        {
            Assert.AreEqual("late", DataOf(await Exec("return Task.Run(() => { System.Threading.Thread.Sleep(120); return \"late\"; });", 0)));
        }

        // ─── Coroutine / IEnumerator path ───────────────────────

        [Test]
        public async Task IteratorBody_IsPumpedOneStepPerEditorUpdate()
        {
            var response = await Exec("yield return null; yield return null;");
            Assert.IsInstanceOf<SuccessResponse>(response);
            StringAssert.Contains("2 steps", ((SuccessResponse)response).message);
        }

        [Test]
        public async Task IteratorYieldingTask_IsAwaited()
        {
            var response = await Exec("yield return Task.Delay(20); yield return null;");
            StringAssert.Contains("2 steps", ((SuccessResponse)response).message);
        }

        [Test]
        public async Task IteratorYieldingValues_CollectsThemAsResults()
        {
            var data = DataOf(await Exec("yield return \"a\"; yield return null; yield return \"b\";"));
            Assert.AreEqual(new List<object> { "a", "b" }, data);
        }

        [Test]
        public async Task ReturnedEnumerator_IsPumpedAndCollected()
        {
            var data = DataOf(await Exec("return new List<int> { 1, 2, 3 }.GetEnumerator();"));
            Assert.AreEqual(new List<object> { 1, 2, 3 }, data);
        }

        [Test]
        public async Task IteratorYieldingYieldInstruction_FailsWithPointerToAwait()
        {
            // Silently ignoring WaitForSeconds would run the routine at frame rate and
            // look like the wait happened.
            var err = ErrorOf(await Exec("yield return new WaitForSeconds(1);"));
            StringAssert.Contains("WaitForSeconds", err.message);
            StringAssert.Contains("Task.Delay", err.message);
        }

        [Test]
        public async Task IteratorThrowing_ReportsRuntimeError()
        {
            var err = ErrorOf(await Exec("yield return 1; throw new System.InvalidOperationException(\"pump boom\");"));
            StringAssert.Contains("pump boom", err.message);
        }

        [Test]
        public async Task EndlessIterator_ReportsTimedOutWithStepsTaken()
        {
            var err = ErrorOf(await Exec("while (true) yield return null;", 300));
            var data = JObject.FromObject(err.data);
            Assert.AreEqual(true, data["timed_out"].Value<bool>());
            Assert.Greater(data["steps"].Value<int>(), 0, "the pump should report how far it got");
        }

        // ─── ExecAwait unit level (no compile round-trip) ───────

        [Test]
        public async Task ExecAwait_PassesPlainValuesThrough()
        {
            var outcome = await ExecAwait.ResolveAsync("hello", 1000);
            Assert.AreEqual("hello", outcome.Value);
            Assert.IsFalse(outcome.TimedOut);
            Assert.IsNull(outcome.Error);
        }

        [Test]
        public async Task ExecAwait_TimedOutOutcomeCarriesStatus()
        {
            var outcome = await ExecAwait.ResolveAsync(Task.Delay(2000), 100);
            Assert.IsTrue(outcome.TimedOut);
            Assert.AreEqual("WaitingForActivation", outcome.Status);
            Assert.IsNull(outcome.Value);
        }

        [Test]
        public async Task ExecAwait_FaultedTaskSurfacesInnerException()
        {
            var t = Task.Run(() => { throw new ArgumentException("inner detail"); });
            var outcome = await ExecAwait.ResolveAsync(t, 5000);
            Assert.IsInstanceOf<ArgumentException>(outcome.Error);
            StringAssert.Contains("inner detail", outcome.Error.Message);
        }

        [Test]
        public async Task ExecAwait_NestedEnumeratorInsideRoutineIsFlattened()
        {
            var outcome = await ExecAwait.ResolveAsync(Outer(), 5000);
            Assert.AreEqual(new List<object> { 1, 2, 3 }, outcome.Value);

            static IEnumerator<object> Outer()
            {
                yield return new List<int> { 1, 2 }.GetEnumerator();
                yield return 3;
            }
        }
    }
}
