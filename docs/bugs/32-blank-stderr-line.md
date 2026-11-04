# #32 `exec` leaves a lone blank line on stderr, so `2>&1 | tail -1` prints nothing

## Summary

The blank line came from the CLI's old automatic update notice, which began with
a bare `\n` on stderr (`printNotice`, `cmd/version_check.go:112-114` before
`c9f416c`). The `unity-cli` PATH wrapper filters the two notice text lines with
`grep -v`, but that pattern never matched the leading blank line, so the blank
line survived and became the last line of `2>&1`. The notice was removed in
`c9f416c` (contained in v0.5.0), and the currently installed v0.7.0 and the
current tree write nothing to stderr for a successful `exec`. No repo code path
currently emits a blank stderr line. As a robustness improvement, stdout now
ends with exactly one newline, so a result that already ends in `\n` no longer
produces a blank final line either.

## Reported behaviour

`unity-cli exec 'return "hello";'` reportedly wrote `hello\n` to stdout and a
lone `\n` to stderr. Ten samples of `unity-cli exec 'return "hello";' 2>&1 |
tail -1` looked as if the editor had answered nothing.

## Reproduction attempts

### 1. Released binary directly (a)

```bash
/usr/local/bin/unity-cli --project "$AGENT_SANDBOX_PROJECT" \
  --ignore-version-mismatch exec 'return "hello";' >out.a 2>err.a
od -c out.a; od -c err.a; wc -c out.a err.a
```

```
0000000   h   e   l   l   o  \n
0000006
0000000
6 out.a
0 err.a
```

Reproduced: no. stderr is empty.

### 2. Through the PATH wrapper (b)

The wrapper (`~/.local/bin/unity-cli`) is:

```bash
#!/usr/bin/env bash
exec 2> >(grep --line-buffered -v -e '^Update available: ' -e '^Run "unity-cli update" to upgrade\.$' >&2)
# ... adds --ignore-version-mismatch and --project, then
exec /usr/local/bin/unity-cli "${args[@]}" "$@"
```

Same capture as (a): stdout `hello\n`, stderr 0 bytes. The issue's exact shape,
repeated 20 times, always ended with `h e l l o \n` (no blank, no reordering
observed):

```bash
for i in $(seq 1 20); do unity-cli exec 'return "hello";' 2>&1 | tail -1 | od -c | head -1; done
```

Reproduced: no.

To prove the wrapper is still the thing that would keep an old notice's blank
line, the old notice text was piped through the same filter:

```bash
printf '\nUpdate available: v0.7.0 → v0.8.0\nRun "unity-cli update" to upgrade.\n' \
  | grep --line-buffered -v -e '^Update available: ' -e '^Run "unity-cli update" to upgrade\.$' | od -c
# 0000000  \n
```

The blank line passes through; the two text lines do not. Without the leading
`\n` the same pipeline emits 0 bytes.

### 3. Freshly built `uc-ds` from this worktree (c)

```bash
cd /tmp/claude-1000/uc-wt && go build -o /tmp/claude-1000/uc-ds .
/tmp/claude-1000/uc-ds --project "$AGENT_SANDBOX_PROJECT" \
  --ignore-version-mismatch exec 'return "hello";' >out.c 2>err.c
```

stdout `hello\n`, stderr 0 bytes. Ten runs of `2>&1 | tail -1` all ended with
`hello\n`. Reproduced: no. (One unrelated transient run showed the tail of the
editor-startup evidence report, `Next: run 'unity-cli dialogs' ...`, because the
shared editor was briefly busy; that is stderr on a successful call but not a
blank line, see Remaining risk.)

### 4. Error paths and result shapes (d)

Built `uc-ds` before the stdout change:

| Case | exit | stdout | stderr |
| --- | --- | --- | --- |
| `exec 'return undefinedSymbol123;'` | 1 | empty | `Error: Compile error:\nL16: ...\n` |
| `frobnicate` (unknown command) | 1 | empty | `Error: Unknown command: frobnicate\n` |
| `exec 'return null;'` | 0 | `OK\n` | empty |
| `exec 'return "";'` | 0 | `\n` | empty |
| `exec 'return "line1\nline2\n";'` | 0 | `line1\nline2\n\n` | empty |
| `exec 'return 42;'` | 0 | `42\n` | empty |
| `--meta exec 'return "hello";'` | 0 | `hello\n` | `meta: {...}\n` |

