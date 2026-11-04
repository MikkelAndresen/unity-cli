using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "screenshot", SkipCompilationGate = true,
        Description = "Capture a PNG. Modes: gameview (the running game's frame as the Game view renders it, includes the game's OnGUI/IMGUI; default in Play mode), camera (Camera.main rendered offscreen into a render texture: no OnGUI, no editor overlays), scene (Scene view camera rendered offscreen: no gizmos, no Handles, no Scene-view UI; default in Edit mode). No mode reads screen pixels of an editor window, so a window moved off any display cannot break a capture.")]
    public static class EditorScreenshot
    {
        private const int DefaultWidth = 1920;
        private const int DefaultHeight = 1080;
        private const string DefaultOutputPath = "Screenshots/screenshot.png";

        // ScreenCapture schedules its write for the end of a frame, so the wait covers the
        // frame rate, not the render: measured ~3 editor updates for a play-mode capture,
        // 5 s covers an idle-throttled editor.
        private const int GameCaptureTimeoutMs = 5000;

        public class Parameters
        {
            // Attribute arguments need compile-time constants, so this repeats ModeNames
            // instead of interpolating it (CS0182).
            [ToolParameter("Capture mode: gameview (the running game's frame, includes the game's OnGUI/IMGUI), camera (offscreen camera render, no OnGUI or editor overlays), scene (Scene view camera render, no gizmos or Handles). Default: gameview in Play mode, scene in Edit mode.")]
            public string Mode { get; set; }

            [ToolParameter("Older spelling of mode: 'scene' -> scene, 'game' -> camera (the offscreen render). Prefer --mode.", Required = false)]
            public string View { get; set; }

            [ToolParameter("Override width for mode camera/scene (default 1920). mode gameview keeps the Game view's own resolution", Required = false)]
            public int Width { get; set; }

            [ToolParameter("Override height for mode camera/scene (default 1080). mode gameview keeps the Game view's own resolution", Required = false)]
            public int Height { get; set; }

            [ToolParameter("Output file path, absolute or relative to project root (default: Screenshots/screenshot.png)", Required = false)]
            public string OutputPath { get; set; }
        }

        public static async Task<object> HandleCommand(JObject @params)
        {
            if (@params == null)
                @params = new JObject();

            var p = new ToolParams(@params);
            var resolution = ScreenshotModes.Resolve(p.Get("mode"), p.Get("view"), EditorApplication.isPlaying);
            if (resolution.Error != null)
                return new ErrorResponse(resolution.Error);

            var width = p.GetInt("width", DefaultWidth).Value;
            var height = p.GetInt("height", DefaultHeight).Value;
            var sizeRequested = p.GetRaw("width") != null || p.GetRaw("height") != null;
            var outputPath = ResolveOutputPath(p.Get("output_path"));

            try
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // The usual cause is a path that only exists in the caller's shell, not
                        // on the machine the editor runs on.
                        return new ErrorResponse(
                            $"Cannot create the output directory {dir}: {ex.GetType().Name}: {ex.Message}. " +
                            "The path is resolved by the editor, so it must be writable on that machine " +
                            "(or relative to the project root).");
                    }
                }

                switch (resolution.Mode)
                {
                    case ScreenshotMode.Scene:
                        return CaptureSceneView(width, height, outputPath);
                    case ScreenshotMode.Camera:
                        return CaptureMainCamera(width, height, outputPath);
                    case ScreenshotMode.GameView:
                        return await CaptureRunningGameAsync(outputPath, sizeRequested);
                    default:
                        return new ErrorResponse($"Unsupported mode '{resolution.Mode}'. Valid: {ScreenshotModes.ModeNames}.");
                }
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Screenshot failed: {e.Message}");
            }
        }

        private static string ResolveOutputPath(string userPath)
        {
            if (string.IsNullOrEmpty(userPath))
                userPath = DefaultOutputPath;

            if (Path.IsPathRooted(userPath))
                return Path.GetFullPath(userPath);

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.Combine(projectRoot, userPath));
        }

        private static object CaptureSceneView(int width, int height, string outputPath)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (!sceneView)
                return new ErrorResponse("No active SceneView found.");

            var camera = sceneView.camera;
            if (!camera)
                return new ErrorResponse("SceneView camera is null.");

            return CaptureCamera(camera, width, height, outputPath, ScreenshotMode.Scene);
        }

        private static object CaptureMainCamera(int width, int height, string outputPath)
        {
            var camera = Camera.main;
            if (!camera)
            {
#if UNITY_2023_1_OR_NEWER
                camera = UnityEngine.Object.FindFirstObjectByType<Camera>();
#else
                camera = UnityEngine.Object.FindObjectOfType<Camera>();
#endif
                if (!camera)
                    return new ErrorResponse("No camera found in scene.");
            }

            return CaptureCamera(camera, width, height, outputPath, ScreenshotMode.Camera);
        }

        private static object CaptureCamera(Camera camera, int width, int height, string outputPath,
            ScreenshotMode mode)
        {
            var previousRT = camera.targetTexture;
            RenderTexture rt = null;
            Texture2D tex = null;

            try
            {
                rt = new RenderTexture(width, height, 24);
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                File.WriteAllBytes(outputPath, tex.EncodeToPNG());

                return new SuccessResponse($"Screenshot saved to {outputPath}",
                    new
                    {
                        path = outputPath,
                        mode = ScreenshotModes.Label(mode),
                        width,
                        height,
                        size_bytes = new FileInfo(outputPath).Length,
                    });
            }
            finally
            {
                camera.targetTexture = previousRT;
                RenderTexture.active = null;
                if (rt) UnityEngine.Object.DestroyImmediate(rt);
                if (tex) UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// Asks the running game for its own frame and answers when the file Unity writes
        /// for it has landed. ScreenCapture's write happens at the end of a frame, so this
        /// awaits editor updates instead of blocking the main thread waiting for the file.
        /// </summary>
        private static async Task<object> CaptureRunningGameAsync(string outputPath, bool sizeRequested)
        {
            if (!EditorApplication.isPlaying)
                return new ErrorResponse(
                    "mode gameview captures the frame the running game produces, and the editor is not " +
                    "in Play mode, so there is no such frame (ScreenCapture writes no file in Edit mode). " +
                    "Enter Play mode with 'unity-cli editor play --wait', or capture with --mode camera "
                    + "or --mode scene.");

            if (EditorApplication.isPaused)
                return new ErrorResponse(
                    "The editor is paused, so the game loop produces no frame to capture. Resume it " +
                    "('unity-cli editor pause' toggles pause), or capture with --mode camera.");

            if (!TryFindGameView(out var gameView, out var gameViewError))
                return new ErrorResponse(gameViewError);

            // A Game view sitting behind another tab renders on its own schedule; focusing and
            // repainting is what makes a frame arrive for the capture to catch.
            gameView.Focus();
            InternalEditorUtility.RepaintAllViews();
            EditorApplication.QueuePlayerLoopUpdate();

            if (File.Exists(outputPath))
                File.Delete(outputPath);

            try
            {
                ScreenCapture.CaptureScreenshot(outputPath);
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is IOException)
            {
                // Unity surfaces an unusable output path as IOException("Operation not supported")
                // and a missing backbuffer as NotSupportedException; neither says which, so name
                // what a caller can act on.
                return new ErrorResponse(
                    $"ScreenCapture refused to capture {outputPath} ({ex.GetType().Name}: {ex.Message}). " +
                    "The path must be writable by the editor process; if the issue is the Game view not " +
                    "rendering, capture with --mode camera, which renders a camera into a render texture " +
                    "without waiting on the game loop.");
            }

            var deadline = new Deadline(GameCaptureTimeoutMs);
            var updates = 0;
            var previousLength = -1L;
            var stableUpdates = 0;

            while (!deadline.Expired)
            {
                await NextEditorUpdate();
                updates++;

                EditorApplication.QueuePlayerLoopUpdate();
                InternalEditorUtility.RepaintAllViews();

                var length = ProbeCaptureFile(outputPath);
                if (length > 0 && length == previousLength)
                {
                    // Unchanged across updates: the write is finished, not merely started.
                    if (++stableUpdates >= 2)
                        return GameCaptureResult(outputPath, length, updates, sizeRequested);
                }
                else
                {
                    stableUpdates = 0;
                    previousLength = length;
                }
            }

            var lastLength = ProbeCaptureFile(outputPath);
            return new ErrorResponse(
                $"ScreenCapture never finished writing {outputPath} within {GameCaptureTimeoutMs}ms " +
                $"({updates} editor updates; {DescribeProbe(lastLength)}). The Game view may not be " +
                "rendering: check 'unity-cli status' for a paused editor, or capture with --mode camera, " +
                "which renders a camera into a render texture without waiting on the game loop.",
                new
                {
                    path = outputPath,
                    mode = ScreenshotModes.Label(ScreenshotMode.GameView),
                    updates,
                    size_bytes = lastLength > 0 ? lastLength : 0,
                });
        }

        private static object GameCaptureResult(string outputPath, long length, int updates, bool sizeRequested)
        {
            var hasSize = ImageDimensions.TryReadPng(outputPath, out var pngWidth, out var pngHeight);
            int? width = hasSize ? pngWidth : (int?)null;
            int? height = hasSize ? pngHeight : (int?)null;

            var note = sizeRequested
                ? "width/height do not apply to mode gameview: the capture keeps the Game view's own "
                  + "resolution. Use --mode camera to choose the size."
                : null;

            return new SuccessResponse(
                $"Game view screenshot saved to {outputPath}" +
                (hasSize ? $" ({pngWidth}x{pngHeight})" : $" ({length} bytes)"),
                new
                {
                    path = outputPath,
                    mode = ScreenshotModes.Label(ScreenshotMode.GameView),
                    width,
                    height,
                    size_bytes = length,
                    updates,
                    note,
                });
        }

        /// <summary>
        /// UnityEditor.GameView is internal, and its module has moved between Unity versions,
        /// so the window is located by name and used through the public EditorWindow base.
        /// </summary>
        private static bool TryFindGameView(out EditorWindow window, out string error)
        {
            window = null;
            error = null;

            var gameViewType = Type.GetType("UnityEditor.GameView, UnityEditor.CoreModule")
                ?? Type.GetType("UnityEditor.GameView, UnityEditor");
            if (gameViewType == null)
            {
                error = "Cannot resolve UnityEditor.GameView in this editor version.";
                return false;
            }

            foreach (var found in Resources.FindObjectsOfTypeAll(gameViewType))
            {
                if (found is EditorWindow existing && existing != null)
                {
                    window = existing;
                    return true;
                }
            }

            try
            {
                window = EditorWindow.GetWindow(gameViewType, false, null, true);
            }
            catch (Exception ex)
            {
                window = null;
                error = $"No Game view window is open, and opening one failed: {ex.Message}";
                return false;
            }

            if (window == null)
                error = "No Game view window is open, and opening one did nothing.";

            return window != null;
        }

        private static Task NextEditorUpdate()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler()
            {
                EditorApplication.update -= Handler;
                tcs.TrySetResult(true);
            }

            EditorApplication.update += Handler;
            return tcs.Task;
        }

        /// -1 when the file is missing or its size cannot be read while Unity writes it.
        private static long ProbeCaptureFile(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (IOException)
            {
                return -1;
            }
            catch (UnauthorizedAccessException)
            {
                return -1;
            }
        }

        static string DescribeProbe(long length)
        {
            if (length > 0) return $"last seen at {length} bytes";
            return length == 0 ? "it exists but is still empty" : "it never appeared";
        }
    }
}
