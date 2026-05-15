# Sample for ScriptDeck. Dumps every saved Wi-Fi profile's SSID and stored
# password as [pscustomobject] rows so they land cleanly in the grid.
#
# Requires elevation: netsh only prints "Key Content" when the process is
# Administrator. We warn-but-still-run if not elevated so the user gets
# the SSID list back without thinking the script silently failed.
#
# Locale tolerance: netsh's output labels are localized, so we anchor on
# the regex SHAPE (": <value>") rather than the label text. The "Key
# Content" string in the second match is the one piece that's still
# locale-sensitive; on non-English Windows you'd swap it for the local
# equivalent or read it from a localized resource lookup.

[CmdletBinding()]
param()

# Admin check up front. We never abort -- a partial result (SSIDs only)
# is more useful than an opaque "no output". Warning surfaces in
# ScriptDeck's console RTB in yellow.
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning "Not elevated. SSIDs will list but the Password column will be empty."
}

# Step 1: collect SSIDs from "show profiles". netsh's English output looks
# like "    All User Profile     : MyHomeWiFi". Match any line that has
# "<label>: <value>" shape, then drop empties and the literal "<None>".
$profilesOut = & netsh.exe wlan show profiles
$names = foreach ($line in $profilesOut) {
    $m = [regex]::Match($line, '^\s*\S.*?:\s*(.+?)\s*$')
    if ($m.Success) {
        $candidate = $m.Groups[1].Value
        if ($candidate -and $candidate -notmatch '^(None|<None>)$') {
            $candidate
        }
    }
}
$names = $names | Select-Object -Unique

# Step 2: per-profile detail. We build the "name=..." argument as its own
# variable so PowerShell's native-command parser never sees an inline
# quote next to an = sign -- that combo is the source of the parser
# error in the original ("The string is missing the terminator: ""."). PS
# auto-quotes the variable's value when invoking netsh, so SSIDs with
# spaces still work.
foreach ($name in $names) {
    $nameArg = "name=$name"
    $detail  = & netsh.exe wlan show profiles $nameArg key=clear

    $password = $null
    foreach ($line in $detail) {
        $m = [regex]::Match($line, 'Key Content\s*:\s*(.+)$')
        if ($m.Success) {
            $password = $m.Groups[1].Value.Trim()
            break
        }
    }

    # Emit a structured row regardless -- callers can filter on null
    # Password if they only want profiles with a stored key.
    [pscustomobject]@{
        SSID     = $name
        Password = $password
    }
}
