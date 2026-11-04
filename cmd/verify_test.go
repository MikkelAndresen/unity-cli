package cmd

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// fakeEditor answers the tools verify uses and records every call.
type fakeEditor struct {
	t                    *testing.T
	compileErrors        bool                     // heartbeat flag after the compile
	connector            string                   // heartbeat connectorVersion
	finishedAt           int64                    // heartbeat compileFinishedAt seen while polling
	echoFinished         *int64                   // refresh_unity echoes compile_finished_at; nil: connector without the clock
	pid                  int                      // heartbeat pid (0: no restart, no restart detection)
	stuckState           string                   // heartbeat state while polling (default "ready")
	diagnostics          map[bool][]interface{}   // compile_diagnostics by source_tree_only; nil: tool unknown (old connector)
	diagnosticsCompiling bool                     // compile_diagnostics keeps answering compiling=true
	console              []string                 // console error entries (old-connector fallback)
	consoleCapped        bool                     // console reply wraps the entries in the capped object
	provenance           map[string]interface{}   // compile_diagnostics' provenance object; nil: connector predates it
	provenanceSeq        []map[string]interface{} // per-call provenance; overrides provenance while non-empty
	refreshReplies       []map[string]interface{} // per-call refresh_unity extras; overrides reply while non-empty
	tests                map[string]interface{}   // run_tests summary; nil: tests not expected
	testsSuccess         bool
	testsMessage         string
	burst                map[string]interface{} // burst_status reply; nil: connector has no burst_status tool
	burstSuccess         bool
	burstMessage         string
	refreshErr           error
	calls                []string
	params               map[string]map[string]interface{}
}

func (f *fakeEditor) send(command string, params interface{}) (*client.CommandResponse, error) {
	p, _ := params.(map[string]interface{})
	key := command
	if command == "compile_diagnostics" {
		key = fmt.Sprintf("compile_diagnostics(source_tree_only=%v)", p["source_tree_only"])
	}
	if command == "console" && p["clear"] == true {
		key = "console(clear)"
	}
	f.calls = append(f.calls, key)
	if f.params == nil {
		f.params = map[string]map[string]interface{}{}
	}
	f.params[command] = p

	data := func(v interface{}) json.RawMessage { b, _ := json.Marshal(v); return b }
	switch command {
	case "console":
		if p["clear"] == true {
			return &client.CommandResponse{Success: true, Message: "Console cleared."}, nil
		}
		if f.consoleCapped {
			return &client.CommandResponse{Success: true, Data: data(map[string]interface{}{
				"entries":          f.console,
				"entries_total":    len(f.console),
				"entries_returned": len(f.console),
				"truncated":        false,
			})}, nil
		}
		return &client.CommandResponse{Success: true, Data: data(f.console)}, nil
	case "refresh_unity":
		if f.refreshErr != nil {
			return nil, f.refreshErr
		}
		reply := map[string]interface{}{"refresh_triggered": true, "compile_requested": true}
		if f.echoFinished != nil {
			reply["compile_finished_at"] = *f.echoFinished
		}
		if len(f.refreshReplies) > 0 {
			for k, v := range f.refreshReplies[0] {
				reply[k] = v
			}
			f.refreshReplies = f.refreshReplies[1:]
			if finished, ok := reply["compile_finished_at"].(int); ok {
				f.finishedAt = int64(finished) + 1000
			}
		}
		return &client.CommandResponse{Success: true, Data: data(reply)}, nil
	case "compile_diagnostics":
		if f.diagnostics == nil {
			return &client.CommandResponse{Success: false, Message: "Unknown command: compile_diagnostics"}, nil
		}
		list := f.diagnostics[p["source_tree_only"] == true]
		total, listed := len(list), list
		if limit, ok := p["limit"].(int); ok && limit > 0 && len(listed) > limit {
			listed = listed[:limit]
		}
		reply := map[string]interface{}{
			"compiling": f.diagnosticsCompiling, "total": total, "diagnostics": listed,
		}
		prov := f.provenance
		if len(f.provenanceSeq) > 0 {
			prov = f.provenanceSeq[0]
			f.provenanceSeq = f.provenanceSeq[1:]
		}
		if prov != nil {
			reply["provenance"] = prov
		}
		return &client.CommandResponse{Success: true, Data: data(reply)}, nil
	case "run_tests":
		if f.tests == nil {
			f.t.Fatalf("run_tests called but not expected")
		}
		return &client.CommandResponse{Success: f.testsSuccess, Message: f.testsMessage, Data: data(f.tests)}, nil
	case "burst_status":
		if f.burst == nil {
			return &client.CommandResponse{Success: false, Message: "Unknown command: burst_status"}, nil
		}
		return &client.CommandResponse{Success: f.burstSuccess, Message: f.burstMessage, Data: data(f.burst)}, nil
	}
	f.t.Fatalf("unexpected command %q", command)
	return nil, nil
}

