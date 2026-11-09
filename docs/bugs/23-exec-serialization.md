# #23 exec returns ToString() for anonymous and property-only types

## Summary

`exec` serialized returned values by reflecting over public instance fields only. Anonymous
types and almost all editor API types keep their data in properties, so they fell through to
`ToString()` and arrived as an opaque JSON string (or just a type name). `Serialize` now walks
public fields and readable public properties, keeps collections/Unity objects/Tasks on their
existing paths, and cuts cycles, excessive depth, and runaway member counts instead of letting
the handler fail.

## Reported behaviour

`unity-cli exec 'return new { a = 1, b = "x" };'` returned the string
`"{ a = 1, b = x }"`, and returning a `CompilationPipeline` assembly returned only
`"UnityEditor.Compilation.Assembly"`, so property access on the result was impossible.

## Reproduction attempts

All were read-only calls through the released `unity-cli` v0.7.0 against the shared editor,
which still has the pre-fix connector:

```bash
unity-cli --ignore-version-mismatch exec 'return new { a = 1, b = "x" };'
```

Output: `{ a = 1, b = x }` — a JSON string. Reproduced: yes.

```bash
unity-cli --ignore-version-mismatch exec \
  'var a = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(); return a.First(x => x.name == "UnityCliConnector.Editor");'
```

Output: `UnityEditor.Compilation.Assembly`. Reproduced: yes (the value is dropped entirely).

```bash
unity-cli --ignore-version-mismatch exec \
  'return new System.Collections.Generic.List<object>{ new { a = 1 }, new { a = 2 } };'
```

Output was `["{ a = 1 }", "{ a = 2 }"]`, showing the fallback also applies inside collections.
Reproduced: yes.

Other probes run at the same time: a property-only `KeyValuePair<string,int>` returned
`[k, 1]`; nested anonymous objects returned `{ outer = { inner = v } }`; a dictionary cycle
nested the same dictionary four times before replacing it with its type name; an
`ArrayList` cycle did the same. `Vector3` and `Dictionary<string,object>` already worked
because they have public fields / are dictionaries. `DateTime` returned its culture string,
an enum its name, and `null` returned `"OK"`. These show the hole was specifically the
property-only branch, not collections or scalar handling.

## Root cause

`unity-connector/Editor/Tools/ExecuteCsharp.cs` (`Serialize`, pre-fix lines 377-435):

- The only reflection branch was
  `type.GetFields(BindingFlags.Public | BindingFlags.Instance)`; there was no properties branch.
- Anonymous types have private `<name>k__BackingField` fields and public getters, so they had
  zero public instance fields and reached `return obj.ToString()`.
- The same applied to property-based editor API types such as
  `UnityEditor.Compilation.Assembly`, `DateTime`, and `KeyValuePair<K,V>`.
- The depth guard returned `obj.ToString()` rather than marking the cut, and there was no cycle
  guard. Collections had a 100-item cap but dictionaries did not.

## Fix

Changed `unity-connector/Editor/Tools/ExecuteCsharp.cs`:

- `Serialize` is now `internal` (plus overloads for tests) and has a safe entry point that
  catches any escaped exception, returns `ToString()` as a last resort, and reports the reason
  as `serialization_error` in the response `meta` instead of failing the exec call.
- Object serialization now includes readable public instance properties and public instance
  fields. Indexers and static getters are skipped; a getter or member that throws becomes
  `"<error: Type: message>"` without dropping the other members.
- Cycles are detected by reference identity and become `"<cycle: TypeName>"`. Depth is capped
  at 6 with `"<truncated at depth 6>"`, and members/dictionaries/enumerables are capped at 100
  entries with the existing `"... (truncated at 100)"` marker.
- `IDictionary`, `IEnumerable`, `Task`, scalar, enum, and `FixedString` handling is preserved.
  Dictionaries now also have the 100-entry cap.
- `UnityEngine.Object` keeps its existing `ToString()` identity string, avoiding a scene-graph
  walk. Unity math structs have an explicit data-member list (Vector2/3/4, Quaternion, Color,
  Rect, Bounds, Matrix4x4, and their integer/32-bit variants), so `Vector3.normalized`,
  `magnitude`, `Quaternion.eulerAngles`, `Bounds.size`, etc. are not enumerated recursively.
- `DateTime`/`DateTimeOffset`/`TimeSpan`/`Guid` stay string scalars; `System.Type` reports its
  full name.
- `ExecuteCsharp.CompileMeta` adds `serialization_error` only when the fallback was used, so
  the normal meta shape is unchanged.

Added direct EditMode tests in `unity-connector/Tests/Editor/ExecSerializeTests.cs` for every
case above, including the throwing-getter and top-level-failure paths.

## Verification

Go checks in this worktree (C# was not compiled here, per the task override):

```bash
cd /tmp/claude-1000/uc-wt
gofmt -w .
go vet ./...
go clean -testcache && go test ./...
```

Results: see the worker report. `go test ./...` is expected to fail only the known unrelated
`cmd/update_test.go` `findAsset` case.

The live reproduction above used the installed v0.7.0 connector, not the edited worktree
package, so it proves the failure, not the new output. The new `ExecSerializeTests` are pure
EditMode tests and the orchestrator must compile and run them against the editor.

## Remaining risk

- Property getters can have side effects. They are unavoidable when the data lives only in
  properties; the per-member catch limits the blast radius to a recorded `<error: ...>`.
- Unity object subclasses still serialize through `ToString()`, so callers get a human-readable
  identity rather than structured fields. This preserves the old behavior and avoids walking
  `Transform`/`GameObject` graphs.
- `Bounds`/`Rect` expose no public fields in this Unity version, so their simple data members
  (`center`/`extents`, `x`/`y`/`width`/`height`) are named explicitly instead of reflecting
  every derived property.
- Truncation markers are strings in the result shape; a consumer must not treat a dictionary
  key `"... (truncated at 100)"` as user data.

## Review follow-up

- The `ToString()` fallback was still silent: `return new object();` answered
  `data: "System.Object"` with no marker in `meta`. `Serialize` now records when a value
  exposes no serializable members and sets `serialization_error`, so the claim above that
  the marker appears "only when the fallback was used" holds for this path too
  (`ExecSerializeTests.ObjectWithoutMembers_ReportsTheToStringFallback`).
- A returned Newtonsoft `JToken` was walked as `IEnumerable<JToken>` and came back as nested
  arrays of empty `JProperty` dumps. `JToken` keeps its JSON text, as it did before the
  structural serializer (`ExecSerializeTests.JToken_SerializesAsItsJsonText`).
- The unused `Serialize(object, int)` overload was removed; the only test-facing overload
  left is the one that reports `serialization_error`.
