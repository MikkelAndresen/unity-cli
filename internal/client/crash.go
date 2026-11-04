package client

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"
	"unicode/utf8"
)

// Crash kinds reported by CrashArtifact.Kind. They name where a file came from, which is what tells
// the reader how to interpret its Detail.
const (
	CrashKindMono    = "mono_crash"
	CrashKindReport  = "crash_report"
	CrashKindPrevLog = "unity_prev_log"
)

// Crash artifact bounds. A project that has collected dumps from many crashes, or a mono report of
// a few hundred KB, must not turn one failed request into a wall of text.
const (
	crashArtifactLimit = 3
	crashDetailChars   = 200
	crashJSONReadLimit = 512 << 10
	crashLogTailLimit  = 16 << 10
	crashScanLineLimit = 200
)

// MonoCrashGlob matches Mono's crash dump. Unity writes it into the process' working directory,
// which is the project root for an editor started from the Hub, and the file outlives Logs/Editor.log
// (which rotates on the next editor start, so the crash itself disappears from it).
const MonoCrashGlob = "mono_crash.*.json"

// macReportGlob and macPrevLogFile are the macOS host's own records of a dead editor. They live in
// the user's home, so a CLI running in a container only reaches them when the host home is mounted.
const (
	macReportGlob   = "Unity*.ips"
	macPrevLogFile  = "Editor-prev.log"
	macReportsDir   = "Library/Logs/DiagnosticReports"
	macUnityLogsDir = "Library/Logs/Unity"
)

// CrashArtifact is one file on disk that says why an editor stopped. Detail is the first line worth
// reading from it, already bounded to crashDetailChars; it is empty when the file holds nothing that
// reads as a crash.
type CrashArtifact struct {
	Path    string    `json:"path"`
	Kind    string    `json:"kind"`
	Detail  string    `json:"detail,omitempty"`
	ModTime time.Time `json:"modified"`
}

// Age is how long ago the artifact was written, for printing.
func (a CrashArtifact) Age() string { return FormatAge(time.Since(a.ModTime)) }

// FindCrashArtifacts looks for crash evidence for project written after since, newest first, at most
// limit of them. project is a Unity project root ("" skips the project-local kinds).
//
// The since filter is what keeps an old dump from accusing a later run of a crash it did not have:
// callers pass the older of "when this request started" and "the last heartbeat".
func FindCrashArtifacts(project string, since time.Time, limit int) []CrashArtifact {
	if limit <= 0 {
		limit = crashArtifactLimit
	}

	type target struct {
		dir     string
		pattern string
		kind    string
	}
	var targets []target
	for _, dir := range projectCrashDirs(project) {
		targets = append(targets, target{dir, MonoCrashGlob, CrashKindMono})
	}
	for _, home := range crashLogHomes(project) {
		targets = append(targets,
			target{filepath.Join(home, macReportsDir), macReportGlob, CrashKindReport},
			target{filepath.Join(home, macUnityLogsDir), macPrevLogFile, CrashKindPrevLog},
		)
	}

	var found []CrashArtifact
	for _, t := range targets {
		matches, err := filepath.Glob(filepath.Join(t.dir, t.pattern))
		if err != nil {
			continue
		}
		for _, path := range matches {
			info, err := os.Stat(path)
			if err != nil || info.IsDir() || !info.ModTime().After(since) {
				continue
			}
			art := CrashArtifact{Path: path, Kind: t.kind, ModTime: info.ModTime(), Detail: artifactDetail(path, t.kind)}
			// Unity rotates Editor-prev.log on every restart, clean ones included, so the file alone
			// proves nothing; only its crash wording counts.
			if t.kind == CrashKindPrevLog && art.Detail == "" {
				continue
			}
			found = append(found, art)
		}
	}

	sort.Slice(found, func(i, j int) bool { return found[i].ModTime.After(found[j].ModTime) })
	if len(found) > limit {
		found = found[:limit]
	}
	return found
}