func (f *fakeEditor) resolve() (*client.Instance, error) {
	state := f.stuckState
	if state == "" {
		state = "ready"
	}
	return &client.Instance{
		State:             state,
		CompileErrors:     f.compileErrors,
		ConnectorVersion:  f.connector,
		CompileFinishedAt: f.finishedAt,
		PID:               f.pid,
	}, nil
}

func (f *fakeEditor) run(args ...string) (int, string) {
	f.t.Helper()
	orig := statusPollInterval
	statusPollInterval = time.Millisecond
	defer func() { statusPollInterval = orig }()
	var out bytes.Buffer
	code := verifyCmd(context.Background(), args, f.send, f.send, f.resolve, 0, &out)
	return code, out.String()
}

func (f *fakeEditor) called(key string) bool {
	for _, c := range f.calls {
		if c == key {
			return true
		}
	}
	return false
}

func clean() map[bool][]interface{} { return map[bool][]interface{}{true: {}, false: {}} }

func passing(n int) map[string]interface{} {
	return map[string]interface{}{"total": n, "passed": n, "failed": 0, "skipped": 0, "failures": []string{}}
}

func TestVerify_CleanCompileAndPassingTests(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(3), testsSuccess: true}
	code, out := f.run()
	if code != verifyOK {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if out != "compile: ok\ntests: 3/3 passed\n" {
		t.Errorf("output = %q", out)
	}
	want := []string{"console(clear)", "refresh_unity", "compile_diagnostics(source_tree_only=true)", "burst_status", "run_tests"}
	if strings.Join(f.calls, ",") != strings.Join(want, ",") {
		t.Errorf("calls = %v, want %v", f.calls, want)
	}
	if f.params["refresh_unity"]["compile"] != "request" {
		t.Errorf("refresh params = %v, want compile=request", f.params["refresh_unity"])
	}
	if f.params["run_tests"]["mode"] != "EditMode" || f.params["run_tests"]["autoSaveScenes"] != true {
		t.Errorf("run_tests params = %v, want EditMode with autoSaveScenes", f.params["run_tests"])
	}
}

func TestVerify_CompileErrorsAreListedAndTestsSkipped(t *testing.T) {
	errs := []interface{}{map[string]interface{}{
		"file": "Assets/Foo.cs", "line": 10, "col": 5, "severity": "error", "code": "CS0103", "message": "The name 'x' does not exist",
	}}
	f := &fakeEditor{t: t, compileErrors: true, diagnostics: map[bool][]interface{}{true: errs, false: errs}}
	code, out := f.run()
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1", code)
	}
	want := "compile: 1 error(s)\n  Assets/Foo.cs:10:5 CS0103 The name 'x' does not exist\n"
	if out != want {
		t.Errorf("output = %q, want %q", out, want)
	}
}

