package cmd

import (
	"fmt"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// modalState is the heartbeat state the connector reports while a modal dialog
// blocks Unity's main thread. Every command except "modal" would hang until the
// dialog is answered, so the CLI fails fast instead.
const modalState = "modal"

// modalCommand is the only command the connector serves while a modal is open.
const modalCommand = "modal"

func isModal(inst *client.Instance) bool {
	return inst != nil && inst.State == modalState
}

// modalBlockedError explains which dialog blocks the editor and how to answer it.
// pending reports whether the command was already sent and is waiting inside Unity.
func modalBlockedError(inst *client.Instance, command string, pending bool) error {
	var b strings.Builder
	if pending {
		fmt.Fprintf(&b, "%q is blocked by a modal dialog in Unity. The command is still pending and resumes once the dialog is answered, but its result will not be reported here.\n", command)
	} else {
		fmt.Fprintf(&b, "Unity is blocked by a modal dialog; %q was not sent.\n", command)
	}
	b.WriteString(describeModal(inst.Modal))
	b.WriteString("Answer it with: unity-cli modal answer --button \"<title>\"")
	return fmt.Errorf("%s", b.String())
}

func describeModal(d *client.ModalDialog) string {
	if d == nil {
		return "Run 'unity-cli modal' to inspect it.\n"
	}
	var b strings.Builder
	fmt.Fprintf(&b, "Modal dialog (%s, id %d)", d.Kind, d.ID)
	if d.Title != "" {
		fmt.Fprintf(&b, " %q", d.Title)
	}
	b.WriteString(":\n")
	for _, text := range d.Texts {
		fmt.Fprintf(&b, "  %s\n", text)
	}
	if len(d.Buttons) == 0 {
		b.WriteString("No native buttons; it cannot be answered from the CLI.\n")
		return b.String()
	}
	buttons := make([]string, 0, len(d.Buttons))
	for _, btn := range d.Buttons {
		label := fmt.Sprintf("[%d] %q", btn.Index, btn.Title)
		var tags []string
		if btn.IsDefault {
			tags = append(tags, "default")
		}
		if btn.IsCancel {
			tags = append(tags, "cancel")
		}
		if !btn.Enabled {
			tags = append(tags, "disabled")
		}
		if len(tags) > 0 {
			label += " (" + strings.Join(tags, ", ") + ")"
		}
		buttons = append(buttons, label)
	}
	fmt.Fprintf(&b, "Buttons: %s\n", strings.Join(buttons, ", "))
	return b.String()
}

// sendWatchingModal wraps send so that a modal dialog opening while the request
// is in flight (e.g. a save prompt raised by the command itself) returns an error
// describing the dialog instead of blocking until the HTTP timeout.
func sendWatchingModal(send sendFn, resolve instanceResolver) sendFn {
	return func(command string, params interface{}) (*client.CommandResponse, error) {
		if command == modalCommand {
			return send(command, params)
		}

		type result struct {
			resp *client.CommandResponse
			err  error
		}
		done := make(chan result, 1)
		go func() {
			resp, err := send(command, params)
			done <- result{resp, err}
		}()

		ticker := time.NewTicker(statusPollInterval)
		defer ticker.Stop()
		for {
			select {
			case r := <-done:
				return r.resp, r.err
			case <-ticker.C:
				if inst, err := resolve(); err == nil && isModal(inst) {
					return nil, modalBlockedError(inst, command, true)
				}
			}
		}
	}
}
