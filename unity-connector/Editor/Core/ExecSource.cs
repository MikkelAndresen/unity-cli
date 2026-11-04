using System;
using System.Collections.Generic;
using System.Text;

namespace UnityCliConnector
{
    /// <summary>
    /// Generates the source compiled for an exec snippet: default imports, hoisted leading
    /// using directives, and the Execute signature the submitted body needs. Kept free of
    /// Unity APIs so the generated text can be tested without invoking csc.
    /// </summary>
    internal static class ExecSource
    {
        internal const string TypeName = "__CliDynamic";

        internal static readonly string[] DefaultUsings =
        {
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.IO",
            "System.Linq",
            "System.Reflection",
            "System.Threading.Tasks",
            "UnityEngine",
            "UnityEngine.SceneManagement",
            "UnityEditor",
            "UnityEditor.SceneManagement",
            "UnityEditorInternal",
        };

        // System and UnityEngine both define these names, so importing both namespaces makes
        // a bare Object or Random a CS0104. Aliases resolve them to the Unity type without
        // stopping a snippet from naming System.Object / System.Random in full.
        internal static readonly string[] DefaultAliases =
        {
            "Object = UnityEngine.Object",
            "Random = UnityEngine.Random",
            "Debug = UnityEngine.Debug",
        };

        internal enum BodyKind { Sync, Async, Iterator }

        internal static string Build(string code, IReadOnlyList<string> extraUsings, bool includeDefaults,
            BodyKind kind)
        {
            if (code == null) throw new ArgumentNullException(nameof(code));

            var directives = new List<string>();
            var body = ExtractLeadingUsings(code, directives);

            if (extraUsings != null)
            {
                foreach (var raw in extraUsings)
                {
                    var directive = NormalizeDirective(raw);
                    if (directive.Length > 0) directives.Add(directive);
                }
            }

            var aliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var directive in directives)
            {
                var alias = AliasName(directive);
                if (alias != null) aliases.Add(alias);
            }

            var sb = new StringBuilder();
            if (includeDefaults)
            {
                foreach (var u in DefaultUsings)
                    sb.Append("using ").Append(u).AppendLine(";");
            }

            foreach (var directive in directives)
                sb.Append("using ").Append(directive).AppendLine(";");

            // A snippet's own alias must win: emitting the default alias too would be a
            // duplicate alias (CS1537), not a preference.
            if (includeDefaults)
            {
                foreach (var alias in DefaultAliases)
                {
                    if (aliases.Contains(AliasName(alias))) continue;
                    sb.Append("using ").Append(alias).AppendLine(";");
                }
            }

            sb.AppendLine();
            sb.Append("public static class ").Append(TypeName).AppendLine(" {");
            switch (kind)
            {
                case BodyKind.Iterator:
                    sb.AppendLine("  public static System.Collections.IEnumerator Execute() {");
                    break;
                case BodyKind.Async:
                    sb.AppendLine("  public static async System.Threading.Tasks.Task<object> Execute() {");
                    break;
                default:
                    sb.AppendLine("  public static object Execute() {");
                    break;
            }
            sb.AppendLine(body);
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Moves using directives from the top of the submitted body to the generated file's
        /// using block. Comments and blank lines around them stay with the body. A using
        /// statement/declaration (`using (…)`, `using var …`) is code, not a directive, so
        /// scanning stops there.
        /// </summary>
        private static string ExtractLeadingUsings(string code, List<string> directives)
        {
            var pos = 0;
            var trivia = new StringBuilder();
            var found = false;

            while (pos < code.Length)
            {
                var triviaStart = pos;
                while (pos < code.Length && char.IsWhiteSpace(code[pos])) pos++;
                trivia.Append(code, triviaStart, pos - triviaStart);

                if (pos >= code.Length) break;

                if (code[pos] == '/' && pos + 1 < code.Length && code[pos + 1] == '/')
                {
                    var end = code.IndexOf('\n', pos);
                    if (end < 0) end = code.Length;
                    trivia.Append(code, pos, end - pos);
                    pos = end;
                    continue;
                }

                if (code[pos] == '/' && pos + 1 < code.Length && code[pos + 1] == '*')
                {
                    var end = code.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        trivia.Append(code, pos, code.Length - pos);
                        pos = code.Length;
                        break;
                    }
                    trivia.Append(code, pos, end + 2 - pos);
                    pos = end + 2;
                    continue;
                }

                int contentStart;
                if (!IsUsingDirectiveStart(code, pos, out contentStart)) break;

                var semicolon = code.IndexOf(';', contentStart);
                if (semicolon < 0) break;

                var directive = code.Substring(contentStart, semicolon - contentStart).Trim();
                if (directive.Length == 0) break;
                directives.Add(directive);
                found = true;
                pos = semicolon + 1;
            }

            return found ? trivia.ToString() + code.Substring(pos) : code;
        }

        private static bool IsUsingDirectiveStart(string code, int pos, out int contentStart)
        {
            contentStart = 0;
            if (string.CompareOrdinal(code, pos, "using", 0, 5) != 0) return false;

            var p = pos + 5;
            if (p < code.Length && !char.IsWhiteSpace(code[p])) return false;
            while (p < code.Length && char.IsWhiteSpace(code[p])) p++;
            if (p >= code.Length) return false;

            // `using (…)` and `using var …` are statements/declarations inside the body.
            if (code[p] == '(') return false;
            if (string.CompareOrdinal(code, p, "var", 0, 3) == 0 &&
                (p + 3 >= code.Length || !IsIdentifierPart(code[p + 3])))
                return false;

            contentStart = p;
            return true;
        }

        private static bool IsIdentifierPart(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        internal static string NormalizeDirective(string raw)
        {
            if (raw == null) return string.Empty;

            var directive = raw.Trim();
            if (directive.StartsWith("using ", StringComparison.Ordinal))
                directive = directive.Substring(6).Trim();
            else if (directive == "using")
                return string.Empty;

            while (directive.Length > 0 && directive[directive.Length - 1] == ';')
                directive = directive.Substring(0, directive.Length - 1).TrimEnd();
            return directive;
        }

        private static string AliasName(string directive)
        {
            if (directive == null) return null;

            var equals = directive.IndexOf('=');
            if (equals < 0) return null;

            var name = directive.Substring(0, equals).Trim();
            if (name.Length == 0 || name.IndexOf(' ') >= 0 || name.IndexOf('\t') >= 0) return null;
            return name;
        }
    }
}
