using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector;
using UnityCliConnector.Tools;

namespace UnityCliConnector.Tests
{
    /// <summary>
    /// Covers issue #22: importing System and UnityEngine made Object/Random ambiguous, and
    /// there was no way to drop those imports. Build() is pure, so most tests inspect the
    /// generated text; the last group runs it through the real exec compile path.
    /// </summary>
    [TestFixture]
    public class ExecSourceTests
    {
        static string Build(string code, string[] extraUsings = null, bool includeDefaults = true,
            ExecSource.BodyKind kind = ExecSource.BodyKind.Sync)
        {
            return ExecSource.Build(code, extraUsings, includeDefaults, kind);
        }

        static string BodyOf(string source)
        {
            var start = source.IndexOf("Execute() {");
            Assert.GreaterOrEqual(start, 0, "generated source must contain the Execute method");
            return source.Substring(start);
        }

        [Test]
        public void Defaults_IncludeAliasesForTheAmbiguousUnityTypes()
        {
            var source = Build("return 1;");

            StringAssert.Contains("using Object = UnityEngine.Object;", source);
            StringAssert.Contains("using Random = UnityEngine.Random;", source);
            StringAssert.Contains("using Debug = UnityEngine.Debug;", source);
            StringAssert.Contains("using System;", source);
            StringAssert.Contains("using UnityEngine;", source);
        }

        [Test]
        public void NoDefaults_LeavesOnlyTheCallersUsings()
        {
            var source = Build("return 1;", new[] { "System.Linq" }, false);

            StringAssert.Contains("using System.Linq;", source);
            StringAssert.DoesNotContain("using System;", source);
            StringAssert.DoesNotContain("using UnityEngine;", source);
            StringAssert.DoesNotContain("using Object = UnityEngine.Object;", source);
            StringAssert.DoesNotContain("using Random = UnityEngine.Random;", source);
        }

        [Test]
        public void ExtraUsings_AcceptDirectiveTextWithOrWithoutUsingAndSemicolon()
        {
            var source = Build("return 1;", new[] { "using Unity.Entities;", "Mathf = UnityEngine.Mathf;" });

            StringAssert.Contains("using Unity.Entities;", source);
            StringAssert.Contains("using Mathf = UnityEngine.Mathf;", source);
            StringAssert.DoesNotContain("using using", source);
        }

        [Test]
        public void LeadingUsings_AreHoistedOutOfTheBody()
        {
            var source = Build("using System.Numerics;\nusing V = UnityEngine.Vector3;\nreturn new V();");

            var body = BodyOf(source);
            StringAssert.Contains("using System.Numerics;\nusing V = UnityEngine.Vector3;", source);
            StringAssert.DoesNotContain("using System.Numerics", body);
            StringAssert.DoesNotContain("using V =", body);
            StringAssert.Contains("return new V();", body);
        }

        [Test]
        public void SnippetAlias_OverridesTheDefaultAliasInsteadOfDuplicatingIt()
        {
            var source = Build("using Object = System.Object;\nusing Random = System.Random;\nreturn 1;");

            StringAssert.Contains("using Object = System.Object;", source);
            StringAssert.Contains("using Random = System.Random;", source);
            StringAssert.DoesNotContain("using Object = UnityEngine.Object;", source);
            StringAssert.DoesNotContain("using Random = UnityEngine.Random;", source);
            StringAssert.Contains("using Debug = UnityEngine.Debug;", source);
        }

        [Test]
        public void UsingStatementInsideTheBody_IsNotHoisted()
        {
            var source = Build("using (var s = new System.IO.MemoryStream()) { return s.Length; }");

            StringAssert.Contains("using (var s = new System.IO.MemoryStream())", BodyOf(source));
            StringAssert.DoesNotContain("using using", source);
        }

