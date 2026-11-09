using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector.Tools;
using UnityEngine;

namespace UnityCliConnector.Tests
{
    /// <summary>
    /// Goes through <see cref="EditorScreenshot.HandleCommand"/> rather than the mode table, so
    /// the dispatch a caller actually gets is covered: a mode wired to the wrong capture function
    /// passes <see cref="ScreenshotModeTests"/> and fails here.
    /// </summary>
    [TestFixture]
    public class EditorScreenshotTests
    {
        string m_Directory;

        [SetUp]
        public void SetUp()
        {
            m_Directory = Path.Combine(Path.GetTempPath(), "unity-cli-screenshot-tests");
            Directory.CreateDirectory(m_Directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(m_Directory))
                Directory.Delete(m_Directory, true);
        }

        [Test]
        public async Task HandleCommand_GameViewOutsidePlayMode_NamesPlayModeAndTheOffscreenMode()
        {
            var error = ErrorOf(await EditorScreenshot.HandleCommand(
                new JObject { ["mode"] = "gameview" }));

            StringAssert.Contains("Play mode", error.message);
            StringAssert.Contains("--mode camera", error.message);
        }

        [Test]
        public async Task HandleCommand_GameViewOutsidePlayMode_LeavesAnExistingScreenshotAlone()
        {
            var path = Path.Combine(m_Directory, "hud.png");
            File.WriteAllText(path, "previous capture");

            ErrorOf(await EditorScreenshot.HandleCommand(
                new JObject { ["mode"] = "gameview", ["output_path"] = path }));

            Assert.AreEqual("previous capture", File.ReadAllText(path));
        }

        [Test]
        public async Task HandleCommand_CameraMode_WritesACompletePngAtTheRequestedSize()
        {
            var cameraObject = new GameObject("screenshot-test-camera");
            cameraObject.tag = "MainCamera";
            cameraObject.AddComponent<Camera>();

            var path = Path.Combine(m_Directory, "nested", "camera.png");
            try
            {
                var success = SuccessOf(await EditorScreenshot.HandleCommand(new JObject
                {
                    ["mode"] = "camera",
                    ["width"] = 64,
                    ["height"] = 48,
                    ["output_path"] = path,
                }));

                Assert.IsTrue(File.Exists(path), "the capture directory should be created");
                Assert.IsTrue(ImageDimensions.TryReadPng(path, out var width, out var height));
                Assert.AreEqual(64, width);
                Assert.AreEqual(48, height);
                // The Game view wait keys on this exact trailer, so check it against a real encoder.
                Assert.IsTrue(ImageDimensions.IsCompletePng(path));

                var data = JObject.FromObject(success.data);
                Assert.AreEqual(64, data["width"].Value<int>());
                Assert.AreEqual(48, data["height"].Value<int>());
                Assert.AreEqual("camera", data["mode"].Value<string>());
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void PartialCapturePath_IsASiblingThatNeverCollidesWithTheOutput()
        {
            var output = Path.Combine(m_Directory, "hud.png");
            var partial = EditorScreenshot.PartialCapturePath(output);

            Assert.AreEqual(Path.Combine(m_Directory, "hud.partial.png"), partial);
            Assert.AreNotEqual(output, partial);
            Assert.AreEqual(".png", Path.GetExtension(partial));
            Assert.AreEqual(m_Directory, Path.GetDirectoryName(partial));
        }

        [Test]
        public void PublishCapture_ReplacesTheOutputAndRemovesThePartial()
        {
            var output = Path.Combine(m_Directory, "hud.png");
            var partial = EditorScreenshot.PartialCapturePath(output);
            File.WriteAllText(output, "previous capture");
            File.WriteAllText(partial, "new capture");

            EditorScreenshot.PublishCapture(partial, output);

            Assert.AreEqual("new capture", File.ReadAllText(output));
            Assert.IsFalse(File.Exists(partial));
        }

        static object DataOf(object response)
        {
            if (response is ErrorResponse err)
                Assert.Fail($"expected success, got: {err.message}");
            return ((SuccessResponse)response).data;
        }

        static SuccessResponse SuccessOf(object response)
        {
            DataOf(response);
            return (SuccessResponse)response;
        }

        static ErrorResponse ErrorOf(object response)
        {
            if (response is ErrorResponse err) return err;
            Assert.Fail($"expected an error, got {response.GetType().Name}");
            return null;
        }
    }
}
