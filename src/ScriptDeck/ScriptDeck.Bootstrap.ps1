# ScriptDeck bootstrap -- dot-sourced into the long-lived PowerShell
# runspace once at startup. Anything defined here is available to every
# button-click script for the lifetime of the app.
#
# Keep this file SMALL. Heavy modules belong in user scripts (or in a
# user-supplied profile dot-sourced from one), not in the bootstrap --
# slow startup is the most visible kind of slow.
#
# Authoring conventions:
#   - Functions go into global scope (the runspace's session state) so
#     scripts can call them without a leading "Get-" prefix or import.
#   - No `Set-StrictMode`, no `$ErrorActionPreference = 'Stop'` -- this
#     file shouldn't change the engine state user scripts inherit.

# ---------------------------------------------------------------------------
# Test-IsLocalTarget -- "is this hostname the local machine?"
#
# Convention used throughout ScriptDeck:
#   * The "computerName" shared input is normalized at click time so
#     empty / "." / "localhost" become $env:COMPUTERNAME before the
#     script runs.
#   * Therefore $ComputerName is always a real hostname when a script
#     starts, and the only thing left to decide is "is it MINE?"
#
# Returns $true when the value is the local machine name (or empty,
# defensively). False otherwise -- which scripts treat as "remote",
# typically wrapping the body in Invoke-Command -ComputerName ...
# ---------------------------------------------------------------------------
# Cached list of every name / address that resolves to "this machine."
# Built lazily on the first call to Test-IsLocalTarget so we pay the
# cost once per session. Includes loopback synonyms, the NetBIOS name,
# the FQDN if the user is domain-joined, and EVERY local IPv4 / IPv6
# address bound to a NIC -- including link-local (fe80::/10) and the
# IPv6 loopback (::1).
$script:__ScriptDeckLocalTargets = $null

function Get-ScriptDeckLocalTargetCache {
    if ($null -ne $script:__ScriptDeckLocalTargets) {
        return $script:__ScriptDeckLocalTargets
    }

    $set = New-Object 'System.Collections.Generic.HashSet[string]' (
        [System.StringComparer]::OrdinalIgnoreCase)

    # Static synonyms that match regardless of NIC config.
    foreach ($name in @($env:COMPUTERNAME, '.', 'localhost', '127.0.0.1', '::1')) {
        if (-not [string]::IsNullOrEmpty($name)) { [void]$set.Add($name) }
    }
    if (-not [string]::IsNullOrEmpty($env:USERDNSDOMAIN)) {
        [void]$set.Add("$env:COMPUTERNAME.$env:USERDNSDOMAIN")
    }

    # Enumerate every address bound to a local NIC. Catches IPv4 (LAN
    # 10.x / 192.168.x), public IPv4, IPv6 (global, ULA, and link-local
    # fe80::), and any tunneled ones. We strip the IPv6 zone index
    # (%eth0 etc.) for the canonical form, but ALSO add the zone-bearing
    # original so a user who pastes "fe80::1%2" still matches.
    try {
        $addrs = [System.Net.Dns]::GetHostAddresses(
            [System.Net.Dns]::GetHostName())
        foreach ($a in $addrs) {
            $s = $a.ToString()
            if ([string]::IsNullOrEmpty($s)) { continue }
            [void]$set.Add($s)
            # Strip "%scope" suffix on link-local IPv6 -- both the
            # zoned and unzoned form should match.
            $pct = $s.IndexOf('%')
            if ($pct -gt 0) { [void]$set.Add($s.Substring(0, $pct)) }
        }
    } catch {
        # GetHostAddresses can fail in heavily restricted environments
        # (no DNS, no network). Static synonyms still cover the common
        # case; remote-vs-local just won't recognize NIC-specific
        # addresses. Acceptable degradation.
    }

    $script:__ScriptDeckLocalTargets = $set
    return $set
}

