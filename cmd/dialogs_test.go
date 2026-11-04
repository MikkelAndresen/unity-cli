package cmd

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// dialogRecord builds one window record the way the AppleScript side does: five fields separated
// by US, list items inside a field by GS, records separated by RS.
func dialogRecord(pid int, title, role string, buttons, texts []string) string {
	return fmt.Sprintf("%d%s%s%s%s%s%s%s%s",
		pid, fieldSep, title, fieldSep, role, fieldSep,
		strings.Join(buttons, listSep), fieldSep, strings.Join(texts, listSep)) + recordSep
}

func TestParseDialogWindows(t *testing.T) {
	multiLine := "The project \"Foo\" appears to be open in another process.\nClosing it first is recommended."

	out := dialogRecord(65279, "Scene - Foo - Unity 6", "window", nil, nil) +
		dialogRecord(65279, "Unity", "dialog", []string{"Continue", "Quit Unity"}, []string{multiLine, "FoW"}) +
		"\n"

	got, err := parseDialogWindows(out)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(got) != 2 {
		t.Fatalf("got %d windows, want 2", len(got))
	}
	if got[0].PID != 65279 || got[0].Title != "Scene - Foo - Unity 6" || got[0].Role != "window" {
		t.Errorf("first window = %+v", got[0])
	}
	if len(got[0].Buttons) != 0 || len(got[0].Texts) != 0 {
		t.Errorf("first window should have no buttons or texts: %+v", got[0])
	}
	second := got[1]
	if second.Title != "Unity" || second.Role != "dialog" {
		t.Errorf("second window = %+v", second)
	}
	if want := []string{"Continue", "Quit Unity"}; strings.Join(second.Buttons, ",") != strings.Join(want, ",") {
		t.Errorf("buttons: got %v, want %v", second.Buttons, want)
	}
	if len(second.Texts) != 2 {
		t.Fatalf("texts: got %v, want 2 entries", second.Texts)
	}
	if second.Texts[0] != multiLine {
		t.Errorf("multi-line dialog text was mangled: %q", second.Texts[0])
	}
}

func TestParseDialogWindows_NoUnityAndEmpty(t *testing.T) {
	for _, in := range []string{"", "\n", noUnityMarker, " " + noUnityMarker + "\n"} {
		got, err := parseDialogWindows(in)
		if err != nil {
			t.Fatalf("input %q: unexpected error %v", in, err)
		}
		if len(got) != 0 {
			t.Errorf("input %q: got %v, want no windows", in, got)
		}
	}
}

func TestParseDialogWindows_Malformed(t *testing.T) {
	if _, err := parseDialogWindows("65279\x1fUnity\x1fdialog"); err == nil {
		t.Error("expected an error for a record with too few fields")
	}
	if _, err := parseDialogWindows("Unity\x1fUnity\x1fdialog\x1f\x1f"); err == nil {
		t.Error("expected an error for a non-numeric pid")
	}
	if _, err := parseDialogWindows("1\x1fUnity\x1fdialog\x1f\x1f\x1fextra"); err == nil {
		t.Error("expected an error for a record with too many fields")
	}
}

func TestParseDialogWindows_CapsEditorWindowTexts(t *testing.T) {
	many := make([]string, maxListedTexts+8)
	for i := range many {
		many[i] = fmt.Sprintf("label %d", i)
	}
	got, err := parseDialogWindows(dialogRecord(1, "Scene - Foo - Unity", "window", nil, many))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(got[0].Texts) != maxListedTexts {
		t.Errorf("got %d texts, want the cap of %d", len(got[0].Texts), maxListedTexts)
	}
	if got[0].TextsTotal != len(many) {
		t.Errorf("TextsTotal: got %d, want %d", got[0].TextsTotal, len(many))
	}
}

func TestParseDialogWindows_DropsMissingValue(t *testing.T) {
	got, err := parseDialogWindows(dialogRecord(1, "Unity", "dialog", []string{"OK", "missing value"}, nil))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(got) != 1 || len(got[0].Buttons) != 1 || got[0].Buttons[0] != "OK" {
		t.Errorf("got %+v, want only the OK button", got)
	}
}

