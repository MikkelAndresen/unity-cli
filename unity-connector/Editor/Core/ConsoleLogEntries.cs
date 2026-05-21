using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
	/// Reflective accessor for UnityEditor.LogEntries — the in-memory editor console
	/// buffer. Per-process (multi-instance safe), survives domain reloads within a
	/// session, but is cleared on editor restart.
	///
	/// Canonical reference: unity-cli-connector's ReadConsole.cs ships the same
	/// reflection pattern and has tracked Unity internals across several LTS bumps;
	/// when something here breaks after a Unity upgrade, diff against that file
	/// first — it usually has the fixed binding before we do.
	///
	/// Convention for adding new editor-internal accessors: each one gets its own
	/// file under Editor/Core/ named after the Unity type it wraps (e.g.
	/// `ConsoleWindowFilters.cs`), follows this same fail-closed pattern
	/// (`IsAvailable` / `InitError` / `TryGet*`), and is referenced from a single
	/// place in calling code so version-drift fixes are localized.
	///
	/// Failure mode: if Unity renames the internals, the static ctor catches the
	/// exception, sets `_initError`, and every `TryGet*` returns false. Callers
	/// degrade rather than throwing on every request.
	internal static class ConsoleLogEntries
	{
		// Mode bit for script-compile errors. Stable across recent Unity versions but
		// technically internal — if a future version reshuffles, the bit value here
		// would need updating. See ReadConsole.cs in unity-cli-connector for the full set.
		public const int ScriptCompileErrorMask = 1 << 11;
		public const int ScriptCompileWarningMask = 1 << 12;

		private static readonly MethodInfo Start, End, GetCount, GetEntry;
		private static readonly Type LOGEntryType;
		private static readonly FieldInfo ModeField, MessageField, FileField, LineField;

		/// Snapshot the in-memory console for ScriptCompileError entries. Returns
		/// false (and an empty list) if reflection is unavailable on this Unity
		/// version — callers should fall through to other sources.
		// Internal seam for unit tests: when set, bypasses reflection entirely and
		// returns the supplied diagnostics. Production code must not set this.
		internal static Func<bool, List<CompileDiag>> TestOverride;

		// "Assets/Foo.cs(12,5): error CS0103: The name 'x' does not exist..."
		private static readonly Regex HeaderPattern = new(
			@"^(?<file>[^()]+)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+(?<code>[A-Z]+\d+):\s*(?<msg>.+)$",
			RegexOptions.Compiled);

		static ConsoleLogEntries()
		{
			try
			{
				var asm = typeof(EditorApplication).Assembly;
				var logEntriesType = asm.GetType("UnityEditor.LogEntries")
				                     ?? throw new Exception("UnityEditor.LogEntries not found");
				LOGEntryType = asm.GetType("UnityEditor.LogEntry")
				               ?? throw new Exception("UnityEditor.LogEntry not found");

				const BindingFlags sf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
				const BindingFlags inf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

				Start = Require(logEntriesType.GetMethod("StartGettingEntries", sf), "LogEntries.StartGettingEntries");
				End = Require(logEntriesType.GetMethod("EndGettingEntries", sf), "LogEntries.EndGettingEntries");
				GetCount = Require(logEntriesType.GetMethod("GetCount", sf), "LogEntries.GetCount");
				GetEntry = Require(logEntriesType.GetMethod("GetEntryInternal", sf), "LogEntries.GetEntryInternal");
				ModeField = Require(LOGEntryType.GetField("mode", inf), "LogEntry.mode");
				MessageField = Require(LOGEntryType.GetField("message", inf), "LogEntry.message");
				FileField = LOGEntryType.GetField("file", inf); // optional
				LineField = LOGEntryType.GetField("line", inf); // optional
			}
			catch (Exception e)
			{
				InitError = $"{e.Message} (Unity {Application.unityVersion})";
				Debug.LogWarning($"[CompilationGate] LogEntries reflection unavailable: {InitError}");
			}
		}

		public static bool IsAvailable => InitError == null;
		public static string InitError { get; }

		public static bool TryGetCompileDiagnostics(bool includeWarnings, out List<CompileDiag> diagnostics)
		{
			if (TestOverride != null)
			{
				diagnostics = TestOverride(includeWarnings) ?? new List<CompileDiag>();
				return true;
			}

			diagnostics = new List<CompileDiag>();
			if (!IsAvailable) return false;

			try
			{
				Start.Invoke(null, null);
				var total = (int)GetCount.Invoke(null, null);
				var entry = Activator.CreateInstance(LOGEntryType);

				for (var i = 0; i < total; i++)
				{
					GetEntry.Invoke(null, new[] { i, entry });
					var mode = (int)ModeField.GetValue(entry);
					var message = MessageField.GetValue(entry) as string;
					var file = FileField?.GetValue(entry) as string;
					var line = LineField != null ? (int)LineField.GetValue(entry) : 0;
					if (TryConvertEntry(mode, message, file, line, includeWarnings, out var diag))
						diagnostics.Add(diag);
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[CompilationGate] LogEntries snapshot failed: {e.Message}");
				return false;
			}
			finally
			{
				try
				{
					End.Invoke(null, null);
				}
				catch
				{
					/* best-effort cleanup */
				}
			}

			return true;
		}

		/// Pure conversion of an in-memory LogEntry's primitive fields into a CompileDiag.
		/// Returns false for entries that don't match the ScriptCompile mode bits or have
		/// an empty message. Extracted from the reflection loop so tests can exercise the
		/// mode-bit filtering without faking out the LogEntries API.
		internal static bool TryConvertEntry(int mode, string message, string file, int line,
			bool includeWarnings, out CompileDiag diag)
		{
			diag = default;
			var isError = (mode & ScriptCompileErrorMask) != 0;
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

		private static T Require<T>(T value, string label) where T : class
		{
			if (value == null) throw new Exception($"Missing internal member: {label}");
			return value;
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