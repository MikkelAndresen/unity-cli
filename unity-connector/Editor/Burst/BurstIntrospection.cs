using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityCliConnector.Tools
{
    /// Reflection bridge over Burst's editor-internal compiler state.
    ///
    /// Nothing here is public API in any Burst version. Burst 2.x (shipped inside
    /// UnityEngine.BurstModule / UnityEditor.BurstModule for Unity 6000.5+) exposes
    /// BurstCompiler.GetInspectorEntryPoints/IsCurrentCompilationDone/TriggerRecompilation as
    /// private statics and describes a target as strings; Burst 1.8.x (a package) exposes the same
    /// ideas as internal statics and describes a target with live Type/MethodInfo. Both shapes are
    /// read here. Every lookup is optional: a missing member downgrades the report instead of
    /// throwing, so a Burst update that renames one thing costs a field, not the tool.
    ///
    /// Why not the Inspector's per-target compile probe (BurstCompilerService.GetDisassembly)?
    /// Measured on Unity 6000.6.5f1 / Burst 2.0.0: it returns "Unable to compile method in
    /// BurstCompilerService::CompileAsync" for *any* non-empty options string, including Burst's own
    /// DefaultOptions, on targets that compile fine. Forcing a full Burst recompile
    /// (TriggerRecompilation) is both cheaper and works across versions.
    internal sealed class BurstIntrospection
    {
        private const BindingFlags StaticAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private const string CompilerTypeName = "Unity.Burst.BurstCompiler";
        private const string LoaderTypeName = "Unity.Burst.Editor.BurstLoader";
        private const string ReflectionTypeName = "Unity.Burst.Editor.BurstReflection";

        private static readonly string[] BurstRuntimeAssemblies = { "UnityEngine.BurstModule", "Unity.Burst" };
        private static readonly string[] BurstEditorAssemblies = { "UnityEditor.BurstModule", "Unity.Burst.Editor", "Unity.Burst" };

        private readonly Type _compiler;
        private readonly Type _reflection;
        private readonly FieldInfo _eagerStatus;
        private readonly MethodInfo _isCompilationDone;
        private readonly MethodInfo _triggerRecompilation;
        private readonly MethodInfo _entryPoints;
        private readonly MethodInfo _findExecuteMethods;
        private readonly FieldInfo _assemblyList;
        private readonly object _globalOptions;
        private readonly MethodInfo _optionsIsEnabled;

        private BurstIntrospection(Type compiler, Type loader, Type reflection)
        {
            _compiler = compiler;
            _reflection = reflection;

            _isCompilationDone = compiler?.GetMethod("IsCurrentCompilationDone", StaticAny);
            _triggerRecompilation = compiler?.GetMethod("TriggerRecompilation", StaticAny);
            _entryPoints = compiler?.GetMethod("GetInspectorEntryPoints", StaticAny);

            // BurstCompiler.Options is a public static field today; the property form is the
            // fallback so a rename in either direction costs a lookup, not the enabled flag.
            var optionsField = compiler?.GetField("Options", StaticAny);
            _globalOptions = optionsField?.GetValue(null)
                             ?? compiler?.GetProperty("Options", StaticAny)?.GetValue(null);
            _optionsIsEnabled = _globalOptions?.GetType().GetMethod("get_EnableBurstCompilation");

            _eagerStatus = loader?.GetField("EagerCompilationStatus", StaticAny);

            _findExecuteMethods = reflection?.GetMethod("FindExecuteMethods", StaticAny);
            _assemblyList = reflection?.GetField("EditorAssembliesThatCanPossiblyContainJobs", StaticAny);
        }

        /// null when Burst's own types are not loaded.
        public static BurstIntrospection Create()
        {
            var compiler = FindType(CompilerTypeName, BurstRuntimeAssemblies);
            if (compiler == null) return null;
            var loader = FindType(LoaderTypeName, BurstEditorAssemblies);
            var reflection = FindType(ReflectionTypeName, BurstEditorAssemblies);
            return new BurstIntrospection(compiler, loader, reflection);
        }

        static Type FindType(string fullName, string[] assemblyNames)
        {
            foreach (var assembly in assemblyNames)
            {
                var type = Type.GetType($"{fullName}, {assembly}");
                if (type != null) return type;
            }

            // The assembly names above are the ones Burst has shipped under; if a future layout
            // moves the types elsewhere, find them by name instead of giving up.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType(fullName); }
                catch (Exception) { continue; }
                if (type != null) return type;
            }
            return null;
        }

        public bool CanQueryIdle => _isCompilationDone != null;

        public bool CanForceRecompile => _triggerRecompilation != null;

        /// Burst's own answer to "is any compile still queued or running".
        public bool IsCompilationDone()
        {
            if (_isCompilationDone == null) return true;
            try { return (bool)_isCompilationDone.Invoke(null, null); }
            catch (TargetInvocationException) { return false; }
        }

        public bool ForceRecompile()
        {
            if (_triggerRecompilation == null) return false;
            _triggerRecompilation.Invoke(null, null);
            return true;
        }

        public enum EagerState
        {
            Unknown,
            NotScheduled,
            Scheduled,
            Completed,
        }

        /// Whether Burst has run its eager (whole-assembly) compile in this editor session. The
        /// field is internal even in Burst's own inspector, which reads it by reflection.
        public EagerState Eager()
        {
            if (_eagerStatus == null) return EagerState.Unknown;
            var value = _eagerStatus.GetValue(null);
            if (value == null) return EagerState.Unknown;
            switch (value.ToString())
            {
                case "Completed": return EagerState.Completed;
                case "Scheduled": return EagerState.Scheduled;
                case "NotScheduled": return EagerState.NotScheduled;
                default: return EagerState.Unknown;
            }
        }

        /// [BurstCompile] disabled from the menu, a build flag, or the environment.
        public bool CompilationEnabled()
        {
            if (_optionsIsEnabled == null || _globalOptions == null) return true;
            try { return (bool)_optionsIsEnabled.Invoke(_globalOptions, null); }
            catch (TargetInvocationException) { return true; }
        }

        /// Every target Burst knows how to compile. Assembly is left empty when the target cannot be
        /// resolved to a loaded type; the caller resolves it against the project's assemblies.
        public List<BurstTargetInfo> Targets()
        {
            return _entryPoints != null ? TargetsFromInspectorEntries() : TargetsFromReflectionScan();
        }

        List<BurstTargetInfo> TargetsFromInspectorEntries()
        {
            var targets = new List<BurstTargetInfo>();
            var entries = _entryPoints.Invoke(null, null) as Array;
            if (entries == null) return targets;

            var type = entries.GetType().GetElementType();
            var fullName = type.GetField("FullName");
            var displayName = type.GetField("DisplayName");
            var scope = type.GetField("Scope");

            foreach (var entry in entries)
            {
                var full = fullName != null ? (string)fullName.GetValue(entry) : "";
                targets.Add(new BurstTargetInfo
                {
                    FullName = full ?? "",
                    DisplayName = displayName != null ? (string)displayName.GetValue(entry) : "",
                    // FullName carries the job/static-method type (nested types as '.'); Scope is the
                    // namespace or containing type, which is what a static-method target names instead.
                    DeclaringType = TypePart(full),
                    Scope = scope != null ? (string)scope.GetValue(entry) ?? "" : "",
                    Assembly = "",
                    Status = "",
                });
            }
            return targets;
        }

        /// "MyGame.HeavyJob - (IJobFor)" -> "MyGame.HeavyJob"; a static-method target's
        /// "MyGame.Util.Run(int)" is returned whole and resolved by its Scope instead.
        static string TypePart(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "";
            var dash = fullName.IndexOf(" - (", StringComparison.Ordinal);
            return (dash > 0 ? fullName.Substring(0, dash) : fullName).Trim();
        }

        List<BurstTargetInfo> TargetsFromReflectionScan()
        {
            var targets = new List<BurstTargetInfo>();
            if (_findExecuteMethods == null) return targets;

            object result;
            try
            {
                result = _findExecuteMethods.GetParameters().Length switch
                {
                    0 => _findExecuteMethods.Invoke(null, null),
                    _ => _findExecuteMethods.Invoke(null, new object[]
                    {
                        _assemblyList?.GetValue(null),
                        DefaultAssemblyOptions(_findExecuteMethods.GetParameters()[1].ParameterType),
                    }),
                };
            }
            catch (TargetInvocationException)
            {
                return targets;
            }
            if (result == null) return targets;

            var list = (System.Collections.IEnumerable)result.GetType().GetField("CompileTargets").GetValue(result);
            foreach (var entry in list)
            {
                var type = entry.GetType();
                var jobType = type.GetField("JobType")?.GetValue(entry) as Type;
                if (jobType == null) continue;

                var interfaceType = type.GetField("JobInterfaceType")?.GetValue(entry) as Type;
                var name = jobType.FullName ?? jobType.Name;
                targets.Add(new BurstTargetInfo
                {
                    FullName = interfaceType == null ? name : $"{name} - ({interfaceType.Name})",
                    DisplayName = interfaceType == null ? jobType.Name : $"{jobType.Name} - ({interfaceType.Name})",
                    DeclaringType = name,
                    Scope = jobType.Namespace ?? "",
                    Assembly = jobType.Assembly?.GetName().Name ?? "",
                    Status = "",
                });
            }
            return targets;
        }

        static object DefaultAssemblyOptions(Type optionsType)
        {
            if (!optionsType.IsEnum) return Activator.CreateInstance(optionsType);
            return Enum.ToObject(optionsType, 0);
        }
    }
}