function Test-IsLocalTarget {
    [CmdletBinding()]
    param(
        # Default to the runspace-injected $ComputerName variable when
        # called bare from a script. PowerShell looks up unqualified
        # variable names in enclosing scopes, so this picks up the
        # global one ScriptDeck publishes before each invocation.
        [string]$ComputerName = $ComputerName
    )

    if ([string]::IsNullOrWhiteSpace($ComputerName)) { return $true }

    $cache = Get-ScriptDeckLocalTargetCache
    if ($cache.Contains($ComputerName)) { return $true }

    # IPv6 strict equality check above handles the literal-string case.
    # Try a structural compare too for the cases where the user typed
    # "::1" but our cache has it differently formatted, or where the
    # user passed an FQDN that resolves to a local address.
    [System.Net.IPAddress]$parsed = $null
    if ([System.Net.IPAddress]::TryParse($ComputerName, [ref]$parsed)) {
        # If it's a parseable IP, see if it's loopback or matches a
        # local NIC. IPAddress equality handles IPv4-mapped-IPv6 and
        # zone-id normalization.
        if ([System.Net.IPAddress]::IsLoopback($parsed)) { return $true }
        try {
            $local = [System.Net.Dns]::GetHostAddresses(
                [System.Net.Dns]::GetHostName())
            foreach ($a in $local) {
                if ($a.Equals($parsed)) { return $true }
            }
        } catch { }
        return $false
    }

    # Non-IP: try resolving via DNS. If the name's addresses overlap
    # with the local-targets cache, it's local. This catches
    # workgroup names, CNAMEs that point at the box, etc. Bounded by
    # a short timeout via .NET defaults; if DNS is slow it's fine
    # because the static synonyms above already covered hostnames.
    try {
        $resolved = [System.Net.Dns]::GetHostAddresses($ComputerName)
        foreach ($a in $resolved) {
            $s = $a.ToString()
            if ($cache.Contains($s)) { return $true }
            if ([System.Net.IPAddress]::IsLoopback($a)) { return $true }
        }
    } catch {
        # Unresolvable -- treat as remote. Worst case, scripts try
        # Invoke-Command and get a clean "couldn't connect" error.
    }

    return $false
}

# ---------------------------------------------------------------------------
# Write-Rtb / Write-Grid -- explicit per-object output routing.
#
# By default, every object a script emits flows to BOTH the console RTB
# (formatted by the button's rtbFormat) and the results grid (when the
# button's outputs include "grid"). That's the right behavior for
# "show me this data, I'll pick the view per button."
#
# Sometimes you want the split: a compact view in the RTB, the full
# property surface in the grid (or vice versa). Pipe the object through
# the matching helper and ScriptDeck's executor confines it to that one
# destination:
#
#     $summaryString  | Write-Rtb       # console only
#     $detailedObjects | Write-Grid     # grid only
#     $bothPlaces                       # default -- both
#
# Mechanics: each helper wraps the input in a PSObject and stamps an
# `__ScriptDeckTarget` NoteProperty (`rtb` or `grid`). The executor
# reads the tag in its DataAdded handler and skips the unwanted
# destination. The tag never appears in grid columns or RTB output --
# the executor strips `__`-prefixed properties from both views.
#
# Both helpers expand arrays via `@($Item)`, so passing a list (either
# positionally or piped) emits one tagged record per element rather
# than a single record holding the array. Strings, ints, and other
# primitives wrap and tag fine via PSObject::AsPSObject.
# ---------------------------------------------------------------------------

function Write-Rtb {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0, ValueFromPipeline = $true)]
        $InputObject
    )
    process {
        if ($null -eq $InputObject) { return }
        foreach ($item in @($InputObject)) {
            # Add-Member -PassThru is the canonical way to attach a note
            # property to any input -- PSCustomObject, PSObject wrapper
            # of a CLR type, or a bare CLR object. -Force overwrites any
            # existing tag from a previous helper call (so piping through
            # Write-Rtb then Write-Grid lets the later helper win, which
            # is the obvious mental model). The result is the same
            # PSObject with the new note attached, ready to flow.
            Add-Member -InputObject $item -MemberType NoteProperty `
                -Name '__ScriptDeckTarget' -Value 'rtb' -Force -PassThru
        }
    }
}

function Write-Grid {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0, ValueFromPipeline = $true)]
        $InputObject
    )
    process {
        if ($null -eq $InputObject) { return }
        foreach ($item in @($InputObject)) {
            Add-Member -InputObject $item -MemberType NoteProperty `
                -Name '__ScriptDeckTarget' -Value 'grid' -Force -PassThru
        }
    }
}