// projectCrashDirs are the places an editor writes its own crash dump: the project root (its working
// directory), and Logs/ next to it.
func projectCrashDirs(project string) []string {
	if project == "" {
		return nil
	}
	project = expandHome(project)
	dirs := []string{project}
	if logs := filepath.Join(project, "Logs"); dirExists(logs) {
		dirs = append(dirs, logs)
	}
	return dirs
}

func dirExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && info.IsDir()
}

// crashLogHomes lists user homes whose macOS log folders may hold the host's crash records: this
// process' home, and — because a sandbox mounts host paths unchanged — the home the project path sits
// in. Guessing a home from a project path only ever costs a missed file when it is wrong.
func crashLogHomes(project string) []string {
	var homes []string
	seen := map[string]bool{}
	add := func(h string) {
		h = strings.TrimRight(filepath.ToSlash(h), "/")
		if h == "" || seen[h] {
			return
		}
		seen[h] = true
		homes = append(homes, h)
	}

	if home, err := os.UserHomeDir(); err == nil {
		add(home)
	}
	if parts := strings.Split(strings.TrimPrefix(filepath.ToSlash(project), "/"), "/"); len(parts) >= 2 &&
		(parts[0] == "Users" || parts[0] == "home") {
		add("/" + parts[0] + "/" + parts[1])
	}
	return homes
}

// artifactDetail reads the one line of an artifact that carries the reason, by kind.
func artifactDetail(path, kind string) string {
	switch kind {
	case CrashKindMono:
		return monoCrashDetail(path)
	case CrashKindReport:
		return crashReportDetail(path)
	case CrashKindPrevLog:
		return prevLogDetail(path)
	}
	return ""
}

// monoExceptionKeys are the Mono crash-report fields that carry the failure, best first. The report
// shape varies by Mono version, hence the list; payload/threads hold the same story further down.
var monoExceptionKeys = []string{"managed_stack_trace", "crash_reason", "reason", "exception", "message", "error"}

// monoCrashDetail pulls the exception and the signal out of a mono_crash JSON report. These files are
// written by the crash handler itself, so they can be truncated mid-object; everything here falls
// back instead of requiring a complete document.
func monoCrashDetail(path string) string {
	head, err := readHead(path, crashJSONReadLimit)
	if err != nil || len(head) == 0 {
		return ""
	}

	var obj map[string]json.RawMessage
	if json.Unmarshal(head, &obj) == nil {
		if detail := monoCrashFields(obj); detail != "" {
			return clampLine(detail, crashDetailChars)
		}
	}
	// Unparseable (truncated, or a Mono build that wrote something else): pick the reason out of the
	// raw text rather than reporting nothing.
	if match := monoKeyInText(head); match != "" {
		return clampLine(match, crashDetailChars)
	}
	return clampLine(firstKeywordLine(string(head)), crashDetailChars)
}

// monoCrashFields reads the report's signal, the exception line, and the crashing thread's frame.
func monoCrashFields(obj map[string]json.RawMessage) string {
	var parts []string
	if sig := monoSignal(obj); sig != "" {
		parts = append(parts, sig)
	}

	message := firstStringField(obj, monoExceptionKeys...)
	if message == "" {
		message = nestedStringField(obj, "payload", "crash_sigsegv", "exception", "crash_reason", "reason", "message")
	}
	if message == "" {
		message = crashingThreadFrame(obj)
	}
	if line := firstKeywordLine(message); line != "" {
		parts = append(parts, line)
	} else if trimmed := clampLine(strings.TrimSpace(message), crashDetailChars); trimmed != "" {
		parts = append(parts, trimmed)
	}

	return strings.Join(parts, "; ")
}

// monoSignal names the signal that killed the process, from either the flat or the nested field.
func monoSignal(obj map[string]json.RawMessage) string {
	num := 0
	for _, key := range []string{"crash_signal", "signal"} {
		if n, ok := intField(obj, key); ok {
			num = n
			break
		}
	}
	if num == 0 {
		if raw, ok := obj["signature"]; ok {
			var nested map[string]json.RawMessage
			if json.Unmarshal(raw, &nested) == nil {
				if n, ok := intField(nested, "signal"); ok {
					num = n
				}
			}
		}
	}
	if num == 0 {
		return ""
	}
	if name, ok := signalNames[num]; ok {
		return fmt.Sprintf("signal %d (%s)", num, name)
	}
	return fmt.Sprintf("signal %d", num)
}

