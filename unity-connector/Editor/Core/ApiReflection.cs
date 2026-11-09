using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityCliConnector
{
    /// <summary>
    /// C#-shaped names and signatures for reflection results, plus the query matching the `api`
    /// tool and the compile-error hints share. Pure: no Unity APIs and no live state, so the tests
    /// can call it on any type.
    /// </summary>
    public static class ApiReflection
    {
        private static readonly Dictionary<Type, string> Keywords = new()
        {
            [typeof(void)] = "void",
            [typeof(object)] = "object",
            [typeof(string)] = "string",
            [typeof(bool)] = "bool",
            [typeof(byte)] = "byte",
            [typeof(sbyte)] = "sbyte",
            [typeof(short)] = "short",
            [typeof(ushort)] = "ushort",
            [typeof(int)] = "int",
            [typeof(uint)] = "uint",
            [typeof(long)] = "long",
            [typeof(ulong)] = "ulong",
            [typeof(float)] = "float",
            [typeof(double)] = "double",
            [typeof(decimal)] = "decimal",
            [typeof(char)] = "char",
        };

        // "Use X instead" / "please use X instead", and the "(UnityUpgradable) -> X" marker Unity's
        // API updater renames with. Both name a replacement member.
        private static readonly Regex UseInsteadPattern =
            new(@"\buse\s+(?<x>[\w.<>+`\[\] ]+?)\s+instead\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UpgradablePattern =
            new(@"\(UnityUpgradable\)\s*->\s*(?<x>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ─── Type names ───────────────────────────────────────

        /// <summary>"int", "string", "List&lt;T&gt;", "T[]", "int?", "Outer.Inner" — never the
        /// reflection spelling (`1) that neither C# source nor compiler messages use.</summary>
        public static string CSharpName(Type type)
        {
            if (type == null) return "?";
            if (type.IsGenericParameter) return type.Name;
            if (Keywords.TryGetValue(type, out var keyword)) return keyword;
            if (type.IsByRef) return CSharpName(type.GetElementType());
            if (type.IsArray)
                return CSharpName(type.GetElementType()) + "[" + new string(',', Math.Max(0, type.GetArrayRank() - 1)) + "]";
            if (type.IsPointer) return CSharpName(type.GetElementType()) + "*";
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                return CSharpName(type.GetGenericArguments()[0]) + "?";

            var name = type.DeclaringType != null
                ? CSharpName(type.DeclaringType) + "." + StripArity(type.Name)
                : StripArity(type.Name);
            if (type.IsGenericType)
                name += "<" + string.Join(", ", type.GetGenericArguments().Select(CSharpName)) + ">";
            return name;
        }

        /// <summary>Namespace-qualified C# name: "UnityEngine.Vector3",
        /// "System.Collections.Generic.List&lt;T&gt;".</summary>
        public static string QualifiedName(Type type)
        {
            if (type == null) return "?";
            var ns = type.Namespace;
            return string.IsNullOrEmpty(ns) ? CSharpName(type) : ns + "." + CSharpName(type);
        }

        public static string TypeKind(Type type)
        {
            if (type == null) return "unknown";
            if (type.IsEnum) return "enum";
            if (type.IsInterface) return "interface";
            if (typeof(MulticastDelegate).IsAssignableFrom(type)) return "delegate";
            if (type.IsValueType) return "struct";
            if (type.IsAbstract && type.IsSealed) return "static class";
            if (type.IsAbstract) return "abstract class";
            return "class";
        }

        public static string StripArity(string name)
        {
            if (name == null) return "";
            var tick = name.IndexOf('`');
            return tick < 0 ? name : name.Substring(0, tick);
        }

        // ─── Query matching ───────────────────────────────────

        /// <summary>"BufferLookup", "BufferLookup&lt;T&gt;" and "BufferLookup`1" all normalize to
        /// "BufferLookup"; a dotted query keeps its namespace prefix.</summary>
        public static string NormalizeQuery(string query)
        {
            if (query == null) return "";
            var q = query.Trim();
            var angle = q.IndexOf('<');
            if (angle > 0) q = q.Substring(0, angle);
            var tick = q.IndexOf('`');
            if (tick > 0) q = q.Substring(0, tick);
            return q.Trim();
        }

        public static bool QueryMatches(Type type, string query) => new QueryForm(query).Matches(type);

        /// <summary>Lower sorts first; negative means the query does not match at all.</summary>
        public static int QueryRank(Type type, string query) => new QueryForm(query).Rank(type);

        /// <summary>A type query cut up once so scanning tens of thousands of types costs string
        /// comparisons instead of allocations: <see cref="QualifiedName"/> per type would not fit a
        /// 100 ms hint budget.</summary>
        public readonly struct QueryForm
        {
            public string Normalized { get; }
            public string LastSegment { get; }
            public string NamespacePart { get; }

            public QueryForm(string query)
            {
                Normalized = NormalizeQuery(query);
                var lastDot = Normalized.LastIndexOf('.');
                if (lastDot < 0)
                {
                    LastSegment = Normalized;
                    NamespacePart = "";
                }
                else
                {
                    LastSegment = Normalized.Substring(lastDot + 1);
                    NamespacePart = Normalized.Substring(0, lastDot);
                }
            }

            public bool IsEmpty => LastSegment.Length == 0;

            public bool Matches(Type type) => Rank(type) >= 0;

            /// <summary>0 for a type the query names, 1 for a prefix of it, 2 for something within
            /// edit distance of it (a typo); negative for no match.</summary>
            public int Rank(Type type)
            {
                if (type == null || IsEmpty) return -1;
                var simple = StripArity(type.Name);
                if (!string.Equals(simple, LastSegment, StringComparison.OrdinalIgnoreCase))
                {
                    if (!(simple.StartsWith(LastSegment, StringComparison.OrdinalIgnoreCase) ||
                          LastSegment.StartsWith(simple, StringComparison.OrdinalIgnoreCase)))
                    {
                        var allowed = Math.Max(2, Math.Min(simple.Length, LastSegment.Length) / 3);
                        if (EditDistance(simple, LastSegment, allowed) < 0) return -1;
                        return NamespaceMatches(type) ? 3 : 4;
                    }
                    return NamespaceMatches(type) ? 1 : 2;
                }
                if (NamespacePart.Length == 0 || NamespaceMatches(type)) return 0;
                // Right name, namespace the query did not ask for: still a candidate, just later.
                return 1;
            }

            private bool NamespaceMatches(Type type)
            {
                if (NamespacePart.Length == 0) return true;
                var ns = type.Namespace ?? "";
                if (string.Equals(ns, NamespacePart, StringComparison.OrdinalIgnoreCase)) return true;
                if (ns.EndsWith("." + NamespacePart, StringComparison.OrdinalIgnoreCase)) return true;
                // "Foo.Bar" also addresses Bar nested in Foo, wherever Foo lives.
                for (var declaring = type.DeclaringType; declaring != null; declaring = declaring.DeclaringType)
                {
                    var chain = QualifiedName(declaring);
                    if (string.Equals(chain, NamespacePart, StringComparison.OrdinalIgnoreCase) ||
                        chain.EndsWith("." + NamespacePart, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
        }

        // ─── Member signatures ────────────────────────────────

        public static string KindOf(MemberInfo member)
        {
            switch (member)
            {
                case ConstructorInfo: return "constructor";
                case MethodInfo: return "method";
                case PropertyInfo: return "property";
                case FieldInfo: return "field";
                case EventInfo: return "event";
                case Type: return "nested type";
                default: return member == null ? "" : member.MemberType.ToString().ToLowerInvariant();
            }
        }

        public static string SignatureOf(MemberInfo member)
        {
            switch (member)
            {
                case ConstructorInfo c: return FormatConstructor(c);
                case MethodInfo m: return FormatMethod(m);
                case PropertyInfo p: return FormatProperty(p);
                case FieldInfo f: return FormatField(f);
                case EventInfo e: return FormatEvent(e);
                case Type t: return TypeKind(t) + " " + CSharpName(t);
                default: return member?.Name ?? "";
            }
        }

        /// <summary>Hint-shaped: "Vector3.Lerp(Vector3,Vector3,float)", "Transform.position" — no
        /// modifiers, return type or parameter names, because a hint has only ~200 characters.</summary>
        public static string ShortSignature(MemberInfo member)
        {
            if (member == null) return "";
            var declaring = member.DeclaringType;
            var owner = declaring != null ? StripArity(declaring.Name) : "";
            switch (member)
            {
                case MethodBase mb when mb.IsConstructor:
                    return owner + "(" + ParameterTypes(mb) + ")";
                case MethodInfo m:
                    var arity = m.IsGenericMethod
                        ? "<" + string.Join(",", m.GetGenericArguments().Select(a => a.Name)) + ">"
                        : "";
                    return owner + "." + m.Name + arity + "(" + ParameterTypes(m) + ")";
                default:
                    return owner + "." + member.Name;
            }
        }

        private static string ParameterTypes(MethodBase method) =>
            string.Join(",", method.GetParameters().Select(p => CSharpName(p.ParameterType)));

        public static string FormatMethod(MethodInfo method)
        {
            var sb = new StringBuilder();
            sb.Append(VisibilityOf(method));
            if (method.IsStatic) sb.Append("static ");
            sb.Append(CSharpName(method.ReturnType)).Append(' ');
            AppendMethodTail(sb, method, method.Name);
            return sb.ToString();
        }

        /// <summary>Modifiers, name, generic list, parameter list, constraints — everything after
        /// the return type, shared by methods and constructors.</summary>
        private static void AppendMethodTail(StringBuilder sb, MethodBase method, string name)
        {
            sb.Append(name);
            // Mono throws NotSupportedException for a constructor's GetGenericArguments, and a
            // constructor is never generic anyway.
            var generics = method is MethodInfo mi ? mi.GetGenericArguments() : Array.Empty<Type>();
            if (generics.Length > 0)
                sb.Append('<').Append(string.Join(", ", generics.Select(g => g.Name))).Append('>');
            sb.Append('(').Append(FormatParameters(method.GetParameters())).Append(')');
            AppendConstraints(sb, generics);
        }

        /// <summary>Constructors are <see cref="ConstructorInfo"/>, not MethodInfo: they have no
        /// return type, and the declaring type's name is the member's name.</summary>
        public static string FormatConstructor(ConstructorInfo ctor)
        {
            var sb = new StringBuilder();
            sb.Append(VisibilityOf(ctor));
            AppendMethodTail(sb, ctor, CSharpName(ctor.DeclaringType));
            return sb.ToString();
        }

        public static string FormatProperty(PropertyInfo property)
        {
            var get = property.GetGetMethod(true);
            var set = property.GetSetMethod(true);
            var accessor = property.Name == "Item"
                ? "this[" + FormatParameters(property.GetIndexParameters()) + "]"
                : property.Name;
            var access = (get != null ? "get; " : "") + (set != null ? "set; " : "");
            var sb = new StringBuilder();
            sb.Append(VisibilityOf(property));
            if ((get != null && get.IsStatic) || (set != null && set.IsStatic)) sb.Append("static ");
            sb.Append(CSharpName(property.PropertyType)).Append(' ').Append(accessor)
                .Append(" { ").Append(access.TrimEnd()).Append(" }");
            return sb.ToString();
        }

        public static string FormatField(FieldInfo field)
        {
            var sb = new StringBuilder();
            sb.Append(VisibilityOf(field));
            if (field.IsLiteral) sb.Append("const ");
            else
            {
                if (field.IsStatic) sb.Append("static ");
                if (field.IsInitOnly) sb.Append("readonly ");
            }
            sb.Append(CSharpName(field.FieldType)).Append(' ').Append(field.Name).Append(';');
            return sb.ToString();
        }

        public static string FormatEvent(EventInfo evt)
        {
            var add = evt.GetAddMethod(true);
            var sb = new StringBuilder();
            sb.Append(VisibilityOf(evt));
            if (add != null && add.IsStatic) sb.Append("static ");
            sb.Append("event ").Append(CSharpName(evt.EventHandlerType)).Append(' ').Append(evt.Name).Append(';');
            return sb.ToString();
        }

        private static string FormatParameters(ParameterInfo[] parameters)
        {
            var parts = new string[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                var type = p.ParameterType;
                var prefix = "";
                if (type.IsByRef)
                {
                    type = type.GetElementType();
                    prefix = p.IsOut ? "out " : p.IsIn ? "in " : "ref ";
                }
                if (p.IsDefined(typeof(ParamArrayAttribute), false)) prefix = "params ";
                var name = string.IsNullOrEmpty(p.Name) ? "arg" + i : p.Name;
                parts[i] = prefix + CSharpName(type) + " " + name;
            }
            return string.Join(", ", parts);
        }

        private static void AppendConstraints(StringBuilder sb, Type[] generics)
        {
            foreach (var g in generics)
            {
                if (!g.IsGenericParameter) continue;
                var constraints = new List<string>();
                foreach (var c in g.GetGenericParameterConstraints())
                {
                    if (c == typeof(ValueType)) continue;
                    constraints.Add(CSharpName(c));
                }
                var attrs = g.GenericParameterAttributes & GenericParameterAttributes.SpecialConstraintMask;
                if ((attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0) constraints.Insert(0, "class");
                var isValue = (attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;
                if (isValue) constraints.Insert(0, "struct");
                // `where T : struct` already implies the parameterless constructor, so writing
                // new() next to it is not something C# source ever says.
                if (!isValue && (attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
                    constraints.Add("new()");
                if (constraints.Count > 0)
                    sb.Append(" where ").Append(g.Name).Append(" : ").Append(string.Join(", ", constraints));
            }
        }

        private static string VisibilityOf(MemberInfo member)
        {
            switch (member)
            {
                case MethodBase mb: return VisibilityOf(mb);
                case FieldInfo f: return VisibilityOfField(f);
                case PropertyInfo p:
                    var accessor = p.GetGetMethod(true) ?? p.GetSetMethod(true);
                    return accessor != null ? VisibilityOf(accessor) : "public ";
                case EventInfo e:
                    var add = e.GetAddMethod(true) ?? e.GetRemoveMethod(true) ?? e.GetRaiseMethod(true);
                    return add != null ? VisibilityOf(add) : "public ";
                case Type t: return TypeVisibility(t);
                default: return "";
            }
        }

        private static string VisibilityOf(MethodBase member)
        {
            if (member.IsPublic) return "public ";
            if (member.IsFamilyOrAssembly) return "protected internal ";
            if (member.IsFamily) return "protected ";
            if (member.IsFamilyAndAssembly) return "private protected ";
            if (member.IsAssembly) return "internal ";
            return "private ";
        }

        private static string VisibilityOfField(FieldInfo member)
        {
            if (member.IsPublic) return "public ";
            if (member.IsFamilyOrAssembly) return "protected internal ";
            if (member.IsFamily) return "protected ";
            if (member.IsFamilyAndAssembly) return "private protected ";
            if (member.IsAssembly) return "internal ";
            return "private ";
        }

        private static string TypeVisibility(Type member)
        {
            if (!member.IsNested) return member.IsPublic ? "public " : "internal ";
            if (member.IsNestedPublic) return "public ";
            if (member.IsNestedFamily || member.IsNestedFamORAssem) return "protected ";
            return "private ";
        }

        // ─── [Obsolete] ───────────────────────────────────────

        public sealed class ObsoleteInfo
        {
            public string Message { get; }
            public bool IsError { get; }
            public string Replacement { get; }

            public ObsoleteInfo(string message, bool isError, string replacement)
            {
                Message = message;
                IsError = isError;
                Replacement = replacement;
            }
        }

        public static ObsoleteInfo ObsoleteOf(MemberInfo member)
        {
            var attr = member?.GetCustomAttribute<ObsoleteAttribute>(false);
            if (attr == null) return null;
            return new ObsoleteInfo(
                string.IsNullOrEmpty(attr.Message) ? null : attr.Message,
                attr.IsError,
                ParseReplacement(attr.Message));
        }

        /// <summary>The member a deprecation notice points at, when it names one. Unity writes it
        /// two ways: "Use X instead." and the "(UnityUpgradable) -&gt; X" marker the API updater
        /// renames with. The marker wins: it is the exact replacement, while the prose often names a
        /// type or an overload without parameters. A marker at a parameter index ("!2") names no
        /// member, so it is not a replacement.</summary>
        public static string ParseReplacement(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            var upgradable = UpgradablePattern.Match(message);
            if (upgradable.Success)
            {
                var target = upgradable.Groups["x"].Value.Trim().TrimEnd('.', ',');
                if (target.Length > 0 && target[0] != '!') return target;
            }
            var instead = UseInsteadPattern.Match(message);
            if (!instead.Success) return null;
            var name = instead.Groups["x"].Value.Trim().TrimEnd('.', ',');
            // "Please use single pass stereo rendering instead" names prose, not a member; only a
            // single dotted identifier is something the caller can write.
            if (name.Length == 0 || name.Contains(" ")) return null;
            return name;
        }

        // ─── Name similarity ──────────────────────────────────

        /// <summary>Levenshtein distance, or -1 when it exceeds <paramref name="max"/> — the cap
        /// keeps the inner loops short for the long names Unity generates.</summary>
        public static int EditDistance(string a, string b, int max)
        {
            if (a == null || b == null) return -1;
            if (a == b) return 0;
            if (Math.Abs(a.Length - b.Length) > max) return -1;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                var rowMin = current[0];
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                    rowMin = Math.Min(rowMin, current[j]);
                }
                if (rowMin > max) return -1;
                (previous, current) = (current, previous);
            }
            return previous[b.Length] <= max ? previous[b.Length] : -1;
        }

        /// <summary>How much a candidate name looks like the typed name; 0 or below rejects it. A
        /// missing member is usually a typo or a rename, so exact beats prefix beats edit distance.</summary>
        public static int NameScore(string candidate, string query)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(query)) return 0;
            if (string.Equals(candidate, query, StringComparison.Ordinal)) return 100;
            if (string.Equals(candidate, query, StringComparison.OrdinalIgnoreCase)) return 90;
            if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
                query.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                return 60 - Math.Abs(candidate.Length - query.Length);

            var allowed = Math.Max(2, Math.Min(query.Length, candidate.Length) / 3);
            var distance = EditDistance(candidate, query, allowed);
            if (distance < 0) return 0;
            return 40 - distance * 4;
        }
    }
}
