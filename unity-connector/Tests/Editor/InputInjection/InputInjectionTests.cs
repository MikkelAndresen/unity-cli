using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliConnector.Tools;
using UnityEditor;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace UnityCliConnector.Tests
{
    /// <summary>
    /// Param parsing/validation plus the state-injection core (QueueControlState → InputSystem.Update →
    /// control readback) on virtual devices that are removed again in TearDown.
    /// The play-mode-only refusal is asserted here too; the full injection-while-playing path is exercised
    /// live through `unity-cli input` rather than in EditMode, because pumping the player loop needs one.
    /// </summary>
    [TestFixture]
    public class InputInjectionTests
    {
        private Gamepad m_Gamepad;
        private Keyboard m_Keyboard;
        private Mouse m_Mouse;
        private bool m_MouseAdded;

        [SetUp]
        public void SetUp()
        {
            m_Gamepad = InputSystem.AddDevice<Gamepad>();
            m_Keyboard = InputSystem.AddDevice<Keyboard>();
            m_Mouse = InputSystem.GetDevice<Mouse>();
            m_MouseAdded = m_Mouse is null;
            if (m_MouseAdded) m_Mouse = InputSystem.AddDevice<Mouse>();
        }

        [TearDown]
        public void TearDown()
        {
            InputInjectionSession.ClearHeldControls();
            InputInjectionSession.RestoreSettings();
            InputInjectionSession.RemoveAddedDevices();
            if (m_Gamepad is { added: true }) InputSystem.RemoveDevice(m_Gamepad);
            if (m_Keyboard is { added: true }) InputSystem.RemoveDevice(m_Keyboard);
            if (m_MouseAdded && m_Mouse is { added: true }) InputSystem.RemoveDevice(m_Mouse);
            m_Gamepad = null;
            m_Keyboard = null;
            m_Mouse = null;
        }

        // ─── key name resolution ────────────────────────────────

        [TestCase("Space", Key.Space)]
        [TestCase("space", Key.Space)]
        [TestCase("W", Key.W)]
        [TestCase("w", Key.W)]
        [TestCase("A", Key.A)]
        [TestCase("LeftShift", Key.LeftShift)]
        [TestCase("left shift", Key.LeftShift)]
        [TestCase("left_shift", Key.LeftShift)]
        [TestCase("LEFTSHIFT", Key.LeftShift)]
        [TestCase("Enter", Key.Enter)]
        [TestCase("return", Key.Enter)]
        [TestCase("Escape", Key.Escape)]
        [TestCase("esc", Key.Escape)]
        [TestCase("ctrl", Key.LeftCtrl)]
        [TestCase("alt", Key.LeftAlt)]
        [TestCase("shift", Key.LeftShift)]
        [TestCase("cmd", Key.LeftMeta)]
        [TestCase("up", Key.UpArrow)]
        [TestCase("down", Key.DownArrow)]
        [TestCase("1", Key.Digit1)]
        [TestCase("9", Key.Digit9)]
        [TestCase("digit1", Key.Digit1)]
        [TestCase("F1", Key.F1)]
        public void TryResolveKey_ValidNames_Resolves(string text, Key expected)
        {
            Assert.IsTrue(InputInjection.TryResolveKey(text, out var key, out var error), error);
            Assert.AreEqual(expected, key);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("NotAKey")]
        [TestCase("None")]
        [TestCase("mouse")]
        public void TryResolveKey_InvalidNames_RejectedWithExamples(string text)
        {
            Assert.IsFalse(InputInjection.TryResolveKey(text, out var key, out var error));
            Assert.AreEqual(Key.None, key);
            Assert.IsNotEmpty(error);
            Assert.That(error, Does.Contain("Space"), "the error should list usable examples");
        }

        [Test]
        public void TryResolveKey_Null_Rejected()
        {
            Assert.IsFalse(InputInjection.TryResolveKey(null, out _, out var error));
            Assert.That(error, Does.Contain("'key' required"));
        }

        [Test]
        public void TryGetKeyControl_OutsideLayout_Rejected()
        {
            Assert.IsFalse(InputInjection.TryGetKeyControl(m_Keyboard, Key.None, out _, out var error));
            Assert.IsNotEmpty(error);

            Assert.IsTrue(InputInjection.TryGetKeyControl(m_Keyboard, Key.Space, out var control, out error), error);
            Assert.AreEqual(m_Keyboard.spaceKey, control);
        }

        // ─── mode / button / phase validation ───────────────────

        [Test]
        public void TryResolveMode_Absent_UsesFallback()
        {
            Assert.IsTrue(InputInjection.TryResolveMode(null, new[] { "press", "tap" }, "press", out var mode, out _));
            Assert.AreEqual("press", mode);

            Assert.IsTrue(InputInjection.TryResolveMode("  ", new[] { "press", "tap" }, "tap", out mode, out _));
            Assert.AreEqual("tap", mode);
        }

        [Test]
        public void TryResolveMode_CaseInsensitive_AcceptsAllowedValues()
        {
            Assert.IsTrue(InputInjection.TryResolveMode("TAP", new[] { "press", "tap" }, "press", out var mode, out _));
            Assert.AreEqual("tap", mode);
        }

        [TestCase("bogus")]
        [TestCase("hold")]
        public void TryResolveMode_NotAllowed_Rejected(string text)
        {
            Assert.IsFalse(InputInjection.TryResolveMode(text, new[] { "press", "tap" }, "press", out _, out var error));
            Assert.That(error, Does.Contain("press | tap"));
        }

        [TestCase("left", "leftButton")]
        [TestCase("LEFT", "leftButton")]
        [TestCase("primary", "leftButton")]
        [TestCase("right", "rightButton")]
        [TestCase("secondary", "rightButton")]
        [TestCase("middle", "middleButton")]
        [TestCase("forward", "forwardButton")]
        [TestCase("back", "backButton")]
        public void TryResolveMouseButton_ValidNames_Resolves(string text, string expectedControl)
        {
            Assert.IsTrue(InputInjection.TryResolveMouseButton(m_Mouse, text, out var button, out var error), error);
            Assert.AreEqual(expectedControl, button.name);
        }

        [TestCase("")]
        [TestCase("scroll")]
        [TestCase("button4")]
        public void TryResolveMouseButton_InvalidNames_Rejected(string text)
        {
            Assert.IsFalse(InputInjection.TryResolveMouseButton(m_Mouse, text, out _, out var error));
            Assert.That(error, Does.Contain("left | right | middle | forward | back"));
        }

        [TestCase("began", TouchPhase.Began)]
        [TestCase("press", TouchPhase.Began)]
        [TestCase("moved", TouchPhase.Moved)]
        [TestCase("stationary", TouchPhase.Stationary)]
        [TestCase("ended", TouchPhase.Ended)]
        [TestCase("release", TouchPhase.Ended)]
        [TestCase("canceled", TouchPhase.Canceled)]
        [TestCase("", TouchPhase.Began)]
        [TestCase(null, TouchPhase.Began)]
        public void TryResolveTouchPhase_MapsAliases(string text, TouchPhase expected)
        {
            Assert.IsTrue(InputInjection.TryResolveTouchPhase(text, out var phase, out _));
            Assert.AreEqual(expected, phase);
        }

        [Test]
        public void TryResolveTouchPhase_Invalid_Rejected()
        {
            Assert.IsFalse(InputInjection.TryResolveTouchPhase("wiggle", out _, out var error));
            Assert.That(error, Does.Contain("began | moved | stationary | ended"));
        }

        [Test]
        public void TryResolveTouchPlan_PhaseOnIdleSlot_Rejected()
        {
            var idle = new TouchState();
            foreach (var mode in new[] { "moved", "stationary", "ended", "canceled" })
            {
                Assert.IsFalse(InputInjection.TryResolveTouchPlan(mode, idle, out _, out _, out var error), mode);
                Assert.That(error, Does.Contain("no touch is in progress"), mode);
            }
        }

        [Test]
        public void TryResolveTouchPlan_BeganAndTapAllowedOnIdleSlot()
        {
            var idle = new TouchState();

            Assert.IsTrue(InputInjection.TryResolveTouchPlan("began", idle, out var phase, out var tap, out _));
            Assert.AreEqual(TouchPhase.Began, phase);
            Assert.IsFalse(tap);

            Assert.IsTrue(InputInjection.TryResolveTouchPlan("tap", idle, out phase, out tap, out _));
            Assert.AreEqual(TouchPhase.Began, phase);
            Assert.IsTrue(tap);
        }

        [Test]
        public void TryResolveTouchPlan_PhaseOnActiveSlot_Accepted()
        {
            var active = new TouchState { phaseId = (byte)TouchPhase.Began };

            Assert.IsTrue(InputInjection.TryResolveTouchPlan("moved", active, out var phase, out _, out var error), error);
            Assert.AreEqual(TouchPhase.Moved, phase);
        }

        [TestCase(0f, 0f, 100f, 0f, 100f)]
        [TestCase(10f, 25f, 100f, 10f, 75f)]
        [TestCase(10f, 25f, 720f, 10f, 695f)]
        public void PointerCoordinates_ConvertBetweenTopLeftAndPointer(
            float x, float y, float height, float pointerX, float pointerY)
        {
            var pointer = InputInjection.TopLeftToPointer(new UnityEngine.Vector2(x, y), height);
            Assert.That(pointer.x, Is.EqualTo(pointerX).Within(0.001f));
            Assert.That(pointer.y, Is.EqualTo(pointerY).Within(0.001f));

            var topLeft = InputInjection.PointerToTopLeft(pointer, height);
            Assert.That(topLeft.x, Is.EqualTo(x).Within(0.001f));
            Assert.That(topLeft.y, Is.EqualTo(y).Within(0.001f));
        }

        // ─── gamepad control lookup ─────────────────────────────

        [TestCase("leftStick")]
        [TestCase("LEFTSTICK")]
        [TestCase("rightTrigger")]
        [TestCase("buttonSouth")]
        [TestCase("dpad")]
        public void TryResolveControl_MatchesByName(string name)
        {
            Assert.IsTrue(InputInjection.TryResolveControl(m_Gamepad, name, out var control, out var error), error);
            Assert.AreEqual(name.ToLowerInvariant(), control.name.ToLowerInvariant());
        }

        [Test]
        public void TryResolveControl_NestedPath_Matches()
        {
            Assert.IsTrue(InputInjection.TryResolveControl(m_Gamepad, "leftStick/x", out var control, out var error), error);
            Assert.That(control.path, Does.EndWith("/leftStick/x"));
        }

        [Test]
        public void TryResolveControl_Unknown_ListsAvailableControls()
        {
            Assert.IsFalse(InputInjection.TryResolveControl(m_Gamepad, "leftRudder", out _, out var error));
            Assert.That(error, Does.Contain("leftStick"));
            Assert.That(error, Does.Contain("rightTrigger"));
        }

        [Test]
        public void TryResolveGamepad_IndexOutOfRange_Rejected()
        {
            Assert.IsFalse(InputInjection.TryResolveGamepad("17", out _, out var error));
            Assert.That(error, Does.Contain("index 17"));
        }

        [Test]
        public void TryResolveGamepad_DefaultSelector_PicksAPresentDevice()
        {
            Assert.IsTrue(InputInjection.TryResolveGamepad(null, out var gamepad, out _));
            Assert.That(InputSystem.devices, Does.Contain(gamepad));
        }

        [Test]
        public void GamepadWrite_ValueOnVectorControl_Rejected()
        {
            Assert.IsFalse(InputInjection.TryBuildGamepadWrite(m_Gamepad.leftStick, null, 1f, null, null,
                out _, out _, out _, out var error));
            Assert.That(error, Does.Contain("--x/--y"));
        }

        [Test]
        public void GamepadWrite_VectorPressWithoutAxes_Rejected()
        {
            Assert.IsFalse(InputInjection.TryBuildGamepadWrite(m_Gamepad.leftStick, "press", null, null, null,
                out _, out _, out _, out var error));
            Assert.That(error, Does.Contain("needs --x"));
        }

        [Test]
        public void GamepadWrite_VectorReleaseWithoutAxes_Centers()
        {
            Assert.IsTrue(InputInjection.TryBuildGamepadWrite(m_Gamepad.leftStick, "release", null, null, null,
                out var mode, out var on, out var off, out var error), error);
            Assert.AreEqual("release", mode);
            Assert.IsTrue(on.IsVector);
            Assert.AreEqual(0f, on.X);
            Assert.AreEqual(0f, on.Y);
            Assert.IsTrue(off.IsVector);
        }

        [Test]
        public void GamepadWrite_DeviceRoot_RejectedAsNotWritable()
        {
            Assert.IsFalse(InputInjection.TryBuildGamepadWrite(m_Gamepad, null, null, null, null,
                out _, out _, out _, out var error));
            Assert.That(error, Does.Contain("not a writable gamepad control"));
        }

        [Test]
        public void GamepadWrite_AxesOnScalarControl_Rejected()
        {
            Assert.IsFalse(InputInjection.TryBuildGamepadWrite(m_Gamepad.leftTrigger, null, null, 1f, null,
                out _, out _, out _, out var error));
            Assert.That(error, Does.Contain("single-axis"));
        }

        [Test]
        public void GamepadWrite_ScalarValue_IsUsed()
        {
            Assert.IsTrue(InputInjection.TryBuildGamepadWrite(m_Gamepad.leftTrigger, null, 0.6f, null, null,
                out var mode, out var on, out _, out var error), error);
            Assert.AreEqual("set", mode);
            Assert.IsFalse(on.IsVector);
            Assert.AreEqual(0.6f, on.Value);
        }

        // ─── state injection round-trip ─────────────────────────

        [Test]
        public void QueueControlState_VectorControl_ReachesInputSystem()
        {
            InputInjection.QueueControlState(m_Gamepad, m_Gamepad.leftStick, InputInjection.ControlWrite.Vector(1f, 0f));
            InputSystem.Update();

            Assert.That(m_Gamepad.leftStick.value.x, Is.EqualTo(1f).Within(0.05f));
            Assert.That(m_Gamepad.leftStick.value.y, Is.EqualTo(0f).Within(0.05f));
            Assert.IsTrue(InputInjection.ControlIsAt(m_Gamepad.leftStick, InputInjection.ControlWrite.Vector(1f, 0f)));
            Assert.IsFalse(InputInjection.ControlIsAt(m_Gamepad.leftStick, InputInjection.ControlWrite.Vector(0f, 0f)));
        }

        [Test]
        public void QueueControlState_OneBitButton_SetsIsPressed()
        {
            InputInjection.QueueControlState(m_Gamepad, m_Gamepad.buttonSouth, InputInjection.ControlWrite.Float(1f));
            InputSystem.Update();

            Assert.IsTrue(m_Gamepad.buttonSouth.isPressed);
            Assert.IsTrue(InputInjection.ControlIsAt(m_Gamepad.buttonSouth, InputInjection.ControlWrite.Float(1f)));

            InputInjection.QueueControlState(m_Gamepad, m_Gamepad.buttonSouth, InputInjection.ControlWrite.Float(0f));
            InputSystem.Update();

            Assert.IsFalse(m_Gamepad.buttonSouth.isPressed);
            Assert.IsTrue(InputInjection.ControlIsAt(m_Gamepad.buttonSouth, InputInjection.ControlWrite.Float(0f)));
        }

        [Test]
        public void QueueControlState_KeyboardBitfieldKey_SetsIsPressed()
        {
            InputInjection.QueueControlState(m_Keyboard, m_Keyboard.spaceKey, InputInjection.ControlWrite.Float(1f));
            InputSystem.Update();

            Assert.IsTrue(m_Keyboard.spaceKey.isPressed);
            Assert.IsTrue(InputInjection.ControlIsAt(m_Keyboard.spaceKey, InputInjection.ControlWrite.Float(1f)));

            InputInjection.QueueControlState(m_Keyboard, m_Keyboard.spaceKey, InputInjection.ControlWrite.Float(0f));
            InputSystem.Update();

            Assert.IsFalse(m_Keyboard.spaceKey.isPressed);
        }

        [Test]
        public void QueueControlStates_BatchKeepsEveryWrittenControl()
        {
            // One event carries all the writes; separate events queued before an update snapshot the same
            // pre-update state and would revert each other.
            InputInjection.QueueControlStates(m_Gamepad, new (InputControl, InputInjection.ControlWrite)[]
            {
                (m_Gamepad.leftShoulder, InputInjection.ControlWrite.Float(1f)),
                (m_Gamepad.leftStick, InputInjection.ControlWrite.Vector(0f, 1f)),
                (m_Gamepad.leftTrigger, InputInjection.ControlWrite.Float(0.5f)),
            });
            InputSystem.Update();

            Assert.IsTrue(m_Gamepad.leftShoulder.isPressed);
            Assert.That(m_Gamepad.leftStick.value.y, Is.EqualTo(1f).Within(0.05f));
            Assert.That(m_Gamepad.leftTrigger.value, Is.EqualTo(0.5f).Within(0.02f));
        }

        [Test]
        public void QueueControlState_AfterAnAppliedWrite_PreservesPreviouslyHeldControls()
        {
            InputInjection.QueueControlState(m_Gamepad, m_Gamepad.leftShoulder, InputInjection.ControlWrite.Float(1f));
            InputSystem.Update();
            InputInjection.QueueControlState(m_Gamepad, m_Gamepad.leftStick, InputInjection.ControlWrite.Vector(0f, 1f));
            InputSystem.Update();

            Assert.IsTrue(m_Gamepad.leftShoulder.isPressed,
                "writing one control must not release keys/buttons already held on the device");
            Assert.That(m_Gamepad.leftStick.value.y, Is.EqualTo(1f).Within(0.05f));
        }

        [Test]
        public void SampleControl_ButtonReportsPressedState()
        {
            InputInjection.QueueControlState(m_Keyboard, m_Keyboard.wKey, InputInjection.ControlWrite.Float(1f));
            InputSystem.Update();

            var data = JObject.FromObject(InputInjection.SampleControl(m_Keyboard.wKey));
            Assert.AreEqual("w", data["control"].Value<string>());
            Assert.IsTrue(data["pressed"].Value<bool>());
            // was_pressed_this_frame is deliberately not asserted: InputSystem.Update() advances the frame
            // counter, so the one-shot flags are only trustworthy for code running inside the same update.
        }

        [Test]
        public void SampleControl_VectorControlReportsXY()
        {
            InputInjection.QueueControlState(m_Gamepad, m_Gamepad.rightStick, InputInjection.ControlWrite.Vector(-0.5f, 0.25f));
            InputSystem.Update();

            var data = JObject.FromObject(InputInjection.SampleControl(m_Gamepad.rightStick));
            // The stick's deadzone remaps the raw state, so the processed value is a little smaller.
            Assert.That(data["xy"]["x"].Value<float>(), Is.EqualTo(-0.5f).Within(0.05f));
            Assert.That(data["xy"]["y"].Value<float>(), Is.EqualTo(0.25f).Within(0.05f));
        }

        // ─── virtual device bookkeeping ───────────────────────────

        [Test]
        public void GetOrAddDevice_TracksTheDeviceAndRemoveAddedDevicesRemovesIt()
        {
            // Touchscreen: the editor has no such device, so this really exercises the add path.
            if (InputSystem.GetDevice<Touchscreen>() is not null)
                Assert.Ignore("a Touchscreen was already present, so GetOrAddDevice would not add one");

            InputInjectionSession.RemoveAddedDevices();
            try
            {
                var screen = InputInjectionSession.GetOrAddDevice<Touchscreen>();

                Assert.That(InputSystem.devices, Does.Contain(screen));
                Assert.That(InputInjectionSession.AddedDevices, Does.Contain(screen));

                Assert.AreEqual(1, InputInjectionSession.RemoveAddedDevices(),
                    "the test starts from an empty session, so only its own device can be removed");
                Assert.IsNull(InputSystem.GetDevice<Touchscreen>(),
                    "a device the tool added has to be gone again — it must not linger in the editor");
                Assert.IsEmpty(InputInjectionSession.AddedDevices);
            }
            finally
            {
                if (InputSystem.GetDevice<Touchscreen>() is { } leftover) InputSystem.RemoveDevice(leftover);
            }
        }

        [Test]
        public void ResetAction_ReportsResetAndRemovedForAToolAddedDevice()
        {
            if (InputSystem.GetDevice<Touchscreen>() is not null)
                Assert.Ignore("a Touchscreen was already present, so the tool would not add this test's device");

            InputInjectionSession.RemoveAddedDevices();
            try
            {
                var screen = InputInjectionSession.GetOrAddDevice<Touchscreen>();
                InputInjectionSession.NoteWrittenDevice(screen);

                var result = InputInjection.HandleCommand(new JObject { ["action"] = "reset" }).Result;
                Assert.IsInstanceOf<SuccessResponse>(result);
                var data = JObject.FromObject(((SuccessResponse)result).data);

                Assert.GreaterOrEqual(data["devices_reset"].Value<int>(), 1);
                Assert.GreaterOrEqual(data["devices_removed"].Value<int>(), 1);
            }
            finally
            {
                InputInjectionSession.RemoveAddedDevices();
            }
        }

        [Test]
        public void FreshFlagCapture_IgnoresTheToolsOwnReadbackUpdate()
        {
            try
            {
                using var capture = new InputInjection.FreshFlagCapture(m_Keyboard.spaceKey);
                InputInjection.QueueControlState(m_Keyboard, m_Keyboard.spaceKey, InputInjection.ControlWrite.Float(1f));
                InputSystem.Update();

                Assert.AreEqual(0, capture.PressFrames,
                    "a manual readback update is not a player-loop frame and must not report a fresh press");
            }
            finally
            {
                InputInjection.QueueControlState(m_Keyboard, m_Keyboard.spaceKey, InputInjection.ControlWrite.Float(0f));
                InputSystem.Update();
            }
        }

        [Test]
        public void ApplyAsync_FailedWrite_RestoresTheSettingsOverride()
        {
            InputInjectionSession.ClearHeldControls();
            InputInjectionSession.RestoreSettings();
            Assert.IsFalse(InputInjectionSession.HasSettingsOverride);

            var p = new ToolParams(new JObject { ["pump_frames"] = 0 });
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await InputInjection.ApplyAsync(p, m_Gamepad, m_Gamepad, "press",
                    InputInjection.ControlWrite.Float(1f), InputInjection.ControlWrite.Float(0f)));

            Assert.IsFalse(InputInjectionSession.HasSettingsOverride,
                "a write that threw must not leave the InputSettings override behind");
        }

        // ─── settings override bookkeeping ────────────────────────

        [Test]
        public void SettingsOverride_AppliesBothFocusNeutralSettingsAndRestoresThem()
        {
            var savedBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            var savedBackground = InputSystem.settings.backgroundBehavior;
            try
            {
                InputInjectionSession.BeginFocusNeutralSettings();

                Assert.AreEqual(InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView,
                    InputSystem.settings.editorInputBehaviorInPlayMode);
                Assert.AreEqual(InputSettings.BackgroundBehavior.IgnoreFocus,
                    InputSystem.settings.backgroundBehavior);

                // Held state keeps the override; nothing held restores it.
                InputInjectionSession.NoteHeld(m_Gamepad, m_Gamepad.leftShoulder);
                InputInjectionSession.RestoreSettingsIfIdle();
                Assert.IsTrue(InputInjectionSession.HasSettingsOverride, "a held control must keep the override");

                InputInjectionSession.NoteReleased(m_Gamepad, m_Gamepad.leftShoulder);
                InputInjectionSession.RestoreSettingsIfIdle();
                Assert.IsFalse(InputInjectionSession.HasSettingsOverride);
                Assert.AreEqual(savedBehavior, InputSystem.settings.editorInputBehaviorInPlayMode);
                Assert.AreEqual(savedBackground, InputSystem.settings.backgroundBehavior);
            }
            finally
            {
                InputInjectionSession.ClearHeldControls();
                InputInjectionSession.RestoreSettings();
            }
        }

        [Test]
        public void SettingsOverride_SecondBegin_DoesNotOverwriteSavedValues()
        {
            var savedBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            var savedBackground = InputSystem.settings.backgroundBehavior;
            try
            {
                InputInjectionSession.BeginFocusNeutralSettings();
                InputInjectionSession.BeginFocusNeutralSettings();
                InputInjectionSession.RestoreSettings();

                Assert.AreEqual(savedBehavior, InputSystem.settings.editorInputBehaviorInPlayMode,
                    "re-entering the session must not save the override as the user's setting");
                Assert.AreEqual(savedBackground, InputSystem.settings.backgroundBehavior);
            }
            finally
            {
                InputInjectionSession.RestoreSettings();
            }
        }

        // ─── lifecycle guard ────────────────────────────────────

        [Test]
        public void InjectionOutsidePlayMode_IsRefused()
        {
            Assert.IsFalse(EditorApplication.isPlaying,
                "this test asserts the Edit-mode refusal, so it must not run in Play mode");

            var task = InputInjection.HandleCommand(new JObject { ["action"] = "key", ["key"] = "Space" });
            Assert.IsTrue(task.IsCompleted, "the refusal must not wait on a player-loop pump");
            Assert.IsInstanceOf<ErrorResponse>(task.Result);
            Assert.That(((ErrorResponse)task.Result).message, Does.Contain("Play mode"));
        }

        [Test]
        public void AxisAction_IsAliasForGamepad()
        {
            var task = InputInjection.HandleCommand(new JObject
            {
                ["action"] = "axis",
                ["control"] = "leftStick",
                ["x"] = 1f,
            });
            Assert.IsInstanceOf<ErrorResponse>(task.Result);
            var message = ((ErrorResponse)task.Result).message;
            Assert.That(message, Does.Contain("Play mode"), "'axis' must reach the gamepad action, got: " + message);
            Assert.That(message, Does.Not.Contain("unknown action"));
        }

        [Test]
        public void UnknownAction_IsRefused()
        {
            var task = InputInjection.HandleCommand(new JObject { ["action"] = "wiggle" });
            Assert.IsInstanceOf<ErrorResponse>(task.Result);
            Assert.That(((ErrorResponse)task.Result).message, Does.Contain("unknown action"));
        }

        [Test]
        public void MissingAction_IsRefused()
        {
            var task = InputInjection.HandleCommand(new JObject());
            Assert.IsInstanceOf<ErrorResponse>(task.Result);
            Assert.That(((ErrorResponse)task.Result).message, Does.Contain("usage"));
        }

        [Test]
        public void StateAction_WorksWithoutPlayMode()
        {
            var task = InputInjection.HandleCommand(new JObject { ["action"] = "state" });
            Assert.IsInstanceOf<SuccessResponse>(task.Result);

            var data = JObject.FromObject(((SuccessResponse)task.Result).data);
            Assert.IsNotNull(data["game_view_focused"]);
            Assert.IsNotNull(data["keyboard"]);
            Assert.IsNotNull(data["legacy_input_note"]);

            // The HTTP layer serializes the response with Newtonsoft; a value type it cannot handle
            // (a raw Vector2, whose .normalized points back at a Vector2) faults that serialization
            // and the CLI never receives an answer at all.
            Newtonsoft.Json.JsonConvert.SerializeObject(((SuccessResponse)task.Result).data);
        }
    }
}
