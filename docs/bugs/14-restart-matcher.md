# #14 — `unity-restart` refuses a running editor: matcher sees `AssetImportWorker`, not the main editor

## Summary

The refusal is produced by the agent-sandbox host daemon, not by this repo: the shim sends only
`project_path` and the daemon resolves the editor pid itself. The live process tree shows why a
project-path matcher is blind to the main editor: the `AssetImportWorker` child carries
`-projectPath <project>` (capital `P`) while the Hub-launched editor carries `-projectpath`
(lowercase), or no project argument at all. The worker also loads the connector's
`[InitializeOnLoad]` classes and binds the connector's HTTP port, so this repo had a real (if
secondary) worker bug: the connector now skips initialization in asset import workers. No Go-side
change was needed — `DiscoverInstance`/`ScanInstances` read the heartbeat, never process argv.

## Reported behaviour

`unity-restart` refused a running editor with a `refused_no_unity_running` detail saying Unity was
"running but not for this project", even though the editor for that project was open (pid 97093 in
the report). `--force` then looked like the only way forward.

## Reproduction attempts

The hard constraint for this task was: do not run `unity-restart`, and do not run `unity-wait` with
anything but `--help`. So the refusal itself was not re-triggered. Instead I read the shims and
reconstructed the matcher from the live process tree.

1. **Read the clients.** `cat /usr/local/bin/unity-restart /usr/local/bin/unity-wait` — reproduced
   the request/response contract, not the refusal. Yes for the contract, no for the refusal.

   - restart request: `{"type":"restart","project_path":...,"reason":...,"force":...,"timeout":...}`
     — no pid, no process name, no argv. The daemon chooses how to find the editor.
   - wait request: `{"type":"status","project_path":...}`; the reply may carry `status`, `running`,
     `pid`, `port`, `version`, `connector_state`, `compile_errors`.
   - restart refusal codes handled by the shim: `refused_cooldown`, `refused_no_progress`,
     `refused_no_unity_running`, plus `error`, `dispatching`, `restarted`. The "running but not for
     this project" text is the daemon's `detail`, not a separate code.

2. **Read the skill/agent docs.**
   `cat /Users/mikkelandresen/Work/agent-sandbox/skills/unity-restart/SKILL.md` and
   `/Users/mikkelandresen/Work/agent-sandbox/agents/unity-restart.md`. The skill says the default
   restart "reads `~/.unity-cli/instances/` to find the editor pid for this project", which is the
   right source; the observed refusal shows the running daemon also (or instead) scans process
   command lines, because a heartbeat-only matcher could not have missed the heartbeat that was on
   disk. Read-only; does not reproduce the refusal.

3. **Checked the heartbeat and the editor answering on it.** Read-only.
   ```
   cat ~/.unity-cli/instances/a7cab838d3b4330d.json
   # {"state":"ready","projectPath":"/Users/mikkelandresen/Work/unity-cli-project",
   #  "port":8091,"pid":65279,...}
   unity-cli status --project "$AGENT_SANDBOX_PROJECT" --ignore-version-mismatch
   # Unity (port 8091): ready ... PID: 65279
   ```
   Then, via a harmless `exec` on the editor:
   ```
   /tmp/claude-1000/uc-ds --port 8091 --ignore-version-mismatch exec \
     'return new { pid = Process.GetCurrentProcess().Id,
                  isWorker = AssetDatabase.IsAssetImportWorkerProcess(),
                  port = UnityCliConnector.HttpServer.Port,
                  batch = Application.isBatchMode };'
   # { pid = 65279, isWorker = False, port = 8091, batch = False }
   ```
   So the heartbeat pid *is* the main editor, and the heartbeat is unambiguous. Yes for "heartbeat
   attributes the editor correctly", no for the refusal.

4. **Listed the live Unity process tree.** Read-only `exec` of `/bin/ps -axo pid=,ppid=,args=` on
   the host, filtered to Unity. The relevant rows (trimmed):
   ```
   65279 36701 .../MacOS/Unity -projectpath /Users/mikkelandresen/Work/unity-cli-project \
         -acceptSoftwareTermsForThisRunOnly -useHub -hubIPC ...
   30244 65279 .../MacOS/Unity -adb2 -batchMode -noUpm -name AssetImportWorkerHW7 \
         -projectPath /Users/mikkelandresen/Work/unity-cli-project \
         -logFile Logs/AssetImportWorkerHW7.log -srvPort 55043 -parentPid 65279 ...
   ```
   Both processes match `/Users/mikkelandresen/Work/unity-cli-project`, but with different argv
   spelling: the editor has lowercase `-projectpath` (Hub launch), the worker has exact
   `-projectPath` plus `-batchMode`, `-name AssetImportWorkerHW7` and `-parentPid 65279`. A
   case-sensitive `-projectPath` matcher therefore sees only the worker. Yes — this is the mechanism
   the issue hypothesised, observed live.

