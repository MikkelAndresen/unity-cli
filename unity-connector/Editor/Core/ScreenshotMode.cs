namespace UnityCliConnector
{
    internal enum ScreenshotMode
    {
        Scene,
        Camera,
        GameView,
    }

    /// Which capture a screenshot call asks for. The table lives outside the tool so the
    /// mode parsing — including what the older `view` spellings mean — runs in EditMode
    /// tests, where no Game view or Play mode is available.
    internal static class ScreenshotModes
    {
        internal readonly struct Resolution
        {
            internal readonly ScreenshotMode Mode;
            /// True when neither mode nor view was named, so the play state chose this.
            internal readonly bool FromDefault;
            internal readonly string Error;

            internal Resolution(ScreenshotMode mode, bool fromDefault = false, string error = null)
            {
                Mode = mode;
                FromDefault = fromDefault;
                Error = error;
            }
        }

        internal static Resolution Resolve(string mode, string view, bool isPlaying)
        {
            string raw;
            bool fromModeParam;

            if (!string.IsNullOrWhiteSpace(mode))
            {
                raw = mode;
                fromModeParam = true;
            }
            else if (!string.IsNullOrWhiteSpace(view))
            {
                raw = view;
                fromModeParam = false;
            }
            else
            {
                // Edit mode keeps the historical bare-call default (the Scene view camera);
                // a running game is better read through its own Game view.
                return new Resolution(isPlaying ? ScreenshotMode.GameView : ScreenshotMode.Scene, fromDefault: true);
            }

            switch (Normalize(raw))
            {
                case "scene": return new Resolution(ScreenshotMode.Scene);
                case "camera": return new Resolution(ScreenshotMode.Camera);
                case "gameview": return new Resolution(ScreenshotMode.GameView);
                // "--view game" named the offscreen Camera.main render before a Game view
                // capture existed, so the old spelling keeps its old meaning. Under --mode,
                // "game" means the Game view.
                case "game": return new Resolution(fromModeParam ? ScreenshotMode.GameView : ScreenshotMode.Camera);
                default:
                    return new Resolution(default, error:
                        $"Unknown {(fromModeParam ? "mode" : "view")} '{raw.Trim()}'. " +
                        $"Valid: {ModeNames}. '--view game' is the older spelling of --mode camera " +
                        "(offscreen camera render); use --mode gameview for the running Game view.");
            }
        }

        internal const string ModeNames =
            "gameview (the running game's frame, includes its OnGUI/IMGUI), camera (offscreen camera " +
            "render, no OnGUI or editor overlays), scene (Scene view camera render, no gizmos or Handles)";

        internal static string Label(ScreenshotMode mode)
        {
            switch (mode)
            {
                case ScreenshotMode.Camera: return "camera";
                case ScreenshotMode.GameView: return "gameview";
                default: return "scene";
            }
        }

        static string Normalize(string raw)
        {
            var buffer = new char[raw.Length];
            var length = 0;

            foreach (var c in raw)
            {
                if (c == ' ' || c == '_' || c == '-') continue;
                buffer[length++] = char.ToLowerInvariant(c);
            }

            return new string(buffer, 0, length);
        }
    }
}
