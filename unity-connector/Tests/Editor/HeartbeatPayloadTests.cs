using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector;

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
                         "timestamp", "compileErrors", "compileFinishedAt", "modal",
                     })
            {
                Assert.IsTrue(json.ContainsKey(key), $"heartbeat payload lost the '{key}' key the CLI reads");
            }

            Assert.AreEqual("compiling", json["state"].Value<string>());
            Assert.IsTrue(json["compileErrors"].Value<bool>());
            Assert.AreEqual(1_234_567_890_123L, json["compileFinishedAt"].Value<long>());
            Assert.That(json["timestamp"].Value<long>(), Is.Positive);
        }
    }
}
