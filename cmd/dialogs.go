package cmd

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// The connector's modal handling lives inside the editor, so it cannot see a dialog that
// appears before its assembly loads — startup dialogs (license, safe mode, "project already
// open", package resolution). This command reaches the same dialogs from outside the process
// with AppleScript + System Events, which is macOS-only and needs Accessibility permission.

// osascriptPath is the AppleScript interpreter. It is addressed absolutely so the CLI does not
// depend on the caller's PATH (a sandbox PATH may have no shell tools at all).
const osascriptPath = "/usr/bin/osascript"

// Separators emitted by the AppleScript side: one per nesting level, because a dialog's texts are
// multi-line and "\n" is therefore unusable. Control characters cannot appear in window titles,
// button names, or dialog text.
const (
	recordSep = "\x1e" // RS, between window records
	fieldSep  = "\x1f" // US, between the fields of one window record
	listSep   = "\x1d" // GS, between the button names or texts inside one field
)

// noUnityMarker is what the list script returns when no process is named "Unity".
const noUnityMarker = "NO_UNITY_PROCESS"

// dialogListScript lists every window (and sheet) of every Unity process as records of:
// pid US title US role US buttons GS texts, terminated by RS.
// argv[1] is a substring a window title must contain to be listed ("" lists all Unity windows).
const dialogListScript = `on run argv
	set needle to item 1 of argv
	set US to (character id 31)
	set GS to (character id 29)
	set RS to (character id 30)
	set out to ""
	tell application "System Events"
		set procs to (every process whose name is "Unity")
		if (count of procs) is 0 then return "NO_UNITY_PROCESS"
		repeat with p in procs
			set thePid to ""
			try
				set thePid to (unix id of p) as text
			end try
			set wins to {}
			try
				set wins to (every window of p)
			end try
			repeat with w in wins
				set parentTitle to my elementName(w)
				if (needle is not "") and (parentTitle does not contain needle) then continue repeat
				set out to out & my recordWindow(thePid, parentTitle, w, US, GS, RS)
				set shs to {}
				try
					set shs to (every sheet of w)
				end try
				repeat with s in shs
					set out to out & my recordWindow(thePid, parentTitle, s, US, GS, RS)
				end repeat
			end repeat
		end repeat
	end tell
	return out
end run

on elementName(el)
	set n to ""
	try
		set n to (name of el) as text
		if n is "missing value" then set n to ""
	end try
	return n
end elementName

on recordWindow(thePid, parentTitle, el, US, GS, RS)
	set elTitle to my elementName(el)
	if elTitle is "" then set elTitle to parentTitle
	set elRole to ""
	try
		set elRole to (role of el) as text
	end try
	set btns to {}
	try
		set btns to (name of every button of el)
	end try
	set txts to {}
	try
		set txts to (name of every static text of el)
	end try
	return thePid & US & elTitle & US & elRole & US & my joined(btns, GS) & US & my joined(txts, GS) & RS
end recordWindow

on joined(items, d)
	set AppleScript's text item delimiters to d
	set t to items as text
	set AppleScript's text item delimiters to ""
	return t
end joined`

// dialogClickScript presses one button of one Unity window, addressed by pid + exact window title
// + exact button title, so it cannot land on a different dialog that appeared since the list.
// argv: 1 = pid, 2 = exact window title, 3 = exact button title.
const dialogClickScript = `on run argv
	set thePid to (item 1 of argv) as integer
	set winTitle to item 2 of argv
	set btnName to item 3 of argv
	set US to (character id 31)
	tell application "System Events"
		set procs to (every process whose unix id is thePid)
		if (count of procs) is 0 then return "NO_PROCESS" & US & thePid
		set p to item 1 of procs
		set elts to {}
		try
			set elts to (every window of p) & (every sheet of p)
		end try
		set sawWindow to false
		repeat with w in elts
			if my elementName(w) is not winTitle then continue repeat
			set sawWindow to true
			repeat with b in (buttons of w)
				if my elementName(b) is not btnName then continue repeat
				click b
				return "CLICKED" & US & winTitle & US & btnName
			end repeat
		end repeat
		if sawWindow then return "NO_BUTTON" & US & winTitle & US & btnName
		return "NO_WINDOW" & US & winTitle
	end tell
end run

on elementName(el)
	set n to ""
	try
		set n to (name of el) as text
		if n is "missing value" then set n to ""
	end try
	return n
end elementName`

