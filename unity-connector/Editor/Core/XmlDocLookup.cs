using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace UnityCliConnector
{
    /// <summary>
    /// XML doc &lt;summary&gt; text from the documentation file that sits next to an assembly. Unity
    /// ships one for its own modules (UnityEngine.CoreModule.xml and friends); third-party and
    /// project assemblies usually ship none, and missing docs are the normal case, so every path
    /// here answers "no" quietly rather than reporting a failure.
    /// </summary>
    public static class XmlDocLookup
    {
        // Unity's biggest doc file measures ~4 MB; anything much larger is generated or vendored
        // output that is not worth holding in memory for a lookup.
        private const long MaxDocBytes = 48L * 1024 * 1024;
        private const int MaxCachedFiles = 4;

        private static readonly Dictionary<string, DocFile> s_Files = new(StringComparer.OrdinalIgnoreCase);

        public static bool TryGetTypeSummary(Type type, out string summary) =>
            TryGetSummary(type?.Assembly, type != null ? TypeKey(type) : null, out summary);

        public static bool TryGetMemberSummary(MemberInfo member, out string summary) =>
            TryGetSummary(member?.DeclaringType?.Assembly, member != null ? MemberKey(member) : null, out summary);

        private static bool TryGetSummary(Assembly assembly, string key, out string summary)
        {
            summary = null;
            if (assembly == null || string.IsNullOrEmpty(key)) return false;

            var file = OpenFor(assembly);
            if (file == null) return false;
            if (file.ByKey.TryGetValue(key, out var text)) { summary = text; return true; }

            // Overloads and odd generic spellings miss the exact key; the same member without its
            // parameter list is the closest thing the file has.
            var open = key.IndexOf('(');
            if (open <= 0) return false;
            return file.ByShortKey.TryGetValue(key.Substring(0, open), out summary) && summary != null;
        }

        private static DocFile OpenFor(Assembly assembly)
        {
            string location;
            try
            {
                location = assembly.Location;
            }
            catch (Exception e) when (e is NotSupportedException || e is System.Security.SecurityException)
            {
                return null;
            }
            if (string.IsNullOrEmpty(location)) return null;

            if (s_Files.TryGetValue(location, out var cached)) return cached;

            var doc = Load(location);
            if (s_Files.Count >= MaxCachedFiles) s_Files.Clear();
            s_Files[location] = doc;
            return doc;
        }

        private static DocFile Load(string assemblyLocation)
        {
            var path = Path.ChangeExtension(assemblyLocation, ".xml");
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxDocBytes) return DocFile.Empty;

                var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
                var xml = XDocument.Load(path);
                foreach (var member in xml.Root?.Element("members")?.Elements("member") ??
                                      Enumerable.Empty<XElement>())
                {
                    var name = member.Attribute("name")?.Value;
                    if (string.IsNullOrEmpty(name)) continue;
                    var summary = Clean(member.Element("summary")?.Value);
                    if (summary.Length == 0) continue;
                    byKey[name] = summary;
                }
                return new DocFile(byKey);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException
                || e is System.Xml.XmlException || e is PathTooLongException)
            {
                return DocFile.Empty;
            }
        }

        /// <summary>Doc text keeps the source's line breaks and nested tags; only the text content
        /// is wanted, and it has to fit on one line.</summary>
        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        // ─── Doc file keys ────────────────────────────────────

        public static string TypeKey(Type type) => type == null ? null : "T:" + RefName(type);

        public static string MemberKey(MemberInfo member)
        {
            if (member == null) return null;
            switch (member)
            {
                case MethodInfo m:
                {
                    var name = m.Name + (m.IsGenericMethod ? "``" + m.GetGenericArguments().Length : "");
                    var parameters = m.GetParameters();
                    var suffix = parameters.Length == 0 ? "" : "(" + string.Join(",", parameters.Select(ParamName)) + ")";
                    return "M:" + Declaring(m) + "." + name + suffix;
                }
                case PropertyInfo p:
                {
                    var index = p.GetIndexParameters();
                    var suffix = index.Length == 0 ? "" : "(" + string.Join(",", index.Select(ParamName)) + ")";
                    return "P:" + Declaring(p) + "." + p.Name + suffix;
                }
                case FieldInfo f:
                    return "F:" + Declaring(f) + "." + f.Name;
                case EventInfo e:
                    return "E:" + Declaring(e) + "." + e.Name;
                case Type t:
                    return TypeKey(t);
                default:
                    return null;
            }
        }

        private static string Declaring(MemberInfo member) =>
            member.DeclaringType != null ? RefName(member.DeclaringType) : member.Name;

        private static string ParamName(ParameterInfo parameter)
        {
            var type = parameter.ParameterType;
            if (type.IsByRef) return RefName(type.GetElementType()) + "&";
            if (type.IsPointer) return RefName(type.GetElementType()) + "*";
            if (type.IsArray)
            {
                var rank = type.GetArrayRank();
                return RefName(type.GetElementType()) + "[" + new string(',', Math.Max(0, rank - 1)) + "]";
            }
            return RefName(type);
        }

        /// <summary>The reflection spelling the doc files use: "System.Int32", "A+B" for nested
        /// types, "List`1[System.Int32]" for a constructed generic, "!0"/"!!0" for parameters.
        /// <see cref="Type.FullName"/> spells a constructed generic's arguments assembly-qualified,
        /// which the doc files never do, so that case is built by hand.</summary>
        private static string RefName(Type type)
        {
            if (type == null) return "";
            if (type.IsGenericParameter)
                return type.DeclaringMethod != null ? "!!" + type.GenericParameterPosition : "!" + type.GenericParameterPosition;
            if (type.IsArray)
            {
                var rank = type.GetArrayRank();
                return RefName(type.GetElementType()) + "[" + new string(',', Math.Max(0, rank - 1)) + "]";
            }
            if (type.IsByRef) return RefName(type.GetElementType()) + "&";
            if (type.IsPointer) return RefName(type.GetElementType()) + "*";
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                var definition = type.GetGenericTypeDefinition().FullName ?? type.GetGenericTypeDefinition().Name;
                return definition + "[" + string.Join(",", type.GetGenericArguments().Select(RefName)) + "]";
            }
            return type.FullName ?? type.Name;
        }

        private sealed class DocFile
        {
            public static readonly DocFile Empty = new(new Dictionary<string, string>(StringComparer.Ordinal));

            public DocFile(Dictionary<string, string> byKey)
            {
                ByKey = byKey;
            }

            public Dictionary<string, string> ByKey { get; }

            public Dictionary<string, string> ByShortKey => m_ShortKey ??= BuildShortKeys();

            private Dictionary<string, string> m_ShortKey;

            private Dictionary<string, string> BuildShortKeys()
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in ByKey)
                {
                    var open = kv.Key.IndexOf('(');
                    var shortKey = open > 0 ? kv.Key.Substring(0, open) : kv.Key;
                    if (!map.ContainsKey(shortKey)) map[shortKey] = kv.Value;
                }
                return map;
            }
        }

        // ─── Testing hooks ────────────────────────────────────
        // Internal seam for unit tests. Production code must not call these.

        internal static void ResetForTests() => s_Files.Clear();
    }
}