No error or success path writes a blank stderr line. The only defect visible
here is the double trailing newline on a multi-line result that already ends in
`\n`, which makes `2>&1 | tail -1` blank (on stdout, not stderr).

### 5. Source search for stderr writes in the exec path (e)

```bash
grep -rn "os.Stderr\|fmt.Fprint.*Stderr\|log\.Print\|log\.SetOutput\|Fprintln(os.Stderr" \
  --include=*.go cmd/ internal/ main.go | grep -v _test.go
```

Relevant hits: `main.go:18` (error only), `cmd/root.go:37` (flag parse error),
`cmd/root.go:221` (`--meta`, opt-in), `cmd/root.go:231,233` (failed response),
`cmd/status.go:26` (stalled-editor report), `cmd/status.go:137,147` (`Waiting
for Unity...` / `Unity is ready.` while polling), `cmd/test.go:82` (PlayMode
progress). None writes a bare or leading newline. The automatic update notice
and its `printNotice` leading `\n` are gone from the current tree, and
`git grep printNotice v0.7.0` finds nothing.

## Root cause

`cmd/version_check.go:112-114` at `c9f416c^`:

```go
func printNotice(current, latest string) {
	fmt.Fprintf(os.Stderr, "\nUpdate available: %s → %s\nRun \"unity-cli update\" to upgrade.\n", current, latest)
}
```

`printUpdateNotice()` ran after every command. The wrapper filters only lines
starting with `Update available: ` and the exact `Run "unity-cli update" to
upgrade.` line, so the leading blank line remained as the sole stderr output.
`c9f416c` removed the notice (`git tag --contains c9f416c` -> v0.5.0 and later);
`v0.7.0` contains the removal. The reported session therefore used a binary
built before that removal (or a stale install), not current source.

## Fix

- The historical cause is already fixed; no code path writes a blank stderr
  line, so no stderr suppression was added.
- Robustness change in `cmd/root.go`: `printResponse` now delegates to
  `printResponseTo(io.Writer, resp)`, which prints every result through
  `oneTrailingNewline`. stdout therefore ends with exactly one newline even
  when the returned string already ends in one (or several), so
  `2>&1 | tail -1` shows the result instead of a blank line. `cmd/root_test.go`
  covers plain, multi-line with/without trailing newline, multiple trailing
  newlines, number, object, null/message and empty-string results.

## Verification

```bash
cd /tmp/claude-1000/uc-wt
gofmt -l .            # no output
go vet ./...          # clean
go test ./...         # all packages pass except the known cmd/update_test.go
                      # findAsset linux/arm64 failure
```

Live, against the shared editor, after rebuilding `/tmp/claude-1000/uc-ds`:

```bash
/tmp/claude-1000/uc-ds --project "$AGENT_SANDBOX_PROJECT" --ignore-version-mismatch \
  exec 'return "line1\nline2\n";'
# stdout: l i n e 1 \n l i n e 2 \n   (exactly one trailing newline)
# stderr: empty
```

Repeated `2>&1 | tail -1` gives `line2\n` 5/5 times. `return ""` still prints a
single `\n` (the result itself is empty), errors still go only to stderr, and
`--meta` still writes its one `meta: ...` line to stderr when requested.

## Remaining risk

- An old binary before v0.5.0 would still print the blank line; only reinstalling
  or `unity-cli update` fixes that. The wrapper was not edited, per the task.
- A successful call can still write non-blank lines to stderr while the editor is
  starting (`Waiting for Unity...`, `Unity is ready.`) or unreachable (the
  startup/evidence report), and `--meta` adds one line. Any of those, not a blank
  line, can still displace the result under `2>&1 | tail -1`. This is separate
  from #32 and was not changed.
- An empty-string result still prints one newline, so `tail -1` is blank for that
  specific result; there is no non-empty final line to print.
