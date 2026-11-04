using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityCliConnector
{
	/// One-line: tools that depend on a green editor compile call this first to short-circuit
	/// with the actual diagnostics instead of throwing further downstream.
	[InitializeOnLoad]
	public static class CompilationGate
	{
		// Latest CompilerMessage[] per assembly path, populated live from the compilation pipeline.
		// Cleared on each fresh compile-start so stale entries from removed assemblies don't linger.
		private static readonly Dictionary<string, CompilerMessage[]> ByAssembly = new();

		// Wall-clock ms of the last observed script-compilation start/finish. Statics reset on
		// domain reload, so afterAssemblyReload re-stamps the finish; a failed compile never
		// reloads, which is why compilationFinished has to cover that case. SessionState carries
		// the pair across a reload too: the new domain only sees the finish, and the console
		// diagnostics of the compile that caused the reload have to be judged against its start.
		//
		// Console ordinals (LogEntry.globalLineIndex) are the freshness order the gate uses: they
		// grow while the console keeps its entries, and LogEntries.Clear restarts them at 0. A read
		// taken inside compilationStarted does not agree with the ordinals that same compile gives
		// its own diagnostics, so the boundary is snapshotted while the editor is idle — on every
		// gate read and after every compile that rebuilt an assembly — and compilationStarted only
		// adopts the last snapshot. A compile that rebuilds nothing must not advance it: the console
		// then still holds the previous compile's diagnostics, which remain current, and the CLI can
		// trigger exactly such a no-op compile right after a real one.
		const string SessionKey = "unity-cli/console-freshness";
		static Freshness s_State = new();

		internal sealed class Freshness
		{
			public bool snapshot_known;
			public int snapshot_first = -1;
			public int snapshot_max = -1;
			public bool boundary_known;
			public int boundary_first = -1;
			public int boundary_max = -1;
			public long started_at_ms;
			public long finished_at_ms;
			// A reload proves the compile succeeded, so console entries older than its finish are
			// stale. compilationFinished can also announce a failed compile; if this domain missed
			// the start, its console errors are younger than the finish and must not be discarded.
			public bool finished_via_reload;
			public bool rebuilt;
		}

		public static long LastCompileFinishedAt => s_State.finished_at_ms;

		/// When the last compile the gate observed started. Heartbeat keeps a requested "compiling"
		/// state until this moves, so a compile queued behind an import is not reported as nothing.
		public static long LastCompileStartedAt => s_State.started_at_ms;

		/// "ordinal" while a console ordinal boundary pins the last compile; "timestamp" when the
		/// gate has to fall back to console time-of-day, which cannot separate entries a day apart.
		public static string ConsoleFreshnessMode => s_State.boundary_known ? "ordinal" : "timestamp";

		static CompilationGate()
		{
			Load();

			// CompileProvenance rides the same events: it needs the compile's start, the assemblies
			// that were rebuilt, and the finish, and two subscriptions could drift apart.
			CompilationPipeline.compilationStarted += _ =>
			{
				NoteCompileStarted();
				CompileProvenance.NoteCompileStarted();
			};
			CompilationPipeline.assemblyCompilationFinished += (path, messages) =>
			{
				s_State.rebuilt = true;
				ByAssembly[path] = messages;
				// Persisted here as well as on the finish: a reload must not be able to lose the
				// fact that this compile rebuilt something.
				Save();
				CompileProvenance.NoteAssemblyCompiled(path);
				if (!HasErrors(messages))
					AssemblyStaleness.NoteAssemblyCompiled(path);
			};
			CompilationPipeline.compilationFinished += _ => NoteCompileFinished(false);
			AssemblyReloadEvents.afterAssemblyReload += () => NoteCompileFinished(true);
		}

		private static bool HasErrors(CompilerMessage[] messages)
		{
			if (messages == null) return false;
			foreach (var message in messages)
				if (message.type == CompilerMessageType.Error)
					return true;
			return false;
		}

		private static void NoteCompileStarted()
		{
			s_State.started_at_ms = NowMs();
			s_State.finished_via_reload = false;
			s_State.rebuilt = false;
			s_State.boundary_known = s_State.snapshot_known;
			s_State.boundary_first = s_State.snapshot_first;
			s_State.boundary_max = s_State.snapshot_max;
			ByAssembly.Clear();
			Save();
		}

		private static void NoteCompileFinished(bool afterReload)
		{
			s_State.finished_at_ms = NowMs();
			s_State.finished_via_reload = afterReload;
			if (s_State.rebuilt && TryReadFreshnessRange(out var first, out var last))
			{
				s_State.snapshot_known = true;
				s_State.snapshot_first = first;
				s_State.snapshot_max = last;
			}
			Save();
			// Only a successful compile reloads the domain. A failed one ends here and never
			// reaches afterAssemblyReload, so it must not leave a reload pending for the next one.
			CompileProvenance.NoteCompileFinished(afterReload,
				willReload: !afterReload && s_State.rebuilt && !AnyErrors());
		}

		private static bool AnyErrors()
		{
			foreach (var messages in ByAssembly.Values)
				if (HasErrors(messages))
					return true;
			return false;
		}

		private static void ObserveConsoleRange(int first, int last)
		{
			if (s_State.snapshot_known && s_State.snapshot_first == first && s_State.snapshot_max == last)
				return;
			s_State.snapshot_known = true;
			s_State.snapshot_first = first;
			s_State.snapshot_max = last;
			Save();
		}

		private static void Load()
		{
			var json = SessionState.GetString(SessionKey, null);
			if (string.IsNullOrEmpty(json)) return;
			try
			{
				s_State = Newtonsoft.Json.JsonConvert.DeserializeObject<Freshness>(json) ?? new Freshness();
			}
			catch (Newtonsoft.Json.JsonException)
			{
				s_State = new Freshness();
			}
		}

		private static void Save()
		{
			SessionState.SetString(SessionKey, Newtonsoft.Json.JsonConvert.SerializeObject(s_State));
		}

		/// Returns an ErrorResponse with compile diagnostics if the editor is mid-compile or has
		/// outstanding errors. Returns null when the caller is clear to proceed. Honors the
		/// `skip_compile_check` / `skipCompileCheck` opt-out parameter.
		public static object CheckOrNull(JObject @params)
		{
			var p = new ToolParams(@params ?? new JObject());
			if (p.GetBool("skip_compile_check") || p.GetBool("skipCompileCheck"))
				return null;

			if (EditorApplication.isCompiling)
				return new ErrorResponse(
					"Editor is currently compiling — retry shortly.",
					new
					{
						// The gate runs before dispatch, so the tool was never entered. `started:false` plus
						// the code let the CLI wait out the compile and repeat any command, including exec.
						code = ErrorCodes.Compiling,
						started = false,
						compiling = true,
						console_fallback_available = ConsoleLogEntries.IsAvailable,
						console_fallback_error = ConsoleLogEntries.InitError
					});

			var errors = CollectErrors();
			if (errors.Count == 0) return null;

			return new ErrorResponse(
				GateMessage(errors),
				new
				{
					compile_errors = errors,
					total = errors.Count,
					compile_started_at_ms = s_State.started_at_ms,
					compiled_at_ms = s_State.finished_at_ms,
					console_freshness = ConsoleFreshnessPayload(),
					provenance_line = CompileProvenance.DescribeLastCompile(),
					console_fallback_available = ConsoleLogEntries.IsAvailable,
					console_fallback_error = ConsoleLogEntries.InitError
				});
		}

		/// The escape hatch is part of the message because that is where an agent first meets the
		/// gate, before it has read any help.
		private static string GateMessage(List<GateDiagnostic> errors)
		{
			var sb = new System.Text.StringBuilder();
			sb.Append("Compilation has ").Append(errors.Count).Append(errors.Count == 1 ? " error" : " errors")
				.Append(" from the last compile (").Append(CompileProvenance.DescribeLastCompile()).Append(").");

			var shown = Math.Min(errors.Count, 3);
			for (var i = 0; i < shown; i++)
			{
				var e = errors[i];
				var parsed = ConsoleLogEntries.Parse(e.message, e.file, e.line, true);
				var text = string.IsNullOrEmpty(parsed.Code) ? parsed.Message : parsed.Code + ": " + parsed.Message;
				sb.Append(' ').Append(e.file).Append('(').Append(e.line).Append(',').Append(e.col).Append(") ").Append(text);
				if (!string.IsNullOrEmpty(e.assembly)) sb.Append(" [").Append(e.assembly).Append(']');
				sb.Append(';');
			}
			if (errors.Count > shown) sb.Append(" (+").Append(errors.Count - shown).Append(" more);");

			sb.Append(" Fix them, or pass --skip_compile_check / skip_compile_check=true to run anyway.");
			return sb.ToString();
		}

		/// Reports which rule filtered the console, so a caller that sees an empty or surprising set
		/// of console diagnostics knows whether it was the ordinal pin or the timestamp fallback.
		private static object ConsoleFreshnessPayload()
		{
			if (s_State.boundary_known)
				return new { mode = ConsoleFreshnessMode, baseline_index = s_State.boundary_max };
			return new
			{
				mode = ConsoleFreshnessMode,
				note = "no compile-start console ordinal recorded; console entries were filtered by their timestamp instead"
			};
		}

		/// Live, correctly-invalidated compile diagnostics — the same set the gate
		/// blocks on, exposed for tools that want to *report* errors (e.g. the
		/// extensions `compile_errors` tool) rather than gate on them. Merges the
		/// CompilationPipeline cache (this session) with the in-memory console
		/// fallback, deduped by file:line:col.
		///
		/// Prefer this over scraping Editor.log: the log is append-only, so a fixed
		/// error's lines linger in the file long after a clean recompile.
		///
		/// Diagnostics the agent can act on get a `hint` (see <see cref="CompileHints"/>):
		/// the closest real members/types, bounded by <paramref name="hintBudgetMs"/> for the
		/// whole call, not per diagnostic. A negative budget means "no hints at all".
		public static IReadOnlyList<ConsoleLogEntries.CompileDiag> GetLiveDiagnostics(bool includeWarnings = false)
			=> GetLiveDiagnostics(includeWarnings, CompileHints.DefaultBudgetMs);

		public static IReadOnlyList<ConsoleLogEntries.CompileDiag> GetLiveDiagnostics(bool includeWarnings, int hintBudgetMs)
		{
			var result = new List<ConsoleLogEntries.CompileDiag>();
			var seen = new HashSet<string>();

			// Primary: live CompilationPipeline cache (this editor session).
			foreach (var kv in ByAssembly)
			{
				if (kv.Value == null) continue;
				foreach (var m in kv.Value)
				{
					var isError = m.type == CompilerMessageType.Error;
					var isWarning = m.type == CompilerMessageType.Warning;
					if (!isError && !(includeWarnings && isWarning)) continue;
					if (!seen.Add(DedupKey(m.file, m.line, m.column))) continue;

					// Reuse the header parser only to lift the CSxxxx code / clean message
					// out of m.message; the compiler's own file/line/column stay authoritative.
					var parsed = ConsoleLogEntries.Parse(m.message, m.file, m.line, isError);
					result.Add(new ConsoleLogEntries.CompileDiag
					{
						File = m.file,
						Line = m.line,
						Col = m.column,
						Severity = isError ? "error" : "warning",
						Code = parsed.Code,
						Message = parsed.Message
					});
				}
			}

			// Fallback: in-memory editor console via reflection. The console is not cleared by a
			// successful recompile, so only entries from the current compile (or newer than its
			// finish when the start predates this domain) may contribute.
			foreach (var d in CurrentConsoleDiagnostics(includeWarnings))
			{
				if (!seen.Add(DedupKey(d.File, d.Line, d.Col))) continue;
				result.Add(d);
			}

			AttachHints(result, hintBudgetMs);
			return result;
		}

		/// Hints for the codes an agent can act on: a member or type it could not find, an API that
		/// is obsolete. One budget for the whole call; the lookups behind a hint are cached per
		/// domain, so the repeat verifies of a fix loop pay nothing.
		private static void AttachHints(List<ConsoleLogEntries.CompileDiag> diags, int hintBudgetMs)
		{
			if (hintBudgetMs < 0) return;
			var budget = new Deadline(hintBudgetMs);
			for (var i = 0; i < diags.Count; i++)
			{
				if (!string.IsNullOrEmpty(diags[i].Hint)) continue;
				var hint = CompileHints.ForDiagnostic(diags[i].Code, diags[i].Message, budget);
				if (string.IsNullOrEmpty(hint)) continue;
				var diag = diags[i];
				diag.Hint = hint;
				diags[i] = diag;
			}
		}

		private static List<GateDiagnostic> CollectErrors()
		{
			var result = new List<GateDiagnostic>();
			var seen = new HashSet<string>();

			// Primary: live CompilationPipeline cache (this editor session).
			foreach (var kv in ByAssembly)
			{
				if (kv.Value == null) continue;
				foreach (var m in kv.Value)
				{
					if (m.type != CompilerMessageType.Error) continue;
					if (!seen.Add(DedupKey(m.file, m.line, m.column))) continue;
					result.Add(new GateDiagnostic
					{
						file = m.file,
						line = m.line,
						col = m.column,
						severity = "error",
						message = m.message,
						assembly = Path.GetFileNameWithoutExtension(kv.Key),
						source = "pipeline"
					});
				}
			}

			// Fallback: in-memory editor console via reflection. Per-process (safe across
			// multiple editor instances) and catches errors the pipeline cache missed —
			// most commonly diagnostics from before our [InitializeOnLoad] subscribed.
			// Degrades silently if reflection is unavailable on this Unity version.
			foreach (var d in CurrentConsoleDiagnostics(false))
			{
				if (!seen.Add(DedupKey(d.File, d.Line, d.Col))) continue;
				result.Add(new GateDiagnostic
				{
					file = d.File,
					line = d.Line,
					col = d.Col,
					severity = d.Severity,
					message = string.IsNullOrEmpty(d.Code) ? d.Message : d.Code + ": " + d.Message,
					assembly = null,
					source = "console"
				});
			}

			return result;
		}

		private static List<ConsoleLogEntries.CompileDiag> CurrentConsoleDiagnostics(bool includeWarnings)
		{
			var result = new List<ConsoleLogEntries.CompileDiag>();
			if (!ConsoleLogEntries.TryGetCompileDiagnostics(includeWarnings, out var consoleDiags))
				return result;

			var now = NowMs();
			var range = ConsoleRange();
			foreach (var d in consoleDiags)
			{
				if (!IsCurrentConsoleDiagnostic(d, now, s_State, range, DiagnosticFileExists))
					continue;
				result.Add(d);
			}
			return result;
		}

		// The console survives successful recompiles, so an old ScriptCompileError (or a sticky
		// build/runtime failure) must not keep blocking after the live pipeline is clean.
		internal static bool IsCurrentConsoleDiagnostic(ConsoleLogEntries.CompileDiag d, long nowMs,
			Freshness state, ConsoleIndexRange range, Func<string, bool> fileExists)
		{
			// A sticky build/runtime failure is not a compiler diagnostic, but the sticky "compiler
			// errors" banner is — and it can be the only console evidence of a failed compile.
			if (d.Sticky && !d.StickyCompileFailure) return false;
			if (fileExists != null && !fileExists(d.File)) return false;

			if (state.boundary_known)
			{
				if (range.Known && ConsoleRestartedSince(state, range)) return true;
				if (d.HasConsoleIndex) return d.ConsoleIndex > state.boundary_max;
			}

			// No ordinal boundary (no idle snapshot preceded this compile, or this Unity version
			// does not expose globalLineIndex): fall back to the console timestamp.
			return IsCurrentByTimestamp(d, nowMs, state.started_at_ms, state.finished_at_ms,
				state.finished_via_reload);
		}

		/// Unity's console Clear restarts globalLineIndex at 0, so an ordinal below what the boundary
		/// saw — the oldest entry's, or the newest one's when the console now holds fewer entries than
		/// it did — means the buffer was cleared after the compile started. Everything left in it was
		/// therefore logged after the compile started, whatever its restarted ordinal says.
		private static bool ConsoleRestartedSince(Freshness state, ConsoleIndexRange range)
			=> range.FirstIndex < state.boundary_first || range.MaxIndex < state.boundary_max;

		private static bool IsCurrentByTimestamp(ConsoleLogEntries.CompileDiag d, long nowMs,
			long compileStartedMs, long compileFinishedMs, bool finishedViaReload)
		{
			// Prefer the start: errors are logged during the compile, so a start-clock threshold
			// keeps this compile's diagnostics and drops everything older. A domain that joined
			// after a successful compile only has the reload's finish clock.
			var threshold = compileStartedMs > 0
				? compileStartedMs
				: (finishedViaReload ? compileFinishedMs : 0);
			if (threshold <= 0) return true;
			if (!d.HasTimestamp) return false;

			var nowSod = TimeOfDaySeconds(nowMs);
			var entryAge = WrapDay(nowSod - d.TimeOfDaySeconds);
			var thresholdAge = WrapDay(nowSod - TimeOfDaySeconds(threshold));
			// The console timestamp has one-second resolution, so allow one second of slack.
			return entryAge <= thresholdAge + 1;
		}

		private static long TimeOfDaySeconds(long epochMs)
		{
			var local = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToLocalTime();
			return local.Hour * 3600L + local.Minute * 60L + local.Second;
		}

		private static long WrapDay(long seconds)
		{
			seconds %= 86400;
			return seconds < 0 ? seconds + 86400 : seconds;
		}

		internal static bool DiagnosticFileExists(string file)
		{
			if (FileExistsForTests != null) return FileExistsForTests(file);
			if (string.IsNullOrEmpty(file)) return true;

			var path = file.Replace('\\', '/');
			if (path.StartsWith("./", StringComparison.Ordinal)) path = path.Substring(2);
			// Compiler/Burst diagnostics can name generated or virtual sources with no file on disk.
			if (IsVirtualSourceName(path)) return true;

			if (Path.IsPathRooted(path)) return File.Exists(path);
			// `Packages/<name>/...` is a logical asset path, not a path under the project's Packages
			// directory: registry packages live in Library/PackageCache/<name>@<hash> and `file:`
			// packages wherever the manifest points.
			if (path.StartsWith("Packages/", StringComparison.Ordinal)) return PackagePathExists(path);
			if (!path.StartsWith("Assets/", StringComparison.Ordinal) &&
			    !path.StartsWith("Library/", StringComparison.Ordinal))
				return true;

			var projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
			return string.IsNullOrEmpty(projectRoot) || File.Exists(Path.Combine(projectRoot, path));
		}

		private static bool PackagePathExists(string assetPath)
		{
			var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
			if (package == null) return false;

			var relative = assetPath.Length > package.assetPath.Length
				? assetPath.Substring(package.assetPath.Length).TrimStart('/')
				: "";
			return relative.Length == 0 || File.Exists(Path.Combine(package.resolvedPath, relative));
		}

		// The file name is the only part that decides this: a real directory called e.g. `unknown`
		// must keep its existence check, while a synthesized source ("unknown", "<unknown>") is not
		// a file that can be missing.
		private static bool IsVirtualSourceName(string path)
		{
			var name = Path.GetFileName(path).Trim();
			if (name.Length == 0) return false;
			if (name.Length > 1 && name[0] == '<' && name[name.Length - 1] == '>') return true;
			return string.Equals(name, "unknown", StringComparison.OrdinalIgnoreCase);
		}

		private static string DedupKey(string file, int line, int col)
		{
			var norm = string.IsNullOrEmpty(file) ? "" : file.Replace('\\', '/');
			return norm + ":" + line + ":" + col;
		}

		// A console read must never break a compile event or a tool response: reflection into
		// LogEntries throws TargetInvocationException when the console is mid-update.
		private static bool TryReadConsoleRange(out int first, out int last)
		{
			try
			{
				return ConsoleLogEntries.TryGetConsoleIndexRange(out first, out last);
			}
			catch (System.Reflection.TargetInvocationException)
			{
				first = -1;
				last = -1;
				return false;
			}
		}

		private static ConsoleIndexRange ConsoleRange()
		{
			if (!TryReadFreshnessRange(out var first, out var last)) return ConsoleIndexRange.Unknown;
			if (!EditorApplication.isCompiling) ObserveConsoleRange(first, last);
			return new ConsoleIndexRange(true, first, last);
		}

		private static bool TryReadFreshnessRange(out int first, out int last)
		{
			if (ConsoleRangeForTests != null)
			{
				first = ConsoleRangeForTests.Value.FirstIndex;
				last = ConsoleRangeForTests.Value.MaxIndex;
				return ConsoleRangeForTests.Value.Known;
			}
			return TryReadConsoleRange(out first, out last);
		}

		internal readonly struct ConsoleIndexRange
		{
			public static readonly ConsoleIndexRange Unknown = default;

			public readonly bool Known;
			public readonly int FirstIndex;
			public readonly int MaxIndex;

			public ConsoleIndexRange(bool known, int firstIndex, int maxIndex)
			{
				Known = known;
				FirstIndex = firstIndex;
				MaxIndex = maxIndex;
			}
		}

		private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		internal sealed class GateDiagnostic
		{
			public string file;
			public int line;
			public int col;
			public string severity;
			public string message;
			public string assembly;
			public string source;
		}

		// ─── Testing hooks ──────────────────────────────────────
		// Internal seam for unit tests. Production code must not call these.

		internal static Func<string, bool> FileExistsForTests;

		internal static ConsoleIndexRange? ConsoleRangeForTests;

		internal static void SeedConsoleBoundaryForTests(int maxIndex, int firstIndex)
		{
			s_State.boundary_known = true;
			s_State.boundary_max = maxIndex;
			s_State.boundary_first = firstIndex;
		}

		internal static void SeedConsoleSnapshotForTests(int maxIndex, int firstIndex)
		{
			s_State.snapshot_known = true;
			s_State.snapshot_max = maxIndex;
			s_State.snapshot_first = firstIndex;
		}

		internal static void NoteCompileStartedForTests() => NoteCompileStarted();

		internal static void NoteAssemblyCompiledForTests() => s_State.rebuilt = true;

		internal static void SeedForTests(string assemblyPath, CompilerMessage[] messages)
		{
			ByAssembly[assemblyPath] = messages;
		}

		internal static void SeedCompileClockForTests(long startedAtMs, long finishedAtMs, bool finishedViaReload = false)
		{
			s_State.started_at_ms = startedAtMs;
			s_State.finished_at_ms = finishedAtMs;
			s_State.finished_via_reload = finishedViaReload;
		}

		// Tests run in the live editor, so they must not leave the gate without the boundary and
		// compile clocks the editor already had: the first reset keeps what is persisted and
		// RestoreLiveStateForTests puts it back.
		static string s_PersistedBeforeTests;
		static bool s_PersistedCaptured;

		internal static void ResetForTests()
		{
			if (!s_PersistedCaptured)
			{
				s_PersistedBeforeTests = SessionState.GetString(SessionKey, null);
				s_PersistedCaptured = true;
			}
			ByAssembly.Clear();
			s_State = new Freshness();
			ConsoleRangeForTests = null;
			FileExistsForTests = null;
			SessionState.EraseString(SessionKey);
		}

		internal static void RestoreLiveStateForTests()
		{
			if (!s_PersistedCaptured) return;
			if (s_PersistedBeforeTests == null) SessionState.EraseString(SessionKey);
			else SessionState.SetString(SessionKey, s_PersistedBeforeTests);
			s_PersistedCaptured = false;
			Load();
		}

		internal static void NoteCompileFinishedForTests(bool afterReload = false) => NoteCompileFinished(afterReload);
	}
}
