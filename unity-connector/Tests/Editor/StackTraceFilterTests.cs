using NUnit.Framework;
using UnityCliConnector;

namespace UnityCliConnector.Tests
{
    /// StackTraceFilter cases pinned to trace shapes copied out of a real Unity 6 console
    /// (Mono colon form) and out of the Entities/Burst plumbing that used to flood it.
    [TestFixture]
    public class StackTraceFilterTests
    {
        private const string BurstTrace =
            "IndexOutOfRangeException : Index was out of bounds.\n" +
            "UnityEngine.Debug:LogError (object)\n" +
            "Unity.Entities.EntityQuery:CalculateBaseEntityCount () (at ./Library/PackageCache/com.unity.entities@0e17c544895b/Unity.Entities/EntityQuery.cs:266)\n" +
            "Unity.Entities.EntityQuery:GetArchetypeChunkComponentType (System.Type,bool)\n" +
            "BatchUpdateFOV:Execute (int,Unity.Entities.Entity&,System.IntPtr) (at ./Library/PackageCache/com.unity.entities@0e17c544895b/Unity.Entities/IJobChunk.cs:40)\n" +
            "Unity.Entities.IJobChunkExtensions/JobChunkStruct`1[BatchUpdateFOV]:Execute (Unity.Entities.IJobChunk&,System.IntPtr,int,int)\n" +
            "Unity.Jobs.IJobExtensions/JobStruct`1[Unity.Entities.IJobChunkExtensions/JobChunkStruct`1[BatchUpdateFOV]]:Execute (Unity.Entities.IJobChunkExtensions/JobChunkStruct`1[Unity.Entities.IJobChunkExtensions/JobChunkStruct`1[BatchUpdateFOV]]&,System.IntPtr,System.IntPtr,Unity.Jobs.LowLevel.Unsafe/JobRanges&)\n" +
            "Unity.Jobs.LowLevel.Unsafe.JobRanges/<>c__DisplayClass6_0:<ExecuteJob>b__0 (System.IntPtr,System.IntPtr,int,int,int,int)\n" +
            "Unity.Jobs.LowLevel.Unsafe.JobRanges:ExecuteJob (System.IntPtr,System.IntPtr,int,int,int,int)\n" +
            "0x000000010b2c4d60 in <unknown>\n" +
            "Unity.Jobs.LowLevel.Unsafe.JobHandleInner:Complete (Unity.Jobs.JobHandle/JobCompleteSafetyContext)\n" +
            "Unity.Entities.SystemState:Update () (at ./Library/PackageCache/com.unity.entities@0e17c544895b/Unity.Entities/SystemState.cs:665)\n" +
            "Unity.Entities.World:Update () (at ./Library/PackageCache/com.unity.entities@0e17c544895b/Unity.Entities/World.cs:196)\n" +
            "FoW.FogOfWarUpdater:Update () (at Assets/FogOfWar/Runtime/Components/FogOfWarUpdater.cs:31)\n";

        [Test]
        public void BurstTrace_KeepsUserFrame_CollapsesEngineRun()
        {
            var f = StackTraceFilter.Format(BurstTrace, StackTraceFilter.StacktraceMode.User, 6, 1000);

            Assert.AreEqual(
                "IndexOutOfRangeException : Index was out of bounds.\n" +
                "... (+12 engine frames)\n" +
                "FoW.FogOfWarUpdater:Update () (at Assets/FogOfWar/Runtime/Components/FogOfWarUpdater.cs:31)",
                f.Text);
            Assert.AreEqual(1, f.FramesKept);
            Assert.AreEqual(12, f.FramesOmitted);
            Assert.AreEqual(0, f.FramesOverCap);
            Assert.Less(f.Text.Length, BurstTrace.Length / 3);
        }

