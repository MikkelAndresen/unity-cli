using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCliConnector.Tests
{
    /// Covers the CompilationGate short-circuit behavior + the ConsoleLogEntries
    /// reflection wrapper. The `_BindingsAreAvailable` test is the canary on Unity
    /// upgrades — if it fails, diff ConsoleLogEntries.cs against ReadConsole.cs
    /// in this package and update the binding names.
    [TestFixture]
    public class CompilationGateTests
    {
        [SetUp]
        public void SetUp()
        {
            CompilationGate.ResetForTests();
            ConsoleLogEntries.TestOverride = null;
        }

        [TearDown]
        public void TearDown()
        {
            CompilationGate.ResetForTests();
            ConsoleLogEntries.TestOverride = null;
        }

        // ─── Reflection canary ──────────────────────────────────

        [Test]
        public void ConsoleLogEntries_ReflectionBindings_AreAvailable()
        {
            Assert.IsTrue(
                ConsoleLogEntries.IsAvailable,
                $"LogEntries reflection failed on Unity {Application.unityVersion}: " +
                $"{ConsoleLogEntries.InitError}. " +
                "See ConsoleLogEntries.cs docstring for the fix protocol.");
        }

        [Test]
        public void ConsoleLogEntries_Snapshot_DoesNotThrow()
        {
            ConsoleLogEntries.TestOverride = null; // ensure real path
            var ok = ConsoleLogEntries.TryGetCompileDiagnostics(
                includeWarnings: false, out var diagnostics);
            Assert.IsTrue(ok, "TryGetCompileDiagnostics reported degraded state on a clean session.");
            Assert.IsNotNull(diagnostics);
        }

        // ─── CheckOrNull contract ───────────────────────────────

        [Test]
        public void CheckOrNull_CleanState_ReturnsNull()
        {
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>();
            Assert.IsNull(CompilationGate.CheckOrNull(new JObject()));
        }

        [Test]
        public void CheckOrNull_PipelineError_ReturnsErrorResponse_TaggedPipeline()
        {
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>();
            CompilationGate.SeedForTests("Library/ScriptAssemblies/Foo.dll", new[]
            {
                MakeCompilerMessage("Assets/Foo.cs", 10, 5, "CS0103: bork", CompilerMessageType.Error)
            });

            var response = CompilationGate.CheckOrNull(new JObject());
            Assert.IsInstanceOf<ErrorResponse>(response);

            var (errors, _) = ExtractDiagnostics((ErrorResponse)response);
            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual("Assets/Foo.cs", errors[0]["file"].Value<string>());
            Assert.AreEqual(10, errors[0]["line"].Value<int>());
            Assert.AreEqual(5, errors[0]["col"].Value<int>());
            Assert.AreEqual("error", errors[0]["severity"].Value<string>());
            Assert.AreEqual("pipeline", errors[0]["source"].Value<string>());
            Assert.AreEqual("Foo", errors[0]["assembly"].Value<string>());
        }

        [Test]
        public void CheckOrNull_SkipFlag_BothCasings_BypassesGate()
        {
            CompilationGate.SeedForTests("Library/ScriptAssemblies/Foo.dll", new[]
            {
                MakeCompilerMessage("Assets/Foo.cs", 1, 1, "bork", CompilerMessageType.Error)
            });
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>();

            Assert.IsNull(CompilationGate.CheckOrNull(new JObject { ["skip_compile_check"] = true }),
                "snake_case opt-out should bypass the gate");
            Assert.IsNull(CompilationGate.CheckOrNull(new JObject { ["skipCompileCheck"] = true }),
                "camelCase opt-out should bypass the gate");
        }

        // ─── Dedup ──────────────────────────────────────────────

        [Test]
        public void CheckOrNull_PipelineAndConsole_MatchingLocation_CollapseToOne_PipelineWins()
        {
            CompilationGate.SeedForTests("Library/ScriptAssemblies/Foo.dll", new[]
            {
                MakeCompilerMessage("Assets/Foo.cs", 10, 5, "from pipeline", CompilerMessageType.Error)
            });
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Foo.cs", Line = 10, Col = 5,
                    Severity = "error", Code = "CS0103", Message = "from console"
                }
            };

            var (errors, _) = ExtractDiagnostics((ErrorResponse)CompilationGate.CheckOrNull(new JObject()));
            Assert.AreEqual(1, errors.Count, "duplicate location should collapse");
            Assert.AreEqual("pipeline", errors[0]["source"].Value<string>(),
                "pipeline collected first should win");
        }

        [Test]
        public void CheckOrNull_PathSeparators_NormalizedForDedup()
        {
            CompilationGate.SeedForTests("Library/ScriptAssemblies/Foo.dll", new[]
            {
                MakeCompilerMessage("Assets\\Foo\\Bar.cs", 7, 3, "msg", CompilerMessageType.Error)
            });
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Foo/Bar.cs", Line = 7, Col = 3,
                    Severity = "error", Code = "", Message = "msg"
                }
            };

            var (errors, _) = ExtractDiagnostics((ErrorResponse)CompilationGate.CheckOrNull(new JObject()));
            Assert.AreEqual(1, errors.Count, "forward/back slashes on same path should dedupe");
        }

        [Test]
        public void CheckOrNull_SameFileLine_DifferentColumns_AreNotDeduped()
        {
            CompilationGate.SeedForTests("Library/ScriptAssemblies/Foo.dll", new[]
            {
                MakeCompilerMessage("Assets/Foo.cs", 10, 5, "first", CompilerMessageType.Error),
                MakeCompilerMessage("Assets/Foo.cs", 10, 12, "second", CompilerMessageType.Error)
            });
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>();

            var (errors, _) = ExtractDiagnostics((ErrorResponse)CompilationGate.CheckOrNull(new JObject()));
            Assert.AreEqual(2, errors.Count, "different column on same line is a distinct diagnostic");
        }

        // ─── ConsoleLogEntries.Parse branches ───────────────────

        [Test]
        public void Parse_StandardHeader_ExtractsAllFields()
        {
            var d = ConsoleLogEntries.Parse(
                "Assets/Foo.cs(12,5): error CS0103: The name 'x' does not exist",
                fileFallback: null, lineFallback: 0, isError: true);

            Assert.AreEqual("Assets/Foo.cs", d.File);
            Assert.AreEqual(12, d.Line);
            Assert.AreEqual(5, d.Col);
            Assert.AreEqual("error", d.Severity);
            Assert.AreEqual("CS0103", d.Code);
            Assert.AreEqual("The name 'x' does not exist", d.Message);
        }

        [Test]
        public void Parse_UnstructuredMessage_FallsBackToLogEntryFields()
        {
            var d = ConsoleLogEntries.Parse(
                "Some loose runtime exception text\n  at Foo.Bar()\n  at Baz.Qux()",
                fileFallback: "Assets/Other.cs", lineFallback: 42, isError: true);

            Assert.AreEqual("Assets/Other.cs", d.File);
            Assert.AreEqual(42, d.Line);
            Assert.AreEqual(0, d.Col);
            Assert.AreEqual("error", d.Severity);
            Assert.AreEqual("", d.Code);
            Assert.AreEqual("Some loose runtime exception text", d.Message,
                "fallback should keep just the first line of the message");
        }

        // ─── TryConvertEntry branches ───────────────────────────

        [Test]
        public void TryConvertEntry_CompileError_Bit_Converts()
        {
            var ok = ConsoleLogEntries.TryConvertEntry(
                mode: ConsoleLogEntries.ScriptCompileErrorMask,
                message: "Assets/Foo.cs(3,7): error CS1002: ; expected",
                file: null, line: 0, includeWarnings: false, out var diag);

            Assert.IsTrue(ok);
            Assert.AreEqual("error", diag.Severity);
            Assert.AreEqual("CS1002", diag.Code);
            Assert.AreEqual(3, diag.Line);
        }

        [Test]
        public void TryConvertEntry_CompileWarning_Bit_RespectsIncludeWarnings()
        {
            const int mode = ConsoleLogEntries.ScriptCompileWarningMask;
            const string msg = "Assets/Foo.cs(3,7): warning CS0168: unused";

            Assert.IsFalse(ConsoleLogEntries.TryConvertEntry(
                mode, msg, null, 0, includeWarnings: false, out _),
                "warning should drop when includeWarnings=false");

            var ok = ConsoleLogEntries.TryConvertEntry(
                mode, msg, null, 0, includeWarnings: true, out var diag);
            Assert.IsTrue(ok, "warning should convert when includeWarnings=true");
            Assert.AreEqual("warning", diag.Severity);
        }

        [Test]
        public void TryConvertEntry_StickyError_Bit_Converts()
        {
            // The persistent "All compiler errors have to be fixed…" banner is a
            // StickyError, not a ScriptCompileError. It still means the editor has
            // unresolved compile failures, so the gate must treat it as blocking.
            var ok = ConsoleLogEntries.TryConvertEntry(
                mode: ConsoleLogEntries.StickyErrorMask,
                message: "All compiler errors have to be fixed before you can enter play mode!",
                file: null, line: 0, includeWarnings: false, out var diag);

            Assert.IsTrue(ok, "sticky compile-error banner should be treated as a compile error");
            Assert.AreEqual("error", diag.Severity);
        }

        [Test]
        public void TryConvertEntry_NonCompileMode_Rejected()
        {
            // Runtime LogType.Error bit (1 << 0) — not a ScriptCompileError. Also
            // covers ScriptingError (1 << 8), the bit Debug.LogError sets, which we
            // intentionally keep OUT of CompileBlockingMask.
            Assert.IsFalse(ConsoleLogEntries.TryConvertEntry(
                mode: 1 << 0, message: "runtime error", file: null, line: 0,
                includeWarnings: true, out _));
            Assert.IsFalse(ConsoleLogEntries.TryConvertEntry(
                mode: 1 << 8, message: "Debug.LogError text", file: null, line: 0,
                includeWarnings: true, out _),
                "ScriptingError (Debug.LogError) must not trip the compilation gate");
        }

        [Test]
        public void TryConvertEntry_EmptyMessage_Rejected()
        {
            Assert.IsFalse(ConsoleLogEntries.TryConvertEntry(
                mode: ConsoleLogEntries.ScriptCompileErrorMask,
                message: "", file: null, line: 0, includeWarnings: false, out _));
        }

        // ─── Real LogEntries iteration ──────────────────────────

        [Test]
        public void TryGetCompileDiagnostics_WithRealConsoleEntry_IteratesWithoutThrowing()
        {
            // Hits the reflection-iteration body of TryGetCompileDiagnostics (the path
            // that the empty-console smoke test couldn't reach). The Debug.LogError entry
            // doesn't have the ScriptCompileError mode bit set — only the compiler emits
            // those — so TryConvertEntry rejects it. But the for-loop still executes,
            // unboxes the entry fields, and reaches the `finally` _end cleanup.
            ConsoleLogEntries.TestOverride = null;

            // LogAssert.ignoreFailingMessages stops the runner from failing on our
            // intentional LogError. ExpectedLogType API would also work but is per-message.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try
            {
                Debug.LogError("intentional test log to seed the console buffer");

                var ok = ConsoleLogEntries.TryGetCompileDiagnostics(
                    includeWarnings: false, out var diagnostics);

                Assert.IsTrue(ok);
                Assert.IsNotNull(diagnostics);
                // Our entry has LogType.Error mode (bit 0), not ScriptCompileError (bit 11),
                // so the conversion path rejects it and the list stays empty.
                Assert.AreEqual(0, diagnostics.Count,
                    "Debug.LogError should not be classified as a compile error");
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }
        }

        // ─── Helpers ────────────────────────────────────────────

        static CompilerMessage MakeCompilerMessage(string file, int line, int column, string message,
            CompilerMessageType type)
        {
            return new CompilerMessage
            {
                file = file,
                line = line,
                column = column,
                message = message,
                type = type
            };
        }

        static (List<JObject> diagnostics, int total) ExtractDiagnostics(ErrorResponse response)
        {
            // ErrorResponse.data is an anonymous object — read it via JObject.FromObject.
            var data = JObject.FromObject(response.data);
            var list = new List<JObject>();
            foreach (var token in (JArray)data["compile_errors"])
                list.Add((JObject)token);
            return (list, data["total"].Value<int>());
        }
    }
}