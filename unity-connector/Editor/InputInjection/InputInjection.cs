using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
// UnityEngine also exports a TouchPhase; the Input System one is what TouchState uses.
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace UnityCliConnector.Tools
{
    /// <summary>
    /// Drives the new Input System from the CLI by writing device state and pumping the player loop,
    /// not by sending OS keystrokes. InputInjectionSession holds how injected events get past the
    /// "Game View focus" gating that otherwise drops them in Play mode.
    /// </summary>
    [UnityCliTool(
        Name = "input",
        Description = "Inject Input System device state in Play mode and pump the player loop so the events are consumed in the same call. " +
                      "Actions: key, mouse, gamepad (alias axis), touch, state, reset. Injected events are seen by the new Input System only — legacy " +
                      "UnityEngine.Input and IMGUI Event code do not see them.")]
    public static class InputInjection
    {
        private const int DefaultPumpFrames = 1;
        private const int MaxPumpFrames = 300;
        private const float ValueEpsilon = 0.02f;
        private const float VectorEpsilon = 0.05f;

        private static readonly List<(InputControl control, ControlWrite write)> NoWrites = new();

        private const string LegacyNote =
            "injected state is visible to the new Input System (UnityEngine.InputSystem) only; code that reads " +
            "UnityEngine.Input or IMGUI Event will not see it";

        private static readonly string[] KeyExamples =
        {
            "Space", "W", "A", "LeftShift", "RightShift", "LeftCtrl", "LeftAlt", "Enter", "Escape",
            "Tab", "UpArrow", "DownArrow", "Digit1", "F1",
        };

        private static readonly IReadOnlyDictionary<string, string> KeyAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["shift"] = nameof(Key.LeftShift),
                ["ctrl"] = nameof(Key.LeftCtrl),
                ["control"] = nameof(Key.LeftCtrl),
                ["alt"] = nameof(Key.LeftAlt),
                ["option"] = nameof(Key.LeftAlt),
                ["cmd"] = nameof(Key.LeftMeta),
                ["command"] = nameof(Key.LeftMeta),
                ["meta"] = nameof(Key.LeftMeta),
                ["win"] = nameof(Key.LeftMeta),
                ["windows"] = nameof(Key.LeftMeta),
                ["esc"] = nameof(Key.Escape),
                ["return"] = nameof(Key.Enter),
                ["spacebar"] = nameof(Key.Space),
                ["del"] = nameof(Key.Delete),
                ["pgup"] = nameof(Key.PageUp),
                ["pgdn"] = nameof(Key.PageDown),
                ["up"] = nameof(Key.UpArrow),
                ["down"] = nameof(Key.DownArrow),
                ["left"] = nameof(Key.LeftArrow),
                ["right"] = nameof(Key.RightArrow),
            };

        private static readonly string[] KeyModes = { "press", "release", "tap" };
        private static readonly string[] MousePositionModes = { "move", "set" };
        private static readonly string[] MouseButtonModes = { "press", "release", "tap", "click" };
        private static readonly string[] GamepadModes = { "set", "press", "release", "tap" };

        public class Parameters
        {
            [ToolParameter("Action: key | mouse | gamepad (alias: axis) | touch | state | reset", Required = true)]
            public string Action { get; set; }

            [ToolParameter("action=key: Key enum name (Space, W, LeftShift, Enter, Digit1, …). Aliases: ctrl, alt, shift, cmd, esc, return, up, down")]
            public string Key { get; set; }

            [ToolParameter("press | release | tap (mouse: click; gamepad: set; mouse position: move; touch: began | moved | stationary | ended). Default: press, or set/move when only a position or value is given")]
            public string Mode { get; set; }

            [ToolParameter("action=mouse: button — left | right | middle | forward | back")]
            public string Button { get; set; }

            [ToolParameter("action=gamepad: control name — leftStick, rightStick, dpad, leftTrigger, rightTrigger, leftShoulder, rightShoulder, buttonSouth, buttonEast, buttonNorth, buttonWest, startButton, selectButton")]
            public string Control { get; set; }

            [ToolParameter("action=gamepad: which gamepad when several are present — name or 0-based index. Default: the first, or a virtual device added on demand")]
            public string Device { get; set; }

            [ToolParameter("X: Game view pixel column for mouse/touch (0 = left edge), or stick/dpad axis for gamepad (-1..1)")]
            public float X { get; set; }

            [ToolParameter("Y: Game view pixel row for mouse/touch (0 = top edge), or stick/dpad axis for gamepad (-1..1)")]
            public float Y { get; set; }

            [ToolParameter("action=mouse: shift position by this X delta instead of setting an absolute position")]
            public float Dx { get; set; }

            [ToolParameter("action=mouse: shift position by this Y delta instead of setting an absolute position")]
            public float Dy { get; set; }

            [ToolParameter("action=mouse: scroll wheel X (positive scrolls away from the user)")]
            public float ScrollX { get; set; }

            [ToolParameter("action=mouse: scroll wheel Y")]
            public float ScrollY { get; set; }

            [ToolParameter("action=gamepad: value for a button/trigger/axis (0 released, 1 fully pressed). Default 1")]
            public float Value { get; set; }

            [ToolParameter("action=touch: touch slot (default 0; the touch id written is slot+1)")]
            public int Index { get; set; }

            [ToolParameter("tap only: player-loop frames to hold before releasing (default 1)")]
            public int HoldFrames { get; set; }

            [ToolParameter("tap only: milliseconds to hold; takes over the timing when combined with hold_frames")]
            public int HoldMs { get; set; }

            [ToolParameter("player-loop frames to pump after the injection (default 1, 0 to skip). A tap also pumps during its hold")]
            public int PumpFrames { get; set; }
        }

        public static async Task<object> HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            var args = p.GetRaw("args") as JArray;
            var action = Arg(p, args, "action", 0);
            if (string.IsNullOrWhiteSpace(action))
                return new ErrorResponse(
                    "usage: input key <name> [press|release|tap] | input mouse [--x --y --button --mode --scroll_x …] | " +
                    "input gamepad --control leftStick --x 1 | input touch --x --y | input state | input reset");

            action = action.Trim().ToLowerInvariant();

            if (action is "key" or "mouse" or "gamepad" or "axis" or "touch" && !EditorApplication.isPlaying)
                return new ErrorResponse(
                    $"'{action}' injects into the running player and needs Play mode — enter it first " +
                    "(unity-cli editor play --wait). 'input state' works without Play mode.");

            switch (action)
            {
                case "key":
                    return await InjectKeyAsync(p, args);
                case "mouse":
                    return await InjectMouseAsync(p);
                case "gamepad":
                case "axis":
                    return await InjectGamepadAsync(p);
                case "touch":
                    return await InjectTouchAsync(p);
                case "state":
                    return new SuccessResponse("Input System state", StateData());
                case "reset":
                    return await ResetAsync();
                default:
                    return new ErrorResponse(
                        $"unknown action '{action}': key | mouse | gamepad (alias axis) | touch | state | reset");
            }
        }

        // ---- actions ----------------------------------------------------------------------------

        private static async Task<object> InjectKeyAsync(ToolParams p, JArray args)
        {
            var keyName = Arg(p, args, "key", 1);
            var modeText = Arg(p, args, "mode", 2);

            // `input key Space press` and `input key press Space` both mean the same thing, so the two
            // positional values are assigned by content, not by order.
            if (args is not null && args.Count > 2 && p.Get("key") is null && p.Get("mode") is null)
            {
                var second = args[2]?.ToString();
                if (KeyModes.ContainsCI(second))
                {
                    keyName = args[1]?.ToString();
                    modeText = second;
                }
                else
                {
                    keyName = second;
                    modeText = args[1]?.ToString();
                }
            }

            if (!TryResolveKey(keyName, out var key, out var keyError))
                return new ErrorResponse(keyError);
            if (!TryResolveMode(modeText, KeyModes, "press", out var mode, out var modeError))
                return new ErrorResponse(modeError);

            var keyboard = InputInjectionSession.GetOrAddDevice<Keyboard>();
            if (!TryGetKeyControl(keyboard, key, out var control, out var controlError))
                return new ErrorResponse(controlError);

            return await ApplyAsync(p, keyboard, control, mode, ControlWrite.Float(1f), ControlWrite.Float(0f),
                identity: new Dictionary<string, object> { ["key"] = key.ToString() });
        }

        private static async Task<object> InjectMouseAsync(ToolParams p)
        {
            var mouse = InputInjectionSession.GetOrAddDevice<Mouse>();
            var modeText = p.Get("mode");
            var identity = new Dictionary<string, object>();

            var preWrites = new List<(InputControl control, ControlWrite write)>();
            InputControl target = null;
            var mode = string.Empty;
            ControlWrite on = default;
            ControlWrite off = default;

            var buttonName = p.Get("button");
            if (!string.IsNullOrWhiteSpace(buttonName))
            {
                if (!TryResolveMouseButton(mouse, buttonName, out var button, out var buttonError))
                    return new ErrorResponse(buttonError);
                if (!TryResolveMode(modeText, MouseButtonModes, "press", out mode, out var modeError))
                    return new ErrorResponse(modeError);
                if (mode == "click") mode = "tap";

                target = button;
                on = ControlWrite.Float(1f);
                off = ControlWrite.Float(0f);
                identity["button"] = button.name;
            }

            var position = ResolveRequestedPosition(mouse, p);
            if (position.HasValue)
            {
                var write = ControlWrite.Vector(position.Value.x, position.Value.y);
                preWrites.Add((mouse.position, write));
                identity["position"] = Vec(position.Value);

                if (target is null)
                {
                    if (!TryResolveMode(modeText, MousePositionModes, "move", out mode, out var modeError))
                        return new ErrorResponse(modeError);
                    target = mouse.position;
                    on = write;
                    off = write;
                }
            }

            var scrollX = p.GetFloat("scroll_x");
            var scrollY = p.GetFloat("scroll_y");
            if (scrollX.HasValue || scrollY.HasValue)
            {
                var current = mouse.scroll.value;
                var write = ControlWrite.Vector(scrollX ?? current.x, scrollY ?? current.y);
                preWrites.Add((mouse.scroll, write));
                identity["scroll"] = Vec(new Vector2(write.X, write.Y));

                if (target is null)
                {
                    if (!TryResolveMode(modeText, MousePositionModes, "set", out mode, out var modeError))
                        return new ErrorResponse(modeError);
                    target = mouse.scroll;
                    on = write;
                    off = write;
                }
            }

            if (target is null)
                return new ErrorResponse(
                    "nothing to inject: give x/y (Game view position), dx/dy, scroll_x/scroll_y, or button (left|right|middle|forward|back)");

            return await ApplyAsync(p, mouse, target, mode, on, off, preWrites, identity);
        }

        private static async Task<object> InjectGamepadAsync(ToolParams p)
        {
            if (!TryResolveGamepad(p.Get("device"), out var gamepad, out var deviceError))
                return new ErrorResponse(deviceError);

            var controlName = p.Get("control");
            if (string.IsNullOrWhiteSpace(controlName))
                return new ErrorResponse($"'control' required — {Join(GamepadControlNames(gamepad))}");
            if (!TryResolveControl(gamepad, controlName, out var control, out var controlError))
                return new ErrorResponse(controlError);

            var isVector = control is InputControl<Vector2>;
            var defaultValue = p.GetFloat("value") ?? 1f;
            var hasExplicitValue = p.GetFloat("value").HasValue ||
                                   p.GetFloat("x").HasValue || p.GetFloat("y").HasValue;
            if (!TryResolveMode(p.Get("mode"), GamepadModes, hasExplicitValue ? "set" : "press", out var mode, out var modeError))
                return new ErrorResponse(modeError);

            var on = isVector
                ? ControlWrite.Vector(p.GetFloat("x") ?? 0f, p.GetFloat("y") ?? 0f)
                : ControlWrite.Float(defaultValue);
            var off = isVector ? ControlWrite.Vector(0f, 0f) : ControlWrite.Float(0f);

            return await ApplyAsync(p, gamepad, control, mode, on, off,
                identity: new Dictionary<string, object>
                {
                    ["control"] = control.name,
                    ["gamepad"] = DeviceLabel(gamepad),
                });
        }

        private static async Task<object> InjectTouchAsync(ToolParams p)
        {
            var touchscreen = InputInjectionSession.GetOrAddDevice<Touchscreen>();
            var index = Math.Max(0, p.GetInt("index", 0) ?? 0);
            if (index >= touchscreen.touches.Count)
                return new ErrorResponse($"{touchscreen.name} has {touchscreen.touches.Count} touch slot(s), so index {index} is out of range");

            var requestedMode = (p.Get("mode") ?? "").Trim().ToLowerInvariant();
            var tap = requestedMode is "tap" or "click";
            if (tap) requestedMode = null;
            if (!TryResolveTouchPhase(requestedMode, out var phase, out var phaseError))
                return new ErrorResponse(phaseError);

            var touch = touchscreen.touches[index];
            var previous = touch.value;
            var x = p.GetFloat("x") ?? (previous.isInProgress ? previous.position.x : touchscreen.position.value.x);
            var y = p.GetFloat("y") ?? (previous.isInProgress ? previous.position.y : touchscreen.position.value.y);

            var holdFrames = ClampFrames(p.GetInt("hold_frames", 1));
            var holdMs = Math.Max(0, p.GetInt("hold_ms", 0) ?? 0);
            var pumpFrames = ClampFrames(p.GetInt("pump_frames", DefaultPumpFrames));

            InputInjectionSession.BeginFocusNeutralSettings();
            InputInjectionSession.NoteWrittenDevice(touchscreen);
            InputInjectionSession.NoteHeld(touchscreen, touch);

            QueueTouchState(touchscreen, touch, index, TouchPhase.Began, x, y, previous);
            UpdateInputStateNow();

            var held = touch.value;
            if (tap)
            {
                await InputInjectionSession.PumpPlayerLoopAsync(holdFrames, holdMs);
                UpdateInputStateNow();
                held = touch.value;
                QueueTouchState(touchscreen, touch, index, TouchPhase.Ended, x, y, held);
            }
            else if (phase != TouchPhase.Began)
            {
                QueueTouchState(touchscreen, touch, index, phase, x, y, held);
            }
            UpdateInputStateNow();

            var heldReport = TouchReport(touch);
            await InputInjectionSession.PumpPlayerLoopAsync(pumpFrames, 0);
            UpdateInputStateNow();
            var stateReport = TouchReport(touch);
            if (tap || phase is TouchPhase.Ended or TouchPhase.Canceled)
                InputInjectionSession.NoteReleased(touchscreen, touch);
            InputInjectionSession.RestoreSettingsIfIdle();

            var consumed = held.phase is not (TouchPhase.None or TouchPhase.Canceled);

            var data = new Dictionary<string, object>
            {
                ["mode"] = tap ? "tap" : phase.ToString().ToLowerInvariant(),
                ["device"] = DeviceLabel(touchscreen),
                ["touch_index"] = index,
                // Vec, not the raw Vector2: Unity 6's Vector2.normalized points back at a Vector2, which makes
                // Newtonsoft's response serialization loop and the connector never answers.
                ["position"] = Vec(new Vector2(x, y)),
                ["consumed"] = consumed,
                ["held_state"] = heldReport,
                ["state"] = stateReport,
                ["pump_frames"] = pumpFrames,
                ["focus"] = InputInjectionSession.FocusData(),
                ["settings"] = InputInjectionSession.SettingsOverrideData(),
                ["legacy_input_note"] = LegacyNote,
            };

            return consumed
                ? new SuccessResponse($"touch {index} {phase.ToString().ToLowerInvariant()} at ({x}, {y}) on {DeviceLabel(touchscreen)}", data)
                : new ErrorResponse($"touch event queued but {touchscreen.name} did not take it", data);
        }

        private static async Task<object> ResetAsync()
        {
            var removed = InputInjectionSession.RemoveAddedDevices();
            var reset = InputInjectionSession.ResetWrittenDevices();
            InputInjectionSession.ClearHeldControls();
            InputInjectionSession.RestoreSettings();

            await InputInjectionSession.PumpPlayerLoopAsync(DefaultPumpFrames, 0);

            return new SuccessResponse(
                $"released injected state ({reset} device(s) reset, {removed} virtual device(s) removed) and restored InputSettings",
                new
                {
                    devices_reset = reset,
                    devices_removed = removed,
                    settings = InputInjectionSession.SettingsOverrideData(),
                    focus = InputInjectionSession.FocusData(),
                    legacy_input_note = LegacyNote,
                });
        }

        private static object StateData()
        {
            // Control reads made outside an input update land in the editor state buffers, which never see the
            // injected events (they are deferred into player updates). Running one update first puts the reader
            // in the player buffer context that game code reads from.
            UpdateInputStateNow();

            var data = new Dictionary<string, object>
            {
                ["playing"] = EditorApplication.isPlaying,
                ["game_view_focused"] = InputInjectionSession.GameViewHasFocus(),
                ["editor_focused"] = EditorApplication.isFocused,
                ["application_is_focused"] = UnityEngine.Application.isFocused,
                ["settings"] = InputInjectionSession.SettingsOverrideData(),
                ["devices"] = InputSystem.devices.Select(DeviceLabel).ToArray(),
                ["added_by_tool"] = InputInjectionSession.AddedDevices.Select(DeviceLabel).ToArray(),
                ["legacy_input_note"] = LegacyNote,
            };

            var keyboard = InputSystem.GetDevice<Keyboard>();
            if (keyboard is not null)
            {
                data["keyboard"] = new
                {
                    device = DeviceLabel(keyboard),
                    pressed_keys = keyboard.allKeys.Where(k => k.isPressed).Select(k => k.keyCode.ToString()).ToArray(),
                };
            }

            var mouse = InputSystem.GetDevice<Mouse>();
            if (mouse is not null)
            {
                data["mouse"] = new
                {
                    device = DeviceLabel(mouse),
                    position = Vec(mouse.position.value),
                    scroll = Vec(mouse.scroll.value),
                    pressed_buttons = new[] { mouse.leftButton, mouse.rightButton, mouse.middleButton, mouse.forwardButton, mouse.backButton }
                        .Where(b => b.isPressed).Select(b => b.name).ToArray(),
                };
            }

            var gamepads = InputSystem.devices.OfType<Gamepad>().ToArray();
            if (gamepads.Length > 0)
                data["gamepads"] = gamepads.Select(GamepadData).ToArray();

            var touchscreens = InputSystem.devices.OfType<Touchscreen>().ToArray();
            if (touchscreens.Length > 0)
                data["touchscreens"] = touchscreens
                    .Select(ts => (object)new
                    {
                        device = DeviceLabel(ts),
                        touches = ts.touches.Select(TouchReport).ToArray(),
                    }).ToArray();

            return data;
        }

        private static object GamepadData(Gamepad gamepad)
        {
            var top = gamepad.allControls.Where(c => ReferenceEquals(c.parent, gamepad)).ToList();

            return new
            {
                device = DeviceLabel(gamepad),
                pressed = top.OfType<ButtonControl>().Where(b => b.isPressed).Select(b => b.name).ToArray(),
                axes = top.OfType<AxisControl>().Where(a => a is not ButtonControl && Math.Abs(a.value) > ValueEpsilon)
                    .Select(a => new { name = a.name, value = Round(a.value) }).ToArray(),
                sticks = top.OfType<InputControl<Vector2>>().Where(c => c.value.sqrMagnitude > ValueEpsilon * ValueEpsilon)
                    .Select(c => new { name = c.name, xy = Vec(c.value) }).ToArray(),
            };
        }

        private static object TouchReport(TouchControl touch)
        {
            var state = touch.value;
            return new
            {
                touch_id = state.touchId,
                phase = state.phase.ToString(),
                position = Vec(state.position),
                pressed = state.isInProgress,
            };
        }

        // ---- shared injection path --------------------------------------------------------------

        private static async Task<object> ApplyAsync(ToolParams p, InputDevice device, InputControl control,
            string mode, ControlWrite on, ControlWrite off,
            List<(InputControl control, ControlWrite write)> preWrites = null,
            Dictionary<string, object> identity = null)
        {
            var holdFrames = ClampFrames(p.GetInt("hold_frames", 1));
            var holdMs = Math.Max(0, p.GetInt("hold_ms", 0) ?? 0);
            var pumpFrames = ClampFrames(p.GetInt("pump_frames", DefaultPumpFrames));

            InputInjectionSession.BeginFocusNeutralSettings();
            InputInjectionSession.NoteWrittenDevice(device);

            var releasing = mode == "release";
            var tapped = mode is "tap" or "click";
            // Counted from inside the input updates themselves: reading wasPressedThisFrame after the
            // call is useless because the tool's own update has already advanced the frame counter.
            using var freshFlags = new FreshFlagCapture(control);
            var writes = new List<(InputControl control, ControlWrite write)>(preWrites ?? NoWrites);
            writes.Add((control, releasing ? off : on));
            QueueControlStates(device, writes);
            UpdateInputStateNow();

            if (releasing) InputInjectionSession.NoteReleased(device, control);
            else InputInjectionSession.NoteHeld(device, control);

            object heldSample = null;
            var heldOk = false;

            if (!releasing)
            {
                if (tapped) await InputInjectionSession.PumpPlayerLoopAsync(holdFrames, holdMs);

                UpdateInputStateNow();
                heldSample = SampleControl(control);
                heldOk = ControlIsAt(control, on);

                if (tapped)
                {
                    QueueControlState(device, control, off);
                    UpdateInputStateNow();
                    InputInjectionSession.NoteReleased(device, control);
                }
            }

            await InputInjectionSession.PumpPlayerLoopAsync(pumpFrames, 0);
            UpdateInputStateNow();
            InputInjectionSession.RestoreSettingsIfIdle();

            var finalSample = SampleControl(control);
            var consumed = releasing
                ? ControlIsAt(control, off)
                : tapped ? heldOk : ControlIsAt(control, on);

            var data = new Dictionary<string, object>
            {
                ["mode"] = mode,
                ["device"] = DeviceLabel(device),
                ["control"] = control.name,
                ["consumed"] = consumed,
                ["fresh_press_frames"] = freshFlags.PressFrames,
                ["fresh_release_frames"] = freshFlags.ReleaseFrames,
                ["state"] = finalSample,
                ["pump_frames"] = pumpFrames,
                ["focus"] = InputInjectionSession.FocusData(),
                ["settings"] = InputInjectionSession.SettingsOverrideData(),
                ["legacy_input_note"] = LegacyNote,
            };
            if (identity is not null)
                foreach (var pair in identity)
                    data[pair.Key] = pair.Value;
            if (heldSample is not null) data["held_state"] = heldSample;

            var label = $"{data.GetValueOrDefault("key") ?? control.name} {mode} on {DeviceLabel(device)}";
            return consumed
                ? new SuccessResponse($"injected: {label}", data)
                : new ErrorResponse($"event queued but the Input System state did not change: {label}", data);
        }

        /// <summary>
        /// Queues one StateEvent built from the device's current state with the given controls overwritten,
        /// so the rest of what the device holds (other pressed keys, the mouse position, the other stick)
        /// survives. A plain QueueStateEvent with a fresh state struct would release everything else.
        ///
        /// Several writes have to share one event: an event snapshots the state as it was when the event was
        /// queued, so two events queued before an update revert each other's controls.
        /// </summary>
        internal static void QueueControlStates(InputDevice device,
            IReadOnlyList<(InputControl control, ControlWrite write)> writes)
        {
            using (StateEvent.From(device, out var eventPtr))
            {
                foreach (var (control, write) in writes)
                    write.Apply(control, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
        }

        internal static void QueueControlState(InputDevice device, InputControl control, ControlWrite write)
        {
            QueueControlStates(device, new[] { (control, write) });
        }

        private static void QueueTouchState(Touchscreen touchscreen, TouchControl touch, int index,
            TouchPhase phase, float x, float y, TouchState previous)
        {
            var began = phase == TouchPhase.Began;
            var state = new TouchState
            {
                touchId = index + 1,
                phaseId = (byte)phase,
                position = new Vector2(x, y),
                delta = began ? Vector2.zero : new Vector2(x - previous.position.x, y - previous.position.y),
                pressure = phase is TouchPhase.Began or TouchPhase.Moved or TouchPhase.Stationary ? 1f : 0f,
                radius = previous.radius,
                tapCount = began ? (byte)(previous.tapCount + 1) : previous.tapCount,
                startTime = began ? InputState.currentTime : previous.startTime,
                startPosition = began ? new Vector2(x, y) : previous.startPosition,
            };

            using (StateEvent.From(touchscreen, out var eventPtr))
            {
                touch.WriteValueIntoEvent(state, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
        }

        /// <summary>
        /// Runs input event processing now instead of waiting for the next player-loop tick, so the readback
        /// describes state that game code can already see. Skipped when the project drives InputSystem.Update
        /// itself (InputSettings.UpdateMode.ProcessEventsManually).
        /// </summary>
        private static void UpdateInputStateNow()
        {
            if (InputSystem.settings.updateMode == InputSettings.UpdateMode.ProcessEventsManually) return;
            InputSystem.Update();
        }

        internal static object SampleControl(InputControl control)
        {
            if (control is ButtonControl button)
            {
                return new
                {
                    control = button.name,
                    pressed = button.isPressed,
                    value = Round(button.value),
                    was_pressed_this_frame = button.wasPressedThisFrame,
                    was_released_this_frame = button.wasReleasedThisFrame,
                };
            }
            if (control is InputControl<Vector2> vector)
                return new { control = vector.name, xy = Vec(vector.value) };
            if (control is AxisControl axis)
                return new { control = axis.name, value = Round(axis.value) };

            return new { control = control.name };
        }

        internal static bool ControlIsAt(InputControl control, ControlWrite want)
        {
            if (control is null) return false;

            if (want.IsVector)
            {
                if (control is not InputControl<Vector2> vector) return false;
                var got = vector.value;
                if (Approximately(got.x, want.X, VectorEpsilon) && Approximately(got.y, want.Y, VectorEpsilon))
                    return true;
                // Sticks run the raw state through a deadzone and the dpad snaps to directions, so a written
                // value comes back smaller (stick 0.3 reads 0.22, dpad 0.3 reads 0). Anything still pointing
                // the same way counts as delivered.
                return want.Magnitude > ValueEpsilon && got.sqrMagnitude > ValueEpsilon * ValueEpsilon &&
                       Vector2.Dot(got, new Vector2(want.X, want.Y)) > 0f;
            }

            // Buttons re-process raw state (press thresholds, one-off values), so compare what game code reads.
            if (control is ButtonControl button && (Approximately(want.Value, 1f) || Approximately(want.Value, 0f)))
                return button.isPressed == want.Value > 0.5f;

            return control is AxisControl axis && Approximately(axis.value, want.Value);
        }

        // ---- parameter resolution (internal for EditMode tests) ---------------------------------

        internal static bool TryResolveKey(string text, out Key key, out string error)
        {
            key = Key.None;
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = $"'key' required — examples: {Join(KeyExamples)}";
                return false;
            }

            var name = text.Trim();
            // Enum.TryParse accepts numeric strings, which would turn "1" into the enum value of Space.
            if (name.All(char.IsDigit)) name = "Digit" + name;
            else name = name.Replace(" ", "").Replace("_", "").Replace("-", "");

            if (KeyAliases.TryGetValue(name, out var alias)) name = alias;

            if (!Enum.TryParse(name, true, out key) || key == Key.None)
            {
                key = Key.None;
                error = $"'{text}' is not a Key name — examples: {Join(KeyExamples)}";
                return false;
            }
            return true;
        }

        internal static bool TryGetKeyControl(Keyboard keyboard, Key key, out KeyControl control, out string error)
        {
            control = null;
            error = null;
            var index = (int)key;
            if (index < 1 || index >= keyboard.allKeys.Count)
            {
                error = $"{keyboard.name} has no key '{key}'";
                return false;
            }

            control = keyboard[key];
            if (control is null)
            {
                error = $"{keyboard.name} has no control for '{key}' in its current layout";
                return false;
            }
            return true;
        }

        internal static bool TryResolveMouseButton(Mouse mouse, string text, out ButtonControl button, out string error)
        {
            button = null;
            error = null;

            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "left":
                case "primary":
                    button = mouse.leftButton;
                    break;
                case "right":
                case "secondary":
                    button = mouse.rightButton;
                    break;
                case "middle":
                    button = mouse.middleButton;
                    break;
                case "forward":
                    button = mouse.forwardButton;
                    break;
                case "back":
                    button = mouse.backButton;
                    break;
                default:
                    error = $"'{text}' is not a mouse button — left | right | middle | forward | back";
                    return false;
            }

            return button is not null;
        }

        internal static bool TryResolveControl(InputDevice device, string text, out InputControl control, out string error)
        {
            control = null;
            error = null;

            var name = (text ?? "").Trim();
            if (name.Length == 0)
            {
                error = "'control' required";
                return false;
            }

            foreach (var candidate in device.allControls)
            {
                if (string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    control = candidate;
                    return true;
                }
            }
            // Allow nesting such as "leftStick/x".
            foreach (var candidate in device.allControls)
            {
                if (candidate.path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))
                {
                    control = candidate;
                    return true;
                }
            }

            error = $"'{name}' is not a control of {device.name} — {Join(GamepadControlNames(device))}";
            return false;
        }

        internal static bool TryResolveMode(string text, IReadOnlyList<string> allowed, string fallback,
            out string mode, out string error)
        {
            mode = (text ?? "").Trim().ToLowerInvariant();
            error = null;

            if (mode.Length == 0)
            {
                mode = fallback;
                return true;
            }
            if (allowed.ContainsCI(mode)) return true;

            error = $"'{text}' is not valid here — expected one of: {string.Join(" | ", allowed)}";
            return false;
        }

        internal static bool TryResolveTouchPhase(string text, out TouchPhase phase, out string error)
        {
            phase = TouchPhase.Began;
            error = null;

            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "press":
                case "began":
                case "down":
                    phase = TouchPhase.Began;
                    return true;
                case "move":
                case "moved":
                    phase = TouchPhase.Moved;
                    return true;
                case "stationary":
                    phase = TouchPhase.Stationary;
                    return true;
                case "release":
                case "ended":
                case "up":
                    phase = TouchPhase.Ended;
                    return true;
                case "canceled":
                case "cancelled":
                    phase = TouchPhase.Canceled;
                    return true;
                default:
                    error = $"'{text}' is not a touch phase — began | moved | stationary | ended | canceled (tap queues began then ended)";
                    return false;
            }
        }

        internal static bool TryResolveGamepad(string text, out Gamepad gamepad, out string error)
        {
            gamepad = null;
            error = null;

            var pads = InputSystem.devices.OfType<Gamepad>().ToList();
            var selector = (text ?? "").Trim();

            if (selector.Length == 0)
            {
                gamepad = pads.Count > 0 ? pads[0] : InputInjectionSession.GetOrAddDevice<Gamepad>();
                return gamepad is not null;
            }

            if (int.TryParse(selector, out var index))
            {
                if (index < 0 || index >= pads.Count)
                {
                    error = $"no gamepad at index {index}; {pads.Count} gamepad(s) present";
                    return false;
                }
                gamepad = pads[index];
                return true;
            }

            gamepad = pads.FirstOrDefault(pad => string.Equals(pad.name, selector, StringComparison.OrdinalIgnoreCase));
            if (gamepad is null)
            {
                error = $"no gamepad named '{selector}'; present: {Join(pads.Select(DeviceLabel))}";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Positional args back `unity-cli input key Space press`; named flags always win.
        /// </summary>
        internal static string Arg(ToolParams p, JArray args, string key, int position)
        {
            var flag = p.Get(key);
            if (!string.IsNullOrWhiteSpace(flag)) return flag;
            if (args is null || position >= args.Count) return null;
            return args[position]?.ToString();
        }

        private static Vector2? ResolveRequestedPosition(Pointer pointer, ToolParams p)
        {
            var x = p.GetFloat("x");
            var y = p.GetFloat("y");
            var dx = p.GetFloat("dx");
            var dy = p.GetFloat("dy");
            if (!x.HasValue && !y.HasValue && !dx.HasValue && !dy.HasValue) return null;

            var current = pointer.position.value;
            return new Vector2(
                x ?? current.x + (dx ?? 0f),
                y ?? current.y + (dy ?? 0f));
        }

        private static IEnumerable<string> GamepadControlNames(InputDevice device)
        {
            return device.allControls
                .Where(c => ReferenceEquals(c.parent, device) &&
                            (c is ButtonControl || c is AxisControl || c is InputControl<Vector2>))
                .Select(c => c.name)
                .Take(24);
        }

        private static int ClampFrames(int? frames)
        {
            return Math.Clamp(frames ?? DefaultPumpFrames, 0, MaxPumpFrames);
        }

        private static Dictionary<string, object> Vec(Vector2 value)
        {
            return new Dictionary<string, object> { ["x"] = Round(value.x), ["y"] = Round(value.y) };
        }

        private static float Round(float value)
        {
            return (float)Math.Round(value, 3);
        }

        private static bool Approximately(float a, float b, float epsilon = ValueEpsilon)
        {
            return Math.Abs(a - b) <= epsilon;
        }

        private static string DeviceLabel(InputDevice device)
        {
            return device is null ? "none" : $"{device.name}/{device.deviceId}";
        }

        private static string Join(IEnumerable<string> values)
        {
            return string.Join(", ", values);
        }

        private static bool ContainsCI(this IEnumerable<string> values, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Counts the input updates in which a control reported a fresh press/release — the same view
        /// MonoBehaviour.Update gets, since it runs after the input update of that frame.
        /// </summary>
        private sealed class FreshFlagCapture : IDisposable
        {
            private readonly InputControl m_Control;

            public int PressFrames { get; private set; }
            public int ReleaseFrames { get; private set; }

            public FreshFlagCapture(InputControl control)
            {
                m_Control = control;
                InputSystem.onAfterUpdate += OnAfterUpdate;
            }

            private void OnAfterUpdate()
            {
                if (m_Control is not ButtonControl button) return;
                if (button.wasPressedThisFrame) ++PressFrames;
                if (button.wasReleasedThisFrame) ++ReleaseFrames;
            }

            public void Dispose() => InputSystem.onAfterUpdate -= OnAfterUpdate;
        }

        internal readonly struct ControlWrite
        {
            public bool IsVector { get; }
            public float X { get; }
            public float Y { get; }
            public float Value { get; }

            private ControlWrite(bool isVector, float x, float y, float value)
            {
                IsVector = isVector;
                X = x;
                Y = y;
                Value = value;
            }

            public static ControlWrite Float(float value) => new(false, 0f, 0f, value);

            public static ControlWrite Vector(float x, float y) => new(true, x, y, 0f);

            public float Magnitude => Mathf.Sqrt(X * X + Y * Y);

            public void Apply(InputControl control, InputEventPtr eventPtr)
            {
                if (IsVector && control is InputControl<Vector2> vector)
                {
                    vector.WriteValueIntoEvent(new Vector2(X, Y), eventPtr);
                    return;
                }
                if (!IsVector && control is InputControl<float> single)
                {
                    single.WriteValueIntoEvent(Value, eventPtr);
                    return;
                }

                throw new InvalidOperationException(
                    $"cannot write {(IsVector ? "Vector2" : "float")} state to control '{control?.name}' of type {control?.GetType().Name}");
            }
        }
    }
}
