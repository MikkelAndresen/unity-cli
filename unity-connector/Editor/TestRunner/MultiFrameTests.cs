using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityCliConnector.TestRunner
{
    /// Whether a test assembly holds tests that span editor frames. ExecutionSettings.runSynchronously
    /// filters those out without reporting them, so a run that may include one must stay stepped.
    internal static class MultiFrameTests
    {
        // Matched by name: the attributes live in UnityEngine.TestRunner, and a project may define its
        // own IOuterUnityTestAction attributes.
        private static readonly HashSet<string> FrameAttributes = new HashSet<string>
        {
            "UnityTestAttribute", "UnitySetUpAttribute", "UnityTearDownAttribute",
        };

        private const string OuterActionInterface = "IOuterUnityTestAction";

        // Per domain: a compile reloads the domain, which is when an assembly's answer can change.
        private static readonly Dictionary<string, bool> Cache = new Dictionary<string, bool>();

        /// assemblyNames null means every loaded test assembly, as the run itself would use.
        internal static bool AnyIn(string[] assemblyNames)
        {
            var wanted = assemblyNames == null ? null : new HashSet<string>(assemblyNames, StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (wanted != null ? !wanted.Contains(name) : !ReferencesNUnit(assembly)) continue;
                if (!Cache.TryGetValue(name, out var multi))
                    Cache[name] = multi = HasMultiFrameTests(assembly);
                if (multi) return true;
            }
            return false;
        }

        internal static void ResetForTests() => Cache.Clear();

        private static bool ReferencesNUnit(Assembly assembly) =>
            !assembly.IsDynamic &&
            assembly.GetReferencedAssemblies().Any(r => r.Name == "nunit.framework");

        internal static bool HasMultiFrameTests(Assembly assembly)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            return types.Any(SpansFrames);
        }

        internal static bool SpansFrames(Type type)
        {
            const BindingFlags members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            return MarksFrames(type.CustomAttributes) ||
                   type.GetMethods(members).Any(m => MarksFrames(m.CustomAttributes));
        }

        private static bool MarksFrames(IEnumerable<CustomAttributeData> attributes)
        {
            foreach (var a in attributes)
            {
                var type = a.AttributeType;
                if (FrameAttributes.Contains(type.Name)) return true;
                if (type.GetInterfaces().Any(i => i.Name == OuterActionInterface)) return true;
            }
            return false;
        }
    }
}
