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
        public void SaveAndDiscardFlags_DefaultToFalse_AndParseTrue()
        {
            Assert.IsFalse(new ToolParams(new JObject()).GetBool("save"));
            Assert.IsFalse(new ToolParams(new JObject()).GetBool("discard"));
            Assert.IsTrue(new ToolParams(new JObject { ["save"] = true }).GetBool("save"));
            Assert.IsTrue(new ToolParams(new JObject { ["save"] = "true" }).GetBool("save"));
            Assert.IsTrue(new ToolParams(new JObject { ["discard"] = true }).GetBool("discard"));
        }

        [Test]
        public void Report_InPlayMode_SaysSceneSavesWereSkippedInsteadOfClaimingThem()
        {
            var scenes = new QuitEditor.SceneState();
            scenes.DirtyNames.Add("Assets/Main.unity");

            var playMode = JObject.FromObject(QuitEditor.BuildReport(save: true, discard: false,
                playMode: true, scenes, new string[0]));
            Assert.IsFalse(playMode["save"].Value<bool>(), "no scene was saved");
            Assert.AreEqual("playmode", playMode["saveSkipped"].Value<string>());
            Assert.IsTrue(playMode["savedAssets"].Value<bool>(), "AssetDatabase.SaveAssets still runs");
            Assert.That(playMode["discardedScenes"].Values<string>(), Does.Contain("Assets/Main.unity"));

            var editMode = JObject.FromObject(QuitEditor.BuildReport(save: true, discard: false,
                playMode: false, scenes, new string[0]));
            Assert.IsTrue(editMode["save"].Value<bool>());
            Assert.IsNull(editMode["saveSkipped"].Value<string>());
        }
    }
}
