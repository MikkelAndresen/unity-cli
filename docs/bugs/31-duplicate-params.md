# #31 A second `--params` silently replaces the first

## Summary

`buildParams` collected every subcommand flag into a `map[string]string`, so a
repeated `--params` stored only the last JSON string. The earlier object was
never unmarshalled, silently dropping parameters such as a test filter.
Repeated `--params` objects now merge in order (later keys win), and any other
repeated flag is an error instead of a silent overwrite.

## Reported behaviour

`unity-cli test_run --params '{"mode":"edit","assembly_names":["Game.Tests.Editor"]}' --params '{"results_path":"Temp/out.xml"}'`
ran with no `assembly_names`, so all 584 EditMode tests ran. No warning was
printed.

## Reproduction attempts

### 1. Released `unity-cli` v0.7.0 against the live editor

```bash
cd /tmp
unity-cli exec --params '{"code":"return 42;"}' --params '{"await_timeout":1000}'
```

Output:

```
Error: 'code' required
```

Reproduced: yes. The first object (which carries `code`) was discarded; only
the second (`await_timeout`) reached the connector.

### 2. Freshly built `uc-ds` (before the fix) against the live editor

```bash
cd /tmp/claude-1000/uc-wt && go build -o /tmp/claude-1000/uc-ds .
cd /tmp
/tmp/claude-1000/uc-ds --ignore-version-mismatch exec \
  --params '{"code":"return 42;"}' --params '{"await_timeout":1000}'
```

Output:

```
Error: 'code' required
```

Reproduced: yes.

### 3. Fake connector: show the exact body for the reported `test_run` case

A local HTTP server on `127.0.0.1:18099` recorded each POST body, and a fake
heartbeat in a temporary `HOME` pointed the CLI at it, so no editor test run was
started.

```bash
mkdir -p /tmp/claude-1000/fake-home/.unity-cli/instances
# server: 10-line python3 http.server handler writing the body to a log,
# answering {"success":true,"data":"fake-ok"}
cat > /tmp/claude-1000/fake-home/.unity-cli/instances/fake.json <<EOF
{"state":"ready","projectPath":"/tmp/fake-project","port":18099,"pid":0,"connectorVersion":"0.7.0","timestamp":$(date +%s%3N)}
EOF

cd /tmp
HOME=/tmp/claude-1000/fake-home /tmp/claude-1000/uc-ds --ignore-version-mismatch \
  test_run --params '{"mode":"edit","assembly_names":["Game.Tests.Editor"]}' \
  --params '{"results_path":"Temp/out.xml"}'
```

Body received before the fix:

```json
{"command":"test_run","params":{"results_path":"Temp/out.xml"}}
```

Reproduced: yes. `mode` and `assembly_names` were gone.

## Root cause

`cmd/root.go:buildParams` parsed subcommand arguments into
`flags := map[string]string{}` and wrote each flag with `flags[key] = value`.
Two `--params` arguments therefore left only the last string, and
`json.Unmarshal` was called once on that last value. The same overwrite existed
in `parseSubFlags`, the string-only parser used by command-level flags (for
example `test --filter a --filter b`).

## Fix

- `cmd/root.go`: new `parseFlagArgs` collects repeated `--params` values in
  order and rejects a repeated non-`--params` flag with a clear error.
  `parseSubFlags` and `buildParams` both use it, so command-level flags and
  JSON parameters follow the same rule.
- `buildParams` unmarshals each `--params` object in order and merges the keys;
  later keys win. Invalid JSON reports which occurrence failed, for example
  `invalid JSON in --params #2`.
- `--params` remains the only repeatable flag. The previous precedence is kept:
  a key supplied through `--params` wins over the same plain flag.
- Help text (`cmd/root.go`) and `README.md` document the merge rule and the
  duplicate-flag error.
- `cmd/root_test.go`: table tests for merging independent keys, preserving the
  first object's filter, later-key precedence, duplicate plain flags, and
  invalid JSON in the first or second object. The existing `parseSubFlags`
  table now covers repeated `--params` and duplicate plain flags.

## Verification

```bash
cd /tmp/claude-1000/uc-wt
gofmt -l .          # no output
go vet ./...        # exit 0
go test ./...       # only the known cmd/update_test.go TestFindAsset failure
go build -o /tmp/claude-1000/uc-ds .
```

Live editor, after the fix:

```bash
cd /tmp
/tmp/claude-1000/uc-ds --ignore-version-mismatch exec \
  --params '{"code":"return 42;"}' --params '{"await_timeout":1000}'
```

Output:

```
42
```

Fake connector, after the fix:

```text
{"command":"test_run","params":{"assembly_names":["Game.Tests.Editor"],"mode":"edit","results_path":"Temp/out.xml"}}
```

Duplicate plain flag:

```text
$ /tmp/claude-1000/uc-ds --ignore-version-mismatch test_run --filter a --filter b
Error: duplicate flag --filter (first "a", then "b"); only --params may be repeated
exit=1
```

Invalid JSON in the second object:

```text
$ /tmp/claude-1000/uc-ds --ignore-version-mismatch test_run --params '{"ok":1}' --params '{'
Error: invalid JSON in --params #2: unexpected end of JSON input
exit=1
```

## Remaining risk

- Repeating a non-`--params` flag was previously accepted (last value won) and
  is now an error. A script that accidentally repeats a flag will start failing
  instead of silently using one value; that is the intended trade-off.
- Duplicate keys inside a single JSON object still follow Go's
  `encoding/json` rule (last key wins). Only separate `--params` objects are
  merged by the CLI.
- A key supplied both by `--params` and by a plain flag keeps the old
  `--params`-wins precedence; this fix does not add a warning for that case.

## Review follow-up

- The `--params` documentation was narrowed to the paths that actually consume it (`exec`, `build`,
  and custom tools). The false `test --params '{"assembly":…}'` example is gone; `test`, `verify`,
  `editor` and `wait` build their parameters from their own flags and do not read `--params`.
- `verify` now writes a subcommand usage error to stderr instead of stdout, so `--json` output stays
  a single parseable object (`wait` does the same for its parser errors).
- The invalid-JSON test now asserts the occurrence number (`#1`, `#2`), not just the message prefix.

## Review follow-up (end-to-end run)

- **A misspelled flag was silently ignored, not just a repeated `--params`.** `test --filtre X` ran
  all 586 tests; `compile_status --sacn false` behaved as if the flag were absent. The connector's
  router now rejects any parameter the tool's `Parameters` schema does not declare, with
  `data.code = unknown_parameter` (`ErrorCodes.UnknownParameter`, `client.CodeUnknownParameter`) and a
  message plus payload listing the parameters the tool takes — in both spellings it accepts (snake and
  camel `case`) and in any legacy alias the schema declares (`scan_budget_ms`, `count`). Reserved keys
  the CLI or connector injects are always allowed: `args`, `queue_deadline_ms`, `deadline_ms`,
  `skip_compile_check`/`skipCompileCheck`. Tools without a `Parameters` class keep accepting anything,
  and the check runs before the compile gate so a typo does not wait for a compile.
  `CommandRouterTests` covers the refusal, the known-parameter list, the reserved keys, the camelCase
  and alias spellings, the cached tool scan and the schema-less case. Live: `exec … --bogus`,
  `api Vector3 --bogus` and `compile_status --sacn false` each answer exit 1 with
  `known parameters: …`. The CLI's own flag parsing (which flags a Go subcommand accepts at all) is
  the Go half and stays with `--params` handling.