        // The old filter was a substring list; Unity.Entities.SystemState was in it by name and
        // everything else Burst emits was not. Namespace roots decide instead.
        [TestCase("Unity.Entities.SystemState:Update ()")]
        [TestCase("Unity.Collections.NativeArray`1[System.Int32]:get_Item (int)")]
        [TestCase("Unity.Burst.BurstCompiler:Compile (object)")]
        [TestCase("Unity.Jobs.JobHandle:Complete ()")]
        [TestCase("System.Reflection.MethodBase:Invoke (object,object[])")]
        [TestCase("Mono.Security.Protocol.Tls.Context:Start ()")]
        [TestCase("Burst.Compiler.IL.Jit.JitCompiler:Compile (string)")]
        [TestCase("NUnit.Framework.Assert:AreEqual (int,int)")]
        [TestCase("<Module>.invoke_void__this___int (int)")]
        [TestCase("<>c__DisplayClass3_0:<Execute>b__0 ()")]
        [TestCase("(wrapper delegate-invoke) <Module>.invoke_void (System.IntPtr)")]
        [TestCase("0x000000010b2c4d60")]
        [TestCase("0x000000010b2c4d60 in <unknown>")]
        [TestCase("--- End of stack trace from previous location where exception was thrown ---")]
        [TestCase("Unity.Serialization.SomeType:Read (System.IO.BinaryReader) (at ./Library/PackageCache/com.unity.entities@1.0/x.cs:3)")]
        public void EngineFrames_AreFiltered(string frame)
        {
            var f = StackTraceFilter.Format("message\n" + frame + "\nFoW.Game:Tick () (at Assets/Game.cs:9)",
                StackTraceFilter.StacktraceMode.User, 6, 1000);

            Assert.That(SplitLines(f.Text), Does.Not.Contain(frame));
            Assert.AreEqual(1, f.FramesKept);
        }

        // Only the root segment decides, so a project whose namespace starts with "Unity" keeps
        // its frames — that is this package's own namespace, not engine code.
        [TestCase("UnityCliConnector.Tools.ExecuteCsharp:CompileAndExecute (string,string,string,int)")]
        [TestCase("UnityWidgets.Panel:Repaint ()")]
        [TestCase("__CliDynamic:<Execute>g__Inner|0_1 ()")]
        public void UserFrames_AreKept(string frame)
        {
            var f = StackTraceFilter.Format("message\nUnityEngine.Debug:LogError (object)\n" + frame,
                StackTraceFilter.StacktraceMode.User, 6, 1000);

            Assert.That(SplitLines(f.Text), Does.Contain(frame));
        }

        // A real Unity 6 entry: Debug.LogException's ToString lines plus the caller-site trace.
        [Test]
        public void ExceptionEntry_KeepsMessageLineAndUserAtFrames()
        {
            var message =
                "System.InvalidOperationException: boom from inner\n" +
                "  at FoW.Thinker.Sub () [0x0001a] in <c1f4b33e5b7c4f0e9c5e1b0e5e5b2f6a>:0\n" +
                "  at FoW.Thinker.Main () [0x0000a] in <c1f4b33e5b7c4f0e9c5e1b0e5e5b2f6a>:0\n" +
                "  at System.Reflection.MethodBase.Invoke (object obj) [0x00016] in <8b4a1>:0\n" +
                "UnityEngine.Debug:LogException (object)\n";

            var f = StackTraceFilter.Format(message, StackTraceFilter.StacktraceMode.User, 6, 1000);

            Assert.AreEqual(
                "System.InvalidOperationException: boom from inner\n" +
                "at FoW.Thinker.Sub () [0x0001a] in <c1f4b33e5b7c4f0e9c5e1b0e5e5b2f6a>:0\n" +
                "at FoW.Thinker.Main () [0x0000a] in <c1f4b33e5b7c4f0e9c5e1b0e5e5b2f6a>:0\n" +
                "... (+2 engine frames)",
                f.Text);
        }

