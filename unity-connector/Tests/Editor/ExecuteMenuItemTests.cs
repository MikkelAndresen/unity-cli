using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector.Tools;

namespace UnityCliConnector.Tests
{
    /// `unity-cli menu` with no path answered with the internal parameter name ('menu_path'
    /// parameter required.), which an agent reads as a bug rather than as a usage line.
    [TestFixture]
    public class ExecuteMenuItemTests
    {
        [Test]
        public void NoPath_AnswersWithAUsageLine()
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(ExecuteMenuItem.HandleCommand(new JObject())));

            Assert.IsFalse(json["success"].Value<bool>());
            StringAssert.StartsWith("usage: unity-cli menu \"<Menu/Path>\"", json["message"].Value<string>());
            StringAssert.DoesNotContain("menu_path", json["message"].Value<string>());
        }

        [Test]
        public void BlockedMenuItem_IsStillNamedByItsPath()
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(
                ExecuteMenuItem.HandleCommand(new JObject { ["args"] = new JArray("File/Quit") })));

            Assert.IsFalse(json["success"].Value<bool>());
            StringAssert.Contains("blocked", json["message"].Value<string>());
        }
    }
}
