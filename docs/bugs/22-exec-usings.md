# #22 exec default usings: CS0104 and no way to trim them

## Summary

`exec` imported both `System` and `UnityEngine`, so a bare `Object`, `Random`, or
`Debug` was ambiguous (CS0104). The extra-using list was additive only, and a
`using` directive at the top of a snippet was emitted inside the generated method
where it is illegal. `exec` now emits Unity aliases for the common collisions,
hoists leading using directives (letting a snippet's own alias override the
default), and accepts `--no_default_usings` so callers can replace the whole set.

## Reported behaviour

`unity-cli exec "return typeof(Object).Name;"` and code using `Random` failed
with CS0104 even though both types were imported. There was no way to remove the
colliding namespace; `--usings` could only add more. A snippet that tried to
disambiguate with its own `using Object = System.Object;` failed to compile.

## Reproduction attempts

### 1. Released `unity-cli` v0.7.0 against the live editor: Object

```bash
cd /tmp
unity-cli --ignore-version-mismatch exec 'return typeof(Object).Name;'
unity-cli --ignore-version-mismatch exec 'Object o = null; return o == null;'
```

Output for both:

```
Error: Compile error:
L16: 'Object' is an ambiguous reference between 'UnityEngine.Object' and 'object'
```

Reproduced: yes.

### 2. Released binary: Random, Debug, Vector3

```bash
unity-cli --ignore-version-mismatch exec 'return typeof(Random).Name;'
unity-cli --ignore-version-mismatch exec 'Debug.Log("x"); return typeof(Debug).Name;'
unity-cli --ignore-version-mismatch exec 'return typeof(Vector3).FullName;'
```

Output:

```
Error: Compile error:
L16: 'Random' is an ambiguous reference between 'UnityEngine.Random' and 'System.Random'
Debug
UnityEngine.Vector3
```

Reproduced: yes for `Random`. `Debug` and `Vector3` are not ambiguous with the
default list (`System.Diagnostics` and `System.Numerics` are not imported), but
adding `System.Numerics` reproduces the same class of failure:

```bash
unity-cli --ignore-version-mismatch exec 'return typeof(Vector3).FullName;' --usings System.Numerics
```

```
Error: Compile error:
L17: 'Vector3' is an ambiguous reference between 'System.Numerics.Vector3' and 'UnityEngine.Vector3'
```

### 3. Released binary: a snippet's own using directive cannot be used

```bash
unity-cli --ignore-version-mismatch exec 'using Object = System.Object; return typeof(Object).FullName;'
```

Output:

```
Error: Compile error:
L16: Identifier expected
```

Reproduced: yes. The directive is placed in the method body by the old
`BuildSource`.

### 4. Released binary: no way to remove the defaults

```bash
unity-cli --ignore-version-mismatch exec 'return typeof(Object).FullName;' --no_default_usings --usings System
```

Output:

```
Error: Compile error:
L17: 'Object' is an ambiguous reference between 'UnityEngine.Object' and 'object'
```

Reproduced: yes. The released connector does not know `no_default_usings`, so
the old defaults remained.

### 5. Workaround confirms aliases resolve the collision

```bash
unity-cli --ignore-version-mismatch exec 'return typeof(Object).FullName;' --usings 'Object = UnityEngine.Object'
```

Output:

```
UnityEngine.Object
```

Reproduced: yes. This is the behavior the fix now makes the default.

## Root cause

- `unity-connector/Editor/Tools/ExecuteCsharp.cs:45-57` (before the fix) had
  `DefaultUsings` with both `System` and `UnityEngine`; no alias was emitted.
- `ExecuteCsharp.cs:122-131` (before the fix) appended every extra using after
  the defaults, so `--usings` could never remove a colliding namespace.
- The same method placed the submitted body inside `Execute()`, so a `using`
  directive at the top of the snippet landed inside the method.
- `HandleCommand` had no parameter that could disable the default set.

## Fix

- Added `unity-connector/Editor/Core/ExecSource.cs`, which builds the generated
  source. It emits the default imports, then the caller's directives, then
  `using Object = UnityEngine.Object;`, `using Random = UnityEngine.Random;`, and
  `using Debug = UnityEngine.Debug;` for the common collisions.
- `ExecSource.ExtractLeadingUsings` hoists leading `using X;` / `using A = B;`
  directives out of the body. `using (…)` statements and `using var …`
  declarations are left in the body. If a snippet declares an alias that the
  defaults also declare, the default alias is omitted so the snippet's win (and
  no duplicate alias error is produced).
- `ExecuteCsharp` now accepts `no_default_usings` (and `noDefaultUsings`), which
  drops both the default imports and default aliases. Combined with `--usings`
  this replaces the whole set.
- `--usings` entries may be namespaces or full directive text, with or without
  `using` and the trailing semicolon.
- CLI help (`cmd/root.go`) and `README.md` document the aliases, hoisting, and
  `--no_default_usings`.
- EditMode tests in `unity-connector/Tests/Editor/ExecSourceTests.cs` cover the
  generated text and run three snippets through the real exec compile path. A
  Go test covers the new flags reaching the connector as parameters.

## Verification

Not compiled in this worker: the worktree has no editor access, and per the task
the orchestrator compiles and runs the EditMode tests. Go checks run here:

```bash
cd /tmp/claude-1000/uc-wt
gofmt -w .
go vet ./...
go clean -testcache && go test ./...
```

`gofmt` and `go vet` were clean. `go test ./...` failed only with the known,
unrelated `cmd/update_test.go` case:

```
--- FAIL: TestFindAsset (0.00s)
    update_test.go:15: findAsset: should find asset for current platform
FAIL
FAIL	github.com/youngwoocho02/unity-cli/cmd	4.283s
ok  	github.com/youngwoocho02/unity-cli/internal/client	2.084s
```

The new Go parameter test passes on its own:

```
$ go test ./cmd -run TestBuildParams_ExecUsingsFlags -v
=== RUN   TestBuildParams_ExecUsingsFlags
--- PASS: TestBuildParams_ExecUsingsFlags (0.00s)
PASS
```

The reproduction above used the released v0.7.0 binary, which does not contain
the fix, so it verifies the failure and the alias workaround, not the new
generated source. The C# change and `ExecSourceTests` were left to the
orchestrator to compile and run, per the task's no-editor-access override.

## Remaining risk

- `Vector3` is intentionally not aliased: `System.Numerics` is not a default
  import, and aliasing it would break snippets that add `System.Numerics` on
  purpose. A snippet can write `using Vector3 = System.Numerics.Vector3;`, which
  now overrides nothing (only `Object`/`Random`/`Debug` have default aliases).
- Leading using extraction is deliberately line/trivia-oriented: it handles
  comments and directives spanning lines, but a using written after executable
  code is left in the body and will still fail to compile as it should.
- Cache keys include the generated source, so a cached snippet cannot be reused
  after a change to aliases, hoisting, or `--no_default_usings`.