// The connector answers a missing member/type or an obsolete API with the closest real answer;
// verify prints it under the error, because reading it is cheaper than going looking for it.
func TestVerify_HintsPrintIndentedUnderTheirError(t *testing.T) {
	errs := []interface{}{
		map[string]interface{}{
			"file": "Assets/Foo.cs", "line": 12, "col": 9, "severity": "error", "code": "CS1061",
			"message": "'Transform' does not contain a definition for 'positon'",
			"hint":    "closest: Transform.position, Transform.localPosition",
		},
		map[string]interface{}{
			"file": "Assets/Foo.cs", "line": 13, "col": 3, "severity": "error", "code": "CS0619",
			"message": "'Object.GetInstanceID()' is obsolete: 'Use GetEntityId instead.'",
			"hint":    "use UnityEngine.Object.GetEntityId()",
		},
		map[string]interface{}{
			"file": "Assets/Foo.cs", "line": 14, "col": 20, "severity": "error", "code": "CS0103",
			"message": "The name 'speed' does not exist in the current context",
		},
	}
	f := &fakeEditor{t: t, compileErrors: true, diagnostics: map[bool][]interface{}{true: errs, false: errs}}
	code, out := f.run()
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1", code)
	}
	want := "compile: 3 error(s)\n" +
		"  Assets/Foo.cs:12:9 CS1061 'Transform' does not contain a definition for 'positon'\n" +
		"    hint: closest: Transform.position, Transform.localPosition\n" +
		"  Assets/Foo.cs:13:3 CS0619 'Object.GetInstanceID()' is obsolete: 'Use GetEntityId instead.'\n" +
		"    hint: use UnityEngine.Object.GetEntityId()\n" +
		"  Assets/Foo.cs:14:20 CS0103 The name 'speed' does not exist in the current context\n"
	if out != want {
		t.Errorf("output =\n%s\nwant\n%s", out, want)
	}
}

func TestVerify_HintSurvivesTheJSONReport(t *testing.T) {
	errs := []interface{}{map[string]interface{}{
		"file": "Assets/Foo.cs", "line": 12, "col": 9, "severity": "error", "code": "CS0246",
		"message": "The type or namespace name 'Vectr3' could not be found",
		"hint":    "closest types: UnityEngine.Vector3 (UnityEngine.CoreModule)",
	}}
	f := &fakeEditor{t: t, compileErrors: true, diagnostics: map[bool][]interface{}{true: errs, false: errs}}
	code, out := f.run("--json")
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1", code)
	}
	if !strings.Contains(out, `"hint":"closest types: UnityEngine.Vector3 (UnityEngine.CoreModule)"`) {
		t.Errorf("json = %s, want the hint kept under the error", out)
	}
}

func TestVerify_ErrorsOnlyInPackagesAreNotReportedAsClean(t *testing.T) {
	pkg := []interface{}{map[string]interface{}{
		"file": "Library/PackageCache/com.x@1/A.cs", "line": 1, "col": 2, "severity": "error", "code": "CS0246", "message": "missing type",
	}}
	f := &fakeEditor{t: t, compileErrors: true, diagnostics: map[bool][]interface{}{true: {}, false: pkg}}
	code, out := f.run()
	if code != verifyFailed || !strings.Contains(out, "Library/PackageCache/com.x@1/A.cs:1:2 CS0246") {
		t.Errorf("exit = %d, output = %q; want 1 listing the package error", code, out)
	}
}

func TestVerify_FailingTestsShowFirstLineOfEach(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), testsMessage: "1 test(s) failed.", tests: map[string]interface{}{
		"total": 2, "passed": 1, "failed": 1, "skipped": 0,
		"failures": []string{"Ns.T.Fails:   Expected: 1\n  But was: 2\nat Ns.T.Fails ()"},
	}}
	code, out := f.run()
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1", code)
	}
	if out != "compile: ok\ntests: 1/2 passed, 1 failed\n  Ns.T.Fails: Expected: 1\n" {
		t.Errorf("output = %q", out)
	}
}

func TestVerify_ForwardsTestOptions(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(1), testsSuccess: true}
	if code, out := f.run("--assembly", "A.Tests,B.Tests", "--filter", "Ns.Class"); code != verifyOK {
		t.Fatalf("exit = %d: %s", code, out)
	}
	p := f.params["run_tests"]
	if p["assembly"] != "A.Tests,B.Tests" || p["filter"] != "Ns.Class" {
		t.Errorf("run_tests params = %v", p)
	}
}

func TestVerify_NoTestsStopsAfterCompile(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean()}
	code, out := f.run("--no-tests")
	if code != verifyOK || out != "compile: ok\n" || f.called("run_tests") {
		t.Errorf("exit = %d, output = %q, calls = %v", code, out, f.calls)
	}
}

// The point of provenance is that the agent stops rebuilding "did the editor pick up my change?"
// by hand, so verify's own output has to carry the line.
func TestVerify_PrintsTheProvenanceLineAfterTheCompileVerdict(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(3), testsSuccess: true,
		provenance: map[string]interface{}{"provenance_line": provLine, "compiled_at_ms": 1_793_782_952_102}}
	code, out := f.run()
	if code != verifyOK {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	want := "compile: ok\n" + provLine + "\ntests: 3/3 passed\n"
	if out != want {
		t.Errorf("output = %q, want %q", out, want)
	}
}

