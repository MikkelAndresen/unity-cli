using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityCliConnector.TestRunner
{
    /// <summary>
    /// Survives domain reloads via [InitializeOnLoad].
    /// Re-registers TestRunnerApi callbacks after PlayMode domain reload
    /// so RunFinished still fires and results are written to file.
    /// </summary>
    [InitializeOnLoad]
    public static class TestRunnerState
    {
        static bool s_WorkerForTests;

        static TestRunnerState()
        {
            // An import worker has no listener and cannot own a test run; scanning the editor's
            // pending markers there would let a worker reattach (and clear) them.
            if (IsWorkerProcess()) return;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        static bool IsWorkerProcess()
        {
            if (s_WorkerForTests) return true;
            try { return AssetDatabase.IsAssetImportWorkerProcess(); }
            catch { return false; }
        }

        internal static void SetWorkerForTests(bool worker) => s_WorkerForTests = worker;

        internal static void RunAfterAssemblyReloadForTests() => OnAfterAssemblyReload();

        public static void MarkPending(int port, string filter)
        {
            var pending = new { port, filter = filter ?? "" };
            try
            {
                Directory.CreateDirectory(RunTests.StatusDir);
                File.WriteAllText(PendingFilePath(port), JsonConvert.SerializeObject(pending));
            }
            catch { }
        }

        public static void ClearPending(int port)
        {
            try
            {
                var path = PendingFilePath(port);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        static void OnAfterAssemblyReload()
        {
            if (IsWorkerProcess()) return;
            try
            {
                Directory.CreateDirectory(RunTests.StatusDir);
                foreach (var file in Directory.GetFiles(RunTests.StatusDir, "test-pending-*.json"))
                {
                    var json = File.ReadAllText(file);
                    var pending = JObject.Parse(json);
                    var port   = pending["port"]?.Value<int>() ?? 0;
                    var filter = pending["filter"]?.Value<string>();

                    if (port == 0) continue;

                    ReattachCallbacks(port, filter);
                }
            }
            catch { }
        }

        static void ReattachCallbacks(int port, string filter)
        {
            var passed  = new List<string>();
            var failed  = new List<string>();
            var skipped = new List<string>();
            var logged  = new List<RunTests.LoggedException>();

            var wake = EditorWake.Hold();
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var callbacks = new RunTests.TestCallbacks(
                onResult: r => RunTests.CollectResult(r, passed, failed, skipped, logged),
                onFinished: _ =>
                {
                    wake.Dispose();
                    Object.DestroyImmediate(api);
                    ClearPending(port);
                    RunTests.WriteResultsFile(port, passed, failed, skipped, logged);
                }
            );

            api.RegisterCallbacks(callbacks);
        }

        static string PendingFilePath(int port) =>
            Path.Combine(RunTests.StatusDir, $"test-pending-{port}.json");
    }
}