5. **Checked whether the worker runs the connector.** Read-only `exec` of
   `/usr/sbin/lsof -nP -a -p 30244 -iTCP`:
   ```
   Unity 30244 ... TCP 127.0.0.1:8090 (LISTEN)
   Unity 30244 ... TCP 127.0.0.1:56244 (LISTEN)
   ```
   The worker is listening on the connector's default port 8090 while the editor heartbeat says
   8091. That means `HttpServer`'s `[InitializeOnLoad]` constructor ran inside the
   `AssetImportWorker` (nothing else in the project binds 8090). No — this is a second bug, found
   while investigating.

6. **Checked whether the worker overwrites the heartbeat.** Polled the instance file every 100 ms
   for 6 s while worker 30244 was alive:
   ```
   f=~/.unity-cli/instances/a7cab838d3b4330d.json
   for ... 60 times: cat "$f" | jq -r '[.pid,.port,.state]|@tsv'; sleep 0.1
   # 44 samples, all "65279 8091 ready"
   ```
   The worker did not write in steady state, most likely because `Heartbeat.Tick` is driven by
   `EditorApplication.update`, which an import worker does not pump. It can still write on the event
   paths (`MarkStopped` from `EditorApplication.quitting`), so the guard below is still warranted.
   Partial: steady-state overwrite not reproduced; event-path overwrite not exercised because it
   would require stopping the shared editor.

Not reproducible from this container: the daemon source itself. The daemon is
`/Users/mikkelandresen/.local/bin/unity-restart-daemon.py` (seen via `ps`), outside the only mounted
host path (`/Users/mikkelandresen/Work`), so the matcher code could not be read. The refusal was not
re-triggered because `unity-restart` is forbidden here.

## Root cause

- **Owner of the refusal (out of this repo):** the host daemon resolves "the editor for this
  project" by scanning process arguments. On macOS a Hub-launched editor has `-projectpath`
  (lowercase), or no project argument at all, while every `AssetImportWorker` child has
  `-projectPath` (capital `P`) and `-parentPid <editor pid>`. A case-sensitive search for
  `-projectPath`, or any matcher that requires the main editor's argv to name the project, sees the
  worker but not the editor. That produces the "running, but not for this project" refusal.
- **This repo's contribution:** the `[InitializeOnLoad]` constructors in
  `unity-connector/Editor/HttpServer.cs:49` and `unity-connector/Editor/Heartbeat.cs:41` had no
  `AssetDatabase.IsAssetImportWorkerProcess()` guard, so an import worker loaded the whole
  connector. Live evidence: worker 30244 held `127.0.0.1:8090` while the main editor was on 8091.
  `BuildJobStore` had the same problem with a worse consequence: `BuildJobStore.Load`
  (`unity-connector/Editor/BuildJobs/BuildJobStore.cs:108-126`) marks any still-running job
  "interrupted" and saves, so a worker starting mid-build could rewrite the editor's build record.
- **Not a bug here:** `internal/client/client.go:141-155` (`ScanInstances`) and
  `internal/client/client.go:206-256` (`DiscoverInstance`) attribute instances by the heartbeat's
  `projectPath`/`pid`/`port`, never by process argv. There is no `-projectPath` matcher in the Go
  CLI to be confused by workers.

## Fix

Changed in this worktree (C# connector, compile verification left to the orchestrator because the
shared editor must not be refreshed from this worker):

- `unity-connector/Editor/HttpServer.cs` — return from the `[InitializeOnLoad]` constructor in an
  asset import worker, so a worker no longer binds the connector port.
- `unity-connector/Editor/Heartbeat.cs` — return from the constructor in a worker, so no worker
  code path can publish a heartbeat with the worker's pid for the editor's project.
- `unity-connector/Editor/Modal/ModalWatchdog.cs` — no watchdog timer in a worker.
- `unity-connector/Editor/BuildJobs/BuildJobStore.cs` — no load/mark-interrupted in a worker.