func TestVerify_ASourceWrittenAfterTheCompileIsSaidOutLoud(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(),
		provenance: map[string]interface{}{
			"provenance_line": provLine,
			"stale_count":     1,
			"stale_files":     []string{"Assets/Foo.cs"},
			"stale_line":      "stale: Assets/Foo.cs modified after this compile — run verify again",
		}}
	code, out := f.run("--no-tests")
	if code != verifyOK {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if !strings.Contains(out, "stale: Assets/Foo.cs modified after this compile") {
		t.Errorf("output = %q, want the stale line", out)
	}
}

func TestVerify_StaleAssemblyRefusesToRunTests(t *testing.T) {
	const staleLine = "stale assembly: Foo.Tests.dll (built 12:01:00) is older than Assets/FooTests.cs (12:04:00) — run unity-cli editor refresh --compile"
	f := &fakeEditor{t: t, diagnostics: clean(),
		provenance: map[string]interface{}{
			"provenance_line":      provLine,
			"stale_assembly_count": 1,
			"stale_assembly_line":  staleLine,
			"stale_assemblies": []interface{}{map[string]interface{}{
				"name": "Foo.Tests", "newest_source": "Assets/FooTests.cs",
			}},
		}}
	code, out := f.run()
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1; output:\n%s", code, out)
	}
	if f.called("run_tests") {
		t.Errorf("tests ran against a stale assembly: %v", f.calls)
	}
	if !strings.Contains(out, staleLine) || !strings.Contains(out, "tests: refused (1 stale assembly(s))") {
		t.Errorf("output = %q, want the stale assembly and the refusal", out)
	}
}

// TestVerify_StaleAssemblyOutsideTheAssemblyFilterDoesNotRefuse: verify must apply the same rule as
// `test --assembly`, which only scans the requested assemblies.
func TestVerify_StaleAssemblyOutsideTheAssemblyFilterDoesNotRefuse(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(1), testsSuccess: true,
		provenance: map[string]interface{}{
			"provenance_line":      provLine,
			"stale_assembly_count": 1,
			"stale_assembly_line":  "stale assembly: Other.dll (built 12:01:00) is older than Assets/Other.cs (12:04:00)",
			"stale_assemblies": []interface{}{map[string]interface{}{
				"name": "Other", "newest_source": "Assets/Other.cs",
			}},
		}}
	code, out := f.run("--assembly", "Foo.Tests")
	if code != verifyOK {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if !f.called("run_tests") {
		t.Errorf("tests did not run: %v", f.calls)
	}
	if !strings.Contains(out, "outside --assembly") {
		t.Errorf("output = %q, want a note that the stale assembly is outside the filter", out)
	}
}

// TestVerify_StaleAssemblyInsideTheAssemblyFilterRefuses is the other half: the assembly the tests
// would load is stale, so verify still refuses.
func TestVerify_StaleAssemblyInsideTheAssemblyFilterRefuses(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(),
		provenance: map[string]interface{}{
			"provenance_line":      provLine,
			"stale_assembly_count": 1,
			"stale_assembly_line":  "stale assembly: Foo.Tests.dll is older than its sources",
			"stale_assemblies": []interface{}{map[string]interface{}{
				"name": "Foo.Tests", "newest_source": "Assets/FooTests.cs",
			}},
		}}
	code, out := f.run("--assembly", "Foo.Tests")
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1; output:\n%s", code, out)
	}
	if f.called("run_tests") {
		t.Errorf("tests ran against a stale assembly: %v", f.calls)
	}
	if !strings.Contains(out, "tests: refused (1 stale assembly(s))") {
		t.Errorf("output = %q, want the refusal", out)
	}
}

func TestVerify_AllowStaleRunsTestsAndSaysWhy(t *testing.T) {
	const staleLine = "stale assembly: Foo.Tests.dll (built 12:01:00) is older than Assets/FooTests.cs (12:04:00) — run unity-cli editor refresh --compile"
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(1), testsSuccess: true,
		provenance: map[string]interface{}{
			"provenance_line":      provLine,
			"stale_assembly_count": 1,
			"stale_assembly_line":  staleLine,
		}}
	code, out := f.run("--allow-stale")
	if code != verifyOK {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if !strings.Contains(out, "warning: "+staleLine) || !strings.Contains(out, "tests: 1/1 passed") {
		t.Errorf("output = %q, want the warning and the test result", out)
	}
	if f.params["run_tests"]["allowStale"] != true {
		t.Errorf("run_tests params = %v, want allowStale=true so the connector does not refuse", f.params["run_tests"])
	}
}

