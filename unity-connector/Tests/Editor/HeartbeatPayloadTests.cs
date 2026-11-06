using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector;
using UnityCliConnector.BuildJobs;

namespace UnityCliConnector.Tests
{
    /// The heartbeat file is read by the Go CLI's client.Instance struct, which polls these keys to
    /// decide whether a compile finished. A renamed key breaks the CLI silently (missing field, no
    /// error), so the payload shape is asserted here.
    [TestFixture]
    public class HeartbeatPayloadTests
    {
        [Test]
        public void BuildStatus_CarriesEveryKeyTheCLIPolls()
        {
            // Serialize the way Write does, so the test follows the production path.
            var json = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(
                Heartbeat.BuildStatus("compiling", true, 1_234_567_890_123L, null)));

            foreach (var key in new[]
                     {
                         "state", "projectPath", "port", "pid", "unityVersion", "connectorVersion",
                         "timestamp", "compileErrors", "compileFinishedAt", "modal", "build",
                     })
            {
                Assert.IsTrue(json.ContainsKey(key), $"heartbeat payload lost the '{key}' key the CLI reads");
            }

            Assert.AreEqual("compiling", json["state"].Value<string>());
            Assert.IsTrue(json["compileErrors"].Value<bool>());
            Assert.AreEqual(1_234_567_890_123L, json["compileFinishedAt"].Value<long>());
            Assert.That(json["timestamp"].Value<long>(), Is.Positive);
        }

        [Test]
        public void BuildStatus_CarriesStartingBeforeTheFirstMainThreadTick()
        {
            // The listener writes this immediately after Start(), before EditorApplication.update
            // has run once. It is the daemon's only evidence that a connector exists during a cold
            // import; losing the state would put it back to reporting None.
            var json = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(
                Heartbeat.BuildStatus("starting", false, 0, null)));

