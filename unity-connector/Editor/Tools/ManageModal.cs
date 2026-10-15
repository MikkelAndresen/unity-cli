using System;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityCliConnector.Modal;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "modal",
        Description = "Inspect or answer a modal dialog blocking the editor (macOS). Actions: list (default), answer.",
        SkipCompilationGate = true,
        RunOffMainThread = true)]
    public static class ManageModal
    {
        static readonly TimeSpan MAIN_THREAD_TIMEOUT = TimeSpan.FromSeconds(5);
        const int DISMISS_WAIT_MS = 1500;
        const int DISMISS_POLL_MS = 50;

        public class Parameters
        {
            [ToolParameter("Action: list (default) or answer")]
            public string Action { get; set; }

            [ToolParameter("answer: title of the button to click (case-insensitive fallback)")]
            public string Button { get; set; }

            [ToolParameter("answer: index of the button to click, as reported by list")]
            public int Index { get; set; }

            [ToolParameter("answer: dialog id from list; refuses to click if a different dialog is open")]
            public long Id { get; set; }
        }

        public static object HandleCommand(JObject parameters)
        {
            var p = new ToolParams(parameters ?? new JObject());
            var action = p.Get("action")?.ToLowerInvariant()
                ?? (p.GetRaw("args") as JArray)?[0]?.ToString()?.ToLowerInvariant()
                ?? "list";

            if (!ModalDispatcher.IsSupported)
                return new ErrorResponse("Modal dialog handling is only supported on macOS.");

            switch (action)
            {
                case "list": return List();
                case "answer": return Answer(p);
                default: return new ErrorResponse($"Unknown modal action: {action}. Available: list, answer.");
            }
        }

        static object List()
        {
            var dialog = Capture();
            if (dialog == null)
                return new SuccessResponse("No modal dialog is open.", new { present = false });
            return new SuccessResponse("Modal dialog is open.", new { present = true, dialog });
        }

        static object Answer(ToolParams p)
        {
            var title = p.Get("button");
            var index = p.GetInt("index");
            var idText = p.Get("id");
            long? id = long.TryParse(idText, out var parsedId) ? parsedId : null;
            if (string.IsNullOrEmpty(title) && !index.HasValue)
                return new ErrorResponse("answer requires --button <title> or --index <n>.");
            if (!string.IsNullOrEmpty(idText) && !id.HasValue)
                return new ErrorResponse($"--id must be a number, got: {idText}");

            if (!ModalDispatcher.TryRunDuringModal(() => ModalInspector.Click(id, title, index),
                    MAIN_THREAD_TIMEOUT, out var click))
                return new ErrorResponse("No modal dialog is open.");

            if (click.Error != null)
                return new ErrorResponse(click.Error, click.Dialog != null ? new { dialog = click.Dialog } : null);

            var next = WaitForDismissal(click.Dialog.id);
            var dismissed = next == null || next.id != click.Dialog.id;
            if (dismissed) ModalWatchdog.Publish(next);
            var message = dismissed
                ? $"Clicked \"{click.Button.title}\"; dialog dismissed."
                : $"Clicked \"{click.Button.title}\"; the dialog is still open.";
            return new SuccessResponse(message, new
            {
                clicked = click.Button.title,
                dismissed,
                next_dialog = dismissed ? next : null,
            });
        }

        /// The click only flags the modal session to stop; AppKit unwinds the loop
        /// after the dispatched job returns. Poll until the loop is gone, or report
        /// whichever dialog is open after it (the same one, or a follow-up dialog).
        static ModalDialog WaitForDismissal(long clickedId)
        {
            var sw = Stopwatch.StartNew();
            ModalDialog current = null;
            while (sw.ElapsedMilliseconds < DISMISS_WAIT_MS)
            {
                Thread.Sleep(DISMISS_POLL_MS);
                current = Capture();
                if (current == null || current.id != clickedId) return current;
            }
            return current;
        }

        static ModalDialog Capture()
        {
            return ModalDispatcher.TryRunDuringModal(ModalInspector.CaptureCurrent, MAIN_THREAD_TIMEOUT, out var dialog)
                ? dialog
                : null;
        }
    }
}
