using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace UnityCliConnector
{
    /// <summary>
    /// Finds [UnityCliTool] handlers via reflection.
    ///
    /// The scan is cached per domain: walking every loaded assembly's types with attribute lookups
    /// costs ~0.6-0.9 s on this project's domain, which was paid on the main thread for every
    /// command (and made a single tool call feel like a second of latency). A domain reload is the
    /// only way the tool set can change, and it resets the statics that hold the cache.
    /// </summary>
    public static class ToolDiscovery
    {
        internal sealed class ToolEntry
        {
            public string Name;
            public MethodInfo Handler;
            public Type Type;
            public UnityCliToolAttribute Attribute;
            // The nested Parameters class, or null for a tool that declares no schema.
            public Type ParametersType;
        }

        static readonly object s_ToolLock = new();
        static Dictionary<string, ToolEntry> s_Tools;

        /// Every tool in the domain by command name, built once per domain. Duplicates keep the
        /// first one found and log, so a broken installation still answers.
        internal static Dictionary<string, ToolEntry> Tools()
        {
            lock (s_ToolLock)
            {
                if (s_Tools != null) return s_Tools;

                var tools = new Dictionary<string, ToolEntry>(StringComparer.Ordinal);
                foreach (var type in ToolTypes())
                {
                    var attr = type.GetCustomAttribute<UnityCliToolAttribute>();
                    var name = attr.Name ?? StringCaseUtility.ToSnakeCase(type.Name);

                    var method = type.GetMethod("HandleCommand",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(JObject) }, null);
                    if (method == null) continue;

                    if (tools.TryGetValue(name, out var existing))
                    {
                        UnityEngine.Debug.LogError(
                            $"[UnityCliConnector] Duplicate tool '{name}': " +
                            $"{existing.Type.FullName} and {type.FullName}. Using first found.");
                        continue;
                    }

                    tools[name] = new ToolEntry
                    {
                        Name = name,
                        Handler = method,
                        Type = type,
                        Attribute = attr,
                        ParametersType = type.GetNestedType("Parameters"),
                    };
                }

                s_Tools = tools;
                return tools;
            }
        }

        static List<Type> ToolTypes()
        {
            var types = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] loaded;
                try { loaded = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException) { continue; }

                foreach (var type in loaded)
                    if (type.IsClass && type.GetCustomAttribute<UnityCliToolAttribute>() != null)
                        types.Add(type);
            }
            return types;
        }

        internal static ToolEntry FindTool(string command)
            => command != null && Tools().TryGetValue(command, out var entry) ? entry : null;

        public static MethodInfo FindHandler(string command) => FindTool(command)?.Handler;

        /// <summary>
        /// The parameter names a tool's schema declares, in the spellings a caller may send: the
        /// snake_case form the CLI derives from the property, its camelCase variant (the older
        /// spelling every tool still accepts), the attribute's own name when it sets one, and any
        /// legacy aliases. Used to reject a misspelled flag instead of silently ignoring it.
        /// </summary>
        internal static HashSet<string> DeclaredParameterNames(Type paramsType)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (paramsType == null) return names;

            foreach (var property in paramsType.GetProperties())
            {
                var attr = property.GetCustomAttribute<ToolParameterAttribute>();
                if (attr?.Name != null) names.Add(attr.Name);
                names.Add(StringCaseUtility.ToSnakeCase(property.Name));
                names.Add(LowerFirst(property.Name));
                if (attr?.Aliases == null) continue;
                foreach (var alias in attr.Aliases) names.Add(alias);
            }
            return names;
        }

        static string LowerFirst(string name)
            => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

        static readonly object s_OffMainThreadLock = new();
        static HashSet<string> s_OffMainThreadTools;

        /// <summary>
        /// Whether <paramref name="command"/> is a tool marked
        /// [UnityCliTool(RunOffMainThread = true)]. Called from the HTTP request
        /// thread, so the set is built once per domain instead of rescanning on every
        /// request; a recompile reloads the domain and resets it.
        /// </summary>
        public static bool IsOffMainThreadTool(string command)
        {
            lock (s_OffMainThreadLock)
            {
                if (s_OffMainThreadTools == null)
                {
                    s_OffMainThreadTools = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var entry in Tools().Values)
                        if (entry.Attribute is { RunOffMainThread: true })
                            s_OffMainThreadTools.Add(entry.Name);
                }
                return s_OffMainThreadTools.Contains(command);
            }
        }

        public static List<object> GetToolSchemas()
        {
            var tools = new List<object>();
            foreach (var entry in Tools().Values)
            {
                tools.Add(new
                {
                    name = entry.Name,
                    description = entry.Attribute.Description ?? "",
                    group = entry.Attribute.Group ?? "",
                    parameters = GetParameterSchema(entry.ParametersType),
                });
            }

            return tools;
        }

        public static List<object> GetParameterSchema(Type paramsType)
        {
            if (paramsType == null) return new List<object>();

            return paramsType.GetProperties()
                .Select(p =>
                {
                    var attr = p.GetCustomAttribute<ToolParameterAttribute>();
                    return new
                    {
                        name = StringCaseUtility.ToSnakeCase(p.Name),
                        type = p.PropertyType.Name,
                        description = attr?.Description ?? "",
                        required = attr?.Required ?? false,
                    };
                })
                .Cast<object>()
                .ToList();        }
    }
}
