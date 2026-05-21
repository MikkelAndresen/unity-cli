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