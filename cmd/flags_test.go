package cmd

import (
	"bytes"
	"regexp"
	"strings"
	"testing"
)

// helpTopicWord is the topic whose --help is printed for a command key: `editor play --help` prints
// the editor topic, and an unknown topic prints a one-line "Unknown help topic" instead.
func helpTopicWord(key string) string {
	return strings.SplitN(key, " ", 2)[0]
}

func topicHelpText(t *testing.T, word string) string {
	t.Helper()
	var buf bytes.Buffer
	printTopicHelpTo(&buf, word)
	if strings.HasPrefix(buf.String(), "Unknown help topic") {
		t.Fatalf("no help topic for %q", word)
	}
	return buf.String()
}

// TestCommandFlagsAreDocumented fails when the parser accepts a flag the command's help never names.
func TestCommandFlagsAreDocumented(t *testing.T) {
	for key, spec := range commandFlags {
		if key == "help" || key == "version" {
			continue
		}
		help := topicHelpText(t, helpTopicWord(key))
		for _, flag := range spec.allowed {
			if !strings.Contains(help, "--"+flag) {
				t.Errorf("%s accepts --%s but its help never mentions it", key, flag)
			}
		}
	}
}

// TestHelpFlagsAreAccepted fails when help documents a flag the parser would reject — the drift that
// made `test --filtre` run the whole suite silently.
func TestHelpFlagsAreAccepted(t *testing.T) {
	documented := regexp.MustCompile(`^ +--([A-Za-z0-9][A-Za-z0-9_-]*)`)
	words := map[string]bool{}
	for key := range commandFlags {
		if key == "help" || key == "version" {
			continue
		}
		words[helpTopicWord(key)] = true
	}

	for word := range words {
		allowed := map[string]bool{}
		for name := range globalFlags {
			allowed[name] = true
		}
		for key, spec := range commandFlags {
			if helpTopicWord(key) != word {
				continue
			}
			for _, flag := range append(spec.allowed, spec.aliases...) {
				allowed[flag] = true
			}
		}

		for _, line := range strings.Split(topicHelpText(t, word), "\n") {
			match := documented.FindStringSubmatch(line)
			if match == nil {
				continue
			}
			if !allowed[match[1]] {
				t.Errorf("help for %s documents --%s, which no %s command accepts (%q)", word, match[1], word, line)
			}
		}
	}
}

