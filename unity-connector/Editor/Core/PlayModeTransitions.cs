using System;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
    /// <summary>
    /// Play-mode changes asked for by a command. Entering or leaving Play mode can reload the domain,
    /// and a domain that reloads takes the calling command's response with it (the answer is written
    /// from a thread-pool continuation that the reload has no reason to wait for), so the state change
    /// is applied on a later update tick and the answer goes out first.
    /// </summary>
    public static class PlayModeTransitions
    {
        /// <summary>
        /// Defers <paramref name="apply"/> to Unity's next update tick. A seam so the EditMode tests
        /// can assert the deferral without entering Play mode; production is Unity's own pump.
        /// </summary>
        internal static Action<Action> Scheduler = action =>
        {
            void Tick()
            {
                EditorApplication.update -= Tick;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // A deferred callback has no caller left to throw at; the CLI learns the state
                    // never changed by reading the heartbeat back.
                    Debug.LogError($"[UnityCliConnector] Deferred editor state change failed: {ex.Message}");
                }
            }

            EditorApplication.update += Tick;
        };

        /// <summary>
        /// Whether a Play-mode change reloads the domain here. Enter Play Mode Options only skips the
        /// reload when it is enabled AND names <see cref="EnterPlayModeOptions.DisableDomainReload"/>;
        /// a project that never touched the setting reloads on every play-mode entry and exit.
        /// </summary>
        public static bool DomainReloadExpected(bool optionsEnabled, EnterPlayModeOptions options)
        {
            return !optionsEnabled || (options & EnterPlayModeOptions.DisableDomainReload) == 0;
        }

        public static bool DomainReloadExpected()
        {
            return DomainReloadExpected(EditorSettings.enterPlayModeOptionsEnabled,
                EditorSettings.enterPlayModeOptions);
        }

        /// <summary>
        /// Queues the play-mode change for the next tick and returns. The caller must have decided the
        /// answer already: this is the point past which the response may stop being deliverable.
        ///
        /// `EditorApplication.delayCall` is the usual way to say "later", and it does not work here: a
        /// backgrounded editor (the state a CLI-driven editor is in) drains `EditorApplication.update`
        /// but never reaches the tick delayCall runs from, so the change would sit queued forever.
        /// Measured in the FoW project: an exec that registered a delayCall setting isPlaying=true left
        /// the editor in Edit mode indefinitely, while an `update` one-shot ran on the next beat.
        /// </summary>
        public static void ApplyOnNextTick(bool play)
        {
            Scheduler(() =>
            {
                if (EditorApplication.isPlaying == play) return;
                EditorApplication.isPlaying = play;
            });
        }

        /// <summary>
        /// What the snippet set in motion between the captured state and now, or null when nothing is
        /// observable yet. Only what is true at this instant is reported: leaving Play mode keeps
        /// <c>EditorApplication.isPlayingOrWillChangePlaymode</c> set until the change lands, so an exit
        /// is not detectable here and must not be guessed at.
        /// </summary>
        public static string ChangeStartedSince(bool wasPlaying, bool wasChanging)
        {
            return ChangeStartedSince(wasPlaying, wasChanging,
                EditorApplication.isPlaying, EditorApplication.isPlayingOrWillChangePlaymode,
                EditorApplication.isCompiling);
        }

        internal static string ChangeStartedSince(bool wasPlaying, bool wasChanging,
            bool nowPlaying, bool nowChanging, bool compiling)
        {
            if (nowChanging && !wasChanging)
                return nowPlaying && !wasPlaying
                    ? "entered play mode"
                    : "a play mode change is in flight";
            if (wasPlaying && !nowPlaying)
                return "left play mode";
            if (compiling)
                return "a script compilation is in flight";
            return null;
        }
    }
}
