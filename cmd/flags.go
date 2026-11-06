package cmd

import (
	"errors"
	"fmt"
	"strings"
)

// flagSpec is one CLI-owned command's flag surface: every --flag it accepts besides the global ones,
// and the spellings that contradict each other. The registry below is the single list of what each
// command understands; TestCommandFlagsAreDocumented and TestHelpFlagsAreAccepted keep it and the
// printed help from drifting apart.
type flagSpec struct {
	allowed []string
	// aliases are accepted but not documented: the connector's own spelling of a flag the CLI
	// already documents (--allow_stale for --allow-stale), kept for scripts written against it.
	aliases   []string
	exclusive [][2]string
	// requires names flags that only mean something together: --keep-console without --compile asks
	// for a console that is not cleared before a compile that is not requested.
	requires []flagRequirement
}

type flagRequirement struct {
	flag, needs string
	// because explains the pairing; empty when the flag name alone makes it obvious.
	because string
}

func (s flagSpec) allows(name string) bool {
	for _, candidate := range append(s.allowed, s.aliases...) {
		if candidate == name {
			return true
		}
	}
	return false
}

// globalFlags are accepted anywhere, before or after the command name: flag.CommandLine owns them
// (or, for --params, buildParams merges them into the tool parameters). --timeout is also the
// per-command budget of `wait`, which is why that spec lists it too.
var globalFlags = map[string]struct{}{
	"port":                    {},
	"project":                 {},
	"timeout":                 {},
	"meta":                    {},
	"ignore-version-mismatch": {},
	"params":                  {},
}

// commandFlags is every command this CLI parses itself. A command name that is not here is a
// passthrough: its flags are tool parameters, and the connector decides which of those it knows
// (answering `unknown_parameter`), so the CLI must not invent a flag list for it.
//
// The specs follow each command's --help: a flag that only appears there and not in the parser is a
// help lie, and a flag the parser takes that help never names is undiscoverable. Editor subcommands
// are keyed "editor <sub>" because play takes --wait while stop does not.
var commandFlags = map[string]flagSpec{
	"status":            {},
	"list":              {},
	"help":              {},
	"version":           {},
	"menu":              {aliases: []string{"menu_path"}},
	"reserialize":       {aliases: []string{"path", "paths"}},
	"update":            {allowed: []string{"check"}},
	"compile_status":    {allowed: []string{"scan", "scan_budget"}},
	"clear_burst_cache": {allowed: []string{"scope", "dry_run"}},
	"burst_status":      {allowed: []string{"wait", "compile", "type", "namespace", "assembly", "list", "include_packages", "limit"}},
	"test": {allowed: []string{
		"mode", "filter", "assembly", "allow-dirty-scenes", "auto-save-scenes", "allow-stale",
	}, aliases: []string{"allow_stale"}},
	"verify": {allowed: []string{
		"no-tests", "no-burst", "burst-wait", "assembly", "filter", "mode", "max",
		"keep-console", "allow-stale", "json",
	}, aliases: []string{"allow_stale"}},
	"wait":    {allowed: []string{"compile", "timeout", "json"}},
	"exec":    {allowed: []string{"usings", "no_default_usings", "await_timeout", "skip_compile_check", "csc", "dotnet"}},
	"console": {allowed: []string{"lines", "type", "stacktrace", "frames", "entry_chars", "max_bytes", "full", "clear"}},
	"screenshot": {allowed: []string{
		"mode", "view", "width", "height", "output_path",
	}},
	"api":          {allowed: []string{"assembly", "docs", "limit", "max_types", "budget", "json"}, aliases: []string{"query", "type"}},
	"build":        {allowed: []string{"profile", "target", "output", "scenes", "development", "no-wait"}, aliases: []string{"output_path", "no_wait", "nowait"}},
	"build status": {},
	"input": {allowed: []string{
		"x", "y", "dx", "dy", "button", "mode", "control", "value", "index",
		"hold_ms", "hold_frames", "scroll_x", "scroll_y",
	}, aliases: []string{"key", "device", "pump_frames"}},
	"profiler": {allowed: []string{
		"depth", "root", "frames", "from", "to", "parent", "min", "sort", "max", "frame", "thread",
	}, aliases: []string{"action"}},
	"modal":        {allowed: []string{"button", "index", "id"}, aliases: []string{"action"}},
	"dialogs":      {allowed: []string{"click", "window"}},
	"editor":       {},
	"editor play":  {allowed: []string{"wait", "no-wait"}, exclusive: [][2]string{{"wait", "no-wait"}}},
	"editor stop":  {allowed: []string{"wait", "no-wait"}, exclusive: [][2]string{{"wait", "no-wait"}}},
	"editor pause": {},
	"editor refresh": {
		allowed:  []string{"force", "compile", "keep-console"},
		requires: []flagRequirement{{flag: "keep-console", needs: "compile", because: "it keeps the console uncleared before the compile, so the compile report may include older errors"}},
	},
	"editor quit":    {allowed: []string{"save", "discard", "force"}, exclusive: [][2]string{{"save", "discard"}}},
	"editor restart": {allowed: []string{"save", "discard", "force", "clean", "editor-path"}, exclusive: [][2]string{{"save", "discard"}}},
}

