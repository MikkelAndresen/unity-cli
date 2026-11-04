using System.Collections.Generic;
using NUnit.Framework;
using UnityCliConnector.Tools;

namespace UnityCliConnector.Tests
{
    /// Tests the Burst report helpers. They hold no Burst references on purpose, so this file
    /// compiles and runs in projects (and connectors) without com.unity.burst installed.
    [TestFixture]
    public class BurstReportTests
    {
        static BurstTargetInfo Target(string full, string declaring, string assembly)
        {
            return new BurstTargetInfo
            {
                FullName = full,
                DisplayName = full.Contains(" - (") ? full.Substring(0, full.IndexOf(" - (", System.StringComparison.Ordinal)) : full,
                DeclaringType = declaring,
                Scope = declaring.Contains(".") ? declaring.Substring(0, declaring.LastIndexOf('.')) : "",
                Assembly = assembly,
            };
        }

        [Test]
        public void TryParse_ReadsTheBurstHeaderTheCSharpPatternMisses()
        {
            // The console's own file/line for this entry are empty/61; the message carries the real ones.
            var message = "/Runtime/Jobs/Managed/IJob.cs(61,17): Burst error BC1051: Invalid managed type " +
                          "found for the field `Values` of the struct `MyGame.HeavyJob`.";

            Assert.IsTrue(BurstErrors.TryParse(message, "", 61, out var error));
            Assert.AreEqual("/Runtime/Jobs/Managed/IJob.cs", error.File);
            Assert.AreEqual(61, error.Line);
            Assert.AreEqual(17, error.Col);
            Assert.AreEqual("BC1051", error.Code);
            Assert.AreEqual("Invalid managed type found for the field `Values` of the struct `MyGame.HeavyJob`.", error.Message);
            Assert.AreEqual("/Runtime/Jobs/Managed/IJob.cs:61:17 BC1051 Invalid managed type found for the field `Values` of the struct `MyGame.HeavyJob`.",
                error.Format());
        }

        [Test]
        public void TryParse_KeepsConsoleFieldsWhenTheHeaderHasNoPosition()
        {
            var message = "Burst error BC1073: internal compiler error\n  at Burst.Stack.Frame()";

            Assert.IsTrue(BurstErrors.TryParse(message, "Assets/Job.cs", 12, out var error));
            Assert.AreEqual("Assets/Job.cs", error.File);
            Assert.AreEqual(12, error.Line);
            Assert.AreEqual("BC1073", error.Code);
            Assert.AreEqual("internal compiler error", error.Message);
            Assert.AreEqual("Assets/Job.cs:12 BC1073 internal compiler error", error.Format());
        }

        [Test]
        public void TryParse_ReadsThePositionOutOfAStackFrame_WhenTheHeaderHasNone()
        {
            // Burst writes "(0,0):" for this class of error and puts the position in the trace.
            var message = "(0,0): Burst error BC1091: External and internal calls are not allowed inside " +
                          "static constructors: System.Console.WindowsConsole.GetConsoleCP()\n" +
                          "at System.Console.WriteLine(System.String value)\n" +
                          "at MyGame.ManagedBodyJob.Execute(MyGame.ManagedBodyJob* this) (at /proj/Assets/Job.cs:39)\n" +
                          "at Unity.Jobs.IJobExtensions.JobStruct`1<MyGame.ManagedBodyJob>.Execute(ref MyGame.ManagedBodyJob data) " +
                          "(at /Users/bokken/build/output/unity/unity/Runtime/Jobs/Managed/IJob.cs:61)\n" +
                          "While compiling job:";

            Assert.IsTrue(BurstErrors.TryParse(message, "", 0, out var error));
            Assert.AreEqual("BC1091", error.Code);
            Assert.AreEqual("/proj/Assets/Job.cs", error.File);
            Assert.AreEqual(39, error.Line);
            Assert.AreEqual(0, error.Col);
            Assert.AreEqual("/proj/Assets/Job.cs:39 BC1091 External and internal calls are not allowed inside static constructors: System.Console.WindowsConsole.GetConsoleCP()",
                error.Format());
            // The engine frame is the only position for a job-producer error, and is still better than none.
            Assert.IsTrue(BurstErrors.TryParse("(0,0): Burst error BC1091: no user frame here\nat Unity.Jobs.JobStruct`1.Execute() (at /Users/bokken/build/output/unity/unity/Runtime/Jobs/Managed/IJob.cs:61)",
                "", 0, out var engineOnly));
            Assert.AreEqual("/Users/bokken/build/output/unity/unity/Runtime/Jobs/Managed/IJob.cs", engineOnly.File);
            Assert.AreEqual(61, engineOnly.Line);
        }

