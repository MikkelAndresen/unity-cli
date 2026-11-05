using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityCliConnector.TestRunner
{
    [UnityCliTool(Description = "Run Unity EditMode or PlayMode tests and return results.")]
    public static class RunTests
    {
        internal static readonly string StatusDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-cli", "status");

        public class Parameters
        {
            [ToolParameter("Test mode: EditMode or PlayMode", Required = true)]
            public string Mode { get; set; }

            [ToolParameter("Filter by namespace, class, or full test name")]
            public string Filter { get; set; }

            [ToolParameter("Only tests in these assemblies (comma-separated assembly names)")]
            public string Assembly { get; set; }

            [ToolParameter("Run tests even when open scenes have unsaved changes")]
            public bool AllowDirtyScenes { get; set; }

            [ToolParameter("Save dirty open scenes before running tests")]
            public bool AutoSaveScenes { get; set; }

            [ToolParameter("Run tests even when an assembly under test is older than its sources")]
            public bool AllowStale { get; set; }
        }

        public static Task<object> HandleCommand(JObject @params)
        {
            if (@params == null)
                return Task.FromResult<object>(new ErrorResponse("Parameters cannot be null."));

            var p = new ToolParams(@params);

            var modeResult = p.GetRequired("mode");
            if (!modeResult.IsSuccess)
                return Task.FromResult<object>(new ErrorResponse(modeResult.ErrorMessage));

            var modeStr = modeResult.Value.Trim();
            TestMode testMode;
            if (modeStr.Equals("EditMode", StringComparison.OrdinalIgnoreCase))
                testMode = TestMode.EditMode;
            else if (modeStr.Equals("PlayMode", StringComparison.OrdinalIgnoreCase))
                testMode = TestMode.PlayMode;
            else
                return Task.FromResult<object>(new ErrorResponse($"Unknown mode '{modeStr}'. Use EditMode or PlayMode."));

            var filter = p.Get("filter", null);
            var assemblies = ParseAssemblies(p.Get("assembly", null));
            var allowStale = p.GetBool("allowStale") || p.GetBool("allow_stale");
            if (!allowStale)
            {
                var only = assemblies == null ? null : new HashSet<string>(assemblies);
                var stale = AssemblyStaleness.ScanProject(AssemblyStaleness.DefaultBudgetMs, only);
                var block = AssemblyStaleness.BlockIfStaleOrIncomplete(stale);
                if (block != null)
                    return Task.FromResult(block);
            }
            var dirtySceneResult = PrepareDirtyScenes(
                p.GetBool("allowDirtyScenes") || p.GetBool("allow_dirty_scenes"),
                p.GetBool("autoSaveScenes") || p.GetBool("auto_save_scenes"));
            if (dirtySceneResult != null)
                return Task.FromResult<object>(dirtySceneResult);

            if (testMode == TestMode.EditMode)
                return ExecuteInProcess(testMode, filter, assemblies);

            StartPlayModeRun(filter, assemblies);
            return Task.FromResult<object>(new SuccessResponse("running", new { port = HttpServer.Port }));
        }

        private static Task<object> ExecuteInProcess(TestMode mode, string filter, string[] assemblies)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var passed  = new List<string>();
            var failed  = new List<string>();
            var skipped = new List<string>();
            var logged  = new List<RunTests.LoggedException>();

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            TestCallbacks callbacks = null;
            callbacks = new TestCallbacks(
                onResult: r => CollectResult(r, passed, failed, skipped, logged),
                onFinished: _ =>
                {
                    if (tcs.Task.IsCompleted) return;
                    // Registered callbacks outlive the TestRunnerApi instance: without this, every
                    // earlier run's callbacks fire again on each later run in the same domain.
                    api.UnregisterCallbacks(callbacks);
                    Object.DestroyImmediate(api);
                    tcs.TrySetResult(BuildResponse(passed, failed, skipped, logged));
                }
            );

            api.RegisterCallbacks(callbacks);
            // The Test Framework advances a stepped run by one task per editor update, and an editor
            // that is not the focused app updates ~6 times a second whatever its interaction mode:
            // 16 tests took 12.6 s stepped, 0.5 s synchronously (bench project, 2026-11-05).
            // A synchronous run silently leaves out tests that span frames, so those keep the
            // stepped run.
            var settings = new ExecutionSettings(BuildFilter(mode, filter, assemblies))
            {
                runSynchronously = mode == TestMode.EditMode && !MultiFrameTests.AnyIn(assemblies),
            };
            api.Execute(settings);
            return tcs.Task;
        }

        private static void StartPlayModeRun(string filter, string[] assemblies)
        {
            var port = HttpServer.Port;

            try { var f = ResultsFilePath(port); if (File.Exists(f)) File.Delete(f); } catch { }
            TestRunnerState.MarkPending(port, filter);

            var passed  = new List<string>();
            var failed  = new List<string>();
            var skipped = new List<string>();
            var logged  = new List<RunTests.LoggedException>();

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            TestCallbacks callbacks = null;
            callbacks = new TestCallbacks(
                onResult: r => CollectResult(r, passed, failed, skipped, logged),
                onFinished: _ =>
                {
                    api.UnregisterCallbacks(callbacks);
                    Object.DestroyImmediate(api);
                    TestRunnerState.ClearPending(port);
                    WriteResultsFile(port, passed, failed, skipped, logged);
                }
            );

            api.RegisterCallbacks(callbacks);
            api.Execute(new ExecutionSettings(BuildFilter(TestMode.PlayMode, filter, assemblies)));
        }

        private static object PrepareDirtyScenes(bool allowDirtyScenes, bool autoSaveScenes)
        {
            var dirtyScenes = GetDirtyOpenScenes();
            if (dirtyScenes.Count == 0)
                return null;

            if (autoSaveScenes)
            {
                foreach (var scene in dirtyScenes)
                {
                    if (string.IsNullOrEmpty(scene.path))
                        return DirtyScenesBlockedResponse(new List<string> { scene.name });
                    if (!EditorSceneManager.SaveScene(scene))
                        return DirtyScenesBlockedResponse(new List<string> { scene.path });
                }
                return null;
            }

            if (allowDirtyScenes)
                return null;

            var paths = new List<string>();
            foreach (var scene in dirtyScenes)
                paths.Add(string.IsNullOrEmpty(scene.path) ? scene.name : scene.path);

            return DirtyScenesBlockedResponse(paths);
        }

        private static List<Scene> GetDirtyOpenScenes()
        {
            var scenes = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                    scenes.Add(scene);
            }
            return scenes;
        }

        private static ErrorResponse DirtyScenesBlockedResponse(List<string> scenes)
        {
            const string message = "Open scenes have unsaved changes. Save or discard them before running tests.";
            return new ErrorResponse(message, new
            {
                blocked = "dirty-scenes",
                message,
                scenes,
            });
        }

        // --- Shared helpers (used by TestRunnerState after domain reload) ---

        internal static void CollectResult(ITestResultAdaptor result,
            List<string> passed, List<string> failed, List<string> skipped, List<LoggedException> logged)
        {
            if (result.Test.IsSuite) return;
            var name = result.Test.FullName;
            switch (result.TestStatus)
            {
                case TestStatus.Passed:  passed.Add(name); break;
                case TestStatus.Failed:
                    failed.Add($"{name}: {result.Message}");
                    logged.AddRange(ExceptionsLogged(name, result.Output));
                    break;
                default:                 skipped.Add(name); break;
            }
        }

        /// An exception a failing test logged rather than threw: Entities catches what a system
        /// throws in OnUpdate and logs it, so the test fails later on an assertion whose message
        /// never mentions the exception.
        internal sealed class LoggedException
        {
            public string test;
            public string exception;
            public string at;
        }

        private const int MaxLoggedPerTest = 3;
        private const int MaxExceptionLength = 300;
        private static readonly Regex ExceptionHeader = new Regex(@"^[A-Za-z_][\w.`+]*Exception(: .*)?$");
        private static readonly Regex FrameLocation = new Regex(@"\(at (.+?:\d+)\)\s*$");

        /// Reads Unity's log capture in ITestResultAdaptor.Output: an exception is logged as
        /// "TypeException: message" followed by its stack, whose first "(at file:line)" frame is
        /// the throw site.
        internal static List<LoggedException> ExceptionsLogged(string test, string output)
        {
            var found = new List<LoggedException>();
            if (string.IsNullOrEmpty(output)) return found;
            var lines = output.Split('\n');
            for (var i = 0; i < lines.Length && found.Count < MaxLoggedPerTest; i++)
            {
                var line = lines[i].Trim();
                if (!ExceptionHeader.IsMatch(line)) continue;
                string at = null;
                for (var j = i + 1; j < lines.Length && at == null; j++)
                {
                    var frame = lines[j].Trim();
                    if (frame.Length == 0 || ExceptionHeader.IsMatch(frame)) break;
                    var m = FrameLocation.Match(frame);
                    if (m.Success) at = ProjectRelative(m.Groups[1].Value);
                }
                var exception = line.Length > MaxExceptionLength ? line.Substring(0, MaxExceptionLength) + "…" : line;
                if (found.Any(e => e.exception == exception && e.at == at)) continue;
                found.Add(new LoggedException { test = test, exception = exception, at = at });
            }
            return found;
        }

        private static string ProjectRelative(string path)
        {
            var root = Path.GetDirectoryName(Application.dataPath)?.Replace('\\', '/') + "/";
            path = path.Replace('\\', '/');
            if (path.StartsWith(root, StringComparison.Ordinal)) return path.Substring(root.Length);
            return path.StartsWith("./", StringComparison.Ordinal) ? path.Substring(2) : path;
        }

        internal static void WriteResultsFile(int port, List<string> passed, List<string> failed, List<string> skipped,
            List<LoggedException> logged)
        {
            var data = new
            {
                success = failed.Count == 0,
                message = failed.Count > 0
                    ? $"{failed.Count} test(s) failed."
                    : $"All {passed.Count} test(s) passed.",
                data = new
                {
                    total   = passed.Count + failed.Count + skipped.Count,
                    passed  = passed.Count,
                    failed  = failed.Count,
                    skipped = skipped.Count,
                    failures = failed,
                    logged_exceptions = logged,
                    passes   = passed,
                }
            };

            try
            {
                Directory.CreateDirectory(StatusDir);
                File.WriteAllText(ResultsFilePath(port), JsonConvert.SerializeObject(data));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityCliConnector] Failed to write test results: {ex.Message}");
            }
        }

        internal static string ResultsFilePath(int port) =>
            Path.Combine(StatusDir, $"test-results-{port}.json");

        internal static object BuildResponse(List<string> passed, List<string> failed, List<string> skipped,
            List<LoggedException> logged)
        {
            var summary = new
            {
                total   = passed.Count + failed.Count + skipped.Count,
                passed  = passed.Count,
                failed  = failed.Count,
                skipped = skipped.Count,
                failures = failed,
                logged_exceptions = logged,
                passes   = passed,
            };
            return failed.Count > 0
                ? (object)new ErrorResponse($"{failed.Count} test(s) failed.", summary)
                : new SuccessResponse($"All {passed.Count} test(s) passed.", summary);
        }

        internal static Filter BuildFilter(TestMode mode, string filterStr, string[] assemblies = null)
        {
            var f = new Filter { testMode = mode };
            // groupNames only: the Test Framework ANDs testNames with groupNames, and testNames
            // matches whole full names, so a class name without its namespace selected nothing.
            if (!string.IsNullOrEmpty(filterStr))
                f.groupNames = new[] { FilterPattern(filterStr) };
            if (assemblies != null && assemblies.Length > 0)
                f.assemblyNames = assemblies;
            return f;
        }

        private static readonly char[] RegexOnlyChars = { '^', '$', '*', '+', '?', '[', ']', '{', '}', '|', '\\' };

        /// A plain name matches whole dot-separated segments of a test's full name, so `BowTests`,
        /// `Ns.BowTests`, `BowTests.Fires` and a namespace prefix all select what they name, and
        /// `Bow` does not select `BowTests`. A filter using regex syntax is passed through as a regex.
        internal static string FilterPattern(string filter)
        {
            if (filter.IndexOfAny(RegexOnlyChars) >= 0) return filter;
            return @"(^|\.)" + Regex.Escape(filter) + @"($|\.|\()";
        }

        internal static string[] ParseAssemblies(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var names = value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            return names.Length > 0 ? names : null;
        }

        internal class TestCallbacks : ICallbacks
        {
            private readonly Action<ITestResultAdaptor> _onResult;
            private readonly Action<ITestResultAdaptor> _onFinished;

            public TestCallbacks(Action<ITestResultAdaptor> onResult, Action<ITestResultAdaptor> onFinished)
            {
                _onResult   = onResult;
                _onFinished = onFinished;
            }

            public void RunStarted(ITestAdaptor testsToRun) { }
            public void RunFinished(ITestResultAdaptor result) => _onFinished(result);
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) => _onResult(result);
        }
    }
}