func TestVerify_StaleAssemblyInJSONIsAnError(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(),
		provenance: map[string]interface{}{
			"stale_assembly_count": 2,
			"stale_assembly_line":  "stale assembly: Foo.Tests.dll (missing) is older than Assets/FooTests.cs (12:04:00) — run unity-cli editor refresh --compile",
		}}
	code, out := f.run("--json")
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1", code)
	}
	var r verifyResult
	if err := json.Unmarshal([]byte(out), &r); err != nil {
		t.Fatalf("output is not JSON: %v (%s)", err, out)
	}
	if !strings.Contains(r.Error, "stale assembly") || !strings.Contains(r.Error, "--allow-stale") {
		t.Errorf("error = %q, want the stale assembly and the escape hatch", r.Error)
	}
}

func TestVerify_JsonCarriesProvenanceFields(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(),
		provenance: map[string]interface{}{
			"provenance_line": provLine, "compiled_at_ms": 1_793_782_952_102,
			"compiled_at": "2026-11-04T04:02:32.102-05:00", "scan_ms": 4, "scan_complete": true,
			"changed_since_previous_compile": 1, "rebuilt_assemblies": []string{"Game"},
		}}
	code, out := f.run("--no-tests", "--json")
	if code != verifyOK {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	var r verifyResult
	if err := json.Unmarshal([]byte(out), &r); err != nil {
		t.Fatalf("output is not the JSON object: %v (%s)", err, out)
	}
	if len(r.Compile.Provenance) == 0 {
		t.Fatalf("compile.provenance is missing: %s", out)
	}
	var p compileProvenance
	if err := json.Unmarshal(r.Compile.Provenance, &p); err != nil {
		t.Fatalf("compile.provenance is not the connector's object: %v (%s)", err, r.Compile.Provenance)
	}
	if p.ProvenanceLine != provLine || p.CompiledAtMs != 1_793_782_952_102 ||
		p.ChangedSincePrevious == nil || *p.ChangedSincePrevious != 1 || !p.ScanComplete || p.ScanMs != 4 {
		t.Errorf("compile.provenance = %+v, want the connector's fields", p)
	}
}

func TestVerify_OlderConnectorWithoutProvenanceStillVerifies(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean()}
	code, out := f.run("--no-tests")
	if code != verifyOK || out != "compile: ok\n" {
		t.Errorf("exit = %d, output = %q; want a clean verify with no provenance line", code, out)
	}
}

func TestVerify_KeepConsoleSkipsTheClear(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean()}
	f.run("--no-tests", "--keep-console")
	if f.called("console(clear)") {
		t.Errorf("console was cleared despite --keep-console: %v", f.calls)
	}
}

func TestVerify_NoTestsRanIsAFailure(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(0), testsSuccess: true}
	code, out := f.run("--assembly", "Typo.Tests")
	if code != verifyFailed || !strings.Contains(out, "tests: none ran") {
		t.Errorf("exit = %d, output = %q; want 1 and 'none ran'", code, out)
	}
}

func TestVerify_OldConnectorFallsBackToConsoleErrors(t *testing.T) {
	f := &fakeEditor{t: t, compileErrors: true, console: []string{"Assets/Foo.cs(1,1): error CS0103: x\nat stack"}}
	code, out := f.run()
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1", code)
	}
	want := "compile: 1 error(s) (connector < 0.7.0: console errors shown)\n  Assets/Foo.cs(1,1): error CS0103: x\n"
	if out != want {
		t.Errorf("output = %q, want %q", out, want)
	}
}

func TestVerify_CappedConnectorConsoleErrorsAreReadFromEntries(t *testing.T) {
	f := &fakeEditor{t: t, compileErrors: true, consoleCapped: true,
		console: []string{"Assets/Foo.cs(1,1): error CS0103: x\nat stack"}}
	code, out := f.run()
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1", code)
	}
	want := "compile: 1 error(s) (connector < 0.7.0: console errors shown)\n  Assets/Foo.cs(1,1): error CS0103: x\n"
	if out != want {
		t.Errorf("output = %q, want %q", out, want)
	}
}

