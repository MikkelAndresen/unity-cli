using System;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector.Modal;
using UnityCliConnector.Tools;
using UnityEditor;

namespace UnityCliConnector.Tests
{
    [TestFixture]
    public class ModalDialogTests
    {
        static readonly ModalButton[] s_SaveDialogButtons =
        {
            new(0, "Save", false, false, true),
            new(1, "Don’t Save", false, false, true),
            new(2, "Cancel", false, true, true),
        };

        [Test]
        public void ResolveButton_ExactTitle()
        {
            Assert.IsNull(ModalInspector.ResolveButton(s_SaveDialogButtons, "Cancel", null, out var match));
            Assert.AreEqual(2, match.index);
        }

        [Test]
        public void ResolveButton_LooseTitle_IgnoresCaseAndCurlyApostrophe()
        {
            Assert.IsNull(ModalInspector.ResolveButton(s_SaveDialogButtons, " don't save ", null, out var match));
            Assert.AreEqual(1, match.index);
        }

        [Test]
        public void ResolveButton_ByIndex()
        {
            Assert.IsNull(ModalInspector.ResolveButton(s_SaveDialogButtons, null, 0, out var match));
            Assert.AreEqual("Save", match.title);
        }

        [Test]
        public void ResolveButton_UnknownTitle_ListsButtons()
        {
            var error = ModalInspector.ResolveButton(s_SaveDialogButtons, "Discard", null, out var match);
            Assert.IsNull(match);
            StringAssert.Contains("[2] \"Cancel\"", error);
        }

        [Test]
        public void ResolveButton_IndexOutOfRange()
        {
            Assert.IsNotNull(ModalInspector.ResolveButton(s_SaveDialogButtons, null, 3, out var match));
            Assert.IsNull(match);
        }

        [Test]
        public void ResolveButton_AmbiguousLooseTitle()
        {
            var buttons = new[] { new ModalButton(0, "OK", false, false, true), new ModalButton(1, "ok", false, false, true) };
            StringAssert.Contains("--index", ModalInspector.ResolveButton(buttons, "Ok", null, out _));
        }

        [Test]
        public void ResolveButton_DisabledButton()
        {
            var buttons = new[] { new ModalButton(0, "Apply", false, false, false) };
            StringAssert.Contains("disabled", ModalInspector.ResolveButton(buttons, "Apply", null, out var match));
            Assert.IsNull(match);
        }

        [Test]
        public void ResolveButton_NoButtons()
        {
            StringAssert.Contains("no native buttons",
                ModalInspector.ResolveButton(Array.Empty<ModalButton>(), "OK", null, out _));
        }

        [Test]
        public void ResolveButton_NeitherTitleNorIndex()
        {
            StringAssert.Contains("--button", ModalInspector.ResolveButton(s_SaveDialogButtons, null, null, out _));
        }

        [Test]
        public void ModalTool_RunsOffMainThread()
        {
            Assert.IsTrue(ToolDiscovery.IsOffMainThreadTool("modal"));
            Assert.IsFalse(ToolDiscovery.IsOffMainThreadTool("exec"));
        }

        [Test]
        public void List_WithoutModal_ReportsNotPresent()
        {
            if (!ModalDispatcher.IsSupported) Assert.Ignore("Modal handling is macOS-only.");

            var response = JObject.FromObject(ManageModal.HandleCommand(new JObject()));
            Assert.IsTrue(response.Value<bool>("success"));
            Assert.IsFalse(response["data"].Value<bool>("present"));
        }

        [Test]
        public void Answer_WithoutModal_Fails()
        {
            if (!ModalDispatcher.IsSupported) Assert.Ignore("Modal handling is macOS-only.");

            var response = JObject.FromObject(ManageModal.HandleCommand(
                new JObject { ["args"] = new JArray("answer"), ["button"] = "OK" }));
            Assert.IsFalse(response.Value<bool>("success"));
        }

        /// Opens a real dialog on the main thread and answers it over the
        /// connector's HTTP server from another thread. Explicit because it pops up
        /// a dialog, and a regression leaves that dialog open until a human clicks it.
        [Test, Explicit("Opens a real modal dialog")]
        public void DisplayDialog_IsAnsweredOverHttp()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) Assert.Ignore("Modal handling is macOS-only.");
            Assert.IsTrue(HttpServer.IsRunning, "Connector HTTP server is not running.");

            var port = HttpServer.Port;
            string listed = null, answered = null;
            var responder = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    var list = Post(port, new JObject { ["command"] = "modal" });
                    if (list["data"]?.Value<bool>("present") == true)
                    {
                        listed = list.ToString();
                        answered = Post(port, new JObject
                        {
                            ["command"] = "modal",
                            ["params"] = new JObject { ["args"] = new JArray("answer"), ["button"] = "Cancel" },
                        }).ToString();
                        return;
                    }
                    Thread.Sleep(100);
                }
            }) { IsBackground = true };
            responder.Start();

            var okClicked = EditorUtility.DisplayDialog("unity-cli modal test",
                "Automated test. If this stays open, click Cancel.", "OK", "Cancel");
            responder.Join(TimeSpan.FromSeconds(5));

            Assert.IsFalse(okClicked, "Expected the Cancel button to be clicked.");
            StringAssert.Contains("Automated test.", listed);
            StringAssert.Contains("\"dismissed\": true", answered);
        }

        static JObject Post(int port, JObject body)
        {
            using var client = new WebClient();
            client.Headers[HttpRequestHeader.ContentType] = "application/json";
            client.Encoding = Encoding.UTF8;
            return JObject.Parse(client.UploadString($"http://127.0.0.1:{port}/command", body.ToString()));
        }
    }
}
