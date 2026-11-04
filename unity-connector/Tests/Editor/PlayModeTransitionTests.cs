using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.Tests
{
    /// <summary>
    /// A command that changes Play mode must answer before the change it starts can reload the domain
    /// it is answering from. The deferral and the reload prediction are asserted here; the round trip
    /// against a live editor is recorded in docs/bugs/10-reload-loses-response.md.
    /// </summary>
    [TestFixture]
    public class PlayModeTransitionTests
    {
        Action<Action> s_RealScheduler;

        [SetUp]
        public void SetUp()
        {
            s_RealScheduler = PlayModeTransitions.Scheduler;
        }

        [TearDown]
        public void TearDown()
        {
            // The seam is static, and without domain reloads a leaked test scheduler would keep
            // swallowing play-mode changes for every later test in the run.
            PlayModeTransitions.Scheduler = s_RealScheduler;
        }

        [Test]
        public void DomainReloadExpected_OnlyDisableDomainReloadSkipsIt()
        {
            Assert.IsTrue(PlayModeTransitions.DomainReloadExpected(false, EnterPlayModeOptions.DisableDomainReload),
                "Enter Play Mode Options off means a reload on every Play mode change, whatever the options say");
            Assert.IsTrue(PlayModeTransitions.DomainReloadExpected(true, EnterPlayModeOptions.None));
            Assert.IsTrue(PlayModeTransitions.DomainReloadExpected(true, EnterPlayModeOptions.DisableSceneReload),
                "skipping the scene reload alone still reloads the domain");
            Assert.IsFalse(PlayModeTransitions.DomainReloadExpected(true, EnterPlayModeOptions.DisableDomainReload));
            Assert.IsFalse(PlayModeTransitions.DomainReloadExpected(true,
                EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload));
        }

        [Test]
        public void DomainReloadExpected_MatchesWhatThisProjectIsConfiguredFor()
        {
            // The pure form above is the rule; this pins it to the API the rule reads, so a renamed or
            // inverted EditorSettings property shows up here instead of in a hung CLI call.
            Assert.AreEqual(
                PlayModeTransitions.DomainReloadExpected(EditorSettings.enterPlayModeOptionsEnabled,
                    EditorSettings.enterPlayModeOptions),
                PlayModeTransitions.DomainReloadExpected());
        }

        [Test]
        public void ApplyOnNextTick_ChangesNothingBeforeTheAnswerIsWritten()
        {
            Assert.IsFalse(EditorApplication.isPlaying, "this fixture assumes Edit mode");

            Action deferred = null;
            PlayModeTransitions.Scheduler = action => deferred = action;

            PlayModeTransitions.ApplyOnNextTick(play: true);

            Assert.IsNotNull(deferred, "the change must be handed to a later tick");
            Assert.IsFalse(EditorApplication.isPlaying,
                "entering Play mode inline would let the reload it starts outrun this command's response");
        }

        [Test]
        public void ChangeStartedSince_ReportsEveryTransitionWithoutGuessing()
        {
            // Nothing changed: an ordinary exec must not claim a state change.
            Assert.IsNull(PlayModeTransitions.ChangeStartedSince(
                wasPlaying: false, wasChanging: false, wasCompiling: false,
                nowPlaying: false, nowChanging: false, compiling: false));
            Assert.IsNull(PlayModeTransitions.ChangeStartedSince(
                wasPlaying: true, wasChanging: true, wasCompiling: false,
                nowPlaying: true, nowChanging: true, compiling: false),
                "the play-mode flags stay set across an exit, so there is nothing to report yet");

            // Requested entry, seen before and after the transition completes.
            Assert.AreEqual("a play mode change is in flight", PlayModeTransitions.ChangeStartedSince(
                wasPlaying: false, wasChanging: false, wasCompiling: false,
                nowPlaying: false, nowChanging: true, compiling: false));
            Assert.AreEqual("entered play mode", PlayModeTransitions.ChangeStartedSince(
                wasPlaying: false, wasChanging: false, wasCompiling: false,
                nowPlaying: true, nowChanging: true, compiling: false));

            // A completed exit is only visible as isPlaying going from true to false.
            Assert.AreEqual("left play mode", PlayModeTransitions.ChangeStartedSince(
                wasPlaying: true, wasChanging: true, wasCompiling: false,
                nowPlaying: false, nowChanging: false, compiling: false));

            // A snippet that asked for a compile: reported only once the compile is actually running.
            Assert.IsNull(PlayModeTransitions.ChangeStartedSince(
                wasPlaying: false, wasChanging: false, wasCompiling: false,
                nowPlaying: false, nowChanging: false, compiling: false));
            Assert.AreEqual("a script compilation is in flight", PlayModeTransitions.ChangeStartedSince(
                wasPlaying: false, wasChanging: false, wasCompiling: false,
                nowPlaying: false, nowChanging: false, compiling: true));
        }

        [Test]
        public void ChangeStartedSince_DoesNotBlameTheSnippetForACompileAlreadyRunning()
        {
            // A compile that was already running when the snippet started is not the snippet's, and
            // exec must not claim it as one it started.
            Assert.IsNull(PlayModeTransitions.ChangeStartedSince(
                wasPlaying: false, wasChanging: false, wasCompiling: true,
                nowPlaying: false, nowChanging: false, compiling: true),
                "the compile was already in flight when the snippet started");
        }

        [Test]
        public void ChangeStartedSince_MatchesWhatTheEditorReportsRightNow()
        {
            // The pure form above is the rule; this pins the wrapper to the Unity properties it reads,
            // in the one state an EditMode fixture is guaranteed to be in with nothing in flight.
            Assume.That(!EditorApplication.isCompiling && !EditorApplication.isPlayingOrWillChangePlaymode);
            Assert.IsNull(PlayModeTransitions.ChangeStartedSince(
                EditorApplication.isPlaying, EditorApplication.isPlayingOrWillChangePlaymode,
                EditorApplication.isCompiling),
                "reading the change against the state it was read from can only report nothing");
        }
    }
}
