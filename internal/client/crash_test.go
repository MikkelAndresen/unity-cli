package client

import (
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// monoCrashFixture follows the shape Mono's crash handler writes: a payload block, a per-thread map
// with one thread marked as crashing, and the exception text where a managed exception was recorded.
// Field sets vary between Mono builds, which is what the defensive parsing is for.
const monoCrashFixture = `{
  "wrapper_runtime_version": "0",
  "crash_guid": "1b6a4e0a-2c3e-4b9f-9e2d-7f0c5d3e8a11",
  "payload": {
    "mono_version": "Mono: 5.0.17 (Vue)",
    "domain": "Domain0, Unity",
    "product_name": "Unity",
    "product_version": "6000.4.4f1"
  },
  "signal": 11,
  "managed_stack_trace": "System.NullReferenceException: Object reference not set to an instance of an object\n  at FoW.FogOfWarManager.PrepareAllTeams () [0x00012] in <f00d>:0",
  "threads": {
    "1": {
      "is_crashing_thread": false,
      "managed_backtrace": [
        { "method": { "fullname": "UnityEditor.EditorApplication:Internal_CallTickPlayerUpdate ()", "name": "Internal_CallTickPlayerUpdate", "type_name": "UnityEditor.EditorApplication" } }
      ]
    },
    "7": {
      "is_crashing_thread": true,
      "native_backtrace": "0   Unity  0x0000000104b2f1c4 _ZThn16_N12ComputeBuffer6UploadEv + 148",
      "managed_backtrace": [
        { "offset": 40, "method": { "fullname": "FoW.FogOfWarSplatPass:Dispatch ()", "name": "Dispatch", "type_name": "FoW.FogOfWarSplatPass" } },
        { "offset": 12, "method": { "fullname": "UnityEditor.EditorApplication:Internal_CallUpdateFunctions ()" } }
      ]
    }
  },
  "modules": [
    { "base_address": "0x102a00000", "debug_filename": "Unity", "uuid": "123456789ABCDEF" }
  ]
}`

// monoCrashNativeFixture is the other common spelling: a SIGSEGV with no managed exception, where the
// only readable thing is the crashing thread's top frame.
const monoCrashNativeFixture = `{
  "wrapper_runtime_version": "0",
  "signature": { "pids": [65279, 65300], "signal": 11, "code": 1 },
  "payload": { "mono_version": "Mono: 5.0.17 (Vue)" },
  "threads": {
    "9": {
      "is_crashing_thread": true,
      "managed_backtrace": [
        { "method": { "name": "Dispatch", "type_name": "FoW.FogOfWarSplatPass" } }
      ]
    }
  }
}`

// writeCrashFile puts a file in project with a mod time ageAgo, as an artifact would appear.
func writeCrashFile(t *testing.T, project, name string, ageAgo time.Duration, body string) string {
	t.Helper()
	path := filepath.Join(project, name)
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatalf("failed to create %s: %v", filepath.Dir(path), err)
	}
	if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
		t.Fatalf("failed to write %s: %v", path, err)
	}
	stamp := time.Now().Add(-ageAgo)
	if err := os.Chtimes(path, stamp, stamp); err != nil {
		t.Fatalf("failed to set mtime on %s: %v", path, err)
	}
	return path
}

func TestMonoCrashDetail_ExceptionAndSignal(t *testing.T) {
	project := t.TempDir()
	path := writeCrashFile(t, project, "mono_crash.1b6a4e0a.0.json", 5*time.Second, monoCrashFixture)

	detail := monoCrashDetail(path)
	if !strings.Contains(detail, "signal 11 (SIGSEGV)") {
		t.Errorf("expected the signal, got %q", detail)
	}
	if !strings.Contains(detail, "System.NullReferenceException: Object reference not set to an instance of an object") {
		t.Errorf("expected the exception line, got %q", detail)
	}
	if strings.Contains(detail, "\n") || len(detail) > crashDetailChars+len("signal 11 (SIGSEGV); ") {
		t.Errorf("detail must stay one bounded line, got %d chars: %q", len(detail), detail)
	}
}

func TestMonoCrashDetail_CrashingThreadWhenNoException(t *testing.T) {
	project := t.TempDir()
	path := writeCrashFile(t, project, "mono_crash.0.0.json", time.Second, monoCrashNativeFixture)

	detail := monoCrashDetail(path)
	if !strings.Contains(detail, "signal 11 (SIGSEGV)") {
		t.Errorf("expected the signal from the nested signature object, got %q", detail)
	}
	if !strings.Contains(detail, "FoW.FogOfWarSplatPass.Dispatch") {
		t.Errorf("expected the crashing thread's frame, got %q", detail)
	}
}

