# #11 Exact CLI/connector version equality blocks every call

## Summary

The CLI compared its release version with the connector's heartbeat version for
exact equality, both before dispatch and again inside every send. Any one-sided
upgrade disabled every command, including the read-only diagnostics needed to
find and fix the mismatch. The gate now accepts a compatibility window: patch
drift is silent, minor/major drift proceeds with one stderr warning, diagnostic
commands are never gated, and only a connector older than the minimum supported
protocol (0.7.0) is refused. `status` always prints both versions and a verdict.

## Reported behaviour

With the connector at 0.5.0 and the CLI at 0.4.0, every call failed until
`--ignore-version-mismatch` was added. Later the CLI appeared to change to 0.5.1
mid-session without an update step, so the same command could flip from working
to failing. The install has two entry points (`~/.local/bin/unity-cli` wrapper
and `/usr/local/bin/unity-cli`), and only the wrapper injects the ignore flag.

## Reproduction attempts

### 1. Real editor, CLI built as 0.7.1 against connector 0.7.0

The version is set through `main.Version`, not `cmd.Version`: `main.init` copies
it into `cmd.Version`, so overriding `cmd.Version` alone is overwritten.

```bash
cd /tmp/claude-1000/uc-wt
go build -ldflags "-X main.Version=0.7.1" -o /tmp/claude-1000/uc-ds .
/tmp/claude-1000/uc-ds status; echo "exit=$?"
/tmp/claude-1000/uc-ds exec 'return 1+1;'; echo "exit=$?"
/tmp/claude-1000/uc-ds console --lines 1; echo "exit=$?"
/tmp/claude-1000/uc-ds modal; echo "exit=$?"
```

All four printed the same failure and exited 1:

```
Error: connector version mismatch: unity-cli 0.7.1, connector 0.7.0. Update both to the same release, or rerun with --ignore-version-mismatch
```

`status` printed its normal state lines first, then the same error, so even the
diagnostic view could not say "compatible patch drift".

Reproduced: yes.

### 2. Fake heartbeat, connector 0.6.0

A copy of the live heartbeat in a temporary `HOME`, with `connectorVersion`
changed to 0.6.0 and a fresh timestamp, exercised `status` without touching the
editor:

```bash
FAKE=$(mktemp -d)
mkdir -p "$FAKE/.unity-cli/instances"
# copy a live heartbeat to "$FAKE/.unity-cli/instances/fake.json",
# set connectorVersion="0.6.0", port=8099, timestamp=now
HOME="$FAKE" /tmp/claude-1000/uc-ds status --port 8099; echo "exit=$?"
```

```
Unity (port 8099): playing
  Project: /Users/mikkelandresen/Work/unity-cli-project
  Version: 6000.6.5f1
  Connector: 0.6.0
  PID:     65279
Error: connector version mismatch: unity-cli 0.7.1, connector 0.6.0. Update both to the same release, or rerun with --ignore-version-mismatch
exit=1
```

Reproduced: yes. The same run also exposed that an empty/unparseable connector
version was a hard error.

### 3. Control: released v0.7.0 CLI

`/usr/local/bin/unity-cli status` against connector 0.7.0 succeeded; the bug
requires only patch-level drift, it is not a general editor failure.

Reproduced: yes, as the control case.

## Root cause

At `c39c5b1`:

- `cmd/status.go:93-112` — `checkConnectorVersion` normalized both strings and
  returned an error whenever they were not exactly equal; an empty connector
  version was also an error.
- `cmd/root.go:111-113` — every non-diagnostic command called that check before
  dispatch.
- `cmd/root.go:117-126` — the send closure called it again before every request,
  so a connector heartbeat that changed during a session could start failing a
  command that had already passed the first check.
- `cmd/root.go:137-142` — the test path had its own copy of the check.

There was no compatibility window, no exemption for read-only commands, and no
way to see both versions while diagnosing the mismatch.

## Fix

Go side only (`cmd/status.go`, `cmd/root.go`):

- `assessConnectorVersion` is now the single rule:
  - development CLI: not checked;
  - same major.minor: compatible and silent (patch drift);
  - different minor or major: compatible, with one warning line naming both
    versions, the executable path, the connector package name, `unity-cli
    update`, and `--ignore-version-mismatch`;
  - unknown/unparseable connector version: proceed with a warning;
  - connector older than `minConnectorVersion` (0.7.0): refuse unless
    `--ignore-version-mismatch`.
