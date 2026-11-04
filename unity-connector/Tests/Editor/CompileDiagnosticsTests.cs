using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector.Tools;
using UnityEditor.Compilation;

namespace UnityCliConnector.Tests
{
    [TestFixture]
    public class CompileDiagnosticsTests
    {
        [SetUp]
        public void SetUp()
        {
            CompilationGate.ResetForTests();
            CompilationGate.FileExistsForTests = _ => true;
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>();
        }

        [TearDown]
        public void TearDown()
        {
            CompilationGate.ResetForTests();
            ConsoleLogEntries.TestOverride = null;
        }

        [Test]
        public void ReportsPipelineErrors_EvenThoughTheyWouldTripTheGate()
        {
            Seed(("Assets/Foo.cs", 10, 5, "Assets/Foo.cs(10,5): error CS0103: The name 'x' does not exist", CompilerMessageType.Error));

            var data = Run(new JObject());

            Assert.IsFalse(data["compiling"].Value<bool>());
            Assert.AreEqual(1, data["total"].Value<int>());
            var d = (JObject)data["diagnostics"][0];
            Assert.AreEqual("Assets/Foo.cs", d["file"].Value<string>());
            Assert.AreEqual(10, d["line"].Value<int>());
            Assert.AreEqual(5, d["col"].Value<int>());
            Assert.AreEqual("error", d["severity"].Value<string>());
            Assert.AreEqual("CS0103", d["code"].Value<string>());
        }

        [Test]
        public void SourceTreeOnly_DropsPackageCacheAndLibrary()
        {
            Seed(("Assets/Foo.cs", 1, 1, "error CS0103: a", CompilerMessageType.Error),
                 ("Library/PackageCache/com.x@1/Bar.cs", 2, 2, "error CS0103: b", CompilerMessageType.Error),
                 ("Library/Bee/gen.cs", 3, 3, "error CS0103: c", CompilerMessageType.Error));

            Assert.AreEqual(3, Run(new JObject())["total"].Value<int>());
            var data = Run(new JObject { ["source_tree_only"] = true });
            Assert.AreEqual(1, data["total"].Value<int>());
            Assert.AreEqual("Assets/Foo.cs", data["diagnostics"][0]["file"].Value<string>());
        }

        [Test]
        public void Warnings_OnlyWhenAsked()
        {
            Seed(("Assets/Foo.cs", 1, 1, "warning CS0618: old", CompilerMessageType.Warning));

            Assert.AreEqual(0, Run(new JObject())["total"].Value<int>());
            Assert.AreEqual(1, Run(new JObject { ["include_warnings"] = true })["total"].Value<int>());
        }

        [Test]
        public void ConsoleOnlyErrors_AreIncluded()
        {
            // Burst errors never reach the CompilationPipeline; they only exist in the console.
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Sys.cs", Line = 7, Col = 3, Severity = "error", Code = "BC1028", Message = "Burst error",
                },
            };

            var data = Run(new JObject());
            Assert.AreEqual(1, data["total"].Value<int>());
            Assert.AreEqual("BC1028", data["diagnostics"][0]["code"].Value<string>());
        }

        [Test]
        public void Limit_CapsTheListButNotTheTotal()
        {
            Seed(("Assets/A.cs", 1, 1, "error CS0103: a", CompilerMessageType.Error),
                 ("Assets/B.cs", 1, 1, "error CS0103: b", CompilerMessageType.Error));

            var data = Run(new JObject { ["limit"] = 1 });
            Assert.AreEqual(2, data["total"].Value<int>());
            Assert.AreEqual(1, ((JArray)data["diagnostics"]).Count);
        }

        [Test]
        public void MissingMemberError_CarriesTheClosestRealMemberAsAHint()
        {
            Seed(("Assets/Foo.cs", 12, 9,
                "Assets/Foo.cs(12,9): error CS1061: 'Transform' does not contain a definition for 'positon' " +
                "and no accessible extension method 'positon' accepting a first argument of type 'Transform' " +
                "could be found (are you missing a using directive or an assembly reference?)",
                CompilerMessageType.Error));

            var d = (JObject)((JArray)Run(new JObject { ["hint_budget"] = 5000 })["diagnostics"])[0];
            var hint = d["hint"].Value<string>();
            Assert.IsNotNull(hint, $"the whole point of the hint is that it rides along: {d}");
            StringAssert.Contains("Transform.position", hint);
        }

        [Test]
        public void Hints_Off_SkipsTheLookups()
        {
            Seed(("Assets/Foo.cs", 12, 9,
                "Assets/Foo.cs(12,9): error CS1061: 'Transform' does not contain a definition for 'positon'",
                CompilerMessageType.Error));

            var d = (JObject)Run(new JObject { ["include_hints"] = false })["diagnostics"][0];
            Assert.AreEqual(JTokenType.Null, d["hint"].Type, "the key is present and empty, not a hint");
            Assert.AreEqual("CS1061", d["code"].Value<string>(), "the error itself is still reported");
        }

        static void Seed(params (string file, int line, int col, string message, CompilerMessageType type)[] messages)
        {
            var list = new List<CompilerMessage>();
            foreach (var m in messages)
                list.Add(new CompilerMessage { file = m.file, line = m.line, column = m.col, message = m.message, type = m.type });
            CompilationGate.SeedForTests("Library/ScriptAssemblies/Test.dll", list.ToArray());
        }

        static JObject Run(JObject @params)
        {
            var response = CompileDiagnostics.HandleCommand(@params);
            Assert.IsInstanceOf<SuccessResponse>(response);
            return JObject.FromObject(((SuccessResponse)response).data);
        }
    }
}