// signalNames covers the signals a native Unity crash actually raises.
var signalNames = map[int]string{
	4:  "SIGILL",
	5:  "SIGTRAP",
	6:  "SIGABRT",
	7:  "SIGBUS",
	8:  "SIGFPE",
	10: "SIGBUS",
	11: "SIGSEGV",
	13: "SIGPIPE",
}

// crashingThreadFrame returns the top frame of the thread Mono marked as crashing, which is the
// managed method the editor died in when no exception object was recorded.
func crashingThreadFrame(obj map[string]json.RawMessage) string {
	raw, ok := obj["threads"]
	if !ok {
		return ""
	}
	var threads map[string]struct {
		IsCrashingThread bool `json:"is_crashing_thread"`
		ManagedBacktrace []struct {
			Method struct {
				Fullname string `json:"fullname"`
				Name     string `json:"name"`
				TypeName string `json:"type_name"`
			} `json:"method"`
		} `json:"managed_backtrace"`
	}
	if json.Unmarshal(raw, &threads) != nil {
		return ""
	}

	frame := ""
	for _, th := range threads {
		if len(th.ManagedBacktrace) == 0 {
			continue
		}
		method := th.ManagedBacktrace[0].Method
		text := method.Fullname
		if text == "" {
			text = strings.TrimSpace(method.TypeName + "." + method.Name)
		}
		if text == "" {
			continue
		}
		if th.IsCrashingThread {
			return text
		}
		if frame == "" {
			frame = text
		}
	}
	return frame
}

func firstStringField(obj map[string]json.RawMessage, keys ...string) string {
	for _, key := range keys {
		raw, ok := obj[key]
		if !ok {
			continue
		}
		var s string
		if json.Unmarshal(raw, &s) == nil && strings.TrimSpace(s) != "" {
			return s
		}
	}
	return ""
}

func nestedStringField(obj map[string]json.RawMessage, object string, keys ...string) string {
	raw, ok := obj[object]
	if !ok {
		return ""
	}
	var nested map[string]json.RawMessage
	if json.Unmarshal(raw, &nested) != nil {
		return ""
	}
	return firstStringField(nested, keys...)
}

func intField(obj map[string]json.RawMessage, key string) (int, bool) {
	raw, ok := obj[key]
	if !ok {
		return 0, false
	}
	var n int
	if json.Unmarshal(raw, &n) != nil {
		return 0, false
	}
	return n, true
}

// monoKeyInText finds "exception"/"crash_reason"-style values in raw bytes when the JSON did not
// parse. Values are cut at the first quote or escape so a partial file still yields a readable line.
var monoKeyInTextRE = regexp.MustCompile(`"(managed_stack_trace|crash_reason|reason|exception|crash_sigsegv)"[[:space:]]*:[[:space:]]*"([^"\\]{6,300})`)

func monoKeyInText(head []byte) string {
	for _, m := range monoKeyInTextRE.FindAllStringSubmatch(string(head), -1) {
		if line := firstKeywordLine(m[2]); line != "" {
			return line
		}
	}
	return ""
}

// crashKeywords are the words that mark a line as the reason something died, rather than surrounding
// noise. Matched case-insensitively.
var crashKeywords = []string{
	"exception", "segfault", "sigsegv", "sigabrt", "sigbus", "sigill",
	"signal", "segmentation", "native crash", "crash", "abort", "assertion failed",
}

// firstKeywordLine returns the first line of text carrying crash wording.
func firstKeywordLine(text string) string {
	count := 0
	for _, line := range strings.Split(text, "\n") {
		if count > crashScanLineLimit {
			break
		}
		count++
		if line = strings.TrimSpace(line); line != "" && lineHasCrashKeyword(line) {
			return line
		}
	}
	return ""
}

