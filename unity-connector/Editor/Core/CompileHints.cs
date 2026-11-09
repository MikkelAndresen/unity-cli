using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityCliConnector
{
    /// <summary>
    /// Turns the codes an agent trips most often — a member that is not there, a type that is not
    /// there, an API that is obsolete — into the answer it was about to go looking for: the closest
    /// real members and types, read off the assemblies its own code compiles against.
    ///
    /// Bounded on purpose: <see cref="MaxCandidates"/> candidates, <see cref="MaxHintChars"/>
    /// characters, and one <see cref="HintBudget"/> shared by all diagnostics of one call. Candidate
    /// lookups are cached per domain, so the repeat verifies of a fix loop cost nothing.
    /// </summary>
    public static class CompileHints
    {
        public const int DefaultBudgetMs = 100;
        public const int MaxCandidates = 3;
        public const int MaxHintChars = 200;

        private static readonly HashSet<string> SupportedCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "CS0117", "CS1061", "CS0246", "CS0234", "CS0619", "CS0618",
        };

        // Roslyn quotes names with ', the old Mono compiler opened them with a backtick and closed
        // with a quote, so both delimiters are accepted on either side.
        private const string Q = "['`\"]";
        private const string N = "[^'`\"]+";

        private static readonly Regex MemberNotFoundPattern = new(
            Q + "(?<type>" + N + ")" + Q + @"\s+does not contain a definition for\s+" +
            Q + "(?<member>" + N + ")" + Q, RegexOptions.Compiled);

        private static readonly Regex TypeNotFoundPattern = new(
            @"type or namespace name\s*" + Q + "(?<name>" + N + ")" + Q + @"\s+could not be found",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NamespaceMemberPattern = new(
            @"type or namespace name\s*" + Q + "(?<name>" + N + ")" + Q +
            @"\s+does not exist in the namespace\s*" + Q + "(?<ns>" + N + ")" + Q,
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Dictionary<string, string> s_HintCache = new();
        private static readonly Dictionary<string, List<MemberInfo>> s_ScanCache = new();

        public static bool Supports(string code) => !string.IsNullOrEmpty(code) && SupportedCodes.Contains(code);

        /// <summary>The hint for one diagnostic, or null when nothing close was found. Pass one
        /// deadline across the diagnostics of a single call so the total stays bounded.</summary>
        public static string ForDiagnostic(string code, string message, Deadline budget)
        {
            if (!Supports(code) || string.IsNullOrEmpty(message)) return null;
            var key = code + "|" + message;
            if (s_HintCache.TryGetValue(key, out var cached) && (cached != null || ApiIndex.Complete))
                return cached;

            var hint = Build(code, message, budget);
            // A partial index or a spent budget cannot prove absence: the answer may only arrive
            // once more assemblies are indexed on a later call.
            if (hint != null || (ApiIndex.Complete && !budget.Expired))
            {
                if (s_HintCache.Count > 512) s_HintCache.Clear();
                s_HintCache[key] = hint;
            }
            return hint;
        }

        public static string ForDiagnostic(string code, string message, int budgetMs = DefaultBudgetMs) =>
            ForDiagnostic(code, message, new Deadline(budgetMs));

        private static string Build(string code, string message, Deadline budget)
        {
            var target = ParseTarget(code, message);
            if (target == null) return null;

            if (!budget.Expired) ApiIndex.EnsureIndexed(budget.RemainingMs);

            switch (code.ToUpperInvariant())
            {
                case "CS0117":
                case "CS1061":
                    return MemberHint(target, budget);
                case "CS0246":
                case "CS0234":
                    return TypeHint(target, budget);
                case "CS0619":
                case "CS0618":
                    return ObsoleteHint(target, budget);
                default:
                    return null;
            }
        }

        // ─── Message parsing ──────────────────────────────────

        public sealed class Target
        {
            public string Code { get; set; }
            public string TypeQuery { get; set; }
            public string MemberQuery { get; set; }
            public string NamespaceQuery { get; set; }
            public string ObsoleteMessage { get; set; }
            public string Replacement { get; set; }
        }

        /// <summary>Lifts the type and member the compiler is complaining about out of its message.
        /// Returns null for a message this code does not recognize (a changed compiler, a variant
        /// wording): that is the same as having no hint.</summary>
        internal static Target ParseTarget(string code, string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            var head = message;
            var newline = head.IndexOf('\n');
            if (newline >= 0) head = head.Substring(0, newline);

            switch ((code ?? "").ToUpperInvariant())
            {
                case "CS0117":
                case "CS1061":
                {
                    var match = MemberNotFoundPattern.Match(head);
                    if (!match.Success) return null;
                    return new Target
                    {
                        Code = code,
                        TypeQuery = match.Groups["type"].Value.Trim(),
                        MemberQuery = StripGenerics(match.Groups["member"].Value.Trim()),
                    };
                }

                case "CS0246":
                {
                    var match = TypeNotFoundPattern.Match(head);
                    if (!match.Success) return null;
                    return new Target { Code = code, TypeQuery = match.Groups["name"].Value.Trim() };
                }

                case "CS0234":
                {
                    var match = NamespaceMemberPattern.Match(head);
                    if (!match.Success) return null;
                    return new Target
                    {
                        Code = code,
                        TypeQuery = match.Groups["name"].Value.Trim(),
                        NamespaceQuery = match.Groups["ns"].Value.Trim(),
                    };
                }

                case "CS0619":
                case "CS0618":
                {
                    // "'Camera.stereoMirrorMode' is obsolete: 'Property ... is no longer supported.'"
                    // The member side may be a type ("'BufferFromEntity<T>' is obsolete: ..."), and
                    // the message side carries the replacement Unity's API updater renames with.
                    var split = head.IndexOf("is obsolete", StringComparison.OrdinalIgnoreCase);
                    if (split < 0) return null;

                    var member = Unquote(head.Substring(0, split).Trim());
                    var tail = head.Substring(split + "is obsolete".Length).Trim();
                    if (tail.StartsWith(":")) tail = tail.Substring(1).Trim();
                    var obsoleteMessage = Unquote(tail);

                    if (member.Length == 0 && obsoleteMessage.Length == 0) return null;
                    var target = new Target
                    {
                        Code = code,
                        ObsoleteMessage = obsoleteMessage.Length > 0 ? obsoleteMessage : null,
                        Replacement = ApiReflection.ParseReplacement(obsoleteMessage),
                    };

                    if (member.EndsWith("()", StringComparison.Ordinal))
                        member = member.Substring(0, member.Length - 2);
                    var lastDot = member.LastIndexOf('.');
                    if (lastDot > 0)
                    {
                        target.TypeQuery = member.Substring(0, lastDot).Trim();
                        target.MemberQuery = StripGenerics(member.Substring(lastDot + 1).Trim());
                    }
                    else
                    {
                        target.TypeQuery = StripGenerics(member.Trim());
                    }
                    return target;
                }

                default:
                    return null;
            }
        }

        private static string Unquote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var trimmed = value.Trim();
            if (trimmed.Length < 2) return trimmed;
            var first = trimmed[0];
            var last = trimmed[trimmed.Length - 1];
            if ((first == '\'' || first == '`' || first == '"') && (last == '\'' || last == '"'))
                return trimmed.Substring(1, trimmed.Length - 2).Trim();
            return trimmed;
        }

        private static string StripGenerics(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var angle = name.IndexOf('<');
            return angle > 0 ? name.Substring(0, angle).Trim() : name.Trim();
        }

        // ─── Candidate gathering ──────────────────────────────

        private static string MemberHint(Target target, Deadline budget)
        {
            if (string.IsNullOrEmpty(target.MemberQuery)) return null;
            var found = GatherMembers(target.TypeQuery, target.MemberQuery, budget);
            if (found.Count == 0) return null;
            return JoinCapped(found.Select(ApiReflection.ShortSignature), MaxHintChars, "closest: ");
        }

        private static string TypeHint(Target target, Deadline budget)
        {
            var query = string.IsNullOrEmpty(target.NamespaceQuery)
                ? target.TypeQuery
                : target.NamespaceQuery + "." + target.TypeQuery;
            if (string.IsNullOrEmpty(query)) return null;

            var types = ApiIndex.FindTypes(query, MaxCandidates);
            if (types.Count == 0) return null;
            // The kind is left out: for "which type did you mean", the namespace and the assembly
            // are what tell the candidates apart, and 200 characters has to cover three of them.
            return JoinCapped(
                types.Select(t => ApiReflection.QualifiedName(t) + " (" + t.Assembly.GetName().Name + ")"),
                MaxHintChars, "closest types: ");
        }

        /// <summary>An obsolete API's hint is its replacement, resolved to a real signature where the
        /// notice names one. Without a name in the notice there is nothing to suggest that the
        /// diagnostic line does not already say, so no hint is better than a guess.</summary>
        private static string ObsoleteHint(Target target, Deadline budget)
        {
            if (string.IsNullOrEmpty(target.Replacement)) return null;
            var resolved = ResolveReplacement(target.Replacement, target.TypeQuery, budget);
            return JoinCapped(new[] { resolved ?? target.Replacement }, MaxHintChars, "use ");
        }

        /// <summary>The real member a deprecation notice points at, spelled the way the caller must
        /// write it: "UnityEngine.Object.GetEntityId()". Null when the notice is prose
        /// ("please use single pass stereo rendering instead") rather than a name.
        /// <paramref name="ownerQuery"/> is the type the obsolete member lives on: a notice usually
        /// names a sibling of it, which is a far cheaper lookup than scanning the domain.</summary>
        private static string ResolveReplacement(string replacement, string ownerQuery, Deadline budget)
        {
            var name = replacement;
            var paren = name.IndexOf('(');
            if (paren > 0) name = name.Substring(0, paren);
            name = StripGenerics(name.Trim());
            if (name.Length == 0 || !Regex.IsMatch(name, @"^[\w.]+$")) return null;

            var lastDot = name.LastIndexOf('.');
            var memberName = lastDot > 0 ? name.Substring(lastDot + 1) : name;
            var typeQuery = lastDot > 0 ? name.Substring(0, lastDot) : ownerQuery;
            var wantedType = ApiReflection.StripArity(typeQuery != null
                ? typeQuery.Substring(typeQuery.LastIndexOf('.') + 1)
                : "");

            if (typeQuery != null)
            {
                foreach (var type in ApiIndex.FindTypes(typeQuery, 2))
                {
                    foreach (var member in ApiIndex.PublicMembers(type))
                    {
                        if (string.Equals(member.Name, memberName, StringComparison.OrdinalIgnoreCase))
                            return QualifiedShortSignature(member);
                    }
                }
            }

            // A bare name in a notice is usually a sibling of the obsolete member, so a member with
            // that name anywhere in the indexed assemblies is the answer the caller wants.
            foreach (var assembly in ApiIndex.IndexedAssemblies())
            {
                if (budget.Expired) break;
                foreach (var member in ScanAssembly(assembly, memberName))
                {
                    var declaring = member.DeclaringType;
                    if (wantedType.Length > 0 && declaring != null &&
                        string.Equals(ApiReflection.StripArity(declaring.Name), wantedType, StringComparison.OrdinalIgnoreCase))
                        return QualifiedShortSignature(member);
                }
            }
            foreach (var assembly in ApiIndex.IndexedAssemblies())
            {
                if (budget.Expired) break;
                var found = ScanAssembly(assembly, memberName);
                if (found.Count > 0) return QualifiedShortSignature(found[0]);
            }
            return null;
        }

        private static string QualifiedShortSignature(MemberInfo member)
        {
            var declaring = member.DeclaringType;
            if (declaring == null) return ApiReflection.ShortSignature(member);
            var prefix = ApiReflection.QualifiedName(declaring);
            if (member is MethodBase method)
            {
                if (method.IsConstructor) return prefix + "(" + ParameterTypes(method) + ")";
                if (method is MethodInfo generic && generic.IsGenericMethod)
                {
                    return prefix + "." + generic.Name + "<" +
                        string.Join(",", generic.GetGenericArguments().Select(a => a.Name)) + ">(" +
                        ParameterTypes(generic) + ")";
                }
                return prefix + "." + method.Name + "(" + ParameterTypes(method) + ")";
            }
            return prefix + "." + member.Name;
        }

        private static string ParameterTypes(MethodBase method) =>
            string.Join(",", method.GetParameters().Select(p => ApiReflection.CSharpName(p.ParameterType)));

        /// <summary>Closest members: first the ones on the type the compiler named, then the same
        /// name on other types, because a rename usually moves the member rather than misspelling
        /// it.</summary>
        private static List<MemberInfo> GatherMembers(string typeQuery, string memberQuery, Deadline budget)
        {
            var scored = new List<(int Source, int Score, int Length, MemberInfo Member)>();

            foreach (var type in ApiIndex.FindTypes(typeQuery, 3))
            {
                foreach (var member in ApiIndex.PublicMembers(type))
                {
                    var score = ApiReflection.NameScore(member.Name, memberQuery);
                    if (score <= 0) continue;
                    scored.Add((0, score, member.Name.Length, member));
                }
            }

            // The typed name plus the two closest real names: the answer is often on another type,
            // and searching that type is cheap once the correct spelling is known.
            var searchNames = new List<string> { memberQuery };
            foreach (var best in scored.OrderByDescending(s => s.Score).ThenBy(s => s.Length))
            {
                if (searchNames.Count >= 3) break;
                if (!searchNames.Contains(best.Member.Name, StringComparer.OrdinalIgnoreCase))
                    searchNames.Add(best.Member.Name);
            }

            foreach (var assembly in ApiIndex.IndexedAssemblies())
            {
                if (budget.Expired || scored.Count >= 40) break;
                foreach (var name in searchNames)
                {
                    foreach (var member in ScanAssembly(assembly, name))
                    {
                        var score = ApiReflection.NameScore(member.Name, memberQuery);
                        if (score <= 0) continue;
                        scored.Add((1, Math.Min(score, 85), member.Name.Length, member));
                    }
                }
            }

            var result = new List<MemberInfo>();
            var seen = new HashSet<string>();
            foreach (var entry in scored
                         .OrderBy(s => s.Source)
                         .ThenByDescending(s => s.Score)
                         .ThenBy(s => s.Length)
                         .ThenBy(s => ApiReflection.ShortSignature(s.Member), StringComparer.OrdinalIgnoreCase))
            {
                if (result.Count >= MaxCandidates) break;
                if (!seen.Add(ApiReflection.ShortSignature(entry.Member))) continue;
                result.Add(entry.Member);
            }
            return result;
        }

        private static List<MemberInfo> ScanAssembly(Assembly assembly, string name)
        {
            var key = assembly.GetName().Name + "|" + name.ToLowerInvariant();
            if (s_ScanCache.TryGetValue(key, out var cached)) return cached;
            var found = ApiIndex.FindMembersByName(assembly, name).ToList();
            if (s_ScanCache.Count > 256) s_ScanCache.Clear();
            s_ScanCache[key] = found;
            return found;
        }

        // ─── Formatting ───────────────────────────────────────

        /// <summary>Prefix plus a comma-separated run of candidates, never longer than
        /// <paramref name="maxChars"/>: candidates that do not fit are dropped and counted.</summary>
        internal static string JoinCapped(IReadOnlyList<string> items, int maxChars, string prefix)
        {
            if (items == null || items.Count == 0) return "";
            for (var shown = items.Count; shown > 0; shown--)
            {
                var joined = string.Join(", ", items.Take(shown));
                var omitted = items.Count - shown;
                var tail = omitted > 0 ? " (+" + omitted + " more)" : "";
                if (prefix.Length + joined.Length + tail.Length <= maxChars)
                    return prefix + joined + tail;
            }
            var first = items[0];
            return first.Length + prefix.Length <= maxChars
                ? prefix + first
                : prefix + first.Substring(0, Math.Max(0, maxChars - prefix.Length));
        }

        internal static string JoinCapped(IEnumerable<string> items, int maxChars, string prefix) =>
            JoinCapped(items.ToList(), maxChars, prefix);

        // ─── Testing hooks ────────────────────────────────────
        // Internal seam for unit tests. Production code must not call these.

        internal static void ResetForTests()
        {
            s_HintCache.Clear();
            s_ScanCache.Clear();
        }
    }
}
