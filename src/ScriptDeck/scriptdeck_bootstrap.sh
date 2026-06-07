#!/bin/bash
# -----------------------------------------------------------------------------
# ScriptDeck bash bootstrap.
#
# Auto-sourced by BashExecutor (via BASH_ENV) before user scripts run, so
# every bash button gets these helpers without an explicit `source` line.
#
# Bash on Windows is fragmented (Git Bash, WSL, MSYS2, Cygwin) -- each has
# its own conventions for converting between Windows-style paths
# ("C:\foo\bar") and the shell's native form ("/c/foo/bar", "/mnt/c/foo/bar",
# "/cygdrive/c/foo/bar"). The helpers below detect which environment we're
# in once, then provide consistent path-translation and JSON-tagging APIs.
#
# Note: WSL invocations don't currently auto-source this file because the
# Windows-side BASH_ENV path isn't visible inside the WSL distro's
# filesystem without translation. WSL users can manually source a copy.
# -----------------------------------------------------------------------------

# ---- Environment detection -------------------------------------------------
# Cached in __scriptdeck_env so the detection only runs once per script.
# Values: "wsl", "git-bash", "cygwin", "other".

if command -v wslpath >/dev/null 2>&1; then
    __scriptdeck_env="wsl"
elif [[ -n "$MSYSTEM" ]]; then
    # Both Git Bash and MSYS2 set MSYSTEM (MINGW64, MSYS, etc.).
    __scriptdeck_env="git-bash"
elif [[ -n "$CYGWIN" ]] || [[ -d /cygdrive ]]; then
    __scriptdeck_env="cygwin"
else
    __scriptdeck_env="other"
fi


# ---- Path translation ------------------------------------------------------
#
# scriptdeck_to_unix_path "C:\foo\bar"
#   -> /mnt/c/foo/bar   (WSL)
#   -> /c/foo/bar       (Git Bash / MSYS2)
#   -> /cygdrive/c/foo/bar  (Cygwin)
#   -> C:/foo/bar       (other -- best-effort, just normalize separators)
#
# Idempotent: passing a path that's already in the native form returns it
# unchanged.

scriptdeck_to_unix_path() {
    local p="$1"
    [[ -z "$p" ]] && return 0
    case "$__scriptdeck_env" in
        wsl)
            wslpath -u "$p" 2>/dev/null || echo "$p"
            ;;
        git-bash)
            # C:\foo\bar -> /c/foo/bar; also handles C:/foo/bar.
            if [[ "$p" =~ ^([a-zA-Z]):[\\/](.*)$ ]]; then
                local drive=$(echo "${BASH_REMATCH[1]}" | tr 'A-Z' 'a-z')
                local rest="${BASH_REMATCH[2]//\\//}"
                echo "/$drive/$rest"
            else
                # Already unix-ish: normalize backslashes to forward.
                echo "${p//\\//}"
            fi
            ;;
        cygwin)
            if command -v cygpath >/dev/null 2>&1; then
                cygpath -u "$p" 2>/dev/null || echo "${p//\\//}"
            else
                echo "${p//\\//}"
            fi
            ;;
        *)
            # Unknown env -- best-effort, just replace backslashes.
            echo "${p//\\//}"
            ;;
    esac
}


# scriptdeck_to_win_path "/c/foo/bar"
#   -> C:\foo\bar
#
# Reverse of the above. Useful when a bash script computes a path that needs
# to go back to a Windows tool (or back into ScriptDeck via a
# __SCRIPTDECK_JSON__ line). Idempotent for already-Windows-style inputs.

scriptdeck_to_win_path() {
    local p="$1"
    [[ -z "$p" ]] && return 0
    case "$__scriptdeck_env" in
        wsl)
            wslpath -w "$p" 2>/dev/null || echo "$p"
            ;;
        git-bash)
            # /c/foo/bar -> C:\foo\bar
            if [[ "$p" =~ ^/([a-zA-Z])/(.*)$ ]]; then
                local drive=$(echo "${BASH_REMATCH[1]}" | tr 'a-z' 'A-Z')
                local rest="${BASH_REMATCH[2]//\//\\}"
                echo "${drive}:\\${rest}"
            else
                echo "$p"
            fi
            ;;
        cygwin)
            if command -v cygpath >/dev/null 2>&1; then
                cygpath -w "$p" 2>/dev/null || echo "$p"
            else
                echo "$p"
            fi
            ;;
        *)
            echo "$p"
            ;;
    esac
}


# scriptdeck_path "<any path>"
#   -> Auto-normalize to the form the current shell understands.
#
# Looks at the input: if it's Windows-style (starts with "<letter>:"), runs
# scriptdeck_to_unix_path. Otherwise passes through unchanged. The "I just
# want it to work" entry point -- shared inputs from ScriptDeck arrive as
# Windows paths, so wrap them in scriptdeck_path before using.