No Go change: the discriminating information the daemon needs is already in the heartbeat
(`projectPath`, `pid`, `port`), and the Go CLI already reads it that way.

### Recommended host-daemon patch (out of this repo)

Prefer the heartbeat as the primary attribution source, then fall back to a worker-aware process
scan. Pseudo-code:

```python
def project_matches(candidate, project):
    return os.path.realpath(candidate) == os.path.realpath(project)

def is_worker(argv):
    a = [x.lower() for x in argv]
    return "-batchmode" in a or any(
        x == "-name" and i + 1 < len(a) and a[i + 1].startswith("assetimportworker")
        for i, x in enumerate(a))

def find_editor(project):
    # 1. Heartbeat: written by the connector inside the main editor, pid is authoritative.
    for path in glob(os.path.expanduser("~/.unity-cli/instances/*.json")):
        hb = json.load(open(path))
        if not project_matches(hb.get("projectPath", ""), project):
            continue
        if hb.get("state") == "stopped" or not hb.get("pid"):
            continue
        pid = hb["pid"]
        argv = ps_argv(pid)          # exact argv, not a substring of the table
        if not argv or "unity" not in basename(argv[0]).lower():
            continue                 # pid was reused by something else
        if is_worker(argv):
            continue
        return pid, hb.get("port")

    # 2. Fallback: scan processes, but exclude workers and match -projectPath case-insensitively.
    for proc in ps_all():            # pid, ppid, argv
        if "unity" not in basename(proc.argv[0]).lower() or is_worker(proc.argv):
            continue
        if project_matches(arg_value(proc.argv, "-projectpath"), project):
            return proc.pid, None

    # 3. Last resort for a worker-only match: walk -parentPid / ppid up to the Unity editor.
    for proc in ps_all():
        if is_worker(proc.argv) and project_matches(arg_value(proc.argv, "-projectpath"), project):
            parent = int(arg_value(proc.argv, "-parentpid") or proc.ppid)
            if is_unity_editor(parent):
                return parent, None
    return None
```

Two additional behaviours make future refusals diagnosable, as the issue asks:

- On `refused_no_unity_running`, report the evidence: every matching pid with its argv excerpt,
  the worker count and their `-parentPid`s, the heartbeat file(s) read, and the project path each
  one implies.
- Treat `-projectpath`/`-projectPath` and any other Unity argument case-insensitively; Unity's own
  launchers are not consistent about argument casing.

## Verification

- `gofmt -l .` — no output.
- `go vet ./...` — exit 0.
- `go test ./...` — only the known pre-existing `cmd/update_test.go` failure
  (`findAsset: should find asset for current platform`) fails; everything else passes.
- C#: **not compiled here.** `unity-cli verify` was forbidden for this worker because the shared
  editor is in use, and there is no standalone Unity compiler in the container. The four edited files
  are in this worktree only; the orchestrator must run
  `unity-cli verify --assembly UnityCliConnector.Tests.Editor`.
- Real behaviour checked without restarting: heartbeat pid 65279 == editor pid answering on port
  8091; worker 30244 observed with `-projectPath`/`-batchMode`/`-parentPid` and LISTEN on 8090;
  `ps`/`lsof` were run read-only through `exec` on the live editor. The refusal itself was not
  reproduced (running `unity-restart` was forbidden).

## Remaining risk

- The host-daemon patch above is a recommendation only; until it lands, `unity-restart` can still
  refuse a Hub-launched editor. This repo cannot fix that.
- The worker guard is unverified by a compiler in this worktree. It assumes
  `AssetDatabase.IsAssetImportWorkerProcess()` is callable from `[InitializeOnLoad]` constructors,
  which is how package code normally uses it; Unity 6000 is the package's minimum (`package.json`).
- Related observation for #28: worker 30244 was holding `127.0.0.1:8090` while the main editor
  heartbeat reported 8091. A worker grabbing the default port after a main-editor reload explains
  the "default port moves to 8091/8092" drift seen there; the `HttpServer` guard should stop that.
- If a stale worker from an older connector is already running when the new package loads, it keeps
  its port until that worker exits; the guard only applies to workers started with the new assembly.
- `scan`/`judgePID` in the Go CLI identifies Unity by executable base name, so an import worker's pid
  would also pass as "Unity alive". That is harmless while the heartbeat pid is the editor's, and it
  is what the new `Heartbeat` guard guarantees.
