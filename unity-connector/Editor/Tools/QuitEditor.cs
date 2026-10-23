using System.Diagnostics;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

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

            var response = new
            {
                pid = Process.GetCurrentProcess().Id,
                projectPath = Application.dataPath.Replace("/Assets", ""),
            };

            ScheduleExit(save);
            return new SuccessResponse("Quitting Unity editor.", response);
        }

        static void ScheduleExit(bool save)
        {
            // HandleRequest writes the reply from a thread-pool continuation after this
            // handler returns, so exiting in the same frame can close the socket first.
            double exitAt = EditorApplication.timeSinceStartup + ExitDelaySeconds;

            void Tick()
            {
                if (EditorApplication.timeSinceStartup < exitAt)
                    return;

                EditorApplication.update -= Tick;

                if (save)
                {
                    // SaveOpenScenes is invalid in or while entering play mode, and
                    // play-mode scene state is reverted anyway. Do not wait for edit
                    // mode: a project with domain reload enabled would wipe this
                    // pending callback. Assets are still saved, then quit this tick.
                    if (!EditorApplication.isPlayingOrWillChangePlaymode)
                        EditorSceneManager.SaveOpenScenes();
                    AssetDatabase.SaveAssets();
                }

                // Exit does not prompt to save dirty scenes (unlike File/Quit) and tears
                // down play mode as part of process shutdown.
                EditorApplication.Exit(0);
            }

            EditorApplication.update += Tick;
        }
    }
}
