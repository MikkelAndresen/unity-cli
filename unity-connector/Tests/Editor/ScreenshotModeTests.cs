using NUnit.Framework;

namespace UnityCliConnector.Tests
{
    [TestFixture]
    public class ScreenshotModeTests
    {
        [Test]
        public void Resolve_NeitherNamed_PlayStateChooses()
        {
            var playing = ScreenshotModes.Resolve(null, null, isPlaying: true);
            Assert.AreEqual(ScreenshotMode.GameView, playing.Mode);
            Assert.IsNull(playing.Fallback);
            Assert.IsNull(playing.Error);

            var paused = ScreenshotModes.Resolve(null, null, isPlaying: true, isPaused: true);
            Assert.AreEqual(ScreenshotMode.Camera, paused.Mode,
                "a paused game renders no Game view frame, so the bare call must still capture");
            Assert.AreEqual("paused", paused.Fallback);

            var editing = ScreenshotModes.Resolve(null, null, isPlaying: false);
            Assert.AreEqual(ScreenshotMode.Scene, editing.Mode);
            Assert.IsNull(editing.Fallback);
        }

        [Test]
        public void Resolve_NamedMode_IsNotADefault()
        {
            var resolution = ScreenshotModes.Resolve("scene", null, isPlaying: true);
            Assert.AreEqual(ScreenshotMode.Scene, resolution.Mode);
            Assert.IsNull(resolution.Fallback);

            var explicitGameView = ScreenshotModes.Resolve("gameview", null, isPlaying: true, isPaused: true);
            Assert.AreEqual(ScreenshotMode.GameView, explicitGameView.Mode,
                "an explicit --mode gameview while paused still reports that there is no frame");
            Assert.IsNull(explicitGameView.Fallback);
        }

        [Test]
        public void Resolve_ModeValues_MapEveryName()
        {
            Assert.AreEqual(ScreenshotMode.GameView, ScreenshotModes.Resolve("gameview", null, false).Mode);
            Assert.AreEqual(ScreenshotMode.GameView, ScreenshotModes.Resolve("Game View", null, false).Mode);
            Assert.AreEqual(ScreenshotMode.GameView, ScreenshotModes.Resolve("game_view", null, false).Mode);
            Assert.AreEqual(ScreenshotMode.Camera, ScreenshotModes.Resolve("camera", null, false).Mode);
            Assert.AreEqual(ScreenshotMode.Camera, ScreenshotModes.Resolve(" CAMERA ", null, false).Mode);
            Assert.AreEqual(ScreenshotMode.Scene, ScreenshotModes.Resolve("scene", null, true).Mode);
        }

        [Test]
        public void Resolve_GameNamesTheGameViewUnderModeButTheCameraUnderTheLegacyView()
        {
            // "--view game" is the shipped spelling of the offscreen camera render; reusing it
            // for the Game view would silently change what existing calls capture.
            Assert.AreEqual(ScreenshotMode.GameView, ScreenshotModes.Resolve("game", null, true).Mode);
            Assert.AreEqual(ScreenshotMode.Camera, ScreenshotModes.Resolve(null, "game", true).Mode);
            Assert.AreEqual(ScreenshotMode.Scene, ScreenshotModes.Resolve(null, "scene", true).Mode);
        }

        [Test]
        public void Resolve_ModeWinsWhenBothAreGiven()
        {
            Assert.AreEqual(ScreenshotMode.GameView, ScreenshotModes.Resolve("gameview", "scene", false).Mode);
        }

        [Test]
        public void Resolve_UnknownValue_NamesTheValidModesAndTheLegacyMapping()
        {
            var resolution = ScreenshotModes.Resolve("viewer", null, false);
            StringAssert.Contains("Unknown mode 'viewer'", resolution.Error);
            StringAssert.Contains("gameview", resolution.Error);
            StringAssert.Contains("--view game", resolution.Error);

            resolution = ScreenshotModes.Resolve(null, "viewer", false);
            StringAssert.Contains("Unknown view 'viewer'", resolution.Error);
        }

        [Test]
        public void Label_RoundTripsEveryModeName()
        {
            Assert.AreEqual("gameview", ScreenshotModes.Label(ScreenshotMode.GameView));
            Assert.AreEqual("camera", ScreenshotModes.Label(ScreenshotMode.Camera));
            Assert.AreEqual("scene", ScreenshotModes.Label(ScreenshotMode.Scene));
        }
    }
}
