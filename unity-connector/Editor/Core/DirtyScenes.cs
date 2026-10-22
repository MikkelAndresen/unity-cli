using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityCliConnector
{
    /// <summary>
    /// Shared guard for commands that would lose or ignore unsaved scene changes
    /// (running tests, quitting the editor).
    /// </summary>
    public static class DirtyScenes
    {
        /// <summary>
        /// Returns null when the command may proceed, or an ErrorResponse naming the
        /// blocking scenes. autoSave saves them first; allowDirty proceeds without saving.
        /// </summary>
        public static object Prepare(bool allowDirty, bool autoSave, string blockedMessage)
        {
            var dirtyScenes = GetDirtyOpenScenes();
            if (dirtyScenes.Count == 0)
                return null;

            if (autoSave)
            {
                foreach (var scene in dirtyScenes)
                {
                    if (string.IsNullOrEmpty(scene.path))
                        return BlockedResponse(blockedMessage, new List<string> { DisplayName(scene) });
                    if (!EditorSceneManager.SaveScene(scene))
                        return BlockedResponse(blockedMessage, new List<string> { scene.path });
                }
                return null;
            }

            if (allowDirty)
                return null;

            var paths = new List<string>();
            foreach (var scene in dirtyScenes)
                paths.Add(DisplayName(scene));

            return BlockedResponse(blockedMessage, paths);
        }

        static string DisplayName(Scene scene)
        {
            if (!string.IsNullOrEmpty(scene.path)) return scene.path;
            return string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name;
        }

        static List<Scene> GetDirtyOpenScenes()
        {
            var scenes = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                    scenes.Add(scene);
            }
            return scenes;
        }

        static ErrorResponse BlockedResponse(string message, List<string> scenes)
        {
            return new ErrorResponse(message, new
            {
                blocked = "dirty-scenes",
                message,
                scenes,
            });
        }
    }
}
