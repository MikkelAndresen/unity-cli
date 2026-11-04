using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityCliConnector
{
    /// Bounds a raw Unity log message (message body plus stack trace) for machine callers.
    ///
    /// Frame noise is recognised structurally, never by a list of known-noisy names: a line is
    /// a frame when it reads "Type:Method (args)" (Unity's Mono form, also used by exception
    /// ToString) or "at Type.Method (args) [0x0] in file:line" (the .NET form), and it is
    /// engine noise when the declaring type's root namespace belongs to Unity or the runtime,
    /// when the frame carries no managed type at all (native/Burst/IL2CPP address frames,
    /// &lt;Module&gt;, compiler-generated &lt;...&gt; types, "(wrapper ...)"), or when its source
    /// lives under Library/ (PackageCache, generated code).
    ///
    /// ReadConsole used to filter with a hardcoded substring list tuned to one project's noise
    /// (it named Unity.Entities.SystemState outright); every Burst/Entities frame slipped
    /// through it, which is what made a single console call 35 KB.
    public static class StackTraceFilter
    {
        /// maxFrames value that keeps every frame.
        public const int UnlimitedFrames = -1;

        public enum StacktraceMode
        {
            /// Message body only, no frames.
            None,
            /// Message body plus user-code frames; engine frames collapse into markers.
            User,
            /// Every line, still capped by maxFrames.
            Full,
        }

        public static bool TryParseMode(string raw, out StacktraceMode mode)
        {
            switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "none": mode = StacktraceMode.None; return true;
                case "user": mode = StacktraceMode.User; return true;
                case "full": mode = StacktraceMode.Full; return true;
                default: mode = StacktraceMode.User; return false;
            }
        }

        public static string ModeName(StacktraceMode mode)
        {
            switch (mode)
            {
                case StacktraceMode.None: return "none";
                case StacktraceMode.Full: return "full";
                default: return "user";
            }
        }

        public readonly struct Formatted
        {
            public readonly string Text;
            public readonly int FramesKept;
            /// <summary>Frames dropped as engine noise plus frames cut by maxFrames.</summary>
            public readonly int FramesOmitted;
            /// <summary>Frames dropped only because maxFrames was reached.</summary>
            public readonly int FramesOverCap;
            public readonly int CharsOmitted;

            public Formatted(string text, int framesKept, int framesOmitted, int framesOverCap, int charsOmitted)
            {
                Text = text;
                FramesKept = framesKept;
                FramesOmitted = framesOmitted;
                FramesOverCap = framesOverCap;
                CharsOmitted = charsOmitted;
            }
        }

        public static Formatted Format(string message, StacktraceMode mode, int maxFrames, int maxEntryChars)
        {
            if (string.IsNullOrEmpty(message))
                return new Formatted(string.Empty, 0, 0, 0, 0);

            if (mode == StacktraceMode.None)
                return new Formatted(FirstLine(message), 0, 0, 0, 0);

            if (maxFrames < 0) maxFrames = int.MaxValue;

            var lines = Classify(message);
            var frames = FindFrames(lines);
            // A trace with no user frame at all would leave no location to act on, so the first
            // frame that is not the Debug.Log call itself survives the structural filter.
            int fallback = frames.Count > 0 && !HasUserFrame(lines) ? FirstNonDebugFrame(lines) : -1;

            var sb = new StringBuilder();
            int kept = 0, omitted = 0, overCap = 0, runEngine = 0, runOther = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].IsFrame)
                {
                    FlushRun(sb, ref runEngine, ref runOther);
                    AppendLine(sb, lines[i].Text);
                    continue;
                }

                bool structural = mode == StacktraceMode.User && lines[i].IsEngine && i != fallback;
                if (structural)
                {
                    omitted++;
                    runEngine++;
                    continue;
                }
                if (kept >= maxFrames)
                {
                    omitted++;
                    overCap++;
                    runOther++;
                    continue;
                }

                FlushRun(sb, ref runEngine, ref runOther);
                AppendLine(sb, lines[i].Text);
                kept++;
            }
            FlushRun(sb, ref runEngine, ref runOther);

            // An entry whose whole body is frames would otherwise come back empty. --frames 0 is an
            // explicit "no frames", so it stays empty there.
            if (sb.Length == 0 && maxFrames > 0 && frames.Count > 0)
                AppendLine(sb, lines[frames[0]].Text);

            var text = sb.ToString();
            int charsOmitted = 0;
            if (maxEntryChars > 0 && text.Length > maxEntryChars)
            {
                // The "... (+N characters omitted)" marker is part of the entry, so it comes out of
                // the cap: 33 covers its longest form. The kept part is then cut at the last line
                // end inside its budget, so a cut entry still reads as whole lines when it can.
                int keep = Math.Max(maxEntryChars / 2, maxEntryChars - 33);
                int newline = text.LastIndexOf('\n', Math.Min(keep, text.Length - 1));
                if (newline > 0) keep = newline;
                charsOmitted = text.Length - keep;
                text = text.Substring(0, keep).TrimEnd() + $"\n... (+{charsOmitted} characters omitted)";
            }

            return new Formatted(text, kept, omitted, overCap, charsOmitted);
        }

        private static string FirstLine(string message)
        {
            int newline = message.IndexOf('\n');
            if (newline < 0) return message.Trim();
            return message.Substring(0, newline).Trim();
        }

        private static void AppendLine(StringBuilder sb, string line)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }

        // Runs of dropped frames read as one marker line instead of N, so a Burst trace costs
        // one line of output rather than dozens.
        private static void FlushRun(StringBuilder sb, ref int engine, ref int other)
        {
            int total = engine + other;
            if (total == 0) return;
            AppendLine(sb, other == 0
                ? $"... (+{total} engine frames)"
                : $"... (+{total} frames omitted)");
            engine = 0;
            other = 0;
        }

        private readonly struct LineInfo
        {
            public readonly string Text;
            public readonly bool IsFrame;
            public readonly bool IsEngine;
            public readonly bool IsDebugCall;

            public LineInfo(string text, bool isFrame, bool isEngine, bool isDebugCall)
            {
                Text = text;
                IsFrame = isFrame;
                IsEngine = isEngine;
                IsDebugCall = isDebugCall;
            }
        }

        private static List<LineInfo> Classify(string message)
        {
            var raw = message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var lines = new List<LineInfo>(raw.Length);
            foreach (var line in raw)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                lines.Add(Inspect(trimmed));
            }
            return lines;
        }

        private static List<int> FindFrames(List<LineInfo> lines)
        {
            var frames = new List<int>();
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].IsFrame) frames.Add(i);
            return frames;
        }

        private static bool HasUserFrame(List<LineInfo> lines)
        {
            foreach (var line in lines)
                if (line.IsFrame && !line.IsEngine) return true;
            return false;
        }

        private static int FirstNonDebugFrame(List<LineInfo> lines)
        {
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].IsFrame && !lines[i].IsDebugCall) return i;
            return -1;
        }

        // "UnityEngine.Debug:LogError (object) (at ./Packages/.../Debug.cs:12)"
        // "  at Foo.Bar () [0x00012] in <hash>:0"
        private static readonly Regex LocationPattern = new(
            @"\s*(?:\(at\s+(?<at>[^)]+)\)|(?:(?:\[0x[0-9a-fA-F]+\]\s*)?in\s+(?<in>.+?)\s*:-?\d+))\s*$",
            RegexOptions.Compiled);

        // Mono form: Type[:Nested/<Generated>]:Method (args). The argument list is required:
        // without it an exception's own "Type: message" first line looks like a frame and gets
        // filtered away with the noise.
        private static readonly Regex ColonFramePattern = new(
            @"^(?<type>[A-Za-z_][\w<>`$.\[\]/+]*):(?<method>[A-Za-z_~<>$`|][\w<>`$.\[\]/|]*)\s*\((?<args>.*)\)$",
            RegexOptions.Compiled);

        // .NET form: "at Foo.Bar.Method (args)", location already stripped.
        private static readonly Regex DotFramePattern = new(
            @"^at\s+(?<qualified>[A-Za-z_<>$`][\w<>`$.\[\]/+|]*)(?:\s*\((?<args>.*)\))?\s*$",
            RegexOptions.Compiled);

        // Mono's rethrow separators carry no frame at all but sit inside the trace region.
        private static readonly Regex RethrowMarkerPattern = new(
            @"^-{3}\s*(?:Begin|End) of (?:Mono logged )?(?:exception|stack trace)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // A line that is only a native address: Burst/IL2CPP frames the managed walk cannot name.
        private static readonly Regex AddressOnlyPattern = new(@"^0x[0-9a-fA-F]{4,}$", RegexOptions.Compiled);

        // "Type:Method" without an argument list, accepted only when it is unambiguous.
        private static readonly Regex BareColonFramePattern = new(
            @"^(?<type>[A-Za-z_][\w<>`$.\[\]/+]*):(?<method>[A-Za-z_~<>$`|][\w<>`$.\[\]/|]*)$",
            RegexOptions.Compiled);

        private const string DebugClassName = "UnityEngine.Debug";

        private static LineInfo Inspect(string line)
        {
            var location = LocationPattern.Match(line);
            var core = location.Success ? line.Substring(0, location.Index).Trim() : line;
            var source = location.Success
                ? (location.Groups["at"].Success ? location.Groups["at"].Value : location.Groups["in"].Value).Trim()
                : null;

            if (NoManagedType(core) || RethrowMarkerPattern.IsMatch(core))
                return new LineInfo(line, true, true, false);

            var colon = ColonFramePattern.Match(core);
            if (colon.Success)
            {
                var type = colon.Groups["type"].Value;
                return new LineInfo(line, true, IsEngineType(type) || IsEngineSource(source), type == DebugClassName);
            }

            var dot = DotFramePattern.Match(core);
            if (dot.Success)
            {
                var qualified = dot.Groups["qualified"].Value;
                return new LineInfo(line, true, IsEngineType(qualified) || IsEngineSource(source),
                    qualified.StartsWith(DebugClassName + ".", StringComparison.Ordinal));
            }

            // Some Unity versions print frames without an argument list. Prose never matches this
            // (a message line has a space after the colon), so "System.Exception: boom" stays text.
            var bare = BareColonFramePattern.Match(core);
            if (bare.Success)
            {
                var type = bare.Groups["type"].Value;
                return new LineInfo(line, true, IsEngineType(type) || IsEngineSource(source), type == DebugClassName);
            }

            return new LineInfo(line, false, false, false);
        }

        // Frames a managed stack walk cannot name: native addresses, IL2CPP's <Module>,
        // Burst/IL2CPP compiler-generated holders, Mono "(wrapper ...)" stubs. A line beginning
        // with "[" is deliberately not one of them: "[Category] message" is a common log prefix.
        private static bool NoManagedType(string core)
        {
            if (core.Length == 0) return true;
            if (core[0] == '(' || core[0] == '<') return true;
            if (core.Contains("<unknown>") || core.Contains("<Module>")) return true;
            return AddressOnlyPattern.IsMatch(core);
        }

        private static readonly string[] EngineNamespaceRoots =
        {
            // "Unity" covers Unity.Entities, Unity.Burst, Unity.Jobs, Unity.Collections, ...
            // Matching is per root segment, so user code under e.g. UnityCliConnector stays.
            "UnityEngine", "UnityEditor", "Unity", "System", "Mono", "Burst", "NUnit",
        };

        private static bool IsEngineType(string type)
        {
            if (string.IsNullOrEmpty(type)) return true;
            if (type[0] == '<' || type[0] == '+' || type[0] == '/') return true;

            int rootEnd = type.IndexOf('.');
            var root = rootEnd < 0 ? type : type.Substring(0, rootEnd);
            foreach (var engine in EngineNamespaceRoots)
                if (string.Equals(root, engine, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // PackageCache and generated code are Unity's own build output, whatever type name
        // a frame inside it happens to carry.
        private static bool IsEngineSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            if (source.StartsWith("./Library/", StringComparison.Ordinal)) return true;
            if (source.StartsWith("Library/", StringComparison.Ordinal)) return true;
            if (source.Contains("/Library/PackageCache/")) return true;
            return source.StartsWith("<generated>", StringComparison.OrdinalIgnoreCase);
        }
    }
}