- `versionExemptCommands` skips both the warning and the refusal for `status`,
  `console`, `compile_diagnostics`, `compile_status`, `modal`, `tools`,
  `tool_describe`, `wait`, `dialogs`, `list`, `help`, `version`, `update`.
- `versionGate` is created once per command invocation and remembers the last
  warned version pair, so the pre-dispatch check and the per-send checks print
  at most one warning for the same pair. A connector that changes mid-session
  gets one new warning, not one per send.
- `status` prints `CLI`, `Connector`, and `Compatibility` for live and quiet
  heartbeats. It never returns a version error.

`minConnectorVersion = "0.7.0"` is justified in a comment: 0.7.0 is the first
connector release with the command set and heartbeat fields (`compileFinishedAt`,
`modal`, `build`) that the current CLI commands send or read.

## Verification

After the fix, against the real editor (connector 0.7.0):

```bash
go build -ldflags "-X main.Version=0.7.1" -o /tmp/claude-1000/uc-ds .
/tmp/claude-1000/uc-ds status
/tmp/claude-1000/uc-ds exec 'return 1+1;'
/tmp/claude-1000/uc-ds console --lines 1
/tmp/claude-1000/uc-ds modal
```

```
Unity (port 8091): playing
  Project: /Users/mikkelandresen/Work/unity-cli-project
  Version: 6000.6.5f1
  CLI:     0.7.1
  Connector: 0.7.0
  Compatibility: compatible (same major.minor 0.7)
  PID:     65279
2
```

`status`, `exec`, `console` and `modal` all exited 0 with empty stderr.

CLI built as 0.8.0, same connector:

```bash
go build -ldflags "-X main.Version=0.8.0" -o /tmp/claude-1000/uc-ds-080 .
/tmp/claude-1000/uc-ds-080 status
/tmp/claude-1000/uc-ds-080 exec 'return 1+1;'
```

```
  CLI:     0.8.0
  Connector: 0.7.0
  Compatibility: compatible with a warning (CLI and connector differ in minor version)
2
```

`exec` stdout was `2` on stdout and exactly one warning line on stderr:

```
warning: unity-cli 0.8.0 (/tmp/claude-1000/uc-ds-080) and connector 0.7.0: they differ in minor version. Run `unity-cli update` for the CLI and update the com.youngwoocho02.unity-cli-connector package in Unity; rerun with --ignore-version-mismatch to hide this warning.
```

`console` and `wait` under 0.8.0 also exited 0 with empty stderr (diagnostic
exemption). A fake heartbeat with connector 0.6.0 now makes `status` exit 0 and
show:

```
  Compatibility: connector 0.6.0 is older than the minimum 0.7.0; update the connector package
```

Checks:

```bash
gofmt -l .                 # clean
go vet ./...               # clean
go test ./... -count=1     # only the known TestFindAsset update_test failure
```

Added table tests for `normalizeVersion`, `parseVersion`,
`assessConnectorVersion`, every exempt command, silent patch drift, one-line
warning deduplication, and `--ignore-version-mismatch` suppression.

## Remaining risk

- The heartbeat has no connector package path, so the warning names the package
  (`com.youngwoocho02.unity-cli-connector`) rather than an on-disk path.
- `minConnectorVersion` is a protocol claim, not an observed handshake. It is
  set to 0.7.0 per the oldest protocol the current commands are built around;
  moving it down requires checking every newer heartbeat field and tool.

## Review follow-up

- `api` and `burst_status` joined the diagnostic exemptions, so the read-only tools that explain
  what the loaded assemblies/Burst are doing still answer under version drift.
- A connector below the minimum is still refused for `verify`, but that refusal now exits 1 with its
  message (a failure), not 2 (the editor could not answer). The same mapping applies if the refusal
  arrives mid-run from a send.
- `README.ko.md` was not updated in this stack; Korean docs are deferred.

## Review follow-up (round 2)

- `versionExemptCommands` no longer lists commands that never reach the gate (`status`, `dialogs`,
  `help`, `version`, `update`). `tool_describe` stays: it is a connector tool that the CLI's default
  dispatch sends like a command, and it is part of the same diagnosis. The
  `connectorCompatibility.Compatible` field is gone: "compatible" is exactly "no refusal", so the
  two can no longer drift apart.
- The exemption test now exercises every entry's behaviour (it must answer a refused connector)
  instead of restating the map, and a separate test pins the state-changing commands as gated.