        // Traces taken verbatim from this project's Editor.log: a test failure through the
        // test-framework runner keeps the test method and drops the runner plumbing.
        [Test]
        public void TestRunnerTrace_KeepsTestMethod()
        {
            var message =
                "  Expected: 5\n  But was:  4\n" +
                "UnityEngine.Debug:LogError (object)\n" +
                "NUnit.Framework.Internal.Commands.TestMethodCommand:RunNonAsyncTestMethod (NUnit.Framework.Internal.ITestExecutionContext)\n" +
                "UnityEngine.TestTools.UnityTestMethodCommand:Execute (NUnit.Framework.Internal.ITestExecutionContext) (at ./Library/PackageCache/com.unity.test-framework@80f691d4262b/UnityEngine.TestRunner/NUnitExtensions/Commands/UnityTestMethodCommand.cs:19)\n" +
                "FoW.Tests.FogOfWarManagerTests:Teams_ExploredFlagPersists () (at Assets/Tests/PlayMode/FogOfWarManagerTests.cs:88)\n";

            var f = StackTraceFilter.Format(message, StackTraceFilter.StacktraceMode.User, 6, 1000);

            StringAssert.Contains("FoW.Tests.FogOfWarManagerTests:Teams_ExploredFlagPersists ()", f.Text);
            StringAssert.DoesNotContain("NUnit.Framework.Internal.Commands", f.Text);
            StringAssert.Contains("Expected: 5", f.Text);
        }

        [Test]
        public void AllEngineFrames_KeepsFirstFrameThatIsNotTheDebugCall()
        {
            var message =
                "Shader error in 'FoW/Overlay': unexpected token\n" +
                "UnityEngine.Debug:LogError (object)\n" +
                "UnityEditor.MaterialEditor:OnInspectorGUI () (at /Users/bokken/build/output/unity/unity/Editor/Mono/Inspectors/MaterialEditor.cs:12)\n" +
                "UnityEditor.EditorApplication:Internal_CallUpdateFunctions () (at /Users/bokken/build/output/unity/unity/Editor/Mono/EditorApplication.cs:412)\n";

            var f = StackTraceFilter.Format(message, StackTraceFilter.StacktraceMode.User, 6, 1000);

            StringAssert.Contains("UnityEditor.MaterialEditor:OnInspectorGUI ()", f.Text);
            Assert.AreEqual(1, f.FramesKept);
        }

        [Test]
        public void CompileErrorLine_IsLeftAlone()
        {
            const string message = "Assets/Scripts/Foo.cs(12,5): error CS0103: The name 'x' does not exist";

            var f = StackTraceFilter.Format(message, StackTraceFilter.StacktraceMode.User, 6, 1000);

            Assert.AreEqual(message, f.Text);
            Assert.AreEqual(0, f.FramesKept);
            Assert.AreEqual(0, f.FramesOmitted);
        }

        [Test]
        public void MessageBodyLines_AreKept()
        {
            var message =
                "[FoW] team 2 out of range\n" +
                "range=12 tiles, entities=3\n" +
                "UnityEngine.Debug:LogError (object)\n" +
                "FoW.FogOfWarManager:PrepareAllTeams () (at Assets/FogOfWar/Runtime/Core/FogOfWarManager.cs:140)\n";

            var f = StackTraceFilter.Format(message, StackTraceFilter.StacktraceMode.User, 6, 1000);

            StringAssert.Contains("range=12 tiles, entities=3", f.Text);
            StringAssert.Contains("FoW.FogOfWarManager:PrepareAllTeams ()", f.Text);
            Assert.AreEqual(1, f.FramesOmitted);
        }

        [Test]
        public void FrameCap_CutsExtraUserFramesAndCountsThem()
        {
            var sb = new System.Text.StringBuilder("boom");
            for (int i = 0; i < 12; i++)
                sb.Append($"\nFoW.Chain:Step{i} () (at Assets/Chain.cs:{i})");

            var f = StackTraceFilter.Format(sb.ToString(), StackTraceFilter.StacktraceMode.User, 6, 0);

            StringAssert.Contains("... (+6 frames omitted)", f.Text);
            Assert.AreEqual(6, f.FramesKept);
            Assert.AreEqual(6, f.FramesOverCap);
            Assert.AreEqual(6, f.FramesOmitted);
        }

