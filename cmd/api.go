package cmd

import (
	"encoding/json"
	"fmt"
	"io"
	"strings"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// apiReply is the part of the `api` tool's answer the CLI acts on: whether the query named something
// that is really there, the sentence the connector built for a nearest-match answer, and what it
// offered instead when nothing matched.
type apiReply struct {
	Exact        bool      `json:"exact"`
	MatchedBy    string    `json:"matched_by"`
	Query        string    `json:"query"`
	Member       string    `json:"member"`
	Note         string    `json:"note"`
	ClosestTypes []string  `json:"closest_types"`
	Matches      int       `json:"matches"`
	Types        []apiType `json:"types"`
}

type apiType struct {
	Type    string `json:"type"`
	Members []struct {
		Name string `json:"name"`
	} `json:"members"`
}

// apiClosestMax caps the suggested names the CLI builds itself: the point is the likely typo fix,
// and the answer that follows lists what it found in full.
const apiClosestMax = 5

// apiCmd runs the api tool and turns its nearest-match fallback into an honest answer. The connector
// answers a typo with the closest real names instead of an error, which is useful text but not a
// successful lookup: `api NoSuchTypeXyz` is reported as a failure, and `api Vector3.Lerpp` (a member
// that does not exist) is answered with the closest members plus a line saying there was no exact
// match.
//
// The answer prints as text: one header per type and one C# signature per member. --json prints the
// connector's reply instead.
func apiCmd(args []string, send sendFn, out io.Writer) (*client.CommandResponse, error) {
	params, err := buildParams(args, nil)
	if err != nil {
		return nil, err
	}
	_, asJSON := params["json"]
	delete(params, "json")
	resp, err := send("api", params)
	if err != nil || resp == nil || !resp.Success {
		return resp, err
	}

	reply, exact, ok := apiExactness(resp.Data)
	if ok && !exact {
		// Nothing to describe: the query named nothing that exists, so the nearest-match suggestion is
		// the connector's message and the command fails rather than passing a list of lookalikes off as
		// the answer.
		if len(reply.Types) == 0 {
			resp.Success = false
			if !asJSON {
				resp.Data = nil
			}
			return resp, nil
		}
		if line := apiNoExactMatchLine(reply); line != "" {
			fmt.Fprintln(out, line)
		}
	}
	if !asJSON {
		if text, rendered := renderAPIText(resp.Data); rendered {
			resp.Data, _ = json.Marshal(text)
		}
	}
	return resp, nil
}

type apiTypeText struct {
	Type          string          `json:"type"`
	Kind          string          `json:"kind"`
	Assembly      string          `json:"assembly"`
	BaseType      string          `json:"base_type"`
	Interfaces    []string        `json:"interfaces"`
	Obsolete      *apiObsolete    `json:"obsolete"`
	Docs          string          `json:"docs"`
	MemberFilter  string          `json:"member_filter"`
	MemberTotal   int             `json:"member_total"`
	MembersListed int             `json:"members_listed"`
	Counts        map[string]int  `json:"counts"`
	Members       []apiMemberText `json:"members"`
}

type apiMemberText struct {
	Signature string       `json:"signature"`
	Obsolete  *apiObsolete `json:"obsolete"`
	Docs      string       `json:"docs"`
}

type apiObsolete struct {
	Message     string `json:"message"`
	IsError     bool   `json:"is_error"`
	Replacement string `json:"replacement"`
}

// apiInterfacesShown caps the interface list in a type header: generic collections implement a
// dozen, and the member signatures are what the caller came for.
const apiInterfacesShown = 6

// apiCountOrder is the order member counts print in, with their singular names.
var apiCountOrder = []struct{ key, one string }{
	{"constructors", "constructor"}, {"methods", "method"}, {"properties", "property"},
	{"fields", "field"}, {"events", "event"}, {"nested_types", "nested type"},
}

// renderAPIText turns the connector's reply into text. rendered is false for a reply without a
// types list (an older or unexpected shape), which then prints as JSON.
func renderAPIText(data json.RawMessage) (string, bool) {
	var reply struct {
		Types *[]apiTypeText `json:"types"`
	}
	if len(data) == 0 || json.Unmarshal(data, &reply) != nil || reply.Types == nil {
		return "", false
	}
	var b strings.Builder
	for i, t := range *reply.Types {
		if i > 0 {
			b.WriteString("\n")
		}
		fmt.Fprintf(&b, "%s (%s, %s)", t.Type, t.Kind, t.Assembly)
		if t.MemberFilter == "" {
			if parents := apiParents(t); parents != "" {
				b.WriteString(" : " + parents)
			}
		}
		b.WriteString("\n")
		if t.Obsolete != nil {
			b.WriteString("  " + apiObsoleteText(t.Obsolete) + "\n")
		}
		if t.Docs != "" {
			b.WriteString("  // " + apiOneLine(t.Docs) + "\n")
		}
		if t.MemberFilter == "" {
			if counts := apiCountsText(t.Counts); counts != "" {
				b.WriteString("  " + counts + "\n")
			}
		}
		for _, m := range t.Members {
			b.WriteString("  " + m.Signature)
			if m.Obsolete != nil {
				b.WriteString("  [" + apiObsoleteText(m.Obsolete) + "]")
			}
			b.WriteString("\n")
			if m.Docs != "" {
				b.WriteString("      // " + apiOneLine(m.Docs) + "\n")
			}
		}
		if more := t.MemberTotal - len(t.Members); more > 0 {
			fmt.Fprintf(&b, "  (+%d more; --limit %d lists them)\n", more, t.MemberTotal)
		}
	}
	return b.String(), true
}

func apiParents(t apiTypeText) string {
	var parents []string
	if t.BaseType != "" && t.BaseType != "object" && t.BaseType != "ValueType" && t.BaseType != "Enum" {
		parents = append(parents, t.BaseType)
	}
	ifaces := t.Interfaces
	if len(ifaces) > apiInterfacesShown {
		ifaces = append(append([]string{}, ifaces[:apiInterfacesShown]...), fmt.Sprintf("(+%d more)", len(t.Interfaces)-apiInterfacesShown))
	}
	return strings.Join(append(parents, ifaces...), ", ")
}

func apiCountsText(counts map[string]int) string {
	var parts []string
	for _, c := range apiCountOrder {
		n := counts[c.key]
		switch {
		case n == 1:
			parts = append(parts, "1 "+c.one)
		case n > 1 && c.one == "property":
			parts = append(parts, fmt.Sprintf("%d properties", n))
		case n > 1:
			parts = append(parts, fmt.Sprintf("%d %ss", n, c.one))
		}
	}
	return strings.Join(parts, ", ")
}

func apiObsoleteText(o *apiObsolete) string {
	text := "obsolete"
	if o.IsError {
		text += " (error)"
	}
	if msg := apiOneLine(o.Message); msg != "" {
		text += ": " + msg
	}
	if o.Replacement != "" && !strings.Contains(text, o.Replacement) {
		text += "; use " + o.Replacement
	}
	return text
}

func apiOneLine(s string) string {
	return strings.Join(strings.Fields(s), " ")
}

// apiExactness reads the connector's "exact" flag. ok is false when the reply carries no such key:
// a connector older than the flag cannot have answered "no exact match", so its result stands.
func apiExactness(data json.RawMessage) (apiReply, bool, bool) {
	if len(data) == 0 || string(data) == "null" {
		return apiReply{}, false, false
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(data, &fields); err != nil {
		return apiReply{}, false, false
	}
	raw, present := fields["exact"]
	if !present {
		return apiReply{}, false, false
	}
	var exact bool
	if err := json.Unmarshal(raw, &exact); err != nil {
		return apiReply{}, false, false
	}
	var reply apiReply
	if err := json.Unmarshal(data, &reply); err != nil {
		return apiReply{}, false, false
	}
	return reply, exact, true
}

// apiNoExactMatchLine is the sentence printed before a nearest-match answer. The connector's own
// note wins: it names the member that does not exist and the members it suggests, deduplicated by
// name, which the payload's type/member lists cannot say without repeating every overload.
func apiNoExactMatchLine(reply apiReply) string {
	if note := strings.TrimSpace(reply.Note); note != "" {
		return note
	}
	var names []string
	seen := map[string]bool{}
	add := func(name string) {
		if name == "" || seen[name] {
			return
		}
		seen[name] = true
		names = append(names, name)
	}
	for _, closest := range reply.ClosestTypes {
		add(closest)
	}
	for _, t := range reply.Types {
		if reply.Member != "" && len(t.Members) > 0 {
			for _, m := range t.Members {
				add(t.Type + "." + m.Name)
			}
			continue
		}
		add(t.Type)
	}
	if len(names) > apiClosestMax {
		rest := len(names) - apiClosestMax
		names = append(names[:apiClosestMax], fmt.Sprintf("(+%d more)", rest))
	}
	if len(names) == 0 {
		return ""
	}
	return "no exact match; closest: " + strings.Join(names, ", ")
}