// lastKeywordLine scans from the end: for a log, the line nearest the death is the one to print.
func lastKeywordLine(text string) string {
	lines := strings.Split(text, "\n")
	for i := len(lines) - 1; i >= 0; i-- {
		line := strings.TrimSpace(lines[i])
		if line != "" && lineHasCrashKeyword(line) {
			return line
		}
	}
	return ""
}

func lineHasCrashKeyword(line string) bool {
	lower := strings.ToLower(line)
	for _, kw := range crashKeywords {
		if strings.Contains(lower, kw) {
			return true
		}
	}
	return false
}

// crashReportDetail reads an macOS DiagnosticReports .ips file. Its first line is a one-line JSON
// header and the second a full report object; older reports are plain text. Both spellings are read
// by keyword, since Apple's field names for the exception have changed.
func crashReportDetail(path string) string {
	head, err := readHead(path, crashLogTailLimit)
	if err != nil || len(head) == 0 {
		return ""
	}
	for _, chunk := range strings.SplitN(string(head), "\n", 3) {
		var obj map[string]json.RawMessage
		if json.Unmarshal([]byte(chunk), &obj) != nil {
			continue
		}
		if detail := reportFields(obj); detail != "" {
			return clampLine(detail, crashDetailChars)
		}
	}
	if line := firstKeywordLine(string(head)); line != "" {
		return clampLine(line, crashDetailChars)
	}
	return ""
}

func reportFields(obj map[string]json.RawMessage) string {
	var parts []string
	if raw, ok := obj["exception"]; ok {
		var exc map[string]json.RawMessage
		if json.Unmarshal(raw, &exc) == nil {
			if t := firstStringField(exc, "types", "type", "signal"); t != "" {
				parts = append(parts, strings.TrimSpace(t))
			}
		} else if s := strings.TrimSpace(string(raw)); s != "" && s != "null" {
			parts = append(parts, strings.Trim(s, `"`))
		}
	}
	if raw, ok := obj["termination"]; ok {
		var term map[string]json.RawMessage
		if json.Unmarshal(raw, &term) == nil {
			if r := firstStringField(term, "reason", "indicator"); r != "" {
				parts = append(parts, "termination: "+r)
			}
		}
	}
	return strings.Join(parts, "; ")
}

// prevLogDetail reads the tail of Unity's rotated Editor log. Editor.log itself is the *current*
// session, so after a crash the only record of the previous one is Editor-prev.log — and it is only
// interesting near its end.
func prevLogDetail(path string) string {
	tail, err := readTail(path, crashLogTailLimit)
	if err != nil || len(tail) == 0 {
		return ""
	}
	return clampLine(lastKeywordLine(string(tail)), crashDetailChars)
}

func readHead(path string, limit int64) ([]byte, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	return io.ReadAll(io.LimitReader(f, limit))
}

func readTail(path string, limit int64) ([]byte, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		return nil, err
	}
	start := int64(0)
	if info.Size() > limit {
		start = info.Size() - limit
	}
	if _, err := f.Seek(start, io.SeekStart); err != nil {
		return nil, err
	}
	return io.ReadAll(f)
}

// clampLine keeps one printed line short and free of embedded newlines, which would otherwise break
// the CLI's indented report layout. The cut is on rune boundaries, because crash text is full of "→"
// and non-ASCII identifiers.
func clampLine(s string, max int) string {
	s = strings.TrimSpace(s)
	if idx := strings.IndexByte(s, '\n'); idx >= 0 {
		s = strings.TrimSpace(s[:idx])
	}
	s = strings.ReplaceAll(s, "\r", "")
	if utf8.RuneCountInString(s) <= max {
		return s
	}
	return string([]rune(s)[:max]) + "…"
}

