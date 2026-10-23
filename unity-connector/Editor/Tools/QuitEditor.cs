using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Description = "Quit the Unity editor gracefully, optionally saving open scenes and assets first.",
        SkipCompilationGate = true)]
    public static class QuitEditor
    {
        // Long enough for the HTTP response to flush before the process is torn down,
        // short enough that a caller can wait on the editor process to disappear.
        const double ExitDelaySeconds = 0.5;

        public class Parameters
        {
            [ToolParameter("Save open scenes and assets before quitting")]
            public bool Save { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            bool save = p.GetBool("save");

            if (save)
            {
                var error = SaveBeforeQuit();
                if (error != null)
                    return error;
            }

            var response = new
            {
                pid = Process.GetCurrentProcess().Id,
                projectPath = Application.dataPath.Replace("/Assets", ""),
            };

            ScheduleExit();
            return new SuccessResponse("Quitting Unity editor.", response);
        }

        // Saves synchronously so a failure is reported and the editor stays up.
        // SaveOpenScenes is not used: for an untitled scene it opens a "Save Scene"
        // file panel, which parks the editor and cannot be answered from the CLI.
        static ErrorResponse SaveBeforeQuit()
        {
            // Scene saves are invalid in or while entering play mode, and play-mode
            // scene state is reverted anyway. Assets are still saved.
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                var untitled = new List<string>();
                var dirty = new List<Scene>();
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isDirty)
                        continue;
                    if (string.IsNullOrEmpty(scene.path))
                        untitled.Add(scene.name);
                    else
                        dirty.Add(scene);
                }

                if (untitled.Count > 0)
                {
                    const string message = "Untitled scenes have unsaved changes and no path to save to. Save them from the editor, or quit without --save to discard them.";
                    return new ErrorResponse(message, new { blocked = "untitled-scenes", message, scenes = untitled });
                }

                foreach (var scene in dirty)
                {
                    if (!EditorSceneManager.SaveScene(scene))
                        return new ErrorResponse($"Failed to save scene {scene.path}; Unity was not quit.");
                }
            }

            AssetDatabase.SaveAssets();
            return null;
        }

        static void ScheduleExit()
        {
            // HandleRequest writes the reply from a thread-pool continuation after this
            // handler returns, so exiting in the same frame can close the socket first.
            double exitAt = EditorApplication.timeSinceStartup + ExitDelaySeconds;

            void Tick()
            {
                if (EditorApplication.timeSinceStartup < exitAt)
                    return;

                EditorApplication.update -= Tick;

                // Exit does not prompt to save dirty scenes (unlike File/Quit) and tears
                // down play mode as part of process shutdown.
                EditorApplication.Exit(0);
            }

            EditorApplication.update += Tick;
        }
    }
}
