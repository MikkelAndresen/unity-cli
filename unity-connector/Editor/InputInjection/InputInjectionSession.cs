using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityCliConnector.Tools
{
    /// <summary>
    /// Session state for the `input` tool: the InputSettings override that lets injected events reach the
    /// player while the Game View lacks focus, the virtual devices the tool added, and the player-loop pump
    /// that consumes queued events inside the same CLI call.
    ///
    /// Enter Play Mode Settings with domain reload disabled keeps these statics alive across play sessions,
    /// so every play-mode transition clears them explicitly.
    /// </summary>
    internal static class InputInjectionSession
    {
        // InputManager computes gameHasFocus as applicationHasFocus || gameShouldGetInputRegardlessOfFocus
        // (|| editor event passthrough), and gameShouldGetInputRegardlessOfFocus is only true when BOTH
        // settings below are set together. With either left at its default, events queued while the Game View
        // is unfocused are deferred into editor updates (PointersAndKeyboardsRespectGameViewFocus) or dropped,
        // and keyboard/pointer devices report canRunInBackground == false, so a focus loss disables them and
        // their state stops updating. Setting both is what makes injection focus-independent.
        private const InputSettings.EditorInputBehaviorInPlayMode GameViewAlwaysGetsInput =
            InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        private const InputSettings.BackgroundBehavior IgnoreGameViewFocus =
            InputSettings.BackgroundBehavior.IgnoreFocus;

        // A play loop that never ticks (paused, or an editor that stopped updating) must not hang the call.
        private const double PumpSafetyTimeoutSeconds = 5.0;

        private static InputSettings.EditorInputBehaviorInPlayMode? s_SavedEditorBehavior;
        private static InputSettings.BackgroundBehavior? s_SavedBackgroundBehavior;
        private static readonly HashSet<(InputDevice device, InputControl control)> s_HeldControls = new();
        private static readonly List<InputDevice> s_AddedDevices = new();
        private static readonly List<InputDevice> s_WrittenDevices = new();
        private static bool s_HandlersRegistered;

        public static bool HasSettingsOverride => s_SavedEditorBehavior.HasValue;

        public static IReadOnlyList<InputDevice> AddedDevices => s_AddedDevices;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            if (s_HandlersRegistered) return;
            s_HandlersRegistered = true;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            // A reload while a key is held must not leave the override baked into the settings asset.
            AssemblyReloadEvents.beforeAssemblyReload += RestoreSettings;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.ExitingPlayMode:
                    RestoreSettings();
                    // Do the device cleanup while play mode is still running: that is the last moment the
                    // devices added by the tool are guaranteed to report added == true. Leaving play mode
                    // does not remove them by itself when Enter Play Mode Options skip the domain reload.
                    RemoveAddedDevices();
                    PruneRemovedDevices(s_WrittenDevices);
                    s_HeldControls.Clear();
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    RestoreSettings();
                    RemoveAddedDevices();
                    PruneRemovedDevices(s_WrittenDevices);
                    s_HeldControls.Clear();
                    break;
            }
        }

        private static void PruneRemovedDevices(List<InputDevice> devices)
        {
            for (var i = devices.Count - 1; i >= 0; --i)
                if (devices[i] is not { added: true })
                    devices.RemoveAt(i);
        }

        /// <summary>
        /// Applies the focus-neutral settings, remembering what the user had. Called before every injection.
        /// </summary>
        public static void BeginFocusNeutralSettings()
        {
            if (HasSettingsOverride) return;

            var settings = InputSystem.settings;
            s_SavedEditorBehavior = settings.editorInputBehaviorInPlayMode;
            s_SavedBackgroundBehavior = settings.backgroundBehavior;
            settings.backgroundBehavior = IgnoreGameViewFocus;
            settings.editorInputBehaviorInPlayMode = GameViewAlwaysGetsInput;
        }

        public static void RestoreSettings()
        {
            if (!HasSettingsOverride) return;

            var settings = InputSystem.settings;
            settings.backgroundBehavior = s_SavedBackgroundBehavior.GetValueOrDefault();
            settings.editorInputBehaviorInPlayMode = s_SavedEditorBehavior.GetValueOrDefault();
            s_SavedEditorBehavior = null;
            s_SavedBackgroundBehavior = null;
        }

        /// <summary>
        /// Tracks which controls the tool left in a non-neutral state, so the settings override can be
        /// dropped again once nothing is held.
        /// </summary>
        public static void NoteHeld(InputDevice device, InputControl control)
        {
            s_HeldControls.Add((device, control));
        }

        public static void NoteReleased(InputDevice device, InputControl control)
        {
            s_HeldControls.Remove((device, control));
        }

        public static void RestoreSettingsIfIdle()
        {
            if (s_HeldControls.Count == 0) RestoreSettings();
        }

        public static void ClearHeldControls()
        {
            s_HeldControls.Clear();
        }

        public static object SettingsOverrideData()
        {
            var settings = InputSystem.settings;
            return new
            {
                override_active = HasSettingsOverride,
                update_mode = settings.updateMode.ToString(),
                editor_input_behavior_in_play_mode = settings.editorInputBehaviorInPlayMode.ToString(),
                background_behavior = settings.backgroundBehavior.ToString(),
                saved_editor_input_behavior = s_SavedEditorBehavior?.ToString(),
                saved_background_behavior = s_SavedBackgroundBehavior?.ToString(),
                held_controls = s_HeldControls
                    .Select(entry => $"{entry.control.name}@{entry.device.name}")
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray(),
            };
        }

        /// <summary>
        /// The device the tool writes to, added as a virtual device when the project has none.
        /// Added devices are tracked so `reset` can remove them again.
        /// </summary>
        public static TDevice GetOrAddDevice<TDevice>()
            where TDevice : InputDevice
        {
            var existing = InputSystem.GetDevice<TDevice>();
            if (existing != null) return existing;

            var added = InputSystem.AddDevice<TDevice>();
            s_AddedDevices.Add(added);
            return added;
        }

        public static void NoteWrittenDevice(InputDevice device)
        {
            if (device != null && !s_WrittenDevices.Contains(device))
                s_WrittenDevices.Add(device);
        }

        /// <summary>
        /// Queues a reset for every device the tool wrote to (releases held keys/buttons/triggers).
        /// </summary>
        public static int ResetWrittenDevices()
        {
            var count = 0;
            for (var i = s_WrittenDevices.Count - 1; i >= 0; --i)
            {
                var device = s_WrittenDevices[i];
                if (device is { added: true })
                {
                    InputSystem.ResetDevice(device);
                    ++count;
                }
                s_WrittenDevices.RemoveAt(i);
            }
            return count;
        }

        public static int RemoveAddedDevices()
        {
            var count = 0;
            for (var i = s_AddedDevices.Count - 1; i >= 0; --i)
            {
                var device = s_AddedDevices[i];
                s_AddedDevices.RemoveAt(i);
                if (device is not { added: true }) continue;
                // The device may also have been written to; reset first so nothing latches in the buffers.
                InputSystem.ResetDevice(device);
                InputSystem.RemoveDevice(device);
                ++count;
            }
            return count;
        }

        /// <summary>
        /// Advances the player loop so game code runs against the state that was just queued, and so a held
        /// injection stays pressed for the requested duration before its release is queued.
        /// Completes on the next EditorApplication.update that satisfies both the frame count and the hold time.
        /// </summary>
        public static Task PumpPlayerLoopAsync(int frames, double holdMilliseconds)
        {
            var targetFrames = Math.Max(0, frames);
            var holdSeconds = Math.Max(0, holdMilliseconds) * 0.001;
            if (targetFrames == 0 && holdSeconds <= 0) return Task.CompletedTask;
            if (!EditorApplication.isPlaying) return Task.CompletedTask;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = EditorApplication.timeSinceStartup;
            var remaining = targetFrames;

            void Tick()
            {
                if (!EditorApplication.isPlaying)
                {
                    Finish();
                    return;
                }

                --remaining;
                var elapsed = EditorApplication.timeSinceStartup - start;
                if ((remaining <= 0 && elapsed >= holdSeconds) || elapsed >= PumpSafetyTimeoutSeconds + holdSeconds)
                {
                    Finish();
                    return;
                }

                PumpOneTick();
            }

            void Finish()
            {
                EditorApplication.update -= Tick;
                tcs.TrySetResult(true);
            }

            PumpOneTick();
            EditorApplication.update += Tick;
            return tcs.Task;
        }

        private static void PumpOneTick()
        {
            try { EditorApplication.QueuePlayerLoopUpdate(); }
            catch (Exception)
            {
            }
            try { InternalEditorUtility.RepaintAllViews(); }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Whether a Game View currently has editor focus — what the Input System uses to decide whether the
        /// player gets input at all. GameView is internal, so the window is located by type name.
        /// </summary>
        public static bool GameViewHasFocus()
        {
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null) return false;

            foreach (var window in Resources.FindObjectsOfTypeAll(gameViewType))
                if (window is EditorWindow { hasFocus: true })
                    return true;
            return false;
        }

        public static object FocusData()
        {
            return new
            {
                game_view_focused = GameViewHasFocus(),
                // What the Input System's own focus gating reads in the editor (InputManager.applicationHasFocus).
                // It can disagree with EditorApplication.isFocused: with a Game View open, play mode reports the
                // player as focused even while the editor is not the frontmost application.
                application_is_focused = UnityEngine.Application.isFocused,
                editor_focused = EditorApplication.isFocused,
                settings_override_active = HasSettingsOverride,
            };
        }
    }
}