// SilenceVerdict is the CLI's judgement about an editor that stopped answering on HTTP while its
// heartbeat file is still on disk. It is built from files only, so it works when the editor is gone
// and when this process cannot see the editor's machine at all.
type SilenceVerdict struct {
	// Crashed is the verdict: the editor's process is provably gone, or its heartbeat stopped
	// mid-sentence with no clean-shutdown marker while crash artifacts appeared.
	Crashed bool
	// Clean means the connector wrote its "stopped" marker: the editor quit on purpose, so a silent
	// heartbeat is the expected end of the story.
	Clean bool
	// Stale means the heartbeat stopped arriving at all. Silent is the weaker condition the request path
	// needs: the beat did not advance while the caller was waiting, which says "it never came back to
	// answer" without needing an absolute age.
	Stale       bool
	Silent      bool
	NotAdvanced bool
	// PIDProof is what the process table here proves about the heartbeat's pid, and PIDNote says why
	// in the words the report prints.
	PIDProof pidProof
	PIDNote  string
	// BeatAge is how long ago the last heartbeat was written; HeartbeatFile is where it was read from.
	BeatAge       time.Duration
	HeartbeatFile string
	ProjectPath   string
	Artifacts     []CrashArtifact
}

// AssessSilence judges the editor described by a heartbeat file that is still on disk.
//
// since is the earliest moment an artifact can belong to this event: the older of the request start
// and the last heartbeat. Artifacts older than that belong to an earlier crash and must not be
// blamed on it.
//
// baselineBeat is the heartbeat stamp the caller saw when its request died, or 0 for a caller that is
// only reading the current state (status). With a baseline, silence is "that beat never advanced": an
// editor that reloaded and beat again under a new stamp is alive, however old the file it left behind
// is.
func AssessSilence(inst *Instance, since time.Time, baselineBeat int64) SilenceVerdict {
	v := SilenceVerdict{}
	if inst == nil {
		return v
	}

	v.Clean = inst.State == "stopped"
	v.BeatAge = heartbeatAgeOf(inst)
	v.Stale = v.BeatAge >= BeatSilenceAge
	v.NotAdvanced = baselineBeat > 0 && inst.Timestamp <= baselineBeat
	v.Silent = v.Stale || v.NotAdvanced
	v.HeartbeatFile = inst.SourceFile
	v.ProjectPath = inst.ProjectPath
	v.PIDProof, v.PIDNote = judgePID(inst.PID)
	if v.Clean {
		return v
	}
	if sessionStartedAfterBeat(inst) {
		// The editor was restarted after this heartbeat was written, so this record describes the
		// previous process. Its vanished pid and any dump it left behind say nothing about the editor
		// that is starting now; the caller falls back to the lockfile's startup evidence.
		return v
	}

	v.Artifacts = FindCrashArtifacts(inst.ProjectPath, since, crashArtifactLimit)
	switch {
	case v.PIDProof == pidGone:
		// The process table this CLI can read contains Unity processes but not this one, so the
		// editor is gone; no artifact is needed to say so.
		v.Crashed = true
	case v.Silent && len(v.Artifacts) > 0:
		// Either the editor's machine is invisible from here (a container, where a missing pid proves
		// nothing) or no artifact survived it: the dump is the evidence.
		v.Crashed = true
	}
	return v
}

// Reason states why the verdict is what it is, in one line, for callers that must answer "how do you
// know?".
func (v SilenceVerdict) Reason() string {
	var parts []string
	if v.PIDProof == pidGone {
		parts = append(parts, v.PIDNote)
	}
	if len(v.Artifacts) > 0 {
		parts = append(parts, fmt.Sprintf("%d crash artifact(s), newest %s", len(v.Artifacts), filepath.Base(v.Artifacts[0].Path)))
	}
	if v.Silent {
		advanced := "did not advance"
		if v.Stale {
			advanced = fmt.Sprintf("silent %s", FormatAge(v.BeatAge))
		}
		parts = append(parts, fmt.Sprintf("heartbeat %s with no \"stopped\" marker", advanced))
	}
	if len(parts) == 0 {
		if v.Clean {
			return "the connector wrote its \"stopped\" marker"
		}
		return "no crash evidence"
	}
	return strings.Join(parts, "; ")
}

// FailureClass is why a request's connection died, decided before the CLI reports it.
type FailureClass int