func TestCollectWindows_KeepsFilterWhenItMatches(t *testing.T) {
	var seen []string
	list := func(needle string) ([]dialogWindow, error) {
		seen = append(seen, needle)
		if needle == "Foo" {
			return []dialogWindow{{PID: 1, Title: "Scene - Foo - Unity"}}, nil
		}
		return nil, nil
	}

	windows, filter, err := collectWindows("Foo", list)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if filter != "Foo" || len(windows) != 1 {
		t.Errorf("got filter %q windows %+v", filter, windows)
	}
	if len(seen) != 1 {
		t.Errorf("expected one listing, got %v", seen)
	}
}

// TestCollectWindows_FallsBackToAllWindows covers the startup-dialog case: a license or safe-mode
// panel does not name the project, so an empty filtered result must not hide it.
func TestCollectWindows_FallsBackToAllWindows(t *testing.T) {
	var seen []string
	list := func(needle string) ([]dialogWindow, error) {
		seen = append(seen, needle)
		if needle == "" {
			return []dialogWindow{{PID: 1, Title: "Unity License"}}, nil
		}
		return nil, nil
	}

	windows, filter, err := collectWindows("Foo", list)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if filter != "" {
		t.Errorf("filter: got %q, want the unfiltered pass to be reported", filter)
	}
	if len(windows) != 1 {
		t.Fatalf("got %+v, want the license window", windows)
	}
	if strings.Join(seen, "|") != "Foo|" {
		t.Errorf("listings: got %v, want filtered then unfiltered", seen)
	}
}

func TestCollectWindows_SkipsFallbackWhenUnfilteredAlready(t *testing.T) {
	calls := 0
	list := func(string) ([]dialogWindow, error) {
		calls++
		return nil, errors.New("osascript missing")
	}
	if _, _, err := collectWindows("", list); err == nil {
		t.Fatal("expected the listing error to surface")
	}
	if calls != 1 {
		t.Errorf("got %d listings, want 1", calls)
	}
}

func TestSelectButton(t *testing.T) {
	windows := []dialogWindow{
		{PID: 1, Title: "Scene - Foo - Unity", Buttons: []string{"Save", "Cancel"}},
		{PID: 1, Title: "Save Changes?", Role: "dialog", Buttons: []string{"Don't Save", "Cancel", "Save"}},
	}

	tests := []struct {
		name      string
		want      string
		window    string
		wantTitle string
		wantErr   string
	}{
		{name: "exact", want: "Don't Save", wantTitle: "Save Changes?"},
		{name: "exact-beats-substring", want: "Save", wantErr: "matches 2 windows"},
		{name: "case-insensitive-substring", want: "don't save", wantTitle: "Save Changes?"},
		{name: "window-needle-disambiguates", want: "Save", window: "Save Changes?", wantTitle: "Save Changes?"},
		{name: "no-match", want: "Retry", wantErr: "no button matching"},
		{name: "needle-excludes-all", want: "Save", window: "nonexistent", wantErr: "no button matching"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			win, _, _, err := selectButton(windows, tc.want, tc.window)
			if tc.wantErr != "" {
				if err == nil || !strings.Contains(err.Error(), tc.wantErr) {
					t.Fatalf("got err %v, want it to contain %q", err, tc.wantErr)
				}
				return
			}
			if err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if win.Title != tc.wantTitle {
				t.Errorf("window: got %q, want %q", win.Title, tc.wantTitle)
			}
		})
	}
}

func TestSelectButton_NotesLooseMatch(t *testing.T) {
	// macOS typographers write the apostrophe as \u2019; agents type ASCII.
	windows := []dialogWindow{{PID: 1, Title: "Unity", Buttons: []string{"Don\u2019t Save"}}}
	win, button, note, err := selectButton(windows, "dont save", "")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if button != "Don\u2019t Save" || win.Title != "Unity" {
		t.Errorf("got window %q button %q", win.Title, button)
	}
	if !strings.Contains(note, "clicked \"Don\u2019t Save\"") {
		t.Errorf("note: got %q, want it to name the button it matched", note)
	}
}

