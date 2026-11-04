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
		// reloads, which is why compilationFinished has to cover that case.
		private static long s_CompileStartedAt;
		private static long s_CompileFinishedAt;
		// A reload proves the compile succeeded, so console entries older than its finish are stale.
		// compilationFinished can also announce a failed compile; if this domain missed the start,
		// its console errors are younger than the finish and must not be discarded.
		private static bool s_CompileFinishedViaReload;

		public static long LastCompileFinishedAt => s_CompileFinishedAt;

		static CompilationGate()
		{
			// CompileProvenance rides the same events: it needs the compile's start, the assemblies
			// that were rebuilt, and the finish, and two subscriptions could drift apart.
			CompilationPipeline.compilationStarted += _ =>
			{
				s_CompileStartedAt = NowMs();
				s_CompileFinishedViaReload = false;
				ByAssembly.Clear();
				CompileProvenance.NoteCompileStarted();
			};
			CompilationPipeline.assemblyCompilationFinished += (path, messages) =>
			{
				ByAssembly[path] = messages;
				CompileProvenance.NoteAssemblyCompiled(path);
			};
			CompilationPipeline.compilationFinished += _ => NoteCompileFinished(false);
			AssemblyReloadEvents.afterAssemblyReload += () => NoteCompileFinished(true);
		}

		private static void NoteCompileFinished(bool afterReload)
		{
			s_CompileFinishedAt = NowMs();
			s_CompileFinishedViaReload = afterReload;
			CompileProvenance.NoteCompileFinished(afterReload);
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
					compile_started_at_ms = s_CompileStartedAt,
					compiled_at_ms = s_CompileFinishedAt,
					provenance_line = CompileProvenance.DescribeLastCompile(),
					console_fallback_available = ConsoleLogEntries.IsAvailable,
					console_fallback_error = ConsoleLogEntries.InitError
				});
		}

		/// Names the first few errors and says how old the compile is, so the caller can act
		/// without a second call. The escape hatch is part of the message because that is where
		/// an agent first meets the gate.
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
			foreach (var d in consoleDiags)
			{
				if (!IsCurrentConsoleDiagnostic(d, now, s_CompileStartedAt, s_CompileFinishedAt,
						s_CompileFinishedViaReload, DiagnosticFileExists))
					continue;
				result.Add(d);
			}
			return result;
		}

		/// A console entry is current only when it belongs to the compile this gate is judging.
		/// The console survives successful recompiles, so an old ScriptCompileError (or a sticky
		/// build/runtime failure) must not keep blocking after the live pipeline is clean.
		internal static bool IsCurrentConsoleDiagnostic(ConsoleLogEntries.CompileDiag d, long nowMs,
			long compileStartedMs, long compileFinishedMs, bool finishedViaReload, Func<string, bool> fileExists)
		{
			if (d.Sticky) return false;
			if (fileExists != null && !fileExists(d.File)) return false;

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

		private static bool DiagnosticFileExists(string file)
		{
			if (FileExistsForTests != null) return FileExistsForTests(file);
			if (string.IsNullOrEmpty(file)) return true;

			var path = file.Replace('\\', '/');
			if (path.StartsWith("./", StringComparison.Ordinal)) path = path.Substring(2);
			// Compiler/Burst diagnostics can name generated or virtual sources with no file on disk.
			if (path.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0) return true;

			if (Path.IsPathRooted(path)) return File.Exists(path);
			if (!path.StartsWith("Assets/", StringComparison.Ordinal) &&
			    !path.StartsWith("Packages/", StringComparison.Ordinal) &&
			    !path.StartsWith("Library/", StringComparison.Ordinal))
				return true;

			var projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
			return string.IsNullOrEmpty(projectRoot) || File.Exists(Path.Combine(projectRoot, path));
		}

		private static string DedupKey(string file, int line, int col)
		{
			var norm = string.IsNullOrEmpty(file) ? "" : file.Replace('\\', '/');
			return norm + ":" + line + ":" + col;
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

		internal static void SeedForTests(string assemblyPath, CompilerMessage[] messages)
		{
			ByAssembly[assemblyPath] = messages;
		}

		internal static void SeedCompileClockForTests(long startedAtMs, long finishedAtMs)
		{
			s_CompileStartedAt = startedAtMs;
			s_CompileFinishedAt = finishedAtMs;
		}

		internal static void ResetForTests()
		{
			ByAssembly.Clear();
			s_CompileStartedAt = 0;
			s_CompileFinishedAt = 0;
			s_CompileFinishedViaReload = false;
			FileExistsForTests = null;
		}

		internal static void NoteCompileFinishedForTests() => NoteCompileFinished(false);
	}
}