        [Test]
        public void UsingDeclarationInsideTheBody_IsNotHoisted()
        {
            var source = Build("using var s = new System.IO.MemoryStream(); return s.Length;");

            StringAssert.Contains("using var s = new System.IO.MemoryStream();", BodyOf(source));
        }

        [Test]
        public void UsingDeclarationWithAnExplicitType_IsNotHoisted()
        {
            const string declaration =
                "using System.IO.MemoryStream s = new System.IO.MemoryStream(new byte[4]);";

            var source = Build(declaration + " return s.Length;");

            StringAssert.Contains(declaration, BodyOf(source));
        }

        [Test]
        public void CommentsAroundLeadingUsings_StayInTheBody()
        {
            var source = Build("// pick a namespace\nusing System.Numerics;\n// body comment\nreturn 1;");

            StringAssert.Contains("using System.Numerics;", source);
            var body = BodyOf(source);
            StringAssert.Contains("// pick a namespace", body);
            StringAssert.Contains("// body comment", body);
        }

        [Test]
        public void LeadingUsingMaySpanLines()
        {
            var source = Build(
                "using Map =\n  System.Collections.Generic.Dictionary<string, int>;\nreturn 1;");

            StringAssert.Contains("using Map =", source);
            StringAssert.DoesNotContain("using Map", BodyOf(source));
        }

        [Test]
        public void AsyncAndIteratorBodies_KeepTheirSignatures()
        {
            StringAssert.Contains("async System.Threading.Tasks.Task<object> Execute()",
                Build("return Task.FromResult(1);", null, true, ExecSource.BodyKind.Async));
            StringAssert.Contains("System.Collections.IEnumerator Execute()",
                Build("yield return null;", null, true, ExecSource.BodyKind.Iterator));
        }

        // ─── bare expressions ───────────────────────────────────

        [TestCase("Time.time")]
        [TestCase("40 + 2")]
        [TestCase("\"text\"")]
        [TestCase("new Vector3(0, 1, 0)")]
        [TestCase("EditorApplication.isPlaying")]
        [TestCase("list.Select(x => x * 2).ToList()")]
        [TestCase("\"a;b\"")]
        public void IsSingleExpression_AcceptsAValueWithNoStatements(string code)
        {
            Assert.IsTrue(ExecSource.IsSingleExpression(code), $"{code} is one expression");
        }

        [TestCase("return 1;")]
        [TestCase("Debug.Log(\"x\");")]
        [TestCase("var x = 1")]
        [TestCase("if (true) { }")]
        [TestCase("for (var i = 0; i < 2; i++) { }")]
        [TestCase("yield return null;")]
        [TestCase("await Task.Delay(1)")]
        [TestCase("throw new System.Exception()")]
        [TestCase("x = 1; y = 2;")]
        [TestCase("")]
        public void IsSingleExpression_RejectsStatementsAndBodies(string code)
        {
            Assert.IsFalse(ExecSource.IsSingleExpression(code), $"{code} is a statement body, not an expression");
        }

        [Test]
        public void ExpressionForm_WrapsTheBodyInAReturn_AndKeepsTheLineMap()
        {
            var generated = ExecSource.BuildMapped("Time.time", null, true, ExecSource.BodyKind.Sync,
                returnExpression: true);

            StringAssert.Contains("return (", generated.Text);
            StringAssert.Contains("#line 1 \"exec-snippet\"", generated.Text);
            var lines = generated.Text.Replace("\r\n", "\n").Split('\n');
            Assert.AreEqual("Time.time", lines[generated.BodyStartLine - 1],
                "BodyStartLine must point at the caller's line 1, whatever wraps it");
        }

        [Test]
        public void StatementForm_TerminatesABodyWrittenWithoutItsSemicolon()
        {
            // `Debug.Log("x")` is what a caller types at a prompt; it has to compile as a statement.
            var generated = ExecSource.BuildMapped("Debug.Log(\"x\")", null, true, ExecSource.BodyKind.Sync);
            var body = generated.Text.Substring(generated.Text.IndexOf("#line 1"));
            StringAssert.Contains("    ;", body, "a missing final semicolon is added on its own line");
        }

