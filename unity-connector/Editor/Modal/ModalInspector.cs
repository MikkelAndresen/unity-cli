using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityCliConnector.Modal
{
    /// <summary>
    /// Reads and answers the window NSApp is running modally. Everything that
    /// touches AppKit must run on the main thread — call through
    /// <see cref="ModalDispatcher.TryRunDuringModal{T}"/>. Only Unity's own
    /// windows are reachable: this walks NSApp of the current process.
    /// </summary>
    internal static class ModalInspector
    {
        const string RETURN_KEY = "\r";
        const string ESCAPE_KEY = "\u001b";

        public readonly struct ClickResult
        {
            public readonly string Error;
            public readonly ModalDialog Dialog;
            public readonly ModalButton Button;

            public ClickResult(string error, ModalDialog dialog, ModalButton button)
            {
                Error = error;
                Dialog = dialog;
                Button = button;
            }
        }

        /// Main thread only. Null when no window is running modally.
        public static ModalDialog CaptureCurrent()
        {
            var window = CurrentModalWindow();
            if (window == IntPtr.Zero) return null;
            return Describe(window, out _);
        }

        /// <summary>
        /// Main thread only. Clicks the button chosen by title or index on the
        /// current modal window. <paramref name="expectedId"/>, when given, must
        /// match the window's id so an answer meant for one dialog never lands on
        /// a different one that replaced it.
        /// </summary>
        public static ClickResult Click(long? expectedId, string title, int? index)
        {
            var window = CurrentModalWindow();
            if (window == IntPtr.Zero)
                return new ClickResult("No modal dialog is open.", null, null);

            var dialog = Describe(window, out var buttonViews);
            if (expectedId.HasValue && expectedId.Value != dialog.id)
                return new ClickResult($"The open modal dialog has id {dialog.id}, not {expectedId.Value}.", dialog, null);

            var error = ResolveButton(dialog.buttons, title, index, out var button);
            if (error != null)
                return new ClickResult(error, dialog, null);

            MacInterop.Send(buttonViews[button.index], MacInterop.Sel("performClick:"), IntPtr.Zero);
            return new ClickResult(null, dialog, button);
        }

        /// Picks a button by exact title, then by loose title (case, curly
        /// apostrophes, surrounding space), or by index. Returns an error message
        /// or null.
        internal static string ResolveButton(IReadOnlyList<ModalButton> buttons, string title, int? index,
            out ModalButton match)
        {
            match = null;
            if (buttons == null || buttons.Count == 0)
                return "The modal dialog has no native buttons (likely an IMGUI window), so it cannot be answered from the CLI.";

            if (!string.IsNullOrEmpty(title))
            {
                var exact = buttons.Where(b => b.title == title).ToList();
                var candidates = exact.Count > 0
                    ? exact
                    : buttons.Where(b => NormalizeTitle(b.title) == NormalizeTitle(title)).ToList();

                if (candidates.Count == 0)
                    return $"No button titled \"{title}\". Buttons: {DescribeButtons(buttons)}.";
                if (candidates.Count > 1)
                    return $"Several buttons match \"{title}\"; pass --index instead. Buttons: {DescribeButtons(buttons)}.";
                match = candidates[0];
            }
            else if (index.HasValue)
            {
                if (index.Value < 0 || index.Value >= buttons.Count)
                    return $"Button index {index.Value} is out of range. Buttons: {DescribeButtons(buttons)}.";
                match = buttons[index.Value];
            }
            else
            {
                return $"Specify --button <title> or --index <n>. Buttons: {DescribeButtons(buttons)}.";
            }

            if (!match.enabled)
            {
                var disabled = match;
                match = null;
                return $"Button \"{disabled.title}\" is disabled.";
            }
            return null;
        }

        static string NormalizeTitle(string title) =>
            (title ?? "").Replace('’', '\'').Replace('‘', '\'').Trim().ToLowerInvariant();

        static string DescribeButtons(IReadOnlyList<ModalButton> buttons) =>
            string.Join(", ", buttons.Select(b => $"[{b.index}] \"{b.title}\""));

        static IntPtr CurrentModalWindow()
        {
            var app = MacInterop.Send(MacInterop.Class("NSApplication"), MacInterop.Sel("sharedApplication"));
            return MacInterop.Send(app, MacInterop.Sel("modalWindow"));
        }

        static ModalDialog Describe(IntPtr window, out List<IntPtr> buttonViews)
        {
            var texts = new List<string>();
            var buttons = new List<ModalButton>();
            buttonViews = new List<IntPtr>();

            var contentView = MacInterop.Send(window, MacInterop.Sel("contentView"));
            if (contentView != IntPtr.Zero)
                Walk(contentView, texts, buttons, buttonViews);

            var windowClass = MacInterop.ClassName(window);
            string kind;
            if (MacInterop.SendBool(window, MacInterop.Sel("isKindOfClass:"), MacInterop.Class("NSSavePanel")))
                kind = "file_panel";
            else if (windowClass != null && windowClass.IndexOf("Alert", StringComparison.Ordinal) >= 0)
                kind = "alert";
            else
                kind = "window";

            return new ModalDialog(
                MacInterop.SendLong(window, MacInterop.Sel("windowNumber")),
                kind,
                MacInterop.ToManagedString(MacInterop.Send(window, MacInterop.Sel("title"))) ?? "",
                texts,
                buttons,
                windowClass);
        }

        static void Walk(IntPtr view, List<string> texts, List<ModalButton> buttons, List<IntPtr> buttonViews)
        {
            if (MacInterop.SendBool(view, MacInterop.Sel("isHidden"))) return;

            var isKindOfClass = MacInterop.Sel("isKindOfClass:");
            if (MacInterop.SendBool(view, isKindOfClass, MacInterop.Class("NSButton")))
            {
                var title = MacInterop.ToManagedString(MacInterop.Send(view, MacInterop.Sel("title"))) ?? "";
                var key = MacInterop.ToManagedString(MacInterop.Send(view, MacInterop.Sel("keyEquivalent")));
                var enabled = MacInterop.SendBool(view, MacInterop.Sel("isEnabled"));
                buttons.Add(new ModalButton(buttons.Count, title, key == RETURN_KEY, key == ESCAPE_KEY, enabled));
                buttonViews.Add(view);
                return;
            }

            if (MacInterop.SendBool(view, isKindOfClass, MacInterop.Class("NSTextField")))
            {
                var text = MacInterop.ToManagedString(MacInterop.Send(view, MacInterop.Sel("stringValue")));
                if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
                return;
            }

            var subviews = MacInterop.Send(view, MacInterop.Sel("subviews"));
            var count = MacInterop.SendLong(subviews, MacInterop.Sel("count"));
            var objectAtIndex = MacInterop.Sel("objectAtIndex:");
            for (long i = 0; i < count; i++)
                Walk(MacInterop.SendIndex(subviews, objectAtIndex, i), texts, buttons, buttonViews);
        }
    }
}