func TestConsoleEntryMessages(t *testing.T) {
	tests := []struct {
		name string
		data string
		want []string
	}{
		{"bare array", `["a","b"]`, []string{"a", "b"}},
		{"capped object", `{"entries":["a","b"],"truncated":true}`, []string{"a", "b"}},
		{"capped object without entries", `{"entries":[],"truncated":true}`, []string{}},
		{"empty", ``, nil},
		{"unreadable", `not json`, nil},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := consoleEntryMessages([]byte(tt.data))
			if !reflect.DeepEqual(got, tt.want) {
				t.Errorf("entries = %v, want %v", got, tt.want)
			}
		})
	}
}

func TestVerify_DialogIsExitTwoAndNamed(t *testing.T) {
	dialog := modalBlockedError(&client.Instance{State: "modal", Modal: &client.ModalDialog{
		Kind: "alert", ID: 7, Texts: []string{"Script Updating Consent"},
		Buttons: []client.ModalButton{{Index: 2, Title: "No", IsCancel: true}},
	}}, "refresh_unity", true)
	f := &fakeEditor{t: t, diagnostics: clean(), refreshErr: dialog}
	code, out := f.run()
	if code != verifyNoReply || !strings.Contains(out, "Script Updating Consent") || !strings.Contains(out, "modal answer") {
		t.Errorf("exit = %d, output = %q; want 2 naming the dialog and how to answer it", code, out)
	}
}

func TestVerify_TestsThatCannotRunAreExitTwo(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), testsMessage: "Open scenes have unsaved changes.",
		tests: map[string]interface{}{"blocked": "dirty-scenes", "scenes": []string{"Untitled"}}}
	code, out := f.run()
	if code != verifyNoReply || !strings.Contains(out, "unsaved changes") {
		t.Errorf("exit = %d, output = %q; want 2", code, out)
	}
}

func TestVerify_JSONOutput(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(2), testsSuccess: true}
	code, out := f.run("--json")
	var r struct {
		Compile struct{ OK bool } `json:"compile"`
		Tests   struct{ Passed int }
		Exit    int
	}
	if err := json.Unmarshal([]byte(out), &r); err != nil {
		t.Fatalf("output is not one JSON object: %q (%v)", out, err)
	}
	if code != verifyOK || !r.Compile.OK || r.Tests.Passed != 2 || r.Exit != 0 {
		t.Errorf("exit = %d, parsed = %+v", code, r)
	}
}

func TestVerify_OldConnectorRunningEveryTestIsSaid(t *testing.T) {
	f := &fakeEditor{t: t, connector: "0.5.1", diagnostics: clean(), tests: passing(320), testsSuccess: true}
	code, out := f.run("--assembly", "A.Tests")
	if code != verifyOK || !strings.Contains(out, "connector < 0.7.0 ignores --assembly") {
		t.Errorf("exit = %d, output = %q; want the note", code, out)
	}
	f = &fakeEditor{t: t, connector: "0.7.0", diagnostics: clean(), tests: passing(3), testsSuccess: true}
	if _, out := f.run("--assembly", "A.Tests"); strings.Contains(out, "ignores --assembly") {
		t.Errorf("note shown for connector 0.7.0: %q", out)
	}
}

func TestVerify_BurstLineFollowsTheCompile(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(3), testsSuccess: true,
		burstSuccess: true,
		burst: map[string]interface{}{
			"idle": true, "idle_known": true, "compiled": 14, "error_count": 0,
			"project_targets": 14, "method": "eager",
		}}
	code, out := f.run()
	if code != verifyOK {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if out != "compile: ok\nburst: 14 targets compiled, 0 error(s)\ntests: 3/3 passed\n" {
		t.Errorf("output = %q", out)
	}
	if f.params["burst_status"]["wait"] != defaultBurstWaitSeconds {
		t.Errorf("burst_status wait = %v, want %d", f.params["burst_status"]["wait"], defaultBurstWaitSeconds)
	}
}