// subFlag is one --name with its value, split the way buildParams splits them.
type subFlag struct {
	name  string
	value string
}

// scanArgs walks args exactly like parseFlagArgs: a token starting with "--" names a flag and takes
// the next token as its value unless that token is itself a flag. Validation and parameter building
// therefore agree on what counts as a flag, which is the point of doing it in one place.
func scanArgs(args []string) (flags []subFlag, positional []string) {
	for i := 0; i < len(args); i++ {
		a := args[i]
		if !strings.HasPrefix(a, "--") {
			positional = append(positional, a)
			continue
		}
		f := subFlag{name: a[2:], value: "true"}
		if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
			f.value = args[i+1]
			i++
		}
		flags = append(flags, f)
	}
	return flags, positional
}

// commandFlagKey names the flag spec a command invocation falls under, how many leading args are its
// subcommand name, and whether this CLI owns the command at all. An editor subcommand this table does
// not know (a typo such as "editor paly") is left to editorCmd, whose error names the real actions.
func commandFlagKey(category string, subArgs []string) (key string, subTokens int, owned bool) {
	sub := ""
	if len(subArgs) > 0 && !strings.HasPrefix(subArgs[0], "--") {
		sub = subArgs[0]
	}
	key = category
	if category == "editor" || (category == "build" && sub == "status") {
		if sub != "" {
			key = category + " " + sub
			subTokens = 1
		}
	}
	_, owned = commandFlags[key]
	return key, subTokens, owned
}

// validateCommandFlags rejects a flag the command does not take, and a pair of flags that contradict
// each other. It runs before discovery, so a typo cannot reach the editor (a `test --filtre x` that
// quietly ran the whole suite is the bug this exists for) and a refused quit never prints that it is
// stopping anything.
func validateCommandFlags(category string, subArgs []string) error {
	key, subTokens, owned := commandFlagKey(category, subArgs)
	if !owned {
		return nil
	}
	spec := commandFlags[key]
	flags, _ := scanArgs(subArgs[subTokens:])

	seen := map[string]bool{}
	for _, f := range flags {
		if f.name == "" {
			return fmt.Errorf("unknown flag -- for %s; see unity-cli %s --help", key, key)
		}
		if _, global := globalFlags[f.name]; global {
			continue
		}
		if !spec.allows(f.name) {
			return unknownFlagError(key, f.name)
		}
		seen[f.name] = true
	}
	if err := checkExclusiveFlags(key, spec, seen); err != nil {
		return err
	}
	return checkRequiredFlags(key, spec, seen)
}

func unknownFlagError(command, flag string) error {
	return fmt.Errorf("unknown flag --%s for %s; see unity-cli %s --help", flag, command, command)
}

// checkExclusiveFlags is separate from validation because quit and restart call it themselves: a
// refused `editor quit --save --discard` must not announce "Stopping Unity" first, and those two
// commands run before the normal dispatch path.
func checkExclusiveFlags(command string, spec flagSpec, seen map[string]bool) error {
	for _, pair := range spec.exclusive {
		if seen[pair[0]] && seen[pair[1]] {
			return fmt.Errorf("--%s and --%s are mutually exclusive (for %s)", pair[0], pair[1], command)
		}
	}
	return nil
}

// checkRequiredFlags rejects a flag whose companion is missing, with the reason the pair exists.
func checkRequiredFlags(command string, spec flagSpec, seen map[string]bool) error {
	for _, need := range spec.requires {
		if !seen[need.flag] || seen[need.needs] {
			continue
		}
		message := fmt.Sprintf("%s --%s only applies with --%s", command, need.flag, need.needs)
		if need.because != "" {
			message += " (" + need.because + ")"
		}
		return errors.New(message)
	}
	return nil
}

// rejectContradictoryFlags is the lookup for a command that parsed its own flags already: quit and
// restart run before the normal dispatch, so their refusal must not wait for it.
func rejectContradictoryFlags(command string, flags map[string]string) error {
	spec, ok := commandFlags[command]
	if !ok {
		return nil
	}
	return checkExclusiveFlags(command, spec, seenFlags(flags))
}

// rejectIncompleteFlags is rejectContradictoryFlags for a flag that needs a companion: editor refresh
// checks it before it decides whether to refresh or compile.
func rejectIncompleteFlags(command string, flags map[string]string) error {
	spec, ok := commandFlags[command]
	if !ok {
		return nil
	}
	return checkRequiredFlags(command, spec, seenFlags(flags))
}

func seenFlags(flags map[string]string) map[string]bool {
	seen := make(map[string]bool, len(flags))
	for name := range flags {
		seen[name] = true
	}
	return seen
}
