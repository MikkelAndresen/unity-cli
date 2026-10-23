using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector.Tools;

namespace UnityCliConnector.Tests
{
    /// <summary>
    /// Registration and param parsing only — never calls HandleCommand, which would
    /// schedule an editor quit.
    /// </summary>
    [TestFixture]
    public class QuitEditorTests
    {
        [Test]
        public void Tool_IsRegisteredAsQuitEditor_AndBypassesCompilationGate()
        {
            var attr = typeof(QuitEditor).GetCustomAttribute<UnityCliToolAttribute>();

            Assert.IsNotNull(attr);
            Assert.AreEqual("quit_editor", attr.Name ?? StringCaseUtility.ToSnakeCase(nameof(QuitEditor)));
            Assert.IsTrue(attr.SkipCompilationGate);
        }

        [Test]
        public void SaveFlag_DefaultsToFalse_AndParsesTrue()
        {
            Assert.IsFalse(new ToolParams(new JObject()).GetBool("save"));
            Assert.IsTrue(new ToolParams(new JObject { ["save"] = true }).GetBool("save"));
            Assert.IsTrue(new ToolParams(new JObject { ["save"] = "true" }).GetBool("save"));
        }
    }
}
