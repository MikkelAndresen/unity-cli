using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine.SceneManagement;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Description = "Refresh Unity assets and optionally request script compilation.", SkipCompilationGate = true)]
    public static class RefreshUnity
    {
        public class Parameters
        {
            [ToolParameter("Refresh mode: if_dirty (default) or force")]
            public string Mode { get; set; }

            [ToolParameter("Allow refresh while the editor is in or entering play mode.")]
            public bool Force { get; set; }

            [ToolParameter("Scope: all (default) or specific path")]
            public string Scope { get; set; }

            [ToolParameter("Compile mode: none (default), request, or clean (discard Unity's compiled-assembly cache first)")]
            public string Compile { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            string mode = p.Get("mode", "if_dirty");
            string scope = p.Get("scope", "all");
            string compile = p.Get("compile", "none");
            // mode:"force" and force:true are the same intent; echo whichever was actually done, so
            // a caller who used mode:"force" no longer reads back force:false.
            bool force = p.GetBool("force") || string.Equals(mode, "force", StringComparison.OrdinalIgnoreCase);

            bool compileRequested = false;
            // Echoed so the CLI has a request-time baseline: a compile can finish before its first
            // poll, and a heartbeat that was already "ready" proves nothing on its own.
            long compileFinishedBefore = CompilationGate.LastCompileFinishedAt;
            var dirtyScenes = GetDirtyOpenScenes();
            var warning = dirtyScenes.Count > 0
                ? "Open scenes have unsaved changes. If Unity becomes unresponsive, an editor modal such as \"open scene changed on disk, reload?\" may be blocking the refresh."
                : null;

            if (!force && EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return new ErrorResponse("Cannot refresh while Unity is in or entering play mode. Exit play mode first, or pass --force if this is intentional.");
            }

            AssetDatabase.Refresh(force
                ? ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport
                : ImportAssetOptions.ForceSynchronousImport);

            bool clean = string.Equals(compile, "clean", StringComparison.OrdinalIgnoreCase);
            // CleanBuildCache only exists from 2021.1; on an older editor this used to fall back to a
            // plain request, which a caller could mistake for the clean rebuild it asked for.
            if (clean && !CleanCompileSupported())
                return new ErrorResponse("compile:\"clean\" needs Unity 2021.1 or newer; request a normal compile instead.");
            if (string.Equals(compile, "request", StringComparison.OrdinalIgnoreCase) || clean)
            {
                Heartbeat.MarkCompileRequested();
                // RequestScriptCompilation is fire-and-forget here: the domain reload it triggers
                // drops the HTTP connection, so the outcome is polled from the heartbeat instead.
#if UNITY_2021_1_OR_NEWER
                if (clean) CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
                else CompilationPipeline.RequestScriptCompilation();
#else
                CompilationPipeline.RequestScriptCompilation();
#endif
                compileRequested = true;
            }

            // Before the compile this is the state the request starts from; the post-compile answer
            // comes from CompileProvenance in compile_status/compile_diagnostics.
            var stale = AssemblyStaleness.ScanProject(AssemblyStaleness.DefaultBudgetMs);
            return new SuccessResponse("Refresh requested.", new
            {
                refresh_triggered = true,
                compile_requested = compileRequested,
                // The CLI may ask for one CleanBuildCache retry after a compile left a stale DLL.
                clean_compile_supported = CleanCompileSupported(),
                compile_finished_at = compileFinishedBefore,
                force = force,
                warning = warning,
                dirty_scenes = dirtyScenes,
                stale_assembly_count = stale.StaleCount,
                stale_assemblies = AssemblyStaleness.ToPayload(stale),
                stale_assembly_line = AssemblyStaleness.FormatLine(stale),
                stale_assembly_scan_complete = stale.Complete,
                stale_assembly_covered_count = stale.CoveredCount,
                stale_assembly_covered_line = AssemblyStaleness.FormatCoveredLine(stale),
            });
        }

        private static bool CleanCompileSupported()
        {
#if UNITY_2021_1_OR_NEWER
            return true;
#else
            return false;
#endif
        }

        private static List<string> GetDirtyOpenScenes()
        {
            var scenes = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isDirty)
                    continue;

                scenes.Add(string.IsNullOrEmpty(scene.path) ? scene.name : scene.path);
            }
            return scenes;
        }
    }
}
