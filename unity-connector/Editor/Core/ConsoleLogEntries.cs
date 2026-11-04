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
		public const int CompileBlockingMask = ScriptCompileErrorMask | StickyErrorMask;

		// Internal seam for unit tests: when set, bypasses reflection entirely and
		// returns the supplied diagnostics. Production code must not set this.
		internal static Func<bool, List<CompileDiag>> TestOverride;

		// "Assets/Foo.cs(12,5): error CS0103: The name 'x' does not exist..."
		private static readonly Regex HeaderPattern = new(
			@"^(?<file>[^()]+)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+(?<code>[A-Z]+\d+):\s*(?<msg>.+)$",
			RegexOptions.Compiled);

		// Unity's GetEntryTimestampInternal prefixes the message with e.g. "[07:20:09]".
		private static readonly Regex TimestampPattern = new(
			@"^\s*\[(?<h>\d{1,2}):(?<m>\d{2}):(?<s>\d{2})\]", RegexOptions.Compiled);

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

			return ReadConsole.TryForEachEntry((mode, message, file, line, timestamp) =>
			{
				if (TryConvertEntry(mode, message, file, line, includeWarnings, out var diag, timestamp))
					result.Add(diag);
			});
		}

		/// Every in-memory console entry, unfiltered, with the full message including its stack
		/// trace. <see cref="Parse"/> cuts the head, and a Burst error writes its position only as an
		/// "(at <file>:<line>)" frame — so a tool that wants Burst's file:line has to read past the head.
		public static bool TryForEachRawEntry(Action<int, string, string, int> visitor)
		{
			return ReadConsole.TryForEachEntry(visitor);
		}

		/// Pure conversion of an in-memory LogEntry's primitive fields into a CompileDiag.
		/// Returns false for entries that don't match the ScriptCompile mode bits or have
		/// an empty message. Extracted so tests can exercise the mode-bit filtering
		/// without faking out the LogEntries API.
		internal static bool TryConvertEntry(int mode, string message, string file, int line,
			bool includeWarnings, out CompileDiag diag, string timestamp = null)
		{
			diag = default;
			var isError = (mode & CompileBlockingMask) != 0;
			var isWarning = (mode & ScriptCompileWarningMask) != 0;
			if (!isError && !(includeWarnings && isWarning)) return false;
			if (string.IsNullOrEmpty(message)) return false;
			diag = Parse(message, file, line, isError);
			diag.Sticky = (mode & StickyErrorMask) != 0;
			diag.TimeOfDaySeconds = ParseTimestampSeconds(timestamp);
			diag.HasTimestamp = diag.TimeOfDaySeconds >= 0;
			return true;
		}

		internal static long ParseTimestampSeconds(string timestamp)
		{
			if (string.IsNullOrEmpty(timestamp)) return -1;
			var m = TimestampPattern.Match(timestamp);
			if (!m.Success) return -1;
			var h = int.Parse(m.Groups["h"].Value);
			var min = int.Parse(m.Groups["m"].Value);
			var s = int.Parse(m.Groups["s"].Value);
			if (h > 23 || min > 59 || s > 59) return -1;
			return h * 3600L + min * 60L + s;
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

			// Burst writes "<file>(<line>,<col>): Burst error BCxxxx: ...", or "(0,0): Burst error ..."
			// with the position only in a stack frame. Neither reads as a C# header, so without this a
			// Burst error is reported with no file and no code.
			if (BurstErrors.TryParse(fullMessage, fileFallback, lineFallback, out var burst))
				return new CompileDiag
				{
					File = burst.File,
					Line = burst.Line,
					Col = burst.Col,
					Severity = isError ? "error" : "warning",
					Code = burst.Code,
					Message = burst.Message
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
			/// <summary>What the caller should write instead, for the codes where that is knowable
			/// (see <see cref="CompileHints"/>). Null when there is nothing close to suggest.</summary>
			public string Hint;
			/// <summary>Sticky console entry (build/runtime failure banner), not a compiler diagnostic.</summary>
			public bool Sticky;
			/// <summary>Local time of day from LogEntries.GetEntryTimestampInternal; -1 is unknown.</summary>
			public long TimeOfDaySeconds;
			public bool HasTimestamp;
		}
	}
}