// dialogWindow is one Unity window or sheet as seen from outside the editor process.
type dialogWindow struct {
	PID     int      `json:"pid"`
	Title   string   `json:"title"`
	Role    string   `json:"role,omitempty"`
	Buttons []string `json:"buttons"`
	Texts   []string `json:"texts"`
	// TextsTotal is set only when Texts was cut short. The editor window carries hundreds of
	// labels; a dialog carries a handful, and the JSON is what an agent reads.
	TextsTotal int `json:"texts_total,omitempty"`
}

const maxListedTexts = 12

// dialogsReport is what `unity-cli dialogs` prints.
type dialogsReport struct {
	ProjectFilter string         `json:"project_filter,omitempty"`
	Windows       []dialogWindow `json:"windows"`
	Clicked       *dialogAction  `json:"clicked,omitempty"`
	Note          string         `json:"note,omitempty"`
}

type dialogAction struct {
	PID    int    `json:"pid"`
	Window string `json:"window"`
	Button string `json:"button"`
}

// dialogsTimeout bounds one osascript round trip. Accessibility calls into a busy or starting
// editor can stall; without a deadline the CLI would hang the same way the connector does.
var dialogsTimeout = 30 * time.Second

// dialogsArgs reads this command's own flags. A bare --click parses as the value "true", which
// no Unity button is titled, so it is reported as a missing value instead of a failed lookup.
func dialogsArgs(args []string) (wantClick, windowNeedle string, err error) {
	flags := parseSubFlags(args)
	wantClick = strings.TrimSpace(flags["click"])
	if wantClick == "true" {
		return "", "", fmt.Errorf(`--click needs a button title, e.g. --click "Continue" (run 'unity-cli dialogs' first to list them)`)
	}
	return wantClick, strings.TrimSpace(flags["window"]), nil
}

// collectWindows lists the windows for the project filter, falling back to every Unity window.
// Startup dialogs (license prompts, safe mode) rarely carry the project name in their title, so a
// filter that finds nothing says nothing about whether a dialog is up.
func collectWindows(needle string, list func(string) ([]dialogWindow, error)) ([]dialogWindow, string, error) {
	windows, err := list(needle)
	if err != nil {
		return nil, needle, err
	}
	if len(windows) == 0 && needle != "" {
		windows, err = list("")
		if err != nil {
			return nil, "", err
		}
		return windows, "", nil
	}
	return windows, needle, nil
}

// dialogsCmd lists or answers the Unity windows of this machine, from outside the editor.
func dialogsCmd(args []string) error {
	if runtime.GOOS != "darwin" {
		return fmt.Errorf("dialogs must be run on the macOS host: it reads Unity's windows with AppleScript and System Events (Accessibility), and this machine is %s. The agent sandbox is granted no Accessibility permission by design. Run 'unity-cli --project <path> dialogs' in a Terminal on the host, or ask the user what the editor is showing", runtime.GOOS)
	}

	wantClick, windowNeedle, err := dialogsArgs(args)
	if err != nil {
		return err
	}

	windows, filter, err := collectWindows(projectNeedle(flagProject), listDialogWindows)
	if err != nil {
		return err
	}

	report := dialogsReport{ProjectFilter: filter, Windows: windows}

	if wantClick == "" {
		report.Note = dialogsNote(windows)
		return printDialogs(report)
	}

	target, button, note, err := selectButton(windows, wantClick, windowNeedle)
	if err != nil {
		report.Note = dialogsNote(windows)
		_ = printDialogs(report)
		return err
	}
	action, err := clickDialogButton(target, button)
	if err != nil {
		return err
	}
	report.ProjectFilter = filter
	report.Windows = nil
	report.Clicked = action
	report.Note = note
	return printDialogs(report)
}

func printDialogs(report dialogsReport) error {
	b, err := json.MarshalIndent(report, "", "  ")
	if err != nil {
		return err
	}
	fmt.Println(string(b))
	return nil
}

func dialogsNote(windows []dialogWindow) string {
	if len(windows) == 0 {
		return "no Unity windows found: Unity is not running on this machine, is still showing its splash window, or Accessibility permission is missing"
	}
	return "answer a button with: unity-cli dialogs --click \"<title>\" --window \"<window title>\""
}

// listDialogWindows runs the list script and parses its records.
func listDialogWindows(needle string) ([]dialogWindow, error) {
	out, err := runOsascript(dialogListScript, needle)
	if err != nil {
		return nil, err
	}
	return parseDialogWindows(out)
}