const (
	// FailureUnknown keeps the transport error as it was: the disk says nothing about why.
	FailureUnknown FailureClass = iota
	// FailureCrash is an editor process that died mid-request.
	FailureCrash
	// FailureReload is an editor that is still alive but not answering on this port right now
	// (domain reload, recompile, a build, or a connector that rebound its port).
	FailureReload
)

// ConnectionFailure is a transport error that the on-disk evidence explains. It wraps the original
// error, whose text stays in the message, so callers that match connection failures keep working.
type ConnectionFailure struct {
	Class     FailureClass
	Instance  *Instance
	Verdict   SilenceVerdict
	Reload    string
	Transport error
}

func (f *ConnectionFailure) Error() string {
	switch f.Class {
	case FailureCrash:
		return f.crashText()
	case FailureReload:
		return f.reloadText()
	}
	return f.Transport.Error()
}

func (f *ConnectionFailure) Unwrap() error { return f.Transport }

// Crashed reports whether the editor is dead rather than merely unreachable. Retry loops use it: a
// dead editor is not "momentarily unreachable", and polling it is how an agent wastes ten minutes.
func (f *ConnectionFailure) Crashed() bool { return f != nil && f.Class == FailureCrash }

func (f *ConnectionFailure) crashText() string {
	var b strings.Builder
	inst := f.Instance
	if inst != nil && inst.PID > 0 {
		// A crash dump or a vanished pid says the process ended, either way; what the pid alone proves is
		// spelled out on the pid line below.
		fmt.Fprintf(&b, "editor process %d exited during the request (crash): %v", inst.PID, f.Transport)
	} else {
		fmt.Fprintf(&b, "the Unity editor stopped answering mid-request with no clean shutdown (crash): %v", f.Transport)
	}

	if inst != nil {
		fmt.Fprintf(&b, "\n  heartbeat: %s (state %q, pid %d, last beat %s ago, no \"stopped\" marker)",
			f.Verdict.HeartbeatFile, inst.State, inst.PID, FormatAge(f.Verdict.BeatAge))
	}
	if f.Verdict.PIDProof != pidGone && f.Verdict.PIDNote != "" {
		fmt.Fprintf(&b, "\n  pid:       %s", f.Verdict.PIDNote)
	}
	for _, art := range f.Verdict.Artifacts {
		fmt.Fprintf(&b, "\n  crash %s: %s (%s ago)", art.Kind, art.Path, art.Age())
		if art.Detail != "" {
			fmt.Fprintf(&b, "\n             %s", art.Detail)
		}
	}
	if len(f.Verdict.Artifacts) == 0 {
		b.WriteString("\n  next:      no crash dump found; read <project>/Logs/Editor.log before Unity restarts (it rotates), and ask the user to relaunch.")
	} else {
		b.WriteString("\n  next:      Unity must be restarted; Logs/Editor.log rotates on restart, so the artifact above is the record of this crash.")
	}
	return b.String()
}

func (f *ConnectionFailure) reloadText() string {
	var b strings.Builder
	fmt.Fprintf(&b, "Unity reloaded or rebound its port during the request (not a crash): %v", f.Transport)
	if f.Reload != "" {
		fmt.Fprintf(&b, "\n  heartbeat: %s", f.Reload)
	}
	b.WriteString("\n  next:      run 'unity-cli wait' to block until it answers again, then repeat the request.")
	return b.String()
}

