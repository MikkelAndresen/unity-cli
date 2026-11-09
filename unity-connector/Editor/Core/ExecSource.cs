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

        /// The source compiled for one snippet, and where the submitted body starts in it: a
        /// compiler diagnostic on line N of <see cref="Text"/> is on snippet line N - BodyStartLine + 1
        /// for anything the `#line` directive does not already map (the generated prologue).
        internal readonly struct Generated
        {
            internal readonly string Text;
            internal readonly int BodyStartLine;
            /// The name the `#line` directive gives the body, so a diagnostic names the caller's code
            /// rather than a temporary file.
            internal const string SnippetFileName = "exec-snippet";

            internal Generated(string text, int bodyStartLine)
            {
                Text = text;
                BodyStartLine = bodyStartLine;
            }
        }

        internal static string Build(string code, IReadOnlyList<string> extraUsings, bool includeDefaults,
            BodyKind kind)
            => BuildMapped(code, extraUsings, includeDefaults, kind, returnExpression: false).Text;

        internal static Generated BuildMapped(string code, IReadOnlyList<string> extraUsings, bool includeDefaults,
            BodyKind kind)
            => BuildMapped(code, extraUsings, includeDefaults, kind, returnExpression: false);

        internal static Generated BuildMapped(string code, IReadOnlyList<string> extraUsings, bool includeDefaults,
            BodyKind kind, bool returnExpression)
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

            // The directive makes the compiler report the caller's line numbers, and gives a debug
            // build (see ExecCompiler) a snippet file to attribute frames to. It goes immediately
            // before the body, so the caller's line 1 is whatever the directive's next line is —
            // including when the expression form puts `return (` above it.
            if (returnExpression) sb.AppendLine("    return (");
            sb.Append("#line 1 \"").Append(Generated.SnippetFileName).AppendLine("\"");
            var bodyStartLine = CountLines(sb) + 1;
            sb.AppendLine(body);
            if (returnExpression) sb.AppendLine("    );");
            // A body written without its final semicolon (`Debug.Log("x")`, `return 1`) is what a
            // caller types at a prompt. Terminating it on its own line is whitespace-equivalent C#,
            // and it still terminates the statement before a trailing comment can swallow it.
            else if (kind != BodyKind.Iterator) sb.AppendLine("    ;");
            sb.AppendLine("#line default");
            // A body that ends without a return (a void call such as `Debug.Log("x")`, a loop) is not
            // an error: the snippet ran and produced no value. The compiler would otherwise reject it
            // with CS0161, which used to make a plain side-effecting snippet unexecutable.
            if (!returnExpression && kind != BodyKind.Iterator) sb.AppendLine("    return null;");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return new Generated(sb.ToString(), bodyStartLine);
        }

        private static int CountLines(StringBuilder sb)
        {
            var lines = 0;
            for (var i = 0; i < sb.Length; i++)
                if (sb[i] == '\n') lines++;
            return lines;
        }

        /// A snippet that is one expression and nothing else (`Time.time`, `1 + 2`,
        /// `new Vector3(0, 1, 0)`) is wrapped in `return (…);`, so the documented bare-expression form
        /// works without the caller adding `return`. Anything containing a top-level `;`, or starting
        /// with a statement keyword, is a body and is compiled as one; that keeps
        /// `Debug.Log("x")` (a void call) and multi-statement snippets working unchanged.
        internal static bool WrapSingleExpression(string code, BodyKind kind)
        {
            if (kind == BodyKind.Iterator) return false;
            return IsSingleExpression(code);
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
            if (IsKeywordAt(code, p, "var")) return false;
            if (IsKeywordAt(code, p, "static"))
            {
                contentStart = p;
                return true;
            }

            // `using <Type> <name> = …;` is a declaration too: the first token is a type
            // and the next one starts the variable name. A directive's first token is the
            // whole namespace or alias target, so nothing identifier-like follows it (a
            // dot separated by whitespace does not count).
            var semicolon = code.IndexOf(';', p);
            if (semicolon < 0) return false;
            var content = code.Substring(p, semicolon - p).TrimEnd();
            var tokenEnd = 0;
            while (tokenEnd < content.Length && !char.IsWhiteSpace(content[tokenEnd])) tokenEnd++;
            var rest = content.Substring(tokenEnd).TrimStart();
            if (rest.Length > 0 && (char.IsLetter(rest[0]) || rest[0] == '_')) return false;

            contentStart = p;
            return true;
        }

        private static bool IsKeywordAt(string code, int pos, string keyword)
        {
            if (string.CompareOrdinal(code, pos, keyword, 0, keyword.Length) != 0) return false;
            var end = pos + keyword.Length;
            return end >= code.Length || !IsIdentifierPart(code[end]);
        }

        private static bool IsIdentifierPart(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        /// True when the code is a single expression with no top-level `;` and no statement keyword:
        /// `Time.time`, `1 + 2`, `new Vector3(0, 1, 0)`, `list.Select(x => x * 2).ToList()`. A block,
        /// a declaration, a control statement or anything with a top-level semicolon is a body.
        /// Semicolons and keywords inside strings, characters, comments and nested brackets do not
        /// count, so `"a;b"` and `f(x => { return 1; })` are still single expressions.
        internal static bool IsSingleExpression(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;

            var body = StripLeadingTrivia(code);
            if (body.Length == 0) return false;
            if (StartsWithStatementKeyword(body)) return false;

            var depth = 0;
            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                switch (c)
                {
                    case '"':
                    case '\'':
                        i = SkipQuoted(body, i);
                        break;
                    case '/' when i + 1 < body.Length && body[i + 1] == '/':
                        i = SkipLineComment(body, i);
                        break;
                    case '/' when i + 1 < body.Length && body[i + 1] == '*':
                        i = SkipBlockComment(body, i);
                        break;
                    case '(':
                    case '[':
                    case '{':
                        depth++;
                        break;
                    case ')' when depth > 0:
                    case ']' when depth > 0:
                    case '}' when depth > 0:
                        depth--;
                        break;
                    case ';' when depth == 0:
                        return false;
                }
            }
            return true;
        }

        static string StripLeadingTrivia(string code)
        {
            var i = 0;
            while (i < code.Length)
            {
                if (char.IsWhiteSpace(code[i]))
                {
                    i++;
                    continue;
                }
                if (i + 1 < code.Length && code[i] == '/' && code[i + 1] == '/')
                {
                    i = SkipLineComment(code, i) + 1;
                    continue;
                }
                if (i + 1 < code.Length && code[i] == '/' && code[i + 1] == '*')
                {
                    i = SkipBlockComment(code, i) + 1;
                    continue;
                }
                break;
            }
            return i >= code.Length ? string.Empty : code.Substring(i);
        }

        /// A leading keyword makes the snippet a statement body even without a top-level `;`:
        /// `if (x) { }`, `for (;;) { }`, `return x`, `var x = 1`, `await Task.Delay(1)` (an await
        /// statement), `throw new Exception()`.
        static bool StartsWithStatementKeyword(string body)
        {
            foreach (var keyword in StatementKeywords)
            {
                if (body.Length < keyword.Length) continue;
                if (string.CompareOrdinal(body, 0, keyword, 0, keyword.Length) != 0) continue;
                if (body.Length == keyword.Length || !IsIdentifierPart(body[keyword.Length])) return true;
            }
            return false;
        }

        static readonly string[] StatementKeywords =
        {
            "return", "if", "else", "for", "foreach", "while", "do", "switch", "try", "catch",
            "finally", "throw", "using", "lock", "unsafe", "fixed", "checked", "unchecked",
            "var", "await", "yield", "class", "struct", "interface", "enum", "namespace",
            "void", "delegate", "goto", "break", "continue",
        };

        static int SkipQuoted(string code, int start)
        {
            var quote = code[start];
            var i = start + 1;
            // A verbatim string (""@"" prefix) treats a doubled quote as an escaped one.
            var verbatim = start > 0 && code[start - 1] == '@';
            while (i < code.Length)
            {
                var c = code[i];
                if (c == '\\' && !verbatim)
                {
                    i += 2;
                    continue;
                }
                if (c == quote)
                {
                    if (verbatim && i + 1 < code.Length && code[i + 1] == quote)
                    {
                        i += 2;
                        continue;
                    }
                    return i;
                }
                i++;
            }
            return code.Length - 1;
        }

        static int SkipLineComment(string code, int start)
        {
            var end = code.IndexOf('\n', start);
            return end < 0 ? code.Length - 1 : end - 1;
        }

        static int SkipBlockComment(string code, int start)
        {
            var end = code.IndexOf("*/", start + 2, StringComparison.Ordinal);
            return end < 0 ? code.Length - 1 : end + 1;
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
