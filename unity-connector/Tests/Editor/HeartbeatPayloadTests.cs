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
        public void BuildStatus_OmitsBuildWhenNoBuildIsRunning()
        {
            var settings = new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore };
            var json = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(
                Heartbeat.BuildStatus("ready", false, 0, null), settings));

            Assert.IsFalse(json.ContainsKey("build"), "an idle editor must not report a build");
        }
    }
}
