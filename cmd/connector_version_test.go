package cmd

import (
	"encoding/json"
	"os"
	"regexp"
	"testing"
)

// The heartbeat reports CONNECTOR_VERSION, not package.json, so a release that bumps only
// package.json ships a connector that every CLI call warns about.
func TestHeartbeatConnectorVersionMatchesPackageJSON(t *testing.T) {
	raw, err := os.ReadFile("../unity-connector/package.json")
	if err != nil {
		t.Fatal(err)
	}
	var pkg struct {
		Version string `json:"version"`
	}
	if err := json.Unmarshal(raw, &pkg); err != nil {
		t.Fatal(err)
	}

	src, err := os.ReadFile("../unity-connector/Editor/Heartbeat.cs")
	if err != nil {
		t.Fatal(err)
	}
	m := regexp.MustCompile(`const string CONNECTOR_VERSION = "([^"]+)";`).FindSubmatch(src)
	if m == nil {
		t.Fatal("CONNECTOR_VERSION constant not found in Heartbeat.cs")
	}
	if got := string(m[1]); got != pkg.Version {
		t.Fatalf("Heartbeat.cs CONNECTOR_VERSION is %q but package.json version is %q", got, pkg.Version)
	}
}
