using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace UnityCliConnector.Tools
{
    /// Which loaded assembly declares each Burst target. Burst 2.x names targets by string only, and
    /// finding the type took up to six spellings times every loaded assembly per target: 814 ms for
    /// 953 targets in an ECS project (Unity 6000.6.5f1, Burst 2.0.0), most of a burst_status call.
    ///
    /// Answers are kept in Library/unity-cli/burst-target-assemblies.json, not only in a static,
    /// because verify asks right after a compile, when the domain (and any static) is new. A cached
    /// assembly is re-checked with one type lookup per spelling, so a type that moved is resolved again.
    /// "Not in a project assembly" is cached as "" and dropped when the set of project assemblies
    /// changes (a package switched to file:, an asmdef added), since that is what could make it wrong.
    internal static class BurstTargetResolver
    {
        const string FileName = "burst-target-assemblies.json";
        internal const string NotInProject = "";

        sealed class CacheFile
        {
            public int version = 1;
            public string project_set = "";
            public Dictionary<string, string> types = new(StringComparer.Ordinal);
        }

        static CacheFile s_Cache;

        // Tests point this at a temp file; null means the project's Library/unity-cli.
        internal static string PathOverride;

        internal static void ResetForTests() => s_Cache = null;

        /// Fills Assembly and DeclaringType on each target that resolves. Targets outside the project
        /// assemblies are resolved only with includePackages, the one reply that lists them.
        internal static void Resolve(IReadOnlyList<BurstTargetInfo> targets,
            IReadOnlyDictionary<string, Assembly> projectAssemblies, bool includePackages)
        {
            var cache = Load();
            var projectSet = string.Join(",", projectAssemblies.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            var dirty = false;
            if (cache.project_set != projectSet)
            {
                foreach (var key in cache.types.Where(kv => kv.Value == NotInProject).Select(kv => kv.Key).ToList())
                    cache.types.Remove(key);
                cache.project_set = projectSet;
                dirty = true;
            }

            var loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (!string.IsNullOrEmpty(name) && !loaded.ContainsKey(name))
                    loaded[name] = assembly;
            }
            var projectOrder = projectAssemblies.Values.ToList();
            List<Assembly> others = null;

            foreach (var target in targets)
            {
                var key = target.DeclaringType + "|" + target.Scope;
                if (cache.types.TryGetValue(key, out var cached))
                {
                    if (cached != NotInProject && loaded.TryGetValue(cached, out var assembly) && TryResolveIn(target, assembly))
                        continue;
                    if (cached == NotInProject && !includePackages)
                        continue;
                }

                string found = null;
                foreach (var assembly in projectOrder)
                    if (TryResolveIn(target, assembly)) { found = target.Assembly; break; }
                if (found == null && includePackages)
                {
                    others ??= loaded.Values.Where(a => !projectAssemblies.ContainsKey(a.GetName().Name)).ToList();
                    foreach (var assembly in others)
                        if (TryResolveIn(target, assembly)) { found = target.Assembly; break; }
                }

                // Unresolvable even by the full scan stays "not in project", which is still true.
                var answer = found ?? NotInProject;
                if (!cache.types.TryGetValue(key, out var previous) || previous != answer)
                {
                    cache.types[key] = answer;
                    dirty = true;
                }
            }

            if (dirty) Save(cache);
        }

        static bool TryResolveIn(BurstTargetInfo target, Assembly assembly)
        {
            foreach (var candidate in Candidates(target))
            {
                var type = FindType(assembly, candidate);
                if (type == null) continue;
                target.Assembly = assembly.GetName().Name;
                target.DeclaringType = type.FullName;
                return true;
            }
            return false;
        }

        /// Burst writes nested types with '+' in Scope and with '.' in FullName; a loaded type name
        /// needs the '+' form. Try both spellings of each name Burst gave us.
        static IEnumerable<string> Candidates(BurstTargetInfo target)
        {
            var seen = new HashSet<string>();
            foreach (var raw in new[] { target.DeclaringType, target.Scope })
            {
                if (string.IsNullOrEmpty(raw)) continue;
                foreach (var variant in NestVariants(raw))
                    if (seen.Add(variant))
                        yield return variant;
            }
        }

        static IEnumerable<string> NestVariants(string name)
        {
            yield return name;
            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0)
                yield return name.Substring(0, lastDot) + "+" + name.Substring(lastDot + 1);
            if (name.Contains("."))
                yield return name.Replace('.', '+');
        }

        static Type FindType(Assembly assembly, string name)
        {
            try { return assembly.GetType(name, false); }
            catch (Exception) { return null; }
        }

        static string CachePath() =>
            PathOverride ?? Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "unity-cli", FileName);

        static CacheFile Load()
        {
            if (s_Cache != null) return s_Cache;
            s_Cache = new CacheFile();
            try
            {
                var path = CachePath();
                if (File.Exists(path))
                {
                    var file = JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(path));
                    if (file?.types != null && file.version == 1)
                        s_Cache = new CacheFile
                        {
                            project_set = file.project_set ?? "",
                            types = new Dictionary<string, string>(file.types, StringComparer.Ordinal),
                        };
                }
            }
            catch (Exception)
            {
                // A corrupt cache costs one cold resolve, nothing else.
            }
            return s_Cache;
        }

        static void Save(CacheFile cache)
        {
            try
            {
                var path = CachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(cache));
            }
            catch (Exception)
            {
            }
        }
    }
}