        [Test]
        public void TryParse_RejectsCSharpDiagnostics()
        {
            Assert.IsFalse(BurstErrors.TryParse("Assets/Foo.cs(3,4): error CS0103: The name 'x' does not exist", "Assets/Foo.cs", 3, out _));
            Assert.IsFalse(BurstErrors.TryParse("", "Assets/Foo.cs", 3, out _));
            Assert.IsFalse(BurstErrors.TryParse(null, "Assets/Foo.cs", 3, out _));
        }

        [Test]
        public void Format_OmitsMissingPositionAndUnknownColumn()
        {
            var error = new BurstErrorEntry { File = "", Line = 0, Col = 0, Code = "BC1051", Message = "bad field" };
            Assert.AreEqual("BC1051 bad field", error.Format());
            error.File = "Assets/Job.cs";
            error.Line = 9;
            Assert.AreEqual("Assets/Job.cs:9 BC1051 bad field", error.Format());
            error.Col = 4;
            Assert.AreEqual("Assets/Job.cs:9:4 BC1051 bad field", error.Format());
        }

        [Test]
        public void IsBurstErrorText_MatchesTheInspectorTest()
        {
            Assert.IsTrue(BurstErrors.IsBurstErrorText("While compiling MyGame.Job\nAssets/Job.cs(12,9): Burst error BC1370: throw"));
            Assert.IsFalse(BurstErrors.IsBurstErrorText("// --- Disassembly for MyGame.Job ---\nmov rax, rbx"));
            Assert.IsFalse(BurstErrors.IsBurstErrorText(null));
        }

        [Test]
        public void Collect_KeepsOnlyBurstErrors_DedupesAndKeepsTheBetterPosition()
        {
            var headOnly = new ConsoleLogEntries.CompileDiag
            {
                File = "", Line = 61, Col = 0, Severity = "error", Code = "",
                Message = "Assets/Job.cs(4,9): Burst error BC1051: `MyGame.HeavyJob` is not supported",
            };
            var duplicate = new ConsoleLogEntries.CompileDiag
            {
                File = "Assets/Job.cs", Line = 4, Col = 9, Severity = "error", Code = "",
                Message = "Assets/Job.cs(4,9): Burst error BC1051: `MyGame.HeavyJob` is not supported",
            };
            var notBurst = new ConsoleLogEntries.CompileDiag
            {
                File = "Assets/Foo.cs", Line = 1, Col = 1, Severity = "error", Code = "CS0103",
                Message = "The name 'x' does not exist",
            };
            var warning = new ConsoleLogEntries.CompileDiag
            {
                File = "Assets/Job.cs", Line = 4, Col = 9, Severity = "warning", Code = "BC1370",
                Message = "Assets/Job.cs(4,9): Burst error BC1370: throw",
            };

            // The console copy carries the trace, so it knows a position outside the engine.
            var raw = new List<BurstConsoleEntry>
            {
                new BurstConsoleEntry(0, "(0,0): Burst error BC1051: `MyGame.HeavyJob` is not supported\n" +
                                        "at MyGame.HeavyJob.Execute() (at Assets/Job.cs:4)", "", 61),
                // A Debug.LogError is not a compile error; the caller filters by mode, and this one is
                // shaped like a Burst error to show the filter is what keeps it out.
                new BurstConsoleEntry(0, "(0,0): Burst error BC9999: logged by hand", "Assets/Foo.cs", 2),
            };

            var errors = BurstErrors.Collect(
                new[] { headOnly, duplicate, notBurst, warning },
                new[] { raw[0] });
            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual("BC1051", errors[0].Code);
            Assert.AreEqual(9, errors[0].Col);

            errors = BurstErrors.Collect(new ConsoleLogEntries.CompileDiag[0], new[] { raw[0] });
            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual(4, errors[0].Line);
            Assert.AreEqual("Assets/Job.cs", errors[0].File);
        }