scriptdeck_path() {
    local p="$1"
    [[ -z "$p" ]] && return 0
    if [[ "$p" =~ ^[a-zA-Z]:[\\/] ]]; then
        scriptdeck_to_unix_path "$p"
    else
        # Already unix-style (or no path at all) -- pass through.
        echo "$p"
    fi
}


# ---- Tagged output ---------------------------------------------------------
#
# scriptdeck_write_rtb "string"
#   -> emit a __SCRIPTDECK_JSON__ line targeting the console RTB only.
#
# scriptdeck_write_grid_row key1=val1 key2=val2 ...
#   -> emit a __SCRIPTDECK_JSON__ line appending one row to the grid.
#      First call's keys pin the column set; later rows align to those
#      columns (matching PowerShell / Python tag semantics).
#
# Escape coverage: backslash, double-quote, newline, tab. Embedded NUL /
# fancy Unicode / control chars below 0x20 will hiccup -- if you have wild
# data, prefer Python where json.dumps is built in.

__scriptdeck_json_escape() {
    local s="$1"
    s="${s//\\/\\\\}"
    s="${s//\"/\\\"}"
    s="${s//$'\n'/\\n}"
    s="${s//$'\t'/\\t}"
    s="${s//$'\r'/\\r}"
    echo "$s"
}

scriptdeck_write_rtb() {
    local val="$*"
    val=$(__scriptdeck_json_escape "$val")
    echo "__SCRIPTDECK_JSON__{\"__ScriptDeckTarget\":\"rtb\",\"value\":\"$val\"}"
}

scriptdeck_write_grid_row() {
    local json='{"__ScriptDeckTarget":"grid","row":{'
    local first=1
    for kv in "$@"; do
        local key="${kv%%=*}"
        local val="${kv#*=}"
        local esc_key=$(__scriptdeck_json_escape "$key")
        local esc_val=$(__scriptdeck_json_escape "$val")
        if [[ $first -eq 0 ]]; then
            json+=","
        fi
        json+="\"$esc_key\":\"$esc_val\""
        first=0
    done
    json+='}}'
    echo "__SCRIPTDECK_JSON__$json"
}


# ---- Shared-input mutation -------------------------------------------------
#
# scriptdeck_set_shared_input id value [label]
#   -> create / update a Volatile shared input. Refuses Static ids (those
#      belong to the workspace file). On refusal, prints to stderr and
#      returns non-zero so a script using `set -e` halts cleanly.
#
# scriptdeck_remove_shared_input id
#   -> drop a Volatile shared input. Refuses Static ids.

__scriptdeck_id_is_static() {
    # __SCRIPTDECK_STATIC_IDS__ is a JSON array; grep for the quoted id.
    local id="$1"
    local list="$__SCRIPTDECK_STATIC_IDS__"
    [[ -z "$list" ]] && return 1
    echo "$list" | grep -q "\"$id\""
}

scriptdeck_set_shared_input() {
    local id="$1"; local value="$2"; local label="${3:-}"
    if [[ -z "$id" ]]; then
        echo "scriptdeck_set_shared_input: id is required" >&2
        return 1
    fi
    if __scriptdeck_id_is_static "$id"; then
        echo "Cannot set shared input '$id': it is a Static input owned by the workspace." >&2
        return 1
    fi
    local esc_id=$(__scriptdeck_json_escape "$id")
    local esc_val=$(__scriptdeck_json_escape "$value")
    if [[ -z "$label" ]]; then
        echo "__SCRIPTDECK_JSON__{\"__ScriptDeckSetSharedInput\":true,\"id\":\"$esc_id\",\"value\":\"$esc_val\",\"label\":null}"
    else
        local esc_label=$(__scriptdeck_json_escape "$label")
        echo "__SCRIPTDECK_JSON__{\"__ScriptDeckSetSharedInput\":true,\"id\":\"$esc_id\",\"value\":\"$esc_val\",\"label\":\"$esc_label\"}"
    fi
}

scriptdeck_remove_shared_input() {
    local id="$1"
    if [[ -z "$id" ]]; then
        echo "scriptdeck_remove_shared_input: id is required" >&2
        return 1
    fi
    if __scriptdeck_id_is_static "$id"; then
        echo "Cannot remove shared input '$id': it is a Static input owned by the workspace." >&2
        return 1
    fi
    local esc_id=$(__scriptdeck_json_escape "$id")
    echo "__SCRIPTDECK_JSON__{\"__ScriptDeckRemoveSharedInput\":true,\"id\":\"$esc_id\"}"
}
