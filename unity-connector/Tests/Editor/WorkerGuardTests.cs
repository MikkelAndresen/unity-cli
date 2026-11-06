using System.IO;
using NUnit.Framework;
using UnityCliConnector.BuildJobs;
using UnityCliConnector.TestRunner;

namespace UnityCliConnector.Tests
{
    /// An AssetImportWorker shares the editor's projectPath, so its writes land in the same Temp
    /// files. The worker guards are what keeps a worker from consuming the editor's pending test
    /// marker or rewriting its build jobs.
    [TestFixture]
    public class WorkerGuardTests
    {
        [Test]
        public void BuildJobStore_InAWorker_RefusesToRecordOrPersistAJob()
        {
            var before = BuildJobStore.CountForTests;
            BuildJobStore.SetWorkerForTests(true);
            try
            {
                BuildJobStore.Add(new BuildJobRecord
                {
                    id = "worker-guard-test",
                    phase = BuildJobRecord.PhaseQueued,
                });
                Assert.AreEqual(before, BuildJobStore.CountForTests,
                    "a worker must not start its own (empty) job list that SaveLocked would write out");
            }
            finally
            {
                BuildJobStore.SetWorkerForTests(false);
            }
        }

        [Test]
        public void TestRunnerState_InAWorker_LeavesTheEditorsPendingMarkerAlone()
        {
            const int port = 52199;
            var path = Path.Combine(RunTests.StatusDir, $"test-pending-{port}.json");
            TestRunnerState.MarkPending(port, "worker-guard-test");
            Assert.IsTrue(File.Exists(path), "the marker is the editor's evidence that a run is pending");

            TestRunnerState.SetWorkerForTests(true);
            try
            {
                TestRunnerState.RunAfterAssemblyReloadForTests();
                Assert.IsTrue(File.Exists(path),
                    "a worker's reload must not reattach to (and later clear) the editor's test run");
            }
            finally
            {
                TestRunnerState.SetWorkerForTests(false);
                TestRunnerState.ClearPending(port);
            }
        }
    }
}
