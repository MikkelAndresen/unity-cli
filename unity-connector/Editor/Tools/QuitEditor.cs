using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
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

        // If Exit is vetoed, the process survives and this callback reports which
        // wantsToQuit subscribers were visible when the request was accepted.
        const double VetoReportDelaySeconds = 2.0;

        public class Parameters
        {
            [ToolParameter("Save open scenes and assets before quitting")]
            public bool Save { get; set; }

            [ToolParameter("Discard unsaved scene changes (the default when --save is not set)")]
            public bool Discard { get; set; }
        }

        internal class SceneState
        {
            public readonly List<Scene> DirtySaved = new List<Scene>();
            public readonly List<string> DirtyNames = new List<string>();
            public readonly List<string> Untitled = new List<string>();
        }

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            bool save = p.GetBool("save");
            bool discard = p.GetBool("discard");
            if (save && discard)
                return new ErrorResponse("save and discard are mutually exclusive.");

            var scenes = InspectScenes();
            // Unity refuses scene saves in (or while entering) play mode; the request must not claim
            // those saves happened. Asset saves still run.
            var playMode = EditorApplication.isPlayingOrWillChangePlaymode;
            if (save)
            {
                var error = SaveBeforeQuit(scenes, playMode);
                if (error != null)
                    return error;
            }

            var subscribers = WantsToQuitSubscribers();
            var response = BuildReport(save, discard, playMode, scenes, subscribers);

            ScheduleExit(subscribers);
            var skipped = save && playMode;
            var message = !save
                ? "Quitting Unity editor."
                : skipped
                    ? "Saving assets (scene saves are skipped in play mode), then quitting Unity editor."
                    : "Saving open scenes and assets, then quitting Unity editor.";
            return new SuccessResponse(message, response);
        }

        internal static object BuildReport(bool save, bool discard, bool playMode, SceneState scenes,
            string[] subscribers)
        {
            var saveScenes = save && !playMode;
            return new
            {
                pid = Process.GetCurrentProcess().Id,
                projectPath = Heartbeat.ProjectRootFromDataPath(Application.dataPath),
                save = saveScenes,
                saveSkipped = save && playMode ? "playmode" : null,
                savedScenes = saveScenes ? scenes.DirtySaved.ConvertAll(s => s.path).ToArray() : new string[0],
                savedAssets = save,
                discard = discard || !save,
                dirtyScenes = scenes.DirtyNames.ToArray(),
                discardedScenes = saveScenes ? new string[0] : scenes.DirtyNames.ToArray(),
                untitledScenes = scenes.Untitled.ToArray(),
                wantsToQuitSubscribers = subscribers,
                warning = subscribers.Length == 0
                    ? null
                    : "EditorApplication.wantsToQuit has subscriber(s); a handler that returns false will veto the exit.",
            };
        }

        static SceneState InspectScenes()
        {
            var state = new SceneState();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isDirty)
                    continue;

                if (string.IsNullOrEmpty(scene.path))
                {
                    state.Untitled.Add(scene.name);
                    state.DirtyNames.Add(scene.name);
                }
                else
                {
                    state.DirtySaved.Add(scene);
                    state.DirtyNames.Add(scene.path);
                }
            }
            return state;
        }

        // Saves synchronously so a failure is reported and the editor stays up.
        // SaveOpenScenes is not used: for an untitled scene it opens a "Save Scene"
        // file panel, which parks the editor and cannot be answered from the CLI.
        static ErrorResponse SaveBeforeQuit(SceneState state, bool playMode)
        {
            // Scene saves are invalid in or while entering play mode, and play-mode
            // scene state is reverted anyway. Assets are still saved.
            if (!playMode)
            {
                if (state.Untitled.Count > 0)
                {
                    const string message = "Untitled scenes have unsaved changes and no path to save to. Save them from the editor, or quit with --discard to discard them.";
                    return new ErrorResponse(message, new { blocked = "untitled-scenes", message, scenes = state.Untitled.ToArray() });
                }

                foreach (var scene in state.DirtySaved)
                {
                    if (!EditorSceneManager.SaveScene(scene))
                        return new ErrorResponse($"Failed to save scene {scene.path}; Unity was not quit.");
                }
            }

            AssetDatabase.SaveAssets();
            return null;
        }

        // Unity exposes wantsToQuit as an event with no public getter. Reading its
        // backing delegate is the only way to tell the caller that a subscriber may
        // veto the exit; the handlers themselves are not invoked here.
        static string[] WantsToQuitSubscribers()
        {
            try
            {
                var field = typeof(EditorApplication).GetField("wantsToQuit",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field?.GetValue(null) is not Delegate handlers)
                    return new string[0];

                var names = new List<string>();
                foreach (var handler in handlers.GetInvocationList())
                {
                    var declaringType = handler.Method.DeclaringType;
                    names.Add(declaringType == null
                        ? handler.Method.Name
                        : declaringType.FullName + "." + handler.Method.Name);
                }
                return names.ToArray();
            }
            catch
            {
                return new string[0];
            }
        }

        static void ScheduleExit(string[] subscribers)
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

                // Execution reaches here only if Exit returned without ending the process.
                // Whether Exit consults wantsToQuit at all is not documented, so name it as a
                // possible cause instead of the cause.
                ScheduleVetoReport(subscribers);
            }

            EditorApplication.update += Tick;
        }

        static void ScheduleVetoReport(string[] subscribers)
        {
            double reportAt = EditorApplication.timeSinceStartup + VetoReportDelaySeconds;

            void Check()
            {
                if (EditorApplication.timeSinceStartup < reportAt)
                    return;

                EditorApplication.update -= Check;
                var names = subscribers.Length == 0
                    ? "none were visible"
                    : string.Join(", ", subscribers);
                UnityEngine.Debug.LogError("[UnityCliConnector] quit_editor: Unity is still running after EditorApplication.Exit(0). " +
                               "An EditorApplication.wantsToQuit handler may have returned false (subscribers: " +
                               names + "). Use `unity-cli editor quit --force` on the host or fix the handler.");
            }

            EditorApplication.update += Check;
        }
    }
}