func TestButtonMatches(t *testing.T) {
	tests := []struct {
		want, have string
		expect     bool
	}{
		{"Continue", "Continue", true},
		{"continue", "Continue", true},
		{"dont save", "Don\u2019t Save", true},
		{"don't save", "Don\u2019t Save", true},
		{"save", "Save Changes", true},
		{"Save", "Don\u2019t Save", false},
		{"quit", "Don't Quit", false},
		{"cancel", "OK", false},
		{"", "OK", false},
	}
	for _, tc := range tests {
		if got := buttonMatches(tc.want, tc.have); got != tc.expect {
			t.Errorf("buttonMatches(%q, %q): got %v, want %v", tc.want, tc.have, got, tc.expect)
		}
	}
}

// TestSelectButton_DoesNotPressNegatedTitle is the destructive case: a prompt whose only match is
// "Don't Save" must not be clicked by "Save".
func TestSelectButton_DoesNotPressNegatedTitle(t *testing.T) {
	windows := []dialogWindow{{PID: 1, Title: "Save Changes?", Buttons: []string{"Don't Save", "Cancel"}}}
	_, _, _, err := selectButton(windows, "Save", "")
	if err == nil || !strings.Contains(err.Error(), "no button matching") {
		t.Fatalf("got err %v, want no match for Save", err)
	}
}

func TestClickScopeError(t *testing.T) {
	one := []dialogWindow{{PID: 1, Title: "Unity"}, {PID: 1, Title: "Scene - Foo - Unity"}}
	two := []dialogWindow{{PID: 1, Title: "Unity"}, {PID: 2, Title: "Scene - Bar - Unity"}}

	tests := []struct {
		name    string
		filter  string
		window  string
		windows []dialogWindow
		wantErr bool
	}{
		{name: "filtered listing is safe", filter: "Foo", windows: two},
		{name: "named window is safe", windows: two, window: "Unity"},
		{name: "fallback with one process is safe", windows: one},
		{name: "fallback across two processes is refused", windows: two, wantErr: true},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			err := clickScopeError(tc.filter, tc.window, tc.windows)
			if (err != nil) != tc.wantErr {
				t.Fatalf("got err %v, want error %v", err, tc.wantErr)
			}
			if err != nil && !strings.Contains(err.Error(), "--window") {
				t.Errorf("error should point at --window: %v", err)
			}
		})
	}
}

func TestParseClickResult(t *testing.T) {
	action, err := parseClickResult("CLICKED\x1fSave Changes?\x1fDon't Save\n", 1, "Save Changes?", "Don't Save")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if action.PID != 1 || action.Window != "Save Changes?" || action.Button != "Don't Save" {
		t.Errorf("got %+v", action)
	}

	for _, tc := range []struct{ out, want string }{
		{"NO_PROCESS\x1f65279", "no process with pid 1"},
		{"NO_WINDOW\x1fSave Changes?", "is gone"},
		{"NO_BUTTON\x1fSave Changes?\x1fDon't Save", "is not on window"},
		{"gibberish", "unexpected dialogs click output"},
	} {
		if _, err := parseClickResult(tc.out, 1, "Save Changes?", "Don't Save"); err == nil || !strings.Contains(err.Error(), tc.want) {
			t.Errorf("output %q: got err %v, want it to contain %q", tc.out, err, tc.want)
		}
	}
}

func TestAccessibilityPermissionError(t *testing.T) {
	tests := []struct {
		name   string
		stderr string
		want   string
	}{
		{
			name:   "assistive access",
			stderr: "system error: Not authorized for Accessibility (-1719)",
			want:   "accessibility permission is missing",
		},
		{
			name:   "classic wording",
			stderr: "System Events got an error: osascript is not allowed assistive access. (-1719)",
			want:   "/usr/bin/osascript",
		},
		{
			name:   "automation prompt refused",
			stderr: "System Events got an error: App is not allowed to send Apple events to System Events. (-1743)",
			want:   "automation permission is missing",
		},
		{name: "unrelated failure", stderr: "Syntax error: expected expression but found end of line.", want: ""},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			err := accessibilityPermissionError(tc.stderr)
			if tc.want == "" {
				if err != nil {
					t.Fatalf("got %v, want nil", err)
				}
				return
			}
			if err == nil {
				t.Fatal("expected a permission error")
			}
			if !strings.Contains(err.Error(), tc.want) {
				t.Errorf("error missing %q:\n%s", tc.want, err.Error())
			}
			if !strings.Contains(err.Error(), "Privacy & Security") {
				t.Errorf("error must name the grant to make:\n%s", err.Error())
			}
		})
	}
}