// parseDialogWindows turns the script's separator-delimited records into structured windows.
// It is pure so the format stays covered by tests on every OS.
func parseDialogWindows(out string) ([]dialogWindow, error) {
	out = strings.TrimSpace(out)
	if out == "" || out == noUnityMarker {
		return nil, nil
	}

	var windows []dialogWindow
	for _, rec := range strings.Split(out, recordSep) {
		rec = strings.Trim(rec, "\n")
		if rec == "" {
			continue
		}
		fields := strings.Split(rec, fieldSep)
		if len(fields) != 5 {
			return nil, fmt.Errorf("unexpected dialogs output (%d fields in %q)", len(fields), rec)
		}
		win := dialogWindow{
			Title:   fields[1],
			Role:    fields[2],
			Buttons: splitList(fields[3]),
			Texts:   splitList(fields[4]),
		}
		pid, convErr := strconv.Atoi(strings.TrimSpace(fields[0]))
		if convErr != nil {
			return nil, fmt.Errorf("non-numeric pid %q in dialogs output", fields[0])
		}
		win.PID = pid
		if len(win.Texts) > maxListedTexts {
			win.TextsTotal = len(win.Texts)
			win.Texts = win.Texts[:maxListedTexts]
		}
		windows = append(windows, win)
	}
	return windows, nil
}

func splitList(s string) []string {
	if s == "" {
		return nil
	}
	parts := strings.Split(s, listSep)
	items := make([]string, 0, len(parts))
	for _, p := range parts {
		if p = strings.TrimSpace(p); p != "" && p != "missing value" {
			items = append(items, p)
		}
	}
	if len(items) == 0 {
		return nil
	}
	return items
}

// selectButton picks the window and button title to click from the listed windows. Matching stays
// in Go (not AppleScript) so the rules are testable, mirror 'modal answer' (case- and
// apostrophe-insensitive), and so an ambiguous title fails instead of clicking the wrong dialog.
func selectButton(windows []dialogWindow, want, windowNeedle string) (dialogWindow, string, string, error) {
	type hit struct {
		win    dialogWindow
		button string
	}
	var exact, loose []hit

	for _, w := range windows {
		if windowNeedle != "" && !strings.Contains(strings.ToLower(w.Title), strings.ToLower(windowNeedle)) {
			continue
		}
		for _, b := range w.Buttons {
			if b == want {
				exact = append(exact, hit{w, b})
			} else if buttonMatches(want, b) {
				loose = append(loose, hit{w, b})
			}
		}
	}

	var hits []hit
	switch {
	case len(exact) > 0:
		hits = exact
	case len(loose) > 0:
		hits = loose
	default:
		return dialogWindow{}, "", "", fmt.Errorf("no button matching %q is listed; run 'unity-cli dialogs' to see the open dialogs and their buttons", want)
	}

	if len(hits) > 1 {
		var b strings.Builder
		fmt.Fprintf(&b, "%q matches %d windows — name one with --window \"<title>\":", want, len(hits))
		for _, h := range hits {
			fmt.Fprintf(&b, "\n  pid %d: %q (%s)", h.win.PID, h.win.Title, strings.Join(h.win.Buttons, ", "))
		}
		return dialogWindow{}, "", "", fmt.Errorf("%s", b.String())
	}

	note := ""
	if hits[0].button != want {
		note = fmt.Sprintf("clicked %q (matched %q)", hits[0].button, want)
	}
	return hits[0].win, hits[0].button, note, nil
}

// buttonMatches is the loose form of a button title test: case-insensitive, apostrophe-insensitive
// (macOS writes "Don’t Save" where an agent types "Don't Save"), and a substring is enough.
func buttonMatches(want, have string) bool {
	want = normalizeButton(want)
	return want != "" && strings.Contains(normalizeButton(have), want)
}

var buttonApostrophes = strings.NewReplacer("'", "", "\u2019", "", "\u2018", "", "`", "")

func normalizeButton(s string) string {
	return strings.TrimSpace(buttonApostrophes.Replace(strings.ToLower(s)))
}

// clickDialogButton presses the button, addressed by the exact pid/title/name it was listed with.
func clickDialogButton(win dialogWindow, button string) (*dialogAction, error) {
	out, err := runOsascript(dialogClickScript, fmt.Sprintf("%d", win.PID), win.Title, button)
	if err != nil {
		return nil, err
	}
	return parseClickResult(out, win.PID, win.Title, button)
}

