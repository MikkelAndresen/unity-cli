using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace UnityCliConnector
{
    /// <summary>
    /// Finds [UnityCliTool] handlers on demand via reflection.
    /// No caching, no registration — every call scans live.
    /// </summary>
    public static class ToolDiscovery
    {
        public static MethodInfo FindHandler(string command)
        {
            MethodInfo found = null;
            Type foundType = null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException) { continue; }

                foreach (var type in types)
                {
                    if (type.IsClass == false) continue;
                    var attr = type.GetCustomAttribute<UnityCliToolAttribute>();
                    if (attr == null) continue;

                    var name = attr.Name ?? StringCaseUtility.ToSnakeCase(type.Name);
                    if (name != command) continue;

                    var method = type.GetMethod("HandleCommand",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(JObject) }, null);

                    if (method == null) continue;

                    if (found != null)
                    {
                        UnityEngine.Debug.LogError(
                            $"[UnityCliConnector] Duplicate tool '{command}': " +
                            $"{foundType.FullName} and {type.FullName}. Using first found.");
                        continue;
                    }

                    found = method;
                    foundType = type;
                }
            }

            return found;
        }

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
                s_OffMainThreadTools ??= ScanOffMainThreadTools();
                return s_OffMainThreadTools.Contains(command);
            }
        }

        static HashSet<string> ScanOffMainThreadTools()
        {
            var names = new HashSet<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException) { continue; }

                foreach (var type in types)
                {
                    if (type.IsClass == false) continue;
                    var attr = type.GetCustomAttribute<UnityCliToolAttribute>();
                    if (attr is not { RunOffMainThread: true }) continue;
                    names.Add(attr.Name ?? StringCaseUtility.ToSnakeCase(type.Name));
                }
            }
            return names;
        }

        public static List<object> GetToolSchemas()
        {
            var tools = new List<object>();
            var nameToType = new Dictionary<string, Type>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException) { continue; }

                foreach (var type in types)
                {
                    if (type.IsClass == false) continue;
                    var attr = type.GetCustomAttribute<UnityCliToolAttribute>();
                    if (attr == null) continue;

                    var name = attr.Name ?? StringCaseUtility.ToSnakeCase(type.Name);

                    if (nameToType.TryGetValue(name, out var existing))
                    {
                        UnityEngine.Debug.LogError(
                            $"[UnityCliConnector] Duplicate tool name '{name}': " +
                            $"{existing.FullName} and {type.FullName}. " +
                            $"Rename one or remove the duplicate.");
                        continue;
                    }
                    nameToType[name] = type;

                    var paramsType = type.GetNestedType("Parameters");

                    tools.Add(new
                    {
                        name,
                        description = attr.Description ?? "",
                        group = attr.Group ?? "",
                        parameters = GetParameterSchema(paramsType),
                    });
                }
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
                .ToList();
        }
    }
}
