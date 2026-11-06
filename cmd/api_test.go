package cmd

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// apiReplyJSON builds an api reply. exact is left out entirely when withExact is false, which is how
// a connector older than the exact-match field answers.
func apiReplyJSON(t *testing.T, withExact, exact bool, payload string) json.RawMessage {
	t.Helper()
	body := payload
	if withExact {
		exactness := "false"
		if exact {
			exactness = "true"
		}
		if body == "" {
			body = `{"exact":` + exactness + `}`
		} else {
			body = `{"exact":` + exactness + `,` + strings.TrimPrefix(strings.TrimSpace(body), "{")
		}
	}
	var out bytes.Buffer
	if err := json.Compact(&out, []byte(body)); err != nil {
		t.Fatalf("bad test payload: %v", err)
	}
	return out.Bytes()
}

func TestAPINoExactMatchIsNotSuccess(t *testing.T) {
	reply := &client.CommandResponse{
		Success: true,
		Message: "Nothing named 'NoSuchTypeXyz' is in the loaded assemblies.",
		Data: apiReplyJSON(t, true, false, `{
			"query": "NoSuchTypeXyz", "member": null, "matched_by": "closest_types",
			"matches": 0, "types": []
		}`),
	}
	var out bytes.Buffer
	got, err := apiCmd([]string{"NoSuchTypeXyz"}, func(string, interface{}) (*client.CommandResponse, error) {
		return reply, nil
	}, &out)
	if err != nil {
		t.Fatalf("apiCmd: %v", err)
	}
	if got.Success {
		t.Error("resp.Success = true, want the exit-1 failure applyAPIVerdict produces for no match")
	}
	if got.Message != reply.Message {
		t.Errorf("message = %q, want the connector's own %q", got.Message, reply.Message)
	}
	if out.Len() != 0 {
		t.Errorf("stdout = %q, want nothing before the error", out.String())
	}
}

// TestAPIFuzzyMatchPrintsTheConnectorNote: the connector's own sentence is what gets printed, since
// it names the missing member and its suggestions without repeating every overload.
func TestAPIFuzzyMatchPrintsTheConnectorNote(t *testing.T) {
	note := "no member named 'Lerpp' on UnityEngine.Vector3, System.Numerics.Vector3; closest: Lerp, Slerp"
	reply := &client.CommandResponse{
		Success: true,
		Message: note,
		Data: apiReplyJSON(t, true, false, `{
			"query": "Vector3.Lerpp", "member": "Lerpp", "matched_by": "type.member", "matches": 2,
			"note": "`+note+`",
			"types": [{"type": "UnityEngine.Vector3", "members": [{"name": "Lerp"}, {"name": "Slerp"}]}]
		}`),
	}
	var out bytes.Buffer
	got, err := apiCmd([]string{"Vector3.Lerpp"}, func(string, interface{}) (*client.CommandResponse, error) {
		return reply, nil
	}, &out)
	if err != nil {
		t.Fatalf("apiCmd: %v", err)
	}
	if !got.Success {
		t.Error("resp.Success = false, want a fuzzy result to answer with the closest matches and exit 0")
	}
	first := strings.SplitN(out.String(), "\n", 2)[0]
	if first != note {
		t.Errorf("first line = %q, want the connector's note %q", first, note)
	}
}

// TestAPIFuzzyMatchWithoutNoteSynthesizesTheClosestNames covers a reply that marks itself inexact but
// carries no sentence: the CLI still says there was no exact match before printing the answer.
func TestAPIFuzzyMatchWithoutNoteSynthesizesTheClosestNames(t *testing.T) {
	reply := &client.CommandResponse{
		Success: true,
		Data: apiReplyJSON(t, true, false, `{
			"query": "Vector3.Lerpp", "member": "Lerpp", "matched_by": "type.member", "matches": 1,
			"types": [{
				"type": "UnityEngine.Vector3",
				"members": [{"name": "Lerp"}, {"name": "Slerp"}, {"name": "Lerp"}]
			}]
		}`),
	}
	var out bytes.Buffer
	got, err := apiCmd([]string{"Vector3.Lerpp"}, func(string, interface{}) (*client.CommandResponse, error) {
		return reply, nil
	}, &out)
	if err != nil {
		t.Fatalf("apiCmd: %v", err)
	}
	if !got.Success {
		t.Error("resp.Success = false, want a fuzzy result to answer with the closest matches and exit 0")
	}
	first := strings.SplitN(out.String(), "\n", 2)[0]
	want := "no exact match; closest: UnityEngine.Vector3.Lerp, UnityEngine.Vector3.Slerp"
	if first != want {
		t.Errorf("first line = %q, want %q (overloads must not repeat)", first, want)
	}
}

