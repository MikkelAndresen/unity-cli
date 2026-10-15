using System.Collections.Generic;

namespace UnityCliConnector.Modal
{
    /// <summary>
    /// Snapshot of the window AppKit is currently running modally. Serialized as-is
    /// into tool responses and into the heartbeat's "modal" field; the Go CLI's
    /// client.ModalDialog mirrors these field names.
    /// </summary>
    public sealed class ModalDialog
    {
        /// NSWindow windowNumber — pass back as `id` to make sure an answer reaches this dialog.
        public readonly long id;
        /// "alert" (DisplayDialog / NSAlert), "file_panel" (save/open panels) or "window" (anything else, e.g. IMGUI ShowModal).
        public readonly string kind;
        public readonly string title;
        public readonly IReadOnlyList<string> texts;
        public readonly IReadOnlyList<ModalButton> buttons;
        public readonly string window_class;

        public ModalDialog(long id, string kind, string title, IReadOnlyList<string> texts,
            IReadOnlyList<ModalButton> buttons, string window_class)
        {
            this.id = id;
            this.kind = kind;
            this.title = title;
            this.texts = texts;
            this.buttons = buttons;
            this.window_class = window_class;
        }
    }

    public sealed class ModalButton
    {
        public readonly int index;
        public readonly string title;
        /// Bound to Return.
        public readonly bool is_default;
        /// Bound to Escape.
        public readonly bool is_cancel;
        public readonly bool enabled;

        public ModalButton(int index, string title, bool is_default, bool is_cancel, bool enabled)
        {
            this.index = index;
            this.title = title;
            this.is_default = is_default;
            this.is_cancel = is_cancel;
            this.enabled = enabled;
        }
    }
}