func TestVerify_BurstErrorsFailLikeCompileErrors(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(3), testsSuccess: true,
		burstSuccess: true,
		burst: map[string]interface{}{
			"idle": true, "compiled": 4, "failed": 1, "error_count": 2,
			"notes": []interface{}{"Burst logs a method's error once per session", "a second caveat"},
			"errors": []interface{}{
				map[string]interface{}{
					"file": "/proj/Assets/Job.cs", "line": 39, "col": 0, "code": "BC1091",
					"message": "The `catch` construction is not supported",
				},
				map[string]interface{}{
					"file": "/engine/IJob.cs", "line": 61, "col": 17, "code": "BC1051",
					"message": "Invalid managed type found for the field `Values` of the struct `MyGame.HeavyJob`",
				},
			},
		}}
	code, out := f.run()
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1; output:\n%s", code, out)
	}
	want := "compile: ok\n" +
		"burst: 4 targets compiled, 2 error(s), 1 failed\n" +
		"  /proj/Assets/Job.cs:39 BC1091 The `catch` construction is not supported\n" +
		"  /engine/IJob.cs:61:17 BC1051 Invalid managed type found for the field `Values` of the struct `MyGame.HeavyJob`\n" +
		// One caveat line only: a two-line summary stays readable.
		"  (Burst logs a method's error once per session)\n"
	if out != want {
		t.Errorf("output = %q, want %q", out, want)
	}
	if f.called("run_tests") {
		t.Errorf("tests ran despite Burst errors: %v", f.calls)
	}
}

func TestVerify_NoBurstSkipsTheCheck(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(1), testsSuccess: true,
		burstSuccess: true,
		burst:        map[string]interface{}{"idle": true, "compiled": 1, "error_count": 1},
		burstMessage: "one error",
	}
	code, out := f.run("--no-burst")
	if code != verifyOK || strings.Contains(out, "burst") || f.called("burst_status") {
		t.Errorf("exit = %d, output = %q, calls = %v; want the burst check skipped", code, out, f.calls)
	}
}

func TestVerify_ConnectorWithoutBurstToolSaysNothing(t *testing.T) {
	f := &fakeEditor{t: t, connector: "0.7.0", diagnostics: clean(), tests: passing(2), testsSuccess: true}
	code, out := f.run()
	if code != verifyOK || strings.Contains(out, "burst") {
		t.Errorf("exit = %d, output = %q; want no burst line when the tool is absent", code, out)
	}
}

func TestVerify_BurstStillCompilingIsReportedButNotAFailure(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(2), testsSuccess: true,
		burstSuccess: true,
		burst: map[string]interface{}{
			"idle": false, "timed_out": true, "wait_s": 5, "not_compiled": 3, "compiled": 1, "error_count": 0,
			"notes": []string{"Burst has not scheduled an eager compile in this editor session"},
		}}
	code, out := f.run("--burst-wait", "5")
	if code != verifyOK {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	want := "compile: ok\n" +
		"burst: 1 targets compiled, 0 error(s), 3 not compiled (Burst still compiling after 5s)\n" +
		"  (Burst has not scheduled an eager compile in this editor session)\n" +
		"tests: 2/2 passed\n"
	if out != want {
		t.Errorf("output = %q, want %q", out, want)
	}
	if f.params["burst_status"]["wait"] != 5 {
		t.Errorf("burst_status wait = %v, want 5", f.params["burst_status"]["wait"])
	}
}

func TestVerify_BurstCheckFailureDoesNotChangeTheVerdict(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(2), testsSuccess: true,
		burst:        map[string]interface{}{"idle": false},
		burstSuccess: false, burstMessage: "Burst is installed but its compiler types are not loaded",
	}
	code, out := f.run()
	if code != verifyOK || !strings.Contains(out, "burst: could not be checked (Burst is installed but") {
		t.Errorf("exit = %d, output = %q; want 0 and the note", code, out)
	}
	if !f.called("run_tests") {
		t.Errorf("tests were skipped after a failed burst check: %v", f.calls)
	}
}

func TestVerify_BurstInJSON(t *testing.T) {
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(1), testsSuccess: true,
		burstSuccess: true,
		burst:        map[string]interface{}{"idle": true, "compiled": 7, "error_count": 0, "burst_version": "2.0.0"},
	}
	code, out := f.run("--json")
	var r struct {
		Burst *struct {
			Compiled int    `json:"compiled"`
			Version  string `json:"burst_version"`
		} `json:"burst"`
		Exit int `json:"exit"`
	}
	if err := json.Unmarshal([]byte(out), &r); err != nil {
		t.Fatalf("output is not one JSON object: %q (%v)", out, err)
	}
	if code != verifyOK || r.Burst == nil || r.Burst.Compiled != 7 || r.Burst.Version != "2.0.0" || r.Exit != 0 {
		t.Errorf("exit = %d, parsed = %+v", code, r)
	}
}

