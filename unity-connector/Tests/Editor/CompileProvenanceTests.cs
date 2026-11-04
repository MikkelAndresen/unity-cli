using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector;

namespace UnityCliConnector.Tests
{
    /// Compile provenance is what lets a caller tell a clean compile from a stale one, so the two
    /// things it promises are asserted here: the record survives the domain reload a compile ends
    /// with, and the source scan names the files written after that compile.
    [TestFixture]
    public class CompileProvenanceTests
    {
        string _tmp;
        string _sessionBefore;

        [SetUp]
        public void SetUp()
        {
            _sessionBefore = CompileProvenance.DumpSessionForTests();
            CompileProvenance.ResetForTests();
            _tmp = Path.Combine(Path.GetTempPath(), "unity-cli-provenance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmp);
        }

        [TearDown]
        public void TearDown()
        {
            CompileProvenance.ResetForTests();
            CompileProvenance.RestoreSessionForTests(_sessionBefore);
            // Leave the live record in memory as well as in SessionState: the tests cleared it, and
            // the next real compile runs in this domain, before any reload can load it back.
            CompileProvenance.ReloadFromSessionForTests();
            try
            {
                if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
            }
            catch (Exception)
            {
            }
        }

        // ─── The compile record ───────────────────────────────────────────

        [Test]
        public void NoteCompileFinished_CapturesStartDurationAndRebuiltAssemblies()
        {
            CompileProvenance.NoteCompileStarted();
            CompileProvenance.NoteAssemblyCompiled("/p/Library/ScriptAssemblies/Game.dll");
            CompileProvenance.NoteAssemblyCompiled("/p/Library/ScriptAssemblies/Game.Tests.Editor.dll");
            CompileProvenance.NoteAssemblyCompiled("/p/Library/ScriptAssemblies/Game.dll");
            CompileProvenance.NoteCompileFinished(false);

            var record = CompileProvenance.SnapshotForTests();
            CollectionAssert.AreEqual(new[] { "Game", "Game.Tests.Editor" }, record.rebuilt,
                "the assemblies the compile produced are what the caller checks its edit against");
            Assert.That(record.started_at_ms, Is.Positive);
            Assert.That(record.finished_at_ms, Is.GreaterThanOrEqualTo(record.started_at_ms));
        }

        [Test]
        public void NoteCompileFinished_AdvancesPreviousToTheCompileBefore_WhichIsWhatChangedCounts()
        {
            CompileProvenance.NoteCompileStarted();
            CompileProvenance.NoteCompileFinished(false);
            var first = CompileProvenance.SnapshotForTests().finished_at_ms;

            CompileProvenance.NoteCompileStarted();
            CompileProvenance.NoteCompileFinished(false);
            var second = CompileProvenance.SnapshotForTests();

            Assert.That(second.previous_finished_at_ms, Is.EqualTo(first),
                "changed_since_previous_compile is counted from the previous compile's finish");
            Assert.That(second.finished_at_ms, Is.GreaterThanOrEqualTo(first));
        }

        [Test]
        public void NoteCompileFinished_AfterReloadRestampsTheSameCompileAndKeepsItsRecord()
        {
            CompileProvenance.NoteCompileStarted();
            CompileProvenance.NoteAssemblyCompiled("/p/Library/ScriptAssemblies/Game.dll");
            CompileProvenance.NoteCompileFinished(false);
            var finished = CompileProvenance.SnapshotForTests().finished_at_ms;
            var previous = CompileProvenance.SnapshotForTests().previous_finished_at_ms;

            // The reload wipes the statics the events fed; SessionState is what carries the record
            // across, and afterAssemblyReload only moves the finish time forward.
            CompileProvenance.ResetForTests();
            CompileProvenance.ReloadFromSessionForTests();
            CompileProvenance.NoteCompileFinished(true);

            var record = CompileProvenance.SnapshotForTests();
            Assert.That(record.finished_at_ms, Is.GreaterThanOrEqualTo(finished),
                "the finish time moves to the end of the reload, which is when the editor answers again");
            Assert.That(record.previous_finished_at_ms, Is.EqualTo(previous),
                "a restamp must not consume the record's own finish as the previous compile");
            CollectionAssert.AreEqual(new[] { "Game" }, record.rebuilt,
                "the assemblies survive the reload they were compiled into");
        }

        [Test]
        public void Payload_CarriesEveryKeyTheGoCLIUnmarshals()
        {
            // cmd/provenance.go reads these by name; a rename there or here drops a field silently.
            var payload = JObject.FromObject(CompileProvenance.BuildPayloadFrom(
                new CompileProvenance.Record
                {
                    finished_at_ms = 1_793_782_952_102L,
                    started_at_ms = 1_793_782_950_000L,
                    previous_finished_at_ms = 1_793_782_900_000L,
                    rebuilt = new List<string> { "Game" },
                },
                new CompileProvenance.SourceScan
                {
                    have_previous = true,
                    changed_since_previous = 2,
                    stale_count = 0,
                    complete = true,
                    roots = new List<string> { "Assets" },
                },
                1_793_782_955_102L));

            foreach (var key in new[]
                     {
                         "compiled_at_ms", "compiled_at", "compile_started_at_ms", "duration_ms",
                         "age_seconds", "rebuilt_assemblies", "rebuilt_truncated",
                         "changed_since_previous_compile", "stale_count", "stale_files",
                         "stale_truncated", "scanned_roots", "scanned_files", "scan_ms",
                         "scan_complete", "scan_note", "provenance_line", "stale_line",
                     })
            {
                Assert.IsTrue(payload.ContainsKey(key), $"provenance payload lost the '{key}' key the CLI reads");
            }

            Assert.AreEqual(2_102L, payload["duration_ms"].Value<long>());
            Assert.AreEqual(3.0, payload["age_seconds"].Value<double>());
            Assert.AreEqual(2, payload["changed_since_previous_compile"].Value<int>());
        }

        // ─── The lines the CLI prints ─────────────────────────────────────

        [Test]
        public void ProvenanceLine_StatesWhenTookChangedAndWhatWasRebuilt()
        {
            const long finished = 1_793_782_952_102L;
            var record = new CompileProvenance.Record
            {
                finished_at_ms = finished,
                started_at_ms = finished - 2_200,
                previous_finished_at_ms = finished - 60_000,
                rebuilt = new List<string> { "Game", "Game.Tests.Editor" },
            };
            var scan = new CompileProvenance.SourceScan
            {
                have_previous = true,
                changed_since_previous = 1,
                complete = true,
            };

            var clock = DateTimeOffset.FromUnixTimeMilliseconds(finished).ToLocalTime().ToString("HH:mm:ss");
            Assert.AreEqual(
                $"compiled {clock} (3 s ago, took 2.2 s) · 1 source file changed since previous compile · " +
                "rebuilt: Game, Game.Tests.Editor",
                CompileProvenance.FormatProvenanceLine(record, scan, finished + 3_000));
        }

        [Test]
        public void ProvenanceLine_LeavesOutWhatIsUnknown_InsteadOfGuessing()
        {
            Assert.AreEqual(
                "compiled: unknown — no script compilation has finished since this editor session started",
                CompileProvenance.FormatProvenanceLine(new CompileProvenance.Record(), null, 1_793_782_952_102L),
                "a connector that has seen no compile must not imply one ran");

            // No scan: no "changed" clause. No recorded assemblies: no "rebuilt" clause.
            var line = CompileProvenance.FormatProvenanceLine(
                new CompileProvenance.Record { finished_at_ms = 1_793_782_952_102L }, null, 1_793_782_952_602L);
            StringAssert.StartsWith("compiled ", line);
            StringAssert.Contains("(just now)", line);
            Assert.IsFalse(line.Contains("changed"), "there is no previous compile to count changes from");
            Assert.IsFalse(line.Contains("rebuilt"), "no assemblies were recorded");
        }

        [Test]
        public void ProvenanceLine_WithNoChangesSaysSo_RatherThanOmittingTheClause()
        {
            const long finished = 1_793_782_952_102L;
            var line = CompileProvenance.FormatProvenanceLine(
                new CompileProvenance.Record { finished_at_ms = finished, previous_finished_at_ms = finished - 1000 },
                new CompileProvenance.SourceScan { have_previous = true, changed_since_previous = 0, complete = true },
                finished + 1000);

            StringAssert.Contains("0 source files changed since previous compile", line,
                "\"nothing changed\" is a different answer from \"not counted\"");
        }

        [Test]
        public void StaleLine_NamesTheFilesAndCountsTheOnesItTruncated()
        {
            var scan = new CompileProvenance.SourceScan { complete = true };
            for (var i = 0; i < CompileProvenance.MaxStaleNames; i++) scan.stale_files.Add($"Assets/F{i}.cs");
            scan.stale_count = CompileProvenance.MaxStaleNames + 2;

            var line = CompileProvenance.FormatStaleLine(scan);
            StringAssert.StartsWith("stale: Assets/F0.cs", line);
            StringAssert.Contains("(+2 more)", line);
            StringAssert.Contains("modified after this compile — run verify again", line);
        }

        [Test]
        public void StaleLine_AbsentWhenNothingIsStale_OrNothingWasScanned()
        {
            Assert.IsNull(CompileProvenance.FormatStaleLine(new CompileProvenance.SourceScan()));
            Assert.IsNull(CompileProvenance.FormatStaleLine(null));
        }

        [Test]
        public void Payload_ReportedStaleScanAlsoCarriesTheNoteTheBudgetMissed()
        {
            var payload = JObject.FromObject(CompileProvenance.BuildPayloadFrom(
                new CompileProvenance.Record { finished_at_ms = 1_793_782_952_102L },
                new CompileProvenance.SourceScan { complete = false, scan_ms = 200, scanned_files = 512 },
                1_793_782_955_102L));

            StringAssert.Contains("stopped after 200 ms", payload["scan_note"].Value<string>());
            Assert.IsFalse(payload["scan_complete"].Value<bool>());
        }

        // ─── The source scan ──────────────────────────────────────────────

        [Test]
        public void ScanRoots_CountsChangesSinceThePreviousCompile_AndListsSourcesNewerThanIt()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var previous = now - 120_000;
            var finished = now - 60_000;

            Write("Old.cs", now - 300_000);
            Write("Changed.cs", now - 90_000);
            Write("Stale.cs", now - 5_000);
            Write("Stale.asmdef", now - 5_000);
            Write("NotSource.cs.meta", now - 5_000);
            Directory.CreateDirectory(Path.Combine(_tmp, "Sub"));
            Write(Path.Combine("Sub", "Nested.cs"), now - 5_000);

            var scan = CompileProvenance.ScanRoots(new[] { _tmp }, _tmp, previous, finished, 5_000);

            Assert.IsTrue(scan.complete);
            Assert.AreEqual(3, scan.changed_since_previous,
                ".cs files written after the previous compile, counting neither .meta nor older files");
            Assert.AreEqual(3, scan.stale_count, ".cs, .asmdef and nested sources written after the compile");
            CollectionAssert.AreEquivalent(
                new[] { "Stale.cs", "Stale.asmdef", Path.Combine("Sub", "Nested.cs") }.Select(p => p.Replace('\\', '/')).ToArray(),
                scan.stale_files.Select(p => p.Replace('\\', '/')).ToArray(),
                "stale names come back relative to the project so they can be opened as-is");
        }