func TestValidateCommandFlags(t *testing.T) {
	tests := []struct {
		name     string
		category string
		args     []string
		wantErr  string
	}{
		{
			name:     "typo on test is rejected instead of running everything",
			category: "test",
			args:     []string{"--filtre", "NoSuchTestXyz"},
			wantErr:  "unknown flag --filtre for test; see unity-cli test --help",
		},
		{
			name:     "typo on status",
			category: "status",
			args:     []string{"--bogus"},
			wantErr:  "unknown flag --bogus for status",
		},
		{
			name:     "typo on exec",
			category: "exec",
			args:     []string{"return 1;", "--bogus"},
			wantErr:  "unknown flag --bogus for exec",
		},
		{
			name:     "editor subcommand names itself in the error",
			category: "editor",
			args:     []string{"play", "--wiat"},
			wantErr:  "unknown flag --wiat for editor play; see unity-cli editor play --help",
		},
		{
			name:     "a flag another subcommand takes is still unknown here",
			category: "editor",
			args:     []string{"pause", "--no-wait"},
			wantErr:  "unknown flag --no-wait for editor pause",
		},
		{
			name:     "global flags are always allowed",
			category: "test",
			args:     []string{"--filter", "A", "--meta", "--params", `{"a":1}`, "--timeout", "5", "--project", "/p", "--port", "1", "--ignore-version-mismatch"},
		},
		{
			name:     "a command's own flags are allowed",
			category: "verify",
			args:     []string{"--no-tests", "--no-burst", "--burst-wait", "5", "--assembly", "A", "--filter", "F", "--mode", "PlayMode", "--max", "3", "--keep-console", "--allow-stale", "--json"},
		},
		{
			name:     "the connector's spelling of allow-stale stays accepted",
			category: "verify",
			args:     []string{"--allow_stale"},
		},
		{
			name:     "a connector parameter spelling the CLI has always forwarded",
			category: "api",
			args:     []string{"--query", "Vector3"},
		},
		{
			name:     "reserialize keeps the connector's path parameter",
			category: "reserialize",
			args:     []string{"--path", "Assets/A.asset"},
		},
		{
			name:     "input keeps the connector's pump_frames parameter",
			category: "input",
			args:     []string{"key", "Space", "--pump_frames", "2"},
		},
		{
			name:     "unknown flags of a custom tool are the connector's to judge",
			category: "my_custom_tool",
			args:     []string{"--anything", "x"},
		},
		{
			name:     "an unknown editor action is left to the action error",
			category: "editor",
			args:     []string{"paly", "--wait"},
		},
		{
			name:     "build status takes no job flags",
			category: "build",
			args:     []string{"status", "--profile", "p"},
			wantErr:  "unknown flag --profile for build status",
		},
		{
			name:     "a flag whose companion is missing",
			category: "editor",
			args:     []string{"refresh", "--keep-console"},
			wantErr:  "editor refresh --keep-console only applies with --compile",
		},
		{
			name:     "the companion present is fine",
			category: "editor",
			args:     []string{"refresh", "--compile", "--keep-console"},
		},
		{
			name:     "contradictory play flags",
			category: "editor",
			args:     []string{"play", "--wait", "--no-wait"},
			wantErr:  "--wait and --no-wait are mutually exclusive (for editor play)",
		},
		{
			name:     "contradictory quit flags",
			category: "editor",
			args:     []string{"quit", "--save", "--discard"},
			wantErr:  "--save and --discard are mutually exclusive (for editor quit)",
		},
		{
			name:     "contradictory restart flags",
			category: "editor",
			args:     []string{"restart", "--discard", "--save"},
			wantErr:  "--save and --discard are mutually exclusive (for editor restart)",
		},
		{
			name:     "quit's force is not a contradiction with save",
			category: "editor",
			args:     []string{"quit", "--save", "--force"},
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := validateCommandFlags(tt.category, tt.args)
			if tt.wantErr == "" {
				if err != nil {
					t.Fatalf("unexpected error: %v", err)
				}
				return
			}
			if err == nil {
				t.Fatalf("expected %q, got no error", tt.wantErr)
			}
			if !strings.Contains(err.Error(), tt.wantErr) {
				t.Errorf("error = %q, want it to contain %q", err, tt.wantErr)
			}
		})
	}
}

// TestScanArgsMatchesParseFlagArgs: validation reads flags through scanArgs, so the two must agree on
// where a flag's value stops. A value that looks like a flag ("--x") is a new flag, not this one's.
func TestScanArgsMatchesParseFlagArgs(t *testing.T) {
	args := []string{"--usings", "A,B", "--mode", "PlayMode", "code", "--flag", "--other", "v"}
	flags, paramsJSON, positional, err := parseFlagArgs(args)
	if err != nil {
		t.Fatalf("parseFlagArgs: %v", err)
	}
	if len(paramsJSON) != 0 {
		t.Errorf("paramsJSON = %v, want none", paramsJSON)
	}
	if flags["usings"] != "A,B" || flags["mode"] != "PlayMode" || flags["flag"] != "true" || flags["other"] != "v" {
		t.Errorf("flags = %v", flags)
	}
	if len(positional) != 1 || positional[0] != "code" {
		t.Errorf("positional = %v, want [code]", positional)
	}

	tokens, positional2 := scanArgs(args)
	if len(tokens) != 4 {
		t.Fatalf("scanArgs found %d flags, want 4: %v", len(tokens), tokens)
	}
	for i, token := range tokens {
		if token.value != flags[token.name] {
			t.Errorf("token %d = %+v, disagrees with parseFlagArgs[%q] = %q", i, token, token.name, flags[token.name])
		}
	}
	if len(positional2) != len(positional) || positional2[0] != positional[0] {
		t.Errorf("scanArgs positional = %v, want %v", positional2, positional)
	}
}
