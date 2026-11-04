using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityCliConnector;

namespace UnityCliConnector.Tests
{
    /// The staleness check is the difference between "the editor said it compiled" and "the DLL
    /// contains the edit", so it is tested against real files with controlled mtimes.
    [TestFixture]
    public class AssemblyStalenessTests
    {
        const long BaseMs = 1_700_000_000_000L;
        string _tmp;

        [SetUp]
        public void SetUp()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "unity-cli-staleness-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmp);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
            }
            catch (Exception)
            {
            }
        }

        [Test]
        public void Evaluate_DllNewerThanEverySource_IsNotStale()
        {
            var source = Write("Game/Player.cs", BaseMs);
            var dll = Write("Library/ScriptAssemblies/Game.dll", BaseMs + 1_000);

            var result = AssemblyStaleness.Evaluate(new[] { Input("Game", dll, source) }, _tmp, 5_000);

            Assert.IsTrue(result.Complete);
            Assert.AreEqual(0, result.StaleCount);
            Assert.AreEqual(0, result.Stale.Count);
        }

        [Test]
        public void Evaluate_SourceNewerThanDll_ReportsBothTimestampsAndTheSource()
        {
            var source = Write("Game/Player.cs", BaseMs + 3_000);
            var dll = Write("Library/ScriptAssemblies/Game.dll", BaseMs + 1_000);

            var result = AssemblyStaleness.Evaluate(new[] { Input("Game", dll, source) }, _tmp, 5_000);

            Assert.AreEqual(1, result.StaleCount);
            var stale = result.Stale[0];
            Assert.AreEqual("Game", stale.Name);
            Assert.AreEqual(BaseMs + 1_000, stale.DllMtimeMs);
            Assert.AreEqual("Game/Player.cs", stale.NewestSource);
            Assert.AreEqual(BaseMs + 3_000, stale.NewestSourceMtimeMs);
            Assert.IsFalse(stale.Missing);
            StringAssert.StartsWith("stale assembly: Game.dll (built ", AssemblyStaleness.FormatLine(result));
            StringAssert.Contains("Game/Player.cs", AssemblyStaleness.FormatLine(result));
            StringAssert.Contains("run unity-cli editor refresh --compile", AssemblyStaleness.FormatLine(result));
        }

        [Test]
        public void Evaluate_AssemblyDefinitionCountsAsASource()
        {
            var source = Write("Game/Player.cs", BaseMs);
            var asmdef = Write("Game/Game.asmdef", BaseMs + 3_000);
            var dll = Write("Library/ScriptAssemblies/Game.dll", BaseMs + 1_000);

            var result = AssemblyStaleness.Evaluate(new[] { Input("Game", dll, source, asmdef) }, _tmp, 5_000);

            Assert.AreEqual(1, result.StaleCount, "an asmdef change rebuilds the assembly just like a .cs change");
            Assert.AreEqual("Game/Game.asmdef", result.Stale[0].NewestSource);
        }

        [Test]
        public void Evaluate_MissingDllIsStaleRatherThanUnknown()
        {
            var source = Write("Game/Player.cs", BaseMs);

            var result = AssemblyStaleness.Evaluate(
                new[] { Input("Game", Path.Combine(_tmp, "missing.dll"), source) }, _tmp, 5_000);

            Assert.AreEqual(1, result.StaleCount);
            Assert.IsTrue(result.Stale[0].Missing);
            Assert.IsNull(result.Stale[0].DllMtimeMs);
            StringAssert.Contains("(missing)", AssemblyStaleness.FormatLine(result));
        }

        [Test]
        public void Evaluate_CapsTheListedNamesButKeepsTheCount()
        {
            var inputs = new List<AssemblyStaleness.AssemblyInput>();
            for (var i = 0; i < AssemblyStaleness.MaxListed + 2; i++)
            {
                var source = Write($"Game{i}/Test.cs", BaseMs + 3_000);
                var dll = Write($"Library/ScriptAssemblies/Game{i}.dll", BaseMs);
                inputs.Add(Input($"Game{i}", dll, source));
            }

            var result = AssemblyStaleness.Evaluate(inputs, _tmp, 5_000);

            Assert.AreEqual(AssemblyStaleness.MaxListed + 2, result.StaleCount);
            Assert.AreEqual(AssemblyStaleness.MaxListed, result.Stale.Count);
            Assert.IsTrue(result.Truncated);
            StringAssert.Contains($"(+{AssemblyStaleness.MaxListed + 1} more)", AssemblyStaleness.FormatLine(result));
        }

        [Test]
        public void Evaluate_StoppingAtTheBudgetIsIncomplete_NotClean()
        {
            var source = Write("Game/Player.cs", BaseMs + 3_000);
            var dll = Write("Library/ScriptAssemblies/Game.dll", BaseMs);

            var result = AssemblyStaleness.Evaluate(new[] { Input("Game", dll, source) }, _tmp, 0);

            Assert.IsFalse(result.Complete, "a scan that never looked must not claim the assembly is current");
        }

        [Test]
        public void Evaluate_SourceTheLastCompileCouldHaveRead_IsCoveredRatherThanStale()
        {
            var source = Write("Game/Player.cs", BaseMs + 1_000);
            var dll = Write("Library/ScriptAssemblies/Game.dll", BaseMs);

            // Unity does not rewrite a DLL whose contents it considers current, so a compile that
            // started after the source left the old DLL in place on purpose.
            var result = AssemblyStaleness.Evaluate(
                new[] { Input("Game", dll, source) }, _tmp, 5_000, BaseMs + 2_000);

            Assert.AreEqual(0, result.StaleCount);
            Assert.AreEqual(1, result.CoveredCount);
            Assert.IsNull(AssemblyStaleness.FormatLine(result), "a covered assembly must not read as a refusal");
            StringAssert.Contains("not stale: Game.dll (built ", AssemblyStaleness.FormatCoveredLine(result));
            StringAssert.Contains("read that source and kept the DLL", AssemblyStaleness.FormatCoveredLine(result));
        }

        [Test]
        public void Evaluate_SourceWrittenAfterTheLastCompileStarted_IsStillStale()
        {
            var source = Write("Game/Player.cs", BaseMs + 3_000);
            var dll = Write("Library/ScriptAssemblies/Game.dll", BaseMs);

            var result = AssemblyStaleness.Evaluate(
                new[] { Input("Game", dll, source) }, _tmp, 5_000, BaseMs + 2_000);

            Assert.AreEqual(1, result.StaleCount, "the edit postdates the compile, so nothing has read it yet");
            Assert.AreEqual(0, result.CoveredCount);
            Assert.IsNull(AssemblyStaleness.FormatCoveredLine(result));
        }

        [Test]
        public void Evaluate_MissingDllIsNeverCovered()
        {
            var source = Write("Game/Player.cs", BaseMs + 1_000);

            var result = AssemblyStaleness.Evaluate(
                new[] { Input("Game", Path.Combine(_tmp, "missing.dll"), source) }, _tmp, 5_000, BaseMs + 2_000);

            Assert.AreEqual(1, result.StaleCount, "a compile cannot decide an absent DLL is up to date");
            Assert.AreEqual(0, result.CoveredCount);
        }

        [Test]
        public void ResolvePath_PipelineRelativePaths_MatchTheAbsoluteSourceRoots()
        {
            // The shape CompilationPipeline actually hands back (probed on 6000.6.5f1): project-relative
            // sources and outputs. Compared against absolute roots they matched nothing, so every
            // assembly was filtered out of the scan and staleness could never be reported.
            var roots = new[] { "/proj/Assets", "/proj/LocalPackages/cli/unity-connector" };
            string ResolveVirtual(string path) =>
                path.StartsWith("Packages/com.cli.connector/", StringComparison.Ordinal)
                    ? "/proj/LocalPackages/cli/unity-connector" + path.Substring("Packages/com.cli.connector".Length)
                    : null;

            var sources = new List<string>
            {
                AssemblyStaleness.ResolvePath("Assets/ClaudeTools/Editor/BuildWithProfile.cs", "/proj", null),
                AssemblyStaleness.ResolvePath("Packages/com.cli.connector/Tests/Editor/HeartbeatPayloadTests.cs", "/proj", ResolveVirtual),
            };

            foreach (var source in sources)
                StringAssert.StartsWith("/proj/", source);
            Assert.IsTrue(AssemblyStaleness.IsProjectAssembly(sources, roots));
            Assert.AreEqual("/proj/Library/ScriptAssemblies/Assembly-CSharp-Editor.dll",
                AssemblyStaleness.ResolvePath("Library/ScriptAssemblies/Assembly-CSharp-Editor.dll", "/proj", null));
        }

        [Test]
        public void ResolvePath_PackageCacheSources_StayOutsideTheProjectRoots()
        {
            string ResolveVirtual(string path) =>
                path.StartsWith("Packages/com.unity.inputsystem/", StringComparison.Ordinal)
                    ? "/proj/Library/PackageCache/com.unity.inputsystem@abc" + path.Substring("Packages/com.unity.inputsystem".Length)
                    : null;

            var sources = new List<string>
            {
                AssemblyStaleness.ResolvePath("Packages/com.unity.inputsystem/InputSystem/AssemblyInfo.cs", "/proj", ResolveVirtual),
            };

            Assert.IsFalse(AssemblyStaleness.IsProjectAssembly(sources, new[] { "/proj/Assets" }),
                "a registry/git package under Library/PackageCache is not the caller's to rebuild");
        }

        [Test]
        public void ResolvePath_AbsoluteAndUnknownVirtualPaths_AreLeftAlone()
        {
            Assert.AreEqual("/proj/Assets/A.cs", AssemblyStaleness.ResolvePath("/proj/Assets/A.cs", "/other", null));
            Assert.AreEqual("/proj/Packages/Unmounted/File.cs",
                AssemblyStaleness.ResolvePath("Packages/Unmounted/File.cs", "/proj", null),
                "with no package to resolve through, fall back to the project-relative meaning");
            Assert.AreEqual("", AssemblyStaleness.ResolvePath(null, "/proj", null));
        }

        [Test]
        public void ScanProject_AFilePackageAssembly_IsScanned()
        {
            // This package is installed as a file: package, and its pipeline sources come back as
            // Packages/com.youngwoocho02.unity-cli-connector/... . If the virtual path is not resolved
            // to its real directory the assembly is filtered out and the scan comes back empty, which is
            // exactly how this check stayed silent in the live project.
            var only = new HashSet<string> { "UnityCliConnector.Tests.Editor" };
            var result = AssemblyStaleness.ScanProject(AssemblyStaleness.DefaultBudgetMs, only);

            Assert.AreEqual(1, result.ScannedAssemblies,
                "this test assembly must count as a project assembly");
        }

        [Test]
        public void ScanProject_APackageCacheAssembly_IsNotScanned()
        {
            var only = new HashSet<string> { "UnityEngine.TestRunner" };
            var result = AssemblyStaleness.ScanProject(AssemblyStaleness.DefaultBudgetMs, only);

            Assert.AreEqual(0, result.ScannedAssemblies,
                "a built-in package's DLL is not the caller's to rebuild");
        }

        AssemblyStaleness.AssemblyInput Input(string name, string dll, params string[] sources) =>
            new AssemblyStaleness.AssemblyInput { Name = name, OutputPath = dll, Sources = sources };

        string Write(string relative, long wroteUtcMs)
        {
            var path = Path.Combine(_tmp, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "// written by AssemblyStalenessTests\n");
            File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(wroteUtcMs).UtcDateTime);
            return path;
        }
    }
}