// TestMonoCrashDetail_TruncatedReport pins why the parse is defensive: the crash handler can die before
// it finishes writing the document, and the reason still has to come out of the bytes that exist.
func TestMonoCrashDetail_TruncatedReport(t *testing.T) {
	project := t.TempDir()
	truncated := `{"wrapper_runtime_version":"0","payload":{"mono_version":"Mono: 5.0.17"},"managed_stack_trace":"System.InvalidOperationException: Compute buffer was disposed`
	path := writeCrashFile(t, project, "mono_crash.deadbeef.0.json", time.Second, truncated)

	detail := monoCrashDetail(path)
	if !strings.Contains(detail, "System.InvalidOperationException: Compute buffer was disposed") {
		t.Errorf("expected the reason from truncated JSON, got %q", detail)
	}
}

func TestMonoCrashDetail_NoJSONFallsBackToKeywordLine(t *testing.T) {
	project := t.TempDir()
	body := "some tool wrote a non-JSON dump\nGot a SIGSEGV while executing native code.\nmore noise"
	path := writeCrashFile(t, project, "mono_crash.nope.0.json", time.Second, body)

	detail := monoCrashDetail(path)
	if !strings.Contains(detail, "SIGSEGV") {
		t.Errorf("expected the signal line, got %q", detail)
	}
}

func TestCrashReportDetailMacOS(t *testing.T) {
	project := t.TempDir()
	header := `{"app_name":"Unity","app_version":"6000.4.4f1","slice_uuid":"abc","timestamp":"2026-11-04T04:00:00Z","platform":1}`
	body := `{"app_name":"Unity","exception":{"type":"EXC_BAD_ACCESS","signal":"SIGSEGV","types":"EXC_BAD_ACCESS (SIGSEGV)"},"termination":{"reason":"Segmentation fault: 11"},"vmSummary":"VM Region Summary..."}`
	path := writeCrashFile(t, project, "Unity-2026-11-04-040000.ips", 2*time.Second, header+"\n"+body)

	detail := crashReportDetail(path)
	if !strings.Contains(detail, "EXC_BAD_ACCESS (SIGSEGV)") {
		t.Errorf("expected the exception type, got %q", detail)
	}
	if !strings.Contains(detail, "Segmentation fault: 11") {
		t.Errorf("expected the termination reason, got %q", detail)
	}
}

func TestCrashReportDetailPlainTextLegacy(t *testing.T) {
	project := t.TempDir()
	body := "Process:         Unity [65279]\nPath:            /Applications/Unity/Unity.app\nException Type:  EXC_CRASH (SIGABRT)\n"
	path := writeCrashFile(t, project, "Unity-legacy.ips", time.Second, body)

	if detail := crashReportDetail(path); !strings.Contains(detail, "EXC_CRASH (SIGABRT)") {
		t.Errorf("expected the exception type line, got %q", detail)
	}
}

func TestPrevLogDetailReadsTheTail(t *testing.T) {
	project := t.TempDir()
	var b strings.Builder
	for i := 0; i < 400; i++ {
		b.WriteString("Reading grid for team 0\n")
	}
	b.WriteString("Crash!!!\n")
	b.WriteString("=================================================\n")
	b.WriteString("Native stacktrace obtained: Received signal SIGSEGV\n")
	b.WriteString("  at FoW.FogOfWarSplatPass.Dispatch ()\n")
	path := writeCrashFile(t, project, "Editor-prev.log", time.Second, b.String())

	detail := prevLogDetail(path)
	if !strings.Contains(detail, "Received signal SIGSEGV") {
		t.Errorf("expected the crash line nearest the end, got %q", detail)
	}
}

// TestPrevLogDetail_CleanRestartHasNoCrashWording keeps a rotated log from every clean quit from
// counting as crash evidence.
func TestPrevLogDetail_CleanRestartHasNoCrashWording(t *testing.T) {
	project := t.TempDir()
	path := writeCrashFile(t, project, "Editor-prev.log", time.Second,
		"Licensing initialization done.\nQuitting editor\nBatchmode quit successfully invoked - shutting down!\n")

	if detail := prevLogDetail(path); detail != "" {
		t.Errorf("expected no crash wording, got %q", detail)
	}
}