        [Test]
        public void PointsAt_ReadsTheTypeBurstNames()
        {
            var target = Target("MyGame.HeavyJob - (IJobFor)", "MyGame.HeavyJob", "MyGame");

            Assert.IsTrue(BurstErrors.PointsAt(Error("Invalid managed type for the field `Values` of the struct `MyGame.HeavyJob`."), target));
            Assert.IsTrue(BurstErrors.PointsAt(Error("Assets/Job.cs(1,1): Burst error BC1065 while compiling `MyGame.HeavyJob.Execute`"), target));
            Assert.IsTrue(BurstErrors.PointsAt(Error("nested MyGame.Heavy+InnerJob failed"), Target("MyGame.Heavy+InnerJob", "MyGame.Heavy+InnerJob", "MyGame")));
            Assert.IsFalse(BurstErrors.PointsAt(Error("something failed about MyGame.OtherJob"), target));
            Assert.IsFalse(BurstErrors.PointsAt(Error(""), target));

            // Attribution through the stack trace when the head only names the BCL call that broke.
            var traced = new BurstErrorEntry
            {
                Code = "BC1091",
                Message = "External and internal calls are not allowed inside static constructors: System.Console.GetOutputCP()",
                Detail = "at System.Console.WriteLine(System.String value)\n" +
                         "at MyGame.HeavyJob.Execute(MyGame.HeavyJob* this) (at Assets/Job.cs:39)\n" +
                         "at Unity.Jobs.IJobExtensions.JobStruct`1<MyGame.HeavyJob>.Execute(ref MyGame.HeavyJob data) (at /engine/IJob.cs:61)",
            };
            Assert.IsTrue(BurstErrors.PointsAt(traced, target));

            // The rest of the entry is a compiler dump that names unrelated jobs: never match on it.
            var dump = new BurstErrorEntry
            {
                Code = "BC1091",
                Message = "unrelated failure",
                Detail = "While compiling job:\nMyGame.HeavyJob\nMyGame.HeavyJob_IL\n",
            };
            Assert.IsFalse(BurstErrors.PointsAt(dump, target));

            // A word boundary, so a sibling type that shares the prefix is not blamed.
            var sibling = new BurstErrorEntry
            {
                Code = "BC1065",
                Message = "bad call",
                Detail = "at MyGame.HeavyJobUtils.Run() (at Assets/Utils.cs:3)",
            };
            Assert.IsFalse(BurstErrors.PointsAt(sibling, target));

            // The generic producer frame names the job, so a job-producer failure attributes to it.
            var producer = new BurstErrorEntry
            {
                Code = "BC1051",
                Message = "Invalid managed type found for the field `Values`",
                Detail = "at Unity.Jobs.IJobExtensions.JobStruct`1<MyGame.HeavyJob>.Execute(ref MyGame.HeavyJob data) (at /engine/IJob.cs:61)",
            };
            Assert.IsTrue(BurstErrors.PointsAt(producer, target));
        }

        [Test]
        public void Matches_FiltersTypeNamespaceAndAssembly()
        {
            var job = Target("MyGame.Sim.ScaleValuesJob - (IJobFor)", "MyGame.Sim.ScaleValuesJob", "MyGame.Runtime");

            Assert.IsTrue(BurstTargets.Matches(job, null, null, null));
            Assert.IsTrue(BurstTargets.Matches(job, "scale", null, null));
            Assert.IsTrue(BurstTargets.Matches(job, "MyGame.Sim.ScaleValuesJob", null, null));
            Assert.IsFalse(BurstTargets.Matches(job, "OtherJob", null, null));

            Assert.IsTrue(BurstTargets.Matches(job, null, "MyGame", null));
            Assert.IsTrue(BurstTargets.Matches(job, null, "MyGame.Sim", null));
            Assert.IsTrue(BurstTargets.Matches(job, null, "MyGame.Sim.", null));
            Assert.IsFalse(BurstTargets.Matches(job, null, "MyGame.Other", null));

            Assert.IsTrue(BurstTargets.Matches(job, null, null, new[] { "MyGame.Runtime" }));
            Assert.IsTrue(BurstTargets.Matches(job, null, null, new[] { "Other", "mygame.runtime" }));
            Assert.IsFalse(BurstTargets.Matches(job, null, null, new[] { "Other" }));
            // An unresolvable assembly never satisfies an explicit --assembly filter.
            Assert.IsFalse(BurstTargets.Matches(Target("X", "X", ""), null, null, new[] { "MyGame.Runtime" }));
        }

