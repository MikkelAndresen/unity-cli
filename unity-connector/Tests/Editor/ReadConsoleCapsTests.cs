using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector.Tools;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityCliConnector.Tests
{
    /// The console tool's caps, driven end to end: entries are logged into the live editor
    /// console and read back through the LogEntries reflection, so this covers the reflection
    /// binding, the newest-first selection and the shape of the reply together.
    [TestFixture]
    public class ReadConsoleCapsTests
    {
        private const string Marker = "unitycli_caps_marker";
        private const string NoiseMarker = Marker + "_noise";
        private const string NoneMarker = Marker + "_none";
        private const string ChainMarker = Marker + "_chain";

        // Shaped like the Entities/Burst noise that used to flood a console call: only the last
        // frame belongs to project code.
        private const string NoiseTrace =
            "UnityEngine.Debug:LogError (object)\n" +
            "Unity.Entities.EntityQuery:CalculateBaseEntityCount () (at ./Library/PackageCache/com.unity.entities@0e17c544895b/Unity.Entities/EntityQuery.cs:266)\n" +
            "Unity.Entities.IJobChunkExtensions/JobChunkStruct`1[FoW.BatchUpdateFOV]:Execute (Unity.Entities.IJobChunk&,System.IntPtr,int,int)\n" +
            "Unity.Jobs.LowLevel.Unsafe.JobRanges:ExecuteJob (System.IntPtr,System.IntPtr,int,int,int,int)\n" +
            "0x000000010b2c4d60 in <unknown>\n" +
            "Unity.Entities.SystemState:Update () (at ./Library/PackageCache/com.unity.entities@0e17c544895b/Unity.Entities/SystemState.cs:665)\n" +
            "FoW.FogOfWarUpdater:Update () (at Assets/FogOfWar/Runtime/Components/FogOfWarUpdater.cs:31)";

        // Every frame is project code here, so only --frames cuts anything.
        private const string ChainTrace =
            "FoW.Chain:Step0 () (at Assets/Chain.cs:1)\n" +
            "FoW.Chain:Step1 () (at Assets/Chain.cs:2)\n" +
            "FoW.Chain:Step2 () (at Assets/Chain.cs:3)\n" +
            "FoW.Chain:Step3 () (at Assets/Chain.cs:4)\n" +
            "FoW.Chain:Step4 () (at Assets/Chain.cs:5)\n" +
            "FoW.Chain:Step5 () (at Assets/Chain.cs:6)";

        // UnityEditor.ConsoleWindow+ConsoleFlags, read/written through LogEntries.consoleFlags.
        private const int CollapseFlag = 1 << 0;
        private const int LogLevelLogFlag = 1 << 7;
        private const int LogLevelWarningFlag = 1 << 8;
        private const int LogLevelErrorFlag = 1 << 9;
        private const int LogLevelMask = LogLevelLogFlag | LogLevelWarningFlag | LogLevelErrorFlag;

        private static readonly Type LogEntriesType =
            typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
        private static readonly BindingFlags StaticAny =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly PropertyInfo ConsoleFlagsProperty =
            LogEntriesType?.GetProperty("consoleFlags", StaticAny);
        private static readonly MethodInfo GetFilteringTextMethod =
            LogEntriesType?.GetMethod("GetFilteringText", StaticAny);
        private static readonly MethodInfo SetFilteringTextMethod =
            LogEntriesType?.GetMethod("SetFilteringText", StaticAny);

        private int _savedConsoleFlags;
        private string _savedFilteringText;
        private bool _savedConsoleFilter;

        [SetUp]
        public void SaveConsoleWindowFilter()
        {
            if (ConsoleFlagsProperty == null || GetFilteringTextMethod == null || SetFilteringTextMethod == null)
                return;
            _savedConsoleFlags = (int)ConsoleFlagsProperty.GetValue(null);
            _savedFilteringText = (string)GetFilteringTextMethod.Invoke(null, null) ?? "";
            _savedConsoleFilter = true;
        }

        [TearDown]
        public void RestoreConsoleWindowFilter()
        {
            if (!_savedConsoleFilter) return;
            ConsoleFlagsProperty.SetValue(null, _savedConsoleFlags);
            SetFilteringTextMethod.Invoke(null, new object[] { _savedFilteringText });
        }

        // The test runner gives SetUp/TearDown their own LogScope, so a flag set in SetUp does not
        // reach the scope that records the test body's logs; each test sets it itself.
        private static void IgnoreFailingLogs() => LogAssert.ignoreFailingMessages = true;

        [Test]
        public void Lines_LimitsToTheNewestEntriesAndCountsTheRest()
        {
            IgnoreFailingLogs();
            LogMarker("lines", 3);

            var data = Run(new JObject { ["type"] = "error", ["lines"] = 1, ["stacktrace"] = "none" });

            // The one entry must be the last one logged, not the first: LogEntries is oldest-first.
            Assert.AreEqual($"{Marker}_lines 2", data["entries"][0].Value<string>());
            Assert.AreEqual(1, data["entries_returned"].Value<int>());
            Assert.GreaterOrEqual(data["entries_total"].Value<int>(), 3);
            Assert.GreaterOrEqual(data["entries_omitted"].Value<int>(), 2);
            Assert.IsTrue(data["truncated"].Value<bool>());
            Assert.That(data["truncated_by"].Values<string>(), Does.Contain("lines"));
            Assert.AreEqual(1, data["limits"]["lines"].Value<int>());
        }

        [Test]
        public void MaxBytes_StopsAddingEntriesAndSaysSo()
        {
            IgnoreFailingLogs();
            LogMarker("bytes", 5);

            var data = Run(new JObject
            {
                ["type"] = "error",
                ["stacktrace"] = "full",
                ["entry_chars"] = 200,
                ["max_bytes"] = 400,
            });

            int bytes = 0;
            foreach (var entry in data["entries"])
                bytes += JsonConvert.ToString(entry.Value<string>()).Length;

            var returned = (JArray)data["entries"];
            // The kept window ends at the newest entry, whichever end of it survives the byte cap.
            Assert.AreEqual($"{Marker}_bytes 4", returned[returned.Count - 1].Value<string>().Split('\n')[0]);
            Assert.Greater(data["entries_returned"].Value<int>(), 0);
            Assert.LessOrEqual(bytes, 400);
            Assert.GreaterOrEqual(data["entries_omitted"].Value<int>(), 1);
            Assert.IsTrue(data["truncated"].Value<bool>());
            Assert.That(data["truncated_by"].Values<string>(), Does.Contain("bytes"));
        }

        [Test]
        public void UserStacktrace_KeepsProjectFrameAndCollapsesEngineFrames()
        {
            IgnoreFailingLogs();
            Debug.LogError($"{NoiseMarker}\n{NoiseTrace}");

            var entry = FindEntry(NoiseMarker, new JObject
            {
                ["type"] = "error",
                ["stacktrace"] = "user",
                ["lines"] = 20,
                ["entry_chars"] = 0,
            });

            StringAssert.Contains("FoW.FogOfWarUpdater:Update () (at Assets/FogOfWar/Runtime/Components/FogOfWarUpdater.cs:31)", entry);
            StringAssert.Contains("engine frames)", entry);
            StringAssert.DoesNotContain("UnityEngine.Debug:LogError", entry);
            StringAssert.DoesNotContain("Unity.Entities.SystemState", entry);
        }

        [Test]
        public void Frames_CutsUserFramesAndCountsThem()
        {
            IgnoreFailingLogs();
            Debug.LogError($"{ChainMarker}\n{ChainTrace}");

            var capped = FindEntry(ChainMarker, new JObject
            {
                ["type"] = "error",
                ["stacktrace"] = "user",
                ["frames"] = 2,
                ["lines"] = 20,
                ["entry_chars"] = 0,
            });
            StringAssert.Contains("FoW.Chain:Step0 ()", capped);
            StringAssert.Contains("FoW.Chain:Step1 ()", capped);
            StringAssert.DoesNotContain("FoW.Chain:Step5 ()", capped);

            var uncapped = FindEntry(ChainMarker, new JObject
            {
                ["type"] = "error",
                ["stacktrace"] = "user",
                ["frames"] = -1,
                ["lines"] = 20,
                ["entry_chars"] = 0,
            });
            StringAssert.Contains("FoW.Chain:Step5 ()", uncapped);
            StringAssert.DoesNotContain("frames omitted", uncapped);
        }

        [Test]
        public void StacktraceNone_ReturnsTheFirstLineOnly()
        {
            IgnoreFailingLogs();
            Debug.LogError($"{NoneMarker}\n{NoiseTrace}");

            var entry = FindEntry(NoneMarker, new JObject
            {
                ["type"] = "error",
                ["stacktrace"] = "none",
                ["lines"] = 20,
            });

            Assert.AreEqual(NoneMarker, entry);
        }

        [Test]
        public void Full_TurnsOffEveryCap()
        {
            IgnoreFailingLogs();
            LogMarker("full", 2);

            var data = Run(new JObject { ["type"] = "error", ["full"] = true });

            Assert.IsFalse(data["truncated"].Value<bool>());
            Assert.AreEqual(data["entries_total"].Value<int>(), data["entries_returned"].Value<int>());
            Assert.AreEqual(0, data["limits"]["lines"].Value<int>());
            Assert.AreEqual(-1, data["limits"]["frames"].Value<int>());
            Assert.AreEqual(0, data["limits"]["max_bytes"].Value<int>());
        }

        [Test]
        public void UnknownStacktraceMode_IsRejected()
        {
            var response = ReadConsole.HandleCommand(new JObject { ["stacktrace"] = "short" }) as ErrorResponse;

            Assert.IsNotNull(response);
            StringAssert.Contains("stacktrace", response.message);
        }

        [Test]
        public void HiddenLogAndWarningTogglesAndSearch_AreIgnoredForTheRead_AndRestored()
        {
            IgnoreFailingLogs();
            RequireConsoleFilterBindings();
            const string marker = Marker + "_window_filter";
            const string search = "unitycli_filter_that_matches_nothing";
            Debug.Log(marker);
            Debug.LogWarning(marker + "_warn");
            Debug.LogError(marker + "_err");

            int hiddenFlags = (_savedConsoleFlags | LogLevelErrorFlag | CollapseFlag) &
                              ~(LogLevelLogFlag | LogLevelWarningFlag);
            SetConsoleWindowFilter(hiddenFlags, search);

            var data = Run(new JObject
            {
                ["type"] = "error,warning,log",
                ["lines"] = 200,
                ["stacktrace"] = "none",
            });

            var entries = data["entries"].Values<string>().ToList();
            Assert.That(entries, Has.Some.EqualTo(marker), "hidden log toggle must not hide the entry");
            Assert.That(entries, Has.Some.EqualTo(marker + "_warn"), "hidden warning toggle must not hide the entry");
            Assert.That(entries, Has.Some.EqualTo(marker + "_err"));

            Assert.AreEqual(hiddenFlags, (int)ConsoleFlagsProperty.GetValue(null), "flags must be restored");
            Assert.AreEqual(search, (string)GetFilteringTextMethod.Invoke(null, null), "search must be restored");

            var reported = data["console_window_filter"];
            Assert.IsNotNull(reported, "the reply should say which window filter was ignored");
            Assert.IsTrue(reported["log_hidden"].Value<bool>());
            Assert.IsTrue(reported["warning_hidden"].Value<bool>());
            Assert.IsFalse(reported["error_hidden"].Value<bool>());
            Assert.AreEqual(search, reported["search"].Value<string>());
            Assert.IsTrue(reported["collapsed"].Value<bool>());
            StringAssert.Contains("ignored Console window filter", reported["note"].Value<string>());
            StringAssert.Contains("search", reported["note"].Value<string>());
        }

        [Test]
        public void HiddenErrorToggle_DoesNotHideErrorsFromTheTool()
        {
            IgnoreFailingLogs();
            RequireConsoleFilterBindings();
            const string marker = Marker + "_window_hidden_error";
            Debug.LogError(marker);

            int hiddenFlags = (_savedConsoleFlags | LogLevelLogFlag | LogLevelWarningFlag) & ~LogLevelErrorFlag;
            SetConsoleWindowFilter(hiddenFlags, "");

            var data = Run(new JObject { ["type"] = "error", ["lines"] = 200, ["stacktrace"] = "none" });

            Assert.That(data["entries"].Values<string>(), Has.Some.EqualTo(marker));
            Assert.AreEqual(hiddenFlags, (int)ConsoleFlagsProperty.GetValue(null), "flags must be restored");
            Assert.IsTrue(data["console_window_filter"]["error_hidden"].Value<bool>());
        }

        [Test]
        public void VisibleConsoleFilter_AddsNoOverrideMetadata()
        {
            IgnoreFailingLogs();
            RequireConsoleFilterBindings();

            int visibleFlags = (_savedConsoleFlags | LogLevelMask) & ~CollapseFlag;
            SetConsoleWindowFilter(visibleFlags, "");

            var data = Run(new JObject { ["type"] = "error", ["lines"] = 1, ["stacktrace"] = "none" });

            Assert.IsNull(data["console_window_filter"]);
            Assert.AreEqual(visibleFlags, (int)ConsoleFlagsProperty.GetValue(null));
        }

        private static void RequireConsoleFilterBindings()
        {
            Assume.That(ConsoleFlagsProperty, Is.Not.Null, "LogEntries.consoleFlags is unavailable");
            Assume.That(GetFilteringTextMethod, Is.Not.Null, "LogEntries.GetFilteringText is unavailable");
            Assume.That(SetFilteringTextMethod, Is.Not.Null, "LogEntries.SetFilteringText is unavailable");
        }

        private static void SetConsoleWindowFilter(int flags, string filteringText)
        {
            ConsoleFlagsProperty.SetValue(null, flags);
            SetFilteringTextMethod.Invoke(null, new object[] { filteringText });
        }

        private static void LogMarker(string tag, int times)
        {
            for (int i = 0; i < times; i++)
                Debug.LogError($"{Marker}_{tag} {i}\n{NoiseTrace}");
        }

        private static string FindEntry(string prefix, JObject query)
        {
            var data = Run(query);
            foreach (var entry in data["entries"])
            {
                var text = entry.Value<string>();
                if (text.StartsWith(prefix)) return text;
            }

            Assert.Fail($"no console entry starting with {prefix} in {data["entries"]}");
            return null;
        }

        private static JObject Run(JObject query)
        {
            var response = ReadConsole.HandleCommand(query) as SuccessResponse;
            Assert.IsNotNull(response, "console tool returned no success payload");
            return JObject.FromObject(response.data);
        }
    }
}