        [Test]
        public void CharCap_KeepsTheEntryInsideTheCapIncludingItsMarker()
        {
            var tight = StackTraceFilter.Format(BurstTrace, StackTraceFilter.StacktraceMode.User, 100, 60);
            Assert.LessOrEqual(tight.Text.Length, 60);
            Assert.Greater(tight.CharsOmitted, 0);
            StringAssert.Contains("characters omitted", tight.Text);

            // Enough budget to end on a line: the engine marker survives, the frame after it does not.
            var generous = StackTraceFilter.Format(BurstTrace, StackTraceFilter.StacktraceMode.User, 100, 120);
            Assert.LessOrEqual(generous.Text.Length, 120);
            StringAssert.Contains("... (+12 engine frames)", generous.Text);
            StringAssert.DoesNotContain("FogOfWarUpdater", generous.Text);
        }

        [Test]
        public void NoFrames_KeepsTheMessageBodyAndSaysWhatWasCut()
        {
            var f = StackTraceFilter.Format(BurstTrace, StackTraceFilter.StacktraceMode.User, 0, 0);

            Assert.AreEqual(
                "IndexOutOfRangeException : Index was out of bounds.\n... (+13 frames omitted)",
                f.Text);
            Assert.AreEqual(0, f.FramesKept);
            Assert.AreEqual(13, f.FramesOmitted);
        }

        [Test]
        public void NoneMode_ReturnsFirstLineOnly()
        {
            var f = StackTraceFilter.Format(BurstTrace, StackTraceFilter.StacktraceMode.None, 6, 1000);

            Assert.AreEqual("IndexOutOfRangeException : Index was out of bounds.", f.Text);
            Assert.AreEqual(0, f.FramesOmitted);
        }

        [Test]
        public void NoneMode_HonoursTheCharCapOnASingleLongLine()
        {
            var f = StackTraceFilter.Format(new string('x', 4000), StackTraceFilter.StacktraceMode.None, 6, 120);

            Assert.LessOrEqual(f.Text.Length, 120);
            Assert.Greater(f.CharsOmitted, 0);
            StringAssert.Contains("characters omitted", f.Text);
        }

        [Test]
        public void FullMode_KeepsEveryFrameButStillHonoursTheCap()
        {
            var f = StackTraceFilter.Format(BurstTrace, StackTraceFilter.StacktraceMode.Full, 3, 0);

            StringAssert.Contains("UnityEngine.Debug:LogError (object)", f.Text);
            StringAssert.Contains("Unity.Entities.EntityQuery:CalculateBaseEntityCount ()", f.Text);
            StringAssert.DoesNotContain("Unity.Entities.SystemState:Update ()", f.Text);
            Assert.AreEqual(3, f.FramesKept);
            Assert.AreEqual(10, f.FramesOverCap);
            Assert.AreEqual(f.FramesOverCap, f.FramesOmitted);
        }

        [Test]
        public void UnlimitedFrames_KeepsEveryUserFrame()
        {
            var sb = new System.Text.StringBuilder("boom");
            for (int i = 0; i < 12; i++)
                sb.Append($"\nFoW.Chain:Step{i} () (at Assets/Chain.cs:{i})");

            var f = StackTraceFilter.Format(sb.ToString(), StackTraceFilter.StacktraceMode.User,
                StackTraceFilter.UnlimitedFrames, 0);

            StringAssert.Contains("FoW.Chain:Step11 ()", f.Text);
            Assert.AreEqual(12, f.FramesKept);
            Assert.AreEqual(0, f.FramesOmitted);
        }

        [TestCase("none", StackTraceFilter.StacktraceMode.None)]
        [TestCase("USER", StackTraceFilter.StacktraceMode.User)]
        [TestCase(" full ", StackTraceFilter.StacktraceMode.Full)]
        public void TryParseMode_AcceptsTheThreeModes(string raw, StackTraceFilter.StacktraceMode expected)
        {
            Assert.IsTrue(StackTraceFilter.TryParseMode(raw, out var mode));
            Assert.AreEqual(expected, mode);
        }

        [TestCase("short")]
        [TestCase("")]
        [TestCase(null)]
        public void TryParseMode_RejectsUnknownModes(string raw)
        {
            Assert.IsFalse(StackTraceFilter.TryParseMode(raw, out _));
        }

        private static System.Collections.Generic.IList<string> SplitLines(string text) =>
            text.Split('\n');
    }
}