func TestAPIClosestTypesAreNamedAndCapped(t *testing.T) {
	reply := &client.CommandResponse{
		Success: true,
		Data: apiReplyJSON(t, true, false, `{
			"query": "Vectr", "member": null, "matched_by": "closest_types", "matches": 7,
			"types": [
				{"type": "UnityEngine.Vector2", "members": []},
				{"type": "UnityEngine.Vector3", "members": []},
				{"type": "UnityEngine.Vector4", "members": []},
				{"type": "UnityEngine.Vector2Int", "members": []},
				{"type": "UnityEngine.Vector3Int", "members": []},
				{"type": "UnityEngine.Vector2d", "members": []},
				{"type": "UnityEngine.Vector3d", "members": []}
			]
		}`),
	}
	var out bytes.Buffer
	if _, err := apiCmd([]string{"Vectr"}, func(string, interface{}) (*client.CommandResponse, error) {
		return reply, nil
	}, &out); err != nil {
		t.Fatalf("apiCmd: %v", err)
	}
	line := strings.SplitN(out.String(), "\n", 2)[0]
	if !strings.Contains(line, "UnityEngine.Vector2, UnityEngine.Vector3") {
		t.Errorf("closest line = %q, want the type names", line)
	}
	if !strings.Contains(line, "(+2 more)") {
		t.Errorf("closest line = %q, want the overflow count instead of a full list", line)
	}
}

func TestAPIExactMatchAddsNoNote(t *testing.T) {
	reply := &client.CommandResponse{
		Success: true,
		Data: apiReplyJSON(t, true, true, `{
			"query": "Vector3.Lerp", "member": "Lerp", "matched_by": "type.member", "matches": 1,
			"types": [{"type": "UnityEngine.Vector3", "members": [{"name": "Lerp"}]}]
		}`),
	}
	var out bytes.Buffer
	got, err := apiCmd([]string{"Vector3.Lerp"}, func(string, interface{}) (*client.CommandResponse, error) {
		return reply, nil
	}, &out)
	if err != nil || got == nil || !got.Success {
		t.Fatalf("apiCmd = %+v, %v; want the exact answer untouched", got, err)
	}
	if out.Len() != 0 {
		t.Errorf("stdout = %q, want no note for an exact match", out.String())
	}
}

func TestAPIOldConnectorWithoutExactFieldKeepsItsAnswer(t *testing.T) {
	reply := &client.CommandResponse{
		Success: true,
		Data: apiReplyJSON(t, false, false, `{
			"query": "Vector3.Lerpp", "member": "Lerpp", "matched_by": "type.member", "matches": 1,
			"types": [{"type": "UnityEngine.Vector3", "members": [{"name": "Lerp"}]}]
		}`),
	}
	var out bytes.Buffer
	got, err := apiCmd([]string{"Vector3.Lerpp"}, func(string, interface{}) (*client.CommandResponse, error) {
		return reply, nil
	}, &out)
	if err != nil || got == nil || !got.Success {
		t.Fatalf("apiCmd = %+v, %v; want a connector without the field to keep its result", got, err)
	}
	if out.Len() != 0 {
		t.Errorf("stdout = %q, want no invented note", out.String())
	}
}

func TestAPISendsTheQueryAsAPositionalArgument(t *testing.T) {
	var sent map[string]interface{}
	_, err := apiCmd([]string{"List.Add", "--limit", "5"}, func(_ string, params interface{}) (*client.CommandResponse, error) {
		sent = params.(map[string]interface{})
		return &client.CommandResponse{Success: true}, nil
	}, &bytes.Buffer{})
	if err != nil {
		t.Fatalf("apiCmd: %v", err)
	}
	args, _ := sent["args"].([]string)
	if len(args) != 1 || args[0] != "List.Add" {
		t.Errorf("args = %v, want the query positional", sent["args"])
	}
	if sent["limit"] != 5 {
		t.Errorf("limit = %v, want the numeric flag value", sent["limit"])
	}
}

func apiSend(reply *client.CommandResponse) sendFn {
	return func(string, interface{}) (*client.CommandResponse, error) { return reply, nil }
}

// apiPrinted is what printResponse shows for a reply whose data is a JSON string.
func apiPrinted(t *testing.T, resp *client.CommandResponse) string {
	t.Helper()
	var text string
	if err := json.Unmarshal(resp.Data, &text); err != nil {
		t.Fatalf("data = %s, want the rendered text as a JSON string: %v", resp.Data, err)
	}
	return text
}

const apiMemberReply = `{
	"query": "SystemAPI.TryGetSingletonBuffer", "member": "TryGetSingletonBuffer", "matched_by": "type.member",
	"matches": 1, "index": {"assemblies_indexed": 423, "complete": true, "types_indexed": 67020},
	"types": [{
		"type": "Unity.Entities.SystemAPI", "kind": "static class", "assembly": "Unity.Entities",
		"base_type": "object", "interfaces": [], "member_filter": "TryGetSingletonBuffer",
		"member_total": 1, "members_listed": 1,
		"counts": {"methods": 44, "constructors": 0, "properties": 1, "fields": 0, "events": 0, "nested_types": 1},
		"members": [{"kind": "method", "name": "TryGetSingletonBuffer", "docs": null, "obsolete": null,
			"signature": "public static bool TryGetSingletonBuffer<T>(out DynamicBuffer<T> value, bool isReadOnly = false) where T : unmanaged, IBufferElementData"}]
	}]
}`