            Assert.AreEqual("starting", json["state"].Value<string>());
        }

        [Test]
        public void BuildStatus_CarriesStartingSinceOnlyWhileStarting()
        {
            var starting = JObject.FromObject(Heartbeat.BuildStatus("starting", false, 0, null, null, 1_700_000_000_000L));
            Assert.AreEqual(1_700_000_000_000L, starting["startingSince"].Value<long>(),
                "status derives 'starting for <age>' from timestamp - startingSince");

            var settings = new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore };
            var ready = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(
                Heartbeat.BuildStatus("ready", false, 0, null), settings));
            Assert.IsFalse(ready.ContainsKey("startingSince"), "the stamp must not outlive the starting state");
        }

        [Test]
        public void MarkCompileRequested_KeepsCompilingUntilTheCompileStartsOrFinishes()
        {
            CompilationGate.ResetForTests();
            Heartbeat.ResetStartupForTests();
            try
            {
                CompilationGate.SeedCompileClockForTests(100, 0);
                Heartbeat.MarkCompileRequested();
                Heartbeat.TickForTests();

                Assert.AreEqual("compiling", Heartbeat.LastState,
                    "a compile queued behind an import must not be reported as nothing_to_compile");

                CompilationGate.SeedCompileClockForTests(200, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Heartbeat.ResetTickIntervalForTests();
                Heartbeat.TickForTests();
                Assert.AreNotEqual("compiling", Heartbeat.LastState, "a finished compile releases the forced state");
            }
            finally
            {
                Heartbeat.TickForTests();
                // Restore the live clocks: leaving the gate reset is what made the heartbeat report
                // compileFinishedAt 0 for minutes after a test run while compile_status still knew the
                // compile. ResetForTests no longer writes the live key, but the in-memory clock is
                // this domain's answer until something restores it.
                CompilationGate.RestoreLiveStateForTests();
            }
        }

        [Test]
        public void CompileFinishedAt_FallsBackToProvenanceSoTheCLINeverSeesATrackedZero()
        {
            // Two records carry the compile clock (the gate's console-freshness key and the provenance
            // key). They are stamped by the same events, so one going missing must not make the
            // heartbeat claim this connector has no clock while compile_status names a valid compile.
            var provenanceBefore = CompileProvenance.DumpSessionForTests();
            CompilationGate.ResetForTests();
            try
            {
                // Stand in for a session whose provenance record survived while the gate's key did not.
                CompileProvenance.ResetForTests();
                CompileProvenance.NoteNothingToCompile(99_000);

                CompilationGate.SeedCompileClockForTests(100, 0);
                Assert.AreEqual(99_000, Heartbeat.ResolveCompileFinishedAt(),
                    "the gate's clock was lost, so the published one comes from the provenance record");

                CompilationGate.SeedCompileClockForTests(100, 4242);
                Assert.AreEqual(99_000, Heartbeat.ResolveCompileFinishedAt(),
                    "the later record wins: a clock never moves backwards for the CLI");

                CompilationGate.SeedCompileClockForTests(100, 123_456);
                Assert.AreEqual(123_456, Heartbeat.ResolveCompileFinishedAt(),
                    "and it is the gate's clock when that is the later one");
            }
            finally
            {
                CompilationGate.RestoreLiveStateForTests();
                CompileProvenance.RestoreSessionForTests(provenanceBefore);
                CompileProvenance.ReloadFromSessionForTests();
            }
        }

        [Test]
        public void StateFor_ReportsPausedWhileTheGameIsPaused()
        {
            // `wait` documents paused as a state to wait on, so the heartbeat has to publish it: a
            // paused game reported as "playing" reads as a run that is still going.
            Assert.AreEqual("paused", Heartbeat.StateFor(false, false, false, playing: true, paused: true));
            Assert.AreEqual("playing", Heartbeat.StateFor(false, false, false, playing: true, paused: false));
            Assert.AreEqual("ready", Heartbeat.StateFor(false, false, false, playing: false, paused: false));
            Assert.AreEqual("compiling", Heartbeat.StateFor(false, compiling: true, false, playing: false, false));
            Assert.AreEqual("refreshing", Heartbeat.StateFor(false, false, updating: true, playing: false, false));
            Assert.AreEqual("building", Heartbeat.StateFor(building: true, false, false, playing: false, false));
        }

        [Test]
        public void OnListenerStarted_WritesStartingAndTheStampBeforeTheFirstTick()
        {
            Heartbeat.ResetStartupForTests();
            try
            {
                Heartbeat.OnListenerStarted();

                var json = JObject.Parse(File.ReadAllText(Heartbeat.FilePathForTests()));
                Assert.AreEqual("starting", json["state"].Value<string>());
                var since = json["startingSince"].Value<long>();
                Assert.That(since, Is.GreaterThan(0), "without a stamp the CLI cannot show how long startup has been stuck");
                Assert.That(json["timestamp"].Value<long>(), Is.GreaterThanOrEqualTo(since));
                Assert.AreEqual("starting", Heartbeat.LastState);
            }
            finally
            {
                Heartbeat.TickForTests();
            }
        }

        [Test]
        public void OnListenerStarted_AfterATickDoesNotReportStartingAgain()
        {
            // A domain reload re-enters OnListenerStarted; the editor has ticked before, so the
            // reload must not make a routine compile look like a hung launch.
            Heartbeat.ResetStartupForTests();
            Heartbeat.MarkTickedForTests();
            try
            {
                Heartbeat.OnListenerStarted();
                Assert.AreNotEqual("starting", Heartbeat.LastState);
            }
            finally
            {
                Heartbeat.TickForTests();
            }
        }

        [Test]
        public void InstanceFileNameFor_StripsOnlyTheDataPathAssetsSuffix()
        {
            Assert.AreEqual("/Users/me/MyGame", Heartbeat.ProjectRootFromDataPath("/Users/me/MyGame/Assets"));
            Assert.AreEqual("/Users/me/Assets/MyGame",
                Heartbeat.ProjectRootFromDataPath("/Users/me/Assets/MyGame/Assets"),
                "a project directory called Assets is part of the hash key");
            Assert.AreEqual("/Users/me/MyGame",
                Heartbeat.ProjectRootFromDataPath("/Users/me/MyGame/Assets/"),
                "a trailing separator is the only other thing removed");

            // Pins the file name the Go CLI looks for; changing the key would orphan every heartbeat.
            Assert.AreEqual("0b6009a1027df197.json", Heartbeat.InstanceFileNameFor("/Users/me/MyGame"));
            Assert.AreEqual("9608307a62cc0695.json", Heartbeat.InstanceFileNameFor("/Users/me/Assets/MyGame"));
        }

        [Test]
        public void BuildStatus_ReportsTheRunningBuildSoWaitersDoNotSeeAWedge()
        {
            // While BuildPlayer holds the main thread, "building" plus the job id is all the CLI has
            // to tell a build apart from an editor that stopped answering.
            var json = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(
                Heartbeat.BuildStatus("building", false, 0, null, new BuildHeartbeatSnapshot
                {
                    id = "build-20240101-000000-abc123",
                    phase = "building",
                    profile = "macOS_dev",
                    target = "StandaloneOSX",
                    output = "/tmp/App.app",
                    elapsed_seconds = 41.5,
                    progress_percent = 0.5,
                    progress_description = "Compiling scripts",
                })));

            Assert.AreEqual("building", json["state"].Value<string>());
            Assert.AreEqual("build-20240101-000000-abc123", json["build"]["id"].Value<string>());
            Assert.AreEqual(41.5, json["build"]["elapsed_seconds"].Value<double>());
            Assert.AreEqual(0.5, json["build"]["progress_percent"].Value<double>());
            Assert.AreEqual("Compiling scripts", json["build"]["progress_description"].Value<string>());
        }

        [Test]
        public void MarkBuildFinished_DropsTheForcedBuildingState()
        {
            // MarkBuildStarted forces the state to "building"; a build that does not pump the update
            // loop returns with that still forced, and MarkBuildFinished must not write it back.
            Heartbeat.MarkBuildStarted();
            Heartbeat.MarkBuildFinished();

            Assert.AreNotEqual(BuildJobRecord.PhaseBuilding, Heartbeat.LastState);
        }

        [Test]
        public void BuildStatus_OmitsBuildWhenNoBuildIsRunning()
        {
            var settings = new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore };
            var json = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(
                Heartbeat.BuildStatus("ready", false, 0, null), settings));

            Assert.IsFalse(json.ContainsKey("build"), "an idle editor must not report a build");
        }
    }
}
