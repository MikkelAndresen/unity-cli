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

		static CompilationGate()
		{
			CompilationPipeline.compilationStarted += _ => ByAssembly.Clear();
			CompilationPipeline.assemblyCompilationFinished += (path, messages) => ByAssembly[path] = messages;
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
				$"Compilation has {errors.Count} error(s). Fix them or pass skip_compile_check=true.",
				new
				{
					compile_errors = errors,
					total = errors.Count,
					console_fallback_available = ConsoleLogEntries.IsAvailable,
					console_fallback_error = ConsoleLogEntries.InitError
				});
		}

		/// Live, correctly-invalidated compile diagnostics — the same set the gate
		/// blocks on, exposed for tools that want to *report* errors (e.g. the
		/// extensions `compile_errors` tool) rather than gate on them. Merges the
		/// CompilationPipeline cache (this session) with the in-memory console
		/// fallback, deduped by file:line:col.
		///
		/// Prefer this over scraping Editor.log: the log is append-only, so a fixed
		/// error's lines linger in the file long after a clean recompile.
		public static IReadOnlyList<ConsoleLogEntries.CompileDiag> GetLiveDiagnostics(bool includeWarnings = false)
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

			// Fallback: in-memory editor console via reflection (cleared on recompile).
			if (ConsoleLogEntries.TryGetCompileDiagnostics(includeWarnings, out var consoleDiags))
				foreach (var d in consoleDiags)
					if (seen.Add(DedupKey(d.File, d.Line, d.Col)))
						result.Add(d);

			return result;
		}

		private static List<object> CollectErrors()
		{
			var result = new List<object>();
			var seen = new HashSet<string>();

			// Primary: live CompilationPipeline cache (this editor session).
			foreach (var kv in ByAssembly)
			{
				if (kv.Value == null) continue;
				foreach (var m in kv.Value)
				{
					if (m.type != CompilerMessageType.Error) continue;
					if (!seen.Add(DedupKey(m.file, m.line, m.column))) continue;
					result.Add(new
					{
						m.file,
						m.line,
						col = m.column,
						severity = "error",
						m.message,
						assembly = Path.GetFileNameWithoutExtension(kv.Key),
						source = "pipeline"
					});
				}
			}

			// Fallback: in-memory editor console via reflection. Per-process (safe across
			// multiple editor instances) and catches errors the pipeline cache missed —
			// most commonly diagnostics from before our [InitializeOnLoad] subscribed.
			// Degrades silently if reflection is unavailable on this Unity version.
			if (ConsoleLogEntries.TryGetCompileDiagnostics(false, out var consoleDiags))
				foreach (var d in consoleDiags)
				{
					if (d.Severity != "error") continue;
					if (!seen.Add(DedupKey(d.File, d.Line, d.Col))) continue;
					result.Add(new
					{
						file = d.File,
						line = d.Line,
						col = d.Col,
						severity = d.Severity,
						message = string.IsNullOrEmpty(d.Code) ? d.Message : d.Code + ": " + d.Message,
						assembly = (string)null,
						source = "console"
					});
				}

			return result;
		}

		private static string DedupKey(string file, int line, int col)
		{
			var norm = string.IsNullOrEmpty(file) ? "" : file.Replace('\\', '/');
			return norm + ":" + line + ":" + col;
		}

		// ─── Testing hooks ──────────────────────────────────────
		// Internal seam for unit tests. Production code must not call these.

		internal static void SeedForTests(string assemblyPath, CompilerMessage[] messages)
		{
			ByAssembly[assemblyPath] = messages;
		}

		internal static void ResetForTests()
		{
			ByAssembly.Clear();
		}
	}
}