        [Test]
        public void StatusOf_FailsOnAPointingError_OtherwiseTrustsIdleBurstQueue()
        {
            var job = Target("MyGame.HeavyJob - (IJob)", "MyGame.HeavyJob", "MyGame");
            var pointing = new List<BurstErrorEntry> { Error("`MyGame.HeavyJob` is not supported") };
            var unrelated = new List<BurstErrorEntry> { Error("`MyGame.Other` is not supported") };

            Assert.AreEqual(BurstTargets.StatusFailed, BurstTargets.StatusOf(job, pointing, true, true));
            // A failed Burst compile anywhere still leaves this target compiled.
            Assert.AreEqual(BurstTargets.StatusCompiled, BurstTargets.StatusOf(job, unrelated, true, true));
            Assert.AreEqual(BurstTargets.StatusNotCompiled, BurstTargets.StatusOf(job, unrelated, true, false));
            // No idle answer from Burst: never claim compiled, never claim pending either.
            Assert.AreEqual(BurstTargets.StatusUnknown, BurstTargets.StatusOf(job, unrelated, false, true));
            Assert.AreEqual(BurstTargets.StatusUnknown, BurstTargets.StatusOf(job, unrelated, false, false));
            Assert.AreEqual(BurstTargets.StatusCompiled, BurstTargets.StatusOf(job, null, true, true));
        }

        [Test]
        public void Parse_ReadsABurstErrorLikeACompileError()
        {
            // ConsoleLogEntries' C# header pattern misses Burst, which left verify printing
            // "(0,0): Burst error BC1006: ..." with no file, no line and no code.
            var message = "(0,0): Burst error BC1006: The `catch` construction is not supported\n" +
                          "at MyGame.ManagedBodyJob.Execute(MyGame.ManagedBodyJob* this) (at /proj/Assets/Job.cs:39)";

            var diag = ConsoleLogEntries.Parse(message, "", 0, isError: true);

            Assert.AreEqual("/proj/Assets/Job.cs", diag.File);
            Assert.AreEqual(39, diag.Line);
            Assert.AreEqual(0, diag.Col);
            Assert.AreEqual("error", diag.Severity);
            Assert.AreEqual("BC1006", diag.Code);
            Assert.AreEqual("The `catch` construction is not supported", diag.Message);
        }

        [Test]
        public void Parse_LeavesCSharpErrorsAlone()
        {
            var diag = ConsoleLogEntries.Parse("Assets/BC1234.cs(3,4): error CS0103: The name 'x' does not exist", "Assets/BC1234.cs", 3, isError: true);

            Assert.AreEqual("Assets/BC1234.cs", diag.File);
            Assert.AreEqual("CS0103", diag.Code);
            Assert.AreEqual("The name 'x' does not exist", diag.Message);
        }

        [Test]
        public void Count_BucketsByStatus()
        {
            var targets = new List<BurstTargetInfo>
            {
                new BurstTargetInfo { Status = BurstTargets.StatusCompiled },
                new BurstTargetInfo { Status = BurstTargets.StatusCompiled },
                new BurstTargetInfo { Status = BurstTargets.StatusFailed },
                new BurstTargetInfo { Status = BurstTargets.StatusNotCompiled },
                new BurstTargetInfo { Status = "something-new" },
            };

            BurstTargets.Count(targets, out var compiled, out var failed, out var notCompiled, out var unknown);
            Assert.AreEqual(2, compiled);
            Assert.AreEqual(1, failed);
            Assert.AreEqual(1, notCompiled);
            Assert.AreEqual(1, unknown);
        }

        [Test]
        public void IsPackageSourcePath_CoversPackagesCachesAndLocalPackages()
        {
            var roots = new List<string> { "/Users/me/proj/LocalPackages/unity-cli/unity-connector" };

            Assert.IsFalse(BurstTargets.IsPackageSourcePath("Assets/Scripts/Job.cs", roots));
            Assert.IsFalse(BurstTargets.IsPackageSourcePath("/Users/me/proj/Assets/Job.cs", roots));
            Assert.IsTrue(BurstTargets.IsPackageSourcePath("Packages/com.x/Runtime/A.cs", roots));
            Assert.IsTrue(BurstTargets.IsPackageSourcePath("/Users/me/proj/Library/PackageCache/com.x@1/A.cs", roots));
            Assert.IsTrue(BurstTargets.IsPackageSourcePath("/Users/me/proj/LocalPackages/unity-cli/unity-connector/Editor/A.cs", roots));
            Assert.IsTrue(BurstTargets.IsPackageSourcePath("\\Users\\me\\proj\\Packages\\com.x\\A.cs", roots));
            // Unknown source: not counted as the project's own code.
            Assert.IsTrue(BurstTargets.IsPackageSourcePath("", roots));
        }

        static BurstErrorEntry Error(string message)
        {
            return new BurstErrorEntry { File = "Assets/Job.cs", Line = 1, Col = 1, Code = "BC1051", Message = message };
        }
    }
}