        [Test]
        public void ScanRoots_WithoutAPreviousCompile_LeavesTheChangedCountUnanswered()
        {
            Write("Any.cs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var scan = CompileProvenance.ScanRoots(new[] { _tmp }, _tmp, 0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000, 5_000);

            Assert.IsFalse(scan.have_previous);
            Assert.AreEqual(0, scan.changed_since_previous,
                "counting every file in the project as changed would be noise, not a count");

            var payload = JObject.FromObject(CompileProvenance.BuildPayloadFrom(
                new CompileProvenance.Record { finished_at_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                scan, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Assert.IsNull(payload["changed_since_previous_compile"].Value<int?>(),
                "the CLI tells a missing answer apart from a zero count");
        }

        [Test]
        public void ScanRoots_StopsAtItsBudget_AndSaysTheCountsAreIncomplete()
        {
            for (var i = 0; i < 2_000; i++) Write($"File{i}.cs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var scan = CompileProvenance.ScanRoots(new[] { _tmp }, _tmp, 0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000, 0);

            Assert.IsFalse(scan.complete, "a scan over budget must report itself incomplete, not clean");
            Assert.That(scan.scanned_files, Is.LessThan(2_000), "it stopped early rather than running to the end");
            Assert.That(scan.scan_ms, Is.LessThanOrEqualTo(500),
                "the scan runs on the editor's main thread: it may not run long on a large project");
        }

        [Test]
        public void ScanRoots_CapsTheStaleNamesItLists_ButKeepsTheCount()
        {
            for (var i = 0; i < CompileProvenance.MaxStaleNames + 3; i++)
                Write($"S{i}.cs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var scan = CompileProvenance.ScanRoots(new[] { _tmp }, _tmp, 0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000, 5_000);

            Assert.AreEqual(CompileProvenance.MaxStaleNames + 3, scan.stale_count);
            Assert.AreEqual(CompileProvenance.MaxStaleNames, scan.stale_files.Count);
            Assert.IsTrue(scan.stale_truncated);
        }

        [Test]
        public void ScanRoots_SkipsHiddenDirectories()
        {
            Directory.CreateDirectory(Path.Combine(_tmp, ".git"));
            Write(Path.Combine(".git", "hooked.cs"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Write("Real.cs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var scan = CompileProvenance.ScanRoots(new[] { _tmp }, _tmp, 0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000, 5_000);

            Assert.AreEqual(1, scan.stale_count, "version-control and tool folders are not sources to report");
        }

        void Write(string relative, long wroteUtcMs)
        {
            var path = Path.Combine(_tmp, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "// written by CompileProvenanceTests\n");
            File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(wroteUtcMs).UtcDateTime);
        }
    }
}
