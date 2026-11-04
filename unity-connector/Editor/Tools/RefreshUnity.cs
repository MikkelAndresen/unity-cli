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

            [ToolParameter("Compile mode: none (default) or request")]
            public string Compile { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            string mode = p.Get("mode", "if_dirty");
            string scope = p.Get("scope", "all");
            string compile = p.Get("compile", "none");
            bool force = p.GetBool("force");

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

            AssetDatabase.Refresh(string.Equals(mode, "force", StringComparison.OrdinalIgnoreCase)
                ? ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport
                : ImportAssetOptions.ForceSynchronousImport);

            if (string.Equals(compile, "request", StringComparison.OrdinalIgnoreCase))
            {
                Heartbeat.MarkCompileRequested();
                // RequestScriptCompilation is fire-and-forget here: the domain reload it triggers
                // drops the HTTP connection, so the outcome is polled from the heartbeat instead.
                CompilationPipeline.RequestScriptCompilation();
                compileRequested = true;
            }

            return new SuccessResponse("Refresh requested.", new
            {
                refresh_triggered = true,
                compile_requested = compileRequested,
                compile_finished_at = compileFinishedBefore,
                force = force,
                warning = warning,
                dirty_scenes = dirtyScenes,
            });
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