        // ─── real exec compile path ─────────────────────────────

        static async Task<object> Exec(string code, bool noDefaults = false, string usings = null)
        {
            var p = new JObject { ["code"] = code };
            if (noDefaults) p["no_default_usings"] = true;
            if (usings != null) p["usings"] = usings;
            return await ExecuteCsharp.HandleCommand(p);
        }

        static object DataOf(object response)
        {
            if (response is ErrorResponse err)
                Assert.Fail($"expected success, got: {err.message}");
            return ((SuccessResponse)response).data;
        }

        [Test]
        public async Task DefaultAliases_CompileAndChooseTheUnityTypes()
        {
            var code =
                "return typeof(Object).FullName + \"|\" + typeof(Random).FullName + \"|\" + typeof(Debug).FullName;";

            Assert.AreEqual("UnityEngine.Object|UnityEngine.Random|UnityEngine.Debug", DataOf(await Exec(code)));
        }

        [Test]
        public async Task SnippetAlias_CompilesAndChoosesTheSystemTypes()
        {
            var code = "using Object = System.Object;\nreturn typeof(Object).FullName;";

            Assert.AreEqual("System.Object", DataOf(await Exec(code)));
        }

        [Test]
        public async Task ExtraUsingsWithAGenericAlias_KeepTheCommasInsideTheAlias()
        {
            var code = "Map m = new Map(); m[\"a\"] = 1; return m[\"a\"];";

            Assert.AreEqual(1,
                DataOf(await Exec(code, usings: "Map = System.Collections.Generic.Dictionary<string,int>")));
        }

        [Test]
        public async Task UsingDeclarationWithAnExplicitType_CompilesInTheBody()
        {
            var code =
                "using System.IO.MemoryStream s = new System.IO.MemoryStream(new byte[4]); return s.Length;";

            Assert.AreEqual(4L, DataOf(await Exec(code)));
        }

        [Test]
        public async Task NoDefaultUsings_CombinesWithExtraUsingsToReplaceTheWholeSet()
        {
            Assert.AreEqual("System.Object",
                DataOf(await Exec("return typeof(Object).FullName;", true, "System")));
        }

        [Test]
        public async Task BareExpression_ReturnsItsValueWithoutAReturn()
        {
            // The top-level help documents `exec "Time.time"`; a bare expression never compiled
            // before, because the statement form needs a `return` and a terminating `;`.
            Assert.AreEqual(42, DataOf(await Exec("40 + 2")));
        }

        [Test]
        public async Task VoidCallWithoutASemicolon_RunsAndAnswersNull()
        {
            var response = await Exec("Debug.Log(\"exec void call\")");
            Assert.IsInstanceOf<SuccessResponse>(response,
                (response as ErrorResponse)?.message ?? "a void call is a valid snippet");
            Assert.IsNull(((SuccessResponse)response).data);
        }

        [Test]
        public async Task CompileError_ReportsTheSnippetLine_NotTheGeneratedOne()
        {
            // The generated wrapper used to add ~18 lines, so a snippet error on line 3 read as L21.
            var message = ((ErrorResponse)await Exec("var a = 1;\nvar b = 2;\nundefinedThing.Run();\nreturn a + b;"))
                .message;

            StringAssert.Contains("L3:", message);
            StringAssert.Contains("undefinedThing", message);
        }

        [Test]
        public async Task RuntimeError_NamesTheTypeTheMessageAndTheSnippetLine()
        {
            var message = ((ErrorResponse)await Exec("string s = null;\nreturn s.Length;")).message;

            StringAssert.Contains("Runtime error:", message);
            StringAssert.Contains("System.NullReferenceException", message);
            StringAssert.Contains("Object reference not set", message);
            StringAssert.Contains("in snippet:line 2", message);
        }
    }
}
