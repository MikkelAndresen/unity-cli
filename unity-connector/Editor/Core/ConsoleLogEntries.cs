using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityCliConnector.Tools;

namespace UnityCliConnector
{
	/// Compile-diagnostic snapshot helper. The actual LogEntries reflection lives
	/// in <see cref="ReadConsole"/> — this class layers ScriptCompile mode-bit
	/// filtering and header parsing on top of <c>ReadConsole.TryForEachEntry</c>
	/// so both the gate and the console tool share one binding.
	///
	/// Failure mode: if ReadConsole's reflection is unavailable on this Unity
	/// version, <c>TryGetCompileDiagnostics</c> returns false and callers degrade
	/// rather than throwing.
	///
	/// Public so sibling packages (e.g. unity-cli-extensions) can read the same
	/// live diagnostics via <see cref="CompilationGate.GetLiveDiagnostics"/> instead
	/// of scraping Editor.log. The test seams below stay internal.
	public static class ConsoleLogEntries
	{
		// Mode bits for script-compile entries. Stable across recent Unity versions.
		public const int ScriptCompileErrorMask = 1 << 11;
		public const int ScriptCompileWarningMask = 1 << 12;

		// Sticky errors — most notably the persistent "All compiler errors have to
		// be fixed before you can enter play mode!" banner — flag unresolved compile
		// failures even when the per-error ScriptCompileError entries are no longer
		// the live console selection. Mirrors ReadConsole.ErrorMask's StickyError bit.
		// We deliberately do NOT fold in ScriptingError (1<<8): that bit is also set
		// by runtime Debug.LogError, which must not trip a *compilation* gate.
		public const int StickyErrorMask = 1 << 13;

		// What the console fallback treats as a compile-blocking error.
		internal const int CompileBlockingMask = ScriptCompileErrorMask | StickyErrorMask;

		// Internal seam for unit tests: when set, bypasses reflection entirely and
		// returns the supplied diagnostics. Production code must not set this.
		internal static Func<bool, List<CompileDiag>> TestOverride;

		// "Assets/Foo.cs(12,5): error CS0103: The name 'x' does not exist..."
		private static readonly Regex HeaderPattern = new(
			@"^(?<file>[^()]+)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+(?<code>[A-Z]+\d+):\s*(?<msg>.+)$",
			RegexOptions.Compiled);

		public static bool IsAvailable => ReadConsole.IsReflectionAvailable;
		public static string InitError => IsAvailable ? null : "ReadConsole reflection unavailable";

		public static bool TryGetCompileDiagnostics(bool includeWarnings, out List<CompileDiag> diagnostics)
		{
			if (TestOverride != null)
			{
				diagnostics = TestOverride(includeWarnings) ?? new List<CompileDiag>();
				return true;
			}

			var result = new List<CompileDiag>();
			diagnostics = result;

			return ReadConsole.TryForEachEntry((mode, message, file, line) =>
			{
				if (TryConvertEntry(mode, message, file, line, includeWarnings, out var diag))
					result.Add(diag);
			});
		}

		/// Pure conversion of an in-memory LogEntry's primitive fields into a CompileDiag.
		/// Returns false for entries that don't match the ScriptCompile mode bits or have
		/// an empty message. Extracted so tests can exercise the mode-bit filtering
		/// without faking out the LogEntries API.
		internal static bool TryConvertEntry(int mode, string message, string file, int line,
			bool includeWarnings, out CompileDiag diag)
		{
			diag = default;
			var isError = (mode & CompileBlockingMask) != 0;
			var isWarning = (mode & ScriptCompileWarningMask) != 0;
			if (!isError && !(includeWarnings && isWarning)) return false;
			if (string.IsNullOrEmpty(message)) return false;
			diag = Parse(message, file, line, isError);
			return true;
		}

		internal static CompileDiag Parse(string fullMessage, string fileFallback, int lineFallback, bool isError)
		{
			// LogEntry's first line tends to carry the structured header; the rest is stacktrace.
			var newline = fullMessage.IndexOf('\n');
			var head = newline >= 0 ? fullMessage.Substring(0, newline) : fullMessage;
			var m = HeaderPattern.Match(head);
			if (m.Success)
				return new CompileDiag
				{
					File = m.Groups["file"].Value,
					Line = int.Parse(m.Groups["line"].Value),
					Col = int.Parse(m.Groups["col"].Value),
					Severity = m.Groups["sev"].Value,
					Code = m.Groups["code"].Value,
					Message = m.Groups["msg"].Value
				};

			// Header didn't match — fall back to LogEntry's own file/line fields.
			return new CompileDiag
			{
				File = fileFallback ?? "",
				Line = lineFallback,
				Col = 0,
				Severity = isError ? "error" : "warning",
				Code = "",
				Message = head
			};
		}

		public struct CompileDiag
		{
			public string File;
			public int Line;
			public int Col;
			public string Severity;
			public string Code;
			public string Message;
		}
	}
}