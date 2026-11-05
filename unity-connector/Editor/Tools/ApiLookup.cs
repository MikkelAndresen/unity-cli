using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(
        Name = "api",
        Description = "What a loaded type actually is: kind, namespace, assembly, base, interfaces, and the C# " +
                      "signature of every member or overload. Reflection over the assemblies the editor compiled, " +
                      "so the answer is what the project's code compiles against — not a PackageCache grep.",
        // A broken compile is exactly when the real API is wanted, and the loaded assemblies are the
        // last good ones; the gate would hide the answer behind the error it is there to explain.
        SkipCompilationGate = true)]
    public static class ApiLookup
    {
        public class Parameters
        {
            [ToolParameter("Type, or Type.Member: 'Vector3', 'Vector3.Lerp', 'Unity.Entities.BufferLookup<T>', 'List`1'")]
            public string Query { get; set; }

            [ToolParameter("Only assemblies whose name contains these (comma-separated, case-insensitive)")]
            public string Assembly { get; set; }

            [ToolParameter("Include the XML doc <summary> where the assembly ships its doc file (default: false)")]
            public bool Docs { get; set; }

            [ToolParameter("Maximum members listed per type (default 20)")]
            public int Limit { get; set; }

            [ToolParameter("Maximum types to describe when several match (default 5)")]
            public int MaxTypes { get; set; }

            [ToolParameter("Milliseconds the assembly scan may take (default 1500)")]
            public int Budget { get; set; }
        }

        private const int DefaultBudgetMs = 1500;
        private const int DefaultMemberLimit = 20;
        private const int DefaultMaxTypes = 5;

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            var query = p.Get("query") ?? p.Get("type")
                ?? (p.GetRaw("args") as JArray)?[0]?.ToString();
            if (string.IsNullOrWhiteSpace(query))
                return new ErrorResponse("'api' needs a type, optionally Type.Member — e.g. api Vector3.Lerp");

            int memberLimit = Math.Max(1, p.GetInt("limit") ?? DefaultMemberLimit);
            int maxTypes = Math.Max(1, p.GetInt("max_types") ?? p.GetInt("maxTypes") ?? DefaultMaxTypes);
            int budget = p.GetInt("budget") ?? DefaultBudgetMs;
            bool docs = p.GetBool("docs");
            var assemblyFilter = SplitList(p.Get("assembly"));

            // The query is either a type, or a type plus a member. Only the loosest reading (a typo
            // of a type name) is allowed to match by edit distance, so a member name never gets
            // mistaken for a near-miss type.
            var stats = ApiIndex.EnsureIndexed(budget);
            var scanBudget = new Deadline(Math.Max(budget / 2, 300));

            var resolution = Resolve(query, assemblyFilter, maxTypes, scanBudget);
            if (resolution.types.Count == 0)
                return new SuccessResponse(resolution.note ?? $"Nothing named '{query}' is in the loaded assemblies.", new
                {
                    query,
                    member = resolution.member,
                    matched_by = resolution.matchedBy,
                    // No type matched: the caller asked for something that does not exist, which is an
                    // answer the CLI reports as a failure rather than as a lookup result.
                    exact = false,
                    note = resolution.note,
                    closest_types = resolution.closest,
                    matches = 0,
                    types = Array.Empty<object>(),
                    index = IndexPayload(stats),
                });

            var described = resolution.types
                .Select(t => Describe(t, resolution.member, docs, memberLimit))
                .Where(d => d != null)
                .ToList();

            var memberExact = resolution.exact && described.All(d => d.memberExact);
            var exact = resolution.exact && memberExact;
            var note = exact ? null : MemberNote(query, resolution, described);
            var listedMembers = described.Sum(d => d.listed);
            var summary = note ?? SummaryLine(query, described, resolution.member, resolution.matchedBy, listedMembers);
            return new SuccessResponse(summary, new
            {
                query,
                member = resolution.member,
                matched_by = resolution.matchedBy,
                // False marks a nearest-match answer: the members or types listed are suggestions, not
                // what the query named, and a caller must not treat them as the API it asked for.
                exact,
                note,
                closest_types = resolution.closest,
                matches = described.Count,
                summary,
                types = described.Select(d => d.payload).ToList(),
                index = IndexPayload(stats),
            });
        }

        /// The sentence for a fuzzy answer: which name did not exist, and what is closest. Built from
        /// the described types' own fuzzy matches so the suggestion is the one actually listed.
        private static string MemberNote(string query, Resolution resolution, List<DescribeResult> described)
        {
            if (!resolution.exact) return resolution.note ?? $"no exact match for '{query}'";

            var owners = string.Join(", ", described.Select(d => d.name));
            var closest = described.SelectMany(d => d.closestMembers)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
            var tail = closest.Count > 0 ? "; closest: " + string.Join(", ", closest) : "";
            return $"no member named '{resolution.member}' on {owners}{tail}";
        }

        /// <summary>Which types to describe, and whether a member filter applies: the exact type the
        /// query names, else the type left of the last dot with the member on the right, else the
        /// types declaring that member, else the closest types to the whole query.</summary>
        private static Resolution Resolve(string query, List<string> assemblyFilter, int maxTypes, Deadline scanBudget)
        {
            var accept = AssemblyPredicate(assemblyFilter);
            var whole = ApiIndex.FindTypes(query, maxTypes, 0, accept);
            if (whole.Count > 0) return new Resolution { types = whole.ToList(), matchedBy = "type" };

            var lastDot = query.LastIndexOf('.');
            if (lastDot > 0)
            {
                var memberQuery = query.Substring(lastDot + 1).Trim();
                var typeQuery = query.Substring(0, lastDot).Trim();
                if (memberQuery.Length > 0)
                {
                    var owner = ApiIndex.FindTypes(typeQuery, maxTypes, 0, accept);
                    if (owner.Count > 0)
                        return new Resolution { types = owner.ToList(), member = memberQuery, matchedBy = "type.member" };

                    var declaring = ApiIndex.TypesWithMember(memberQuery, maxTypes, scanBudget, accept);
                    if (declaring.Count > 0)
                        return new Resolution { types = declaring, member = memberQuery, matchedBy = "member" };
                }
            }
            else if (query.Length > 2)
            {
                // A bare name that is no type is most likely a member: "api GetSingletonBuffer".
                var declaring = ApiIndex.TypesWithMember(query, maxTypes, scanBudget, accept);
                if (declaring.Count > 0) return new Resolution { types = declaring, member = query, matchedBy = "member" };
            }

            // Nothing names this query: name the closest types as suggestions only. Describing them
            // would present a list of unrelated types as if it were the answer.
            var closest = ApiIndex.FindTypes(query, 3, int.MaxValue, accept)
                .Select(ApiReflection.QualifiedName)
                .ToList();
            return new Resolution
            {
                matchedBy = "closest_types",
                exact = false,
                closest = closest,
                note = $"no type or member named '{query}' in the loaded assemblies" +
                       (closest.Count > 0 ? "; closest: " + string.Join(", ", closest) : ""),
            };
        }

        private static Func<Type, bool> AssemblyPredicate(List<string> assemblyFilter) =>
            assemblyFilter.Count == 0 ? null : t => AssemblyMatches(t.Assembly, assemblyFilter);

        private sealed class Resolution
        {
            public List<Type> types { get; set; } = new List<Type>();
            public string member { get; set; }
            public string matchedBy { get; set; }
            // False when the answer is a nearest-match fallback rather than what the query named.
            public bool exact = true;
            // The closest names when the query resolved to nothing at all.
            public List<string> closest = new List<string>();
            public string note { get; set; }
        }

        private static DescribeResult Describe(Type type, string memberQuery, bool docs, int memberLimit)
        {
            var all = ApiIndex.PublicApi(type);

            var exactMatches = string.IsNullOrEmpty(memberQuery)
                ? all
                : all.Where(m => NameMatches(m.Name, memberQuery)).ToList();
            var memberExact = exactMatches.Count > 0;
            // A member filter that matches nothing is a typo worth answering, not an empty result.
            if (string.IsNullOrEmpty(memberQuery) && exactMatches.Count == 0) return null;
            var matching = exactMatches;
            List<string> closestMembers = new();
            if (!string.IsNullOrEmpty(memberQuery) && !memberExact)
            {
                matching = all.Where(m => ApiReflection.NameScore(m.Name, memberQuery) > 0)
                    .OrderByDescending(m => ApiReflection.NameScore(m.Name, memberQuery))
                    .ToList();
                closestMembers = matching.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
            }
            if (matching.Count == 0 && all.Count == 0) return null;

            var ordered = matching
                .OrderBy(m => KindOrder(m))
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => ApiReflection.SignatureOf(m), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var listed = ordered.Take(memberLimit)
                .Select(m => MemberPayload(m, docs))
                .ToList();

            var interfaces = type.GetInterfaces();
            var payload = (object)new
            {
                type = ApiReflection.QualifiedName(type),
                kind = ApiReflection.TypeKind(type),
                @namespace = string.IsNullOrEmpty(type.Namespace) ? null : type.Namespace,
                assembly = type.Assembly.GetName().Name,
                base_type = type.BaseType != null ? ApiReflection.CSharpName(type.BaseType) : null,
                interfaces = interfaces.Select(ApiReflection.CSharpName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                generic_arity = type.IsGenericType ? type.GetGenericArguments().Length : 0,
                obsolete = ObsoletePayload(type),
                docs = docs && XmlDocLookup.TryGetTypeSummary(type, out var typeSummary) ? typeSummary : null,
                member_filter = string.IsNullOrEmpty(memberQuery) ? null : memberQuery,
                member_total = matching.Count,
                members_listed = listed.Count,
                counts = Counts(all),
                members = listed,
            };
            return new DescribeResult
            {
                payload = payload,
                listed = listed.Count,
                total = matching.Count,
                kind = ApiReflection.TypeKind(type),
                name = ApiReflection.QualifiedName(type),
                assembly = type.Assembly.GetName().Name,
                memberExact = memberExact,
                closestMembers = closestMembers,
            };
        }

        private static object MemberPayload(MemberInfo member, bool docs) => new
        {
            kind = ApiReflection.KindOf(member),
            name = member.Name,
            signature = ApiReflection.SignatureOf(member),
            obsolete = ObsoletePayload(member),
            docs = docs && XmlDocLookup.TryGetMemberSummary(member, out var summary) ? summary : null,
        };

        private static object ObsoletePayload(MemberInfo member)
        {
            var info = ApiReflection.ObsoleteOf(member);
            if (info == null) return null;
            return new
            {
                message = info.Message,
                is_error = info.IsError,
                replacement = info.Replacement,
            };
        }

        private static object Counts(IReadOnlyList<MemberInfo> members) => new
        {
            methods = members.Count(m => m is MethodInfo),
            constructors = members.Count(m => m is ConstructorInfo),
            properties = members.Count(m => m is PropertyInfo),
            fields = members.Count(m => m is FieldInfo),
            events = members.Count(m => m is EventInfo),
            nested_types = members.Count(m => m is Type),
        };

        private static object IndexPayload(ApiIndex.ScanStats stats) => new
        {
            assemblies_indexed = stats.AssembliesIndexed,
            types_indexed = stats.TypesIndexed,
            complete = stats.Complete,
        };

        private static string SummaryLine(string query, List<DescribeResult> described, string memberQuery, string matchedBy, int listedMembers)
        {
            var what = string.IsNullOrEmpty(memberQuery) ? "members" : $"'{memberQuery}' matches";
            if (described.Count == 1)
            {
                var only = described[0];
                var detail = only.listed < only.total ? $" of {only.total} {what} listed" : $" {what}";
                return $"{only.name} ({only.kind}, {only.assembly}): {only.listed}{detail}";
            }
            var names = string.Join(", ", described.Select(d => d.name));
            return $"{described.Count} types match '{query}' ({matchedBy}): {names}" +
                $" ({listedMembers} {what} in total)";
        }

        private static bool NameMatches(string name, string query)
        {
            if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return true;
            var stripped = ApiReflection.StripArity(query);
            return !string.IsNullOrEmpty(stripped) &&
                   string.Equals(name, stripped, StringComparison.OrdinalIgnoreCase);
        }

        private static bool AssemblyMatches(Assembly assembly, List<string> filter)
        {
            var name = assembly.GetName().Name ?? "";
            return filter.Any(f => name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static List<string> SplitList(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new List<string>();
            return value.Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        private static int KindOrder(MemberInfo member)
        {
            switch (member)
            {
                case Type: return 0;
                case ConstructorInfo: return 1;
                case MethodInfo: return 2;
                case PropertyInfo: return 3;
                case FieldInfo: return 4;
                case EventInfo: return 5;
                default: return 6;
            }
        }

        private sealed class DescribeResult
        {
            public object payload { get; set; }
            public int listed { get; set; }
            public int total { get; set; }
            public string kind { get; set; }
            public string name { get; set; }
            public string assembly { get; set; }
            public bool memberExact { get; set; } = true;
            public List<string> closestMembers { get; set; } = new List<string>();
        }
    }
}
