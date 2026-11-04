using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityCliConnector
{
    /// <summary>
    /// Reflection index over the assemblies the editor loaded, built lazily and extended across
    /// calls so a bounded caller (compile-error hints) never pays for a full domain scan at once.
    ///
    /// Statics reset on domain reload, which is also what invalidates them: every Type here comes
    /// from the assembly set the reloaded domain holds. Assemblies loaded later without a reload
    /// (exec's compiled bodies) are excluded on purpose — see <see cref="IsEligible"/>.
    /// </summary>
    public static class ApiIndex
    {
        public readonly struct ScanStats
        {
            public int AssembliesIndexed { get; }
            public int TypesIndexed { get; }
            public bool Complete { get; }

            public ScanStats(int assembliesIndexed, int typesIndexed, bool complete)
            {
                AssembliesIndexed = assembliesIndexed;
                TypesIndexed = typesIndexed;
                Complete = complete;
            }
        }

        private static readonly Dictionary<Assembly, Type[]> s_TypesOf = new();
        private static readonly Dictionary<Assembly, int> s_RankOf = new();
        private static readonly Dictionary<Assembly, string> s_LocationOf = new();
        private static readonly List<Type> s_Types = new();
        private static List<Assembly> s_Eligible;
        private static int s_LoadedCount = -1;
        private static bool s_Complete;

        /// <summary>Index more assemblies until <paramref name="budgetMs"/> runs out. Returns the
        /// state after the work it managed to do; complete=false means an eligible assembly is
        /// still unindexed, so callers that can afford it should ask again.</summary>
        public static ScanStats EnsureIndexed(int budgetMs)
        {
            var deadline = new Deadline(budgetMs);
            var eligible = EligibleAssemblies();
            foreach (var assembly in eligible)
            {
                if (s_TypesOf.ContainsKey(assembly)) continue;
                if (deadline.Expired) break;
                IndexAssembly(assembly);
            }
            s_Complete = eligible.All(a => s_TypesOf.ContainsKey(a));
            return new ScanStats(s_TypesOf.Count, s_Types.Count, s_Complete);
        }

        public static IReadOnlyList<Type> Types => s_Types;
        public static int IndexedAssemblyCount => s_TypesOf.Count;

        /// <summary>Indexed assemblies in lookup priority order (Unity's core modules first, then
        /// project and package code), so a budgeted scan sees the likely answers first.</summary>
        public static IReadOnlyList<Assembly> IndexedAssemblies() =>
            s_TypesOf.Keys.OrderBy(a => RankOf(a)).ThenBy(a => a.GetName().Name, StringComparer.OrdinalIgnoreCase).ToList();

        public static Type[] TypesOf(Assembly assembly) =>
            s_TypesOf.TryGetValue(assembly, out var types) ? types : Array.Empty<Type>();

        public static int RankOf(Assembly assembly) =>
            s_RankOf.TryGetValue(assembly, out var rank) ? rank : 9;

        /// <summary>Types the query names, best match first. Only indexed assemblies can match,
        /// so call <see cref="EnsureIndexed"/> with a budget that fits the caller.</summary>
        public static IReadOnlyList<Type> FindTypes(string query, int limit) => FindTypes(query, limit, int.MaxValue);

        /// <summary>The same, but refusing matches looser than <paramref name="maxRank"/>
        /// (<see cref="ApiReflection.QueryForm.Rank"/>): 0 when the caller means "a type the query
        /// names exactly", larger when it wants typos suggested too.</summary>
        public static IReadOnlyList<Type> FindTypes(string query, int limit, int maxRank)
        {
            var form = new ApiReflection.QueryForm(query);
            if (form.IsEmpty || limit <= 0) return Array.Empty<Type>();

            var matches = new List<(int Rank, int AssemblyRank, int Visibility, Type Type)>();
            foreach (var type in s_Types)
            {
                var rank = form.Rank(type);
                if (rank < 0 || rank > maxRank) continue;
                matches.Add((rank, RankOf(type.Assembly), IsPublicType(type) ? 0 : 1, type));
                if (matches.Count >= 200) break;
            }
            return matches
                .OrderBy(m => m.Rank)
                .ThenBy(m => m.AssemblyRank)
                .ThenBy(m => m.Visibility)
                .ThenBy(m => ApiReflection.QualifiedName(m.Type).Length)
                .ThenBy(m => m.Type.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(m => m.Type)
                .ToList();
        }

        public static bool TryResolve(string query, out Type type)
        {
            var found = FindTypes(query, 1);
            type = found.Count > 0 ? found[0] : null;
            return type != null;
        }

        /// <summary>Members declared on <paramref name="type"/> that are callable from user code:
        /// public instance and static methods, properties, fields and events.</summary>
        public static IReadOnlyList<MemberInfo> PublicMembers(Type type)
        {
            if (type == null) return Array.Empty<MemberInfo>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly;
            try
            {
                var members = new List<MemberInfo>();
                members.AddRange(type.GetMethods(flags));
                members.AddRange(type.GetProperties(flags));
                members.AddRange(type.GetFields(flags));
                members.AddRange(type.GetEvents(flags));
                members.RemoveAll(m => m is MethodInfo mi && (mi.IsSpecialName || mi.IsPrivate));
                return members;
            }
            catch (Exception e) when (e is TypeLoadException || e is NotSupportedException || e is ArgumentException)
            {
                return Array.Empty<MemberInfo>();
            }
        }

        /// <summary>What a type offers its callers, in the order a reader expects: nested types,
        /// constructors, methods, properties, fields, events.</summary>
        public static IReadOnlyList<MemberInfo> PublicApi(Type type)
        {
            var members = new List<MemberInfo>(PublicMembers(type));
            if (type == null) return members;
            try
            {
                members.AddRange(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
                members.AddRange(type.GetNestedTypes(BindingFlags.Public));
            }
            catch (Exception e) when (e is TypeLoadException || e is NotSupportedException || e is ArgumentException)
            {
            }
            return members;
        }

        /// <summary>Every type in <paramref name="assembly"/> declaring a public member with this
        /// exact name (case-insensitive). The name in a compiler message is a typo, so the search
        /// name usually comes from a close match found elsewhere.</summary>
        public static IReadOnlyList<MemberInfo> FindMembersByName(Assembly assembly, string name)
        {
            if (assembly == null || string.IsNullOrEmpty(name)) return Array.Empty<MemberInfo>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly | BindingFlags.IgnoreCase;
            var found = new List<MemberInfo>();
            foreach (var type in TypesOf(assembly))
            {
                try
                {
                    foreach (var member in type.GetMember(name, flags))
                    {
                        if (member is MethodInfo mi && mi.IsSpecialName) continue;
                        found.Add(member);
                    }
                }
                catch (Exception e) when (e is TypeLoadException || e is NotSupportedException
                    || e is ArgumentException || e is AmbiguousMatchException)
                {
                    // One unusable type must not cost the caller the whole assembly.
                }
            }
            return found;
        }

        /// <summary>Types declaring a public member of this exact name, in lookup priority order.
        /// Bounded by <paramref name="budget"/>: a whole domain is ~47 000 types, and one type
        /// whose name lookup fails must not cost the caller the answer.</summary>
        public static List<Type> TypesWithMember(string memberName, int limit, Deadline budget)
        {
            var types = new List<Type>();
            if (string.IsNullOrEmpty(memberName) || limit <= 0) return types;
            var seen = new HashSet<Type>();
            foreach (var assembly in IndexedAssemblies())
            {
                if (budget.Expired || types.Count >= limit) break;
                foreach (var member in FindMembersByName(assembly, memberName))
                {
                    var declaring = member.DeclaringType;
                    if (declaring == null || !seen.Add(declaring)) continue;
                    types.Add(declaring);
                    if (types.Count >= limit) break;
                }
            }
            return types;
        }

        private static void IndexAssembly(Assembly assembly)
        {
            if (!s_RankOf.TryGetValue(assembly, out var rank))
            {
                var location = s_LocationOf.TryGetValue(assembly, out var known) ? known : assembly.Location;
                rank = Rank(assembly, location ?? "");
                s_RankOf[assembly] = rank;
            }
            Type[] types;
            try
            {
                types = assembly.GetTypes() ?? Array.Empty<Type>();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = (e.Types ?? Array.Empty<Type>()).Where(t => t != null).ToArray();
            }
            catch (Exception e) when (e is TypeLoadException || e is NotSupportedException || e is BadImageFormatException)
            {
                types = Array.Empty<Type>();
            }
            s_TypesOf[assembly] = types;
            s_Types.AddRange(types);
        }

        /// <summary>Eligible assemblies in lookup priority order. Cached: <see cref="Assembly.Location"/>
        /// is not a cheap property, and this runs on the editor's main thread inside a 100 ms budget.</summary>
        private static List<Assembly> EligibleAssemblies()
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            if (s_Eligible != null && loaded.Length == s_LoadedCount) return s_Eligible;

            var ranked = new List<(Assembly Assembly, int Rank)>();
            foreach (var assembly in loaded)
            {
                if (!IsEligible(assembly, out var location)) continue;
                s_LocationOf[assembly] = location;
                var rank = Rank(assembly, location);
                s_RankOf[assembly] = rank;
                ranked.Add((assembly, rank));
            }

            s_Eligible = ranked
                .OrderBy(a => a.Rank)
                .ThenBy(a => a.Assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
                .Select(a => a.Assembly)
                .ToList();
            s_LoadedCount = loaded.Length;
            return s_Eligible;
        }

        /// <summary>Reflection only sees what user code compiles against, so the editor's own
        /// throwaway assemblies are out: dynamic ones, and the bodies <c>exec</c> compiles to
        /// <c>Path.GetTempPath()/unity-cli-exec/&lt;8 hex&gt;.dll</c> — a random assembly name that
        /// would otherwise show up as a match for almost any query.</summary>
        private static bool IsEligible(Assembly assembly, out string location)
        {
            location = "";
            if (assembly.IsDynamic) return false;
            string name;
            try
            {
                name = assembly.GetName().Name;
                location = assembly.Location ?? "";
            }
            catch (Exception e) when (e is NotSupportedException || e is System.Security.SecurityException)
            {
                return false;
            }
            if (string.IsNullOrEmpty(name) || location.Length == 0) return false;
            if (name.StartsWith("__Cli", StringComparison.Ordinal)) return false;
            var path = location.Replace('\\', '/');
            if (path.Contains("/unity-cli-exec/")) return false;
            return true;
        }

        private static int Rank(Assembly assembly, string location)
        {
            var name = assembly.GetName().Name ?? "";
            if (name == "UnityEngine.CoreModule" || name == "UnityEditor.CoreModule") return 0;
            if (location.Replace('\\', '/').Contains("/Library/ScriptAssemblies/")) return 1;
            if (name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                name.StartsWith("UnityEditor", StringComparison.Ordinal)) return 2;
            if (name.StartsWith("System", StringComparison.Ordinal) || name == "mscorlib" ||
                name == "netstandard" || name.StartsWith("Microsoft", StringComparison.Ordinal)) return 3;
            return 4;
        }

        private static bool IsPublicType(Type type)
        {
            if (type.DeclaringType != null) return type.IsNestedPublic;
            return type.IsPublic;
        }

        // ─── Testing hooks ────────────────────────────────────
        // Internal seam for unit tests. Production code must not call these.

        internal static void ResetForTests()
        {
            s_TypesOf.Clear();
            s_RankOf.Clear();
            s_LocationOf.Clear();
            s_Types.Clear();
            s_Eligible = null;
            s_LoadedCount = -1;
            s_Complete = false;
        }
    }
}
