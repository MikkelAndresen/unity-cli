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
func apiCmd(args []string, send sendFn, out io.Writer) (*client.CommandResponse, error) {
	params, err := buildParams(args, nil)
	if err != nil {
		return nil, err
	}
	resp, err := send("api", params)
	if err != nil || resp == nil || !resp.Success {
		return resp, err
	}

	reply, exact, ok := apiExactness(resp.Data)
	if !ok || exact {
		return resp, nil
	}
	// Nothing to describe: the query named nothing that exists, so the nearest-match suggestion is
	// the connector's message and the command fails rather than passing a list of lookalikes off as
	// the answer.
	if len(reply.Types) == 0 {
		resp.Success = false
		return resp, nil
	}
	if line := apiNoExactMatchLine(reply); line != "" {
		fmt.Fprintln(out, line)
	}
	return resp, nil
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