// explainDroppedConnection classifies a failed request before it is reported, so that a hard crash,
// a domain reload and a dropped socket do not all come back as the same bare EOF. It reads only files
// the connector already wrote, so it costs nothing when the editor is fine.
func explainDroppedConnection(inst *Instance, start time.Time, transport error) error {
	base := fmt.Errorf("cannot connect to Unity at port %d: %w", inst.Port, transport)
	if !droppedConnection(transport) {
		return base
	}

	// The project, not the port: a connector that comes back from a domain reload may pick a new one,
	// and a port-scoped lookup would then find nothing and lose the crash record with it. Without a
	// project path in the heartbeat there is nothing to scope by, so the port has to do.
	port := portScope(inst)
	beat := NewestHeartbeat(inst.ProjectPath, port)
	if beat == nil {
		return base
	}

	since := start
	if beatTime := time.UnixMilli(beat.Timestamp); beatTime.Before(since) {
		since = beatTime
	}
	verdict := AssessSilence(beat, since, inst.Timestamp)
	if !verdict.Crashed && verdict.PIDProof == pidUnproven {
		// The crash handler writes its dump a moment after the socket dies, and from a container the pid
		// proves nothing, so a verdict that is one artifact short of "crashed" gets one more look before
		// the caller is sent away with a bare EOF. A pid this machine can see is already decided: pidAlive
		// means the editor is up (so this is a reload), and pidGone means the verdict needs no artifact.
		if second := waitForCrashDump(beat, since, inst.Timestamp); second.Crashed {
			verdict = second
			beat = NewestHeartbeat(inst.ProjectPath, portScope(inst))
		}
	}
	failure := &ConnectionFailure{Instance: beat, Verdict: verdict, Transport: base}

	switch {
	case verdict.Crashed:
		failure.Class = FailureCrash
		return failure
	case reloadNote(beat, inst.Port) != "":
		failure.Class = FailureReload
		failure.Reload = reloadNote(beat, inst.Port)
		return failure
	}
	return base
}

// crashArtifactGrace is how long a failed request waits for a crash dump that the dying editor has not
// finished writing. A variable so tests can shrink it.
var crashArtifactGrace = 500 * time.Millisecond

// waitForCrashDump takes the one extra reading, after the grace.
func waitForCrashDump(beat *Instance, since time.Time, baselineBeat int64) SilenceVerdict {
	time.Sleep(crashArtifactGrace)
	return AssessSilence(NewestHeartbeat(beat.ProjectPath, portScope(beat)), since, baselineBeat)
}

// portScope is how a heartbeat should be looked up again: by project when it names one, else by port.
func portScope(inst *Instance) int {
	if inst.ProjectPath != "" {
		return 0
	}
	return inst.Port
}

// reloadNote describes a heartbeat that proves the editor is merely busy, or "" when the silence
// looks like nothing of the kind.
func reloadNote(beat *Instance, requestedPort int) string {
	age := heartbeatAgeOf(beat)
	if beat.Port != requestedPort && age < BeatSilenceAge {
		return fmt.Sprintf("%s (state %q, pid %d) is answering on port %d now",
			beat.SourceFile, beat.State, beat.PID, beat.Port)
	}
	if age < BeatSilenceAge {
		switch beat.State {
		case "starting", "compiling", "reloading", "refreshing", "building", "modal":
			return fmt.Sprintf("%s still says %q (last beat %s ago)", beat.SourceFile, beat.State, FormatAge(age))
		}
	}
	return ""
}

// droppedConnection reports the transport failures that mean "the socket died", as opposed to "the
// editor is slow" (a timeout) — the case where saying "crash" would be wrong.
func droppedConnection(err error) bool {
	if err == nil {
		return false
	}
	if errors.Is(err, os.ErrDeadlineExceeded) {
		return false
	}
	if errors.Is(err, io.ErrUnexpectedEOF) || errors.Is(err, io.EOF) {
		return true
	}
	msg := strings.ToLower(err.Error())
	for _, marker := range []string{"connection refused", "connection reset", "connection closed", "broken pipe", "eof"} {
		if strings.Contains(msg, marker) {
			return true
		}
	}
	return false
}

// BeatSilenceAge is how long a heartbeat may go unwritten before the connector counts as stopped. It
// rewrites about twice a second even while compiling, so a gap this long means its main thread is
// gone; the margin covers the couple of seconds a domain reload pauses the writes.
const BeatSilenceAge = 3 * time.Second

// heartbeatAgeOf is how old an instance file's last beat is. A file with no timestamp is treated as
// ancient rather than fresh, so a connector that never wrote one is never mistaken for a live editor.
func heartbeatAgeOf(inst *Instance) time.Duration {
	if inst == nil || inst.Timestamp <= 0 {
		return 24 * time.Hour
	}
	return time.Since(time.UnixMilli(inst.Timestamp))
}
