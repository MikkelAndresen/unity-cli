using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor.Compilation;
using Assembly = System.Reflection.Assembly;

namespace UnityCliConnector
{
    /// <summary>
    /// Finds [UnityCliTool] handlers on demand via reflection.
    ///
    /// Discovery is restricted to assemblies whose sources live entirely under
    /// Packages/ — i.e. tools shipped in a package, not in the project's
    /// /Assets tree. A coding agent with RW on /Assets cannot author a new
    /// [UnityCliTool] and have it become callable.
    /// </summary>
    public static class ToolDiscovery
    {
        // Asmdef names whose sources are all under Packages/. Built lazily; the
        // editor resets statics on domain reload, which is also when new
        // packages become discoverable, so the cache naturally invalidates.
        static HashSet<string> s_TrustedAssemblyNames;

        static bool IsTrustedAssembly(Assembly assembly)
        {
            if (s_TrustedAssemblyNames == null)
            {
                s_TrustedAssemblyNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var a in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
                {
                    if (a.sourceFiles == null || a.sourceFiles.Length == 0) continue;
                    bool allUnderPackages = a.sourceFiles.All(p =>
                        p.StartsWith("Packages/", StringComparison.Ordinal) ||
                        p.StartsWith("Packages\\", StringComparison.Ordinal));
                    if (allUnderPackages) s_TrustedAssemblyNames.Add(a.name);
                }
            }
            return s_TrustedAssemblyNames.Contains(assembly.GetName().Name);
        }

        public static MethodInfo FindHandler(string command)
        {
            MethodInfo found = null;
            Type foundType = null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsTrustedAssembly(assembly)) continue;

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

        public static List<object> GetToolSchemas()
        {
            var tools = new List<object>();
            var nameToType = new Dictionary<string, Type>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsTrustedAssembly(assembly)) continue;

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