func TestProjectNeedle(t *testing.T) {
	dir := t.TempDir()
	project := filepath.Join(dir, "MyProject")
	if err := os.MkdirAll(project, 0755); err != nil {
		t.Fatalf("failed to create project dir: %v", err)
	}

	if got := projectNeedle(project); got != "MyProject" {
		t.Errorf("got %q, want MyProject", got)
	}
	if got := projectNeedle(project + string(filepath.Separator)); got != "MyProject" {
		t.Errorf("trailing separator: got %q, want MyProject", got)
	}
	if got := projectNeedle("/Volumes/Nope/OtherProject"); got != "OtherProject" {
		t.Errorf("path not visible from this machine: got %q, want OtherProject", got)
	}
}

func TestProjectNeedle_FallsBackToRunningInstance(t *testing.T) {
	writeInstanceFile(t, client.Instance{
		State:       "ready",
		ProjectPath: "/Users/dev/Work/RunningProject",
		Port:        8090,
		PID:         os.Getpid(),
		Timestamp:   time.Now().UnixMilli(),
	})

	if got := projectNeedle(""); got != "RunningProject" {
		t.Errorf("got %q, want RunningProject", got)
	}
}

func TestDialogsArgs(t *testing.T) {
	click, window, err := dialogsArgs([]string{"--click", "Don't Save", "--window", "Save Changes"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if click != "Don't Save" || window != "Save Changes" {
		t.Errorf("got click %q window %q", click, window)
	}

	if _, _, err := dialogsArgs([]string{"--click"}); err == nil || !strings.Contains(err.Error(), "needs a button title") {
		t.Errorf("bare --click: got err %v, want a usage error", err)
	}

	click, window, err = dialogsArgs(nil)
	if err != nil || click != "" || window != "" {
		t.Errorf("no args: got click %q window %q err %v", click, window, err)
	}
}

// TestDialogsCmd_OffHost explains that the command belongs to the macOS host instead of pretending
// there are no dialogs.
func TestDialogsCmd_OffHost(t *testing.T) {
	if runtime.GOOS == "darwin" {
		t.Skip("this is the host the command runs on")
	}
	err := dialogsCmd([]string{"--click", "Continue"})
	if err == nil {
		t.Fatal("expected an error explaining the command needs macOS")
	}
	if !strings.Contains(err.Error(), "must be run on the macOS host") || !strings.Contains(err.Error(), runtime.GOOS) {
		t.Errorf("got: %v", err)
	}
}

// TestSelectButton_CountsWindowsNotButtonHits: one window with two loosely matching buttons is one
// window, not a two-window tie, and the advice must not send the user to --window.
func TestSelectButton_CountsWindowsNotButtonHits(t *testing.T) {
	windows := []dialogWindow{{PID: 1, Title: "Save Changes?", Buttons: []string{"Save Draft", "Save Changes", "Cancel"}}}
	_, _, _, err := selectButton(windows, "Save", "")
	if err == nil {
		t.Fatal("expected the loose title to be reported as ambiguous")
	}
	msg := err.Error()
	if !strings.Contains(msg, "2 buttons on window") {
		t.Errorf("message should count buttons on the one window:\n%s", msg)
	}
	if strings.Contains(msg, "2 windows") {
		t.Errorf("message must not claim two windows:\n%s", msg)
	}
}

// TestDialogScripts_Structure pins the parts of the AppleScript that no test on this machine can
// execute: sheets belong to a window (not a process), a nameless element is addressed by its parent's
// title just as the listing named it, and no `continue repeat` is used.
func TestDialogScripts_Structure(t *testing.T) {
	for name, script := range map[string]string{"list": dialogListScript, "click": dialogClickScript} {
		t.Run(name, func(t *testing.T) {
			if !strings.Contains(script, "every window of p") {
				t.Error("every window of the process must be enumerated")
			}
			if strings.Contains(script, "every sheet of p") {
				t.Error("sheets are elements of a window, not of the process")
			}
			if !strings.Contains(script, "every sheet of w") {
				t.Error("sheets must be enumerated per window")
			}
			if !strings.Contains(script, "on elementName(el)") {
				t.Error("the shared element-name helper is missing")
			}
			if !strings.Contains(script, `if elTitle is "" then set elTitle to parentTitle`) &&
				!strings.Contains(script, `if sTitle is "" then set sTitle to wTitle`) {
				t.Error("a nameless element must fall back to its parent's title, as the listing does")
			}
		})
	}

	if strings.Contains(dialogClickScript, "continue repeat") {
		t.Error("`continue repeat` is not used anywhere else in these scripts; drop it")
	}
	if !strings.Contains(dialogClickScript, "repeat with w in wins") ||
		strings.Index(dialogClickScript, "repeat with w in wins") > strings.Index(dialogClickScript, "repeat with s in shs") {
		t.Error("the click script must walk windows first, then each window's sheets")
	}
}

// TestDialogsListToClickHandoff records the pid/title/button handoff between the listing and the
// click: the click script must be addressed with exactly what the listing returned.
func TestDialogsListToClickHandoff(t *testing.T) {
	orig := osascriptRunner
	t.Cleanup(func() { osascriptRunner = orig })

	var scripts []string
	var calls [][]string
	osascriptRunner = func(script string, argv ...string) (string, error) {
		scripts = append(scripts, script)
		calls = append(calls, append([]string(nil), argv...))
		if len(scripts) == 1 {
			return dialogRecord(65279, "Save Changes?", "sheet", []string{"Don't Save", "Cancel", "Save"},
				[]string{"The scene has unsaved changes"}), nil
		}
		return "CLICKED" + fieldSep + argv[1] + fieldSep + argv[2], nil
	}

	windows, err := listDialogWindows("Foo")
	if err != nil {
		t.Fatalf("listDialogWindows: %v", err)
	}
	if len(windows) != 1 || windows[0].Title != "Save Changes?" || windows[0].PID != 65279 {
		t.Fatalf("unexpected listing: %+v", windows)
	}
	if got := strings.Join(calls[0], "|"); got != "Foo" {
		t.Errorf("list script argv = %q, want the window needle", got)
	}

	win, button, _, err := selectButton(windows, "Don't Save", "")
	if err != nil {
		t.Fatalf("selectButton: %v", err)
	}
	action, err := clickDialogButton(win, button)
	if err != nil {
		t.Fatalf("clickDialogButton: %v", err)
	}
	if action.PID != 65279 || action.Window != "Save Changes?" || action.Button != "Don't Save" {
		t.Errorf("unexpected action: %+v", action)
	}
	if scripts[1] != dialogClickScript {
		t.Error("the click must run the click script")
	}
	if got := strings.Join(calls[1], "|"); got != "65279|Save Changes?|Don't Save" {
		t.Errorf("click argv = %q, want the pid, window title and button title from the listing", got)
	}
}

// TestProjectNeedle_DoesNotPruneHeartbeats: listing windows is read-only, so the project lookup must
// not go through the instance scan that deletes dead-pid files.
func TestProjectNeedle_DoesNotPruneHeartbeats(t *testing.T) {
	isolatedHome(t)
	path := writeInstanceFile(t, client.Instance{
		State:       "ready",
		ProjectPath: "/Users/dev/Work/RunningProject",
		Port:        8090,
		PID:         999999999,
		Timestamp:   time.Now().UnixMilli(),
	})

	if got := projectNeedle(""); got != "RunningProject" {
		t.Fatalf("got %q, want RunningProject", got)
	}
	if _, err := os.Stat(path); err != nil {
		t.Errorf("a read-only listing deleted the heartbeat file: %v", err)
	}
}
