#!/bin/bash
# ScriptDeck Bash demo button.
#
# Shows the three things a bash button typically does:
#   1. Plain echo lands in the console RTB.
#   2. scriptdeck_write_grid_row populates the structured grid.
#   3. scriptdeck_set_shared_input creates a Volatile shared input.
#
# Run from the sample workspace's "Tools" tab -> "Bash Info" button.

# 1. Plain echo -> console RTB.
echo "Bash $BASH_VERSION"
echo "Running under: $__scriptdeck_env"
echo "Workspace's computerName env var: ${computerName:-(unset)}"

# Demo the path-translation helper: convert any inbound Windows path
# (here we synthesize one) to whatever this shell understands.
demo_win_path='C:\Users\example\report.txt'
unix=$(scriptdeck_path "$demo_win_path")
echo "Path translation: '$demo_win_path' -> '$unix'"

# 2. Structured records -> results grid.
scriptdeck_write_grid_row property=version       value="$BASH_VERSION"
scriptdeck_write_grid_row property=environment   value="$__scriptdeck_env"
scriptdeck_write_grid_row property=bash_path     value="$BASH"
scriptdeck_write_grid_row property=script_dir    value="$(pwd)"

# 3. Volatile session input that other buttons can read via
#    {{lastBashRun}} token substitution.
scriptdeck_set_shared_input lastBashRun "$(date '+%Y-%m-%d %H:%M:%S')" "Last Bash run"
