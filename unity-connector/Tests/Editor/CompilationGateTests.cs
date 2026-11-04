using System;
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
            CompilationGate.FileExistsForTests = _ => true;
            ConsoleLogEntries.TestOverride = null;
        }

        [TearDown]
        public void TearDown()
        {
            CompilationGate.ResetForTests();
            CompilationGate.RestoreLiveStateForTests();
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

        // ─── Console staleness (ordinal pin) ────────────────────

        [Test]
        public void CheckOrNull_StaleConsoleEntry_AfterCleanCompile_DoesNotBlock()
        {
            var now = NowMs();
            CompilationGate.SeedCompileClockForTests(now - 5000, now - 1000);
            // The console held entries 3..10 when the compile started; this entry is from before it.
            CompilationGate.SeedConsoleBoundaryForTests(maxIndex: 10, firstIndex: 3);
            CompilationGate.ConsoleRangeForTests = new CompilationGate.ConsoleIndexRange(true, 3, 12);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Old.cs", Line = 1, Col = 1, Severity = "error",
                    Code = "CS1002", Message = "; expected", ConsoleIndex = 6
                }
            };

            Assert.IsNull(CompilationGate.CheckOrNull(new JObject()),
                "a console error logged before the last compile started must not block exec");
        }

        [Test]
        public void CheckOrNull_FreshConsoleEntry_FromCurrentCompile_Blocks()
        {
            var now = NowMs();
            CompilationGate.SeedCompileClockForTests(now - 5000, now - 1000);
            CompilationGate.SeedConsoleBoundaryForTests(maxIndex: 10, firstIndex: 3);
            CompilationGate.ConsoleRangeForTests = new CompilationGate.ConsoleIndexRange(true, 3, 12);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Fresh.cs", Line = 7, Col = 2, Severity = "error",
                    Code = "CS0103", Message = "The name 'x' does not exist", ConsoleIndex = 11
                }
            };

            var response = CompilationGate.CheckOrNull(new JObject());
            Assert.IsInstanceOf<ErrorResponse>(response);
            var (errors, _) = ExtractDiagnostics((ErrorResponse)response);
            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual("console", errors[0]["source"].Value<string>());

            var message = ((ErrorResponse)response).message;
            StringAssert.Contains("Assets/Fresh.cs(7,2)", message, "the blocked message must name the error");
            StringAssert.Contains("skip_compile_check", message, "the escape hatch must be in the message");
        }

        [Test]
        public void CheckOrNull_AfterReload_KeepsConsoleErrorsFromTheCompileThatReloaded()
        {
            // A successful compile reloads the domain, so the new domain has no start clock and the
            // console warnings/errors it logged still belong to the compile that just finished.
            // They carry an ordinal, not necessarily a timestamp this domain can trust.
            var now = NowMs();
            CompilationGate.SeedCompileClockForTests(0, now, finishedViaReload: true);
            CompilationGate.SeedConsoleBoundaryForTests(maxIndex: 10, firstIndex: 3);
            CompilationGate.ConsoleRangeForTests = new CompilationGate.ConsoleIndexRange(true, 3, 12);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Warned.cs", Line = 4, Col = 1, Severity = "error",
                    Code = "CS0168", Message = "The variable is declared but never used", ConsoleIndex = 12
                }
            };

            var response = CompilationGate.CheckOrNull(new JObject());
            Assert.IsInstanceOf<ErrorResponse>(response,
                "the compile that caused the reload is the last one: its console diagnostics are current");
        }

        [Test]
        public void CheckOrNull_AfterReload_DropsConsoleErrorsFromBeforeTheCompile()
        {
            var now = NowMs();
            CompilationGate.SeedCompileClockForTests(0, now, finishedViaReload: true);
            CompilationGate.SeedConsoleBoundaryForTests(maxIndex: 10, firstIndex: 3);
            CompilationGate.ConsoleRangeForTests = new CompilationGate.ConsoleIndexRange(true, 3, 12);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Older.cs", Line = 4, Col = 1, Severity = "error",
                    Code = "CS0168", Message = "stale", ConsoleIndex = 9
                }
            };

            Assert.IsNull(CompilationGate.CheckOrNull(new JObject()));
        }

        [Test]
        public void CheckOrNull_ConsoleClearedSinceCompileStart_KeepsTheFirstIndexDrop()
        {
            // LogEntries.Clear restarts the ordinals at 0: the oldest entry now sits below the
            // ordinal the compile-start pin saw, so everything left was logged after that start.
            var now = NowMs();
            CompilationGate.SeedCompileClockForTests(now - 5000, now - 1000);
            CompilationGate.SeedConsoleBoundaryForTests(maxIndex: 10, firstIndex: 3);
            CompilationGate.ConsoleRangeForTests = new CompilationGate.ConsoleIndexRange(true, 0, 2);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/AfterClear.cs", Line = 1, Col = 1, Severity = "error",
                    Code = "CS1002", Message = "; expected", ConsoleIndex = 1
                }
            };

            var response = CompilationGate.CheckOrNull(new JObject());
            Assert.IsInstanceOf<ErrorResponse>(response,
                "after a console Clear the restarted ordinals are not evidence of staleness");
        }

        [Test]
        public void CheckOrNull_ConsoleClearedSinceCompileStart_KeepsTheMaxIndexDrop()
        {
            // A console that started empty pins first=0, so only the shrunk maximum reveals the Clear.
            var now = NowMs();
            CompilationGate.SeedCompileClockForTests(now - 5000, now - 1000);
            CompilationGate.SeedConsoleBoundaryForTests(maxIndex: 10, firstIndex: 0);
            CompilationGate.ConsoleRangeForTests = new CompilationGate.ConsoleIndexRange(true, 0, 4);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/AfterClear.cs", Line = 2, Col = 1, Severity = "error",
                    Code = "CS1002", Message = "; expected", ConsoleIndex = 2
                }
            };

            Assert.IsInstanceOf<ErrorResponse>(CompilationGate.CheckOrNull(new JObject()));
        }

        [Test]
        public void NoOpCompile_DoesNotAdvanceTheBoundary()
        {
            // The CLI's `refresh --compile` can run a second, empty compile right after the real one
            // (measured against the editor: start/finish fire, no assemblyCompilationFinished). The
            // console still holds the real compile's diagnostics, so the boundary must not move.
            CompilationGate.SeedConsoleSnapshotForTests(maxIndex: 10, firstIndex: 3);
            CompilationGate.ConsoleRangeForTests = new CompilationGate.ConsoleIndexRange(true, 3, 10);
            CompilationGate.NoteCompileStartedForTests();
            CompilationGate.NoteCompileFinishedForTests();
            CompilationGate.NoteCompileStartedForTests();
            CompilationGate.NoteCompileFinishedForTests();

            CompilationGate.SeedCompileClockForTests(0, NowMs(), finishedViaReload: true);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/FromTheRealCompile.cs", Line = 1, Col = 1, Severity = "error",
                    Code = "CS0618", Message = "obsolete", ConsoleIndex = 11
                }
            };

            Assert.IsInstanceOf<ErrorResponse>(CompilationGate.CheckOrNull(new JObject()),
                "a compile that rebuilt nothing has no diagnostics of its own to make the previous ones stale");
        }

        [Test]
        public void CompileThatRebuilt_AdvancesTheBoundaryToThePostCompileConsole()
        {
            CompilationGate.SeedConsoleSnapshotForTests(maxIndex: 10, firstIndex: 3);
            CompilationGate.NoteCompileStartedForTests();
            CompilationGate.NoteAssemblyCompiledForTests();
            CompilationGate.ConsoleRangeForTests = new CompilationGate.ConsoleIndexRange(true, 3, 20);
            CompilationGate.NoteCompileFinishedForTests();
            CompilationGate.NoteCompileStartedForTests();

            CompilationGate.SeedCompileClockForTests(0, NowMs(), finishedViaReload: true);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/PreviousCompile.cs", Line = 1, Col = 1, Severity = "error",
                    Code = "CS0618", Message = "obsolete", ConsoleIndex = 12
                }
            };

            Assert.IsNull(CompilationGate.CheckOrNull(new JObject()),
                "the next compile starts above the console the finished compile left behind");
        }

        [Test]
        public void AfterReload_WithNoOrdinalBoundary_KeepsDiagnosticsLoggedAfterTheCompileStart()
        {
            // No idle snapshot preceded the compile (the first compile after the editor opened), so
            // the gate falls back to timestamps — and must use the compile's start, not the reload's
            // finish, or every diagnostic of the compile that caused the reload is dropped.
            var now = NowMs();
            CompilationGate.SeedCompileClockForTests(now - 10_000, now - 1000, finishedViaReload: true);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/WarnedBeforeReload.cs", Line = 4, Col = 1, Severity = "error",
                    Code = "CS0168", Message = "declared but never used",
                    HasTimestamp = true, TimeOfDaySeconds = TimeOfDaySeconds(now - 9000)
                }
            };

            var response = (ErrorResponse)CompilationGate.CheckOrNull(new JObject());
            Assert.IsNotNull(response, "the entry was logged 9 s ago, after the compile started 10 s ago");
            var data = JObject.FromObject(response.data);
            Assert.AreEqual("timestamp", data["console_freshness"]["mode"].Value<string>());
        }

        [Test]
        public void CheckOrNull_NoOrdinalPin_FallsBackToTimestamp_AndSaysSo()
        {
            var now = NowMs();
            CompilationGate.SeedCompileClockForTests(now - 5000, now - 1000);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Fresh.cs", Line = 7, Col = 2, Severity = "error",
                    Code = "CS0103", Message = "missing",
                    HasTimestamp = true, TimeOfDaySeconds = TimeOfDaySeconds(now)
                }
            };

            var response = (ErrorResponse)CompilationGate.CheckOrNull(new JObject());
            var data = JObject.FromObject(response.data);
            Assert.AreEqual("timestamp", data["console_freshness"]["mode"].Value<string>(),
                "the result has to say when the gate fell back to timestamps");
            Assert.AreEqual("timestamp", CompilationGate.ConsoleFreshnessMode);
        }

        [Test]
        public void CheckOrNull_OrdinalPin_ReportsTheBaselineOrdinal()
        {
            CompilationGate.SeedConsoleBoundaryForTests(maxIndex: 10, firstIndex: 3);
            CompilationGate.ConsoleRangeForTests = new CompilationGate.ConsoleIndexRange(true, 3, 12);
            CompilationGate.SeedForTests("Library/ScriptAssemblies/Foo.dll", new[]
            {
                MakeCompilerMessage("Assets/Foo.cs", 1, 1, "CS0103: bork", CompilerMessageType.Error)
            });

            var data = JObject.FromObject(((ErrorResponse)CompilationGate.CheckOrNull(new JObject())).data);
            Assert.AreEqual("ordinal", data["console_freshness"]["mode"].Value<string>());
            Assert.AreEqual(10, data["console_freshness"]["baseline_index"].Value<int>());
        }

        [Test]
        public void CheckOrNull_StickyBuildFailure_DoesNotBlock()
        {
            var now = NowMs();
            CompilationGate.SeedCompileClockForTests(now - 5000, now - 1000);
            CompilationGate.SeedConsoleBoundaryForTests(maxIndex: 10, firstIndex: 3);
            CompilationGate.ConsoleRangeForTests = new CompilationGate.ConsoleIndexRange(true, 3, 12);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "", Line = 0, Col = 0, Severity = "error",
                    Message = "Build completed with a result of 'Failed'",
                    Sticky = true, ConsoleIndex = 11
                }
            };

            Assert.IsNull(CompilationGate.CheckOrNull(new JObject()),
                "sticky build/runtime failures are not compiler diagnostics");
        }

        [Test]
        public void CheckOrNull_StickyCompilerBanner_StillBlocks()
        {
            // The banner can be the only console evidence of a failed compile (the pipeline events
            // were missed), so excluding every sticky entry would let exec through on a broken project.
            var now = NowMs();
            CompilationGate.SeedCompileClockForTests(now - 5000, now - 1000);
            CompilationGate.SeedConsoleBoundaryForTests(maxIndex: 10, firstIndex: 3);
            CompilationGate.ConsoleRangeForTests = new CompilationGate.ConsoleIndexRange(true, 3, 12);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "", Line = 0, Col = 0, Severity = "error",
                    Message = "All compiler errors have to be fixed before you can enter play mode!",
                    Sticky = true, StickyCompileFailure = true, ConsoleIndex = 11
                }
            };

            Assert.IsInstanceOf<ErrorResponse>(CompilationGate.CheckOrNull(new JObject()));
        }

        [Test]
        public void CheckOrNull_ConsoleEntryForDeletedFile_DoesNotBlock()
        {
            var now = NowMs();
            CompilationGate.SeedCompileClockForTests(now - 5000, now - 1000);
            CompilationGate.FileExistsForTests = _ => false;
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Deleted.cs", Line = 1, Col = 1, Severity = "error",
                    Code = "CS1002", Message = "; expected",
                    HasTimestamp = true, TimeOfDaySeconds = TimeOfDaySeconds(now)
                }
            };

            Assert.IsNull(CompilationGate.CheckOrNull(new JObject()),
                "an error pointing at a file that no longer exists is stale");
        }

        [Test]
        public void CheckOrNull_Message_NamesFirstThreeErrors_AndEscapeHatch()
        {
            CompilationGate.SeedForTests("Library/ScriptAssemblies/Foo.dll", new[]
            {
                MakeCompilerMessage("Assets/A.cs", 1, 1, "CS0103: a", CompilerMessageType.Error),
                MakeCompilerMessage("Assets/B.cs", 2, 2, "CS0103: b", CompilerMessageType.Error),
                MakeCompilerMessage("Assets/C.cs", 3, 3, "CS0103: c", CompilerMessageType.Error),
                MakeCompilerMessage("Assets/D.cs", 4, 4, "CS0103: d", CompilerMessageType.Error)
            });

            var message = ((ErrorResponse)CompilationGate.CheckOrNull(new JObject())).message;
            StringAssert.Contains("Compilation has 4 errors", message);
            StringAssert.Contains("Assets/A.cs(1,1)", message);
            StringAssert.Contains("Assets/B.cs(2,2)", message);
            StringAssert.Contains("Assets/C.cs(3,3)", message);
            StringAssert.Contains("(+1 more)", message);
            StringAssert.DoesNotContain("Assets/D.cs", message);
            StringAssert.Contains("--skip_compile_check", message);
        }

        [Test]
        public void GetLiveDiagnostics_DropsStaleConsoleEntry_ButKeepsFreshOne()
        {
            var now = NowMs();
            CompilationGate.SeedCompileClockForTests(now - 5000, now - 1000);
            CompilationGate.SeedConsoleBoundaryForTests(maxIndex: 10, firstIndex: 3);
            CompilationGate.ConsoleRangeForTests = new CompilationGate.ConsoleIndexRange(true, 3, 12);
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Old.cs", Line = 1, Col = 1, Severity = "error",
                    Code = "CS1002", Message = "; expected", ConsoleIndex = 8
                },
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Now.cs", Line = 2, Col = 2, Severity = "error",
                    Code = "CS0103", Message = "missing", ConsoleIndex = 12
                }
            };

            var diags = CompilationGate.GetLiveDiagnostics();
            Assert.AreEqual(1, diags.Count);
            Assert.AreEqual("Assets/Now.cs", diags[0].File);
        }

        [Test]
        public void ParseTimestampSeconds_ReadsLogEntriesPrefix()
        {
            Assert.AreEqual(7 * 3600 + 20 * 60 + 9,
                ConsoleLogEntries.ParseTimestampSeconds("[07:20:09] unitycli_caps_marker"));
            Assert.AreEqual(-1, ConsoleLogEntries.ParseTimestampSeconds(null));
            Assert.AreEqual(-1, ConsoleLogEntries.ParseTimestampSeconds("no timestamp"));
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

        // ─── Compile clock (evidence the CLI polls for) ────────────

        [Test]
        public void LastCompileFinishedAt_IsWallClockAndAdvances()
        {
            var before = CompilationGate.LastCompileFinishedAt;
            CompilationGate.NoteCompileFinishedForTests();
            var after = CompilationGate.LastCompileFinishedAt;

            Assert.That(after, Is.GreaterThanOrEqualTo(before),
                "a finished compile has to be visible to the CLI's before/after comparison; " +
                "equal is possible when both stamps land in the same millisecond");
            // Unix ms, not EditorApplication.timeSinceStartup: the CLI compares this against the
            // heartbeat's own timestamps, across a domain reload that resets the static.
            Assert.That(after, Is.EqualTo(System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Within(5000),
                "the compile clock must be wall-clock ms");
        }

        // ─── GetLiveDiagnostics (public API consumed by extensions) ──

        [Test]
        public void GetLiveDiagnostics_MergesPipelineAndConsole_DedupedByLocation()
        {
            CompilationGate.SeedForTests("Library/ScriptAssemblies/Foo.dll", new[]
            {
                MakeCompilerMessage("Assets/Foo.cs", 10, 5,
                    "Assets/Foo.cs(10,5): error CS1031: Type expected", CompilerMessageType.Error)
            });
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>
            {
                // Same location as the pipeline entry → collapses (pipeline added first).
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Foo.cs", Line = 10, Col = 5,
                    Severity = "error", Code = "CS1031", Message = "duplicate"
                },
                // Distinct location → kept.
                new ConsoleLogEntries.CompileDiag
                {
                    File = "Assets/Bar.cs", Line = 3, Col = 1,
                    Severity = "error", Code = "CS0103", Message = "bork"
                }
            };

            var diags = CompilationGate.GetLiveDiagnostics(includeWarnings: false);

            Assert.AreEqual(2, diags.Count, "matching location should collapse to one");
            // Pipeline entry: code lifted from the message header, col kept from the compiler.
            Assert.AreEqual("Assets/Foo.cs", diags[0].File);
            Assert.AreEqual(5, diags[0].Col);
            Assert.AreEqual("CS1031", diags[0].Code);
            Assert.AreEqual("Type expected", diags[0].Message);
            // Console-only entry survives.
            Assert.AreEqual("Assets/Bar.cs", diags[1].File);
        }

        [Test]
        public void GetLiveDiagnostics_ExcludesWarnings_UnlessRequested()
        {
            CompilationGate.SeedForTests("Library/ScriptAssemblies/Foo.dll", new[]
            {
                MakeCompilerMessage("Assets/Foo.cs", 1, 1,
                    "Assets/Foo.cs(1,1): warning CS0168: unused", CompilerMessageType.Warning)
            });
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>();

            Assert.AreEqual(0, CompilationGate.GetLiveDiagnostics(includeWarnings: false).Count,
                "warnings must not surface when includeWarnings=false");

            var withWarnings = CompilationGate.GetLiveDiagnostics(includeWarnings: true);
            Assert.AreEqual(1, withWarnings.Count);
            Assert.AreEqual("warning", withWarnings[0].Severity);
            Assert.AreEqual("CS0168", withWarnings[0].Code);
        }

        [Test]
        public void GetLiveDiagnostics_CleanState_ReturnsEmpty()
        {
            ConsoleLogEntries.TestOverride = _ => new List<ConsoleLogEntries.CompileDiag>();
            Assert.AreEqual(0, CompilationGate.GetLiveDiagnostics().Count);
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
            Assert.IsTrue(diag.Sticky, "the gate needs the sticky marker to exclude build/runtime failures");
            Assert.IsTrue(diag.StickyCompileFailure,
                "the play-mode banner names compiler errors, so it is compile evidence");
        }

        [Test]
        public void TryConvertEntry_StickyBuildFailure_IsNotCompileEvidence()
        {
            Assert.IsTrue(ConsoleLogEntries.TryConvertEntry(
                ConsoleLogEntries.StickyErrorMask, "Build completed with a result of 'Failed'",
                null, 0, false, out var build));
            Assert.IsTrue(build.Sticky);
            Assert.IsFalse(build.StickyCompileFailure, "a failed player build is not a compile failure");

            Assert.IsTrue(ConsoleLogEntries.TryConvertEntry(
                ConsoleLogEntries.ScriptCompileErrorMask, "Assets/Foo.cs(1,1): error CS1002: ; expected",
                null, 0, false, out var compile));
            Assert.IsFalse(compile.Sticky);
            Assert.IsFalse(compile.StickyCompileFailure);
        }

        [Test]
        public void TryConvertEntry_RecordsTheConsoleOrdinal()
        {
            Assert.IsTrue(ConsoleLogEntries.TryConvertEntry(
                ConsoleLogEntries.ScriptCompileErrorMask, "Assets/Foo.cs(1,1): error CS1002: ; expected",
                null, 0, false, out var diag, "[07:20:09] x", consoleIndex: 42));

            Assert.AreEqual(42, diag.ConsoleIndex);
            Assert.IsTrue(diag.HasConsoleIndex);
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

        // ─── Console ordinal + timestamp bindings (real LogEntries) ──

        [Test]
        public void RealConsoleEntries_ExposeOrdinalAndParseableTimestamp()
        {
            // The gate's ordinal rule is only as good as these two bindings: globalLineIndex must
            // exist and grow for new entries, and GetEntryTimestampInternal must return the
            // bracketed [HH:MM:SS] prefix ParseTimestampSeconds expects (the timestamp fallback).
            ConsoleLogEntries.TestOverride = null;
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try
            {
                Debug.LogError("unitycli_r12_ordinal_probe");

                Assert.IsTrue(ConsoleLogEntries.TryGetConsoleIndexRange(out _, out var consoleMax),
                    "LogEntry.globalLineIndex binding is required by the console freshness rule");

                int probes = 0, probeIndex = -1;
                var iterated = UnityCliConnector.Tools.ReadConsole.TryForEachEntry(
                    (mode, message, file, line, timestamp, index) =>
                    {
                        if (message == null || !message.StartsWith("unitycli_r12_ordinal_probe")) return;
                        probes++;
                        probeIndex = index;
                        Assert.GreaterOrEqual(index, 0, "the entry must carry its console ordinal");
                        Assert.GreaterOrEqual(ConsoleLogEntries.ParseTimestampSeconds(timestamp), 0,
                            $"GetEntryTimestampInternal returned '{timestamp}', which is not a [HH:MM:SS] stamp");
                    });

                Assert.IsTrue(iterated, "iterating the in-memory console failed");
                Assert.GreaterOrEqual(probes, 1, "the probe entry must be visible to the reflection seam");
                Assert.AreEqual(consoleMax, probeIndex,
                    "the entry logged last must be the console's highest ordinal; that ordering is what " +
                    "the compile-start pin relies on");
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }
        }

        // ─── Diagnostic file resolution ─────────────────────────

        [Test]
        public void DiagnosticFileExists_ResolvesPackageAssetPaths()
        {
            CompilationGate.FileExistsForTests = null;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CompilationGate).Assembly);
            if (package == null) Assert.Ignore("the connector is not installed as a package in this project");

            Assert.IsTrue(CompilationGate.DiagnosticFileExists(
                    package.assetPath + "/Editor/Core/CompilationGate.cs"),
                "a package diagnostic names a logical asset path that PackageInfo maps to its real source");
            Assert.IsFalse(CompilationGate.DiagnosticFileExists(
                    package.assetPath + "/Editor/Core/Ghost12DoesNotExist.cs"),
                "a package diagnostic for a file that is gone is still stale");
        }

        [Test]
        public void DiagnosticFileExists_DoesNotExemptDirectoriesNamedUnknown()
        {
            CompilationGate.FileExistsForTests = null;
            Assert.IsFalse(CompilationGate.DiagnosticFileExists("Assets/unknown/Old.cs"),
                "the virtual-source exemption is about the file name, not any path containing 'unknown'");
            Assert.IsTrue(CompilationGate.DiagnosticFileExists("unknown"));
            Assert.IsTrue(CompilationGate.DiagnosticFileExists("<unknown>"));
        }

        // ─── Helpers ────────────────────────────────────────────

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        static long TimeOfDaySeconds(long epochMs)
        {
            var local = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToLocalTime();
            return local.Hour * 3600L + local.Minute * 60L + local.Second;
        }

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