# ---------------------------------------------------------------------------
# Set-SharedInput / Get-SharedInput / Remove-SharedInput
#
# Manipulate ScriptDeck's session-scoped Volatile shared inputs at
# runtime. Once Set, the value becomes a $variable for every subsequent
# button click in the same workspace -- same convention as Static
# (workspace-JSON) inputs.
#
#   $token = Invoke-RestMethod ... | Select -ExpandProperty access_token
#   Set-SharedInput -Id 'authToken' -Value $token
#   # ...later, in another button's script:
#   Invoke-RestMethod -Headers @{ Authorization = "Bearer $authToken" } ...
#
# Mechanics: each helper emits a PSObject with a sentinel property the
# executor intercepts. The objects NEVER reach the console RTB or
# results grid -- they're routed entirely to Shell's session-input
# dispatch.
#
# Duplicate rule: a Static input (one declared in the workspace JSON)
# cannot be shadowed by a Volatile input. Set-SharedInput throws a
# terminating error in that case; Remove-SharedInput refuses to remove
# a Static one. Use the Inputs grid in the Shell UI for Static-input
# management.
#
# Session inputs are cleared when the workspace closes / switches or
# the app exits. They never write to disk.
# ---------------------------------------------------------------------------

function Set-SharedInput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Id,

        [Parameter(Mandatory = $true, Position = 1)]
        [AllowEmptyString()]
        [AllowNull()]
        $Value,

        [Parameter(Position = 2)]
        [string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Id)) {
        throw "Set-SharedInput: -Id is required."
    }

    # Client-side duplicate check. ScriptDeckInputs is published into
    # every runspace at dispatch by PowerShellExecutor; its .Static
    # member is the list of workspace-declared input ids. If it's not
    # present (test runs / a host that doesn't publish it) we just
    # skip the check -- Shell still has its own server-side guard.
    if ((Test-Path Variable:Script:ScriptDeckInputs -ErrorAction SilentlyContinue) -or `
        (Test-Path Variable:Global:ScriptDeckInputs  -ErrorAction SilentlyContinue) -or `
        (Get-Variable -Name 'ScriptDeckInputs' -Scope Global -ErrorAction SilentlyContinue)) {
        $meta = Get-Variable -Name 'ScriptDeckInputs' -ValueOnly -ErrorAction SilentlyContinue
        if ($meta -and $meta.Static -and ($meta.Static -contains $Id)) {
            throw "Set-SharedInput: '$Id' is already a Static workspace input. Session inputs cannot shadow Static ones."
        }
    }

    # Emit the sentinel-tagged object. The executor's DataAdded handler
    # intercepts on __ScriptDeckSetSharedInput == $true and routes to
    # Shell.OnSessionInputSetRequested. The object never appears in
    # any user-visible output.
    $val = if ($null -eq $Value) { '' } else { [string]$Value }
    [PSCustomObject]@{
        __ScriptDeckSetSharedInput = $true
        Id                         = $Id
        Value                      = $val
        Label                      = $Label
    }
}

function Get-SharedInput {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [string]$Id
    )

    # Inputs surface as runspace variables. With an Id, just return
    # that variable's value (the simple case scripts usually want).
    # Without an Id, return everything ScriptDeck published: walk
    # $ScriptDeckInputs metadata and emit one record per known input
    # tagged with Scope.
    if ($PSBoundParameters.ContainsKey('Id') -and -not [string]::IsNullOrEmpty($Id)) {
        $v = Get-Variable -Name $Id -ValueOnly -ErrorAction SilentlyContinue
        return $v
    }

    $meta = Get-Variable -Name 'ScriptDeckInputs' -ValueOnly -ErrorAction SilentlyContinue
    if (-not $meta) { return @() }

    $results = @()
    foreach ($scope in @('Static', 'Volatile')) {
        foreach ($name in @($meta.$scope)) {
            if ([string]::IsNullOrEmpty($name)) { continue }
            $val = Get-Variable -Name $name -ValueOnly -ErrorAction SilentlyContinue
            $results += [PSCustomObject]@{
                Id    = $name
                Value = $val
                Scope = $scope
            }
        }
    }
    return $results
}

function Remove-SharedInput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Id
    )

    if ([string]::IsNullOrWhiteSpace($Id)) {
        throw "Remove-SharedInput: -Id is required."
    }

    # Refuse to remove a Static input -- scripts shouldn't be able to
    # mutate the workspace's declared inputs.
    $meta = Get-Variable -Name 'ScriptDeckInputs' -ValueOnly -ErrorAction SilentlyContinue
    if ($meta -and $meta.Static -and ($meta.Static -contains $Id)) {
        throw "Remove-SharedInput: '$Id' is a Static workspace input. Only Volatile (session-scoped) inputs can be removed."
    }

    # Silent no-op for unknown ids matches PowerShell's Remove-Item
    # convention with -ErrorAction SilentlyContinue.
    [PSCustomObject]@{
        __ScriptDeckRemoveSharedInput = $true
        Id                            = $Id
    }
}