func TestVersionBefore(t *testing.T) {
	cases := map[string]bool{"0.5.1": true, "v0.6.9": true, "0.7.0": false, "1.0.0": false, "dev": false, "": false}
	for v, want := range cases {
		if got := versionBefore(v, 0, 7); got != want {
			t.Errorf("versionBefore(%q, 0, 7) = %v, want %v", v, got, want)
		}
	}
}

func TestVerify_UsageErrorGoesToStderr(t *testing.T) {
	var out bytes.Buffer
	var code int
	errOut := captureStderr(t, func() {
		code = verifyCmd(context.Background(), []string{"--filter", "a", "--filter", "b"}, nil, nil, nil, 0, &out)
	})
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1", code)
	}
	if out.Len() != 0 {
		t.Errorf("stdout = %q, want empty so --json consumers keep a parseable stream", out.String())
	}
	if !strings.Contains(errOut, "duplicate flag --filter") {
		t.Errorf("stderr = %q, want the usage error", errOut)
	}
}

func TestVerify_ConnectorVersionRefusalIsExitOne(t *testing.T) {
	refusal := &connectorRefusal{message: "connector 0.6.0 is older than the minimum 0.7.0"}
	send := func(string, interface{}) (*client.CommandResponse, error) { return nil, refusal }
	var out bytes.Buffer
	code := verifyCmd(context.Background(), nil, send, send, nil, 0, &out)
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1", code)
	}
	if !strings.Contains(out.String(), "older than the minimum") {
		t.Errorf("output = %q, want the refusal message", out.String())
	}
}

func TestVerifyExit_ConnectorRefusalIsFailure(t *testing.T) {
	var out bytes.Buffer
	if code := verifyExit(&out, &connectorRefusal{message: "connector 0.6.0 is too old"}); code != verifyFailed {
		t.Errorf("exit = %d, want 1", code)
	}
	if code := verifyExit(&out, fmt.Errorf("no Unity instances")); code != verifyNoReply {
		t.Errorf("exit = %d, want 2 for a missing editor", code)
	}
}

// TestVerify_NoCompileObservedIsSaidOutLoud: an editor that was already ready is not proof that the
// requested compile ran, so verify reports that the verdict is the editor's current state instead of
// silently treating "ready" as "compiled".
func TestVerify_NoCompileObservedIsSaidOutLoud(t *testing.T) {
	compileTestBudgets(t)
	base := int64(1_000)
	f := &fakeEditor{t: t, diagnostics: clean(), tests: passing(1), testsSuccess: true,
		echoFinished: &base, finishedAt: base}

	code, out := f.run()
	if code != verifyOK {
		t.Fatalf("exit = %d, want 0; output:\n%s", code, out)
	}
	if !strings.Contains(out, "no script compilation was observed") {
		t.Errorf("output = %q, want the note that no compile was seen", out)
	}
}

// TestVerify_TruncatedDiagnosticsSaySo: error_count is the true total while errors[] is capped, so
// the report must not look like the complete list.
func TestVerify_TruncatedDiagnosticsSaySo(t *testing.T) {
	errs := []interface{}{
		map[string]interface{}{"file": "Assets/A.cs", "line": 1, "col": 1, "message": "e1"},
		map[string]interface{}{"file": "Assets/B.cs", "line": 1, "col": 1, "message": "e2"},
		map[string]interface{}{"file": "Assets/C.cs", "line": 1, "col": 1, "message": "e3"},
	}
	f := &fakeEditor{t: t, compileErrors: true, diagnostics: map[bool][]interface{}{true: errs, false: errs}}
	code, out := f.run("--max", "2")
	if code != verifyFailed {
		t.Fatalf("exit = %d, want 1", code)
	}
	if !strings.Contains(out, "compile: 3 error(s)") || strings.Count(out, "Assets/") != 2 {
		t.Errorf("output = %q, want the true total and two listed errors", out)
	}
}