// The signature is what a member query is for, so it is the second line, not 35 lines into JSON.
func TestAPIMemberAnswerIsHeaderThenSignatures(t *testing.T) {
	got, err := apiCmd([]string{"SystemAPI.TryGetSingletonBuffer"}, apiSend(&client.CommandResponse{
		Success: true, Data: apiReplyJSON(t, true, true, apiMemberReply),
	}), &bytes.Buffer{})
	if err != nil {
		t.Fatalf("apiCmd: %v", err)
	}
	want := "Unity.Entities.SystemAPI (static class, Unity.Entities)\n" +
		"  public static bool TryGetSingletonBuffer<T>(out DynamicBuffer<T> value, bool isReadOnly = false) where T : unmanaged, IBufferElementData\n"
	if text := apiPrinted(t, got); text != want {
		t.Errorf("text =\n%s\nwant\n%s", text, want)
	}
}

func TestAPIJSONFlagKeepsTheConnectorReply(t *testing.T) {
	var sent map[string]interface{}
	data := apiReplyJSON(t, true, true, apiMemberReply)
	got, err := apiCmd([]string{"SystemAPI.TryGetSingletonBuffer", "--json"}, func(_ string, params interface{}) (*client.CommandResponse, error) {
		sent = params.(map[string]interface{})
		return &client.CommandResponse{Success: true, Data: data}, nil
	}, &bytes.Buffer{})
	if err != nil {
		t.Fatalf("apiCmd: %v", err)
	}
	if _, forwarded := sent["json"]; forwarded {
		t.Error("--json was sent to the connector; it is a CLI output switch")
	}
	if string(got.Data) != string(data) {
		t.Errorf("data = %s, want the connector reply unchanged", got.Data)
	}
}

func TestAPITypeAnswerHasParentsCountsAndTheCutOff(t *testing.T) {
	got, err := apiCmd([]string{"Vector3"}, apiSend(&client.CommandResponse{
		Success: true,
		Data: apiReplyJSON(t, true, true, `{
			"query": "Vector3", "member": null, "matched_by": "type", "matches": 1,
			"types": [{
				"type": "UnityEngine.Vector3", "kind": "struct", "assembly": "UnityEngine.CoreModule",
				"base_type": "ValueType", "interfaces": ["IEquatable<Vector3>", "IFormattable"],
				"member_filter": null, "member_total": 3, "members_listed": 1,
				"counts": {"methods": 2, "constructors": 0, "properties": 1, "fields": 0, "events": 0, "nested_types": 0},
				"members": [{"signature": "public static Vector3 Lerp(Vector3 a, Vector3 b, float t)",
					"obsolete": {"message": "Will be removed", "is_error": false, "replacement": "LerpUnclamped"}}]
			}]
		}`),
	}), &bytes.Buffer{})
	if err != nil {
		t.Fatalf("apiCmd: %v", err)
	}
	want := "UnityEngine.Vector3 (struct, UnityEngine.CoreModule) : IEquatable<Vector3>, IFormattable\n" +
		"  2 methods, 1 property\n" +
		"  public static Vector3 Lerp(Vector3 a, Vector3 b, float t)  [obsolete: Will be removed; use LerpUnclamped]\n" +
		"  (+2 more; --limit 3 lists them)\n"
	if text := apiPrinted(t, got); text != want {
		t.Errorf("text =\n%s\nwant\n%s", text, want)
	}
}

// With nothing to describe the error message is the answer; the JSON details would only repeat it.
func TestAPINothingFoundPrintsNoDetails(t *testing.T) {
	got, _ := apiCmd([]string{"NoSuchTypeXyz"}, apiSend(&client.CommandResponse{
		Success: true,
		Message: "no type or member named 'NoSuchTypeXyz' in the loaded assemblies",
		Data:    apiReplyJSON(t, true, false, `{"query": "NoSuchTypeXyz", "matches": 0, "types": []}`),
	}), &bytes.Buffer{})
	if got.Success || len(got.Data) != 0 {
		t.Errorf("got success=%v data=%s, want a failure carrying only the message", got.Success, got.Data)
	}
}

func TestAPIObsoleteReplacementIsNotRepeated(t *testing.T) {
	got := apiObsoleteText(&apiObsolete{Message: "Use IsBufferEnabled (UnityUpgradable) -> IsBufferEnabled(*)", Replacement: "IsBufferEnabled(*)"})
	if want := "obsolete: Use IsBufferEnabled (UnityUpgradable) -> IsBufferEnabled(*)"; got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}