// parseClickResult maps the click script's markers to an action or an explanation. The dialog may
// have been dismissed between the listing and the click, which is what the NO_* markers mean.
func parseClickResult(out string, pid int, window, button string) (*dialogAction, error) {
	fields := strings.Split(strings.TrimSpace(out), fieldSep)
	switch fields[0] {
	case "CLICKED":
		return &dialogAction{PID: pid, Window: window, Button: button}, nil
	case "NO_PROCESS":
		return nil, fmt.Errorf("no process with pid %d on this machine — the editor quit or changed pid", pid)
	case "NO_WINDOW":
		return nil, fmt.Errorf("window %q is gone; re-run 'unity-cli dialogs' and click again", window)
	case "NO_BUTTON":
		return nil, fmt.Errorf("button %q is not on window %q any more; re-run 'unity-cli dialogs'", button, window)
	default:
		return nil, fmt.Errorf("unexpected dialogs click output: %q", out)
	}
}

// runOsascript runs one embedded AppleScript with argv, mapping permission failures to an
// explanation of what to grant, since that is the failure an agent cannot fix from here.
func runOsascript(script string, argv ...string) (string, error) {
	if _, err := os.Stat(osascriptPath); err != nil {
		return "", fmt.Errorf("%s not found: dialogs needs the macOS host's AppleScript interpreter", osascriptPath)
	}

	ctx, cancel := context.WithTimeout(context.Background(), dialogsTimeout)
	defer cancel()

	args := append([]string{"-e", script}, argv...)
	// osascript hands the arguments after -e to the script's `run` handler as argv, which keeps
	// project paths and button titles out of the script text (no quoting or injection).
	cmd := exec.CommandContext(ctx, osascriptPath, args...)
	var stdout, stderr strings.Builder
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	if err := cmd.Run(); err != nil {
		if ctx.Err() == context.DeadlineExceeded {
			return "", fmt.Errorf("osascript did not answer within %s: Unity may be busy or the Accessibility prompt is waiting for a click", dialogsTimeout)
		}
		if permErr := accessibilityPermissionError(stderr.String()); permErr != nil {
			return "", permErr
		}
		msg := strings.TrimSpace(stderr.String())
		if msg == "" {
			msg = err.Error()
		}
		return "", fmt.Errorf("osascript failed: %s", msg)
	}
	return stdout.String(), nil
}

// accessibilityPermissionError recognises the two macOS permission refusals an agent cannot fix
// from a sandbox, and says precisely which grant is missing.
func accessibilityPermissionError(stderr string) error {
	low := strings.ToLower(stderr)
	switch {
	case strings.Contains(low, "assistive access"), strings.Contains(low, "-1719"),
		strings.Contains(low, "not authorized") && strings.Contains(low, "accessibility"):
		return fmt.Errorf(`accessibility permission is missing on the macOS host (osascript: %s).
  Grant it there: System Settings -> Privacy & Security -> Accessibility, then enable (or add with +)
    /usr/bin/osascript
    and the app that started this command (Terminal, iTerm, VS Code, or the agent's shell)
  then re-run 'unity-cli dialogs'. A sandbox or container cannot be granted this.`, strings.TrimSpace(stderr))
	case strings.Contains(low, "-1743"), strings.Contains(low, "not allowed to control"):
		return fmt.Errorf(`automation permission is missing on the macOS host (osascript: %s).
  Grant it there: System Settings -> Privacy & Security -> Automation, enable
    <the app running unity-cli> -> System Events
  then re-run 'unity-cli dialogs'.`, strings.TrimSpace(stderr))
	}
	return nil
}

// projectNeedle narrows the window list to the requested project. Unity's window titles carry the
// project folder name ("SceneName - ProjectName - Unity 6 …"), so the folder name is the filter;
// "" means list every Unity process's windows.
func projectNeedle(project string) string {
	if project == "" {
		if inst, err := client.DiscoverInstance("", 0); err == nil {
			project = inst.ProjectPath
		}
	}
	project = strings.TrimRight(filepath.ToSlash(project), "/")
	if project == "" {
		return ""
	}
	// A path that exists here but is not a directory is not a project: filtering window titles by
	// its name would match nothing. A path that does not exist here (another machine's project) is
	// still worth filtering by name.
	if info, err := os.Stat(project); err == nil && !info.IsDir() {
		return ""
	}
	return filepath.Base(project)
}