func TestFindCrashArtifacts_SortedNewestFirstAndFilteredByAge(t *testing.T) {
	project := t.TempDir()
	oldest := writeCrashFile(t, project, "mono_crash.old.0.json", 40*time.Minute, monoCrashFixture)
	middle := writeCrashFile(t, project, "mono_crash.oldevent.0.json", 90*time.Second, monoCrashFixture)
	newest := writeCrashFile(t, project, "Logs/mono_crash.new.0.json", 20*time.Second, monoCrashNativeFixture)

	// since = 30s ago: only the newest file qualifies. A dump from an earlier crash must not be blamed
	// on the silence being judged.
	fresh := FindCrashArtifacts(project, time.Now().Add(-30*time.Second), 3)
	if len(fresh) != 1 {
		t.Fatalf("expected only the fresh artifact, got %+v", fresh)
	}
	if fresh[0].Path != newest {
		t.Errorf("Path: got %q, want %q", fresh[0].Path, newest)
	}
	if fresh[0].Kind != CrashKindMono || fresh[0].Detail == "" {
		t.Errorf("unexpected artifact: %+v", fresh[0])
	}

	all := FindCrashArtifacts(project, time.Now().Add(-time.Hour), 2)
	if len(all) != 2 {
		t.Fatalf("expected the limit to apply, got %+v", all)
	}
	if all[0].Path != newest || all[1].Path != middle {
		t.Errorf("expected newest first, got %q then %q", all[0].Path, all[1].Path)
	}
	if all[1].Path == oldest {
		t.Errorf("the limit should drop the oldest, got %q", all[1].Path)
	}
}

// TestFindCrashArtifacts_CleanPrevLogNotEvidence: Editor-prev.log exists after every restart, so only
// crash wording makes it an artifact.
func TestFindCrashArtifacts_CleanPrevLogNotEvidence(t *testing.T) {
	home := writeInstanceFiles(t, map[string]Instance{})
	writeCrashFile(t, home, filepath.Join("Library", "Logs", "Unity", "Editor-prev.log"), 10*time.Second,
		"Batchmode quit successfully invoked - shutting down!\n")

	if got := FindCrashArtifacts(t.TempDir(), time.Now().Add(-time.Minute), 3); len(got) != 0 {
		t.Errorf("expected no artifacts, got %+v", got)
	}
}

func TestFindCrashArtifacts_MacReportAndPrevLog(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)

	reports := filepath.Join(home, "Library", "Logs", "DiagnosticReports")
	logs := filepath.Join(home, "Library", "Logs", "Unity")
	for _, d := range []string{reports, logs} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			t.Fatalf("failed to create %s: %v", d, err)
		}
	}
	report := writeCrashFile(t, home, filepath.Join("Library", "Logs", "DiagnosticReports", "Unity-2026-11-04-040000.ips"),
		5*time.Second, "Process: Unity [65279]\nException Type:  EXC_BAD_ACCESS (SIGSEGV)\n")
	prev := writeCrashFile(t, home, filepath.Join("Library", "Logs", "Unity", "Editor-prev.log"),
		6*time.Second, "Native stacktrace obtained: Received signal SIGABRT\n")

	got := FindCrashArtifacts(t.TempDir(), time.Now().Add(-time.Minute), 5)
	if len(got) != 2 {
		t.Fatalf("expected both macOS artifacts, got %+v", got)
	}
	if got[0].Path != report || got[1].Path != prev {
		t.Errorf("expected newest first: %v", []string{got[0].Path, got[1].Path})
	}
	if !strings.Contains(got[1].Detail, "SIGABRT") {
		t.Errorf("prev log detail: %q", got[1].Detail)
	}
}

func TestClampLine(t *testing.T) {
	tests := []struct {
		in   string
		max  int
		want string
	}{
		{"first\nsecond", 100, "first"},
		{"  padded  ", 100, "padded"},
		{"abcdefg", 3, "abc…"},
		{"", 10, ""},
	}
	for _, tc := range tests {
		if got := clampLine(tc.in, tc.max); got != tc.want {
			t.Errorf("clampLine(%q, %d): got %q, want %q", tc.in, tc.max, got, tc.want)
		}
	}
}

func TestDroppedConnection(t *testing.T) {
	tests := []struct {
		name string
		err  error
		want bool
	}{
		{"nil", nil, false},
		{"eof", io.EOF, true},
		{"unexpected eof", io.ErrUnexpectedEOF, true},
		{"wrapped eof", fmt.Errorf("Post %q: %w", "http://x", io.EOF), true},
		{"refused", errors.New("dial tcp 127.0.0.1:8090: connect: connection refused"), true},
		{"reset", errors.New("read tcp: read: connection reset by peer"), true},
		{"broken pipe", errors.New("write tcp 127.0.0.1:8090: write: broken pipe"), true},
		{"deadline is not a drop", fmt.Errorf("Post \"http://127.0.0.1:8090/command\": %w", os.ErrDeadlineExceeded), false},
		{"http status", errors.New("HTTP 500 from Unity"), false},
	}
	for _, tc := range tests {
		if got := droppedConnection(tc.err); got != tc.want {
			t.Errorf("%s: got %v, want %v", tc.name, got, tc.want)
		}
	}
}
